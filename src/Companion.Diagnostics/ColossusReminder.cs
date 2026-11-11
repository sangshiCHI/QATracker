using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Companion.Diagnostics;

internal record ColossusNext(string Id,int Type,long OpensAt,long ServerAt,long ReceivedTick)
{internal string Key=>Id+":"+OpensAt;}
internal static class ColossusReminder
{
    internal const string PublicUrl="https://api.artxgames.com/questaway/public/colosso";
    static readonly Dictionary<int,string> Names=JsonSerializer.Deserialize<LabelData>(Assembly.GetExecutingAssembly().GetManifestResourceStream("colossus-labels.json")!)!.Names;
    internal record LabelData(Dictionary<int,string> Names);
    internal static string Name(int type)=>Names.GetValueOrDefault(type,"巨像");
    internal static ColossusNext? Parse(string json,long receivedTick)
    {
        using var doc=JsonDocument.Parse(json,new JsonDocumentOptions{MaxDepth=32});var root=doc.RootElement;
        if(!root.TryGetProperty("agora",out var clock)||!clock.TryGetInt64(out var server)||server<0)throw new InvalidDataException("invalid_colossus_clock");
        if(!root.TryGetProperty("proxima",out var next)||next.ValueKind==JsonValueKind.Null)return null;
        var id=next.GetProperty("id").GetString();var type=next.GetProperty("tipo").GetInt32();var opens=next.GetProperty("abre").GetInt64();
        if(id is null||!Regex.IsMatch(id,@"^\d{4}-\d{2}-\d{2}T\d{2}$")||!Names.ContainsKey(type)||opens<=server||opens-server>7L*86400000)throw new InvalidDataException("invalid_colossus_next");
        _=DateTimeOffset.FromUnixTimeMilliseconds(opens);return new(id,type,opens,server,receivedTick);
    }
    internal static async Task<ColossusNext?> Fetch(CancellationToken token)
    {
        using var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false}){Timeout=TimeSpan.FromSeconds(15)};
        using var request=new HttpRequestMessage(HttpMethod.Get,PublicUrl);request.Headers.UserAgent.ParseAdd("QATK/0.1");
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false);response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(2*1024*1024,token).ConfigureAwait(false);
        return Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false),Environment.TickCount64);
    }
    internal static double? RemainingSeconds(ColossusNext? next,long nowTick)
    {
        if(next is null||nowTick<next.ReceivedTick||nowTick-next.ReceivedTick>180000)return null;
        return (next.OpensAt-next.ServerAt-(nowTick-next.ReceivedTick))/1000.0;
    }
    internal static bool Due(ColossusNext? next,int minutes,long nowTick,IEnumerable<string> sent)=>next is not null&&minutes is (1 or 5)&&RemainingSeconds(next,nowTick) is double seconds&&seconds>0&&seconds<=minutes*60&&!sent.Contains(next.Key);
    internal static Window Toast(ColossusNext next,double seconds,bool test=false)
    {
        var window=new Window{Width=310,Height=116,Title="QATK · 巨像提醒",ShowInTaskbar=false,ShowActivated=false,Topmost=true,WindowStyle=WindowStyle.None,AllowsTransparency=true,Background=Brushes.Transparent,ResizeMode=ResizeMode.NoResize};
        var area=SystemParameters.WorkArea;window.Left=area.Right-window.Width-12;window.Top=area.Bottom-window.Height-12;
        var text=new StackPanel();text.Children.Add(new TextBlock{Text=Name(next.Type),FontSize=13,Foreground=WikiUi.Brush("#fff0ce")});
        text.Children.Add(new TextBlock{Text=(test?"测试提醒 · ":"")+"约 "+(seconds>=60?Math.Ceiling(seconds/60)+" 分钟":Math.Ceiling(seconds)+" 秒")+"后开启",FontSize=11,Foreground=WikiUi.Brush("#c5d89b"),Margin=new Thickness(0,5,0,0)});
        WikiUi.Frame(window,test?"提醒样式测试":"巨像提醒",text);
        window.SourceInitialized+=(_,_)=>
        {
            var source=(HwndSource)PresentationSource.FromVisual(window);Native.OverlayStyle(source.Handle,false);
            source.AddHook((nint hwnd,int message,nint wParam,nint lParam,ref bool handled)=>{if(message==0x21){handled=true;return 3;}return 0;});
        };
        var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(10)};timer.Tick+=(_,_)=>window.Close();window.Closed+=(_,_)=>timer.Stop();window.Loaded+=(_,_)=>timer.Start();return window;
    }
}
