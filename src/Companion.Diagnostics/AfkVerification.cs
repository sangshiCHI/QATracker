using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Companion.Diagnostics;

internal static class AfkVerification
{
    static SaveSnapshot Sample(long time,double xp=100,int level=73,string identity="SYNTHETIC",int stage=3)=>new(time,22,identity,10,0,
        SaveSource.Qualities.ToDictionary(q=>q,_=>0L),SaveSource.Qualities.ToDictionary(q=>q,_=>0L),[],time.ToString())
        {OpenedChestCounters=new(){{"normal",10},{"stage",0},{"act",0}},Afk=new(3,stage,2,
            new[]{"knight","archer","mage","priest"}.Select((id,i)=>new AfkHero("synthetic-"+i,id,level,xp+i)).ToArray(),[new("insight",3600000)])};
    public static int Run(Application app,string output)
    {
        Directory.CreateDirectory(output);var checks=new Dictionary<string,bool>();var exit=1;
        var fixture=new Window{Title="QATK AFK owned-window fixture · SYNTHETIC",Left=300,Top=280,Width=425,Height=234,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowActivated=false,Background=Brushes.SaddleBrown,Content=new TextBlock{Text="SYNTHETIC OWNED GAME WINDOW",Foreground=Brushes.Ivory}};
        var started=false;
        fixture.ContentRendered+=async (_,_)=>
        {
            if(started)return;started=true;
            OverlayWindow? overlay=null;
            try
            {
                var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();var first=Sample(now-21000);var second=Sample(now-1000,300);
                var tracker=new AfkTracker();tracker.Push(first);checks["first XP sample is baseline not income"]=tracker.Current!.Heroes.All(h=>h.XpPerHour is null);
                tracker.Push(second);checks["four hero same-level XP rates from saved time"]=tracker.Current!.Heroes.Length==4&&tracker.Current.Heroes.All(h=>h.XpPerHour==36000);
                tracker.Push(second);checks["repeated save does not add rate samples"]=tracker.Current!.Heroes.All(h=>h.XpPerHour==36000);
                tracker.Push(Sample(now+19000,30,74));checks["level up resets rate rather than negative or invented XP"]=tracker.Current!.Heroes.All(h=>h.XpPerHour is null);
                tracker.Push(Sample(now+39000,40,74,"OTHER"));checks["identity switch resets XP rate"]=tracker.Current!.Heroes.All(h=>h.XpPerHour is null);
                tracker.Push(Sample(now+59000,50,74,"OTHER",4));checks["map switch resets XP rate"]=tracker.Current!.Heroes.All(h=>h.XpPerHour is null);
                tracker.Push(Sample(now+59000+121000,60,74,"OTHER",4));checks["long observation gap resets XP rate"]=tracker.Current!.Heroes.All(h=>h.XpPerHour is null);
                tracker.Push(Sample(now-1000));checks["save rollback resets XP rate"]=tracker.Current!.Heroes.All(h=>h.XpPerHour is null);
                checks["AFK display is not serialized into history or exports"]=!JsonSerializer.Serialize(first).Contains("synthetic-0")&&!JsonSerializer.Serialize(first).Contains("Afk");
                using(var json=JsonDocument.Parse("{\"act\":3,\"stage\":3,\"difficulty\":2,\"heroes\":[{\"id\":\"test\",\"classId\":\"knight\",\"level\":73,\"xp\":123.5}],\"buffs\":[{\"id\":\"insight\",\"msRemaining\":10000}],\"settings\":{\"password\":\"NOT_COLLECTED\"}}"))
                {
                    var p=SaveSource.ParseAfk(json.RootElement)!;checks["whitelist parses actual potion millisecond key and fractional XP"]=p.Heroes[0].Xp==123.5&&p.Potions![0].RemainingMs==10000;
                    checks["raw hero ID and unrelated settings omitted"]=!JsonSerializer.Serialize(p).Contains("NOT_COLLECTED")&&p.Heroes[0].Key!="test";
                }
                var clock=DateTimeOffset.FromUnixTimeMilliseconds(now-1000);tracker.Reset();tracker.Push(first);tracker.Push(second);
                checks["active potion countdown explicitly approximate"]=AfkPanel.PotionText(tracker.Current,clock.AddSeconds(1)).Contains("约");
                checks["stale potion never silently zero"]=AfkPanel.PotionText(tracker.Current,clock.AddMinutes(2)).Contains("等待新存档");
                checks["missing potion source stays unknown"]=AfkPanel.PotionText(null,clock)=="药水：—";
                checks["real difficulty label is reused from local game catalogue"]=AfkPanel.MapName(first.Afk! with{Stage=6})=="深渊 3-6";
                checks["map time uses human minute second format"]=AfkPanel.MapDuration(71)=="1分11秒"&&AfkPanel.MapDuration(58)=="58秒";
                var emptyPotion=new AfkTelemetry(now,first.Afk! with{Potions=[]},[]);
                checks["fresh empty potion list confirms warning"]=AfkPanel.NoActivePotion(emptyPotion,DateTimeOffset.FromUnixTimeMilliseconds(now));
                checks["unknown stale or future potion state never warns empty"]=!AfkPanel.NoActivePotion(null,clock)&&!AfkPanel.NoActivePotion(emptyPotion,DateTimeOffset.FromUnixTimeMilliseconds(now).AddSeconds(76))&&!AfkPanel.NoActivePotion(emptyPotion,DateTimeOffset.FromUnixTimeMilliseconds(now-1));
                checks["wall clock alone cannot declare saved active potion expired"]=!AfkPanel.NoActivePotion(emptyPotion with{Save=first.Afk! with{Potions=[new("insight",1000)]}},DateTimeOffset.FromUnixTimeMilliseconds(now+2000));
                var mapTracker=new AfkTracker();var enemyCount=AfkWaveRules.TotalEnemies(first.Afk!)!.Value;
                mapTracker.Push(first with{Afk=first.Afk! with{TotalKills=1000}});
                checks["map estimate initially unknown"]=mapTracker.Current!.EstimatedMapSeconds is null;
                mapTracker.Push(first with{SavedAt=first.SavedAt+60000,Afk=first.Afk! with{TotalKills=1000+enemyCount}});
                checks["map estimate derives from observed kill rate and native enemy count"]=mapTracker.Current!.EstimatedMapSeconds==60;
                mapTracker.Push(first with{SavedAt=first.SavedAt+80000,Afk=first.Afk! with{Stage=4,TotalKills=1000+enemyCount+10}});
                checks["map switch clears prior map timing"]=mapTracker.Current!.EstimatedMapSeconds is null;
                var rows=new[]{("3-3 · Lv74 · 适中",new Rect(0,100,150,20)),("9/12",new Rect(160,100,50,20)),("12/100",new Rect(0,200,50,20))};
                var hud=GameHudReading.Parse(rows);checks["wave reads unique fraction on map HUD row"]=hud?.Map=="3-3"&&hud.Wave=="9/12";
                checks["ambiguous fractions stay unknown"]=GameHudReading.Parse(rows.Append(("8/12",new Rect(220,100,50,20)))) is null;
                checks["invalid wave values rejected"]=GameHudReading.Parse(new[]{rows[0],("13/12",rows[1].Item2)}) is null;
                checks["native map 3-3 first completed wave uses exact group size"]=AfkWaveRules.Completed(first.Afk! with{StageKills=7}) is{Completed:1,Total:12};
                checks["native map first group not yet complete"]=AfkWaveRules.Completed(first.Afk! with{StageKills=6}) is{Completed:0,Total:12};
                checks["unknown saved progress stays unknown"]=AfkWaveRules.Completed(first.Afk!) is null;
                checks["wave rules reject unmatched executable"]=!AfkWaveRules.MatchesExe(Environment.ProcessPath!);
                var xpTarget=AfkWaveRules.XpToNext(73)!.Value;
                var xpHero=new AfkHeroRate(new("synthetic","knight",73,xpTarget/2),xpTarget/2);
                checks["upgrade ETA uses native required XP and measured rate"]=AfkPanel.UpgradeEstimate(xpHero,true,now,DateTimeOffset.FromUnixTimeMilliseconds(now))=="约1小时后升级";
                checks["upgrade ETA stays unknown without verified rules or rate"]=AfkPanel.UpgradeEstimate(xpHero,false,now,DateTimeOffset.FromUnixTimeMilliseconds(now))=="计算中"&&AfkPanel.UpgradeEstimate(xpHero with{XpPerHour=null},true,now,DateTimeOffset.FromUnixTimeMilliseconds(now))=="计算中";
                checks["upgrade ETA rejects stale samples"]=AfkPanel.UpgradeEstimate(xpHero,true,now,DateTimeOffset.FromUnixTimeMilliseconds(now).AddMinutes(2))=="计算中";
                var next=ColossusReminder.Parse("{\"agora\":1000000,\"proxima\":{\"id\":\"2026-11-10T07\",\"tipo\":8,\"abre\":1300000}}",1000)!;
                checks["colossus public time parser"]=next.Type==8&&next.OpensAt==1300000;
                checks["five minute reminder due at threshold"]=ColossusReminder.Due(next,5,1000,[]);
                checks["one minute reminder not early"]=!ColossusReminder.Due(next,1,1000,[]);
                var one=next with{ServerAt=next.OpensAt-60000};
                checks["one minute reminder due at threshold"]=ColossusReminder.Due(one,1,1000,[]);
                checks["reminder event dedupe across toggles"]=!ColossusReminder.Due(one,1,1000,[one.Key]);
                checks["started event never triggers late notification"]=!ColossusReminder.Due(one,1,61000,[]);
                checks["stale official data never guessed"]=!ColossusReminder.Due(next,5,181001,[]);
                checks["invalid lead ignored"]=!ColossusReminder.Due(one,2,1000,[]);
                checks["missing official next remains unknown"]=ColossusReminder.Parse("{\"agora\":1000000,\"proxima\":null}",1000) is null;
                checks["monotonic elapsed clock drives countdown"]=ColossusReminder.RemainingSeconds(next,2000)==299;
                var toast=ColossusReminder.Toast(next,300,true);toast.Show();await Task.Delay(100);
                var toastFlags=JsonSerializer.SerializeToElement(Native.OverlayFlags(new WindowInteropHelper(toast).Handle));
                checks["corner toast never activates or takes taskbar entry"]=toastFlags.GetProperty("NoActivate").GetBoolean()&&!toastFlags.GetProperty("IsForeground").GetBoolean()&&!toast.ShowInTaskbar;
                WikiUi.SaveOwnWindow(toast,output,"reminder-preview-synthetic.png");toast.Close();
                var cachePath=Path.Combine(output,"cache-owned-fixture.json");File.WriteAllText(cachePath,"original");var calls=0;long cacheTick=1000;
                var cache=new AfkReadCache((_,_)=>Task.FromResult(Sample(now+ ++calls)),()=>cacheTick);
                await cache.Read(cachePath,true,CancellationToken.None);
                for(var i=0;i<4;i++){cacheTick+=1000;await cache.Read(cachePath,true,CancellationToken.None);}
                checks["low resource mode skips repeated full parse within bounded TTL"]=calls==1;
                cacheTick+=1000;await cache.Read(cachePath,true,CancellationToken.None);
                checks["unchanged metadata still forces full validation every five seconds"]=calls==2;
                File.WriteAllText(cachePath,"changed-length");await cache.Read(cachePath,true,CancellationToken.None);
                checks["changed file metadata bypasses cache immediately"]=calls==3;
                File.Delete(cachePath);try{await cache.Read(cachePath,true,CancellationToken.None);checks["missing save cannot be hidden by cache"]=false;}catch(FileNotFoundException){checks["missing save cannot be hidden by cache"]=true;}
                File.WriteAllText(cachePath,"original");await cache.Read(cachePath,false,CancellationToken.None);await cache.Read(cachePath,false,CancellationToken.None);
                checks["normal mode retains uncached reader semantics"]=calls==5;
                var validPath=Path.Combine(output,"hash-cache-synthetic.json");
                var valid="""{"savedAt":1000,"game":{"version":22,"game":{"playerId":"SYNTHETIC","sim":{"chestSerial":10,"bausPerdidos":0},"itemsFound":{"chest":{"common":1},"forge":{}}}}}""";
                File.WriteAllText(validPath,valid);long hashClock=0;var realCache=new AfkReadCache(clock:()=>hashClock);
                var originalSample=await realCache.Read(validPath,true,CancellationToken.None);hashClock=5000;
                var sameSample=await realCache.Read(validPath,true,CancellationToken.None);
                checks["full byte revalidation reuses snapshot only for identical SHA256"]=ReferenceEquals(originalSample,sameSample);
                var stamp=File.GetLastWriteTimeUtc(validPath);File.WriteAllText(validPath,valid.Replace("\"common\":1","\"common\":2"));File.SetLastWriteTimeUtc(validPath,stamp);hashClock=10000;
                var changedSample=await realCache.Read(validPath,true,CancellationToken.None);
                checks["same size and timestamp changed bytes are reparsed at five second bound"]=!ReferenceEquals(sameSample,changedSample)&&changedSample.ChestEquipment["common"]==2;
                File.WriteAllText(validPath,"invalid");hashClock=15000;
                try{await realCache.Read(validPath,true,CancellationToken.None);checks["changed invalid bytes never reuse valid snapshot"]=false;}catch(JsonException){checks["changed invalid bytes never reuse valid snapshot"]=true;}
                File.WriteAllText(validPath,valid);var recovered=await realCache.Read(validPath,true,CancellationToken.None);
                checks["valid source recovers after rejected bytes"]=recovered.ChestEquipment["common"]==1;
                using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();try{await realCache.Read(validPath,true,cancelled.Token);checks["cache cancellation preserved"]=false;}catch(OperationCanceledException){checks["cache cancellation preserved"]=true;}}
                using var me=Process.GetCurrentProcess();var handle=new WindowInteropHelper(fixture).Handle;
                var target=Native.Windows(me.MainModule!.FileName).Single(w=>Native.Handle(w)==handle);var original=target.Client;
                var connectionChosen=false;
                var picker=new GameConnectionWindow(validPath,_=>connectionChosen=true){ShowActivated=false};
                var observation=new ProcessObservation(target.Pid,target.StartUtc,target.Exe,target.Hwnd,null);
                picker.UpdateCandidates([observation,observation with{Pid=int.MaxValue}]);picker.Show();picker.UpdateLayout();
                IEnumerable<DependencyObject> Nodes(DependencyObject root)
                {
                    yield return root;
                    foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var node in Nodes(child))yield return node;
                }
                var list=Nodes(picker).OfType<ListBox>().Single();var confirmation=Nodes(picker).OfType<CheckBox>().Single();
                var connect=Nodes(picker).OfType<Button>().Single(b=>b.Content as string=="连接这一个");
                checks["multi-instance choice lists candidates without auto-connecting"]=list.Items.Count==2&&!connect.IsEnabled&&!connectionChosen;
                list.SelectedIndex=0;checks["instance selection alone does not confirm save ownership"]=!connect.IsEnabled;
                confirmation.IsChecked=true;checks["explicit instance plus save confirmation enables connection"]=connect.IsEnabled;
                picker.UpdateCandidates([observation with{MainWindow="0x999"},observation with{Pid=int.MaxValue,MainWindow="0x888"}]);
                checks["main HWND metadata refresh does not reset selected process or confirmation"]=confirmation.IsChecked==true&&connect.IsEnabled&&list.SelectedIndex==0;
                WikiUi.SaveOwnWindow(picker,output,"connection-choice-synthetic.png");
                connect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                for(var i=0;i<40&&!connectionChosen;i++)await Task.Delay(50);
                checks["confirmed owned process connects with readable synthetic save"]=connectionChosen&&!picker.IsVisible;
                picker.Close();
                var stats=new StatisticsEngine(null,false,false,true);stats.Push(first);stats.Push(second);
                var view=new RuntimeView("tracking",stats.State(),DateTimeOffset.UtcNow.AddHours(-2),target,true){Afk=tracker.Current};
                var uiSettings=new CompanionSettings();overlay=new(uiSettings);overlay.Update(view);await Task.Delay(200);
                checks["small AFK launcher exists beside wiki"]=overlay.OwnedWindows.Cast<Window>().Any(w=>w.Title=="QATK · 挂机入口"&&w.Width<=48);
                await overlay.ToggleAfk();await Task.Delay(300);overlay.Update(view with{Window=null});
                checks["AFK minimizes only owned fixture"]=Native.IsIconic(handle);
                checks["AFK stays visible even when runtime window observation missing"]=overlay.IsVisible&&overlay.IsAfk;
                checks["AFK disables per frame following and uses one second display clock"]=overlay.LowResourceDisplay;
                checks["AFK wiki always prefers above independent of normal docking"]=overlay.WikiPreferredSide=="top";
                checks["AFK panel exactly occupies original game client"]=Native.OverlayIsAt(new WindowInteropHelper(overlay).Handle,original.X,original.Y,original.Width,original.Height);
                uiSettings.Scale=1.2;overlay.ApplySettings();
                checks["AFK scale updates live without changing original footprint"]=overlay.Content is Border{Child:AfkPanel{LayoutTransform:ScaleTransform{ScaleX:1.2}}}&&Native.OverlayIsAt(new WindowInteropHelper(overlay).Handle,original.X,original.Y,original.Width,original.Height);
                uiSettings.Scale=1;overlay.ApplySettings();
                var minimizedBeforeDrag=Native.Follow(target);
                overlay.MoveAfkTo(original.X+24,original.Y+18);var moved=overlay.AfkBounds!;overlay.Update(view);await Task.Delay(1100);
                checks["AFK dragged position survives runtime and timer updates"]=Native.OverlayIsAt(new WindowInteropHelper(overlay).Handle,moved.X,moved.Y,moved.Width,moved.Height)&&moved.X!=original.X;
                checks["AFK dragging never moves minimized game"]=minimizedBeforeDrag is not null&&Native.Follow(target)?.Client==minimizedBeforeDrag.Client&&Native.Follow(target)?.Window==minimizedBeforeDrag.Window;
                var flags=JsonSerializer.SerializeToElement(Native.OverlayFlags(new WindowInteropHelper(overlay).Handle));checks["AFK panel is interactive but nonactivating"]=!flags.GetProperty("ClickThrough").GetBoolean()&&flags.GetProperty("NoActivate").GetBoolean();
                WikiUi.SaveOwnWindow(overlay,output,"afk-owned-window.png");
                Native.SetMinimized(target,false);await Task.Delay(1300);
                for(var i=0;i<15&&!Native.IsIconic(handle);i++)await Task.Delay(100);
                checks["external game restore keeps AFK and re-minimizes only its target"]=overlay.IsAfk&&overlay.IsVisible&&Native.IsIconic(handle);
                Native.SetMinimized(target,false);await Task.Delay(1300);
                for(var i=0;i<15&&!Native.IsIconic(handle);i++)await Task.Delay(100);
                checks["repeated external restore never exits AFK or moves panel"]=overlay.IsAfk&&Native.IsIconic(handle)&&overlay.AfkBounds==moved;
                Program.Save(output,"afk-resize-debug-before.json",new{Moved=moved,Current=overlay.AfkBounds,Actual=Native.OverlayBounds(new WindowInteropHelper(overlay).Handle)});
                checks["AFK exposes native resizable frame"]=overlay.ResizeMode==ResizeMode.CanResize;
                var own=new WindowInteropHelper(overlay).Handle;
                Native.PositionOverlay(own,moved.X,moved.Y,moved.Width+60,moved.Height+40);overlay.RememberAfkSize();var resized=overlay.AfkBounds!;overlay.Update(view);await Task.Delay(1100);
                checks["AFK resized rectangle survives periodic updates"]=resized.Width==moved.Width+60&&resized.Height==moved.Height+40&&Native.OverlayIsAt(own,resized.X,resized.Y,resized.Width,resized.Height);
                Program.Save(output,"afk-resize-debug-after.json",new{Resized=resized,Current=overlay.AfkBounds,Actual=Native.OverlayBounds(own)});
                overlay.MinimizeAfk();await Task.Delay(1100);
                checks["AFK minimize hides helper without restoring game"]=!overlay.IsVisible&&overlay.IsAfk&&Native.IsIconic(handle);
                overlay.ShowAfk();checks["tray show returns same AFK rectangle"]=overlay.IsVisible&&overlay.AfkBounds==resized;
                overlay.MinimizeAfk();Native.SetMinimized(target,false);await Task.Delay(1300);
                checks["game taskbar restore also unhides minimized helper"]=overlay.IsVisible&&overlay.IsAfk&&Native.IsIconic(handle);
                Native.PositionOverlay(own,resized.X,resized.Y,240,150);overlay.RememberAfkSize();overlay.Update(view);await Task.Delay(300);overlay.UpdateLayout();
                checks["AFK has no detached launcher windows"]=overlay.OwnedWindows.Cast<Window>().All(w=>!w.IsVisible);
                IEnumerable<System.Windows.DependencyObject> Walk(System.Windows.DependencyObject node){for(var i=0;i<VisualTreeHelper.GetChildrenCount(node);i++){var child=VisualTreeHelper.GetChild(node,i);yield return child;foreach(var descendant in Walk(child))yield return descendant;}}
                var inlineButtons=Walk(overlay).OfType<Button>().Where(b=>b.Content is string s&&new[]{"恢复","wiki","−","⚙"}.Contains(s)).ToArray();
                checks["all four AFK actions are inside narrow panel"]=inlineButtons.Length==4&&inlineButtons.All(b=>{var p=b.TranslatePoint(new Point(),overlay);return p.X>=0&&p.Y>=0&&p.X+b.ActualWidth<=overlay.ActualWidth+.5&&p.Y+b.ActualHeight<=overlay.ActualHeight+.5;});
                WikiUi.SaveOwnWindow(overlay,output,"afk-narrow-controls.png");
                var preview=new Window{Width=425,Height=234,ShowActivated=false,WindowStyle=WindowStyle.None,Background=Brushes.SaddleBrown};var panel=new AfkPanel();preview.Content=new Border{Padding=new Thickness(8),Child=panel};
                var previewData=tracker.Current! with{Heroes=tracker.Current!.Heroes.Select(h=>new AfkHeroRate(h.Hero with{Xp=xpTarget*.6},xpTarget*.2)).ToArray()};
                panel.Update(view with{Afk=previewData,WaveRulesVerified=true},hud);preview.Show();WikiUi.SaveOwnWindow(preview,output,"afk-data-preview-synthetic.png");preview.Close();
                var warningPreview=new Window{Width=425,Height=310,ShowActivated=false,WindowStyle=WindowStyle.None,Background=Brushes.SaddleBrown};var warningPanel=new AfkPanel();warningPreview.Content=new Border{Padding=new Thickness(8),Child=warningPanel};
                warningPanel.Update(view with{State=view.State with{EquipmentAvailable=true},Afk=emptyPotion with{SavedAt=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),Save=emptyPotion.Save with{Stage=6,StageKills=5}},WaveRulesVerified=true},null,true);warningPreview.Show();warningPreview.UpdateLayout();
                checks["no potion warning visible beside equipment total"]=Walk(warningPanel).OfType<TextBlock>().Any(t=>t.Text=="当前没有生效的药水"&&t.Visibility==Visibility.Visible);
                checks["wave text retained without progress bar per clarified request"]=!Walk(warningPanel).OfType<ProgressBar>().Any(p=>p.Height==14)&&Walk(warningPanel).OfType<TextBlock>().Any(t=>t.Text.Contains("已清波次")&&t.Text.Contains("存档"));
                var summaries=Walk(warningPanel).OfType<TextBlock>().Where(t=>t.Tag as string=="farming-summary").ToArray();
                checks["map wave and estimated map time share one nonwrapping line"]=summaries.Length==1&&summaries[0].Text.Contains("已清波次")&&(summaries[0].Text.Contains("/图")||summaries[0].Text.Contains("每图耗时"))&&summaries[0].TextWrapping==TextWrapping.NoWrap;
                checks["AFK footer displays actual version"]=Walk(warningPanel).OfType<TextBlock>().Any(t=>t.Text=="v"+SoftwareUpdate.DisplayVersion);
                WikiUi.SaveOwnWindow(warningPreview,output,"afk-map-potion-warning-synthetic.png");
                var stateBeforePreview=JsonSerializer.Serialize(view.State);
                warningPanel.Update(view with{Afk=previewData with{SavedAt=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}},null,false);warningPanel.PreviewPotionWarning(150);
                checks["potion preview visibly marks test without requiring real exhaustion"]=Walk(warningPanel).OfType<TextBlock>().Any(t=>t.Text.StartsWith("[测试] 当前没有生效的药水")&&t.Visibility==Visibility.Visible);
                WikiUi.SaveOwnWindow(warningPreview,output,"potion-reminder-preview-synthetic.png");await Task.Delay(230);warningPanel.Tick();
                checks["potion preview expires and never changes real counters"]=!Walk(warningPanel).OfType<TextBlock>().Any(t=>t.Text.StartsWith("[测试] 当前没有生效的药水")&&t.Visibility==Visibility.Visible)&&JsonSerializer.Serialize(view.State)==stateBeforePreview;
                warningPreview.Close();
                await overlay.ToggleAfk();await Task.Delay(1300);
                checks["restore returns original window and default overlay"]=!Native.IsIconic(handle)&&!overlay.IsAfk;
                checks["restore preserves native original position"]=Native.Follow(target)?.Client==original;
                checks["normal mode has no AFK update handoff"]=overlay.CaptureUpdateState() is null;
                checks["normal footer displays actual version"]=Walk(overlay).OfType<TextBlock>().Any(t=>t.Text=="v"+SoftwareUpdate.DisplayVersion);
                WikiUi.SaveOwnWindow(overlay,output,"normal-version-local.png");
                var offered=new UpdateRelease("v0.9.9","[测试] 挂机更新状态交接",new Uri("https://github.com/sangshiCHI/QATracker/releases/download/v0.9.9/QATK.exe"),new string('A',64),100);
                var normalUpdate=CompanionShell.NewUpdateWindow(offered,out _,out var normalTitle);normalUpdate.Show();await Task.Delay(80);
                checks["normal mode update dialog shows version without stealing focus"]=normalUpdate.IsVisible&&normalUpdate.Topmost&&!normalUpdate.ShowActivated&&normalTitle.Text.Contains("v0.9.9");normalUpdate.Close();
                overlay.Update(view);await overlay.ToggleAfk();await Task.Delay(250);overlay.MoveAfkTo(moved.X,moved.Y);overlay.MinimizeAfk();
                var handoff=overlay.CaptureUpdateState()!;var encoded=JsonSerializer.Serialize(handoff);var decoded=JsonSerializer.Deserialize<AfkUpdateState>(encoded)!;
                checks["AFK update handoff persists target bounds hidden and process identity"]=SoftwareUpdate.ValidAfkState(decoded)&&decoded.Target==handoff.Target&&decoded.Bounds==handoff.Bounds&&decoded.Hidden&&decoded.Windows.SequenceEqual(handoff.Windows);
                checks["AFK handoff refuses foreign process and invalid dimensions"]=!SoftwareUpdate.ValidAfkState(handoff with{Bounds=handoff.Bounds with{Width=0}})&&!SoftwareUpdate.ValidAfkState(handoff with{Windows=[handoff.Target with{Pid=handoff.Target.Pid+1}]});
                overlay.Close();overlay=new(uiSettings);overlay.Update(view);await Task.Delay(200);overlay.ResumeUpdateState(decoded);await Task.Delay(250);
                checks["updated overlay restores hidden AFK and exact panel bounds"]=overlay.IsAfk&&overlay.AfkBounds==decoded.Bounds&&!overlay.IsVisible&&Native.IsIconic(handle)&&overlay.LowResourceDisplay;
                var hiddenUpdate=CompanionShell.NewUpdateWindow(offered,out _,out _);hiddenUpdate.Show();await Task.Delay(80);
                checks["minimized AFK panel still allows independent update prompt"]=overlay.IsAfk&&!overlay.IsVisible&&hiddenUpdate.IsVisible&&hiddenUpdate.Topmost;hiddenUpdate.Close();
                overlay.ShowAfk();var afkUpdate=CompanionShell.NewUpdateWindow(offered,out _,out var afkTitle);afkUpdate.Show();await Task.Delay(80);
                checks["AFK mode update dialog stays visible while game minimized"]=overlay.IsAfk&&Native.IsIconic(handle)&&afkUpdate.IsVisible&&afkUpdate.Topmost&&!afkUpdate.ShowActivated&&afkTitle.Text.Contains("v0.9.9");afkUpdate.Close();
                overlay.Close();overlay=new(uiSettings);overlay.Update(view);await Task.Delay(150);
                Native.SetMinimized(target,true);await Task.Delay(150);SoftwareUpdate.RestoreUpdateWindows(decoded);await Task.Delay(250);
                checks["failed update safety restores only anchored game windows"]=!Native.IsIconic(handle);
                overlay.RestoreAfk();
                try{overlay.ResumeUpdateState(decoded with{Target=decoded.Target with{StartUtc=decoded.Target.StartUtc.AddDays(-1)},Windows=decoded.Windows.Select(w=>w with{StartUtc=w.StartUtc.AddDays(-1)}).ToArray()});checks["stale game process handoff never adopted"]=false;}catch(InvalidOperationException){checks["stale game process handoff never adopted"]=!overlay.IsAfk;}
                overlay.Update(view);await overlay.ToggleAfk();await Task.Delay(200);overlay.Close();overlay=null;await Task.Delay(300);
                checks["closing helper in AFK restores minimized game"]=!Native.IsIconic(handle);
                try{Native.SetMinimized(target with{StartUtc=target.StartUtc.AddDays(-1)},true);checks["PID start anchor mismatch refuses minimizing"]=false;}catch(InvalidOperationException){checks["PID start anchor mismatch refuses minimizing"]=!Native.IsIconic(handle);}
                exit=checks.Values.All(x=>x)?0:1;
            }
            catch(Exception e){Program.Save(output,"afk-error.json",Program.Error(e));}
            finally{overlay?.Close();Program.Save(output,"afk-result.json",new{Passed=exit==0,Cases=checks.Count,Checks=checks,Synthetic=true,Notice="Owned fixture windows and synthetic save deltas; not real game minimization or real wave coverage acceptance."});fixture.Close();app.Shutdown(exit);}
        };
        app.ShutdownMode=ShutdownMode.OnExplicitShutdown;app.Run(fixture);return exit;
    }
    internal static async Task<int> Probe(string output,string exe)
    {
        var snapshot=await SaveSource.Read(SaveSource.DefaultPath,CancellationToken.None);var target=Native.AutomaticWindow(exe);GameHudReading? hud=null;Box? bounds=null;
        using(var source=new GameSurfaceTracker())if(target is not null){for(var i=0;i<12;i++){bounds=source.Get(target);await Task.Delay(200);}hud=await Task.Run(()=>source.ReadHud(target)).WaitAsync(TimeSpan.FromSeconds(4));}
        Program.Save(output,"afk-readonly-probe.json",new{SavedAt=snapshot.SavedAt,Map=snapshot.Afk is{} d?$"{d.Act}-{d.Stage}":null,Heroes=snapshot.Afk?.Heroes.Select(h=>new{h.ClassId,h.Level,h.Xp}),Potions=snapshot.Afk?.Potions,Hud=hud,WaveRulesVerified=AfkWaveRules.MatchesExe(exe),SavedCompletedWaves=AfkWaveRules.Completed(snapshot.Afk),Surface=bounds,GameInputSent=false,GameWindowChanged=false});return 0;
    }
    internal static async Task<int> ReadPerformance(string output)
    {
        var path=SaveSource.DefaultPath;await SaveSource.Read(path,CancellationToken.None);
        var rows=new List<object>();SaveSnapshot? previous=null;string? hash=null;
        foreach(var cached in new[]{false,true,true,false})
        {
            var allocated=GC.GetTotalAllocatedBytes(true);using var process=Process.GetCurrentProcess();var cpu=process.TotalProcessorTime;var timer=Stopwatch.StartNew();
            for(var i=0;i<30;i++)
            {
                if(cached){var result=await SaveSource.ReadCached(path,CancellationToken.None,hash,previous);previous=result.Snapshot;hash=result.BytesHash;}
                else await SaveSource.Read(path,CancellationToken.None);
            }
            rows.Add(new{Cached=cached,Reads=30,ElapsedMs=timer.Elapsed.TotalMilliseconds,CpuMs=(process.TotalProcessorTime-cpu).TotalMilliseconds,AllocatedBytes=GC.GetTotalAllocatedBytes(true)-allocated});
        }
        Program.Save(output,"read-performance.json",new{Rows=rows,RealSave=true,GameInputSent=false,RawSaveWritten=false,Notice="Short repeated-read allocation benchmark, not live whole-process CPU or memory proof."});return 0;
    }
}

