using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Companion.Diagnostics;

internal static class ChestOnlyVerification
{
    public static int Run(string output,string? evidenceRoot)
    {
        Directory.CreateDirectory(output);var checks=new List<object>();int failed=0;
        void Check(string name,Func<bool> test){bool pass;string? error=null;try{pass=test();}catch(Exception e){pass=false;error=e.GetType().Name;}checks.Add(new{Name=name,Passed=pass,ErrorType=error});if(!pass)failed++;}
        var labels=AppResources.ActivityQualityLabels;var now=DateTimeOffset.UtcNow;
        ActivitySnapshot Ref(string text,int step)=>GameActivitySource.Parse(text,now.AddSeconds(step),"synthetic-owned-log",labels);
        var baseline="17:00\n掉落了一个宝箱";
        var opened=baseline+"\n17:01\n开启了宝箱：普通 测试剑";
        StatisticsEngine Pure()=>new(chestOnly:true);
        var empty=SaveSource.Qualities.ToDictionary(q=>q,_=>0L);
        var raw=new SaveSnapshot(1000,22,"synthetic-identity",100,0,new(empty),new(empty),[],"synthetic-baseline");
        var mixed=raw with{SavedAt=2000,ChestSerial=101,ChestEquipment=SaveSource.Qualities.ToDictionary(q=>q,q=>q=="rare"?2L:q=="heroic"?2L:q=="common"?3L:q=="uncommon"?1L:0L),ForgeEquipment=SaveSource.Qualities.ToDictionary(q=>q,q=>q=="mythic"?1L:0L),Fingerprint="synthetic-mixed"};
        Check("raw mixed bucket cannot populate chest-only equipment",()=>{var s=Pure();s.Push(raw);s.Push(mixed);return s.EquipmentCount==0&&!s.EquipmentAvailable&&s.State().MixedEquipment!.Values.Sum()==8;});
        Check("first activity read is baseline not historical income",()=>{var s=Pure();s.PushActivity(Ref(opened,0),"synthetic");return s.EquipmentCount==0;});
        Check("immediate and sealed transmutation notifications excluded",()=>{var s=Pure();s.PushActivity(Ref(baseline,0),"synthetic");s.PushActivity(Ref(opened+"\n17:02\n转化完成：稀有 即时转化\n17:02\n转化完成：史诗 密封转化",1),"synthetic");return s.EquipmentCount==1&&s.Equipment["rare"]==0&&s.Equipment["heroic"]==0;});
        Check("forge count cannot enter chest equipment qualities",()=>{var s=Pure();s.Push(raw);s.Push(mixed);s.PushActivity(Ref(baseline,0),"synthetic");s.PushActivity(Ref(opened,1),"synthetic");return s.EquipmentCount==1&&s.Forged["mythic"]==1&&s.Equipment["mythic"]==0;});
        Check("repeated read is not another drop",()=>{var s=Pure();s.PushActivity(Ref(baseline,0),"synthetic");s.PushActivity(Ref(opened,1),"synthetic");s.PushActivity(Ref(opened,2),"synthetic");return s.EquipmentCount==1;});
        Check("two same-name notifications are two drops",()=>{var s=Pure();s.PushActivity(Ref(baseline,0),"synthetic");s.PushActivity(Ref(opened+"\n17:01\n开启了宝箱：普通 测试剑",1),"synthetic");return s.EquipmentCount==2;});
        Check("resume restored cursor cannot recount history",()=>{var s=Pure();s.PushActivity(Ref(baseline,0),"synthetic");s.PushActivity(Ref(opened,1),"synthetic");var restored=new StatisticsEngine(JsonSerializer.Deserialize<StatisticsState>(JsonSerializer.Serialize(s.State(),Program.Json),Program.Json),true);restored.PushActivity(Ref(opened,2),"synthetic");return restored.EquipmentCount==1;});
        Check("identity change rejects mixed account before activity input",()=>{var s=Pure();s.Push(raw);try{s.Push(mixed with{Identity="another"});return false;}catch(InvalidDataException){return s.EquipmentCount==0;}});
        Check("missing source stays unavailable despite mixed positive bucket",()=>{var s=Pure();s.Push(raw);s.Push(mixed);s.ActivityUnavailable("missing_log");return !s.EquipmentAvailable&&s.HasGaps&&s.EquipmentCount==0;});
        Check("source interruption preserves lower bound and marks incomplete",()=>{var s=Pure();s.PushActivity(Ref(baseline,0),"synthetic");s.PushActivity(Ref(opened,1),"synthetic");s.ActivityUnavailable("missing_log");return s.EquipmentCount==1&&!s.EquipmentAvailable&&s.HasGaps;});
        Check("ambiguous equipment quantity makes the source incomplete not zero",()=>{var s=Pure();s.PushActivity(Ref(baseline,0),"synthetic");s.PushActivity(Ref(baseline+"\n17:01\n开启了宝箱：普通 测试剑×2",1),"synthetic");return s.EquipmentCount==0&&!s.EquipmentAvailable&&s.HasGaps;});
        Check("pause rebaseline excludes paused history",()=>{var s=Pure();s.PushActivity(Ref(baseline,0),"synthetic");s.ResetActivityBaseline();s.PushActivity(Ref(opened,2),"synthetic");return s.EquipmentCount==0&&s.HasGaps;});
        Check("opening initial backlog is not capped by new chests",()=>{var s=Pure();s.Push(raw);s.Push(mixed);s.PushActivity(Ref(baseline,0),"synthetic");s.PushActivity(Ref(opened+"\n17:01\n开启了宝箱：普通 第二剑",1),"synthetic");return s.ChestCount==1&&s.EquipmentCount==2;});
        Check("ordinary and colossus openings both count with distinct sources",()=>{var s=Pure();s.PushActivity(Ref(baseline,0),"synthetic");s.PushActivity(Ref(opened+"\n17:02\n巨像宝箱：稀有 巨像环",1),"synthetic");var sources=s.State().EquipmentSourceCounts!;return s.EquipmentCount==2&&s.Equipment["rare"]==1&&sources["ordinary"]==1&&sources["colossus"]==1;});
        Check("colossus currency plus equipment is one item across repeated reads",()=>{var s=Pure();s.PushActivity(Ref(baseline,0),"synthetic");var text=baseline+"\n17:02\n巨像宝箱：+323.4k\n17:02\n巨像宝箱：稀有 贤者长袍";s.PushActivity(Ref(text,1),"synthetic");s.PushActivity(Ref(text,2),"synthetic");return s.EquipmentCount==1&&s.State().EquipmentSourceCounts!["colossus"]==1;});
        Check("colossus materials and unnamed rewards are not equipment",()=>{var s=Pure();s.PushActivity(Ref(baseline,0),"synthetic");s.PushActivity(Ref(baseline+"\n17:02\n巨像宝箱：秘银矿石\n17:02\n巨像宝箱：稀有 ",1),"synthetic");return s.EquipmentCount==0;});
        foreach(var label in labels)
            Check("colossus quality contract "+label.Value,()=>{var s=Pure();s.PushActivity(Ref(baseline,0),"synthetic");s.PushActivity(Ref(baseline+"\n17:02\n巨像宝箱："+label.Key+" 合成契约样本",1),"synthetic");return s.EquipmentCount==1&&s.Equipment[label.Value]==1;});
        var testDb=Path.Combine(output,"synthetic-chest-only.sqlite3");var store=new SessionStore(testDb);
        var legacy=store.Begin("synthetic:legacy");var legacyStats=new StatisticsEngine();legacyStats.Push(raw);var legacyDelta=legacyStats.Push(mixed);store.Save(legacy.SessionId,legacyStats,legacyDelta);store.End(legacy.SessionId,"synthetic_end");
        var current=store.Begin("synthetic:chest-only",true);var currentStats=new StatisticsEngine(JsonSerializer.Deserialize<StatisticsState>(current.StateJson,Program.Json),true);currentStats.Push(raw);var delta=currentStats.Push(mixed);currentStats.PushActivity(Ref(baseline,0),current.SessionId);var drops=currentStats.PushActivity(Ref(opened,1),current.SessionId);store.Save(current.SessionId,currentStats,delta,drops);store.Save(current.SessionId,currentStats,null,drops);
        Check("history keeps old eight mixed and new one chest-only",()=>{var h=store.History();return h.Single(x=>x.SessionId==legacy.SessionId).EquipmentCount==8&&h.Single(x=>x.SessionId==current.SessionId).EquipmentCount==1;});
        var export=Path.Combine(output,"synthetic-history.json");store.Export(export);
        Check("export keeps per-session scope and does not recast old history",()=>{using var j=JsonDocument.Parse(File.ReadAllText(export));var rows=j.RootElement.GetProperty("Sessions").EnumerateArray().ToArray();return rows.Single(x=>x.GetProperty("SessionId").GetString()==legacy.SessionId).GetProperty("EquipmentScope").GetString()=="LegacyMixedCounter"&&rows.Single(x=>x.GetProperty("SessionId").GetString()==current.SessionId).GetProperty("EquipmentCount").GetInt64()==1;});
        Check("storage duplicate event retry creates one chest drop",()=>{using var db=new SqliteConnection("Data Source="+testDb);db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM EquipmentDrops WHERE SessionId=$id AND Source='NativeVisibleChestOpening'";cmd.Parameters.AddWithValue("$id",current.SessionId);return (long)cmd.ExecuteScalar()! ==1;});
        var unknown=store.Begin("synthetic:unknown",true);var unknownStats=Pure();unknownStats.Push(raw);store.Save(unknown.SessionId,unknownStats,null);var unknownExport=Path.Combine(output,"synthetic-unknown.json");store.Export(unknownExport);
        Check("unknown equipment exports null not zero",()=>{using var j=JsonDocument.Parse(File.ReadAllText(unknownExport));var r=j.RootElement.GetProperty("Sessions").EnumerateArray().Single(x=>x.GetProperty("SessionId").GetString()==unknown.SessionId);return r.GetProperty("EquipmentCount").ValueKind==JsonValueKind.Null&&r.GetProperty("QualityCounts").ValueKind==JsonValueKind.Null&&!r.GetProperty("EquipmentCountingAvailable").GetBoolean();});
        var failing=store.Begin("synthetic:failed-activity-save",true);var failingStats=Pure();failingStats.Push(raw);failingStats.PushActivity(Ref(baseline,0),failing.SessionId);var failedDrops=failingStats.PushActivity(Ref(opened,1),failing.SessionId);
        using(var db=new SqliteConnection("Data Source="+testDb)){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="CREATE TRIGGER fail_activity_summary BEFORE UPDATE ON Sessions BEGIN SELECT RAISE(ABORT,'synthetic failure'); END";cmd.ExecuteNonQuery();}
        bool saveFailed=false;try{store.Save(failing.SessionId,failingStats,null,failedDrops);}catch(SqliteException){saveFailed=true;}
        Check("activity save failure reported without advancing persisted summary",()=>saveFailed&&store.History().Single(s=>s.SessionId==failing.SessionId).EquipmentCount==0);
        Check("activity event and drop rolled back with failed summary",()=>{using var db=new SqliteConnection("Data Source="+testDb);db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM Events WHERE SessionId=$id";cmd.Parameters.AddWithValue("$id",failing.SessionId);return (long)cmd.ExecuteScalar()! ==0;});
        using(var db=new SqliteConnection("Data Source="+testDb)){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="DROP TRIGGER fail_activity_summary";cmd.ExecuteNonQuery();}
        store.Save(failing.SessionId,failingStats,null,failedDrops);
        Check("retry after activity transaction failure saves count once",()=>store.History().Single(s=>s.SessionId==failing.SessionId).EquipmentCount==1);
        var openLegacy=store.Begin("synthetic:scope-switch");store.Save(openLegacy.SessionId,legacyStats,null);var switched=store.Begin("synthetic:scope-switch",true);
        Check("scope switch closes old mixed session instead of reinterpreting",()=>switched.SessionId!=openLegacy.SessionId&&store.History().Single(s=>s.SessionId==openLegacy.SessionId).EndReason=="statistics_scope_changed"&&store.History().Single(s=>s.SessionId==openLegacy.SessionId).EquipmentCount==8);
        var giantSession=store.Begin("synthetic:colossus-storage",true);var giantStats=Pure();giantStats.Push(raw);giantStats.PushActivity(Ref(baseline,0),giantSession.SessionId);
        var combined=opened+"\n17:02\n巨像宝箱：+323.4k\n17:02\n巨像宝箱：稀有 贤者长袍\n17:03\n转化完成：神话 专注腰带";
        var giantDrops=giantStats.PushActivity(Ref(combined,1),giantSession.SessionId);store.Save(giantSession.SessionId,giantStats,null,giantDrops);store.Save(giantSession.SessionId,giantStats,null,giantDrops);
        Check("colossus persistence accepts both opening types and rejects currency",()=>{using var db=new SqliteConnection("Data Source="+testDb);db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM EquipmentDrops WHERE SessionId=$id";cmd.Parameters.AddWithValue("$id",giantSession.SessionId);return (long)cmd.ExecuteScalar()! ==2;});
        Check("persisted colossus category is distinct from ordinary",()=>{using var db=new SqliteConnection("Data Source="+testDb);db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM EquipmentDrops WHERE SessionId=$id AND Source='NativeVisibleColossusOpening'";cmd.Parameters.AddWithValue("$id",giantSession.SessionId);return (long)cmd.ExecuteScalar()! ==1;});
        var resume=store.Begin("synthetic:colossus-storage",true);var resumedStats=new StatisticsEngine(JsonSerializer.Deserialize<StatisticsState>(resume.StateJson,Program.Json),true);resumedStats.PushActivity(Ref(combined,2),resume.SessionId);store.Save(resume.SessionId,resumedStats,null);
        Check("colossus cursor and source counts survive storage recovery",()=>resumedStats.EquipmentCount==2&&resumedStats.State().EquipmentSourceCounts!["ordinary"]==1&&resumedStats.State().EquipmentSourceCounts!["colossus"]==1);
        var giantExport=Path.Combine(output,"synthetic-colossus-history.json");store.Export(giantExport);
        Check("colossus history and export retain both categories and total",()=>{using var doc=JsonDocument.Parse(File.ReadAllText(giantExport));var row=doc.RootElement.GetProperty("Sessions").EnumerateArray().Single(x=>x.GetProperty("SessionId").GetString()==giantSession.SessionId);var counts=row.GetProperty("EquipmentSourceCounts");return row.GetProperty("EquipmentCount").GetInt64()==2&&counts.GetProperty("ordinary").GetInt64()==1&&counts.GetProperty("colossus").GetInt64()==1;});
        var v1Session=store.Begin("synthetic:old-ordinary-scope");var v1Stats=new StatisticsEngine(currentStats.State() with{EquipmentScope=StatisticsEngine.OrdinaryOnlyScope,EquipmentSourceCounts=null});store.Save(v1Session.SessionId,v1Stats,null);store.End(v1Session.SessionId,"synthetic_old_scope");var upgraded=store.Begin("synthetic:old-ordinary-scope",true);
        Check("old ordinary-only history is preserved without retroactive colossus counting",()=>store.History().Single(x=>x.SessionId==v1Session.SessionId).EquipmentCount==1&&upgraded.SessionId!=v1Session.SessionId&&v1Stats.EquipmentScope==StatisticsEngine.OrdinaryOnlyScope);
        var farmSession=store.Begin("synthetic:farming-only",ordinaryOnly:true);
        var farm=new StatisticsEngine(null,false,true);farm.Push(raw);farm.PushActivity(Ref(baseline,0),farmSession.SessionId);
        var farmDrops=farm.PushActivity(Ref(combined,1),farmSession.SessionId);
        Check("farming excludes colossus and transmutation from actual totals qualities and recent",()=>farm.EquipmentCount==1&&farm.Equipment["rare"]==0&&farm.Equipment["mythic"]==0&&farm.ChestDrops.Single().Notice.Kind=="ChestOpened");
        store.Save(farmSession.SessionId,farm,null,farmDrops);store.Save(farmSession.SessionId,farm,null,farmDrops);
        var farmResume=store.Begin("synthetic:farming-only",ordinaryOnly:true);
        var farmRestored=new StatisticsEngine(JsonSerializer.Deserialize<StatisticsState>(farmResume.StateJson,Program.Json),false,true);
        farmRestored.PushActivity(Ref(combined,2),farmResume.SessionId);
        Check("farming recovery keeps ordinary scope and never recounts ignored rewards",()=>farmResume.SessionId==farmSession.SessionId&&farmRestored.EquipmentCount==1&&farmRestored.State().EquipmentSourceCounts!["colossus"]==0);
        var farmExport=Path.Combine(output,"synthetic-farming-history.json");store.Export(farmExport);
        Check("farming persisted history and export have one ordinary drop only",()=>{using var j=JsonDocument.Parse(File.ReadAllText(farmExport));var row=j.RootElement.GetProperty("Sessions").EnumerateArray().Single(x=>x.GetProperty("SessionId").GetString()==farmSession.SessionId);using var db=new SqliteConnection("Data Source="+testDb);db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM EquipmentDrops WHERE SessionId=$id";cmd.Parameters.AddWithValue("$id",farmSession.SessionId);return row.GetProperty("EquipmentCount").GetInt64()==1&&row.GetProperty("EquipmentScope").GetString()==StatisticsEngine.FarmingScope&&(long)cmd.ExecuteScalar()! ==1;});
        var oldCombined=store.Begin("synthetic:farming-switch",true);store.Save(oldCombined.SessionId,giantStats,null,giantDrops);
        var farmSwitched=store.Begin("synthetic:farming-switch",ordinaryOnly:true);
        Check("farming starts new session without rewriting previous combined history",()=>farmSwitched.SessionId!=oldCombined.SessionId&&store.History().Single(x=>x.SessionId==oldCombined.SessionId) is{EquipmentCount:2,EndReason:"statistics_scope_changed"});
        bool rejectsGiant=false;try{store.Save(farmSession.SessionId,farm,null,giantDrops.Where(x=>x.Notice.Kind=="ColossusReward").ToArray());}catch(InvalidDataException){rejectsGiant=true;}
        Check("farming persistence rejects accidentally forwarded colossus events",()=>rejectsGiant);
        var bulk="\n17:01\n开启了2个宝箱：稀有 秘法头环 ×2";
        var bulkEngine=new StatisticsEngine(null,false,true);bulkEngine.PushActivity(Ref(baseline,0),"bulk");var bulkEvents=bulkEngine.PushActivity(Ref(baseline+bulk,1),"bulk");
        bulkEngine.PushActivity(Ref(baseline+bulk,2),"bulk");
        Check("farming exact native batch counts two and repeated reads count once",()=>bulkEngine.EquipmentCount==2&&bulkEngine.Equipment["rare"]==2&&bulkEvents.Length==2);
        var secondBulkEvents=bulkEngine.PushActivity(Ref(baseline+bulk+bulk,3),"bulk");
        Check("two identical batch occurrences preserve four real items",()=>bulkEngine.EquipmentCount==4&&bulkEngine.Equipment["rare"]==4);
        var bulkRestore=new StatisticsEngine(JsonSerializer.Deserialize<StatisticsState>(JsonSerializer.Serialize(bulkEngine.State(),Program.Json),Program.Json),false,true);
        bulkRestore.PushActivity(Ref(baseline+bulk+bulk,4),"bulk");
        Check("expanded batch cursor survives JSON recovery without recount",()=>bulkRestore.EquipmentCount==4);
        var bulkSession=store.Begin("synthetic:bulk-storage",ordinaryOnly:true);var allBulkEvents=bulkEvents.Concat(secondBulkEvents).ToArray();store.Save(bulkSession.SessionId,bulkEngine,null,allBulkEvents);store.Save(bulkSession.SessionId,bulkEngine,null,allBulkEvents);
        Check("expanded batch events retain unique IDs in storage and match summary",()=>{using var db=new SqliteConnection("Data Source="+testDb);db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM EquipmentDrops WHERE SessionId=$id";cmd.Parameters.AddWithValue("$id",bulkSession.SessionId);return (long)cmd.ExecuteScalar()! ==4&&store.History().Single(x=>x.SessionId==bulkSession.SessionId).EquipmentCount==4;});
        var clippedSession=store.Begin("synthetic:bulk-clipped",ordinaryOnly:true);var clipped=new StatisticsEngine(null,false,true);clipped.Push(raw);clipped.PushActivity(Ref(baseline,0),clippedSession.SessionId);
        var clippedDrops=clipped.PushActivity(Ref(baseline+"\n17:01\n开启了7个宝箱：普通 慈悲之书, 优秀 城垛铁靴, 稀有 秘法头环, 史诗 朝圣者十字, 神话 专注腰带 (+2)",1),clippedSession.SessionId);
        store.Save(clippedSession.SessionId,clipped,null,clippedDrops);var clippedExport=Path.Combine(output,"synthetic-bulk-clipped.json");store.Export(clippedExport);
        Check("clipped bulk is known lower bound five not guessed seven",()=>clipped.EquipmentCount==5&&!clipped.EquipmentAvailable&&clipped.HasGaps);
        Check("clipped bulk history export remains explicitly incomplete",()=>{using var doc=JsonDocument.Parse(File.ReadAllText(clippedExport));var row=doc.RootElement.GetProperty("Sessions").EnumerateArray().Single(x=>x.GetProperty("SessionId").GetString()==clippedSession.SessionId);return row.GetProperty("EquipmentIncomplete").GetBoolean()&&!row.GetProperty("EquipmentCountingAvailable").GetBoolean()&&row.GetProperty("EquipmentCount").GetInt64()==5;});
        var previousParser=store.Begin("synthetic:parser-switch");store.Save(previousParser.SessionId,v1Stats,null);var nextParser=store.Begin("synthetic:parser-switch",ordinaryOnly:true);
        Check("batch parser upgrade starts fresh baseline instead of backfilling formerly ignored batches",()=>nextParser.SessionId!=previousParser.SessionId&&JsonSerializer.Deserialize<StatisticsState>(nextParser.StateJson,Program.Json)!.EquipmentScope==StatisticsEngine.FarmingScope);
        var bootstrapSession=store.Begin("synthetic:empty-log-bootstrap",ordinaryOnly:true);var bootstrap=new StatisticsEngine(null,false,true);bootstrap.Push(raw);
        bootstrap.PushActivity(Ref("17:01",0),bootstrapSession.SessionId);bootstrap.PushActivity(Ref(opened,1),bootstrapSession.SessionId);
        Check("initial empty own-notice source never backfills loaded equipment history",()=>bootstrap.EquipmentCount==0&&bootstrap.HasGaps&&!bootstrap.EquipmentAvailable);
        store.Save(bootstrapSession.SessionId,bootstrap,null);var bootstrapExport=Path.Combine(output,"synthetic-bootstrap-unknown.json");store.Export(bootstrapExport);
        Check("bootstrap uncertainty exports null instead of confirmed zero equipment",()=>{using var doc=JsonDocument.Parse(File.ReadAllText(bootstrapExport));var row=doc.RootElement.GetProperty("Sessions").EnumerateArray().Single(x=>x.GetProperty("SessionId").GetString()==bootstrapSession.SessionId);return row.GetProperty("EquipmentCount").ValueKind==JsonValueKind.Null&&row.GetProperty("EquipmentIncomplete").GetBoolean();});
        var bootstrapResume=store.Begin("synthetic:empty-log-bootstrap",ordinaryOnly:true);var afterBootstrap=new StatisticsEngine(JsonSerializer.Deserialize<StatisticsState>(bootstrapResume.StateJson,Program.Json),false,true);
        var afterBootstrapDrops=afterBootstrap.PushActivity(Ref(opened+"\n17:02\n开启了宝箱：优秀 基线后弓",2),bootstrapResume.SessionId);store.Save(bootstrapResume.SessionId,afterBootstrap,null,afterBootstrapDrops);store.Save(bootstrapResume.SessionId,afterBootstrap,null,afterBootstrapDrops);
        Check("bootstrap recovery counts only subsequent ordinary equipment once",()=>afterBootstrap.EquipmentCount==1&&afterBootstrap.Equipment["common"]==0&&afterBootstrap.Equipment["uncommon"]==1&&afterBootstrap.HasGaps);
        Check("bootstrap history summary and drop rows never contain historical equipment",()=>{using var db=new SqliteConnection("Data Source="+testDb);db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM EquipmentDrops WHERE SessionId=$id AND EquipmentName='基线后弓'";cmd.Parameters.AddWithValue("$id",bootstrapSession.SessionId);return (long)cmd.ExecuteScalar()! ==1&&store.History().Single(x=>x.SessionId==bootstrapSession.SessionId).EquipmentCount==1;});
        object? replay=null;
        if(evidenceRoot is not null)
        {
            var beforePath=Path.Combine(evidenceRoot,"phase2-positive-yield-20261108-171958/followup/real-samples.jsonl");
            var afterPath=Path.Combine(evidenceRoot,"live-equipment-20261108-175328/real-samples.jsonl");
            var activityPath=Path.Combine(evidenceRoot,"phase2-rare-colossus-loss-20261108-180013/activity-broad.json");
            using var before=JsonDocument.Parse(File.ReadLines(beforePath).Last());using var after=JsonDocument.Parse(File.ReadLines(afterPath).Last());using var activityDoc=JsonDocument.Parse(File.ReadAllText(activityPath));
            SaveSnapshot Load(JsonElement row)=>SaveSource.Parse(JsonSerializer.SerializeToUtf8Bytes(new{savedAt=row.GetProperty("SavedAt").GetInt64(),game=new{version=row.GetProperty("Version").GetInt32(),game=new{playerId="RECORDED_WHITELIST_RECONSTRUCTION_NOT_ORIGINAL_IDENTITY",sim=new{chestSerial=row.GetProperty("ChestSerial").GetInt64(),bausPerdidos=row.GetProperty("LostChests").GetInt64(),inventory=Array.Empty<object>(),stash=Array.Empty<object>()},itemsFound=new{chest=row.GetProperty("ChestEquipment"),forge=row.GetProperty("ForgedEquipment")}}}}));
            var first=Load(before.RootElement);var last=Load(after.RootElement);
            var start=DateTimeOffset.FromUnixTimeMilliseconds(first.SavedAt).ToOffset(TimeSpan.FromHours(8));var end=DateTimeOffset.FromUnixTimeMilliseconds(last.SavedAt).ToOffset(TimeSpan.FromHours(8));
            var events=activityDoc.RootElement.GetProperty("HistoricalEvents").EnumerateArray().Select(x=>(Time:x.GetProperty("time").GetString()!,Text:x.GetProperty("text").GetString()!)).Concat(activityDoc.RootElement.GetProperty("RewardCandidates").EnumerateArray().Where(x=>x.GetProperty("text").GetString()!.StartsWith("转化完成：",StringComparison.Ordinal)).Select(x=>(Time:x.GetProperty("time").GetString()!,Text:x.GetProperty("text").GetString()!))).OrderBy(x=>x.Time,StringComparer.Ordinal).ToArray();
            string BeforeText()=>string.Join('\n',events.Where(e=>TimeOnly.Parse(e.Time)<TimeOnly.FromDateTime(start.DateTime)).Select(e=>e.Time+"\n"+e.Text));
            string AfterText()=>string.Join('\n',events.Where(e=>TimeOnly.Parse(e.Time).AddMinutes(1)<=TimeOnly.FromDateTime(end.DateTime)).Select(e=>e.Time+"\n"+e.Text));
            var stats=Pure();stats.Push(first);stats.PushActivity(GameActivitySource.Parse(BeforeText(),start,"recorded-window-reconstruction",labels),"recorded-scope");var rawDelta=stats.Push(last);var accepted=stats.PushActivity(GameActivitySource.Parse(AfterText(),end,"recorded-window-reconstruction",labels),"recorded-scope");
            Check("real counterexample pure chest is five not mixed eight",()=>stats.EquipmentCount==5&&stats.State().MixedEquipment!.Values.Sum()==8);
            Check("real qualities exclude rare two and transmuted epic one",()=>stats.Equipment["common"]==3&&stats.Equipment["uncommon"]==1&&stats.Equipment["heroic"]==1&&stats.Equipment["rare"]==0);
            var realStore=new SessionStore(Path.Combine(output,"recorded-reference-replay.sqlite3"));var realSession=realStore.Begin("synthetic:recorded-reference-replay",true);realStore.Save(realSession.SessionId,stats,rawDelta,accepted);var realExport=Path.Combine(output,"recorded-reference-export.json");realStore.Export(realExport);
            Check("real counterexample persisted and exported as five",()=>{using var doc=JsonDocument.Parse(File.ReadAllText(realExport));return realStore.History().Single().EquipmentCount==5&&doc.RootElement.GetProperty("Sessions")[0].GetProperty("EquipmentCount").GetInt64()==5;});
            Check("real exported quality distribution is three common one uncommon one epic",()=>{using var doc=JsonDocument.Parse(File.ReadAllText(realExport));var q=doc.RootElement.GetProperty("Sessions")[0].GetProperty("QualityCounts");return q.GetProperty("common").GetInt64()==3&&q.GetProperty("uncommon").GetInt64()==1&&q.GetProperty("heroic").GetInt64()==1&&q.GetProperty("rare").GetInt64()==0;});
            Check("real stored drops contain no transmuted rare equipment",()=>{using var db=new SqliteConnection("Data Source="+Path.Combine(output,"recorded-reference-replay.sqlite3"));db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM EquipmentDrops WHERE Quality='rare'";return (long)cmd.ExecuteScalar()! ==0;});
            replay=new{Source="RealWhitelistAndMinuteReference_ReconstructedWindow_NotLiveSession",EquipmentCount=stats.EquipmentCount,QualityCounts=stats.Equipment,MixedCounter=stats.State().MixedEquipment,WindowStart=start,WindowEnd=end,ExcludedTransmutationNotices=3,IdentityContinuity="not present in recording",ReferenceCompleteness="unknown",ReferenceOrdering="minute-sorted reconstruction; not original polling frames",AccuracyPercent=(double?)null,Precision=(double?)null,Recall=(double?)null};
        }
        Program.Save(output,"chest-only-result.json",new{Passed=failed==0,Checks=checks,Failed=failed,Replay=replay,FullAccuracyAccepted=false});
        Console.WriteLine($"Chest-only acceptance: {checks.Count-failed}/{checks.Count}; full accuracy remains unaccepted.");return failed==0?0:1;
    }
}
