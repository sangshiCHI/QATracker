using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Companion.Diagnostics;

internal static partial class Program
{
    static void Suite(string name, Action run)
    {
        try { run(); }
        catch (Exception ex) { Check(false, $"{name}: {ex.GetType().Name}: {ex.Message}"); }
        finally { SqliteConnection.ClearAllPools(); }
    }
    static long Scalar(string path, string sql)
    {
        using var db = new SqliteConnection($"Data Source={path}"); db.Open();
        using var command = db.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
    static void Sql(string path, string sql)
    {
        using var db = new SqliteConnection($"Data Source={path}"); db.Open();
        using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    static void InputBoundaryChecks()
    {
        var raw = Raw(); raw["game"]!["version"] = 4294967318L; // 2^32 + 22 must not alias supported v22.
        Reject(() => Parse(raw), "version cannot wrap from Int64 to supported Int32");
        raw = Raw(); var item = Item("overflow-level"); item["itemLevel"] = (long)int.MaxValue + 1;
        ((JsonArray)Game(raw)["sim"]!["inventory"]!).Add(item);
        Reject(() => Parse(raw), "item level cannot wrap to negative Int32");
        raw = Raw(); ((JsonArray)Game(raw)["sim"]!["inventory"]!).Add(Item(""));
        Reject(() => Parse(raw), "empty equipment ID cannot enter dedup identity");
        raw = Raw(); item = Item("blank-type"); item["baseType"] = " ";
        ((JsonArray)Game(raw)["sim"]!["inventory"]!).Add(item);
        Reject(() => Parse(raw), "blank equipment type rejected");
    }
    static void SequenceChecks()
    {
        var random = new Random(73221); var stats = new StatisticsEngine(); stats.Push(Parse(Raw()));
        long serial = 100, lost = 5, expectedChest = 0, at = 1000;
        var chest = Counts(10); var forge = Counts(3); var duplicateSafe = true;
        for (int i = 0; i < 128; i++)
        {
            var generated = random.Next(0, 6); var dropped = random.Next(0, generated + 1);
            serial += generated; lost += dropped; expectedChest += generated - dropped; at += 1000;
            foreach (var q in SaveSource.Qualities) { chest[q] += random.Next(0, 3); forge[q] += random.Next(0, 2); }
            var raw = Raw(at, serial, lost);
            Game(raw)["itemsFound"]!["chest"] = System.Text.Json.JsonSerializer.SerializeToNode(chest);
            Game(raw)["itemsFound"]!["forge"] = System.Text.Json.JsonSerializer.SerializeToNode(forge);
            var snapshot = Parse(raw); stats.Push(snapshot);
            var before = stats.EquipmentCount; duplicateSafe &= stats.Push(snapshot) is null && before == stats.EquipmentCount;
            if (i == 63) stats = new StatisticsEngine(stats.State());
        }
        Check(duplicateSafe, "128 seeded intervals: duplicate reads never inflate counters");
        Check(stats.ChestCount == expectedChest && stats.GeneratedChestCount == serial - 100 && stats.LostChestCount == lost - 5, "128 seeded intervals: chest totals match independent model after restore");
        Check(SaveSource.Qualities.All(q => stats.Equipment[q] == chest[q] - (q == "common" ? 10 : 0)), "128 seeded intervals: seven quality totals match independent model");
        Check(SaveSource.Qualities.All(q => stats.Forged[q] == forge[q] - (q == "common" ? 3 : 0)) && !stats.HasGaps, "128 seeded intervals: forge remains separate, no fabricated gap");
    }
    static void FileReadChecks(string output)
    {
        var path = Path.Combine(output, "synthetic-save.json");
        File.WriteAllText(path, Raw().ToJsonString(), new UTF8Encoding(false));
        var original = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        Check(SaveSource.Read(path, CancellationToken.None).GetAwaiter().GetResult().Fingerprint == Parse(Raw()).Fingerprint, "async read uses real production parser");
        Check(original == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), "async read leaves source file byte-identical");
        using (var token = new CancellationTokenSource())
        {
            token.Cancel();
            var cancelled = false;
            try { SaveSource.Read(path, token.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "cancelled read does not deliver a sample");
        }
        File.WriteAllText(path, ""); Reject(() => SaveSource.Read(path, CancellationToken.None).GetAwaiter().GetResult(), "empty file rejected rather than zero收益");
        using (var stream = File.Create(path)) stream.SetLength(4 * 1024 * 1024 + 1);
        Reject(() => SaveSource.Read(path, CancellationToken.None).GetAwaiter().GetResult(), "oversized save rejected before allocating input buffer");
        File.WriteAllText(path, "{\"game\":");
        Reject(() => SaveSource.Read(path, CancellationToken.None).GetAwaiter().GetResult(), "partial on-disk JSON rejected rather than zero收益");
    }
    static void MigrationChecks(string output)
    {
        var original = Path.Combine(output, "synthetic-legacy-original.db");
        // Exact pre-Duration Sessions shape from the previous phase2 source baseline.
        Sql(original, """
            CREATE TABLE Sessions(SessionId TEXT PRIMARY KEY,ProcessKey TEXT NOT NULL,StartTime TEXT NOT NULL,
              EndTime TEXT,ChestCount INTEGER NOT NULL DEFAULT 0,EquipmentCount INTEGER NOT NULL DEFAULT 0,
              StateJson TEXT NOT NULL,EndReason TEXT NOT NULL DEFAULT 'running');
            PRAGMA user_version=1;
            """);
        using (var db = new SqliteConnection($"Data Source={original}"))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO Sessions(SessionId,ProcessKey,StartTime,EndTime,ChestCount,EquipmentCount,StateJson,EndReason) VALUES('synthetic-old-ended','synthetic:old','2026-11-08T00:00:00Z','2026-11-08T00:01:00Z',7,9,$state,'legacy_complete')";
            command.Parameters.AddWithValue("$state", System.Text.Json.JsonSerializer.Serialize(new StatisticsEngine().State(), Json));
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools(); var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original)));
        var copy = Path.Combine(output, "synthetic-legacy-migration-copy.db"); File.Copy(original, copy);
        var store = new SessionStore(copy); var session = store.Begin("synthetic:legacy-process");
        var stats = new StatisticsEngine(); stats.Push(Parse(Raw())); var delta = stats.Push(Parse(Raw(2000, 102, 5, 12)));
        store.Save(session.SessionId, stats, delta);
        var ended = true;
        try { store.End(session.SessionId, "synthetic_legacy_exit"); } catch (SqliteException) { ended = false; }
        Check(ended && store.History().Single(s => s.SessionId == session.SessionId).EndTime is not null && store.History().Single(s => s.SessionId == session.SessionId).EquipmentCount == 2, "pre-Duration database upgrades without losing ability to end session");
        var old = store.History().Single(s => s.SessionId == "synthetic-old-ended");
        Check(old.ChestCount == 7 && old.EquipmentCount == 9 && old.EndReason == "legacy_complete", "legacy upgrade preserves preexisting completed session counters");
        Check(hash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original))), "legacy upgrade experiment leaves original DB unchanged");
        if (!ended) return;
        store = new SessionStore(copy);
        Check(store.History().Length == 2, "reopening legacy-compatible storage retains both historical sessions");
        // Duration may be derived on export or migrated; test the public behavior, not a required column.
        var export = Path.Combine(output, "synthetic-legacy-export.json"); store.Export(export);
        using var exported = JsonDocument.Parse(File.ReadAllText(export));
        var duration = exported.RootElement.GetProperty("Sessions").EnumerateArray().Single(s => s.GetProperty("SessionId").GetString() == "synthetic-old-ended").GetProperty("DurationSeconds").GetDouble();
        Check(Math.Abs(duration - 60) < 0.1, "legacy history exports duration accurately from original timestamps");
    }
    static void TransactionChecks(string output)
    {
        var path = Path.Combine(output, "synthetic-fault-injection.db");
        var store = new SessionStore(path); var session = store.Begin("synthetic:fault-process");
        var stats = new StatisticsEngine(); stats.Push(Parse(Raw())); store.Save(session.SessionId, stats, null);
        var events = Scalar(path, "SELECT COUNT(*) FROM Events");
        var row = Raw(2000, 103, 5, 12); ((JsonArray)Game(row)["sim"]!["inventory"]!).Add(Item("fault-item"));
        var delta = stats.Push(Parse(row));
        Sql(path, "CREATE TRIGGER fail_save BEFORE UPDATE ON Sessions BEGIN SELECT RAISE(ABORT,'synthetic_save_fault'); END;");
        var threw = false; try { store.Save(session.SessionId, stats, delta); } catch (SqliteException) { threw = true; }
        Check(threw, "injected summary write failure is reported");
        Check(Scalar(path, "SELECT COUNT(*) FROM Events") == events && Scalar(path, "SELECT COUNT(*) FROM EquipmentDrops") == 0 && store.History().Single().EquipmentCount == 0, "failed save rolls back events, observations and summary together");
        Sql(path, "DROP TRIGGER fail_save;"); store.Save(session.SessionId, stats, delta);
        Check(store.History().Single().EquipmentCount == 2 && Scalar(path, "SELECT COUNT(*) FROM EquipmentDrops") == 1, "retry of failed save commits pending delta exactly once");
        // Synthetic error at the end of multi-statement cleanup must not erase child rows only.
        store.End(session.SessionId, "synthetic_end");
        events = Scalar(path, "SELECT COUNT(*) FROM Events");
        Sql(path, "CREATE TRIGGER fail_clear BEFORE DELETE ON Sessions BEGIN SELECT RAISE(ABORT,'synthetic_clear_fault'); END;");
        threw = false; try { store.ClearEnded(); } catch (SqliteException) { threw = true; }
        Check(threw, "injected history deletion failure is reported");
        Check(Scalar(path, "SELECT COUNT(*) FROM Events") == events && Scalar(path, "SELECT COUNT(*) FROM EquipmentDrops") == 1 && store.History().Length == 1, "failed history cleanup rolls back child deletions as one transaction");
        Sql(path, "DROP TRIGGER fail_clear;"); store.ClearEnded();
        Check(store.History().Length == 0 && Scalar(path, "SELECT COUNT(*) FROM Events") == 0 && Scalar(path, "SELECT COUNT(*) FROM EquipmentDrops") == 0, "cleanup retry removes all ended session data consistently");
    }
}
