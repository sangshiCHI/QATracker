using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Companion.Diagnostics;

// A tiny owned hit target leaves the statistics HWND's original whole-window click-through untouched.
internal sealed class WikiLauncherWindow:Window
{
    readonly Button button;
    readonly bool settingsButton,afkButton;
    internal const int WikiWidth=48,ButtonHeight=22;
    public event Action? Requested;
    public WikiLauncherWindow(bool settingsButton=false,bool afkButton=false)
    {
        this.settingsButton=settingsButton;this.afkButton=afkButton;
        Title=settingsButton?"QATK · 设置入口":afkButton?"QATK · 挂机入口":"QATK · wiki入口";WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;
        ShowInTaskbar=false;ShowActivated=false;Focusable=false;Topmost=true;ResizeMode=ResizeMode.NoResize;Width=settingsButton?22:WikiWidth;Height=ButtonHeight;
        button=WikiUi.Button(settingsButton?"⚙":afkButton?"挂机":"wiki");button.Focusable=false;button.Padding=new Thickness(2,0,2,0);button.FontSize=11;
        button.ToolTip=settingsButton?"设置":afkButton?"最小化游戏，原位显示挂机统计":"本地 wiki 搜索";button.Click+=(_,_)=>Requested?.Invoke();Content=button;
        SourceInitialized+=(_,_)=>Native.OverlayStyle(new WindowInteropHelper(this).Handle,false);
    }
    internal void InvokeForTest()=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    internal void SetAfk(bool active){if(afkButton){button.Content=active?"恢复":"挂机";button.ToolTip=active?"恢复游戏窗口":"最小化游戏，原位显示挂机统计";}}
    public void Follow(int overlayX,int overlayY,int overlayWidth,double physicalScale,double opacity,int? overlayHeight=null)
    {
        Opacity=opacity;if(!IsVisible)Show();int w=Math.Max(16,(int)Math.Round((settingsButton?22:WikiWidth)*physicalScale)),h=Math.Max(14,(int)Math.Round(ButtonHeight*physicalScale));
        var x=overlayX+overlayWidth-w-(int)Math.Round(9*physicalScale);
        if(afkButton)x-=(int)Math.Round((WikiWidth+6)*physicalScale);
        var y=settingsButton?overlayY+(overlayHeight??(int)Math.Round(OverlayWindow.BaseHeight*physicalScale))-h-(int)Math.Round(7*physicalScale):overlayY+(int)Math.Round(7*physicalScale);
        var handle=new WindowInteropHelper(this).Handle;if(!Native.OverlayIsAt(handle,x,y,w,h))Native.PositionOverlay(handle,x,y,w,h);
    }
}
