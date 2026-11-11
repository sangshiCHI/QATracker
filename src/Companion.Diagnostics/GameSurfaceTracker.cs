using System.Windows.Automation;

namespace Companion.Diagnostics;

// QuestAway's desktop host stays monitor-sized while its scene3d document moves inside it.
// Query geometry only on a worker; a stalled accessibility provider must never stall the overlay.
internal sealed class GameSurfaceTracker(Func<GameWindow,Box?>? read=null):IDisposable
{
    Task<Box?>? pending;string? pendingKey,currentKey;Box? bounds;DateTimeOffset observed,started;
    AutomationElement? document;string? documentKey;
    GameWindow? follower;NativeFollowerMotion motion=new();DateTimeOffset followerSearch;
    IDisposable? locationWatch;string? watchedKey;
    public bool FastMotion=>motion.Active;
    internal GameWindow? FollowerWindow(GameWindow game)
    {
        var candidate=Volatile.Read(ref follower);
        return candidate?.Pid==game.Pid&&candidate.StartUtc==game.StartUtc?Native.Follow(candidate):null;
    }
    internal GameHudReading? ReadHud(GameWindow game)
    {
        var doc=document;
        if(doc is null||documentKey!=$"{game.Pid}:{game.StartUtc.Ticks}:{game.Hwnd}")return null;
        try
        {
            var texts=doc.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text));
            if(texts.Count>600)return null;
            var rows=new List<(string Text,System.Windows.Rect Bounds)>();
            foreach(AutomationElement element in texts){var info=element.Current;if(info.Name.Length<=128)rows.Add((info.Name,info.BoundingRectangle));}
            var result=GameHudReading.Parse(rows);
            if(result is null&&doc.TryGetCurrentPattern(TextPattern.Pattern,out var pattern))
            {
                var lines=((TextPattern)pattern).DocumentRange.GetText(8192).Split('\n');
                result=GameHudReading.Parse(lines.Select((text,index)=>(text.Trim(),new System.Windows.Rect(0,index*10,200,10))));
            }
            return result;
        }
        catch(Exception e)when(e is ElementNotAvailableException or System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException or NotSupportedException){return null;}
    }
    internal Box? FollowerBounds(GameWindow? game)
    {
        var candidate=Volatile.Read(ref follower);
        if(game is null||candidate?.Pid!=game.Pid||candidate.StartUtc!=game.StartUtc)return null;
        return Native.Follow(candidate) is{Visible:true,Minimized:false} visible?visible.Window:null;
    }
    public Action? Updated{get;set;}
    public Box? Get(GameWindow window,bool request=true)
    {
        var key=$"{window.Pid}:{window.StartUtc.Ticks}:{window.Hwnd}";
        if(currentKey!=key){currentKey=key;bounds=null;motion=new();locationWatch?.Dispose();locationWatch=null;watchedKey=null;}
        if(pending?.IsCompleted==true)
        {
            if(pendingKey==key){bounds=pending.GetAwaiter().GetResult();observed=started;}
            pending=null;
        }
        if(request&&pending is null)
        {
            pendingKey=key;
            started=DateTimeOffset.UtcNow;
            pending=Task.Run(()=>{try{return (read??Read)(window);}catch(Exception e) when(e is ElementNotAvailableException or System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException){document=null;return null;}});
            _=pending.ContinueWith(_=>Updated?.Invoke(),TaskScheduler.Default);
        }
        if(DateTimeOffset.UtcNow-observed>=TimeSpan.FromSeconds(2)||bounds is null)return null;
        var candidate=Volatile.Read(ref follower);
        if(candidate?.Pid==window.Pid&&candidate.StartUtc==window.StartUtc&&Native.Follow(candidate) is {Visible:true,Minimized:false} proxy)
        {
            if(watchedKey!=candidate.Hwnd){locationWatch?.Dispose();locationWatch=Native.WatchLocation(candidate,()=>Updated?.Invoke());watchedKey=candidate.Hwnd;}
            var pointer=Native.Pointer();
            return motion.Resolve(bounds,proxy.Window,window.Monitor,pointer.X,pointer.Y,pointer.Down,
                Native.PointerTargets(window,pointer.X,pointer.Y),Environment.TickCount64);
        }
        locationWatch?.Dispose();locationWatch=null;watchedKey=null;motion=new();return bounds;
    }
    public void Dispose(){Updated=null;locationWatch?.Dispose();locationWatch=null;}
    Box? Read(GameWindow window)
    {
        if(Native.Follow(window) is not {Visible:true,Minimized:false})return null;
        var key=$"{window.Pid}:{window.StartUtc.Ticks}:{window.Hwnd}";
        if(document is null||documentKey!=key)
        {
            document=null;documentKey=key;
            var root=AutomationElement.FromHandle(Native.Handle(window));
            if(root.Current.ProcessId!=window.Pid)return null;
            var matches=root.FindAll(TreeScope.Descendants,new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Document),
                new PropertyCondition(AutomationElement.ClassNameProperty,"scene3d")));
            if(matches.Count!=1)return null;
            document=matches[0];
        }
        if((follower is null||Native.Follow(follower) is not {Visible:true,Minimized:false})&&DateTimeOffset.UtcNow-followerSearch>TimeSpan.FromSeconds(2))
        {
            followerSearch=DateTimeOffset.UtcNow;
            GameWindow? found=null;
            foreach(var candidate in Native.Windows(window.Exe).Where(w=>w.Pid==window.Pid&&w.StartUtc==window.StartUtc&&w.Hwnd!=window.Hwnd&&w.Visible&&w.ClassName=="Tauri Window"))
            {
                var owned=AutomationElement.FromHandle(Native.Handle(candidate));
                if(owned.Current.ProcessId!=window.Pid)continue;
                var docs=owned.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Document));
                if(docs.Count>64)continue;
                foreach(AutomationElement doc in docs)
                    if(doc.TryGetCurrentPattern(ValuePattern.Pattern,out var value)&&((ValuePattern)value).Current.Value=="http://tauri.localhost/log.html")
                    {if(found is not null&&found.Hwnd!=candidate.Hwnd){Volatile.Write(ref follower,null);return null;}found=candidate;break;}
            }
            Volatile.Write(ref follower,found);
        }
        var info=document.Current;var rect=info.BoundingRectangle;
        if(info.IsOffscreen||rect.IsEmpty||!double.IsFinite(rect.X)||!double.IsFinite(rect.Y)||
            !double.IsFinite(rect.Width)||!double.IsFinite(rect.Height)||rect.Width<80||rect.Height<40)return null;
        if(Native.Follow(window) is not {Visible:true,Minimized:false})return null;
        return new((int)Math.Round(rect.X),(int)Math.Round(rect.Y),(int)Math.Round(rect.Width),(int)Math.Round(rect.Height));
    }
}
