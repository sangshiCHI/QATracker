using System.Text.Json;

namespace Companion.Diagnostics;

internal static class SelfTests
{
    public static int Run()
    {
        var count=0;
        void Assert(bool condition,string label){if(!condition)throw new Exception(label);count++;Console.WriteLine("PASS "+label);}
        void Reject(Action action,string label){try{action();}catch(ArgumentException){Assert(true,label);return;}throw new Exception("应拒绝："+label);}
        Assert(Region.Parse("10,20,100,80")==new Region(10,20,100,80),"ROI parse");
        Reject(()=>Region.Parse("1,2,3"),"ROI missing field");
        Reject(()=>Region.Parse("-1,0,20,20"),"negative ROI");
        Reject(()=>new Region(0,0,400,200).Validate(300,200),"ROI outside frame");
        Reject(()=>new Region(int.MaxValue,0,2,2).Validate(400,200),"ROI integer overflow");
        Reject(()=>new Region(0,0,2000,2000).Validate(4000,4000),"no unbounded full frame OCR");
        Reject(()=>new Region(0,0,0,20).Validate(400,200),"empty ROI");
        var candidate=new CandidateEvent("test","synthetic",EventType.UnknownEvent,DateTimeOffset.UtcNow,"Synthetic",null,null,null,null);
        var json=JsonSerializer.Serialize(candidate,Program.Json);
        var decoded=JsonSerializer.Deserialize<CandidateEvent>(json,Program.Json)!;
        Assert(!decoded.IsConfirmed&&decoded.Confidence is null&&decoded.RuleConfirmationScore is null,"unknown confidence remains null, not confirmed");
        Assert(decoded.EventType==EventType.UnknownEvent,"event round trip");
        Assert(AppResources.Text("app").Contains(SoftwareUpdate.DisplayVersion),"window title uses actual assembly version");
        var assembly=typeof(Program).Assembly;
        Assert(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyFileVersionAttribute>(assembly)?.Version==assembly.GetName().Version!.ToString(),"assembly and file versions agree");
        Assert(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion==SoftwareUpdate.DisplayVersion,"product version agrees with displayed version");
        var start=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc);var box=new Box(0,0,425,234);
        var primary=new GameWindow(10,start,@"C:\fixture\QuestAway.exe","0x10","Fixture",true,false,false,96,box,box,box);
        var other=primary with{Pid=20,Hwnd="0x20",Client=new(0,0,1000,800)};
        var owner=new ProcessObservation(primary.Pid,start,primary.Exe,primary.Hwnd,null);
        Assert(Native.ChooseWindow([primary,other],other.Hwnd,other.Hwnd,owner)==primary,"foreign foreground and main window cannot steal session binding");
        Assert(Native.ChooseWindow([other],other.Hwnd,other.Hwnd,owner) is null,"missing bound window never falls back to other instance");
        Assert(Native.ChooseWindow([primary with{StartUtc=start.AddSeconds(1)}],primary.Hwnd,primary.Hwnd,owner) is null,"PID reuse with changed start time rejected");
        Assert(Native.ChooseWindow([primary with{Exe=@"C:\other\QuestAway.exe"}],primary.Hwnd,primary.Hwnd,owner) is null,"different executable cannot match bound game");
        Assert(Native.ChooseWindow([primary],primary.Hwnd,primary.Hwnd,owner with{StartUtc=null}) is null,"unknown process lifetime is not trusted");
        Assert(Native.ChooseWindow([primary],primary.Hwnd,primary.Hwnd,owner with{Error="metadata_unavailable"}) is null,"failed process observation is not trusted");
        var rebuilt=primary with{Hwnd="0x30"};
        Assert(Native.ChooseWindow([rebuilt,other],other.Hwnd,primary.Hwnd,owner)==rebuilt,"same process window rebuild accepted without crossing instances");
        Assert(Native.ChooseWindow([primary],null,null)==primary,"unscoped diagnostic caller remains compatible");
        Assert(CompanionRuntime.ConnectionCandidates([owner,owner with{Pid=20}],"").Length==2,"multi instance candidates are not silently guessed");
        Assert(CompanionRuntime.ConnectionCandidates([owner with{Error="unreadable"}],"").Length==0,"unreadable process never becomes selectable");
        Assert(CompanionRuntime.ConnectionCandidates([owner],@"C:\other\QuestAway.exe").Length==0,"configured executable still restricts instance discovery");
        Assert(StartupDiagnostics.IsPlayer([])&&!StartupDiagnostics.IsPlayer(["--self-test"])&&!StartupDiagnostics.IsPlayer(["--synthetic","--afk"]),"startup notices never block synthetic or developer commands");
        Assert(StartupDiagnostics.FailureText(new DllNotFoundException("private path")).Contains("完整解压"),"component startup failure explains safe recovery without extra runtime install");
        Assert(!StartupDiagnostics.FailureText(new Exception("private account and path")).Contains("private"),"startup notice never leaks raw exception message");
        foreach(var code in new[]{"multiple_game","game_window_unavailable","game_surface_missing","game_window_changed","game_minimize_not_confirmed"})
            Assert(!CompanionShell.OperationFailureText(new InvalidOperationException(code)).Contains("InvalidOperationException"),"actionable operation failure: "+code);
        var directory=Path.Combine(AppContext.BaseDirectory,"selftest-"+Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);var save=Path.Combine(directory,"fixture-save.json");File.WriteAllText(save,"{}");
            var choice=new GameConnectionChoice(owner,save);
            Assert(CompanionRuntime.MatchesChoice(owner,choice),"explicit process and source pair accepted");
            Assert(!CompanionRuntime.MatchesChoice(owner with{StartUtc=start.AddSeconds(1)},choice),"expired selected process refused");
            Assert(!CompanionRuntime.MatchesChoice(owner with{Pid=20},choice),"choice never adopts a different instance");
            Assert(!CompanionRuntime.MatchesChoice(owner,choice with{SavePath="relative.json"}),"relative source cannot be selected");
            StartupDiagnostics.Record(directory,new IOException("private path and account"));
            var record=File.ReadAllText(Path.Combine(directory,"startup-error.json"));
            Assert(!record.Contains("private")&&record.Contains("HResult"),"startup report contains fixed diagnostic fields only");
            File.Delete(save);Assert(!CompanionRuntime.MatchesChoice(owner,choice),"source disappearing before connection refused");
        }
        finally{File.Delete(Path.Combine(directory,"fixture-save.json"));File.Delete(Path.Combine(directory,"startup-error.json"));Directory.Delete(directory);}
        Console.WriteLine($"{count} checks passed. Synthetic only; not a real loot or dedup test.");return 0;
    }
}
