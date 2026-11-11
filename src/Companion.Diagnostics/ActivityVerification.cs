using System.Text.Json;

namespace Companion.Diagnostics;

// Development verification inside the same executable; not enabled in player statistics until the source gate passes.
internal static class ActivityVerification
{
    public static int Tests(string output,string? beforePath,string? afterPath)
    {
        Directory.CreateDirectory(output);var checks=new List<object>();int failed=0;
        void Check(string name,Func<bool> test)
        {
            bool pass;string? error=null;try{pass=test();}catch(Exception e){pass=false;error=e.GetType().Name;}
            checks.Add(new{Name=name,Passed=pass,ErrorType=error});if(!pass)failed++;
        }
        var labels=AppResources.ActivityQualityLabels;
        var now=DateTimeOffset.Parse("2026-11-08T10:00:00Z");
        ActivitySnapshot Parse(string text,int step=0,string doc="synthetic-document")=>GameActivitySource.Parse(text,now.AddSeconds(step),doc,labels);
        bool Reject(string text){try{Parse(text);return false;}catch(InvalidDataException){return true;}}
        var a="18:00\n掉落了一个宝箱";var b="18:01\n开启了宝箱：普通 测试剑";
        Check("strict own notification prefixes exclude other-player lines",()=>Parse("18:00\n其他玩家开启了宝箱：普通 PRIVATE_PLAYER\n"+a).Notices.Length==1);
        Check("recognized quality and equipment name retained",()=>Parse(b).Notices.Single() is{Name:"测试剑",Quality:"common",Kind:"ChestOpened"});
        Check("colossus equipment separated from ordinary chest",()=>Parse("18:02\n巨像宝箱：稀有 测试环").Notices.Single() is{Quality:"rare",Kind:"ColossusReward"});
        Check("minerals are not equipment",()=>Parse("18:02\n开启了宝箱：秘银矿石").Notices.Single().Quality is null);
        Check("unknown qualities are not automatic equipment",()=>Parse("18:02\n开启了宝箱：未知 测试剑").Notices.Single().Quality is null);
        Check("ambiguous quantity does not become one equipment",()=>Parse("18:02\n开启了宝箱：普通 测试剑×2").Notices.Single().Quality is null);
        Check("native batch distinct equipment expands explicit groups",()=>{var n=Parse("18:02\n开启了2个宝箱：稀有 秘法头环, 普通 贤者长袍").Notices;return n.Length==2&&n[0].Quality=="rare"&&n[1].Quality=="common"&&n.Select(x=>x.Key).Distinct().Count()==2;});
        Check("native batch repeated item expands exact multiplier",()=>{var n=Parse("18:02\n开启了2个宝箱：稀有 秘法头环 ×2").Notices;return n.Length==2&&n.All(x=>x.Name=="秘法头环"&&x.Kind=="ChestOpened")&&n.Select(x=>x.Key).Distinct().Count()==2;});
        Check("clipped batch keeps five visible groups plus unknown marker",()=>{var n=Parse("18:02\n开启了7个宝箱：普通 慈悲之书, 优秀 城垛铁靴, 稀有 秘法头环, 史诗 朝圣者十字, 神话 专注腰带 (+2)").Notices;return n.Count(x=>x.Quality is not null)==5&&n.Count(x=>x.Kind=="ChestEquipmentUnconfirmed")==1;});
        Check("batch material does not become equipment or silently complete",()=>{var n=Parse("18:02\n开启了2个宝箱：普通 贤者长袍, 秘银矿石 ×2").Notices;return n.Count(x=>x.Quality is not null)==1&&n.Any(x=>x.Kind=="ChestEquipmentUnconfirmed");});
        Check("malformed batch multiplier remains unconfirmed",()=>Parse("18:02\n开启了2个宝箱：稀有 秘法头环 ×0").Notices.Single().Kind=="ChestEquipmentUnconfirmed");
        Check("unrecognized bulk prefix remains unconfirmed",()=>Parse("18:02\n开启了999999999999个宝箱：普通 测试剑").Notices.Single().Kind=="ChestEquipmentUnconfirmed");
        Check("batch expansion is bounded",()=>Reject("18:02\n开启了999个宝箱：普通 测试剑 ×999, 普通 测试盾 ×999"));
        Check("batch missing timestamp is rejected",()=>Reject("开启了2个宝箱：普通 测试剑, 稀有 测试盾"));
        Check("unrelated player batch is not our reward",()=>Parse("18:02\n其他玩家开启了2个宝箱：普通 测试剑").Notices.Length==0);
        Check("missing timestamp rejected",()=>Reject("开启了宝箱：普通 测试剑"));
        Check("invalid timestamp rejected",()=>Reject("25:61\n开启了宝箱：普通 测试剑"));
        Check("truncated text rejected",()=>Reject(new string('x',250000)));
        Check("oversized notice rejected",()=>Reject("18:00\n开启了宝箱：普通 "+new string('x',200)));
        Check("first snapshot is baseline, never historical yield",()=>new ActivitySequence("synthetic").Push(Parse(a+"\n"+b)).Events.Length==0);
        Check("unchanged multi-frame prompt not counted again",()=>{var t=new ActivitySequence("synthetic");t.Push(Parse(a));return t.Push(Parse(a,1)).Events.Length==0;});
        Check("new same-name same-minute occurrences are preserved",()=>{var t=new ActivitySequence("synthetic");t.Push(Parse(b));var r=t.Push(Parse(b+"\n"+b+"\n"+b,1));return r.Events.Length==2&&r.Events.Select(x=>x.EventId).Distinct().Count()==2;});
        Check("rolling prefix removal does not recount retained notices",()=>{var t=new ActivitySequence("synthetic");t.Push(Parse(a+"\n"+b));return t.Push(Parse(b+"\n18:02\n掉落了一个宝箱",1)).Events.Length==1;});
        Check("no overlap marks gap and rebaselines, no backfill",()=>{var t=new ActivitySequence("synthetic");t.Push(Parse(a));var r=t.Push(Parse(b,1));return r.Events.Length==0&&r.HasGaps&&r.Status=="reference_gap";});
        Check("old observations do not rewind the cursor",()=>{var t=new ActivitySequence("synthetic");t.Push(Parse(a,2));return t.Push(Parse(a+"\n"+b,1)).Status=="old_observation"&&t.State().Previous!.Notices.Length==1;});
        Check("reference time regression is rejected",()=>{var t=new ActivitySequence("synthetic");t.Push(Parse(b));return t.Push(Parse(a,1)).Status=="reference_backwards";});
        Check("midnight rebaselines with a gap instead of remaining stuck all day",()=>{var t=new ActivitySequence("synthetic");var midnight=new DateTimeOffset(2026,11,8,23,59,59,DateTimeOffset.Now.Offset);t.Push(GameActivitySource.Parse("23:59\n掉落了一个宝箱",midnight,"day-test",labels));var next=GameActivitySource.Parse("00:00\n掉落了一个宝箱",midnight.AddSeconds(2),"day-test",labels);var reset=t.Push(next);return reset.Status=="reference_day_changed"&&reset.HasGaps&&t.Push(next with{ObservedUtc=midnight.AddSeconds(3)}).Status=="unchanged";});
        Check("missing reference time does not advance baseline",()=>{var t=new ActivitySequence("synthetic");t.Push(Parse(a));return t.Push(Parse("",1)).Status=="reference_time_unavailable"&&t.State().Previous!.Notices.Length==1;});
        Check("document rebuild cannot replay old history",()=>{var t=new ActivitySequence("synthetic");t.Push(Parse(a));var r=t.Push(Parse(a+"\n"+b,1,"rebuilt"));return r.Status=="document_changed"&&r.Events.Length==0&&r.HasGaps;});
        Check("restored cursor rejects repeated snapshot",()=>{var t=new ActivitySequence("synthetic");t.Push(Parse(a));t.Push(Parse(a+"\n"+b,1));var restored=new ActivitySequence("synthetic",t.State());return restored.Push(Parse(a+"\n"+b,2)).Events.Length==0;});
        Check("cursor arrays are detached from running sequence",()=>{var t=new ActivitySequence("synthetic");t.Push(Parse(a));t.State().Previous!.Notices[0]=new("18:00","bad","bad");return t.Push(Parse(a,1)).Events.Length==0;});
        Check("native notification has no fabricated probability",()=>{var t=new ActivitySequence("synthetic");t.Push(Parse(a));return t.Push(Parse(a+"\n"+b,1)).Events.Single().Confidence is null;});
        Check("sequence overflow state rejected",()=>{try{_=new ActivitySequence("synthetic",new(null,long.MaxValue,false));return false;}catch(InvalidDataException){return true;}});
        Check("empty initial document cannot certify its first populated notification as new",()=>{var t=new ActivitySequence("synthetic");t.Push(Parse(""));var r=t.Push(Parse(a,1));return r.Events.Length==0&&r.HasGaps&&r.Status=="reference_initial_population";});
        Check("timestamp-only initial frame never backfills delayed own history",()=>{var t=new ActivitySequence("synthetic");t.Push(Parse("18:02"));var r=t.Push(Parse(a+"\n"+b+"\n18:02",1));return r.Events.Length==0&&r.HasGaps;});
        Check("after initial history baseline only a subsequently appended notice is emitted",()=>{var t=new ActivitySequence("synthetic");t.Push(Parse("18:02"));t.Push(Parse(a+"\n"+b+"\n18:02",1));var r=t.Push(Parse(a+"\n"+b+"\n18:03\n开启了宝箱：优秀 后续弓",2));return r.Events.Length==1&&r.Events[0].Notice.Name=="后续弓"&&r.HasGaps;});
        Check("repeated delayed history is never emitted after initialization",()=>{var t=new ActivitySequence("synthetic");t.Push(Parse("18:02"));t.Push(Parse(b+"\n18:02",1));return t.Push(Parse(b+"\n18:02",2)).Events.Length==0;});
        Check("restored empty initial cursor still rejects delayed history",()=>{var t=new ActivitySequence("synthetic");t.Push(Parse("18:02"));var restored=new ActivitySequence("synthetic",t.State());var r=restored.Push(Parse(b+"\n18:02",1));return r.Events.Length==0&&r.HasGaps;});
        object? replay=null;
        if(beforePath is not null&&afterPath is not null)
        {
            ActivitySnapshot Load(string path,int step)
            {
                using var doc=JsonDocument.Parse(File.ReadAllText(path));var root=doc.RootElement;
                var text=string.Join('\n',root.GetProperty("HistoricalEvents").EnumerateArray().Select(e=>e.GetProperty("time").GetString()+"\n"+e.GetProperty("text").GetString()));
                return GameActivitySource.Parse(text,root.GetProperty("CapturedAtUtc").GetDateTimeOffset(),"recorded-whitelist-reference",labels);
            }
            var tracker=new ActivitySequence("recorded-reference-replay");tracker.Push(Load(beforePath,0));var batch=tracker.Push(Load(afterPath,1));
            replay=new{Source="RecordedWhitelistReferenceReplay_NotLiveRead",BeforeSha256=Hash(beforePath),AfterSha256=Hash(afterPath),batch.Status,batch.HasGaps,batch.Events,
                ChestNotifications=batch.Events.Count(e=>e.Notice.Kind=="ChestObtained"),OrdinaryEquipmentNotifications=batch.Events.Count(e=>e.Notice.Kind=="ChestOpened"&&e.Notice.Quality is not null),
                ColossusEquipmentNotifications=batch.Events.Count(e=>e.Notice.Kind=="ColossusReward"&&e.Notice.Quality is not null),FullAccuracyAccepted=false};
        }
        Program.Save(output,"activity-tests.json",new{SyntheticChecks=checks.Count,Passed=checks.Count-failed,Failed=failed,Checks=checks,RecordedReferenceReplay=replay,FullAccuracyAccepted=false});
        Console.WriteLine($"Activity source: {checks.Count-failed}/{checks.Count} synthetic checks passed; recorded replay is not live or full accuracy acceptance.");
        return failed==0?0:1;
    }
    static string Hash(string path)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    public static async Task<int> Watch(string exe,string output,int seconds)
    {
        Directory.CreateDirectory(output);var games=Native.Processes(exe).Where(p=>p.Exe?.Equals(exe,StringComparison.OrdinalIgnoreCase)==true&&p.StartUtc is not null).ToArray();
        if(games.Length!=1)throw new InvalidDataException("activity_requires_one_game");
        var game=games.Single();var labels=AppResources.ActivityQualityLabels;var tracker=new ActivitySequence(Guid.NewGuid().ToString("N"));
        Task<ActivitySnapshot>? pending=null;var rows=new List<object>();var errors=new List<object>();var watch=System.Diagnostics.Stopwatch.StartNew();
        while(watch.Elapsed.TotalSeconds<seconds)
        {
            pending??=Task.Run(()=>GameActivitySource.Read(game,labels));
            try
            {
                var snapshot=await pending.WaitAsync(TimeSpan.FromSeconds(3));pending=null;
                var batch=tracker.Push(snapshot);rows.Add(new{snapshot.ObservedUtc,snapshot.LatestMinute,VisibleNotices=snapshot.Notices.Length,batch.Status,batch.HasGaps,batch.Events});
            }
            catch(TimeoutException){errors.Add(new{ObservedUtc=DateTimeOffset.UtcNow,Code="activity_read_pending"});} // One pending read only; never spawn a worker per timeout.
            catch(Exception e)
            {
                pending=null;
                var reason=e is InvalidDataException&&System.Text.RegularExpressions.Regex.IsMatch(e.Message,@"^activity_[a-z_]+$")?e.Message:null;
                errors.Add(new{ObservedUtc=DateTimeOffset.UtcNow,Code="activity_read_unavailable",ErrorType=e.GetType().Name,Reason=reason});
            }
            Program.Save(output,"activity-watch.json",new{Source="GameUiAutomation_ReadOnly",Synthetic=false,Rows=rows,Errors=errors,ReferenceCompleteness="unknown",FullAccuracyAccepted=false,DefaultPlayerStatisticsModified=false});
            await Task.Delay(2000);
        }
        Console.WriteLine($"Passive activity source: {rows.Count} observations, {errors.Count} unavailable/pending reads; no game input.");
        return rows.Count>0?0:2;
    }
}
