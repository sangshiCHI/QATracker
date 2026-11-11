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
        Console.WriteLine($"{count} checks passed. Synthetic only; not a real loot or dedup test.");return 0;
    }
}
