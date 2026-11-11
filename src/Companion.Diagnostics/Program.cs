using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Companion.Diagnostics;

internal static class Program
{
    internal static readonly JsonSerializerOptions Json=new(){WriteIndented=true, IncludeFields=true,Encoder=JavaScriptEncoder.Create(UnicodeRanges.All)};
    [STAThread]
    public static int Main(string[] args)
    {
        var player=StartupDiagnostics.IsPlayer(args);
        try
        {
            if(player&&!OperatingSystem.IsWindowsVersionAtLeast(10,0,19041))
            {StartupDiagnostics.Notify("此版本需要 Windows 10（系统内部版本 19041 或以上）或 Windows 11 的64位环境。未修改统计记录。");return 1;}
            if(Option(args,"--apply-update") is string updatePlan)return SoftwareUpdate.Apply(updatePlan);
            if(args.Contains("--self-test")) return SelfTests.Run();
            var output=Option(args,"--output") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QuestAwayCompanion","Diagnostics",DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            // Do not enumerate other processes before normal startup's guarded runtime loop.
            var exe=Option(args,"--exe")??"";
            if(exe.Length==0&&args.Any(a=>a is "--probe" or "--window-auto" or "--watch-activity" or "--capture" or "--diagnostics" or "--ui-smoke" or "--probe-afk-fields"))
                exe=Native.Processes("QuestAway.exe").FirstOrDefault(p=>p.Exe is not null)?.Exe??"";
            if(args.Contains("--self-test-updates"))return UpdateVerification.Run(output).GetAwaiter().GetResult();
            if(args.Contains("--self-test-online"))return OnlinePresenceVerification.Run(output).GetAwaiter().GetResult();
            if(args.Contains("--self-test-afk"))return AfkVerification.Run(new Application(),output);
            if(args.Contains("--benchmark-afk-read"))return AfkVerification.ReadPerformance(output).GetAwaiter().GetResult();
            if(args.Contains("--probe-afk-fields"))return AfkVerification.Probe(output,exe).GetAwaiter().GetResult();
            if(args.Contains("--probe-update")){var release=SoftwareUpdate.Check(CancellationToken.None).GetAwaiter().GetResult();Save(output,"update-probe.json",new{Repository=SoftwareUpdate.Repository,Available=release is not null,Version=release?.Version,Notes=release?.Notes});return 0;}
            if(args.Contains("--self-test-wiki"))return WikiSearch.Verify(output);
            if(args.Contains("--self-test-wiki-ui"))
            {
                var wikiApp=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};var wiki=new WikiWindow(autoDismiss:false);
                wiki.Loaded+=async (_,_)=>await wiki.VerifyUi(output,wikiApp);return wikiApp.Run(wiki);
            }
            if(args.Contains("--self-test-chest-only"))return ChestOnlyVerification.Run(output,Option(args,"--evidence-root"));
            if(args.Contains("--self-test-core-counts"))return CoreCountVerification.Run(output);
            if(args.Contains("--self-test-production-counts"))return ProductionCountVerification.Run(output,Option(args,"--replay"));
            if(args.Contains("--self-test-activity"))return ActivityVerification.Tests(output,Option(args,"--activity-before"),Option(args,"--activity-after"));
            if(args.Contains("--watch-activity"))return ActivityVerification.Watch(exe,output,int.Parse(Option(args,"--seconds")??"120")).GetAwaiter().GetResult();
            if(Option(args,"--inspect-store") is string database){StoreInspection.Run(database,output);return 0;}
            if(args.Contains("--watch-save"))return LiveVerification.Run(output,int.Parse(Option(args,"--seconds")??"120"),CancellationToken.None).GetAwaiter().GetResult();
            if(args.Contains("--window-auto"))
            {
                var selected=Native.AutomaticWindow(exe);
                Save(output,"automatic-window.json",new{Selected=selected,Restrictions=selected is null?null:Native.CaptureRestrictions(selected)});
                return selected is null?2:0;
            }
            if(args.Contains("--probe"))
            {
                Directory.CreateDirectory(output);
                var observations=new List<object>();
                var me=Process.GetCurrentProcess(); var cpu=me.TotalProcessorTime; var timer=Stopwatch.StartNew();
                for(var i=0;i<5;i++)
                {
                    observations.Add(new{AtUtc=DateTimeOffset.UtcNow,Processes=Native.Processes(string.IsNullOrEmpty(exe)?"QuestAway.exe":exe),
                        Windows=string.IsNullOrEmpty(exe)?[]:Native.Windows(exe)});
                    if(i<4) Thread.Sleep(500);
                }
                Save(output,"probe.json",new{Os=Environment.OSVersion.VersionString,Runtime=Environment.Version.ToString(),
                    Is64Bit=Environment.Is64BitProcess,SessionId=me.SessionId,Executable=exe,
                    Wgc=ProbeCapability(()=>CaptureProbe.Supported()),Ocr=ProbeCapability(()=>CaptureProbe.Languages()),Observations=observations,
                    SampleMs=timer.Elapsed.TotalMilliseconds,CpuMs=(me.TotalProcessorTime-cpu).TotalMilliseconds,
                    WorkingSetBytes=me.WorkingSet64,Notice="只读观察；未验证窗口移动、最小化、退出或掉落。"});
                Console.WriteLine(Path.Combine(output,"probe.json")); return 0;
            }
            if(args.Contains("--capture"))
            {
                if(string.IsNullOrWhiteSpace(exe)||!Path.IsPathFullyQualified(exe)) throw new ArgumentException("--exe 必须是游戏 EXE 完整路径。");
                var roi=Region.Parse(Option(args,"--roi")??throw new ArgumentException("必须显式提供 --roi x,y,width,height"));
                var window=SelectWindow(exe,Option(args,"--hwnd"));
                // WGC session is apartment-bound; preserve the STA dispatcher across awaits even in CLI mode.
                var captureApp=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
                var exitCode=1;
                captureApp.Startup+=async (_,_)=>
                {
                    try
                    {
                        var result=await CaptureProbe.Run(Native.Handle(window),roi,output,Option(args,"--language")??"zh-Hans",CancellationToken.None);
                        Save(output,"capture.json",new{AtUtc=DateTimeOffset.UtcNow,Target=window,Result=result});
                        Console.WriteLine(Path.Combine(output,"capture.json"));exitCode=result.Status=="UnconfirmedOcr"?0:2;
                    }
                    catch(Exception e){Save(output,"capture-error.json",Error(e));Console.Error.WriteLine(JsonSerializer.Serialize(Error(e),Json));}
                    finally{captureApp.Shutdown();}
                };
                captureApp.Run();return exitCode;
            }
            var app=new Application();
            if(args.Contains("--overlay-language-test"))return OwnUiVerification.Run(app,output);
            if(args.Contains("--self-test-wgc"))
            {
                var fixture=new Window{Title="Companion synthetic capture test — not loot",Width=640,Height=320,
                    Background=new SolidColorBrush(Color.FromRgb(42,28,18)),Content=new TextBlock{Text="诊断样本 非真实掉落\nCHEST TEST 123",FontSize=30,Foreground=Brushes.Ivory,Margin=new Thickness(35)}};
                var code=1;
                fixture.ContentRendered+=async (_,_)=>
                {
                    try
                    {
                        var result=await CaptureProbe.Run(new WindowInteropHelper(fixture).Handle,new Region(10,40,580,180),output,"zh-Hans",CancellationToken.None);
                        Save(output,"synthetic-capture.json",new{Synthetic=true,Result=result});
                        if(result.PossibleBlank||!result.Text.Contains("123"))throw new Exception("合成图捕获/OCR 没有读出已知的 123，不能算通过。");
                        code=0;
                    }
                    catch(Exception e){Save(output,"synthetic-error.json",Error(e));}
                    finally{fixture.Close();}
                };
                app.Run(fixture);return code;
            }
            if(!args.Contains("--diagnostics")&&!args.Contains("--ui-smoke"))
                return CompanionShell.Run(app,Option(args,"--exe"),Option(args,"--save"),Option(args,"--data"),
                    Option(args,"--run-seconds") is string seconds?int.Parse(seconds):null,args.Contains("--synthetic"),args.Contains("--wiki"),args.Contains("--afk"),args.Contains("--preview-reminders"),Option(args,"--update-handoff"));
            var diagnostic=new DiagnosticWindow(exe,output);
            if(args.Contains("--ui-smoke"))
            {
                diagnostic.ContentRendered+=async (_,_)=>
                {
                    await Task.Delay(700);
                    var image=new System.Windows.Media.Imaging.RenderTargetBitmap((int)diagnostic.ActualWidth,(int)diagnostic.ActualHeight,96,96,PixelFormats.Pbgra32);
                    image.Render(diagnostic);
                    var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                    Directory.CreateDirectory(output);
                    using(var file=File.Create(Path.Combine(output,"diagnostic-ui-render.png")))encoder.Save(file);
                    diagnostic.Close();
                };
            }
            app.Run(diagnostic); return 0;
        }
        catch(Exception e)
        {
            if(player)StartupDiagnostics.Report(e);
            Console.Error.WriteLine(JsonSerializer.Serialize(Error(e),Json));return 1;
        }
    }
    internal static object Error(Exception e)=>new{Status="Failed",Type=e.GetType().Name,HResult=$"0x{e.HResult:X8}",e.Message,e.StackTrace};
    static object ProbeCapability<T>(Func<T> probe)
    {
        try{return new{Status="Available",Value=probe()};}
        catch(Exception e){return Error(e);}
    }
    static string? Option(string[] args,string key)
    {
        var index=Array.IndexOf(args,key);
        if(index<0)return null;
        if(index+1>=args.Length||args[index+1].StartsWith("--"))throw new ArgumentException($"{key} 缺少值");
        return args[index+1];
    }
    internal static GameWindow SelectWindow(string exe,string? handle=null)
    {
        var windows=Native.Windows(exe).Where(w=>w.Visible&&!w.Minimized&&!w.Cloaked&&w.Client.Width>0&&w.Client.Height>0).ToArray();
        if(handle is not null) windows=windows.Where(w=>w.Hwnd.Equals(handle,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(windows.Length!=1)throw new InvalidOperationException($"找到 {windows.Length} 个可用窗口。请刷新并明确选择；不会猜测或捕获其他窗口。");
        return windows[0];
    }
    internal static void Save(string dir,string name,object value)
    {
        Directory.CreateDirectory(dir);
        var path=Path.Combine(dir,name);
        File.WriteAllText(path,JsonSerializer.Serialize(value,Json));
        File.WriteAllText(path+".sha256",Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }
}

internal sealed class DiagnosticWindow : Window
{
    readonly TextBox exe,roi,log;
    readonly ComboBox windows,language;
    readonly string output;
    readonly CancellationTokenSource lifetime=new();
    readonly Button capture;
    public DiagnosticWindow(string path,string directory)
    {
        output=directory;Title="QuestAway Companion · Phase 0 诊断（不是统计版）";Width=820;Height=650;MinWidth=650;MinHeight=520;
        Background=new SolidColorBrush(Color.FromRgb(43,29,20));Foreground=Brushes.Ivory;
        var panel=new DockPanel{Margin=new Thickness(16)};Content=panel;
        var top=new StackPanel();DockPanel.SetDock(top,Dock.Top);panel.Children.Add(top);
        top.Children.Add(new TextBlock{Text="只读诊断：不操作游戏，不自动计数。截图/OCR 仅保存在本机。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)});
        exe=new TextBox{Text=path,Margin=new Thickness(0,3,0,3)};top.Children.Add(exe);
        var actions=new StackPanel{Orientation=Orientation.Horizontal};top.Children.Add(actions);
        Add(actions,"选择游戏 EXE",(_,_)=>{var picker=new OpenFileDialog{Filter="游戏程序 (*.exe)|*.exe",CheckFileExists=true};if(picker.ShowDialog(this)==true)exe.Text=picker.FileName;});
        Add(actions,"刷新窗口",(_,_)=>Refresh());
        windows=new ComboBox{Margin=new Thickness(0,6,0,6),DisplayMemberPath="SelectionLabel"};top.Children.Add(windows);
        top.Children.Add(new TextBlock{Text="区域 x,y,宽,高（捕获帧像素，含窗口边框；不会自动截全屏）"});
        roi=new TextBox{Text="0,0,400,160",Margin=new Thickness(0,4,0,8)};top.Children.Add(roi);
        language=new ComboBox{ItemsSource=CaptureProbe.Languages(),Margin=new Thickness(0,3,0,8)};
        language.SelectedItem=CaptureProbe.Languages().FirstOrDefault(l=>l.StartsWith("zh"))??CaptureProbe.Languages().FirstOrDefault();top.Children.Add(language);
        var controls=new StackPanel{Orientation=Orientation.Horizontal};top.Children.Add(controls);
        capture=Add(controls,"采集一次区域 + OCR",async (_,_)=>await Capture());
        Add(controls,"观察窗口 10 秒",async (_,_)=>await Observe());
        Add(controls,"打开本机结果目录",(_,_)=>{Directory.CreateDirectory(output);Process.Start(new ProcessStartInfo("explorer.exe",output){UseShellExecute=true});});
        log=new TextBox{IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,12,0,0)};panel.Children.Add(log);
        Closed+=(_,_)=>lifetime.Cancel();
        Loaded+=(_,_)=>Refresh();
    }
    static Button Add(Panel parent,string text,RoutedEventHandler action)
    {
        var button=new Button{Content=text,Margin=new Thickness(0,3,7,3),Padding=new Thickness(8,5,8,5),Background=new SolidColorBrush(Color.FromRgb(124,93,43)),Foreground=Brushes.Ivory};
        button.Click+=action;parent.Children.Add(button);return button;
    }
    void Refresh()
    {
        try
        {
            if(!Path.IsPathFullyQualified(exe.Text))throw new ArgumentException("请先指定 QuestAway 游戏 EXE。");
            var found=Native.Windows(exe.Text).Where(w=>w.Visible&&!w.Cloaked).ToArray();windows.ItemsSource=found;if(found.Length==1)windows.SelectedIndex=0;
            var result=new{WgcSupported=CaptureProbe.Supported(),OcrLanguages=CaptureProbe.Languages(),Processes=Native.Processes(exe.Text),Windows=found};
            Program.Save(output,"ui-probe.json",result);log.Text=JsonSerializer.Serialize(result,Program.Json);
        }
        catch(Exception e){log.Text=JsonSerializer.Serialize(Program.Error(e),Program.Json);}
    }
    async Task Capture()
    {
        capture.IsEnabled=false;
        try
        {
            if(windows.SelectedItem is not GameWindow selected)throw new InvalidOperationException("请先明确选择游戏窗口。");
            // Revalidate identity immediately before capture; stale HWND/PID must not target another process.
            var current=Program.SelectWindow(exe.Text,selected.Hwnd);
            if(current.Pid!=selected.Pid||current.StartUtc!=selected.StartUtc)throw new InvalidOperationException("游戏进程已变化，请刷新窗口。");
            var dir=Path.Combine(output,DateTime.Now.ToString("HHmmss-fff"));
            var result=await CaptureProbe.Run(Native.Handle(current),Region.Parse(roi.Text),dir,language.SelectedItem as string??"zh-Hans",lifetime.Token);
            Program.Save(dir,"capture.json",new{Target=current,Result=result});log.Text=JsonSerializer.Serialize(result,Program.Json)+"\n"+dir;
        }
        catch(Exception e){Program.Save(output,"last-error.json",Program.Error(e));log.Text=JsonSerializer.Serialize(Program.Error(e),Program.Json);}
        finally{capture.IsEnabled=true;}
    }
    async Task Observe()
    {
        try
        {
            var path=exe.Text;var samples=new List<object>();
            for(var i=0;i<10;i++)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                var sample=new{AtUtc=DateTimeOffset.UtcNow,Processes=Native.Processes(path),Windows=Native.Windows(path)};
                samples.Add(sample);log.Text=JsonSerializer.Serialize(sample,Program.Json);
                await Task.Delay(1000,lifetime.Token);
            }
            Program.Save(output,"window-observations-"+DateTime.Now.ToString("HHmmss-fff")+".json",samples);
        }
        catch(OperationCanceledException) { }
        catch(Exception e){log.Text=JsonSerializer.Serialize(Program.Error(e),Program.Json);}
    }
}
