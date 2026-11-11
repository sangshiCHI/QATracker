using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Companion.Diagnostics;

internal static class ProductionCountVerification
{
    public static int Run(string output,string? replayPath)
    {
        Directory.CreateDirectory(output);var checks=new List<object>();int failed=0;
        void Check(string name,Func<bool> test){bool pass;string? error=null;try{pass=test();}catch(Exception e){pass=false;error=e.GetType().Name+": "+e.Message;}checks.Add(new{Name=name,Passed=pass,Error=error});if(!pass)failed++;}
        Dictionary<string,long> Counts(long common=0)=>SaveSource.Qualities.ToDictionary(q=>q,q=>q=="common"?common:0L);
        SaveSnapshot Sample(long at,long chest=10,long forge=3,long opened=100)=>new(at,22,"synthetic-production",1000,0,Counts(chest),Counts(forge),[],"synthetic-production-"+at){OpenedChestCounters=new(){["normal"]=opened,["stage"]=20,["act"]=3}};
        StatisticsEngine Native()=>new(null,false,false,true);
        var first=Sample(1000);var next=Sample(2000,13,5,102);var stats=Native();
        Check("native scope baseline is known zero, not lifetime income",()=>{stats.Push(first);return stats.NativeProduction&&!stats.ChestOnly&&stats.EquipmentCount==0&&stats.EquipmentAvailable&&!stats.HasGaps&&stats.OpenedChestCount==0;});
        var delta=stats.Push(next);
        Check("chest plus forge quality deltas count once",()=>stats.EquipmentCount==5&&stats.Equipment["common"]==5&&stats.Forged["common"]==2&&stats.State().MixedEquipment!["common"]==3);
        Check("openings remain independent from produced equipment",()=>stats.OpenedChestCount==2&&stats.ChestCount==0&&stats.EquipmentCount>stats.OpenedChestCount);
        Check("repeated save cannot inflate either total",()=>{stats.Push(next);return stats.EquipmentCount==5&&stats.OpenedChestCount==2;});
        Check("raw batch retains separate native bucket deltas",()=>delta is not null&&delta.Equipment["common"]==3&&delta.Forged["common"]==2&&delta.NewlyObservedItems.Length==0);
        Check("no activity source required, no false warmup gap",()=>{stats.ActivityUnavailable("no_activity_window");stats.ResetActivityBaseline();return stats.EquipmentAvailable&&!stats.HasGaps&&stats.State().Activity is null;});
        Check("activity cannot double-count production counters",()=>{try{stats.PushActivity(new(DateTimeOffset.UtcNow,"synthetic",null,[]),"synthetic");}catch(InvalidOperationException){return stats.EquipmentCount==5;}return false;});
        Check("temporary save failure is unknown without discarding totals",()=>{stats.SaveUnavailable();return !stats.EquipmentAvailable&&!stats.OpenedChestAvailable&&stats.EquipmentCount==5&&!stats.HasGaps;});
        Check("identical recovered save restores availability without recount",()=>{stats.Push(next);return stats.EquipmentAvailable&&stats.OpenedChestAvailable&&stats.EquipmentCount==5&&!stats.HasGaps;});
        Check("unobserved saves reconcile exact native cumulative delta",()=>{stats.SaveUnavailable();stats.Push(Sample(3000,16,7,103));return stats.EquipmentCount==10&&stats.OpenedChestCount==3&&!stats.HasGaps;});
        Check("state recovery preserves baseline and combined quality totals",()=>{var restored=new StatisticsEngine(JsonSerializer.Deserialize<StatisticsState>(JsonSerializer.Serialize(stats.State(),Program.Json),Program.Json),false,false,true);restored.Push(Sample(3000,16,7,103));return restored.EquipmentCount==10&&restored.Equipment["common"]==10&&restored.OpenedChestCount==3;});
        Check("inventory appearance and transfer are not production events",()=>{var s=Native();s.Push(first);var d=s.Push(next with{ChestEquipment=Counts(10),ForgeEquipment=Counts(3),Items=[new("synthetic-purchased","synthetic-type","rare",1)]});return s.EquipmentCount==0&&s.RecentItems.Count==0&&d!.NewlyObservedItems.Length==0;});
        Check("material or gem opening can increment only opening count",()=>{var s=Native();s.Push(first);s.Push(Sample(2000,10,3,101));return s.OpenedChestCount==1&&s.EquipmentCount==0&&s.Equipment.Values.All(n=>n==0);});
        foreach(var q in SaveSource.Qualities)
            Check("both native buckets preserve quality: "+q,()=>{var s=Native();var raw=first with{ChestEquipment=Counts(),ForgeEquipment=Counts()};s.Push(raw);s.Push(next with{ChestEquipment=SaveSource.Qualities.ToDictionary(k=>k,k=>k==q?2L:0),ForgeEquipment=SaveSource.Qualities.ToDictionary(k=>k,k=>k==q?3L:0)});return s.EquipmentCount==5&&s.Equipment[q]==5;});
        Check("counter reset retains observed lower bound, not negative gains",()=>{var s=Native();s.Push(first);s.Push(next);s.Push(Sample(3000,0,0,0));s.Push(Sample(4000,1,0,1));return s.EquipmentCount==6&&s.HasGaps&&s.OpenedChestIncomplete&&s.OpenedChestCount==3;});
        Check("older save explicitly rebuilds native baseline",()=>{var s=Native();s.Push(next);s.Push(first);return s.Status=="save_rollback"&&s.HasGaps&&s.EquipmentCount==0;});
        Check("identity switch cannot merge production",()=>{var s=Native();s.Push(first);try{s.Push(next with{Identity="other"});}catch(InvalidDataException){return s.EquipmentCount==0&&s.Status=="identity_changed";}return false;});
        Check("same savedAt conflict is unknown, never a second gain",()=>{var s=Native();s.Push(first);s.Push(next with{SavedAt=first.SavedAt});return s.EquipmentCount==0&&!s.EquipmentAvailable&&s.Status=="conflicting_save";});
        var path=Path.Combine(output,"synthetic-production.sqlite3");var store=new SessionStore(path);var old=store.Begin("synthetic:same-process",ordinaryOnly:true);var pure=new StatisticsEngine(null,false,true);pure.Push(first);
        var initial=new ActivitySnapshot(DateTimeOffset.UtcNow,"synthetic","18:00",[new("18:00","掉落了一个宝箱","ChestObtained")]);pure.PushActivity(initial,old.SessionId);
        var oldDrops=pure.PushActivity(initial with{ObservedUtc=initial.ObservedUtc.AddSeconds(1),LatestMinute="18:01",Notices=[..initial.Notices,new("18:01","开启了宝箱：普通 旧样本甲","ChestOpened","旧样本甲","common"),new("18:01","开启了宝箱：普通 旧样本乙","ChestOpened","旧样本乙","common")]},old.SessionId);pure.ActivityUnavailable("old_gap");store.Save(old.SessionId,pure,null,oldDrops);
        var session=store.Begin("synthetic:same-process",false,false,true);var persisted=Native();persisted.Push(first);var produced=persisted.Push(next);store.Save(session.SessionId,persisted,produced);store.Save(session.SessionId,persisted,produced);
        Check("new scope starts a fresh baseline without rewriting old history",()=>session.SessionId!=old.SessionId&&store.History().Single(s=>s.SessionId==old.SessionId).EndReason=="statistics_scope_changed"&&store.History().Single(s=>s.SessionId==old.SessionId).EquipmentCount==2&&JsonSerializer.Deserialize<StatisticsState>(store.History().Single(s=>s.SessionId==old.SessionId).StateJson,Program.Json)!.HasGaps);
        Check("native storage restores the same session",()=>{var resumed=store.Begin("synthetic:same-process",false,false,true);var s=new StatisticsEngine(JsonSerializer.Deserialize<StatisticsState>(resumed.StateJson,Program.Json),false,false,true);s.Push(next);return resumed.SessionId==session.SessionId&&s.EquipmentCount==5&&s.OpenedChestCount==2;});
        Check("storage has one counter batch, no fabricated per-item rows",()=>{using var db=new SqliteConnection("Data Source="+path);db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM Events WHERE SessionId=$id";cmd.Parameters.AddWithValue("$id",session.SessionId);var batches=(long)cmd.ExecuteScalar()!;cmd.CommandText="SELECT COUNT(*) FROM EquipmentDrops WHERE SessionId=$id";return batches==1&&(long)cmd.ExecuteScalar()! ==0;});
        var export=Path.Combine(output,"production-export.json");store.Export(export);using(var j=JsonDocument.Parse(File.ReadAllText(export)))
        {
            var rows=j.RootElement.GetProperty("Sessions").EnumerateArray().ToArray();var row=rows.Single(r=>r.GetProperty("SessionId").GetString()==session.SessionId);var legacy=rows.Single(r=>r.GetProperty("SessionId").GetString()==old.SessionId);
            Check("export names combined scope and exact quality totals",()=>row.GetProperty("EquipmentScope").GetString()==StatisticsEngine.ProductionScope&&row.GetProperty("EquipmentCount").GetInt64()==5&&row.GetProperty("QualityCounts").GetProperty("common").GetInt64()==5&&!row.GetProperty("EquipmentIncomplete").GetBoolean());
            Check("export bucket provenance does not pretend pure chest attribution",()=>row.GetProperty("EquipmentSourceCounts").GetProperty("native_chest_bucket").GetInt64()==3&&row.GetProperty("EquipmentSourceCounts").GetProperty("native_forge_bucket").GetInt64()==2&&!row.GetProperty("PureChestHistoricalQualitiesRecoverable").GetBoolean());
            Check("old pure scope is not reclassified as combined production",()=>legacy.GetProperty("EquipmentScope").GetString()==StatisticsEngine.FarmingScope);
        }
        var unavailableExport=Path.Combine(output,"unavailable-export.json");
        persisted.SaveUnavailable();store.Save(session.SessionId,persisted,null);store.Export(unavailableExport);using(var j=JsonDocument.Parse(File.ReadAllText(unavailableExport)))
        {
            var row=j.RootElement.GetProperty("Sessions").EnumerateArray().Single(r=>r.GetProperty("SessionId").GetString()==session.SessionId);
            Check("unreadable source exports lower bound and incomplete marker",()=>!row.GetProperty("EquipmentCountingAvailable").GetBoolean()&&row.GetProperty("EquipmentIncomplete").GetBoolean()&&row.GetProperty("EquipmentCount").GetInt64()==5);
        }
        object? replay=null;
        if(replayPath is not null)
        {
            var inputHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(replayPath)));
            var rows=File.ReadLines(replayPath).Where(s=>!string.IsNullOrWhiteSpace(s)).Select(s=>JsonDocument.Parse(s)).ToArray();
            try
            {
                var s=Native();SaveSnapshot? start=null,end=null;bool duplicate=true;long? restoredBefore=null,restoredAfter=null;int index=0;
                foreach(var row in rows)
                {
                    var r=row.RootElement;var identity=r.TryGetProperty("IdentityContinuityHash",out var identityValue)?identityValue.GetString():"recorded-whitelist-reconstruction";
                    var raw=new{savedAt=r.GetProperty("SavedAt"),game=new{version=r.GetProperty("Version"),game=new{playerId=identity,sim=new{chestSerial=r.GetProperty("ChestSerial"),bausPerdidos=r.GetProperty("LostChests")},itemsFound=new{chest=r.GetProperty("ChestEquipment"),forge=r.GetProperty("ForgedEquipment")},contadores=new{bausAbertos=r.GetProperty("Opened")}}}};
                    var snap=SaveSource.Parse(JsonSerializer.SerializeToUtf8Bytes(raw));start??=snap;end=snap;s.Push(snap);var count=s.EquipmentCount;s.Push(snap);duplicate&=s.EquipmentCount==count;
                    if(++index==rows.Length/2){restoredBefore=s.EquipmentCount;s=new(JsonSerializer.Deserialize<StatisticsState>(JsonSerializer.Serialize(s.State(),Program.Json),Program.Json),false,false,true);restoredAfter=s.EquipmentCount;}
                }
                var expected=SaveSource.Qualities.ToDictionary(q=>q,q=>end!.ChestEquipment[q]-start!.ChestEquipment[q]+end.ForgeEquipment[q]-start.ForgeEquipment[q]);
                Check("real recorded native bucket delta and seven qualities agree",()=>!s.HasGaps&&SaveSource.Qualities.All(q=>s.Equipment[q]==expected[q])&&s.EquipmentCount==expected.Values.Sum());
                Check("real replay repeated reads and restoration do not recount",()=>duplicate&&restoredBefore==restoredAfter);
                Check("real replay input remains byte-identical",()=>inputHash==Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(replayPath))));
                replay=new{Source="RealWhitelist_ReconstructedSaveEnvelope_ProductionCode_NotLiveSession",Rows=rows.Length,Expected=expected,Actual=s.Equipment,Count=s.EquipmentCount,s.OpenedChestCount,InputSHA256=inputHash,FullAccuracyAccepted=false};
            }
            finally{foreach(var row in rows)row.Dispose();}
        }
        Program.Save(output,"production-counts-result.json",new{Passed=checks.Count-failed,Failed=failed,Checks=checks,Replay=replay,FullAccuracyAccepted=false});Console.WriteLine($"Production counters: {checks.Count-failed}/{checks.Count}; boundary checks and optional real replay, not full accuracy acceptance.");return failed==0?0:1;
    }
}
