using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Companion.Diagnostics;

internal static class OwnUiVerification
{
    public static int Run(Application app,string output)
    {
        app.ShutdownMode=ShutdownMode.OnExplicitShutdown;var exit=1;var results=new List<object>();
        var fixture=new Window{Title="QATK UI verification · SYNTHETIC",Width=1000,Height=720,Left=100,Top=100,
            Background=Brushes.SaddleBrown,Content=new TextBlock{Text="SYNTHETIC UI CHECK — NO REAL GAME DATA",FontSize=22,Foreground=Brushes.Ivory,Margin=new Thickness(20)}};
        fixture.ContentRendered+=async (_,_)=>
        {
            OverlayWindow? overlay=null;
            try
            {
                MotionChecks(results);
                AppResources.Validate();var settings=new CompanionSettings{Expanded=true,Scale=1,Opacity=.88,ClickThrough=true};overlay=new(settings);
                using var me=Process.GetCurrentProcess();var handle=new WindowInteropHelper(fixture).Handle;
                var target=Native.Windows(me.MainModule!.FileName).Single(w=>Native.Handle(w)==handle);
                var eventCount=0;var locationWatch=Native.WatchLocation(target,()=>eventCount++);
                if(locationWatch is null)throw new InvalidOperationException("Own location event subscription unavailable");
                fixture.Left+=10;fixture.Top+=10;await Task.Delay(200);
                var delivered=eventCount>0;results.Add(new{Name="out-of-context native movement events arrive for owned target",Passed=delivered,Synthetic=true,EventCount=eventCount});
                locationWatch.Dispose();if(!delivered)throw new InvalidOperationException("Own location events not delivered");
                var countAtDispose=eventCount;fixture.Left-=10;fixture.Top-=10;await Task.Delay(200);
                var detached=eventCount==countAtDispose;results.Add(new{Name="native location subscription releases on dispose",Passed=detached,Synthetic=true});
                if(!detached)throw new InvalidOperationException("Own location events continued after disposal");
                var qualities=SaveSource.Qualities.ToDictionary(q=>q,_=>0L);
                var first=new SaveSnapshot(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),22,"SYNTHETIC-UI",100,0,new(qualities),new(qualities),[],"synthetic-ui-first"){OpenedChestCounters=new(){["normal"]=100,["stage"]=20,["act"]=3}};
                var stats=new StatisticsEngine(null,false,false,true);stats.Push(first);
                var second=first with{SavedAt=first.SavedAt+1,ChestSerial=106,LostChests=1,Fingerprint="synthetic-ui-next",
                    ChestEquipment=SaveSource.Qualities.Select((q,i)=>(q,i)).ToDictionary(p=>p.q,p=>(long)p.i+1),
                    OpenedChestCounters=new(){["normal"]=103,["stage"]=20,["act"]=3},Items=[new("synthetic-one","wpn_knight","rare",10),new("synthetic-two","arm_heavy_helmet","heroic",10),new("synthetic-three","jwl_ring","mythic",10)]};
                stats.Push(second);
                var unavailable=new StatisticsEngine(null,false,false,true);unavailable.Push(first);unavailable.SaveUnavailable();
                foreach(var language in AppResources.Languages)
                foreach(var clickThrough in new[]{true,false})
                foreach(var unknown in new[]{false,true})
                {
                    AppResources.Language=language;settings.Language=language;settings.ClickThrough=clickThrough;
                    overlay.Update(new(unknown?"activity_unavailable":"tracking",unknown?unavailable.State():stats.State(),DateTimeOffset.UtcNow.AddMinutes(-3),target,true));
                    overlay.ApplySettings();
                    await Task.Delay(250);overlay.UpdateLayout();
                    var clipped=new List<string>();
                    foreach(var text in Descendants(overlay).OfType<TextBlock>().Where(t=>t.IsVisible))
                    {
                        var point=text.TransformToAncestor(overlay).Transform(new Point(0,0));
                        if(point.Y+text.ActualHeight>overlay.ActualHeight+1)clipped.Add(text.Text);
                    }
                    var image=new RenderTargetBitmap((int)overlay.ActualWidth,(int)overlay.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(overlay);
                    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));Directory.CreateDirectory(output);
                    using(var file=File.Create(Path.Combine(output,"overlay-"+language+(clickThrough?"-through":"-interactive")+(unknown?"-unknown":"")+".png")))encoder.Save(file);
                    var flags=Native.OverlayFlags(new WindowInteropHelper(overlay).Handle);
                    var flagJson=JsonSerializer.SerializeToElement(flags);
                    var rendered=Descendants(overlay).OfType<TextBlock>().Select(t=>t.Text).ToArray();
                    var coreVisible=rendered.Any(t=>t.Contains("本次装备产出："))&&rendered.Any(t=>t.StartsWith("最近保存："))&&rendered.Any(t=>t.StartsWith("本次记录："))&&SaveSource.Qualities.All(q=>rendered.Any(t=>t.StartsWith(AppResources.Text(q)+" ",StringComparison.Ordinal)));
                    coreVisible&=!rendered.Any(t=>t.Contains(AppResources.Text("opened_total"))||t.Contains(AppResources.Text("opened_session"))||t.Contains(AppResources.Text("source"))||t.Contains(AppResources.Text("waiting_save_update")))&&overlay.Width==OverlayWindow.BaseWidth&&overlay.Height==OverlayWindow.BaseHeight;
                    var noItemTracking=!rendered.Any(t=>t.Contains("SYNTHETIC common")||t.Contains("巨像样本"))&&!rendered.Contains(AppResources.Text("recent"));
                    var passed=coreVisible&&noItemTracking&&clipped.Count==0&&flagJson.GetProperty("ClickThrough").GetBoolean()==clickThrough&&flagJson.GetProperty("NoActivate").GetBoolean()&&!flagJson.GetProperty("IsForeground").GetBoolean();
                    results.Add(new{Language=language,ClickThrough=clickThrough,UnknownSource=unknown,Passed=passed,Synthetic=true,Dpi=target.Dpi,Clipped=clipped,Flags=flags});
                    if(!passed)throw new InvalidOperationException("Own UI layout or activation failed for "+language);
                }
                settings.ClickThrough=true;
                overlay.Update(new("tracking",stats.State(),DateTimeOffset.UtcNow.AddMinutes(-3),target,true));overlay.ApplySettings();
                async Task Attached(string name)
                {
                    await Task.Delay(700);
                    var tracked=Native.Follow(target)??throw new InvalidOperationException("Own fixture lost");
                    var actual=Native.Windows(me.MainModule!.FileName).Single(w=>Native.Handle(w)==new WindowInteropHelper(overlay).Handle).Window;
                    var x=tracked.Client.X+Math.Clamp(tracked.Client.Width-actual.Width-settings.OffsetX,0,Math.Max(0,tracked.Client.Width-actual.Width));
                    var y=tracked.Client.Y+Math.Clamp(settings.OffsetY,0,Math.Max(0,tracked.Client.Height-actual.Height));
                    var passed=Math.Abs(actual.X-x)<=1&&Math.Abs(actual.Y-y)<=1;
                    results.Add(new{Name=name,Passed=passed,Synthetic=true,TargetClient=tracked.Client,ActualOverlay=actual,ExpectedX=x,ExpectedY=y});
                    if(!passed)throw new InvalidOperationException("Own overlay attachment failed: "+name);
                }
                fixture.Left+=140;fixture.Top+=70;fixture.Width=500;fixture.Height=230;
                await Attached("follow moved and resized fixture with automatic downscaling");
                var ownHandle=new WindowInteropHelper(overlay).Handle;
                var beforeDrift=Native.Windows(me.MainModule!.FileName).Single(w=>Native.Handle(w)==ownHandle).Window;
                Native.PositionOverlay(ownHandle,beforeDrift.X+28,beforeDrift.Y+18,beforeDrift.Width,beforeDrift.Height);
                await Attached("repair own-window displacement while target rectangle remains unchanged");
                fixture.Width=1000;fixture.Height=720;fixture.Left-=140;fixture.Top-=70;
                await Attached("follow restored fixture size and position");
                overlay.Close();
                Box? simulatedSurface=new(target.Monitor.X+300,target.Monitor.Y+300,425,234);
                overlay=new(settings,new GameSurfaceTracker(_=>Volatile.Read(ref simulatedSurface)));
                var surfaceTarget=target with{ClassName="Tauri Window"};
                overlay.Update(new("tracking",stats.State(),DateTimeOffset.UtcNow,surfaceTarget,true));
                async Task SurfaceAttached(string name,bool visible=true)
                {
                    await Task.Delay(1100);
                    var surface=Volatile.Read(ref simulatedSurface);
                    var actual=Native.Windows(me.MainModule!.FileName).SingleOrDefault(w=>Native.Handle(w)==new WindowInteropHelper(overlay).Handle);
                    var screen=System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(surface?.X??0,surface?.Y??0)).WorkingArea;
                    var placement=surface is null?null:WikiPopupPlacement.Dock(new(surface.X,surface.Y,surface.Width,surface.Height),new(screen.X,screen.Y,screen.Width,screen.Height),new(OverlayWindow.BaseWidth,OverlayWindow.BaseHeight),settings.DockSide,alignBottom:true);
                    var expectedX=placement?.X??0;var expectedY=placement?.Y??0;
                    var passed=visible?actual is {Visible:true}&&Math.Abs(actual.Window.X-expectedX)<=1&&Math.Abs(actual.Window.Y-expectedY)<=1&&!Native.OverlayFlags(new WindowInteropHelper(overlay).Handle).ToString()!.Contains("IsForeground = True"):
                        actual is null||!actual.Visible;
                    results.Add(new{Name=name,Passed=passed,Synthetic=true,Surface=surface,Host=Native.Follow(target)?.Client,ActualOverlay=actual?.Window,ActualVisible=actual?.Visible,Flags=Native.OverlayFlags(new WindowInteropHelper(overlay).Handle),ExpectedX=expectedX,ExpectedY=expectedY});
                    if(!passed)throw new InvalidOperationException("Surface attachment failed: "+name);
                }
                await SurfaceAttached("attach beside visible surface rather than transparent host corner");
                Volatile.Write(ref simulatedSurface,simulatedSurface with{X=simulatedSurface.X+80,Y=simulatedSurface.Y+40});
                await SurfaceAttached("follow surface movement while host rectangle stays unchanged");
                Volatile.Write(ref simulatedSurface,simulatedSurface with{Width=550,Height=300});
                await SurfaceAttached("follow surface resize while host rectangle stays unchanged");
                Volatile.Write(ref simulatedSurface,simulatedSurface with{X=target.Monitor.X+target.Monitor.Width-550});
                await SurfaceAttached("keep chosen attachment side and clamp at monitor edge");
                var lastSurface=simulatedSurface;Volatile.Write(ref simulatedSurface,null);
                await SurfaceAttached("missing surface hides instead of jumping to fullscreen host",false);
                Volatile.Write(ref simulatedSurface,lastSurface);
                await SurfaceAttached("surface recovery restores attachment");
                Volatile.Write(ref simulatedSurface,new Box(target.Monitor.X+300,target.Monitor.Y+300,425,234));
                await SurfaceAttached("surface movement latency baseline");
                var delays=new List<double>();
                for(var i=0;i<12;i++)
                {
                    var next=simulatedSurface! with{X=simulatedSurface!.X+4,Y=simulatedSurface.Y+2};
                    var watch=Stopwatch.StartNew();Volatile.Write(ref simulatedSurface,next);
                    while(watch.ElapsedMilliseconds<1000&&!Native.OverlayIsAt(new WindowInteropHelper(overlay).Handle,next.X+next.Width+8,next.Y+next.Height-OverlayWindow.BaseHeight,OverlayWindow.BaseWidth,OverlayWindow.BaseHeight))await Task.Delay(1);
                    delays.Add(watch.Elapsed.TotalMilliseconds);
                }
                var timely=delays.All(ms=>ms<150);
                results.Add(new{Name="surface updates have no 200ms polling stall on this fixture",Passed=timely,Synthetic=true,ObservedDelayMs=delays});
                if(!timely)throw new InvalidOperationException("Own surface follow latency exceeded fixture threshold");
                Program.Save(output,"ui-language-result.json",new{Passed=true,Synthetic=true,Results=results,Notice="Own-window rendering at observed monitor DPI only; not real loot or untested DPI acceptance."});exit=0;
            }
            catch(Exception e){Program.Save(output,"ui-language-result.json",new{Passed=false,Synthetic=true,Results=results,Error=Program.Error(e)});}
            finally{overlay?.Close();fixture.Close();app.Shutdown();}
        };
        fixture.Show();app.Run();return exit;
    }
    static void MotionChecks(List<object> results)
    {
        var body=new Box(300,300,425,234);var proxy=new Box(400,533,240,427);var monitor=new Box(0,0,2560,1440);
        NativeFollowerMotion Seed(){var m=new NativeFollowerMotion();m.Resolve(body,proxy,monitor,350,350,false,true,0);m.Resolve(body,proxy,monitor,350,350,false,true,400);return m;}
        void Check(string name,Box actual,Box expected){var passed=actual==expected;results.Add(new{Name=name,Passed=passed,Synthetic=true,Actual=actual,Expected=expected});if(!passed)throw new InvalidOperationException(name);}
        var m=Seed();Check("native follower baseline does not fabricate movement",m.Resolve(body,proxy,monitor,350,350,true,true,410),body);
        var moved=proxy with{X=480,Y=573};var translated=body with{X=380,Y=340};
        Check("native follower updates while UIA scene bounds are stale",m.Resolve(body,moved,monitor,430,390,true,true,426),translated);
        Check("absolute native displacement does not accumulate duplicate reads",m.Resolve(body,moved,monitor,430,390,true,true,442),translated);
        Check("release retains confirmed native displacement until UIA catches up",m.Resolve(body,moved,monitor,430,390,false,true,458),translated);
        Check("UIA catchup does not double native displacement",m.Resolve(translated,moved,monitor,430,390,false,true,610),translated);
        m.Resolve(translated,moved,monitor,430,390,true,true,620);
        Check("confirmed release permits a rapid second scene drag",m.Resolve(translated,moved with{X=500},monitor,450,390,true,true,636),translated with{X=400});
        m=Seed();m.Resolve(body,proxy,monitor,800,900,true,true,410);
        Check("independent activity-window drag cannot move the scene overlay",m.Resolve(body,moved,monitor,880,940,true,true,426),body);
        m=Seed();m.Resolve(body,proxy,monitor,350,350,true,false,410);
        Check("unfocused scene cannot arm native follower motion",m.Resolve(body,moved,monitor,430,390,true,false,426),body);
        m=Seed();m.Resolve(body,proxy,monitor,350,350,true,true,410);
        Check("click without native movement is not predicted mouse dragging",m.Resolve(body,proxy,monitor,430,390,true,true,426),body);
        Check("clamped native follower falls back to measured scene",m.Resolve(body,proxy with{Y=1013},monitor,430,390,true,true,442),body);
        m=Seed();m.Resolve(body,proxy,monitor,350,350,true,true,410);m.Resolve(body,moved,monitor,430,390,true,true,426);
        var resized=body with{Width=500};Check("scene resize invalidates translation calibration",m.Resolve(resized,moved,monitor,430,390,true,true,442),resized);
        m=Seed();m.Resolve(body,proxy,monitor,350,350,true,true,410);m.Resolve(body,moved,monitor,430,390,true,true,426);
        Check("follower resize cannot be guessed as scene scaling",m.Resolve(body,moved with{Width=300},monitor,430,390,true,true,442),body);
        m=Seed();m.Resolve(body,proxy,monitor,350,350,true,true,410);m.Resolve(body,moved,monitor,430,390,true,true,426);m.Resolve(body,moved,monitor,430,390,false,true,442);
        Check("unreconciled release eventually returns to measured source",m.Resolve(body,moved,monitor,430,390,false,true,1100),body);
        m=Seed();m.Resolve(body,proxy,monitor,350,350,true,true,410);
        Check("stationary native window does not suppress a measured scene move",m.Resolve(translated,proxy,monitor,430,390,true,true,600),translated);
        m=new();var edgeBody=body with{Y=1000};var edgeProxy=proxy with{Y=1013};
        m.Resolve(edgeBody,edgeProxy,monitor,350,1050,false,true,0);m.Resolve(edgeBody,edgeProxy,monitor,350,1050,false,true,400);m.Resolve(edgeBody,edgeProxy,monitor,350,1050,true,true,410);
        Check("startup-clamped follower cannot establish a false scene offset",m.Resolve(edgeBody,edgeProxy with{Y=900},monitor,350,940,true,true,426),edgeBody);
        m=Seed();m.Resolve(body,moved,monitor,800,900,false,true,410);
        Check("independently moved follower must settle before rearming",m.Resolve(body,moved,monitor,350,350,true,true,426),body);
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}
    }
}
