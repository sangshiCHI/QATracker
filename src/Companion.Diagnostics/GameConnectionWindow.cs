using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;

namespace Companion.Diagnostics;

// Only ambiguous launches need a choice; a PID is never persisted across launches.
internal sealed class GameConnectionWindow:Window
{
    readonly ListBox instances=new(){Height=132,DisplayMemberPath="Label",Background=WikiUi.Brush("#382516"),Foreground=WikiUi.Brush("#fff1ce"),BorderBrush=WikiUi.Brush("#a8843d")};
    readonly TextBox path=new(){IsReadOnly=true,Margin=new Thickness(0,8,0,6),Background=WikiUi.Brush("#382516"),Foreground=WikiUi.Brush("#fff1ce"),BorderBrush=WikiUi.Brush("#a8843d")};
    readonly CheckBox confirmed=new(){Content="我确认这个存档属于选中的游戏实例",Foreground=WikiUi.Brush("#fff1ce"),Margin=new Thickness(0,8,0,8)};
    readonly TextBlock status=new(){TextWrapping=TextWrapping.Wrap,Foreground=WikiUi.Brush("#e0bd72")};
    readonly Button connect=WikiUi.Button("连接这一个");bool busy,closed;readonly CancellationTokenSource lifetime=new();
    sealed record Row(ProcessObservation Process,string Label);
    internal GameConnectionWindow(string save,Action<GameConnectionChoice> selected)
    {
        Title="QATK · 选择游戏";Width=490;Height=405;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        WindowStyle=WindowStyle.None;AllowsTransparency=true;ResizeMode=ResizeMode.NoResize;
        path.Text=save;
        Closed+=(_,_)=>{closed=true;lifetime.Cancel();lifetime.Dispose();};
        var body=new StackPanel{Margin=new Thickness(6)};
        body.Children.Add(new TextBlock{Text="暂不支持同时统计多个游戏，请选择一个实例连接。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)});
        body.Children.Add(instances);
        body.Children.Add(new TextBlock{Text="统计使用的存档（沙盒实例需选择其独立存档）",Margin=new Thickness(0,9,0,0)});
        var browse=WikiUi.Button("浏览文件");browse.Click+=(_,_)=>
        {
            var picker=new OpenFileDialog{Filter="游戏存档 (*.json)|*.json",CheckFileExists=true};
            if(picker.ShowDialog(this)==true){path.Text=picker.FileName;confirmed.IsChecked=false;}
        };
        var fileRow=new DockPanel();DockPanel.SetDock(browse,Dock.Right);fileRow.Children.Add(browse);fileRow.Children.Add(path);body.Children.Add(fileRow);
        body.Children.Add(confirmed);body.Children.Add(status);body.Children.Add(connect);
        void Enable(){connect.IsEnabled=!closed&&!busy&&instances.SelectedItem is Row&&confirmed.IsChecked==true&&File.Exists(path.Text);instances.IsEnabled=!busy;confirmed.IsEnabled=!busy;browse.IsEnabled=!busy;}
        instances.SelectionChanged+=(_,_)=>{confirmed.IsChecked=false;Enable();};confirmed.Checked+=(_,_)=>Enable();confirmed.Unchecked+=(_,_)=>Enable();
        connect.Click+=async (_,_)=>
        {
            if(busy||instances.SelectedItem is not Row row||confirmed.IsChecked!=true)return;
            var choice=new GameConnectionChoice(row.Process,path.Text);busy=true;Enable();
            try
            {
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await SaveSource.Read(choice.SavePath,timeout.Token);
                if(closed||timeout.IsCancellationRequested)return;
                if(!Native.Processes(row.Process.Exe!).Any(p=>CompanionRuntime.MatchesChoice(p,choice)))
                {status.Text="游戏实例已退出或存档已不可用，请重新选择。";return;}
                selected(choice);Close();
            }
            catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OperationCanceledException or InvalidOperationException or ArgumentException or KeyNotFoundException or OverflowException)
            {status.Text="无法读取该存档，尚未开始统计。请检查所选文件。";}
            finally{busy=false;Enable();}
        };
        WikiUi.Frame(this,"选择游戏",body);Enable();
    }
    internal void UpdateCandidates(ProcessObservation[] candidates)
    {
        var previous=(instances.SelectedItem as Row)?.Process;
        var rows=candidates.OrderBy(p=>p.Pid).Select(p=>new Row(p,$"{Path.GetFileName(p.Exe)} · PID {p.Pid} · 启动于 {p.StartUtc?.ToLocalTime():HH:mm:ss}")).ToArray();
        static object Identity(ProcessObservation p)=>(p.Pid,p.StartUtc,p.Exe?.ToUpperInvariant());
        if(instances.Items.Cast<Row>().Select(r=>Identity(r.Process)).SequenceEqual(rows.Select(r=>Identity(r.Process))))return;
        instances.ItemsSource=rows;instances.SelectedItem=rows.FirstOrDefault(r=>previous is not null&&Identity(r.Process).Equals(Identity(previous)));
        status.Text=rows.Length==0?"正在等待可用的游戏实例。":"";
    }
}
