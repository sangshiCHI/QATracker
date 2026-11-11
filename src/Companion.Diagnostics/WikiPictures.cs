using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace Companion.Diagnostics;

internal sealed record WikiPicture(string File,string Sha256,string SourceSha256,int Width,int Height);
internal sealed record WikiPictureManifest(int Version,int ExpectedImages,bool Complete,
    Dictionary<string,string> EntryIcons,Dictionary<string,string[]> PageImages,Dictionary<string,WikiPicture> Images);

internal sealed class WikiPictures
{
    public static readonly Lazy<WikiPictures> Local=new(()=>new());
    public WikiPictureManifest Manifest{get;}
    readonly ZipArchive archive;
    readonly Dictionary<string,BitmapSource> cache=new(StringComparer.Ordinal);
    readonly Queue<string> order=new();
    long cachedBytes;
    internal int CachedCount=>cache.Count;
    WikiPictures()
    {
        var assembly=Assembly.GetExecutingAssembly();
        using var json=assembly.GetManifestResourceStream("wiki-images.json")??throw new InvalidDataException("缺少本地插图目录。");
        Manifest=JsonSerializer.Deserialize<WikiPictureManifest>(json)??throw new InvalidDataException("插图目录为空。");
        archive=new(assembly.GetManifestResourceStream("wiki-images.zip")??throw new InvalidDataException("缺少本地插图包。"),ZipArchiveMode.Read);
        if(Manifest.Version!=1||Manifest.Images.Any(p=>!SafePath(p.Key)||!Regex.IsMatch(p.Value.File,@"\A[a-f0-9]{64}\.png\z")||
           p.Value.Width<1||p.Value.Height<1||p.Value.Width>8192||p.Value.Height>8192||
           (long)p.Value.Width*p.Value.Height>16000000||archive.GetEntry(p.Value.File) is null))
            throw new InvalidDataException("本地插图目录损坏。");
    }
    internal static bool SafePath(string path)=>path.Length<250&&Regex.IsMatch(path,@"\A/game/[a-zA-Z0-9/_-]+\.webp\z");
    public string? IconPath(string entry)=>Manifest.EntryIcons.GetValueOrDefault(entry);
    public BitmapSource? Icon(string entry)=>Get(IconPath(entry));
    public BitmapSource? Get(string? path,int width=128)
    {
        if(path is null||!SafePath(path)||!Manifest.Images.TryGetValue(path,out var info))return null;
        width=Math.Clamp(width,16,1200);var key=path+"@"+width;
        if(cache.TryGetValue(key,out var existing))return existing;
        try
        {
            var entry=archive.GetEntry(info.File)!;if(entry.Length>64000000)return null;
            using var source=entry.Open();using var stream=new MemoryStream();source.CopyTo(stream);
            if(!Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0,(int)stream.Length))).Equals(info.Sha256,StringComparison.OrdinalIgnoreCase))return null;
            stream.Position=0;
            var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;
            image.DecodePixelWidth=Math.Min(width,info.Width);image.StreamSource=stream;image.EndInit();image.Freeze();
            var bytes=(long)image.PixelWidth*image.PixelHeight*4;
            while(cache.Count>0&&(cache.Count>=96||cachedBytes+bytes>32*1024*1024))
            {var removed=order.Dequeue();var old=cache[removed];cachedBytes-=(long)old.PixelWidth*old.PixelHeight*4;cache.Remove(removed);}
            if(bytes<=32*1024*1024){cache.Add(key,image);order.Enqueue(key);cachedBytes+=bytes;}
            return image;
        }
        catch(Exception e)when(e is IOException or NotSupportedException or System.Runtime.InteropServices.COMException){return null;}
    }
    public static void Verify(Dictionary<string,bool> checks)
    {
        var data=Local.Value;
        checks["all declared pictures embedded"]=data.Manifest.Complete&&data.Manifest.Images.Count==data.Manifest.ExpectedImages;
        checks["all picture files decode and hash match"]=data.Manifest.Images.Keys.All(p=>data.Get(p) is not null);
        checks["bitmap cache bounded"]=data.CachedCount<=96;
        checks["bitmap cache byte budget bounded"]=data.cachedBytes<=32*1024*1024;
        checks["loaded bitmap frozen"]=data.Manifest.Images.Keys.Take(12).All(p=>data.Get(p)?.IsFrozen==true);
        checks["all indexed icons resolve locally"]=data.Manifest.EntryIcons.Values.All(p=>data.Manifest.Images.ContainsKey(p));
        checks["external paths never loaded"]=new[]{"https://evil.test/a.png","file:///c:/x.png","/game/../x.webp","//evil/x.webp","/game/x.webp?token=x"}.All(p=>data.Get(p) is null);
        checks["missing picture is explicit null"]=data.Get("/game/qatk-does-not-exist.webp") is null;
    }
}
