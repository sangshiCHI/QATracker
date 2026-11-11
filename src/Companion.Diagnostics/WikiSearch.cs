using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Companion.Diagnostics;

internal sealed record WikiEntry(string Title,string English,string Path,string Category,string Aliases,string? IconPath=null)
{
    public override string ToString()=>Title+"  ·  "+Category;
    public System.Windows.Media.ImageSource? Icon=>WikiPictures.Local.Value.Get(IconPath??WikiPictures.Local.Value.IconPath(Path));
}
internal sealed record WikiPage(string Path,string Text,string Sha256);
internal sealed record WikiCell(string Text,string[] Images,bool Header=false,int ColSpan=1,int RowSpan=1);
internal sealed record WikiBlock(string Kind,string Text,WikiCell[][]? Rows=null);
internal sealed record WikiDetail(string Path,string Text,string[] Images,bool Specific)
{public WikiBlock[]? Blocks{get;init;}}
internal sealed record WikiSnapshot(int Version,string GameVersion,string RetrievedAt,bool Complete,int ExpectedPageCount,WikiEntry[] Entries,WikiPage[] Pages,WikiDetail[] Details)
{public int SectionEntryCount{get;init;}}

internal sealed class WikiSearch
{
    public WikiSnapshot Snapshot{get;}
    readonly Dictionary<string,WikiPage> pages;
    readonly Dictionary<string,WikiDetail> details;
    public static readonly Lazy<WikiSearch> Local=new(Load);
    WikiSearch(WikiSnapshot snapshot)
    {
        if(snapshot.Version!=1||snapshot.Entries.Length==0||snapshot.Pages.Length==0||
           snapshot.Entries.Any(e=>!ValidPath(e.Path)||string.IsNullOrWhiteSpace(e.Title))||
           snapshot.Pages.Any(p=>!ValidPath(p.Path)||string.IsNullOrWhiteSpace(p.Text)))
            throw new InvalidDataException("本地百科数据格式不受支持。");
        if(snapshot.Details is null||snapshot.Details.Length!=snapshot.Entries.Length)throw new InvalidDataException("缺少净化后的条目正文。");
        Snapshot=snapshot;pages=snapshot.Pages.ToDictionary(p=>p.Path,StringComparer.Ordinal);
        // The native index intentionally has multiple stat names linking to one mechanics anchor.
        details=snapshot.Details.GroupBy(p=>p.Path,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.First(),StringComparer.Ordinal);
    }
    static WikiSearch Load()
    {
        using var resource=Assembly.GetExecutingAssembly().GetManifestResourceStream("wiki-zh.json.gz")??throw new FileNotFoundException("未打包本地百科资料。");
        using var gzip=new GZipStream(resource,CompressionMode.Decompress);
        return new(JsonSerializer.Deserialize<WikiSnapshot>(gzip)??throw new InvalidDataException("百科资料为空。"));
    }
    internal static bool ValidPath(string path)=>path.Length<=300&&Regex.IsMatch(path,@"\A/[a-z0-9][a-z0-9/_-]*(?:#[a-zA-Z0-9_-]+)?\z",RegexOptions.CultureInvariant);
    internal static Uri SourceUri(string path)=>ValidPath(path)?new Uri("https://questaway.wiki/zh"+path):throw new ArgumentException("非法百科链接。");
    public string Details(WikiEntry entry)=>details.TryGetValue(entry.Path,out var detail)?detail.Text:"此条目的独立资料暂未提取完整。";
    internal WikiBlock[]? Blocks(WikiEntry entry)=>details.GetValueOrDefault(entry.Path)?.Blocks;
    public string[] Images(WikiEntry entry)=>details.TryGetValue(entry.Path,out var detail)?detail.Images:[];
    public IReadOnlyList<WikiEntry> Find(string input)
    {
        var query=input.Trim();if(query.Length<2||query.Length>100)return [];
        var words=query.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        bool Matches(string text)=>words.All(w=>text.Contains(w,StringComparison.OrdinalIgnoreCase));
        // Match explicit names, real section headings and their aliases; never arbitrary body mentions.
        return Snapshot.Entries.Where(e=>details[e.Path].Specific&&Matches(e.Title+" "+e.English+" "+e.Aliases))
            .OrderBy(e=>e.Title.Equals(query,StringComparison.OrdinalIgnoreCase)||e.English.Equals(query,StringComparison.OrdinalIgnoreCase)?0:e.Title.StartsWith(query,StringComparison.OrdinalIgnoreCase)?1:2)
            .ThenBy(e=>e.English.Length).Take(9).ToArray();
    }
    public static int Verify(string output)
    {
        var data=Local.Value;var checks=new Dictionary<string,bool>();
        checks["reference entries exclude five website tools"]=data.Snapshot.SectionEntryCount>=0&&data.Snapshot.Entries.Length-data.Snapshot.SectionEntryCount==1868;
        checks["all indexed static pages captured"]=data.Snapshot.Complete&&data.Snapshot.Pages.Length==data.Snapshot.ExpectedPageCount;
        checks["every indexed entry has local page"]=data.Snapshot.Entries.All(e=>data.pages.ContainsKey(e.Path.Split('#')[0]));
        checks["Chinese class search"]=data.Find("战士").Any(e=>e.Path=="/classes/warrior");
        checks["Chinese skill search"]=data.Find("血腥斩").Any(e=>e.Path=="/skills/bloody-cut");
        checks["English aliases case-insensitive"]=data.Find("WARRIOR").Any(e=>e.Path=="/classes/warrior");
        checks["empty query"]=data.Find("   ").Count==0;
        checks["unmatched query"]=data.Find("QATK_NO_MATCH_6947623").Count==0;
        checks["oversized query bounded"]=data.Find(new string('字',101)).Count==0;
        checks["bounded result list"]=data.Find("宝石").Count<=9;
        checks["Chinese titles on all entries"]=data.Snapshot.Entries.All(e=>Regex.IsMatch(e.Title,@"[\u3400-\u9fff]"));
        checks["article mention cannot become unrelated search hit"]=data.Find("附魔").All(e=>e.Title.Contains("附魔")||e.Aliases.Split('|').Any(a=>a.Contains("附魔")));
        foreach(var topic in new[]{("等级差","/mechanics/combat#level"),("等级差惩罚","/mechanics/leveling#penalty"),("离线进度","/mechanics/economy#offline"),("范围技能","/mechanics/combat#area"),("经验如何分配","/mechanics/leveling#sharing"),("附魔","/workbench#enchant")})
            checks["real section searchable: "+topic.Item1]=data.Find(topic.Item1).Any(e=>e.Path==topic.Item2);
        var levels=data.Find("等级差").FirstOrDefault(e=>e.Path=="/mechanics/combat#level");
        checks["level difference opens local quantitative table"]=levels is not null&&(data.Blocks(levels)??[]).Any(b=>b.Kind=="table"&&(b.Rows??[]).SelectMany(r=>r).Any(c=>c.Text=="×0.87"));
        checks["section exact match outranks broader topics"]=data.Find("等级差").FirstOrDefault()?.Title=="等级差";
        checks["unanchored gem section finds its real parent article"]=data.Find("宝箱开出的阶数几率").Any(e=>e.Path=="/gems");
        checks["quality selector name reaches its quantitative rarity reference"]=data.Find("优秀装备").Any(e=>e.Path=="/items#rarities");
        checks["furniture style selector name reaches local style reference"]=data.Find("质朴家具").Any(e=>e.Path=="/housing#styles");
        checks["unrelated body-only phrase is not a search keyword"]=data.Find("不包含任何玩家真实存档").Count==0;
        checks["one character does not populate unrelated hits"]=data.Find("附").Count==0;
        checks["missing local details are not shown as placeholder results"]=data.Snapshot.Entries.Where(e=>!data.details[e.Path].Specific).All(e=>!data.Find(e.Title).Any(r=>r.Path==e.Path));
        checks["all indexed entries have specific local details"]=data.Snapshot.Details.All(d=>d.Specific);
        var shield=data.Snapshot.Entries.First(e=>e.Path=="/passive-tree#node-5");
        checks["supplemented tree node searchable by Chinese name"]=data.Find(shield.Title).Any(e=>e.Path==shield.Path);
        checks["native tree display scaling, not raw values"]=data.Details(shield).Contains("吸收提高 +9%")&&data.Details(shield).Contains("格挡 +200");
        var renewal=data.Snapshot.Entries.First(e=>e.Path=="/passive-tree#node-11");
        checks["healing node uses native quarter scaling"]=data.Details(renewal).Contains("治疗量 +2%")&&data.Details(renewal).Contains("自动回复提高 +20%");
        checks["supplemented node images all embedded"]=data.Snapshot.Details.Where(d=>d.Path.StartsWith("/passive-tree#node-")).All(d=>d.Images.All(p=>WikiPictures.Local.Value.Get(p) is not null));
        checks["all captured pages have SHA256"]=data.Snapshot.Pages.All(p=>p.Sha256.Length==64);
        checks["Chinese detail offline"]=data.Details(data.Snapshot.Entries.First(e=>e.Path=="/classes/warrior")).Contains("战士");
        checks["exact Chinese result first"]=data.Find("战士")[0].Path=="/classes/warrior";
        checks["safe fixed origin"]=data.Snapshot.Entries.All(e=>SourceUri(e.Path).Host=="questaway.wiki"&&SourceUri(e.Path).Scheme=="https");
        checks["reject external or executable paths"]=new[]{"//evil.test","/../evil","/x%2f..","/x?token=bad","javascript:evil","/x\\evil","/x\nbad","https://evil.test"}.All(p=>!ValidPath(p));
        checks["no executable markup in local text"]=data.Snapshot.Pages.All(p=>!p.Text.Contains("<script",StringComparison.OrdinalIgnoreCase)&&!p.Text.Contains("<iframe",StringComparison.OrdinalIgnoreCase));
        checks["advertisements removed"]=data.Snapshot.Details.All(p=>!p.Text.Contains("广告"));
        checks["raw links removed"]=data.Snapshot.Details.All(p=>!p.Text.Contains("https://")&&!p.Text.Contains("http://"));
        checks["ruby detail is specific"]=data.Snapshot.Details.First(p=>p.Path=="/gems#ruby").Specific&&data.Details(data.Snapshot.Entries.First(e=>e.Path=="/gems#ruby")).Contains("红宝石");
        checks["ruby detail excludes other gem rows"]=!data.Details(data.Snapshot.Entries.First(e=>e.Path=="/gems#ruby")).Contains("中毒宝石");
        checks["website tools not search results"]=data.Snapshot.Entries.All(e=>e.Path is not "/my-save" and not "/party-builder" and not "/builds" and not "/farming" and not "/drop-finder");
        checks["shared-anchor aliases agree on detail"]=data.Snapshot.Details.GroupBy(p=>p.Path).All(g=>g.All(d=>d.Text==g.First().Text));
        checks["per-entry original icons retained"]=data.Snapshot.Entries.Where(e=>e.IconPath is not null).All(e=>WikiPictures.Local.Value.Get(e.IconPath) is not null);
        WikiPictures.Verify(checks);
        Program.Save(output,"wiki-result.json",new{Passed=checks.Values.All(v=>v),Cases=checks.Count,Checks=checks,Source="Real public static snapshot; controlled search/security checks, not gameplay",data.Snapshot.GameVersion,Entries=data.Snapshot.Entries.Length,Pages=data.Snapshot.Pages.Length});
        return checks.Values.All(v=>v)?0:1;
    }
}
