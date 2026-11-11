using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Forms=System.Windows.Forms;

namespace Companion.Diagnostics;

internal static class CompanionShell
{
    public static int Run(Application app,string? exeOverride,string? saveOverride,string? dataOverride,int? seconds,bool synthetic,bool openWiki=false,bool startAfk=false,bool previewReminders=false,string? resumePlan=null)
    {
        AppResources.Validate();var settings=synthetic?new CompanionSettings{StorageDirectory=dataOverride}:CompanionSettings.Load();settings.Language="zh-Hans";AppResources.Language=settings.Language;
        if(exeOverride is not null)settings.GameExe=exeOverride;
        if(saveOverride is not null)settings.SavePath=saveOverride;
        var directory=dataOverride??CompanionSettings.DataDirectory;Directory.CreateDirectory(directory);
        if(synthetic&&(dataOverride is null||string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataOverride)),Path.TrimEndingDirectorySeparator(Path.GetFullPath(CompanionSettings.DataDirectory)),StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("Synthetic tests require a separate --data directory.");
        using var singleton=new Mutex(true,"Local\\QuestAwayCompanion-"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(directory)))[..20],out var created);
        if(!created)return 0;
        app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        var store=new SessionStore(Path.Combine(directory,"sessions.sqlite3"));
        var resume=resumePlan is null?null:SoftwareUpdate.ReadResume(resumePlan);bool resumeAttached=false,resumeConfirmed=false;
        var overlay=new OverlayWindow(settings);RuntimeView? latest=null;var failureCode=0;long lastRendered=-1;Forms.NotifyIcon? statusIcon=null;bool waitAnnounced=false;
        var runtime=new CompanionRuntime(settings,store,view=>app.Dispatcher.Invoke(()=>
        {
            latest=view;overlay.Update(view);
            if(resume is not null&&!resumeConfirmed&&view.Status is not ("storage_error" or "read_error" or "identity_changed"))
            {
                if(resume.Afk is{} afk)
                {
                    if(!resumeAttached&&view.State.Previous is not null&&view.Window is{} game&&game.Pid==afk.Target.Pid&&game.StartUtc==afk.Target.StartUtc)
                    {overlay.ResumeUpdateState(afk);resumeAttached=true;}
                    if(resumeAttached&&overlay.IsAfk&&Native.Follow(afk.Target) is{Minimized:true})
                    {SoftwareUpdate.ConfirmResume(resumePlan!);resumeConfirmed=true;}
                }
                else if(view.State.Previous is not null||view.Status=="waiting_game"){SoftwareUpdate.ConfirmResume(resumePlan!);resumeConfirmed=true;}
            }
            if(statusIcon is not null)
            {
                var tooltip="QATK 0.1 · "+AppResources.Text(view.Status);statusIcon.Text=tooltip.Length>63?tooltip[..63]:tooltip;
                if(view.Status=="waiting_game"&&!waitAnnounced){waitAnnounced=true;statusIcon.ShowBalloonTip(3000,"QATK 0.1",AppResources.Text("waiting_game"),Forms.ToolTipIcon.Info);}
            }
            if(dataOverride is not null)
            {
                var handle=new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
                File.AppendAllText(Path.Combine(directory,"ui-timeline.jsonl"),JsonSerializer.Serialize(new{AtUtc=DateTimeOffset.UtcNow,view.Status,
                    Visible=overlay.IsVisible,Target=view.Window,Flags=handle!=0?Native.OverlayFlags(handle):null,
                    Origin=overlay.IsVisible?new{X=overlay.PointToScreen(new Point(0,0)).X,Y=overlay.PointToScreen(new Point(0,0)).Y}:null,
                    OverlayWidth=overlay.ActualWidth,OverlayHeight=overlay.ActualHeight,view.State.ChestCount,view.State.OpenedChestCount,OpenedChestTotal=view.State.Previous?.OpenedChests,view.State.OpenedChestIncomplete,EquipmentCount=view.State.Equipment.Values.Sum(),view.Synthetic})+"\n");
                if(overlay.IsVisible&&view.State.Previous is not null&&view.State.Previous.SavedAt!=lastRendered)
                {
                    lastRendered=view.State.Previous.SavedAt;overlay.UpdateLayout();
                    var image=new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1,(int)overlay.ActualWidth),Math.Max(1,(int)overlay.ActualHeight),96,96,PixelFormats.Pbgra32);
                    image.Render(overlay);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                    using var file=File.Create(Path.Combine(directory,"overlay-"+lastRendered+".png"));encoder.Save(file);
                }
            }
        }),synthetic);
        using var trayIcon=AppIcon.CreateTrayIcon();
        using var tray=new Forms.NotifyIcon{Icon=trayIcon,Visible=true,Text="QATK · QuestAway Companion 0.1"};
        statusIcon=tray;
        var menu=new Forms.ContextMenuStrip();tray.ContextMenuStrip=menu;
        void Failure(Exception e)
        {
            failureCode=1;
            if(synthetic&&dataOverride is not null)File.AppendAllText(Path.Combine(directory,"test-errors.txt"),e.GetType().Name+" "+e.Message+"\n");
            else MessageBox.Show(AppResources.Text("error")+" · "+e.GetType().Name,AppResources.Text("app"),MessageBoxButton.OK,MessageBoxImage.Warning);
        }
        Window? settingsWindow=null,aboutWindow=null;
        Window? previewToast=null;
        WikiWindow? wikiWindow=null;
        using var updateLifetime=new CancellationTokenSource();bool checkingUpdate=false;string? offeredVersion=null,pendingUpdate=null;Window? updateWindow=null;AfkUpdateState? pendingAfk=null;
        using var presence=new OnlinePresence();
        async Task CheckUpdates(bool manual=false)
        {
            if(checkingUpdate){if(manual)updateWindow?.Activate();return;}checkingUpdate=true;
            try
            {
                var release=await SoftwareUpdate.Check(updateLifetime.Token);
                if(updateLifetime.IsCancellationRequested)return;
                if(release is null){if(manual)MessageBox.Show("暂无可用的正式更新。","QATK 0.1");return;}
                if(updateWindow is not null){if(manual)updateWindow.Activate();return;}
                if(!manual&&offeredVersion==release.Version)return;offeredVersion=release.Version;
                var window=NewUpdateWindow(release,out var install,out var title,out var progressBar,out var downloadStatus,out var later);updateWindow=window;
                using var downloadLifetime=CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token);
                var downloadToken=downloadLifetime.Token;
                window.Closed+=(_,_)=>{downloadLifetime.Cancel();updateWindow=null;};
                install.Click+=async (_,_)=>
                {
                    install.IsEnabled=false;install.Content="下载中…";later.Content="取消下载";downloadStatus.Text="正在连接下载…";
                    progressBar.Visibility=Visibility.Visible;progressBar.Value=0;
                    var progress=new Progress<UpdateProgress>(p=>{if(downloadToken.IsCancellationRequested)return;progressBar.Value=100.0*p.Received/p.Total;downloadStatus.Text=$"正在下载 {p.Received/1048576.0:F1} / {p.Total/1048576.0:F1} MB（{progressBar.Value:F0}%）";});
                    try
                    {
                        var stage=Path.Combine(SoftwareUpdate.UpdateRoot,Guid.NewGuid().ToString("N"));
                        var payload=await SoftwareUpdate.Download(release,stage,downloadToken,progress:progress);
                        downloadToken.ThrowIfCancellationRequested();pendingAfk=overlay.CaptureUpdateState();pendingUpdate=payload;runtime.StopForUpdate();window.Close();
                    }
                    catch(OperationCanceledException){ }
                    catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or InvalidDataException or BadImageFormatException)
                    {downloadStatus.Text="更新未完成，旧版仍可用。可重试或稍后更新。";install.IsEnabled=true;install.Content="重试更新";later.Content="稍后";}
                };
                window.Show();
                // Keep cancellation alive until this dialog actually closes, not only until Show returns.
                var closed=new TaskCompletionSource();window.Closed+=(_,_)=>closed.TrySetResult();await closed.Task;
            }
            catch(OperationCanceledException){ }
            catch(Exception e)when(e is IOException or InvalidDataException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
            {if(manual&&!updateLifetime.IsCancellationRequested)MessageBox.Show("暂时无法检查更新，当前版本可继续使用。","QATK 0.1");}
            finally{checkingUpdate=false;}
        }
        async Task WatchUpdates()
        {
            try{await Task.Delay(TimeSpan.FromSeconds(12),updateLifetime.Token);while(!updateLifetime.IsCancellationRequested){await CheckUpdates();await Task.Delay(TimeSpan.FromHours(6),updateLifetime.Token);}}catch(OperationCanceledException){ }
        }
        async Task WatchColossus()
        {
            ColossusNext? next=null;long nextFetch=0;
            try
            {
                while(!updateLifetime.IsCancellationRequested)
                {
                    if(!settings.ColossusReminderEnabled){next=null;nextFetch=0;settings.ColossusReminderStatus="";}
                    else
                    {
                        if(Environment.TickCount64>=nextFetch)
                        {
                            try{next=await ColossusReminder.Fetch(updateLifetime.Token);settings.ColossusReminderStatus=next is null?"暂无下场时间":ColossusReminder.Name(next.Type)+" · "+DateTimeOffset.FromUnixTimeMilliseconds(next.OpensAt).ToLocalTime().ToString("HH:mm");nextFetch=Environment.TickCount64+30000;}
                            catch(OperationCanceledException)when(updateLifetime.IsCancellationRequested){break;}
                            catch(Exception e)when(e is IOException or InvalidDataException or System.Net.Http.HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or OverflowException or OperationCanceledException)
                            {next=null;nextFetch=Environment.TickCount64+60000;settings.ColossusReminderStatus="时间暂不可用";}
                        }
                        if(settings.ColossusReminderEnabled&&ColossusReminder.Due(next,settings.ColossusReminderMinutes,Environment.TickCount64,settings.ColossusReminderSent))
                        {
                            var toast=ColossusReminder.Toast(next!,ColossusReminder.RemainingSeconds(next,Environment.TickCount64)!.Value);toast.Show();
                            settings.ColossusReminderSent=settings.ColossusReminderSent.Append(next!.Key).TakeLast(32).ToArray();
                            try{settings.Save();}catch(Exception e)when(e is IOException or UnauthorizedAccessException){settings.ColossusReminderStatus="提醒已显示，去重记录未保存";}
                        }
                    }
                    await Task.Delay(1000,updateLifetime.Token);
                }
            }
            catch(OperationCanceledException){ }
        }
        void PreviewPotion(){if(overlay.IsVisible)overlay.PreviewPotionWarning();else MessageBox.Show("连接游戏后可预览产出位置的提醒。","QATK 0.1");}
        void PreviewColossus()
        {
            previewToast?.Close();var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var toast=ColossusReminder.Toast(new("PREVIEW",8,now+settings.ColossusReminderMinutes*60000,now,Environment.TickCount64),settings.ColossusReminderMinutes*60,true);previewToast=toast;
            toast.Closed+=(_,_)=>{if(ReferenceEquals(previewToast,toast))previewToast=null;};toast.Show();
        }
        void About()
        {
            if(aboutWindow is not null){aboutWindow.WindowState=WindowState.Normal;aboutWindow.Show();aboutWindow.Activate();return;}
            aboutWindow=NewAboutWindow(async()=>await CheckUpdates(true));aboutWindow.Closed+=(_,_)=>aboutWindow=null;aboutWindow.Show();
        }
        void Settings()
        {
            if(settingsWindow is not null){settingsWindow.WindowState=WindowState.Normal;settingsWindow.Show();settingsWindow.Activate();return;}
            var window=NewSettingsWindow();settingsWindow=window;
            window.Closed+=(_,_)=>settingsWindow=null;
            BuildSettings(window,settings,()=>{overlay.ApplySettings();wikiWindow?.RefreshPlacement();},settings.Save,Failure,
                PreviewPotion,PreviewColossus,showAbout:About);
            window.Show();window.Activate();
        }
        void History()
        {
            try
            {
                var window=BaseWindow(AppResources.Text("history"),650,470);var body=new DockPanel{Margin=new Thickness(16)};window.Content=body;
                var buttons=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(buttons,Dock.Top);body.Children.Add(buttons);
                var text=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Background=new SolidColorBrush(Color.FromRgb(45,31,20)),Foreground=Brushes.Ivory};
                void Refresh()=>text.Text=string.Join("\n\n",store.History().Select(s=>
                {
                    var state=JsonSerializer.Deserialize<StatisticsState>(s.StateJson,Program.Json);var known=state?.Previous is not null;
                    var pure=StatisticsEngine.IsChestScope(state?.EquipmentScope);
                    var available=StatisticsEngine.IsTrackedScope(state?.EquipmentScope)?state!.EquipmentAvailable&&!state.HasGaps:known;
                    var equipment=available?s.EquipmentCount.ToString():s.EquipmentCount>0?"≥"+s.EquipmentCount:AppResources.Text("unknown");
                    var label=StatisticsEngine.IsProductionScope(state?.EquipmentScope)?"production_scope_history":state?.EquipmentScope is StatisticsEngine.OrdinaryOnlyScope or StatisticsEngine.FarmingScope?"ordinary_scope_history":pure?"chest_equipment":"legacy_counter";
                    var sources=StatisticsEngine.SourceCounts(state);
                    var sourceText=state?.EquipmentScope==StatisticsEngine.ChestOnlyScope&&sources is not null&&(available||s.EquipmentCount>0)?
                        $" · {AppResources.Text("ordinary")}: {(available||sources["ordinary"]>0?sources["ordinary"].ToString():"—")} / {AppResources.Text("colossus")}: {(available||sources["colossus"]>0?sources["colossus"].ToString():"—")}":"";
                    var openingsKnown=state?.OpenedChestAvailable==true;
                    var openings=openingsKnown&&state!.OpenedChestIncomplete==false?state.OpenedChestCount?.ToString()??AppResources.Text("unknown"):state?.OpenedChestCount>0?"≥"+state.OpenedChestCount:AppResources.Text("unknown");
                    var qualities=StatisticsEngine.IsTrackedScope(state?.EquipmentScope)?string.Join(" · ",SaveSource.Qualities.Select(q=>AppResources.Text(q)+": "+(available?state!.Equipment[q].ToString():state!.Equipment[q]>0?"≥"+state.Equipment[q]:"—"))):AppResources.Text("historical_qualities_unknown");
                    return $"{s.StartTime.ToLocalTime():yyyy-MM-dd HH:mm:ss} → {(s.EndTime is null?"…":s.EndTime.Value.ToLocalTime().ToString("HH:mm:ss"))}\n{AppResources.Text("opened_total")}: {(openingsKnown?state!.Previous?.OpenedChests?.ToString():AppResources.Text("unknown"))} · {AppResources.Text("opened_session")}: {openings}\n{AppResources.Text(label)}: {equipment}"+sourceText+(state?.HasGaps==true?" · "+AppResources.Text("partial"):"")+"\n"+qualities+(state?.OpeningCounterScope is null?"\n"+AppResources.Text("opened_legacy"):state.OpenedChestIncomplete?"\n"+AppResources.Text("opened_rebaseline"):"");
                }));
                var export=new Button{Content=AppResources.Text("export"),Margin=new Thickness(0,0,8,8),Padding=new Thickness(8)};buttons.Children.Add(export);
                export.Click+=(_,_)=>{try{var picker=new SaveFileDialog{Filter="JSON|*.json",FileName="QuestAway-Companion-0.1-history.json"};if(picker.ShowDialog(window)==true)store.Export(picker.FileName);}catch(Exception e){Failure(e);}};
                var clear=new Button{Content=AppResources.Text("clear"),Margin=new Thickness(0,0,8,8),Padding=new Thickness(8)};buttons.Children.Add(clear);
                clear.Click+=(_,_)=>{if(MessageBox.Show(AppResources.Text("confirm_clear"),AppResources.Text("app"),MessageBoxButton.YesNo)==MessageBoxResult.Yes){try{store.ClearEnded();Refresh();}catch(Exception e){Failure(e);}}};
                body.Children.Add(text);Refresh();window.Show();
            }
            catch(Exception e){Failure(e);}
        }
        void Wiki()
        {
            if(wikiWindow is null)
            {
                wikiWindow=new WikiWindow{AdditionalExclusions=()=>overlay.WikiExclusions,PreferredSideProvider=()=>overlay.WikiPreferredSide};
                if(overlay.IsVisible&&!wikiWindow.PlaceNear(overlay)){wikiWindow.Close();wikiWindow=null;return;}
                wikiWindow.Closed+=(_,_)=>wikiWindow=null;wikiWindow.Show();
            }
            else{if(wikiWindow.WindowState==WindowState.Minimized)wikiWindow.WindowState=WindowState.Normal;wikiWindow.ShowSearch();wikiWindow.Activate();}
        }
        var trayActions=new Dictionary<string,Action>();
        trayActions["show"]=overlay.ToggleVisible;trayActions["wiki"]=Wiki;trayActions["settings"]=Settings;trayActions["history"]=History;trayActions["exit"]=runtime.Stop;trayActions["about"]=About;
        trayActions["pause"]=()=>{runtime.TogglePause();BuildMenu();};trayActions["interaction"]=()=>{settings.ClickThrough=!settings.ClickThrough;settings.Save();overlay.ApplySettings();};
        trayActions["update"]=async()=>await CheckUpdates(true);trayActions["afk"]=async()=>{try{await overlay.ToggleAfk();}catch(Exception e){Failure(e);}};
        void BuildMenu()=>FillTrayMenu(menu,key=>{try{trayActions[key]();}catch(Exception e){Failure(e);}},overlay.IsAfk);
        menu.Opening+=(_,_)=>BuildMenu();
        overlay.WikiRequested+=()=>{if(Environment.TickCount64-WikiWindow.LastOutsideDismissalTick>=250)Wiki();};
        overlay.SettingsRequested+=Settings;
        overlay.AfkModeChanged+=active=>runtime.LowResourceMode=active;
        BuildMenu();tray.DoubleClick+=(_,_)=>app.Dispatcher.Invoke(()=>{if(overlay.IsAfk)overlay.ShowAfk();else Settings();});
        app.Startup+=async (_,_)=>
        {
            if(openWiki)Wiki();
            runtime.Start();
            // Diagnostic/synthetic sessions must never pollute the real online count.
            if(!synthetic&&dataOverride is null&&seconds is null)presence.Start();
            async Task EnterAfkWhenReady()
            {
                try{for(var i=0;i<120&&!updateLifetime.IsCancellationRequested;i++){if(overlay.IsVisible){if(startAfk)await overlay.ToggleAfk();if(previewReminders){await Task.Delay(500,updateLifetime.Token);PreviewPotion();PreviewColossus();}return;}await Task.Delay(250,updateLifetime.Token);}}
                catch(OperationCanceledException){ }
                catch(Exception e){if(!updateLifetime.IsCancellationRequested)Failure(e);}
            }
            if(startAfk||previewReminders)_=EnterAfkWhenReady();
            if(!synthetic){_=WatchUpdates();_=WatchColossus();}
            if(seconds is not null)_=Task.Run(async()=>{await Task.Delay(TimeSpan.FromSeconds(seconds.Value));runtime.Stop();});
            try{await runtime.Completion!;}
            catch(Exception e){Failure(e);}
            finally
            {
                updateLifetime.Cancel();updateWindow?.Close();aboutWindow?.Close();
                await presence.Stop();
                tray.Visible=false;overlay.Close();
                if(dataOverride is not null&&latest is not null)Program.Save(directory,"last-view.json",latest);
                if(pendingUpdate is not null)
                {
                    try{if(!runtime.UpdateRestartSafe)throw new IOException("session_save_not_confirmed");SoftwareUpdate.LaunchInstaller(pendingUpdate,pendingAfk);}
                    catch(Exception e){Failure(e);}
                }
                app.Shutdown();
            }
        };
        app.SessionEnding+=(_,_)=>runtime.Stop();
        app.Run();menu.Dispose();return failureCode;
    }
    internal static void FillTrayMenu(Forms.ContextMenuStrip menu,Action<string> selected,bool afk)
    {
        foreach(var item in menu.Items.Cast<Forms.ToolStripItem>().ToArray())item.Dispose();menu.Items.Clear();
        Forms.ToolStripMenuItem Item(string text,string key){var item=new Forms.ToolStripMenuItem(text){Tag=key};item.Click+=(_,_)=>selected(key);return item;}
        menu.Items.Add(Item("显示 / 隐藏","show"));menu.Items.Add(Item(afk?"恢复游戏":"挂机模式","afk"));menu.Items.Add(Item("设置","settings"));menu.Items.Add(Item("检查更新","update"));menu.Items.Add(Item("关于","about"));menu.Items.Add(Item("退出","exit"));
    }
    static Window BaseWindow(string title,int width,int height)=>new(){Title="QuestAway Companion 0.1 · "+title,Icon=AppIcon.Image,Width=width,Height=height,
        Background=new SolidColorBrush(Color.FromRgb(43,29,20)),Foreground=Brushes.Ivory,WindowStartupLocation=WindowStartupLocation.CenterScreen};
    internal static Window NewSettingsWindow()
    {
        var window=BaseWindow(AppResources.Text("settings"),440,475);window.Topmost=true;
        window.WindowStyle=WindowStyle.None;window.AllowsTransparency=true;window.ResizeMode=ResizeMode.NoResize;return window;
    }
    internal static Window NewAboutWindow(Action? checkUpdates=null)
    {
        var window=NewSettingsWindow();window.Title="QATK · 关于";window.Width=350;window.Height=235;
        var body=new StackPanel{Margin=new Thickness(6)};
        body.Children.Add(new TextBlock{Text="QATK",FontSize=24,FontWeight=FontWeights.Bold,Foreground=WikiUi.Brush("#efd08b"),Margin=new Thickness(0,0,0,8)});
        body.Children.Add(new TextBlock{Text="当前版本：v"+SoftwareUpdate.DisplayVersion,FontSize=13,Foreground=WikiUi.Brush("#ead7ab"),Margin=new Thickness(0,0,0,8)});
        body.Children.Add(new TextBlock{Text="QuestAway 中文桌面助手\n装备产出统计 · 挂机查看 · 离线 wiki",FontSize=12,Foreground=WikiUi.Brush("#cbb181"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,14)});
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};var update=WikiUi.Button("检查更新");update.Tag="about-update";update.IsEnabled=checkUpdates is not null;update.Click+=(_,_)=>checkUpdates?.Invoke();buttons.Children.Add(update);
        var close=WikiUi.Button("关闭");close.Margin=new Thickness(8,0,0,0);close.Click+=(_,_)=>window.Close();buttons.Children.Add(close);body.Children.Add(buttons);
        WikiUi.Frame(window,"关于",body);return window;
    }
    internal static Window NewUpdateWindow(UpdateRelease release,out Button install,out TextBlock title)
        =>NewUpdateWindow(release,out install,out title,out _,out _,out _);
    internal static Window NewUpdateWindow(UpdateRelease release,out Button install,out TextBlock title,out ProgressBar progress,out TextBlock status,out Button later)
    {
        var window=NewSettingsWindow();window.Width=470;window.Height=440;window.ShowActivated=false;
        var body=new DockPanel{Margin=new Thickness(6)};var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(actions,Dock.Bottom);body.Children.Add(actions);
        later=WikiUi.Button("稍后");install=WikiUi.Button("立即更新");install.Margin=new Thickness(8,0,0,0);actions.Children.Add(later);actions.Children.Add(install);later.Click+=(_,_)=>window.Close();
        var download=new StackPanel{Margin=new Thickness(0,8,0,8)};DockPanel.SetDock(download,Dock.Bottom);body.Children.Add(download);
        status=new TextBlock{Text="确认后下载；关闭此窗口可取消。",TextWrapping=TextWrapping.Wrap,Foreground=WikiUi.Brush("#d6b879")};
        progress=new ProgressBar{Minimum=0,Maximum=100,Height=8,Margin=new Thickness(0,6,0,0),Visibility=Visibility.Collapsed,Foreground=WikiUi.Brush("#c39b48"),Background=WikiUi.Brush("#24180f")};download.Children.Add(status);download.Children.Add(progress);
        var currentVersion=SoftwareUpdate.DisplayVersion;
        title=new TextBlock{Text="当前版本："+currentVersion+"  →  新版本："+release.Version+"\n更新内容",Foreground=WikiUi.Brush("#e8ca8e"),Margin=new Thickness(0,0,0,12)};DockPanel.SetDock(title,Dock.Top);body.Children.Add(title);
        body.Children.Add(new TextBox{Text=release.Notes,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Background=Brushes.Transparent,Foreground=Brushes.Ivory,BorderThickness=new Thickness(0)});
        WikiUi.Frame(window,"发现新版本",body);return window;
    }
    internal static OpenFileDialog ConnectionDialog(bool game,string? current)
    {
        var dialog=new OpenFileDialog{Title=game?"选择 QuestAway 游戏程序":"选择游戏存档",Filter=game?"游戏程序 (*.exe)|*.exe":"游戏存档 (*.json)|*.json",CheckFileExists=true,CheckPathExists=true,Multiselect=false,RestoreDirectory=true};
        if(!string.IsNullOrWhiteSpace(current)&&Path.IsPathFullyQualified(current)&&File.Exists(current)){dialog.InitialDirectory=Path.GetDirectoryName(current);dialog.FileName=Path.GetFileName(current);}
        return dialog;
    }
    internal static void BuildSettings(Window window,CompanionSettings settings,Action apply,Action persist,Action<Exception> failure,Action? previewPotion=null,Action? previewColossus=null,Func<bool,string?,string?>? selectConnectionFile=null,Func<string?>? locateGame=null,Action? showAbout=null)
    {
        window.Height=430;
        var body=new StackPanel{Margin=new Thickness(5)};
        var scroll=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};WikiUi.Frame(window,"设置",scroll);
        var note=new TextBlock{Visibility=Visibility.Collapsed,Foreground=WikiUi.Brush("#d6b879"),Margin=new Thickness(0,0,0,8)};body.Children.Add(note);
        var connections=new StackPanel();
        void Label(string text,Panel? parent=null)=>(parent??body).Children.Add(new TextBlock{Text=text,Foreground=WikiUi.Brush("#e5cca0"),Margin=new Thickness(0,9,0,5)});
        var pending=false;var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(300)};
        void Flush(){timer.Stop();if(!pending)return;try{persist();pending=false;note.Visibility=Visibility.Collapsed;}catch(Exception e)when(e is IOException or UnauthorizedAccessException){note.Text="保存失败";note.Visibility=Visibility.Visible;failure(e);}}
        void Changed(){apply();pending=true;timer.Stop();timer.Start();}
        timer.Tick+=(_,_)=>Flush();window.Closed+=(_,_)=>Flush();
        void Connection(string label,bool game)
        {
            Label(label,connections);
            var field=new TextBox{IsReadOnly=true,Tag=game?"game-path":"save-path",Padding=new Thickness(8,5,8,5),Background=WikiUi.Brush("#24180f"),Foreground=WikiUi.Brush("#f4e5c9"),BorderBrush=WikiUi.Brush("#886737"),BorderThickness=new Thickness(1)};connections.Children.Add(field);
            var row=new DockPanel{Margin=new Thickness(0,5,0,1)};var browse=WikiUi.Button("浏览文件");browse.Tag=game?"browse-game":"browse-save";browse.Margin=new Thickness(5,0,0,0);DockPanel.SetDock(browse,Dock.Right);row.Children.Add(browse);
            var automatic=WikiUi.Button("重新查找");automatic.Tag=game?"auto-game":"auto-save";automatic.Margin=new Thickness(5,0,0,0);DockPanel.SetDock(automatic,Dock.Right);row.Children.Add(automatic);
            var state=new TextBlock{FontSize=11,Foreground=WikiUi.Brush("#d6b879"),TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};row.Children.Add(state);connections.Children.Add(row);
            string? resolved=null;
            string Configured()=>game?settings.GameExe:settings.SavePath;
            void Refresh()
            {
                var manual=!string.IsNullOrWhiteSpace(Configured());bool searchFailed=false;
                try{resolved=manual?Configured():game?(locateGame is null?GameDiscovery.FindExe(""):locateGame()):SaveSource.DefaultPath;}
                catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception){resolved=null;searchFailed=true;}
                var found=resolved is not null&&File.Exists(resolved);field.Text=found?resolved!:manual?Configured():"尚未找到文件";field.ToolTip=found?resolved:null;
                state.Text=manual?(found?"手动选择":"所选文件已不存在"):searchFailed?"自动查找未完成，请重试":found?"自动查找 · 已找到":game?"等待游戏，可重新查找":"尚未找到存档，可重新查找";
                automatic.Content=manual?"自动查找":"重新查找";
            }
            void Set(string path){if(Configured()==path)return;if(game)settings.GameExe=path;else settings.SavePath=path;Changed();}
            browse.Click+=(_,_)=>
            {
                try
                {
                    string? path;if(selectConnectionFile is not null)path=selectConnectionFile(game,resolved);else{var dialog=ConnectionDialog(game,resolved);path=dialog.ShowDialog(window)==true?dialog.FileName:null;}
                    if(path is null)return;
                    if(!Path.IsPathFullyQualified(path)||!File.Exists(path)||!Path.GetExtension(path).Equals(game?".exe":".json",StringComparison.OrdinalIgnoreCase)){note.Text="请选择有效的"+(game?"游戏程序":"游戏存档");note.Visibility=Visibility.Visible;return;}
                    Set(path);Refresh();
                }
                catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception){note.Text="未能打开文件选择，请重试";note.Visibility=Visibility.Visible;}
            };
            automatic.Click+=(_,_)=>{Set("");Refresh();};Refresh();
        }
        Connection("游戏程序",true);Connection("游戏存档",false);
        void SliderRow(string label,double minimum,double maximum,double value,Action<double> change)
        {
            var header=new TextBlock{Foreground=WikiUi.Brush("#e5cca0"),Margin=new Thickness(0,12,0,5)};body.Children.Add(header);
            void Caption(double v)=>header.Text=label+"  "+(v*100).ToString("0")+"%";Caption(value);
            var slider=new Slider{Minimum=minimum,Maximum=maximum,Value=value,Foreground=WikiUi.Brush("#b88c44"),Background=WikiUi.Brush("#5c4027"),Margin=new Thickness(0,2,0,2)};
            body.Children.Add(slider);slider.ValueChanged+=(_,_)=>{Caption(slider.Value);change(slider.Value);Changed();};
        }
        SliderRow("透明度",.3,1,settings.Opacity,v=>settings.Opacity=v);
        SliderRow("缩放",.65,2,settings.Scale,v=>settings.Scale=v);
        Label("统计框位置");var row=new System.Windows.Controls.Primitives.UniformGrid{Columns=4};body.Children.Add(row);
        var buttons=new List<Button>();var labels=new[]{"右侧","左侧","上方","下方"};
        void Highlight(){for(var i=0;i<buttons.Count;i++)buttons[i].Background=WikiUi.Brush(settings.DockSide==WikiPopupPlacement.Sides[i]?"#98703a":"#49301c");}
        for(var i=0;i<labels.Length;i++)
        {
            var side=WikiPopupPlacement.Sides[i];var button=WikiUi.Button(labels[i]);button.Margin=new Thickness(0,0,5,0);buttons.Add(button);row.Children.Add(button);
            button.Click+=(_,_)=>{settings.DockSide=side;Highlight();Changed();};
        }
        Highlight();row.ToolTip="Wiki 默认向上，下方停靠时向下；边缘自动换侧。";
        void ReminderRow(CheckBox checkbox,Action? preview,string tag){var row=new DockPanel();var button=WikiUi.Button("测试");button.Tag=tag;button.FontSize=10;button.Padding=new Thickness(7,1,7,1);button.Height=22;button.Margin=new Thickness(0,6,0,0);button.IsEnabled=preview is not null;button.ToolTip="仅预览，不更改统计或正式提醒记录";button.Click+=(_,_)=>preview?.Invoke();DockPanel.SetDock(button,Dock.Right);row.Children.Add(button);row.Children.Add(checkbox);body.Children.Add(row);}
        var potionReminder=new CheckBox{Content="补药水提醒",IsChecked=settings.PotionReminderEnabled,Foreground=WikiUi.Brush("#ecd29e"),Margin=new Thickness(0,10,0,3)};ReminderRow(potionReminder,previewPotion,"test-potion");
        potionReminder.ToolTip="新鲜存档确认无生效药水时，在装备产出旁显示红字。";
        potionReminder.Checked+=(_,_)=>{settings.PotionReminderEnabled=true;Changed();};potionReminder.Unchecked+=(_,_)=>{settings.PotionReminderEnabled=false;Changed();};
        var remind=new CheckBox{Content="巨像提醒",IsChecked=settings.ColossusReminderEnabled,Foreground=WikiUi.Brush("#ecd29e"),Margin=new Thickness(0,8,0,7)};ReminderRow(remind,previewColossus,"test-colossus");
        var reminderChoices=new StackPanel{Orientation=Orientation.Horizontal,IsEnabled=settings.ColossusReminderEnabled};body.Children.Add(reminderChoices);
        foreach(var minutes in new[]{1,5})
        {
            var option=new RadioButton{Content="提前 "+minutes+" 分钟",GroupName="QATK-Colossus",IsChecked=settings.ColossusReminderMinutes==minutes,Foreground=WikiUi.Brush("#d8bf93"),Margin=new Thickness(0,0,20,0)};
            option.Checked+=(_,_)=>{settings.ColossusReminderMinutes=minutes;Changed();};reminderChoices.Children.Add(option);
        }
        void ReminderChanged(){settings.ColossusReminderEnabled=remind.IsChecked==true;reminderChoices.IsEnabled=settings.ColossusReminderEnabled;Changed();}
        remind.Checked+=(_,_)=>ReminderChanged();remind.Unchecked+=(_,_)=>ReminderChanged();
        var reminderStatus=new TextBlock{Text=settings.ColossusReminderStatus,FontSize=10,Foreground=WikiUi.Brush("#bca477"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,7,0,0)};body.Children.Add(reminderStatus);
        remind.ToolTip="仅在 QATK 运行时读取官方公开场次；提醒不抢焦点，不操作游戏。";
        void RefreshStatus(){reminderStatus.Text=settings.ColossusReminderStatus;reminderStatus.Visibility=string.IsNullOrWhiteSpace(reminderStatus.Text)?Visibility.Collapsed:Visibility.Visible;}
        RefreshStatus();var statusTimer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};statusTimer.Tick+=(_,_)=>RefreshStatus();statusTimer.Start();window.Closed+=(_,_)=>statusTimer.Stop();
        var connectionSection=new Expander{Header="连接位置",ToolTip="默认自动查找，也可浏览文件手动选择",IsExpanded=false,Content=connections,Foreground=WikiUi.Brush("#bca477"),Margin=new Thickness(0,12,0,8)};
        connectionSection.Expanded+=(_,_)=>window.Dispatcher.BeginInvoke(new Action(()=>scroll.ScrollToEnd()),System.Windows.Threading.DispatcherPriority.Loaded);body.Children.Add(connectionSection);
        var bottom=new DockPanel();var about=WikiUi.Button("关于 · v"+SoftwareUpdate.DisplayVersion);about.Tag="settings-about";about.IsEnabled=showAbout is not null;about.Click+=(_,_)=>showAbout?.Invoke();DockPanel.SetDock(about,Dock.Left);bottom.Children.Add(about);
        var done=WikiUi.Button("完成");done.HorizontalAlignment=HorizontalAlignment.Right;done.Click+=(_,_)=>window.Close();bottom.Children.Add(done);body.Children.Add(bottom);
    }
}


