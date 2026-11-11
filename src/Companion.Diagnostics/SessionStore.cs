using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Companion.Diagnostics;

public record SessionInfo(string SessionId,string ProcessKey,DateTimeOffset StartTime,DateTimeOffset? EndTime,
    long ChestCount,long EquipmentCount,string StateJson,string EndReason);

internal sealed class SessionStore
{
    readonly string connection;
    public SessionStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connection=new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadWriteCreate,DefaultTimeout=5}.ToString();
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS Sessions(SessionId TEXT PRIMARY KEY,ProcessKey TEXT NOT NULL,StartTime TEXT NOT NULL,
              EndTime TEXT,ChestCount INTEGER NOT NULL DEFAULT 0,EquipmentCount INTEGER NOT NULL DEFAULT 0,
              StateJson TEXT NOT NULL,EndReason TEXT NOT NULL DEFAULT 'running');
            CREATE TABLE IF NOT EXISTS Events(EventId TEXT NOT NULL,SessionId TEXT NOT NULL,EventTime TEXT NOT NULL,
              EventType TEXT NOT NULL,RawData TEXT NOT NULL,IsConfirmed INTEGER NOT NULL,PRIMARY KEY(SessionId,EventId));
            CREATE TABLE IF NOT EXISTS EquipmentDrops(Id TEXT NOT NULL,SessionId TEXT NOT NULL,EquipmentName TEXT NOT NULL,
              Quality TEXT NOT NULL,ObtainedAt TEXT NOT NULL,Source TEXT NOT NULL,Confidence REAL,
              PRIMARY KEY(SessionId,Id));
            PRAGMA user_version=1;
            """;
        cmd.ExecuteNonQuery();
    }
    SqliteConnection Open(){var db=new SqliteConnection(connection);db.Open();return db;}
    public SessionInfo Begin(string processKey,bool chestOnly=false,bool ordinaryOnly=false)=>Begin(processKey,chestOnly,ordinaryOnly,false);
    public SessionInfo Begin(string processKey,bool chestOnly,bool ordinaryOnly,bool nativeProduction)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT SessionId,ProcessKey,StartTime,EndTime,ChestCount,EquipmentCount,StateJson,EndReason FROM Sessions WHERE EndTime IS NULL ORDER BY StartTime DESC";
        var unfinished=new List<SessionInfo>();using(var rows=cmd.ExecuteReader())while(rows.Read())unfinished.Add(Row(rows));
        var matching=unfinished.FirstOrDefault(s=>s.ProcessKey==processKey);
        var scope=nativeProduction?StatisticsEngine.ProductionScope:ordinaryOnly?StatisticsEngine.FarmingScope:StatisticsEngine.ChestOnlyScope;
        if((chestOnly||ordinaryOnly||nativeProduction)&&matching is not null&&JsonSerializer.Deserialize<StatisticsState>(matching.StateJson,Program.Json)?.EquipmentScope!=scope)matching=null;
        if((ordinaryOnly||nativeProduction)&&matching is not null&&JsonSerializer.Deserialize<StatisticsState>(matching.StateJson,Program.Json)?.OpeningCounterScope!=StatisticsEngine.OpeningScope)matching=null;
        foreach(var old in unfinished.Where(s=>s.SessionId!=matching?.SessionId))End(old.SessionId,(chestOnly||ordinaryOnly||nativeProduction)&&old.ProcessKey==processKey?"statistics_scope_changed":"interrupted");
        if(matching is not null)return matching;
        var session=new SessionInfo(Guid.NewGuid().ToString("N"),processKey,DateTimeOffset.UtcNow,null,0,0,JsonSerializer.Serialize(new StatisticsEngine(null,chestOnly,ordinaryOnly,nativeProduction).State(),Program.Json),"running");
        cmd.CommandText="INSERT INTO Sessions(SessionId,ProcessKey,StartTime,StateJson) VALUES($id,$key,$time,$state)";
        cmd.Parameters.AddWithValue("$id",session.SessionId);cmd.Parameters.AddWithValue("$key",processKey);cmd.Parameters.AddWithValue("$time",session.StartTime.ToString("o"));cmd.Parameters.AddWithValue("$state",session.StateJson);cmd.ExecuteNonQuery();
        return session;
    }
    public void Save(string id,StatisticsEngine stats,SampleDelta? delta,ActivityEvent[]? activityEvents=null)
    {
        using var db=Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        if(delta is not null)
        {
            // Preserve the Phase 2 regression contract: one source-counter batch, not invented per-item events.
            cmd.CommandText="INSERT OR IGNORE INTO Events(EventId,SessionId,EventTime,EventType,RawData,IsConfirmed) VALUES($event,$session,$time,'SaveCounterDelta',$data,$confirmed)";
            cmd.Parameters.AddWithValue("$event",delta.EventId);cmd.Parameters.AddWithValue("$session",id);cmd.Parameters.AddWithValue("$time",DateTimeOffset.FromUnixTimeMilliseconds(delta.SavedAt).ToString("o"));
            cmd.Parameters.AddWithValue("$data",stats.NativeProduction?JsonSerializer.Serialize(new{Scope=StatisticsEngine.ProductionScope,Delta=delta,EquipmentFormula="Equipment+Forged; native bucket deltas"},Program.Json):stats.ChestOnly?JsonSerializer.Serialize(new{Scope="NativeMixedCounter_DiagnosticOnly",Delta=delta},Program.Json):JsonSerializer.Serialize(delta,Program.Json));cmd.Parameters.AddWithValue("$confirmed",stats.ChestOnly?0:1);cmd.ExecuteNonQuery();
            if(!stats.ChestOnly&&!stats.NativeProduction)foreach(var item in delta.NewlyObservedItems)
            {
                cmd.Parameters.Clear();cmd.CommandText="INSERT OR IGNORE INTO EquipmentDrops(Id,SessionId,EquipmentName,Quality,ObtainedAt,Source,Confidence) VALUES($item,$session,$name,$quality,$time,'InventoryAppearance_Unattributed',NULL)";
                cmd.Parameters.AddWithValue("$item",item.Id);cmd.Parameters.AddWithValue("$session",id);cmd.Parameters.AddWithValue("$name",item.BaseType);cmd.Parameters.AddWithValue("$quality",item.Quality);
                cmd.Parameters.AddWithValue("$time",DateTimeOffset.FromUnixTimeMilliseconds(delta.SavedAt).ToString("o"));cmd.ExecuteNonQuery();
            }
        }
        foreach(var drop in activityEvents??[])
        {
            if(!stats.ChestOnly||!(drop.Notice.Kind=="ChestOpened"||stats.EquipmentScope==StatisticsEngine.ChestOnlyScope&&drop.Notice.Kind=="ColossusReward")||drop.Notice.Name is null||drop.Notice.Quality is null)throw new InvalidDataException("invalid_chest_activity_event");
            var source=drop.Notice.Kind=="ColossusReward"?"NativeVisibleColossusOpening":"NativeVisibleChestOpening";
            cmd.Parameters.Clear();cmd.CommandText="INSERT OR IGNORE INTO Events(EventId,SessionId,EventTime,EventType,RawData,IsConfirmed) VALUES($event,$session,$time,'EquipmentObtained',$data,1)";
            cmd.Parameters.AddWithValue("$event",drop.EventId);cmd.Parameters.AddWithValue("$session",id);cmd.Parameters.AddWithValue("$time",drop.ObservedUtc.ToString("o"));cmd.Parameters.AddWithValue("$data",JsonSerializer.Serialize(new{Source=source,Event=drop,TimePrecision=drop.TimePrecision},Program.Json));cmd.ExecuteNonQuery();
            cmd.Parameters.Clear();cmd.CommandText="INSERT OR IGNORE INTO EquipmentDrops(Id,SessionId,EquipmentName,Quality,ObtainedAt,Source,Confidence) VALUES($event,$session,$name,$quality,$time,$source,NULL)";
            cmd.Parameters.AddWithValue("$event",drop.EventId);cmd.Parameters.AddWithValue("$session",id);cmd.Parameters.AddWithValue("$name",drop.Notice.Name);cmd.Parameters.AddWithValue("$quality",drop.Notice.Quality);cmd.Parameters.AddWithValue("$time",drop.ObservedUtc.ToString("o"));cmd.Parameters.AddWithValue("$source",source);cmd.ExecuteNonQuery();
        }
        cmd.Parameters.Clear();cmd.CommandText="UPDATE Sessions SET ChestCount=$chests,EquipmentCount=$equipment,StateJson=$state WHERE SessionId=$id";
        cmd.Parameters.AddWithValue("$chests",stats.ChestCount);cmd.Parameters.AddWithValue("$equipment",stats.EquipmentCount);cmd.Parameters.AddWithValue("$state",JsonSerializer.Serialize(stats.State(),Program.Json));cmd.Parameters.AddWithValue("$id",id);cmd.ExecuteNonQuery();tx.Commit();
    }
    public void End(string id,string reason)
    {
        using var db=Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="UPDATE Sessions SET EndTime=$time,EndReason=$reason WHERE SessionId=$id AND EndTime IS NULL";
        cmd.Parameters.AddWithValue("$time",DateTimeOffset.UtcNow.ToString("o"));cmd.Parameters.AddWithValue("$reason",reason);cmd.Parameters.AddWithValue("$id",id);cmd.ExecuteNonQuery();
        tx.Commit();
    }
    public SessionInfo[] History()
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT SessionId,ProcessKey,StartTime,EndTime,ChestCount,EquipmentCount,StateJson,EndReason FROM Sessions ORDER BY StartTime DESC LIMIT 500";
        using var rows=cmd.ExecuteReader();var result=new List<SessionInfo>();while(rows.Read())result.Add(Row(rows));return result.ToArray();
    }
    public void Export(string path)
    {
        var sessions=History().Select(s=>
        {
            var state=JsonSerializer.Deserialize<StatisticsState>(s.StateJson,Program.Json);
            var pure=StatisticsEngine.IsChestScope(state?.EquipmentScope);
            var available=StatisticsEngine.IsTrackedScope(state?.EquipmentScope)?state!.EquipmentAvailable&&!state.HasGaps:state?.Previous is not null;
            return new{s.SessionId,s.StartTime,s.EndTime,DurationSeconds=((s.EndTime??DateTimeOffset.UtcNow)-s.StartTime).TotalSeconds,
                s.ChestCount,ChestCountMeaning="NewlyObtained_LegacyField_NotOpened",
                OpenedChestTotal=state?.OpenedChestAvailable==true?state.Previous?.OpenedChests:null,
                OpenedChestCount=state?.OpenedChestAvailable==true&&state.OpenedChestIncomplete==false||state?.OpenedChestCount>0?state?.OpenedChestCount:null,
                OpenedChestCountingAvailable=state?.OpenedChestAvailable??false,
                OpenedChestIncomplete=state?.OpenedChestIncomplete==true||state?.OpenedChestAvailable!=true,
                OpeningCounterScope=state?.OpeningCounterScope??"UnavailableInLegacyHistory",
                PureChestHistoricalQualitiesRecoverable=pure,
                QualityCountMeaning=StatisticsEngine.IsProductionScope(state?.EquipmentScope)?"NativeChestPlusForgeProduction_PerSession":pure?"ConfirmedChestEquipment_PerSession":"LegacyMixed_NotRecoverableAsChestEquipment",
                EquipmentCount=available||s.EquipmentCount>0?(long?)s.EquipmentCount:null,s.EndReason,
                Source=s.ProcessKey.StartsWith("synthetic:")?"Synthetic":s.ProcessKey.StartsWith("real:")?"RealLocalSave":"Unknown",
                EquipmentScope=state?.EquipmentScope??"LegacyMixedCounter",EquipmentCountingAvailable=available,
                EquipmentSourceCounts=available||s.EquipmentCount>0?StatisticsEngine.SourceCounts(state)?.ToDictionary(p=>p.Key,p=>available||p.Value>0?(long?)p.Value:null):null,
                EquipmentIncomplete=StatisticsEngine.IsTrackedScope(state?.EquipmentScope)&&(state!.HasGaps||!available),EquipmentSourceStatus=state?.EquipmentSourceStatus??"unknown",
                CountingAvailable=state?.Previous is not null,HasGaps=state?.HasGaps??true,
                QualityCounts=available||s.EquipmentCount>0?state?.Equipment.ToDictionary(p=>p.Key,p=>available||p.Value>0?(long?)p.Value:null):null};
        });
        // Explicit whitelist: no baseline inventory, account identity, original save or credentials in exports.
        File.WriteAllText(path,JsonSerializer.Serialize(new{Version="0.1",ChestOpeningCounterSource="contadores.bausAbertos.normal+stage+act",EquipmentCounterScope="PerSession_SeeEquipmentScope",EquipmentLootAccuracyAccepted=false,Sessions=sessions},Program.Json));
    }
    public void ClearEnded()
    {
        using var db=Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="DELETE FROM EquipmentDrops WHERE SessionId IN(SELECT SessionId FROM Sessions WHERE EndTime IS NOT NULL);DELETE FROM Events WHERE SessionId IN(SELECT SessionId FROM Sessions WHERE EndTime IS NOT NULL);DELETE FROM Sessions WHERE EndTime IS NOT NULL;";cmd.ExecuteNonQuery();tx.Commit();
    }
    static SessionInfo Row(SqliteDataReader r)=>new(r.GetString(0),r.GetString(1),DateTimeOffset.Parse(r.GetString(2)),r.IsDBNull(3)?null:DateTimeOffset.Parse(r.GetString(3)),r.GetInt64(4),r.GetInt64(5),r.GetString(6),r.GetString(7));
}
