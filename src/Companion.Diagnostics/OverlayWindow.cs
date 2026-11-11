using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Companion.Diagnostics;

internal sealed class OverlayWindow:Window
{
    internal const int BaseWidth=280,BaseHeight=140;
    readonly CompanionSettings settings;readonly StackPanel panel=new();readonly DispatcherTimer follow=new(){Interval=TimeSpan.FromMilliseconds(200)};
    RuntimeView? view;bool enabled=true,dragging,closed;GameWindow? current;Box? surface;Border border;double appliedScale=-1;
    readonly GameSurfaceTracker surfaceTracker;
    WikiLauncherWindow? wikiLauncher,settingsLauncher,afkLauncher;
    readonly AfkPanel afkPanel=new();readonly List<GameWindow> minimizedByUs=new();GameWindow? afkTarget;Box? afkBounds;
    bool afkTransition,resumeMinimizing;long resumeDeadline;GameHudReading? hud;
    readonly List<IDisposable> restoreWatches=new();bool restoreQueued;
    bool afkHidden,resizing;
    internal bool IsAfk=>afkTarget is not null;
    internal Box? AfkBounds=>afkBounds;
    internal AfkUpdateState? CaptureUpdateState()
    {
        if(!IsAfk)return null;RememberAfkSize();
        return afkTarget is{} target&&afkBounds is{} bounds?new(target,minimizedByUs.ToArray(),bounds,afkHidden||!enabled):null;
    }
    internal void ResumeUpdateState(AfkUpdateState state)
    {
        if(IsAfk||closed||!SoftwareUpdate.ValidAfkState(state))throw new InvalidOperationException("invalid_afk_handoff");
        if(!Native.SameProcess(state.Target)||Native.Follow(state.Target) is null)throw new InvalidOperationException("afk_game_changed");
        afkTarget=state.Target;afkBounds=state.Bounds;afkHidden=state.Hidden;resumeMinimizing=true;resumeDeadline=Environment.TickCount64+5000;Hide();
        MinWidth=240;MinHeight=150;ResizeMode=ResizeMode.CanResize;
        CompositionTarget.Rendering-=OnRendering;follow.Interval=TimeSpan.FromSeconds(1);
        try
        {
            foreach(var window in state.Windows.Where(w=>Native.SameProcess(w)&&Native.Follow(w) is not null))
            {
                minimizedByUs.Add(window);
                if(Native.WatchRestore(window,()=>{if(!closed&&IsAfk)Dispatcher.BeginInvoke(new Action(FollowAfk));}) is{} watch)restoreWatches.Add(watch);
                Native.SetMinimized(window,true);
            }
            Draw();var own=new WindowInteropHelper(this).EnsureHandle();Native.OverlayStyle(own,false);
            AfkModeChanged?.Invoke(true);FollowAfk();
        }
        catch{RestoreAfk();throw;}
    }
    internal bool LowResourceDisplay=>IsAfk&&follow.Interval==TimeSpan.FromSeconds(1);
    internal event Action<bool>? AfkModeChanged;
    TextBlock? durationLabel;
    TextBlock? potionWarning;
    long potionPreviewUntil;
    internal void PreviewPotionWarning(int milliseconds=10000){potionPreviewUntil=Environment.TickCount64+Math.Clamp(milliseconds,50,10000);afkPanel.PreviewPotionWarning(milliseconds);UpdatePotionWarning();}
    internal string PopupSide{get;private set;}="right";
    internal string WikiPreferredSide=>IsAfk?"top":WikiPopupPlacement.SearchSide(PopupSide);
    public event Action? WikiRequested;
    public event Action? SettingsRequested;
    internal Box? GameExclusionBounds=>IsAfk?null:surface??current?.Client;
    internal Rect[] WikiExclusions=>new[]{GameExclusionBounds,surfaceTracker.FollowerBounds(current)}
        .Where(b=>b is{Width:>0,Height:>0}).Select(b=>new Rect(b!.X,b.Y,b.Width,b.Height)).ToArray();
    public OverlayWindow(CompanionSettings settings):this(settings,null){}
    internal OverlayWindow(CompanionSettings settings,GameSurfaceTracker? surfaceTracker)
    {
        this.surfaceTracker=surfaceTracker??new();
        var queued=0;
        this.surfaceTracker.Updated=()=>{if(!Dispatcher.HasShutdownStarted&&Interlocked.Exchange(ref queued,1)==0)Dispatcher.BeginInvoke(DispatcherPriority.Render,new Action(()=>{Interlocked.Exchange(ref queued,0);Follow(false);}));};
        this.settings=settings;Title=AppResources.Text("app");WindowStyle=WindowStyle.None;AllowsTransparency=true;
        Background=Brushes.Transparent;ShowInTaskbar=false;ShowActivated=false;Topmost=true;Focusable=false;ResizeMode=ResizeMode.NoResize;
        border=new Border{Background=Brush("#2c1c12"),BorderBrush=Brush("#a8843d"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(5),Padding=new Thickness(10,8,10,8),Child=panel};Content=border;
        SourceInitialized+=(_,_)=>
        {
            var source=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)??throw new InvalidOperationException("overlay_source_missing");source.AddHook(Hook);
            Native.OverlayStyle(source.Handle,settings.ClickThrough);
        };
        MouseLeftButtonDown+=(_,e)=>{if((IsAfk||!settings.ClickThrough)&&e.ChangedButton==MouseButton.Left){dragging=true;try{DragMove();if(IsAfk){var p=PointToScreen(new Point());MoveAfkTo((int)Math.Round(p.X),(int)Math.Round(p.Y));}else RememberPosition();}catch(InvalidOperationException){ }finally{dragging=false;}}};
        LocationChanged+=(_,_)=>{if(dragging&&IsAfk&&!resizing){var p=PointToScreen(new Point());MoveAfkTo((int)Math.Round(p.X),(int)Math.Round(p.Y));}};
        afkPanel.MinimizeRequested+=MinimizeAfk;
        afkPanel.RestoreRequested+=async()=>await ToggleAfk();afkPanel.WikiRequested+=()=>WikiRequested?.Invoke();afkPanel.SettingsRequested+=()=>SettingsRequested?.Invoke();
        SizeChanged+=(_,_)=>{if(IsAfk&&dragging&&resizing)RememberAfkSize();};
        CompositionTarget.Rendering+=OnRendering;
        IsVisibleChanged+=(_,_)=>{if(!IsVisible){wikiLauncher?.Hide();settingsLauncher?.Hide();afkLauncher?.Hide();}};
        Closed+=(_,_)=>{closed=true;RestoreAfk();wikiLauncher?.Close();settingsLauncher?.Close();afkLauncher?.Close();follow.Stop();CompositionTarget.Rendering-=OnRendering;this.surfaceTracker.Dispose();};follow.Tick+=(_,_)=>{Follow();UpdateClock();if(IsAfk)afkPanel.Tick();};follow.Start();
    }
    public void Update(RuntimeView next){view=next;Draw();Follow();}
    public void ToggleVisible(){if(IsAfk&&afkHidden){ShowAfk();return;}enabled=!enabled;Follow();}
    internal void MinimizeAfk(){if(IsAfk){afkHidden=true;Hide();}}
    internal void ShowAfk(){if(IsAfk){enabled=true;afkHidden=false;Follow();}}
    public void ApplySettings()
    {
        Opacity=settings.Opacity;
        afkPanel.LayoutTransform=new ScaleTransform(settings.Scale,settings.Scale);
        var handle=new WindowInteropHelper(this).Handle;if(handle!=0)Native.OverlayStyle(handle,settings.ClickThrough&&!IsAfk);
        Draw();Follow();
    }
    static SolidColorBrush Brush(string hex)=>new((Color)ColorConverter.ConvertFromString(hex));
    static TextBlock Text(string value,double size=12,string color="#f0e2c8")=>new(){Text=value,FontSize=size,Foreground=Brush(color),TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(0,2,0,2)};
    void Draw()
    {
        if(IsAfk){border.Child=afkPanel;if(view is not null)afkPanel.Update(view,hud,settings.PotionReminderEnabled);return;}
        border.Child=panel;
        panel.Children.Clear();
        if(view is null)return;
        var state=view.State;var known=state.Previous is not null;
        var equipmentKnown=StatisticsEngine.IsTrackedScope(state.EquipmentScope)?state.EquipmentAvailable&&!state.HasGaps:known;
        var equipmentCount=state.Equipment.Values.Sum();
        var equipmentDisplay=equipmentKnown?equipmentCount.ToString():equipmentCount>0?"≥"+equipmentCount:AppResources.Text("unknown");
        var total=Text((view.Synthetic?"[测试] ":"")+"本次装备产出："+equipmentDisplay,13);
        total.Margin=new Thickness(0,0,107,7);total.FontWeight=FontWeights.SemiBold;
        total.ToolTip="原生累计产出，包含开箱／转化／制造；"+AppResources.Text(view.Status)+(equipmentKnown?"":"；未知或不完整时保留下界");
        panel.Children.Add(total);
        potionWarning=Text("当前没有生效的药水",11.5,"#ff6258");potionWarning.FontWeight=FontWeights.Bold;panel.Children.Add(potionWarning);UpdatePotionWarning();
        var quality=new Grid();for(var i=0;i<3;i++)quality.ColumnDefinitions.Add(new());
        for(var i=0;i<3;i++)quality.RowDefinitions.Add(new());
        for(var i=0;i<SaveSource.Qualities.Length;i++)
        {
            var q=SaveSource.Qualities[i];var count=equipmentKnown?state.Equipment[q].ToString():state.Equipment[q]>0?"≥"+state.Equipment[q]:"—";
            var cell=Text(AppResources.Text(q)+" "+count,11.5,AppResources.Color(q));Grid.SetRow(cell,i/3);Grid.SetColumn(cell,i%3);quality.Children.Add(cell);
        }
        panel.Children.Add(quality);
        panel.Children.Add(Text("最近保存："+SavedClock(state.Previous?.SavedAt),10.5,"#b89a5e"));
        var duration=Text("",10.5,"#b89a5e");durationLabel=duration;UpdateClock();
        duration.Margin=new Thickness(0,2,5,2);duration.ToolTip="会话经过时长，包含暂停和等待；不是有效采样时长";
        var footer=new DockPanel{Margin=new Thickness(0,0,28,0)};var version=Text("v"+SoftwareUpdate.DisplayVersion,9.5,"#b89a5e");version.VerticalAlignment=VerticalAlignment.Bottom;DockPanel.SetDock(version,Dock.Right);footer.Children.Add(version);footer.Children.Add(duration);panel.Children.Add(footer);
    }
    void UpdatePotionWarning(){if(potionWarning is not null){var preview=Environment.TickCount64<potionPreviewUntil;potionWarning.Text=(preview?"[测试] ":"")+"当前没有生效的药水";potionWarning.Visibility=preview||settings.PotionReminderEnabled&&view is not null&&view.State.EquipmentAvailable&&AfkPanel.NoActivePotion(view.Afk,DateTimeOffset.UtcNow)?Visibility.Visible:Visibility.Collapsed;}}
    void UpdateClock(){if(durationLabel is not null&&view is not null)durationLabel.Text="本次记录："+SessionDuration(view.StartTime,DateTimeOffset.UtcNow);UpdatePotionWarning();}
    internal static string SessionDuration(DateTimeOffset start,DateTimeOffset now)
    {
        var elapsed=now>start?now-start:TimeSpan.Zero;
        return (elapsed.Days>0?$"{elapsed.Days}天：":"")+$"{elapsed.Hours}小时：{elapsed.Minutes:00}分：{elapsed.Seconds:00}秒";
    }
    internal static string SavedClock(long? value)
    {
        if(value is null)return "—";
        try{return DateTimeOffset.FromUnixTimeMilliseconds(value.Value).ToLocalTime().ToString("HH:mm:ss");}
        catch(ArgumentOutOfRangeException){return "—";}
    }
    void OnRendering(object? sender,EventArgs e){if(IsVisible&&!IsAfk)Follow();}
    void Follow(bool requestSurface=true)
    {
        if(dragging||closed)return;
        if(IsAfk){FollowAfk();return;}
        current=view?.Window is null?null:Native.Follow(view.Window);
        if(!enabled||current is null||current.Minimized||!current.Visible||current.Client.Width<80||current.Client.Height<80){if(IsVisible)Hide();return;}
        surface=current.ClassName=="Tauri Window"?surfaceTracker.Get(current,requestSurface):null;
        if(current.ClassName=="Tauri Window"&&surface is null){if(IsVisible)Hide();return;}
        var dpi=current.Dpi>0?current.Dpi/96.0:1;
        var available=surface is null?current.Client:current.Monitor;
        var scale=Math.Min(settings.Scale,Math.Min((available.Width-16)/(BaseWidth*dpi),(available.Height-16)/(BaseHeight*dpi)));
        if(scale<.4){if(IsVisible)Hide();return;}
        if(Math.Abs(appliedScale-scale)>.001){border.LayoutTransform=new ScaleTransform(scale,scale);appliedScale=scale;}
        Width=BaseWidth*scale;Height=BaseHeight*scale;
        Opacity=settings.Opacity;
        if(!IsVisible)Show();
        var width=(int)Math.Round(Width*dpi);var height=(int)Math.Round(Height*dpi);
        var x=current.Client.X+Math.Clamp(current.Client.Width-width-settings.OffsetX,0,Math.Max(0,current.Client.Width-width));
        var y=current.Client.Y+Math.Clamp(settings.OffsetY,0,Math.Max(0,current.Client.Height-height));
        if(surface is not null)
        {
            var screen=System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(surface.X+surface.Width/2,surface.Y+surface.Height/2)).WorkingArea;
            var follower=surfaceTracker.FollowerBounds(current);
            var extra=follower is null?Array.Empty<Rect>():new[]{new Rect(follower.X,follower.Y,follower.Width,follower.Height)};
            var place=WikiPopupPlacement.Dock(new(surface.X,surface.Y,surface.Width,surface.Height),new(screen.X,screen.Y,screen.Width,screen.Height),new(width,height),settings.DockSide,extra,Math.Round(8*dpi),alignBottom:true);
            if(place is not Rect position){Hide();return;}
            x=(int)position.X;y=(int)position.Y;
            PopupSide=position.Left>=surface.X+surface.Width?"right":position.Right<=surface.X?"left":position.Bottom<=surface.Y?"top":"bottom";
        }
        // WPF/DPI/layout may change actual HWND bounds after a placement request.
        try
        {
            var handle=new WindowInteropHelper(this).Handle;if(!Native.OverlayIsAt(handle,x,y,width,height))Native.PositionOverlay(handle,x,y,width,height);
            if(wikiLauncher is null){wikiLauncher=new WikiLauncherWindow{Owner=this};wikiLauncher.Requested+=()=>WikiRequested?.Invoke();}
            wikiLauncher.Follow(x,y,width,scale*dpi,settings.Opacity);
            if(settingsLauncher is null){settingsLauncher=new WikiLauncherWindow(settingsButton:true){Owner=this};settingsLauncher.Requested+=()=>SettingsRequested?.Invoke();}
            settingsLauncher.Follow(x,y,width,scale*dpi,settings.Opacity,height);
            if(afkLauncher is null){afkLauncher=new WikiLauncherWindow(afkButton:true){Owner=this};afkLauncher.Requested+=async()=>{try{await ToggleAfk();}catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){MessageBox.Show(CompanionShell.OperationFailureText(e),"QATK "+SoftwareUpdate.DisplayVersion);}};}
            afkLauncher.SetAfk(false);afkLauncher.Follow(x,y,width,scale*dpi,settings.Opacity,height);
        }
        catch(System.ComponentModel.Win32Exception){Hide();}
    }
    internal async Task ToggleAfk()
    {
        if(afkTransition||closed)return;
        if(IsAfk){RestoreAfk();Draw();Follow();return;}
        var target=current is null?null:Native.Follow(current);var bounds=surface??target?.Client;
        if(target is null)throw new InvalidOperationException(view?.Status=="multiple_game"?"multiple_game":"game_window_unavailable");
        if(target is null||target.ClassName=="Tauri Window"&&surface is null||bounds is not{Width:>=80,Height:>=40}||target.Minimized||!Native.SameProcess(target))throw new InvalidOperationException("game_surface_missing");
        afkTransition=true;
        try
        {
        afkTarget=target;afkBounds=bounds;hud=null;
        afkHidden=false;MinWidth=240;MinHeight=150;ResizeMode=ResizeMode.CanResize;
        wikiLauncher?.Hide();settingsLauncher?.Hide();afkLauncher?.Hide();
        CompositionTarget.Rendering-=OnRendering;follow.Interval=TimeSpan.FromSeconds(1);AfkModeChanged?.Invoke(true);
        minimizedByUs.Clear();minimizedByUs.Add(target);
        if(surfaceTracker.FollowerWindow(target) is {Visible:true,Minimized:false} follower&&follower.Hwnd!=target.Hwnd)minimizedByUs.Add(follower);
            foreach(var window in minimizedByUs)
                if(Native.WatchRestore(window,()=>{if(restoreQueued||closed||!IsAfk)return;restoreQueued=true;Dispatcher.BeginInvoke(DispatcherPriority.Render,new Action(()=>{restoreQueued=false;if(!closed&&IsAfk)FollowAfk();}));}) is{} watch)restoreWatches.Add(watch);
            afkPanel.LayoutTransform=new ScaleTransform(settings.Scale,settings.Scale);Draw();var own=new WindowInteropHelper(this).Handle;if(own!=0)Native.OverlayStyle(own,false);foreach(var window in minimizedByUs)Native.SetMinimized(window,true);
            for(var i=0;i<15&&!Native.IsIconic(Native.Handle(target));i++)await Task.Delay(100);
            if(closed)return;
            if(!Native.IsIconic(Native.Handle(target)))throw new InvalidOperationException("game_minimize_not_confirmed");
            Follow();
        }
        catch{RestoreAfk();if(!closed){Draw();Follow();}throw;}
        finally{afkTransition=false;}
    }
    internal void RestoreAfk()
    {
        if(!IsAfk)return;
        foreach(var watch in restoreWatches)watch.Dispose();restoreWatches.Clear();
        foreach(var window in minimizedByUs.AsEnumerable().Reverse())
        {try{if(Native.SameProcess(window)&&Native.Follow(window) is{Minimized:true})Native.SetMinimized(window,false);}catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){ }}
        afkTarget=null;afkBounds=null;minimizedByUs.Clear();hud=null;afkLauncher?.SetAfk(false);
        afkHidden=false;resizing=false;resumeMinimizing=false;MinWidth=0;MinHeight=0;ResizeMode=ResizeMode.NoResize;
        follow.Interval=TimeSpan.FromMilliseconds(200);if(!closed){CompositionTarget.Rendering-=OnRendering;CompositionTarget.Rendering+=OnRendering;}AfkModeChanged?.Invoke(false);
        if(!closed){var own=new WindowInteropHelper(this).Handle;if(own!=0)Native.OverlayStyle(own,settings.ClickThrough);}
        if(!closed&&IsVisible)Hide();
    }
    void FollowAfk()
    {
        if(!enabled){if(IsVisible)Hide();return;}
        var target=afkTarget!;var b=afkBounds!;var live=Native.Follow(target);
        if(live is null){RestoreAfk();if(!closed)Draw();return;}
        // ShowWindowAsync may not have minimized yet; do not mistake that transition for a user restore.
        if(resumeMinimizing)
        {
            if(live.Minimized)resumeMinimizing=false;
            else{if(Environment.TickCount64>=resumeDeadline){RestoreAfk();Draw();}return;}
        }
        if(afkHidden&&live.Minimized)return;
        if(!live.Minimized)afkHidden=false;
        if(!afkTransition)
        {
            try{foreach(var window in minimizedByUs)if(Native.Follow(window) is{Minimized:false})Native.SetMinimized(window,true);}
            catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){RestoreAfk();Draw();return;}
        }
        // No UIA / capture / per-frame work while the real game is minimized. Saved counters drive this view.
        var dpi=target.Dpi>0?target.Dpi/96.0:1;border.LayoutTransform=Transform.Identity;appliedScale=-1;
        Width=b.Width/dpi;Height=b.Height/dpi;Opacity=settings.Opacity;if(!IsVisible)Show();
        var handle=new WindowInteropHelper(this).Handle;if(!Native.OverlayIsAt(handle,b.X,b.Y,b.Width,b.Height))Native.PositionOverlay(handle,b.X,b.Y,b.Width,b.Height);
        // AFK is interactive: native WPF buttons belong inside the resizable panel, not separate HWNDs.
        wikiLauncher?.Hide();settingsLauncher?.Hide();afkLauncher?.Hide();
    }
    internal void MoveAfkTo(int x,int y)
    {
        if(afkBounds is not{} b)return;
        var screen=System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x+b.Width/2,y+b.Height/2)).WorkingArea;
        afkBounds=b with{X=Math.Clamp(x,screen.Left,Math.Max(screen.Left,screen.Right-b.Width)),Y=Math.Clamp(y,screen.Top,Math.Max(screen.Top,screen.Bottom-b.Height))};
        FollowAfk();
    }
    internal void RememberAfkSize()
    {
        if(IsAfk&&Native.OverlayBounds(new WindowInteropHelper(this).Handle) is{Width:>=80,Height:>=40} box)afkBounds=box;
    }
    void RememberPosition()
    {
        if(current is null)return;var origin=PointToScreen(new Point(0,0));var dpi=current.Dpi/96.0;
        settings.OffsetX=surface is null?(int)(current.Client.X+current.Client.Width-origin.X-ActualWidth*dpi):(int)(origin.X-surface.X-surface.Width);
        settings.OffsetY=(int)(origin.Y-(surface?.Y??current.Client.Y));settings.Save();
    }
    nint Hook(nint hwnd,int msg,nint wParam,nint lParam,ref bool handled)
    {
        if(IsAfk&&msg==0x231){dragging=true;resizing=false;}
        if(IsAfk&&msg==0x214)resizing=true;
        if(IsAfk&&msg==0x232)
        {
            RememberAfkSize();
            dragging=false;resizing=false;FollowAfk();
        }
        if(IsAfk&&msg==0x84&&Native.OverlayBounds(hwnd) is{} bounds)
        {
            var x=(short)((long)lParam&0xffff);var y=(short)(((long)lParam>>16)&0xffff);
            var left=x<bounds.X+7;var right=x>=bounds.X+bounds.Width-7;var top=y<bounds.Y+7;var bottom=y>=bounds.Y+bounds.Height-7;
            var hit=top?(left?13:right?14:12):bottom?(left?16:right?17:15):left?10:right?11:0;
            if(hit!=0){handled=true;return hit;}
        }
        if(msg==0x21){handled=true;return 3;} // MA_NOACTIVATE; do not steal game focus.
        if(msg==0x84&&settings.ClickThrough&&!IsAfk){handled=true;return -1;}
        return 0;
    }
}
