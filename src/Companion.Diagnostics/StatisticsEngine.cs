using System.Security.Cryptography;
using System.Text;
namespace Companion.Diagnostics;

public record SampleDelta(string EventId,long SavedAt,long GeneratedChests,long LostChests,long ObtainedChests,
    Dictionary<string,long> Equipment,Dictionary<string,long> Forged,ObservedItem[] NewlyObservedItems);
public record StatisticsState(SaveSnapshot? Previous,long ChestCount,long GeneratedChestCount,long LostChestCount,
    Dictionary<string,long> Equipment,Dictionary<string,long> Forged,ObservedItem[] RecentItems,bool HasGaps,
    string EquipmentScope="LegacyMixedCounter",bool EquipmentAvailable=false,ActivityCursor? Activity=null,
    Dictionary<string,long>? MixedEquipment=null,ActivityEvent[]? ChestDrops=null,string EquipmentSourceStatus="unknown",
    Dictionary<string,long>? EquipmentSourceCounts=null)
{
    public long? OpenedChestCount{get;init;}
    public bool OpenedChestAvailable{get;init;}
    public bool OpenedChestIncomplete{get;init;}
    public string? OpeningCounterScope{get;init;}
}

internal sealed class StatisticsEngine
{
    public const string ChestOnlyScope="VisibleChestAndColossusOpening_v2";
    public const string OrdinaryOnlyScope="VisibleChestOpeningOnly_v1";
    public const string FarmingScope="VisibleOrdinaryChestOpening_v3";
    public const string OpeningScope="NativeOpenedChestCounters_v1";
    public const string ProductionScope="NativeChestAndForgeProduction_v1";
    public static bool IsProductionScope(string? scope)=>scope==ProductionScope;
    public static bool IsTrackedScope(string? scope)=>IsChestScope(scope)||IsProductionScope(scope);
    public bool NativeProduction=>IsProductionScope(EquipmentScope);
    public long? OpenedChestCount{get;private set;}
    public bool OpenedChestAvailable{get;private set;}
    public bool OpenedChestIncomplete{get;private set;}
    public void OpeningUnavailable(){OpenedChestAvailable=false;}
    public void SaveUnavailable()
    {
        OpeningUnavailable();
        if(NativeProduction){EquipmentAvailable=false;EquipmentSourceStatus="save_unavailable";}
        else if(ChestOnly)ActivityUnavailable("save_identity_unavailable");
    }
    public static bool IsChestScope(string? scope)=>scope is ChestOnlyScope or OrdinaryOnlyScope or FarmingScope;
    public string EquipmentScope{get;}
    public bool ChestOnly=>IsChestScope(EquipmentScope);
    public bool EquipmentAvailable{get;private set;}
    public string EquipmentSourceStatus{get;private set;}="unknown";
    readonly Dictionary<string,long> mixed=SaveSource.Qualities.ToDictionary(q=>q,_=>0L);
    readonly Dictionary<string,long> sources=new(){["ordinary"]=0,["colossus"]=0};bool sourceCountsAvailable=true;
    public static Dictionary<string,long>? SourceCounts(StatisticsState? state)=>state?.EquipmentSourceCounts is not null?new(state.EquipmentSourceCounts):
        state?.EquipmentScope==OrdinaryOnlyScope?new(){["ordinary"]=state.Equipment.Values.Sum(),["colossus"]=0}:null;
    ActivityCursor? cursor;ActivitySequence? activity;
    public List<ActivityEvent> ChestDrops{get;}=[];
    public SaveSnapshot? Previous{get;private set;}
    public long ChestCount{get;private set;}
    public long GeneratedChestCount{get;private set;}
    public long LostChestCount{get;private set;}
    public Dictionary<string,long> Equipment{get;}=SaveSource.Qualities.ToDictionary(q=>q,_=>0L);
    public Dictionary<string,long> Forged{get;}=SaveSource.Qualities.ToDictionary(q=>q,_=>0L);
    public long EquipmentCount=>Equipment.Values.Sum();
    public bool HasGaps{get;private set;}
    public string Status{get;private set;}="waiting_save";
    public List<ObservedItem> RecentItems{get;}=[];
    public StatisticsEngine(StatisticsState? state=null,bool chestOnly=false):this(state,chestOnly,false){}
    public StatisticsEngine(StatisticsState? state,bool chestOnly,bool ordinaryOnly):this(state,chestOnly,ordinaryOnly,false){}
    public StatisticsEngine(StatisticsState? state,bool chestOnly,bool ordinaryOnly,bool nativeProduction)
    {
        EquipmentScope=nativeProduction?ProductionScope:ordinaryOnly?FarmingScope:chestOnly?ChestOnlyScope:IsTrackedScope(state?.EquipmentScope)?state!.EquipmentScope:"LegacyMixedCounter";
        if(state is null)return;
        if((chestOnly||ordinaryOnly||nativeProduction)&&state.EquipmentScope!=EquipmentScope)throw new InvalidDataException("statistics_scope_changed");
        Previous=state.Previous;ChestCount=state.ChestCount;GeneratedChestCount=state.GeneratedChestCount;LostChestCount=state.LostChestCount;
        OpenedChestCount=state.OpenedChestCount;OpenedChestAvailable=state.OpenedChestAvailable;OpenedChestIncomplete=state.OpenedChestIncomplete;
        foreach(var q in SaveSource.Qualities){Equipment[q]=state.Equipment[q];Forged[q]=state.Forged[q];}
        RecentItems.AddRange(state.RecentItems.Take(3));HasGaps=state.HasGaps;
        foreach(var q in SaveSource.Qualities)mixed[q]=(state.MixedEquipment??state.Equipment)[q];
        EquipmentAvailable=state.EquipmentAvailable;EquipmentSourceStatus=state.EquipmentSourceStatus;cursor=CopyCursor(state.Activity);
        ChestDrops.AddRange((state.ChestDrops??[]).Take(3));
        var savedSources=SourceCounts(state);
        if(savedSources is not null)foreach(var key in sources.Keys.ToArray())sources[key]=savedSources.GetValueOrDefault(key);
        else if(ChestOnly&&EquipmentCount>0)sourceCountsAvailable=false;
    }
    public StatisticsState State()=>new(Previous,ChestCount,GeneratedChestCount,LostChestCount,new(Equipment),new(Forged),RecentItems.ToArray(),HasGaps,
        EquipmentScope,EquipmentAvailable,activity?.State()??CopyCursor(cursor),new(mixed),ChestDrops.ToArray(),EquipmentSourceStatus,
        NativeProduction?new(){["native_chest_bucket"]=mixed.Values.Sum(),["native_forge_bucket"]=Forged.Values.Sum()}:ChestOnly&&sourceCountsAvailable?new(sources):null){OpenedChestCount=OpenedChestCount,OpenedChestAvailable=OpenedChestAvailable,OpenedChestIncomplete=OpenedChestIncomplete,OpeningCounterScope=OpeningScope};
    static ActivityCursor? CopyCursor(ActivityCursor? value)=>value is null?null:value with{Previous=value.Previous is null?null:value.Previous with{Notices=value.Previous.Notices.ToArray()}};
    public void ActivityUnavailable(string reason)
    {
        if(!ChestOnly)return;
        EquipmentAvailable=false;EquipmentSourceStatus=reason;HasGaps=true;
    }
    public void ResetActivityBaseline()
    {
        if(!ChestOnly)return;
        var old=activity?.State()??cursor;cursor=new(null,old?.Sequence??0,true);activity=null;
        ActivityUnavailable("activity_baseline_reset");
    }
    public ActivityEvent[] PushActivity(ActivitySnapshot snapshot,string sessionId)
    {
        if(!ChestOnly)throw new InvalidOperationException("legacy_scope_has_no_activity_input");
        activity??=new(sessionId,cursor);var batch=activity.Push(snapshot);cursor=activity.State();
        EquipmentAvailable=snapshot.Notices.Length>0&&batch.Status is "baseline" or "unchanged" or "observed_events";
        EquipmentSourceStatus=batch.Status;HasGaps|=batch.HasGaps;
        if(!EquipmentAvailable&&batch.Status!="baseline")HasGaps=true;
        var drops=batch.Events.Where(e=>(e.Notice.Kind=="ChestOpened"||EquipmentScope==ChestOnlyScope&&e.Notice.Kind=="ColossusReward")&&e.Notice.Quality is not null&&e.Notice.Name is not null).ToArray();
        foreach(var drop in drops)
        {
            if(!Equipment.ContainsKey(drop.Notice.Quality!))throw new InvalidDataException("unknown_activity_quality");
            Equipment[drop.Notice.Quality!]=checked(Equipment[drop.Notice.Quality!]+1);
            var source=drop.Notice.Kind=="ColossusReward"?"colossus":"ordinary";sources[source]=checked(sources[source]+1);
            ChestDrops.Insert(0,drop);if(ChestDrops.Count>3)ChestDrops.RemoveAt(3);
        }
        if(batch.Events.Any(e=>e.Notice.Kind=="ChestEquipmentUnconfirmed"))ActivityUnavailable("unconfirmed_chest_equipment");
        return drops;
    }
    public void Rebaseline(SaveSnapshot snapshot){Previous=snapshot;OpenedChestAvailable=snapshot.OpenedChests is not null;OpenedChestCount??=OpenedChestAvailable?0:null;OpenedChestIncomplete=true;if(NativeProduction)HasGaps=true;Status="baseline";}
    void ResetOpeningBaseline(SaveSnapshot n,string status)
    {
        Rebaseline(n);if(!NativeProduction)OpenedChestCount=OpenedChestAvailable?0:null;HasGaps=true;Status=status;
    }
    public SampleDelta? Push(SaveSnapshot n)
    {
        var p=Previous;
        if(p is null){Previous=n;OpenedChestAvailable=n.OpenedChests is not null;OpenedChestCount=OpenedChestAvailable?0:null;if(NativeProduction){EquipmentAvailable=true;EquipmentSourceStatus="native_counter_baseline";}Status="baseline";return null;}
        if(n.Identity!=p.Identity){Status="identity_changed";throw new InvalidDataException(Status);}
        if(n.SavedAt<p.SavedAt){if((EquipmentScope==FarmingScope||NativeProduction)&&n.OpenedChests is not null)ResetOpeningBaseline(n,"save_rollback");else Status="old_save";return null;}
        OpenedChestAvailable=n.OpenedChests is not null;
        if(NativeProduction){EquipmentAvailable=true;EquipmentSourceStatus="native_counters";}
        if(n.Fingerprint==p.Fingerprint){Status="waiting_save";return null;}
        if(n.SavedAt==p.SavedAt){if(NativeProduction)SaveUnavailable();Status="conflicting_save";return null;}
        if(n.OpenedChestCounters is not null&&p.OpenedChestCounters is not null&&n.OpenedChestCounters.Any(k=>k.Value<p.OpenedChestCounters.GetValueOrDefault(k.Key)))
        {ResetOpeningBaseline(n,"counter_reset");return null;}
        var generated=n.ChestSerial-p.ChestSerial;var lost=n.LostChests-p.LostChests;
        if(generated<0||lost<0||lost>generated||SaveSource.Qualities.Any(q=>n.ChestEquipment[q]<p.ChestEquipment[q]||n.ForgeEquipment[q]<p.ForgeEquipment[q]))
        {
            ResetOpeningBaseline(n,"counter_reset");return null;
        }
        var equipment=SaveSource.Qualities.ToDictionary(q=>q,q=>n.ChestEquipment[q]-p.ChestEquipment[q]);
        if(n.OpenedChests is long current&&p.OpenedChests is long previous&&OpenedChestCount is long counted)OpenedChestCount=checked(counted+current-previous);
        else {if(n.OpenedChests is not null)OpenedChestCount??=0;OpenedChestIncomplete=true;}
        var forge=SaveSource.Qualities.ToDictionary(q=>q,q=>n.ForgeEquipment[q]-p.ForgeEquipment[q]);
        var oldIds=p.Items.Select(i=>i.Id).ToHashSet(StringComparer.Ordinal);
        var newItems=NativeProduction?[]:n.Items.Where(i=>!oldIds.Contains(i.Id)).ToArray();
        // New IDs are observed arrivals, not proof of chest origin: buying/crafting may occur in the same save span.
        if(!ChestOnly&&!NativeProduction)foreach(var item in newItems){RecentItems.Insert(0,item);if(RecentItems.Count>3)RecentItems.RemoveAt(3);}
        ChestCount=checked(ChestCount+generated-lost);GeneratedChestCount=checked(GeneratedChestCount+generated);LostChestCount=checked(LostChestCount+lost);
        foreach(var q in SaveSource.Qualities){mixed[q]=checked(mixed[q]+equipment[q]);if(!ChestOnly)Equipment[q]=checked(Equipment[q]+equipment[q]+(NativeProduction?forge[q]:0));Forged[q]=checked(Forged[q]+forge[q]);}
        Previous=n;Status="tracking";
        return new(n.Fingerprint,n.SavedAt,generated,lost,generated-lost,equipment,forge,newItems);
    }
}

public record ActivityNotice(string Minute,string Text,string Kind,string? Name=null,string? Quality=null)
{
    public int Part{get;init;}
    public string Key=>Minute+"\n"+Text+(Part==0?"":"\npart:"+Part);
}
public record ActivitySnapshot(DateTimeOffset ObservedUtc,string DocumentId,string? LatestMinute,ActivityNotice[] Notices);
public record ActivityEvent(string EventId,DateTimeOffset ObservedUtc,ActivityNotice Notice)
{
    // Explicit visible notification, not a recognizer probability or an exact drop timestamp.
    public double? Confidence=>null;
    public string TimePrecision=>"observed_at; displayed minute retained separately";
}
public record ActivityCursor(ActivitySnapshot? Previous,long Sequence,bool HasGaps);
public record ActivityBatch(string Status,ActivityEvent[] Events,bool HasGaps);

internal sealed class ActivitySequence
{
    readonly string sessionId;ActivitySnapshot? previous;long sequence;bool gaps;
    public ActivitySequence(string sessionId,ActivityCursor? cursor=null)
    {
        this.sessionId=sessionId;
        if(cursor is not null)
        {
            if(cursor.Sequence<0||cursor.Sequence>long.MaxValue-1000)throw new InvalidDataException("activity_sequence_invalid");
            previous=Copy(cursor.Previous);sequence=cursor.Sequence;gaps=cursor.HasGaps;
        }
    }
    static ActivitySnapshot? Copy(ActivitySnapshot? value)=>value is null?null:value with{Notices=value.Notices.ToArray()};
    public ActivityCursor State()=>new(Copy(previous),sequence,gaps);
    public ActivityBatch Push(ActivitySnapshot next)
    {
        if(previous is null){previous=Copy(next);return new("baseline",[],gaps);}
        var old=previous;
        if(next.ObservedUtc<=old.ObservedUtc)return new("old_observation",[],gaps);
        if(next.DocumentId!=old.DocumentId)return Rebaseline(next,"document_changed");
        if(old.LatestMinute is not null&&next.LatestMinute is null)return new("reference_time_unavailable",[],gaps);
        // Minute-only source cannot certify midnight/reset chronology. Fail closed instead of inventing a date.
        if(old.LatestMinute is not null&&next.LatestMinute is not null&&TimeOnly.Parse(next.LatestMinute)<TimeOnly.Parse(old.LatestMinute))
        {
            if(next.ObservedUtc.ToLocalTime().Date>old.ObservedUtc.ToLocalTime().Date)return Rebaseline(next,"reference_day_changed");
            return new("reference_backwards",[],gaps);
        }
        var a=old.Notices;var b=next.Notices;
        if(a.Select(x=>x.Key).SequenceEqual(b.Select(x=>x.Key))){previous=Copy(next);return new("unchanged",[],gaps);}
        if(b.Length==0)return Rebaseline(next,"reference_cleared");
        var overlap=Math.Min(a.Length,b.Length);
        while(overlap>0&&!a.Skip(a.Length-overlap).Select(x=>x.Key).SequenceEqual(b.Take(overlap).Select(x=>x.Key)))overlap--;
        if(overlap==0&&a.Length>0)return Rebaseline(next,"reference_gap");
        // Empty accessibility text can precede delayed history, even on the first read.
        if(a.Length==0)return Rebaseline(next,gaps?"reference_repopulated":"reference_initial_population");
        var events=b.Skip(overlap).Select(notice=>new ActivityEvent(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{sessionId}:activity:{checked(++sequence)}"))),next.ObservedUtc,notice)).ToArray();
        previous=Copy(next);return new("observed_events",events,gaps);
    }
    ActivityBatch Rebaseline(ActivitySnapshot next,string reason){gaps=true;previous=Copy(next);return new(reason,[],gaps);}
}
