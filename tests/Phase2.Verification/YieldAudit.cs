using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Companion.Diagnostics;

internal static partial class Program
{
    static async Task<int> ProbeRealSave(string output)
    {
        Directory.CreateDirectory(output);
        try
        {
            var first = await SaveSource.Read(SaveSource.DefaultPath, CancellationToken.None);
            await Task.Delay(1000);
            var last = await SaveSource.Read(SaveSource.DefaultPath, CancellationToken.None);
            var engine = new StatisticsEngine(); engine.Push(first); engine.Push(last);
            File.WriteAllText(Path.Combine(output, "real-save-probe.json"), JsonSerializer.Serialize(new
            {
                Source = "RealLocalSave", Synthetic = false, Parser = "Production_SaveSource.Read_OriginalSave",
                ObservedUtc = DateTimeOffset.UtcNow, FirstSavedAt = first.SavedAt, LastSavedAt = last.SavedAt,
                last.Version, IdentityContinuousBetweenTwoReads = first.Identity == last.Identity,
                LifetimeGenerated = last.ChestSerial, LifetimeLost = last.LostChests,
                LifetimeChestEquipment = last.ChestEquipment, LifetimeForgeEquipment = last.ForgeEquipment,
                ObservedChestDelta = engine.ChestCount, ObservedEquipmentDelta = engine.EquipmentCount,
                AccuracyPercent = (double?)null, Notice = "Two read-only production-parser reads; not positive-yield or event-accuracy acceptance. Identity and inventory are not exported."
            }, Json));
            Console.WriteLine("Original real save parsed twice with production reader; identity/inventory excluded, accuracy unknown.");
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(output, "real-save-probe-error.json"), JsonSerializer.Serialize(new { Status = "failed", ErrorType = e.GetType().Name }, Json));
            Console.WriteLine("Original real save probe failed: " + e.GetType().Name); return 1;
        }
    }

    // Audits existing whitelist recordings. Never treats inventory arrivals as loot or self-comparison as accuracy.
    static JsonObject AuditYieldSamples(string samplesPath, string qualityPath, string? activityPath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(samplesPath)));
        var stats = new StatisticsEngine(); SaveSnapshot? first = null, last = null;
        var balances = new JsonArray(); int samples = 0, chestPositive = 0, equipmentPositive = 0;
        bool synthetic = false, incompleteBalance = false, mismatch = false;
        foreach (var line in File.ReadLines(samplesPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line); var row = doc.RootElement;
            synthetic |= row.TryGetProperty("Synthetic", out var marker) && marker.ValueKind == JsonValueKind.True;
            var at = row.GetProperty("SavedAt").GetInt64();
            if (last is not null && at <= last.SavedAt) throw new InvalidDataException("audit_requires_unique_increasing_save_times");
            var raw = Raw(at, row.GetProperty("ChestSerial").GetInt64(), row.GetProperty("LostChests").GetInt64());
            raw["game"]!["version"] = row.GetProperty("Version").GetInt64();
            Game(raw)["itemsFound"]!["chest"] = JsonNode.Parse(row.GetProperty("ChestEquipment").GetRawText());
            Game(raw)["itemsFound"]!["forge"] = JsonNode.Parse(row.GetProperty("ForgedEquipment").GetRawText());
            var snapshot = Parse(raw); first ??= snapshot; last = snapshot;
            var delta = stats.Push(snapshot); samples++;
            if (delta?.ObtainedChests > 0) chestPositive++;
            if (delta?.Equipment.Values.Sum() > 0) equipmentPositive++;
            long? openedTotal = null, queued = null;
            if (row.TryGetProperty("Opened", out var opened) && opened.ValueKind == JsonValueKind.Object &&
                new[] { "normal", "stage", "act" }.All(k => opened.TryGetProperty(k, out var value) && value.TryGetInt64(out var n) && n >= 0))
                openedTotal = checked(opened.GetProperty("normal").GetInt64() + opened.GetProperty("stage").GetInt64() + opened.GetProperty("act").GetInt64());
            if (row.TryGetProperty("QueuedChests", out var queue) && queue.TryGetInt64(out var q) && q >= 0) queued = q;
            long? error = openedTotal is not null && queued is not null ? snapshot.ChestSerial - snapshot.LostChests - openedTotal - queued : null;
            incompleteBalance |= error is null; mismatch |= error is not null && error != 0;
            balances.Add(new JsonObject { ["SavedAt"] = at, ["GeneratedLifetime"] = snapshot.ChestSerial, ["LostLifetime"] = snapshot.LostChests,
                ["OpenedLifetime"] = openedTotal, ["Queued"] = queued, ["BalanceError"] = error });
        }
        if (first is null || last is null || samples < 2) throw new InvalidDataException("audit_needs_multiple_samples");
        if (hash != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(samplesPath)))) throw new InvalidDataException("audit_input_changed");
        var endpointChests = (last.ChestSerial - first.ChestSerial) - (last.LostChests - first.LostChests);
        var endpointEquipment = SaveSource.Qualities.Sum(q => last.ChestEquipment[q] - first.ChestEquipment[q]);
        var consistent = !stats.HasGaps && stats.ChestCount == endpointChests && stats.EquipmentCount == endpointEquipment;
        bool recordedReal = false;
        var provenancePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(samplesPath))!, "sampler-command.json");
        if (File.Exists(provenancePath))
        {
            using var provenance = JsonDocument.Parse(File.ReadAllText(provenancePath)); var p = provenance.RootElement;
            recordedReal = p.TryGetProperty("Source", out var source) && source.GetString() == "DefaultRealLocalSave" &&
                p.TryGetProperty("Synthetic", out var flag) && flag.ValueKind == JsonValueKind.False &&
                p.TryGetProperty("Arguments", out var arguments) && arguments.EnumerateArray().Any(a => a.GetString() == Path.GetFullPath(samplesPath));
        }
        var positive = chestPositive > 0 || equipmentPositive > 0;
        var gate = synthetic ? "SyntheticOnly" : !recordedReal ? "UnverifiedRecordingOrigin" : !consistent || mismatch ? "CounterMismatchOrGap" :
            incompleteBalance ? "MissingReconciliationFields" : !positive ? "NeedsPositiveInterval" : "PositiveCountersObserved_AccuracyNotEstablished";
        var report = new JsonObject
        {
            ["Source"] = "WhitelistCounterAudit", ["InputSha256"] = hash, ["Samples"] = samples,
            ["InputMarkedSynthetic"] = synthetic, ["RealSamplerProvenancePresent"] = recordedReal,
            ["ParserEnvelope"] = "Reconstructed_WhitelistReplay_NotOriginalSave", ["FirstSavedAt"] = first.SavedAt, ["LastSavedAt"] = last.SavedAt,
            ["IdentityContinuity"] = "not_included_in_recording", ["ManualCountsRequired"] = false,
            ["EquipmentCounterScope"] = "itemsFound.chest; inclusion of colossus rewards not verified",
            ["GeneratedChestDelta"] = stats.GeneratedChestCount, ["LostChestDelta"] = stats.LostChestCount,
            ["ObtainedChestDelta"] = stats.ChestCount, ["ChestEquipmentDelta"] = stats.EquipmentCount,
            ["QualityDeltas"] = JsonSerializer.SerializeToNode(stats.Equipment), ["ForgeDeltas"] = JsonSerializer.SerializeToNode(stats.Forged),
            ["PositiveChestIntervals"] = chestPositive, ["PositiveEquipmentIntervals"] = equipmentPositive,
            ["CounterSequenceConsistent"] = consistent, ["HasGaps"] = stats.HasGaps,
            ["NativeBalanceEquation"] = "chestSerial - bausPerdidos - sum(bausAbertos) - queuedChests",
            ["NativeBalanceStatus"] = mismatch ? "mismatch" : incompleteBalance ? "unknown" : "balanced_in_observed_samples",
            ["PerSampleBalance"] = balances, ["InventoryUsedForLootTotals"] = false,
            ["PositiveObservation"] = positive, ["AcceptanceGate"] = gate,
            ["AccuracyPercent"] = null, ["Precision"] = null, ["Recall"] = null, ["HumanGroundTruth"] = false,
            ["Notice"] = "Native counter reconciliation is not event accuracy. No inventory net-change, no manual counts required, no historical backfill."
        };
        if (!string.IsNullOrWhiteSpace(activityPath))
        {
            using var activity = JsonDocument.Parse(File.ReadAllText(activityPath));
            using var qualities = JsonDocument.Parse(File.ReadAllText(qualityPath));
            var labels = qualities.RootElement.GetProperty("qualities").EnumerateArray().ToDictionary(q => q.GetProperty("zhHans").GetString()!, q => q.GetProperty("id").GetString()!);
            int obtained = 0, opened = 0, ordinaryEquipment = 0, colossusEquipment = 0, otherOrUnknown = 0;
            var byQuality = Counts(); var bossByQuality = Counts();
            foreach (var e in activity.RootElement.GetProperty("HistoricalEvents").EnumerateArray())
            {
                var text = e.GetProperty("text").GetString() ?? "";
                if (text == "掉落了一个宝箱") { obtained++; continue; }
                bool chest = text.StartsWith("开启了宝箱：", StringComparison.Ordinal), boss = text.StartsWith("巨像宝箱：", StringComparison.Ordinal);
                if (!chest && !boss) continue; // Global notices, even with quality words, are never local loot.
                if (chest) opened++;
                var payload = text[(text.IndexOf('：') + 1)..];
                var quality = labels.FirstOrDefault(q => payload.StartsWith(q.Key + " ", StringComparison.Ordinal)).Value;
                if (quality is null) { otherOrUnknown++; continue; }
                if (boss) { colossusEquipment++; bossByQuality[quality]++; }
                else { ordinaryEquipment++; byQuality[quality]++; }
            }
            report["ActivityReference"] = new JsonObject
            {
                ["Source"] = "GameActivityAccessibilityText", ["Scope"] = "Historical_NotAlignedToCounterWindow",
                ["InputSha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(activityPath))),
                ["ObservedChestNotices"] = obtained, ["ObservedChestOpenNotices"] = opened,
                ["ObservedOrdinaryEquipmentNotices"] = ordinaryEquipment, ["ObservedColossusEquipmentNotices"] = colossusEquipment,
                ["OtherOrUnknownRewardNotices"] = otherOrUnknown, ["OrdinaryEquipmentQualities"] = JsonSerializer.SerializeToNode(byQuality),
                ["ColossusEquipmentQualities"] = JsonSerializer.SerializeToNode(bossByQuality), ["CountsAreLowerBounds"] = true,
                ["UsedAsAccuracyGroundTruth"] = false, ["TimePrecision"] = "minute", ["Completeness"] = "unknown"
            };
        }
        return report;
    }

    static int RunYieldAudit(string[] args)
    {
        var output = args[2]; Directory.CreateDirectory(output);
        try
        {
            var report = AuditYieldSamples(args[1], args[3], args.Length > 4 ? args[4] : null);
            File.WriteAllText(Path.Combine(output, "yield-audit.json"), report.ToJsonString(Json));
            Console.WriteLine($"Yield audit: samples={report["Samples"]}, chests={report["ObtainedChestDelta"]}, equipment={report["ChestEquipmentDelta"]}, gate={report["AcceptanceGate"]}; accuracy unknown.");
            return 0; // Evaluation completed, not an acceptance/accuracy pass.
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(output, "yield-audit-error.json"), JsonSerializer.Serialize(new { Status = "failed", ErrorType = e.GetType().Name }, Json));
            Console.WriteLine("Yield audit failed: " + e.GetType().Name); return 1;
        }
    }

    static void YieldAuditChecks(string output)
    {
        var folder = Path.Combine(output, "synthetic-yield-audit"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "samples.jsonl"); var qualityPath = Path.Combine(folder, "qualities.json");
        File.WriteAllText(qualityPath, """{"qualities":[{"id":"common","zhHans":"普通"},{"id":"rare","zhHans":"稀有"}]}""");
        object Row(long at, long serial, long found, long opened, bool includeOpened = true) => new
        {
            Synthetic = true, SavedAt = at, Version = 22, ChestSerial = serial, LostChests = 5,
            ChestEquipment = Counts(found), ForgedEquipment = Counts(3), QueuedChests = 0,
            Opened = includeOpened ? new { normal = opened, stage = 10, act = 5 } : null,
            Items = new[] { new { id = "SYNTHETIC_PRIVATE_ITEM", baseType = "SYNTHETIC_PRIVATE_NAME" } }
        };
        void Rows(params object[] rows) => File.WriteAllLines(path, rows.Select(row => JsonSerializer.Serialize(row)));
        Rows(Row(1000, 100, 10, 80), Row(2000, 102, 12, 82));
        var report = AuditYieldSamples(path, qualityPath, null);
        Check(report["PositiveObservation"]!.GetValue<bool>() && report["ObtainedChestDelta"]!.GetValue<long>() == 2 && report["ChestEquipmentDelta"]!.GetValue<long>() == 2, "yield audit detects positive counter intervals without inventory deltas");
        Check(report["AcceptanceGate"]!.GetValue<string>() == "SyntheticOnly" && report["AccuracyPercent"] is null, "synthetic positives cannot become real accuracy acceptance");
        Check(report["NativeBalanceStatus"]!.GetValue<string>() == "balanced_in_observed_samples", "native generated-minus-loss reconciliation includes opened and queue counters");
        Check(!report.ToJsonString().Contains("SYNTHETIC_PRIVATE"), "yield audit never exports inventory identities or names");
        Rows(Row(1000, 100, 10, 80), Row(2000, 103, 12, 82));
        Check(AuditYieldSamples(path, qualityPath, null)["NativeBalanceStatus"]!.GetValue<string>() == "mismatch", "native balance mismatch cannot masquerade as a passing audit");
        Rows(Row(1000, 100, 10, 80, false), Row(2000, 102, 12, 82, false));
        Check(AuditYieldSamples(path, qualityPath, null)["NativeBalanceStatus"]!.GetValue<string>() == "unknown", "missing reconciliation fields remain unknown rather than zero");
        Rows(Row(2000, 102, 12, 82), Row(1000, 100, 10, 80));
        Reject(() => AuditYieldSamples(path, qualityPath, null), "yield audit rejects unordered sample windows");
        Rows(Row(1000, 100, 10, 80), Row(2000, 102, 12, 82));
        var activity = Path.Combine(folder, "activity.json");
        File.WriteAllText(activity, """{"HistoricalEvents":[{"text":"掉落了一个宝箱"},{"text":"开启了宝箱：普通 测试剑"},{"text":"开启了宝箱：测试矿石"},{"text":"巨像宝箱：稀有 测试环"},{"text":"其他玩家发现了传说 装备"}]}""");
        report = AuditYieldSamples(path, qualityPath, activity);
        Check(report["ActivityReference"]!["ObservedOrdinaryEquipmentNotices"]!.GetValue<int>() == 1 && report["ActivityReference"]!["ObservedColossusEquipmentNotices"]!.GetValue<int>() == 1, "activity audit separates ordinary and colossus equipment, excludes world notices");
        Check(!report["ActivityReference"]!["UsedAsAccuracyGroundTruth"]!.GetValue<bool>() && report["Precision"] is null && report["Recall"] is null, "historical activity text cannot manufacture time-aligned precision or recall");
    }
}
