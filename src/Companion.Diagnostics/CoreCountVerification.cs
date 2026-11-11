using System.Text.Json;
using System.Text.Json.Nodes;

namespace Companion.Diagnostics;

// Development checks inside the existing executable; never a second player entry.
internal static class CoreCountVerification
{
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);var checks=new List<object>();var failed=0;
        void Check(string name,Func<bool> test){bool pass;string? error=null;try{pass=test();}catch(Exception e){pass=false;error=e.GetType().Name+": "+e.Message;}checks.Add(new{Name=name,Passed=pass,Error=error});if(!pass)failed++;}
        var empty=SaveSource.Qualities.ToDictionary(q=>q,_=>0L);
        SaveSnapshot Sample(long at,long normal,long stage=20,long act=3)=>new(at,22,"synthetic-core",1000,0,new(empty),new(empty),[],"synthetic-"+at){OpenedChestCounters=new(){["normal"]=normal,["stage"]=stage,["act"]=act}};
        StatisticsEngine Core()=>new(null,false,true);
        var first=Sample(1000,100);var next=Sample(2000,102,21);var stats=Core();
        Check("first read establishes zero session openings and lifetime 123",()=>{stats.Push(first);return stats.OpenedChestCount==0&&stats.Previous!.OpenedChests==123&&stats.OpenedChestAvailable;});
        Check("opening delta independent of newly obtained chests",()=>{stats.Push(next);return stats.OpenedChestCount==3&&stats.ChestCount==0&&stats.Previous!.OpenedChests==126;});
        Check("repeated save does not recount openings",()=>{stats.Push(next);return stats.OpenedChestCount==3;});
        Check("stored state recovery does not recount",()=>{var s=new StatisticsEngine(JsonSerializer.Deserialize<StatisticsState>(JsonSerializer.Serialize(stats.State(),Program.Json),Program.Json),false,true);s.Push(next);return s.OpenedChestCount==3&&s.Previous!.OpenedChests==126;});
        Check("opening rollback resets baseline with explicit incompleteness",()=>{var s=Core();s.Push(first);s.Push(next);s.Push(Sample(3000,10));return s.OpenedChestCount==0&&s.OpenedChestIncomplete&&s.HasGaps&&s.Status=="counter_reset";});
        Check("category rollback cannot hide behind total growth",()=>{var s=Core();s.Push(first);s.Push(Sample(2000,99,25));return s.Status=="counter_reset"&&s.OpenedChestCount==0;});
        Check("older save resets farming baseline instead of negative income",()=>{var s=Core();s.Push(next);s.Push(first);return s.Status=="save_rollback"&&s.Previous==first&&s.OpenedChestCount==0&&s.OpenedChestIncomplete;});
        Check("positive increments resume from reset baseline",()=>{var s=Core();s.Push(first);s.Push(Sample(2000,10));s.Push(Sample(3000,12));return s.OpenedChestCount==2&&s.OpenedChestIncomplete;});
        Check("identity change is not merged",()=>{var s=Core();s.Push(first);try{s.Push(next with{Identity="another"});}catch(InvalidDataException){return s.Status=="identity_changed"&&s.OpenedChestCount==0;}return false;});
        Check("missing opening source is unavailable not known zero",()=>{var s=Core();s.Push(first with{OpenedChestCounters=null});return s.OpenedChestCount is null&&!s.OpenedChestAvailable;});
        Check("missing interval retains lower bound, never infers old openings",()=>{var s=Core();s.Push(first);s.Push(next);s.Push(Sample(3000,105) with{OpenedChestCounters=null});s.Push(Sample(4000,107));return s.OpenedChestCount==3&&s.OpenedChestIncomplete;});
        Check("read error does not destroy a confirmed lower bound",()=>{stats.OpeningUnavailable();var state=stats.State();return !state.OpenedChestAvailable&&state.OpenedChestCount==3;});
        Check("same save restores opening availability without recount",()=>{stats.Push(next);return stats.OpenedChestAvailable&&stats.OpenedChestCount==3;});
        Check("paused rebaseline preserves prior lower bound",()=>{var s=Core();s.Push(first);s.Push(next);s.Rebaseline(Sample(3000,110));s.Push(Sample(4000,111));return s.OpenedChestCount==4&&s.OpenedChestIncomplete;});
        var raw=new JsonObject{["savedAt"]=1000,["game"]=new JsonObject{["version"]=22,["game"]=new JsonObject{["playerId"]="synthetic",["sim"]=new JsonObject{["chestSerial"]=1000,["bausPerdidos"]=0},["itemsFound"]=new JsonObject{["chest"]=new JsonObject(),["forge"]=new JsonObject()},["contadores"]=new JsonObject{["bausAbertos"]=new JsonObject{["normal"]=100,["stage"]=20,["act"]=3}}}}};
        SaveSnapshot Parse()=>SaveSource.Parse(System.Text.Encoding.UTF8.GetBytes(raw.ToJsonString()));
        Check("native whitelist sums normal stage act",()=>Parse().OpenedChests==123);
        foreach(var value in new JsonNode?[]{JsonValue.Create(-1),JsonValue.Create(1.5),JsonValue.Create("100"),JsonValue.Create(9007199254740992L),null})
        {
            var invalid=value?.DeepClone();
            Check("invalid native opening counter rejected: "+(invalid?.ToJsonString()??"null"),()=>{raw["game"]!["game"]!["contadores"]!["bausAbertos"]!["normal"]=invalid;try{Parse();}catch(InvalidDataException){return true;}return false;});
        }
        var time=DateTimeOffset.UtcNow;var labels=AppResources.ActivityQualityLabels;
        ActivitySnapshot Frame(string text,int step)=>GameActivitySource.Parse(text,time.AddSeconds(step),"synthetic-core-doc",labels);
        var baseline="18:00\n掉落了一个宝箱";
        var full=baseline+"\n18:01\n开启了2个宝箱：稀有 测试物 ×2\n18:02\n转化完成：史诗 转化物\n18:02\n锻造完成：神话 锻造物\n18:03\n开启了宝箱：辉耀精华\n18:03\n开启了宝箱：狂怒宝石 ★";
        var equipment=Core();equipment.Push(first);equipment.PushActivity(Frame(baseline,0),"synthetic-core");
        var drops=equipment.PushActivity(Frame(full,1),"synthetic-core");equipment.PushActivity(Frame(full,2),"synthetic-core");
        Check("batch x2 counted once, transmutation forge gems materials excluded",()=>equipment.EquipmentCount==2&&equipment.Equipment["rare"]==2&&equipment.Equipment["heroic"]==0&&equipment.Equipment["mythic"]==0);
        var dbPath=Path.Combine(output,"synthetic-core.sqlite3");var store=new SessionStore(dbPath);var session=store.Begin("synthetic:core",ordinaryOnly:true);
        store.Save(session.SessionId,equipment,null,drops);store.Save(session.SessionId,equipment,null,drops);
        var stored=store.Begin("synthetic:core",ordinaryOnly:true);var recovered=new StatisticsEngine(JsonSerializer.Deserialize<StatisticsState>(stored.StateJson,Program.Json),false,true);
        recovered.PushActivity(Frame(full,3),stored.SessionId);
        Check("storage and cursor recovery retain openings and equipment",()=>stored.SessionId==session.SessionId&&recovered.OpenedChestCount==0&&recovered.Previous!.OpenedChests==123&&recovered.EquipmentCount==2);
        var clipped=full+"\n18:04\n开启了7个宝箱：普通 一, 优秀 二, 稀有 三, 史诗 四, 神话 五 (+2)";
        var visible=recovered.PushActivity(Frame(clipped,4),stored.SessionId);store.Save(stored.SessionId,recovered,null,visible);
        Check("truncation retains known five equipment lower bound",()=>recovered.EquipmentCount==7&&recovered.HasGaps&&!recovered.EquipmentAvailable);
        var legacy=store.Begin("synthetic:legacy-core");store.Save(legacy.SessionId,new StatisticsEngine(),null);
        var export=Path.Combine(output,"core-export.json");store.Export(export);using var json=JsonDocument.Parse(File.ReadAllText(export));
        var rows=json.RootElement.GetProperty("Sessions").EnumerateArray().ToArray();var current=rows.Single(r=>r.GetProperty("SessionId").GetString()==session.SessionId);var old=rows.Single(r=>r.GetProperty("SessionId").GetString()==legacy.SessionId);
        Check("export preserves opening total and session count independently",()=>current.GetProperty("OpenedChestTotal").GetInt64()==123&&current.GetProperty("OpenedChestCount").GetInt64()==0&&current.GetProperty("ChestCountMeaning").GetString()!.Contains("NotOpened"));
        Check("export preserves partial equipment lower bound and qualities",()=>current.GetProperty("EquipmentCount").GetInt64()==7&&current.GetProperty("EquipmentIncomplete").GetBoolean()&&current.GetProperty("QualityCounts").GetProperty("rare").GetInt64()==3);
        Check("old history cannot invent opening counts or pure qualities",()=>old.GetProperty("OpenedChestTotal").ValueKind==JsonValueKind.Null&&old.GetProperty("OpenedChestCount").ValueKind==JsonValueKind.Null&&!old.GetProperty("PureChestHistoricalQualitiesRecoverable").GetBoolean());
        var lower=store.Begin("synthetic:opening-gap",ordinaryOnly:true);stats.OpeningUnavailable();store.Save(lower.SessionId,stats,null);store.Export(Path.Combine(output,"opening-gap-export.json"));
        using(var gap=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"opening-gap-export.json"))))
        {
            var row=gap.RootElement.GetProperty("Sessions").EnumerateArray().Single(r=>r.GetProperty("SessionId").GetString()==lower.SessionId);
            Check("export interrupted opening source as incomplete lower bound",()=>row.GetProperty("OpenedChestTotal").ValueKind==JsonValueKind.Null&&row.GetProperty("OpenedChestCount").GetInt64()==3&&row.GetProperty("OpenedChestIncomplete").GetBoolean());
        }
        Check("legacy state cannot manufacture missing opening baseline",()=>{var oldState=stats.State() with{OpeningCounterScope=null,OpenedChestCount=null,OpenedChestAvailable=false,Previous=first with{OpenedChestCounters=null}};var oldSession=store.Begin("synthetic:upgrade",ordinaryOnly:true);using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+dbPath)){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE Sessions SET StateJson=$state WHERE SessionId=$id";cmd.Parameters.AddWithValue("$state",JsonSerializer.Serialize(oldState,Program.Json));cmd.Parameters.AddWithValue("$id",oldSession.SessionId);cmd.ExecuteNonQuery();}var updated=store.Begin("synthetic:upgrade",ordinaryOnly:true);return updated.SessionId!=oldSession.SessionId&&store.History().Single(s=>s.SessionId==oldSession.SessionId).EndReason=="statistics_scope_changed";});
        Check("five bundled language labels cover core counters",()=>{AppResources.Validate();return AppResources.Languages.All(l=>{AppResources.Language=l;return AppResources.Text("opened_total")!=AppResources.Text("unknown")&&AppResources.Text("opened_session")!=AppResources.Text("unknown");});});
        Program.Save(output,"core-counts-result.json",new{Synthetic=true,Passed=checks.Count-failed,Failed=failed,Checks=checks,FullAccuracyAccepted=false});
        Console.WriteLine($"Core counts: {checks.Count-failed}/{checks.Count}; synthetic boundary checks, not live accuracy.");return failed==0?0:1;
    }
}
