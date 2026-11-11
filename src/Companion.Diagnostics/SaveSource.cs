using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Companion.Diagnostics;

public record ObservedItem(string Id,string BaseType,string Quality,int Level);
public record AfkHero(string Key,string ClassId,int Level,double Xp);
public record AfkPotion(string Id,double RemainingMs);
public record AfkSave(int? Act,int? Stage,int? Difficulty,AfkHero[] Heroes,AfkPotion[]? Potions)
{public long? StageKills{get;init;}public long? TotalKills{get;init;}}
public record SaveSnapshot(long SavedAt,int Version,string Identity,long ChestSerial,long LostChests,
    Dictionary<string,long> ChestEquipment,Dictionary<string,long> ForgeEquipment,ObservedItem[] Items,string Fingerprint)
{
    public Dictionary<string,long>? OpenedChestCounters{get;init;}
    public long? OpenedChests=>OpenedChestCounters?.Values.Sum();
    [System.Text.Json.Serialization.JsonIgnore] public AfkSave? Afk{get;init;}
}

internal static class SaveSource
{
    public static readonly string[] Qualities=["common","uncommon","rare","heroic","mythic","ascendant","eternal"];
    public static string DefaultPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"com.artxgames.questaway","save.json");
    public static async Task<SaveSnapshot> Read(string path,CancellationToken token)
        =>(await ReadCached(path,token,null,null,false)).Snapshot;
    internal static async Task<(SaveSnapshot Snapshot,string? BytesHash)> ReadCached(string path,CancellationToken token,string? previousHash,SaveSnapshot? previous,bool hashBytes=true)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,4096,true);
        if(stream.Length is <=0 or >4*1024*1024)throw new InvalidDataException("save_size");
        var bytes=new byte[(int)stream.Length];
        try
        {
            await stream.ReadExactlyAsync(bytes,token);
            token.ThrowIfCancellationRequested();
            var hash=hashBytes?Convert.ToHexString(SHA256.HashData(bytes)):null;
            return (previous is not null&&hash is not null&&hash==previousHash?previous:Parse(bytes),hash);
        }
        finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    public static SaveSnapshot Parse(byte[] data)
    {
        // Only whitelisted game/statistics fields are accessed. Never inspect settings, credentials or WebView storage.
        var offset=data.Length>=3&&data[0]==239&&data[1]==187&&data[2]==191?3:0;
        using var outer=JsonDocument.Parse(data.AsMemory(offset),new JsonDocumentOptions{MaxDepth=80});
        var root=outer.RootElement;
        var saved=Counter(root,"savedAt");
        var nested=root.GetProperty("game");
        using var inner=nested.ValueKind==JsonValueKind.String?JsonDocument.Parse(nested.GetString()!,new JsonDocumentOptions{MaxDepth=80}):JsonDocument.Parse(nested.GetRawText());
        var rawVersion=Counter(inner.RootElement,"version");
        if(rawVersion!=22)throw new InvalidDataException("unsupported_save_version");
        var version=(int)rawVersion;
        var g=inner.RootElement.GetProperty("game");
        var sim=g.GetProperty("sim");
        var identityText=g.GetProperty("playerId").GetString();
        if(string.IsNullOrWhiteSpace(identityText))throw new InvalidDataException("missing_save_identity");
        var season=root.TryGetProperty("temporada",out var seasonValue)?seasonValue.GetRawText():"unknown";
        var identity=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityText+":"+season)));
        var found=g.GetProperty("itemsFound");
        var chest=QualityCounters(found.GetProperty("chest"));
        var forge=QualityCounters(found.GetProperty("forge"));
        var items=new Dictionary<string,ObservedItem>();
        void Item(JsonElement item)
        {
            if(item.ValueKind!=JsonValueKind.Object)return;
            var id=item.GetProperty("id").GetString();var quality=item.GetProperty("rarity").GetString();var type=item.GetProperty("baseType").GetString();
            if(string.IsNullOrWhiteSpace(id)||id.Length>160||string.IsNullOrWhiteSpace(type)||type.Length>100||!Qualities.Contains(quality))throw new InvalidDataException("invalid_equipment");
            var level=Counter(item,"itemLevel");
            if(level>int.MaxValue)throw new InvalidDataException("invalid_equipment");
            items[id]=new(id,type,quality!,(int)level);
        }
        foreach(var key in new[]{"inventory","stash"})
            if(sim.TryGetProperty(key,out var list))foreach(var item in list.EnumerateArray())Item(item);
        foreach(var key in new[]{"heroes","bench"})
            if(g.TryGetProperty(key,out var heroes))foreach(var h in heroes.EnumerateArray())
                if(h.TryGetProperty("equipment",out var equipment))foreach(var entry in equipment.EnumerateObject())Item(entry.Value);
        var serial=Counter(sim,"chestSerial");var lost=Counter(sim,"bausPerdidos");
        if(lost>serial)throw new InvalidDataException("invalid_chest_counters");
        Dictionary<string,long>? opened=null;
        if(g.TryGetProperty("contadores",out var counters)&&counters.TryGetProperty("bausAbertos",out var openedValue))
        {
            string[] kinds=["normal","stage","act"];
            if(openedValue.ValueKind!=JsonValueKind.Object||openedValue.EnumerateObject().Any(p=>!kinds.Contains(p.Name)))throw new InvalidDataException("invalid_opened_chest_counters");
            opened=kinds.ToDictionary(k=>k,k=>Counter(openedValue,k));
            if(opened.Values.Sum()>9_007_199_254_740_991)throw new InvalidDataException("invalid_opened_chest_counters");
        }
        AfkSave? afk=null;
        try{afk=ParseAfk(g);}catch(Exception e)when(e is InvalidOperationException or FormatException or OverflowException){ } // Optional display fields must not disable core counters.
        var snapshot=new SaveSnapshot(saved,version,identity,serial,lost,chest,forge,items.Values.OrderBy(i=>i.Id,StringComparer.Ordinal).ToArray(),""){OpenedChestCounters=opened,Afk=afk};
        var fingerprint=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot)));
        return snapshot with{Fingerprint=fingerprint};
    }
    static long Counter(JsonElement element,string key)
    {
        if(!element.TryGetProperty(key,out var value)||value.ValueKind!=JsonValueKind.Number||!value.TryGetInt64(out var n)||n<0||n>9_007_199_254_740_991)throw new InvalidDataException("invalid_counter:"+key);
        return n;
    }
    static Dictionary<string,long> QualityCounters(JsonElement element)
    {
        if(element.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("invalid_quality_counters");
        foreach(var property in element.EnumerateObject())if(!Qualities.Contains(property.Name))throw new InvalidDataException("unknown_quality");
        return Qualities.ToDictionary(q=>q,q=>element.TryGetProperty(q,out _)?Counter(element,q):0L);
    }
    internal static AfkSave? ParseAfk(JsonElement game)
    {
        double? Number(JsonElement obj,string key,double max=9_007_199_254_740_991)=>obj.ValueKind==JsonValueKind.Object&&obj.TryGetProperty(key,out var v)&&v.TryGetDouble(out var n)&&double.IsFinite(n)&&n>=0&&n<=max?n:null;
        int? Integer(string key){var n=Number(game,key,100);return n is >=1&&n==Math.Truncate(n.Value)?(int)n.Value:null;}
        var heroes=new List<AfkHero>();
        if(game.TryGetProperty("heroes",out var list)&&list.ValueKind==JsonValueKind.Array&&list.GetArrayLength()<=4)
            foreach(var hero in list.EnumerateArray())
            {
                var xp=Number(hero,"xp");var level=Number(hero,"level",100);
                if(xp is null||level is null||level<1||level!=Math.Truncate(level.Value)||!hero.TryGetProperty("classId",out var c)||c.ValueKind!=JsonValueKind.String)continue;
                var id=hero.TryGetProperty("id",out var idValue)&&idValue.ValueKind==JsonValueKind.String?idValue.GetString():null;
                var key=string.IsNullOrWhiteSpace(id)?"":Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
                var classId=c.GetString()??"";if(classId.Length>40)continue;
                heroes.Add(new(key,classId,(int)level.Value,xp.Value));
            }
        if(heroes.Where(h=>h.Key.Length>0).GroupBy(h=>h.Key).Any(g=>g.Count()>1))heroes.Clear();
        List<AfkPotion>? potions=null;
        if(game.TryGetProperty("buffs",out var buffs)&&buffs.ValueKind==JsonValueKind.Array&&buffs.GetArrayLength()<=32)
        {
            potions=new();foreach(var buff in buffs.EnumerateArray())
            {
                var remaining=Number(buff,"msRemaining",365d*86400000);
                if(remaining is null||!buff.TryGetProperty("id",out var id)||id.ValueKind!=JsonValueKind.String||id.GetString() is not string name||name.Length>50){potions=null;break;}
                potions.Add(new(name,remaining.Value));
            }
        }
        var difficulty=Number(game,"difficulty",2);
        var kills=game.TryGetProperty("sim",out var sim)?Number(sim,"kills"):null;var start=Number(game,"stageStartKills");
        long? stageKills=kills is not null&&start is not null&&kills>=start&&kills==Math.Truncate(kills.Value)&&start==Math.Truncate(start.Value)?(long)(kills.Value-start.Value):null;
        return new(Integer("act"),Integer("stage"),difficulty is not null&&difficulty==Math.Truncate(difficulty.Value)?(int)difficulty.Value:null,heroes.ToArray(),potions?.ToArray()){StageKills=stageKills,TotalKills=kills is not null&&kills==Math.Truncate(kills.Value)?(long)kills.Value:null};
    }
}

