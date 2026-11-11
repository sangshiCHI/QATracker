using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Companion.Diagnostics;

public record Box(int X, int Y, int Width, int Height);
public record GameWindow(int Pid, DateTime StartUtc, string Exe, string Hwnd, string ClassName,
    bool Visible, bool Minimized, bool Cloaked, uint Dpi, Box Window, Box Client, Box Monitor)
{
    public string SelectionLabel => $"{Hwnd} · {Client.Width}×{Client.Height} · DPI {Dpi}" + (Minimized?" · 最小化":"");
}
public record ProcessObservation(int Pid, DateTime? StartUtc, string? Exe, string MainWindow, string? Error);

internal static class Native
{
    internal delegate bool EnumProc(nint hwnd, nint data);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; public readonly Box Box => new(Left, Top, Right-Left, Bottom-Top); }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, nint data);
    [DllImport("kernel32.dll",SetLastError=true)] static extern SafeProcessHandle OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageName(SafeProcessHandle process,uint flags,StringBuilder name,ref int size);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll",SetLastError=true)] static extern bool ShowWindowAsync(nint hwnd,int command);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd,uint flags);
    [DllImport("user32.dll")] static extern nint GetWindow(nint hwnd,uint command);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] static extern nint GetWindowLongPtr(nint hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW",SetLastError=true)] static extern nint SetWindowLongPtr(nint hwnd,int index,nint value);
    [DllImport("user32.dll",SetLastError=true)] static extern bool SetWindowPos(nint hwnd,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowDisplayAffinity(nint hwnd,out uint affinity);
    [DllImport("user32.dll",SetLastError=true)] static extern bool GetLayeredWindowAttributes(nint hwnd,out uint colorKey,out byte alpha,out uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern nint WindowFromPoint(Point point);
    delegate void WinEventProc(nint hook,uint kind,nint hwnd,int objectId,int childId,uint thread,uint time);
    [DllImport("user32.dll",SetLastError=true)] static extern nint SetWinEventHook(uint first,uint last,nint module,WinEventProc callback,uint pid,uint thread,uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint hwnd, int attr, out int value, int size);

    public static ProcessObservation[] Processes(string exe)
    {
        var rows = new List<ProcessObservation>();
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        using (p)
        {
            try { rows.Add(new(p.Id, p.StartTime.ToUniversalTime(), ProcessExe(p.Id), $"0x{p.MainWindowHandle:X}", null)); }
            catch (Exception e) { rows.Add(new(p.Id, null, null, "unknown", e.GetType().Name)); }
        }
        return rows.ToArray();
    }
    static string ProcessExe(int pid)
    {
        // Metadata query only: no PROCESS_VM_READ/WRITE rights, no game-memory access.
        using var process=OpenProcess(0x1000,false,pid);
        if(process.IsInvalid)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var name=new StringBuilder(1024);var length=name.Capacity;
        if(QueryFullProcessImageName(process,0,name,ref length))return name.ToString();
        if(Marshal.GetLastWin32Error()!=122)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        name=new StringBuilder(32768);length=name.Capacity;
        if(!QueryFullProcessImageName(process,0,name,ref length))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return name.ToString();
    }

    public static GameWindow[] Windows(string exe)
    {
        var processes = Processes(exe).Where(p => p.Exe is not null &&
            Path.GetFullPath(p.Exe).Equals(Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)).ToDictionary(p=>p.Pid);
        var rows = new List<GameWindow>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!processes.TryGetValue((int)pid, out var process) || process.StartUtc is null) return true;
            if (!GetWindowRect(hwnd, out var rect) || !GetClientRect(hwnd, out var client)) return true;
            var origin = new Point(); ClientToScreen(hwnd, ref origin);
            var name = new StringBuilder(256); GetClassName(hwnd, name, name.Capacity);
            var monitor = new MonitorInfo {Size=Marshal.SizeOf<MonitorInfo>()}; GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref monitor);
            DwmGetWindowAttribute(hwnd, 14, out var cloaked, 4);
            rows.Add(new((int)pid, process.StartUtc.Value, process.Exe!, $"0x{hwnd:X}", name.ToString(),
                IsWindowVisible(hwnd), IsIconic(hwnd), cloaked != 0, GetDpiForWindow(hwnd), rect.Box,
                new(origin.X, origin.Y, client.Right, client.Bottom), monitor.Monitor.Box));
            return true;
        }, 0);
        return rows.ToArray();
    }

    public static nint Handle(GameWindow window) => (nint)Convert.ToInt64(window.Hwnd[2..], 16);
    internal static bool SameProcess(GameWindow window)=>Processes(window.Exe).Any(p=>p.Pid==window.Pid&&p.StartUtc==window.StartUtc&&string.Equals(p.Exe,window.Exe,StringComparison.OrdinalIgnoreCase));
    internal static void SetMinimized(GameWindow window,bool minimized)
    {
        if(!SameProcess(window)||Follow(window) is null)throw new InvalidOperationException("game_window_changed");
        if(IsIconic(Handle(window))==minimized)return;
        if(!ShowWindowAsync(Handle(window),minimized?6:9))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    public static GameWindow? Follow(GameWindow window)
    {
        var hwnd=Handle(window);GetWindowThreadProcessId(hwnd,out var pid);
        if(pid!=window.Pid||!IsWindow(hwnd)||!GetClientRect(hwnd,out var client)||!GetWindowRect(hwnd,out var rect))return null;
        var origin=new Point();ClientToScreen(hwnd,ref origin);
        return window with{Visible=IsWindowVisible(hwnd),Minimized=IsIconic(hwnd),Dpi=GetDpiForWindow(hwnd),Window=rect.Box,
            Client=new(origin.X,origin.Y,client.Right,client.Bottom)};
    }

    public static GameWindow? AutomaticWindow(string exe)
    {
        var all=Windows(exe).Where(w=>w.Visible&&!w.Cloaked&&w.Client.Width>0&&w.Client.Height>0).ToArray();
        // Process.MainWindowHandle can itself point at an owned popup.
        var handles=all.Select(Handle).ToHashSet();
        all=all.Where(w=>!handles.Contains(GetWindow(Handle(w),4))).ToArray();
        // Follow the game's owner, not its foreground log/settings popup.
        var foreground=$"0x{GetAncestor(GetForegroundWindow(),3):X}";
        var main=Processes(exe).FirstOrDefault(p=>p.Exe is not null&&p.Exe.Equals(exe,StringComparison.OrdinalIgnoreCase))?.MainWindow;
        return ChooseWindow(all,foreground,main);
    }
    internal static GameWindow? ChooseWindow(GameWindow[] all,string? foreground,string? main)=>
        all.FirstOrDefault(w=>w.Hwnd.Equals(foreground,StringComparison.OrdinalIgnoreCase))??
        all.FirstOrDefault(w=>w.Hwnd.Equals(main,StringComparison.OrdinalIgnoreCase))??
        all.OrderByDescending(w=>(long)w.Client.Width*w.Client.Height).FirstOrDefault();
    public static void OverlayStyle(nint ownHwnd,bool clickThrough)
    {
        GetWindowThreadProcessId(ownHwnd,out var pid);
        if(pid!=Environment.ProcessId)throw new InvalidOperationException("Only our own overlay window may be configured.");
        long style=(long)GetWindowLongPtr(ownHwnd,-20);
        style|=0x08000000L|0x80L; // NOACTIVATE + TOOLWINDOW; WPF supplies LAYERED for transparency.
        style=clickThrough?style|0x20L:style&~0x20L;
        SetWindowLongPtr(ownHwnd,-20,(nint)style);
    }
    public static void PositionOverlay(nint ownHwnd,int x,int y,int width,int height)
    {
        GetWindowThreadProcessId(ownHwnd,out var pid);
        if(pid!=Environment.ProcessId)throw new InvalidOperationException("Not our overlay.");
        if(!SetWindowPos(ownHwnd,-1,x,y,width,height,0x10|0x40))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    internal static bool OverlayIsAt(nint hwnd,int x,int y,int width,int height)=>
        GetWindowRect(hwnd,out var rect)&&rect.Left==x&&rect.Top==y&&rect.Right-rect.Left==width&&rect.Bottom-rect.Top==height;
    internal static Box? OverlayBounds(nint hwnd)=>GetWindowRect(hwnd,out var rect)?rect.Box:null;
    internal static (int X,int Y,bool Down) Pointer()
    {GetCursorPos(out var p);return(p.X,p.Y,(GetAsyncKeyState(1)&0x8000)!=0);}
    internal static (int X,int Y,int Buttons) PopupPointer()
    {
        GetCursorPos(out var p);
        return(p.X,p.Y,((GetAsyncKeyState(1)&0x8000)!=0?1:0)|((GetAsyncKeyState(2)&0x8000)!=0?2:0)|((GetAsyncKeyState(4)&0x8000)!=0?4:0));
    }
    internal static bool PointerTargets(GameWindow window,int x,int y)=>
        GetAncestor(WindowFromPoint(new Point{X=x,Y=y}),2)==Handle(window);
    internal static IDisposable? WatchLocation(GameWindow window,Action changed)
    {
        var target=Handle(window);GetWindowThreadProcessId(target,out var pid);if(pid!=window.Pid)return null;
        var watch=new LocationWatch(target,pid,changed);if(watch.Available)return watch;watch.Dispose();return null;
    }
    internal static IDisposable? WatchRestore(GameWindow window,Action changed)
    {
        if(!SameProcess(window))return null;
        var watch=new LocationWatch(Handle(window),(uint)window.Pid,changed,0x0017);if(watch.Available)return watch;watch.Dispose();return null;
    }
    sealed class LocationWatch:IDisposable
    {
        readonly WinEventProc callback;nint hook;
        public bool Available=>hook!=0;
        public LocationWatch(nint target,uint pid,Action changed,uint eventKind=0x800B)
        {
            callback=(_,_,hwnd,objectId,childId,_,_)=>{if(hook!=0&&hwnd==target&&objectId==0&&childId==0)changed();};
            // OUTOFCONTEXT, PID-filtered: callback stays here; no DLL enters the game.
            hook=SetWinEventHook(eventKind,eventKind,0,callback,pid,0,0);
        }
        public void Dispose(){if(hook!=0){UnhookWinEvent(hook);hook=0;}}
    }
    public static object CaptureRestrictions(GameWindow window)
    {
        var hwnd=Handle(window);var ok=GetWindowDisplayAffinity(hwnd,out var affinity)!=0;
        var layered=GetLayeredWindowAttributes(hwnd,out _,out var alpha,out var flags);
        return new{window.Hwnd,window.ClassName,Style=$"0x{(long)GetWindowLongPtr(hwnd,-20):X}",
            AffinityQuerySucceeded=ok,Affinity=ok?(uint?)affinity:null,
            LayeredAttributesAvailable=layered,LayeredAlpha=layered?(byte?)alpha:null,LayeredFlags=layered?(uint?)flags:null,
            Notice="只读查询；不会改变游戏样式、捕获策略或显示模式。"};
    }
    public static object OverlayFlags(nint ownHwnd)
    {
        GetWindowThreadProcessId(ownHwnd,out var pid);
        if(pid!=Environment.ProcessId)throw new InvalidOperationException("Not our overlay.");
        var style=(long)GetWindowLongPtr(ownHwnd,-20);
        return new{ClickThrough=(style&0x20)!=0,NoActivate=(style&0x08000000)!=0,Topmost=(style&8)!=0,IsForeground=GetForegroundWindow()==ownHwnd};
    }
}
