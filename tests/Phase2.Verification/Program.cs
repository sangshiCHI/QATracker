using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Companion.Diagnostics;

// Development-only entry point. Production classes are compiled unchanged from a frozen snapshot.
internal static partial class Program
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    static int passed, failed;
    static readonly List<object> checks = [];
    static void Check(bool value, string name)
    {
        checks.Add(new { Name = name, Passed = value });
        if (value) passed++; else failed++;
        Console.WriteLine($"{(value ? "PASS" : "FAIL")} {name}");
    }
    static void Reject(Action action, string name)
    {
        try { action(); Check(false, name); }
        catch (InvalidDataException) { Check(true, name); }
        catch (JsonException) { Check(true, name); }
        catch (KeyNotFoundException) { Check(true, name); }
    }
    static Dictionary<string, long> Counts(long common = 0) => SaveSource.Qualities.ToDictionary(q => q, q => q == "common" ? common : 0L);
    static JsonObject Raw(long at = 1000, long serial = 100, long lost = 5, long chest = 10, long forge = 3, string identity = "synthetic-player") => new()
    {
        ["savedAt"] = at, ["temporada"] = 1,
        ["game"] = new JsonObject
        {
            ["version"] = 22,
            ["game"] = new JsonObject
            {
                ["playerId"] = identity,
                ["sim"] = new JsonObject { ["chestSerial"] = serial, ["bausPerdidos"] = lost, ["inventory"] = new JsonArray(), ["stash"] = new JsonArray() },
                ["itemsFound"] = new JsonObject { ["chest"] = JsonSerializer.SerializeToNode(Counts(chest)), ["forge"] = JsonSerializer.SerializeToNode(Counts(forge)) },
                ["heroes"] = new JsonArray(), ["bench"] = new JsonArray()
            }
        }
    };
    static JsonNode Game(JsonObject raw) => raw["game"]!["game"]!;
    static SaveSnapshot Parse(JsonObject raw) => SaveSource.Parse(Encoding.UTF8.GetBytes(raw.ToJsonString()));
    static JsonObject Item(string id) => new() { ["id"] = id, ["baseType"] = "synthetic-sword", ["rarity"] = "common", ["itemLevel"] = 1 };

    static void StatisticsChecks()
    {
        var baseline = Parse(Raw());
        var stats = new StatisticsEngine();
        Check(stats.Push(baseline) is null && stats.ChestCount == 0 && stats.EquipmentCount == 0, "first read is baseline, not lifetime loot");
        Check(stats.Push(baseline) is null && stats.Status == "waiting_save", "identical fingerprint ignored");
        var next = Parse(Raw(2000, 104, 6, 12, 4));
        var delta = stats.Push(next)!;
        Check(delta.GeneratedChests == 4 && delta.LostChests == 1 && delta.ObtainedChests == 3, "generated minus lost counter arithmetic");
        Check(stats.EquipmentCount == 2 && stats.Forged["common"] == 1, "forge is separate from chest equipment");
        Check(stats.Push(next) is null && stats.ChestCount == 3 && stats.EquipmentCount == 2, "repeated positive sample not counted twice");
        Check(stats.Push(baseline) is null && stats.Status == "old_save" && stats.Previous == next, "old save cannot rewind baseline");
        Check(stats.Push(Parse(Raw(2000, 105, 6, 13))) is null && stats.Status == "conflicting_save" && stats.EquipmentCount == 2, "same timestamp conflicting counters ignored");
        Reject(() => stats.Push(Parse(Raw(3000, identity: "another-player"))), "identity change rejected");
        Check(stats.Previous == next && stats.ChestCount == 3, "identity change cannot mix sessions");
        var season = Raw(3000); season["temporada"] = 2;
        Reject(() => stats.Push(Parse(season)), "season change rejected as different identity");
        var reset = Parse(Raw(3000, 1, 0, 0, 0));
        Check(stats.Push(reset) is null && stats.HasGaps && stats.Status == "counter_reset" && stats.ChestCount == 3, "counter reset marks gap without negative loot");
        stats.Push(Parse(Raw(4000, 3, 0, 1, 0)));
        Check(stats.ChestCount == 5 && stats.EquipmentCount == 3 && stats.HasGaps, "after reset only new baseline deltas accumulate");
        var impossible = new StatisticsEngine(); impossible.Push(Parse(Raw()));
        Check(impossible.Push(Parse(Raw(2000, 101, 7))) is null && impossible.HasGaps && impossible.ChestCount == 0, "interval losses greater than generated mark gap");
        var equipmentReset = new StatisticsEngine(); equipmentReset.Push(baseline);
        Check(equipmentReset.Push(Parse(Raw(2000, 101, 5, 9))) is null && equipmentReset.HasGaps, "equipment counter rollback marks gap");
        var inventory = new StatisticsEngine(); var old = Raw();
        ((JsonArray)Game(old)["sim"]!["inventory"]!).Add(Item("old-id")); inventory.Push(Parse(old));
        Check(inventory.RecentItems.Count == 0, "baseline inventory not shown as new drops");
        var moved = Raw(2000); ((JsonArray)Game(moved)["sim"]!["stash"]!).Add(Item("old-id"));
        Check(inventory.Push(Parse(moved))!.NewlyObservedItems.Length == 0 && inventory.EquipmentCount == 0, "transfer inventory to stash does not create loot");
        var bought = Raw(3000); ((JsonArray)Game(bought)["sim"]!["stash"]!).Add(Item("old-id"));
        foreach (var id in new[] { "new-1", "new-2", "new-3", "new-4" }) ((JsonArray)Game(bought)["sim"]!["inventory"]!).Add(Item(id));
        Check(inventory.Push(Parse(bought))!.NewlyObservedItems.Length == 4 && inventory.EquipmentCount == 0, "new inventory IDs not attributed to chest loot");
        Check(inventory.RecentItems.Count == 3, "recent observation list is capped at three, not ordered drop times");
        var forgeOnly = new StatisticsEngine(); forgeOnly.Push(baseline); forgeOnly.Push(Parse(Raw(2000, forge: 6)));
        Check(forgeOnly.EquipmentCount == 0 && forgeOnly.Forged["common"] == 3, "crafting-only interval cannot inflate chest loot");
        var restored = new StatisticsEngine(JsonSerializer.Deserialize<StatisticsState>(JsonSerializer.Serialize(stats.State(), Json), Json));
        Check(restored.Push(stats.Previous!) is null && restored.ChestCount == stats.ChestCount && restored.HasGaps, "state round trip restores dedup baseline and gaps");
        var detached = stats.State(); detached.Equipment["common"] = 999;
        Check(stats.EquipmentCount == 3, "exported count dictionary cannot mutate running totals");
        foreach (var quality in SaveSource.Qualities)
        {
            var byQuality = new StatisticsEngine(); byQuality.Push(baseline);
            var row = Raw(2000); Game(row)["itemsFound"]!["chest"]![quality] = baseline.ChestEquipment[quality] + 2;
            byQuality.Push(Parse(row));
            Check(byQuality.EquipmentCount == 2 && byQuality.Equipment[quality] == 2, "quality counter isolated: " + quality);
        }
        var identicalNames = new StatisticsEngine(); identicalNames.Push(baseline);
        var first = Raw(2000, chest: 11); ((JsonArray)Game(first)["sim"]!["inventory"]!).Add(Item("first-same-name"));
        identicalNames.Push(Parse(first));
        var second = Raw(3000, chest: 12);
        ((JsonArray)Game(second)["sim"]!["inventory"]!).Add(Item("first-same-name"));
        ((JsonArray)Game(second)["sim"]!["inventory"]!).Add(Item("second-same-name"));
        Check(identicalNames.Push(Parse(second))!.NewlyObservedItems.Length == 1 && identicalNames.EquipmentCount == 2, "consecutive identical names with distinct IDs are not swallowed");
    }

    static void ParserChecks()
    {
        var raw = Raw(); var snap = Parse(raw);
        Check(snap.Version == 22 && snap.ChestSerial == 100 && snap.ChestEquipment.Count == 7, "whitelisted v22 counters parsed");
        var nested = Raw(); nested["game"] = nested["game"]!.ToJsonString();
        Check(Parse(nested).Fingerprint == snap.Fingerprint, "string and object game envelopes match");
        var bom = new byte[] { 239, 187, 191 }.Concat(Encoding.UTF8.GetBytes(raw.ToJsonString())).ToArray();
        Check(SaveSource.Parse(bom).Fingerprint == snap.Fingerprint, "UTF8 BOM accepted");
        raw["settings"] = new JsonObject { ["token"] = "SYNTHETIC_SECRET_MUST_NOT_EXPORT" };
        Check(Parse(raw).Fingerprint == snap.Fingerprint && !JsonSerializer.Serialize(Parse(raw)).Contains("SYNTHETIC_SECRET"), "unrelated settings excluded from snapshot and fingerprint");
        var invalid = Raw(); invalid["game"]!["version"] = 23; Reject(() => Parse(invalid), "unsupported schema rejected");
        invalid = Raw(); Game(invalid)["sim"]!["chestSerial"] = -1; Reject(() => Parse(invalid), "negative counters rejected");
        invalid = Raw(); Game(invalid)["sim"]!["chestSerial"] = 9007199254740992L; Reject(() => Parse(invalid), "unsafe integer rejected");
        invalid = Raw(); Game(invalid)["sim"]!["bausPerdidos"] = 101; Reject(() => Parse(invalid), "lost greater than serial rejected");
        invalid = Raw(); Game(invalid)["sim"]!.AsObject().Remove("chestSerial"); Reject(() => Parse(invalid), "missing counter rejected, not inventory fallback");
        invalid = Raw(); Game(invalid)["itemsFound"]!["chest"]!["unknown-quality"] = 1; Reject(() => Parse(invalid), "unknown counter quality rejected");
        invalid = Raw(); var item = Item("bad"); item["rarity"] = "invented";
        ((JsonArray)Game(invalid)["sim"]!["inventory"]!).Add(item); Reject(() => Parse(invalid), "unknown equipment quality rejected");
        invalid = Raw(); Game(invalid)["playerId"] = ""; Reject(() => Parse(invalid), "missing player identity rejected");
        Reject(() => SaveSource.Parse(Encoding.UTF8.GetBytes("{\"game\":")), "partial JSON rejected rather than counted as zero");
    }

    static void StorageChecks(string output)
    {
        var path = Path.Combine(output, "synthetic-sessions.db");
        var store = new SessionStore(path); var session = store.Begin("synthetic:100:started-A");
        var stats = new StatisticsEngine(); stats.Push(Parse(Raw()));
        store.Save(session.SessionId, stats, null);
        var arrival = Raw(2000, 102, 5, 13, 4); ((JsonArray)Game(arrival)["sim"]!["inventory"]!).Add(Item("new-item"));
        var delta = stats.Push(Parse(arrival)); store.Save(session.SessionId, stats, delta);
        var once = Scalar(path, "SELECT COUNT(*) FROM Events");
        store.Save(session.SessionId, stats, delta);
        using (var db = new SqliteConnection($"Data Source={path}"))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM Events"; Check(once > 0 && (long)command.ExecuteScalar()! == once, "SQLite event replay is idempotent including lifecycle and aggregate events");
            command.CommandText = "SELECT COUNT(*) FROM EquipmentDrops WHERE Source='InventoryAppearance_Unattributed' AND Confidence IS NULL";
            Check((long)command.ExecuteScalar()! == 1, "observed equipment stored unattributed with null confidence");
            command.CommandText = "PRAGMA integrity_check"; Check((string)command.ExecuteScalar()! == "ok", "SQLite integrity check");
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; command.ExecuteNonQuery();
        }
        // Recovery runs against an additional copy, leaving the first synthetic DB available for inspection.
        var copy = Path.Combine(output, "synthetic-recovery-copy.db"); File.Copy(path, copy);
        var recovered = new SessionStore(copy); var resumed = recovered.Begin(session.ProcessKey);
        Check(resumed.SessionId == session.SessionId && resumed.EquipmentCount == 3, "additional DB copy resumes same process instance");
        var state = new StatisticsEngine(JsonSerializer.Deserialize<StatisticsState>(resumed.StateJson, Json));
        Check(state.Push(Parse(arrival)) is null && state.EquipmentCount == 3, "recovery does not replay persisted positive delta");
        var newProcess = recovered.Begin("synthetic:100:started-B");
        Check(newProcess.SessionId != resumed.SessionId && recovered.History().Single(x => x.SessionId == resumed.SessionId).EndReason == "interrupted", "same PID with new start gets distinct session");
        Check(store.History().Single().EndTime is null, "recovery test leaves original DB unchanged");
        store.End(session.SessionId, "synthetic_exit"); var end = store.History().Single().EndTime; store.End(session.SessionId, "second_end");
        Check(store.History().Single().EndTime == end && store.History().Single().EndReason == "synthetic_exit", "session end is idempotent");
        var export = Path.Combine(output, "synthetic-export.json"); store.Export(export); var text = File.ReadAllText(export);
        Check(!new[] { "playerId", "Identity", "StateJson", "ProcessKey", "Items", "synthetic-player", "new-item" }.Any(text.Contains), "export excludes identity, inventory and internal state");
        using (var doc = JsonDocument.Parse(text)) Check(doc.RootElement.GetProperty("Version").GetString() == "0.1" && doc.RootElement.GetProperty("Sessions")[0].GetProperty("EquipmentCount").GetInt64() == 3, "export version and totals match");
        recovered.ClearEnded(); Check(recovered.History().Length == 1 && recovered.History()[0].SessionId == newProcess.SessionId, "clear history preserves active session");
        SqliteConnection.ClearAllPools();
    }

    static object Replay(string path)
    {
        var stats = new StatisticsEngine(); int samples = 0, positive = 0; long? previousTime = null;
        SaveSnapshot? first = null, last = null;
        var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line); var row = doc.RootElement;
            var at = row.GetProperty("SavedAt").GetInt64();
            if (previousTime is not null && at < previousTime) throw new InvalidDataException("replay_not_chronological");
            previousTime = at;
            // Replay only existing whitelist counters; never copy item IDs, identity or raw save contents.
            var raw = Raw(at, row.GetProperty("ChestSerial").GetInt64(), row.GetProperty("LostChests").GetInt64());
            if (row.GetProperty("Version").GetInt32() != 22) throw new InvalidDataException("unsupported_replay_version");
            Game(raw)["itemsFound"]!["chest"] = JsonNode.Parse(row.GetProperty("ChestEquipment").GetRawText());
            Game(raw)["itemsFound"]!["forge"] = JsonNode.Parse(row.GetProperty("ForgedEquipment").GetRawText());
            var snapshot = Parse(raw); first ??= snapshot; last = snapshot;
            var delta = stats.Push(snapshot); samples++;
            if (delta is not null && (delta.ObtainedChests > 0 || delta.Equipment.Values.Sum() > 0)) positive++;
        }
        Check(samples >= 2, "existing real whitelist sample replay contains multiple observations");
        if (first is not null && last is not null && !stats.HasGaps)
        {
            Check(stats.ChestCount == (last.ChestSerial - first.ChestSerial) - (last.LostChests - first.LostChests), "replay chest accumulation matches independent endpoint difference");
            Check(stats.EquipmentCount == SaveSource.Qualities.Sum(q => last.ChestEquipment[q] - first.ChestEquipment[q]), "replay equipment accumulation matches independent endpoint difference");
        }
        Check(sourceHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), "real whitelist input unchanged by replay");
        return new { Source = "ExistingRealWhitelistCounterReplay", SourceSha256 = sourceHash, Synthetic = false, ParserEnvelope = "Reconstructed_NotOriginalSave", Samples = samples, PositiveIntervals = positive, stats.ChestCount, stats.EquipmentCount, stats.HasGaps, HumanGroundTruth = false, Accuracy = "unknown", ItemAttribution = "not tested" };
    }

    public static int Main(string[] args)
    {
        if (args.Contains("--original-checks")) return SelfTests.Run();
        if (args.Length == 2 && args[0] == "--probe-real-save") return ProbeRealSave(args[1]).GetAwaiter().GetResult();
        if (args.Length >= 4 && args[0] == "--audit-yield") return RunYieldAudit(args);
        var output = args[0]; Directory.CreateDirectory(output); object? replay = null; string? error = null;
        try
        {
            Suite("parser", ParserChecks); Suite("statistics", StatisticsChecks); Suite("storage", () => StorageChecks(output));
            Suite("input boundaries", InputBoundaryChecks); Suite("counter sequence", SequenceChecks);
            Suite("file reads", () => FileReadChecks(output)); Suite("legacy schema", () => MigrationChecks(output));
            Suite("transaction rollback", () => TransactionChecks(output));
            Suite("yield audit", () => YieldAuditChecks(output));
            if (args.Length > 1) replay = Replay(args[1]);
        }
        catch (Exception ex) { failed++; error = ex.GetType().Name + ": " + ex.Message; Console.WriteLine("ERROR " + error); }
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = passed, Failed = failed, Error = error, Checks = checks, Replay = replay, SyntheticTests = true, LiveGameAcceptance = "not performed", UiAcceptance = "not performed" }, Json));
        Console.WriteLine($"{passed} passed, {failed} failed. Synthetic regression != live loot accuracy.");
        return failed == 0 ? 0 : 1;
    }
}
