using System.Diagnostics;
using System.Text.Json;

namespace Companion.Diagnostics;

internal record RuntimeView(string Status,StatisticsState State,DateTimeOffset StartTime,GameWindow? Window,bool Synthetic)
{public AfkTelemetry? Afk{get;init;}public bool WaveRulesVerified{get;init;}public ProcessObservation[] Candidates{get;init;}=[];}
internal record GameConnectionChoice(ProcessObservation Process,string SavePath);

internal sealed class CompanionRuntime
{
    readonly CompanionSettings settings;readonly SessionStore store;readonly Action<RuntimeView> update;
    readonly CancellationTokenSource stop=new();readonly bool synthetic;
    public bool Paused{get;private set;}
    bool updating;
    public bool UpdateRestartSafe{get;private set;}
    bool resumeBaseline;string? sessionId;StatisticsEngine stats=new();
    readonly AfkTracker afk=new();
    bool waveRulesVerified;
    readonly AfkReadCache saveCache=new();volatile bool lowResourceMode;
    GameConnectionChoice? requested;string? selectedSave;GameConnectionChoice? activeConnection;
    internal GameConnectionChoice? Connection=>Volatile.Read(ref activeConnection);
    internal void SelectGame(GameConnectionChoice choice)=>Interlocked.Exchange(ref requested,choice);
    internal static ProcessObservation[] ConnectionCandidates(ProcessObservation[] observed,string configured)=>observed.Where(p=>
        p.Error is null&&p.StartUtc is not null&&!string.IsNullOrWhiteSpace(p.Exe)&&
        (string.IsNullOrWhiteSpace(configured)||string.Equals(p.Exe,configured,StringComparison.OrdinalIgnoreCase))).ToArray();
    internal static bool MatchesChoice(ProcessObservation process,GameConnectionChoice choice)=>
        process.Error is null&&process.StartUtc is not null&&process.Pid==choice.Process.Pid&&process.StartUtc==choice.Process.StartUtc&&
        string.Equals(process.Exe,choice.Process.Exe,StringComparison.OrdinalIgnoreCase)&&Path.IsPathFullyQualified(choice.SavePath)&&File.Exists(choice.SavePath);
    public bool LowResourceMode{get=>lowResourceMode;set=>lowResourceMode=value;}
    public Task? Completion{get;private set;}
    public CompanionRuntime(CompanionSettings settings,SessionStore store,Action<RuntimeView> update,bool synthetic=false)
    {this.settings=settings;this.store=store;this.update=update;this.synthetic=synthetic;stats=new(null,false,false,!synthetic);}
    public void Start()=>Completion=Task.Run(Loop);
    public void TogglePause(){Paused=!Paused;if(!Paused)resumeBaseline=true;}
    public void Stop()=>stop.Cancel();
    public void StopForUpdate(){updating=true;stop.Cancel();}
    async Task Loop()
    {
        ProcessObservation? game=null;GameWindow? lastWindow=null;var start=DateTimeOffset.UtcNow;var status="waiting_game";SampleDelta? pending=null;bool dirty=false;bool selectionRequired=false;
        ActivityEvent[] pendingActivity=[];Task<ActivitySnapshot>? activityRead=null;string? activitySession=null;
        var nextActivity=DateTimeOffset.MinValue;var activityStarted=DateTimeOffset.MinValue;int epoch=0,readEpoch=0;
        void Persist(){if(!dirty)return;store.Save(sessionId!,stats,pending,pendingActivity);dirty=false;pending=null;pendingActivity=[];}
        void SourceUnavailable(){if(sessionId is not null){stats.SaveUnavailable();dirty=true;}}
        try
        {
            while(!stop.IsCancellationRequested)
            {
                GameWindow? window=null;
                ProcessObservation[] candidates=[];
                try
                {
                    var exe=game?.Exe??GameDiscovery.FindExe(settings.GameExe);
                    var observed=Native.Processes(exe??"QuestAway.exe");
                    var processes=ConnectionCandidates(observed,game?.Exe??settings.GameExe);
                    if(game is null)
                    {
                        if(processes.Length>1)selectionRequired=true;
                        candidates=processes;
                        ProcessObservation? candidate=null;
                        var choice=Volatile.Read(ref requested);
                        if(choice is not null)
                        {
                            selectionRequired=true;
                            candidate=processes.FirstOrDefault(p=>MatchesChoice(p,choice));
                            if(candidate is null)Interlocked.CompareExchange(ref requested,null,choice);
                            else selectedSave=choice.SavePath;
                        }
                        else if(!selectionRequired&&processes.Length==1)candidate=processes[0];
                        if(candidate is null)status=processes.Length==0?"waiting_game":"multiple_game";
                        else
                        {
                        var key=$"{(synthetic?"synthetic":"real")}:{candidate.Pid}:{candidate.StartUtc!.Value.Ticks}";
                        var session=store.Begin(key,false,false,!synthetic);sessionId=session.SessionId;start=session.StartTime;
                        afk.Reset();
                        waveRulesVerified=AfkWaveRules.MatchesExe(candidate.Exe!);
                        var restored=JsonSerializer.Deserialize<StatisticsState>(session.StateJson,Program.Json);
                        stats=new(restored,false,false,!synthetic);
                        // A persisted sampling session survived an unobserved process interruption.
                        if(!synthetic&&restored?.Previous is not null){stats.SaveUnavailable();dirty=true;}
                        game=candidate;
                        Volatile.Write(ref activeConnection,new GameConnectionChoice(candidate,SavePath()));
                        candidates=[];
                        }
                    }
                    if(game is not null)
                    {
                        Persist();
                        if(observed.Any(p=>p.Pid==game.Pid&&p.Error is not null))throw new IOException("process_metadata_unavailable");
                        var alive=processes.Any(p=>p.Pid==game.Pid&&p.StartUtc==game.StartUtc);
                        if(!alive)
                        {
                            // Never merge a fast restart's new process into this session.
                            if(processes.Length==0&&!Paused)
                            {
                                try{var last=await SaveSource.Read(SavePath(),CancellationToken.None);pending=stats.Push(last);dirty=true;}catch(Exception e)when(e is IOException or JsonException or InvalidOperationException or KeyNotFoundException){SourceUnavailable();Log("final_read_unavailable",e);}
                            }
                            else if(processes.Length>0)SourceUnavailable(); // Another writer may still own the shared default save; do not guess a final delta.
                            Persist();
                            store.End(sessionId!,"game_exited");return;
                        }
                        window=LowResourceMode&&lastWindow?.Pid==game.Pid&&lastWindow.StartUtc==game.StartUtc?Native.Follow(lastWindow):Native.AutomaticWindow(game.Exe!,game);
                        if(window is not null)lastWindow=window;
                        if(Paused){status="paused";afk.Reset();}
                        else
                        {
                            var snapshot=await saveCache.Read(SavePath(),LowResourceMode,stop.Token);
                            if(stats.Previous is null&&snapshot.SavedAt<new DateTimeOffset(game.StartUtc!.Value).ToUnixTimeMilliseconds())status="waiting_save";
                            else
                            {
                                var before=(stats.Previous?.Fingerprint,stats.EquipmentAvailable,stats.OpenedChestAvailable);
                                if(resumeBaseline)afk.Reset();afk.Push(snapshot);
                                if(resumeBaseline){stats.Rebaseline(snapshot);stats.ResetActivityBaseline();epoch++;resumeBaseline=false;dirty=true;}
                                pending=stats.Push(snapshot);
                                if(stats.Status is "counter_reset" or "save_rollback"){stats.ResetActivityBaseline();epoch++;}
                                if(before!=(stats.Previous?.Fingerprint,stats.EquipmentAvailable,stats.OpenedChestAvailable))dirty=true;
                                status=stats.Status=="waiting_save"?"waiting_save_update":stats.Status;
                                if(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-snapshot.SavedAt>75000)status="stale_save";
                                if(stats.ChestOnly)
                                {
                                    if(activityRead?.IsCompleted==true)
                                    {
                                        try
                                        {
                                            if(activitySession==sessionId&&readEpoch==epoch)
                                            {
                                                var reference=await activityRead;
                                                var age=reference.LatestMinute is null?TimeSpan.MaxValue:DateTime.Now.TimeOfDay-TimeSpan.Parse(reference.LatestMinute);
                                                if(age<TimeSpan.Zero||age>TimeSpan.FromMinutes(3))stats.ActivityUnavailable("activity_reference_stale");
                                                else pendingActivity=stats.PushActivity(reference,sessionId!);
                                                dirty=true;
                                            }
                                        }
                                        catch(Exception e){stats.ActivityUnavailable("activity_unavailable");dirty=true;Log("activity_unavailable",e);}
                                        finally{activityRead=null;nextActivity=DateTimeOffset.UtcNow.AddSeconds(2);}
                                    }
                                    if(activityRead is not null&&DateTimeOffset.UtcNow-activityStarted>TimeSpan.FromSeconds(3)&&stats.EquipmentAvailable)
                                    {stats.ActivityUnavailable("activity_read_pending");dirty=true;}
                                    if(activityRead is null&&DateTimeOffset.UtcNow>=nextActivity)
                                    {
                                        var target=game;activitySession=sessionId;readEpoch=epoch;activityStarted=DateTimeOffset.UtcNow;
                                        activityRead=Task.Run(()=>GameActivitySource.Read(target,AppResources.ActivityQualityLabels));
                                    }
                                    if(!stats.EquipmentAvailable)status="activity_unavailable";
                                }
                                Persist();
                            }
                        }
                    }
                }
                catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
                catch(InvalidDataException e)when(e.Message=="identity_changed")
                {
                    if(sessionId is not null)store.End(sessionId,"identity_changed");sessionId=null;game=null;selectedSave=null;Interlocked.Exchange(ref requested,null);Volatile.Write(ref activeConnection,null);stats=new(null,false,false,!synthetic);afk.Reset();epoch++;status="identity_changed";
                }
                catch(InvalidDataException e)when(e.Message=="unsupported_save_version"){status="unsupported_version";SourceUnavailable();Log(status,e);}
                catch(InvalidDataException e){status="read_error";SourceUnavailable();Log(status,e);}
                catch(Microsoft.Data.Sqlite.SqliteException e){status="storage_error";Log(status,e);}
                catch(Exception e)when(e is IOException or JsonException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException or FormatException or System.ComponentModel.Win32Exception)
                {status="read_error";SourceUnavailable();Log(status,e);}
                update(new(status,stats.State(),start,window,synthetic){Afk=afk.Current,WaveRulesVerified=waveRulesVerified,Candidates=candidates});
                await Task.Delay(1000,stop.Token);
            }
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){ }
        finally
        {
            if(sessionId is not null)
            {
                try{Persist();if(stop.IsCancellationRequested&&!updating)store.End(sessionId,"assistant_closed");UpdateRestartSafe=updating;}
                catch(Exception e){Log("final_storage_error",e);} // Keep unfinished session for transactional recovery.
            }
            else UpdateRestartSafe=updating;
        }
    }
    string SavePath()=>selectedSave??(string.IsNullOrWhiteSpace(settings.SavePath)?SaveSource.DefaultPath:settings.SavePath);
    static void Log(string code,Exception error)
    {
        try
        {
            var path=Path.Combine(CompanionSettings.DataDirectory,"companion.log");
            if(File.Exists(path)&&new FileInfo(path).Length>1024*1024)File.Move(path,path+".previous",true);
            File.AppendAllText(path,$"{DateTimeOffset.UtcNow:o} {code} {error.GetType().Name} 0x{error.HResult:X8}\n");
        }catch(IOException){ }catch(UnauthorizedAccessException){ }
    }
}
