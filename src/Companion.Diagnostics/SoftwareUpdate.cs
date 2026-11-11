using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Companion.Diagnostics;

internal record UpdateRelease(string Version,string Notes,Uri Download,string Sha256,long Size)
{
    public string? ExecutableSha256 {get;init;}
    public long ExecutableSize {get;init;}
}
internal record UpdateProgress(long Received,long Total);
internal record AfkUpdateState(GameWindow Target,GameWindow[] Windows,Box Bounds,bool Hidden);
internal record UpdateJob(string Target,string TargetSha256,string Payload,string PayloadSha256,int ParentPid,long ParentStartTicks,AfkUpdateState? Afk=null,GameConnectionChoice? Connection=null);

internal static class SoftwareUpdate
{
    internal const string Repository="sangshiCHI/QATracker";
    internal const string LatestUrl="https://api.github.com/repos/"+Repository+"/releases/latest";
    internal const string FeedPath="companion/releases/latest.json";
    internal const string CdnUrl="https://cdn.jsdelivr.net/gh/"+Repository+"@main/"+FeedPath;
    internal const string RawUrl="https://raw.githubusercontent.com/"+Repository+"/main/"+FeedPath;
    internal const string DomesticRepository="sangshichi/QATracker";
    internal const string DomesticUrl="https://gitee.com/"+DomesticRepository+"/raw/main/"+FeedPath;
    internal const string DomesticRawUrl="https://raw.giteeusercontent.com/"+DomesticRepository+"/raw/main/"+FeedPath;
    internal const long MaximumSize=512L*1024*1024;
    internal static readonly Version CurrentVersion=typeof(SoftwareUpdate).Assembly.GetName().Version??new(0,1,0,0);
    internal static string DisplayVersion=>CurrentVersion.ToString(CurrentVersion.Revision>0?4:CurrentVersion.Build>0?3:2);
    internal static string UpdateRoot=>Path.Combine(CompanionSettings.DataDirectory,"Updates");
    static HttpClient Client()=>new(new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=false,UseDefaultCredentials=false}){Timeout=TimeSpan.FromSeconds(20)};
    internal static bool SafeUrl(Uri uri,bool api=false)
    {
        if(uri.Scheme!="https"||!uri.IsDefaultPort||uri.UserInfo.Length!=0)return false;
        if(api)return uri.AbsoluteUri is LatestUrl or CdnUrl or RawUrl or DomesticUrl or DomesticRawUrl||
            uri.Fragment.Length==0&&uri.GetLeftPart(UriPartial.Path)==DomesticRawUrl&&
            Regex.IsMatch(uri.Query,@"\A\?signature=[A-Za-z0-9_~%+./=-]{1,2048}\z",RegexOptions.CultureInvariant);
        return uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "foruda.gitee.com"||
            uri.Host=="github.com"&&uri.AbsolutePath.StartsWith("/"+Repository+"/releases/download/",StringComparison.Ordinal)||
            uri.Host=="gitee.com"&&(uri.AbsolutePath.StartsWith("/"+DomesticRepository+"/releases/download/",StringComparison.Ordinal)||Regex.IsMatch(uri.AbsolutePath,"^/"+DomesticRepository+@"/attach_files/\d+/download/[^/]+$"));
    }
    static async Task<HttpResponseMessage> Get(HttpClient client,Uri url,bool api,CancellationToken token)
    {
        for(var i=0;i<6;i++)
        {
            if(!SafeUrl(url,api))throw new InvalidDataException("untrusted_update_url");
            using var request=new HttpRequestMessage(HttpMethod.Get,url);request.Headers.UserAgent.ParseAdd("QATK/0.1");
            if(api)request.Headers.Accept.ParseAdd("application/vnd.github+json");
            var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false);
            if((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))return response;
            var location=response.Headers.Location;response.Dispose();if(location is null)throw new InvalidDataException("update_redirect_missing");
            url=location.IsAbsoluteUri?location:new Uri(url,location);
        }
        throw new InvalidDataException("update_redirect_limit");
    }
    internal static UpdateRelease? Parse(string json)
    {
        using var doc=JsonDocument.Parse(json);var r=doc.RootElement;
        if(r.GetProperty("draft").GetBoolean()||r.GetProperty("prerelease").GetBoolean())return null;
        var tag=r.GetProperty("tag_name").GetString()??"";
        if(!Regex.IsMatch(tag,@"^v?\d+\.\d+(?:\.\d+){0,2}$"))throw new InvalidDataException("invalid_update_version");
        var parts=tag.TrimStart('v').Split('.');var normalized=string.Join('.',parts.Concat(Enumerable.Repeat("0",4-parts.Length)));
        if(!Version.TryParse(normalized,out var version))throw new InvalidDataException("invalid_update_version");
        if(version<=CurrentVersion)return null;
        var notes=r.TryGetProperty("body",out var body)?body.GetString()??"":"";
        if(string.IsNullOrWhiteSpace(notes))throw new InvalidDataException("update_notes_missing");
        if(notes.Length>32000)notes=notes[..32000]+"\n（更新说明过长，已截断）";
        var all=r.GetProperty("assets").EnumerateArray().ToArray();
        // Native GitHub asset JSON has no inner-EXE metadata. Keep its verified EXE fallback usable.
        var zipped=all.Any(a=>a.GetProperty("name").GetString()=="QATK.zip"&&a.TryGetProperty("executable_digest",out _));
        var filename=zipped?"QATK.zip":"QATK.exe";
        var assets=all.Where(a=>a.GetProperty("name").GetString()==filename).ToArray();
        if(assets.Length!=1)throw new InvalidDataException("update_asset_missing_or_ambiguous");
        var asset=assets[0];var digest=asset.GetProperty("digest").GetString()??"";var size=asset.GetProperty("size").GetInt64();
        if(!Regex.IsMatch(digest,@"^sha256:[a-fA-F0-9]{64}$")||size<1||size>MaximumSize)throw new InvalidDataException("invalid_update_integrity");
        if(!Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(),UriKind.Absolute,out var url)||
            (url.AbsoluteUri!="https://github.com/"+Repository+"/releases/download/"+tag+"/"+filename&&url.AbsoluteUri!="https://gitee.com/"+DomesticRepository+"/releases/download/"+tag+"/"+filename))throw new InvalidDataException("untrusted_update_asset");
        var release=new UpdateRelease(tag,notes,url,digest[7..].ToUpperInvariant(),size);
        if(zipped)
        {
            var inner=asset.GetProperty("executable_digest").GetString()??"";var innerSize=asset.GetProperty("executable_size").GetInt64();
            if(!Regex.IsMatch(inner,@"^sha256:[a-fA-F0-9]{64}$")||innerSize<1||innerSize>MaximumSize)throw new InvalidDataException("invalid_update_executable_integrity");
            release=release with{ExecutableSha256=inner[7..].ToUpperInvariant(),ExecutableSize=innerSize};
        }
        return release;
    }
    internal static async Task<UpdateRelease?> Check(CancellationToken token,HttpClient? supplied=null)
    {
        using var owned=supplied is null?Client():null;var client=supplied??owned!;
        // A missing mirror file is not proof of no release. Query official API as well.
        async Task<(bool Valid,UpdateRelease? Release)> Read(string url)
        {
            using var limit=CancellationTokenSource.CreateLinkedTokenSource(token);limit.CancelAfter(TimeSpan.FromSeconds(10));
            try{return (true,await ReadFeed(client,url,limit.Token).ConfigureAwait(false));}
            catch(Exception e)when(!token.IsCancellationRequested&&e is HttpRequestException or IOException or InvalidDataException or JsonException or InvalidOperationException or FormatException or KeyNotFoundException or OperationCanceledException){return (false,null);}
        }
        var feeds=await Task.WhenAll(new[]{DomesticUrl,DomesticRawUrl,CdnUrl,RawUrl,LatestUrl}.Select(Read)).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if(!feeds.Any(f=>f.Valid))throw new HttpRequestException("update_sources_unavailable");
        var releases=feeds.Where(f=>f.Release is not null).Select(f=>f.Release!).ToArray();
        foreach(var group in releases.GroupBy(r=>ReleaseVersion(r.Version)))
            if(group.Select(r=>(r.ExecutableSha256??r.Sha256,r.ExecutableSha256 is null?r.Size:r.ExecutableSize)).Distinct().Count()>1)throw new InvalidDataException("update_sources_conflict");
        return releases.OrderByDescending(r=>ReleaseVersion(r.Version)).ThenByDescending(r=>r.Download.Host=="gitee.com").FirstOrDefault();
    }
    static Version ReleaseVersion(string tag)
    {
        var parts=tag.TrimStart('v').Split('.');return Version.Parse(string.Join('.',parts.Concat(Enumerable.Repeat("0",4-parts.Length))));
    }
    static async Task<UpdateRelease?> ReadFeed(HttpClient client,string url,CancellationToken token)
    {
        using var response=await Get(client,new Uri(url),true,token).ConfigureAwait(false);
        if(response.StatusCode==HttpStatusCode.NotFound&&url==LatestUrl)return null;
        response.EnsureSuccessStatusCode();
        await using var stream=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var bytes=new MemoryStream();var buffer=new byte[16384];int read;
        while((read=await stream.ReadAsync(buffer,token).ConfigureAwait(false))>0){if(bytes.Length+read>1024*1024)throw new InvalidDataException("update_metadata_too_large");bytes.Write(buffer,0,read);}
        return Parse(System.Text.Encoding.UTF8.GetString(bytes.ToArray()));
    }
    internal static string Hash(string file){using var stream=File.OpenRead(file);return Convert.ToHexString(SHA256.HashData(stream));}
    internal static void Verify(string file,string hash)
    {
        if(!Regex.IsMatch(hash,@"^[A-Fa-f0-9]{64}$")||Hash(file)!=hash.ToUpperInvariant())throw new InvalidDataException("update_hash_mismatch");
        using var stream=File.OpenRead(file);using var pe=new PEReader(stream);
        if(pe.PEHeaders.CoffHeader.Machine!=Machine.Amd64||pe.PEHeaders.PEHeader?.Magic!=PEMagic.PE32Plus)throw new InvalidDataException("update_not_win_x64");
    }
    internal static async Task<string> Download(UpdateRelease release,string directory,CancellationToken token,HttpClient? supplied=null,IProgress<UpdateProgress>? progress=null)
    {
        Directory.CreateDirectory(directory);var part=Path.Combine(directory,"download.part");var result=Path.Combine(directory,"QATK.new");
        using var owned=supplied is null?Client():null;var client=supplied??owned!;
        using var overall=CancellationTokenSource.CreateLinkedTokenSource(token);overall.CancelAfter(TimeSpan.FromMinutes(30));
        using var idle=CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
        var readToken=idle.Token;
        var created=false;
        try
        {
            idle.CancelAfter(TimeSpan.FromSeconds(30));
            using var response=await Get(client,release.Download,false,readToken).ConfigureAwait(false);response.EnsureSuccessStatusCode();
            if(response.Content.Headers.ContentLength is long length&&length!=release.Size)throw new InvalidDataException("update_length_mismatch");
            await using(var input=await response.Content.ReadAsStreamAsync(readToken).ConfigureAwait(false))
            await using(var output=new FileStream(part,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true))
            {
                created=true;
                var buffer=new byte[65536];long total=0;int n;var clock=Stopwatch.StartNew();long last=-250;
                progress?.Report(new(0,release.Size));
                while(true)
                {
                    idle.CancelAfter(TimeSpan.FromSeconds(30));n=await input.ReadAsync(buffer,readToken).ConfigureAwait(false);if(n==0)break;
                    total+=n;if(total>release.Size||total>MaximumSize)throw new InvalidDataException("update_too_large");await output.WriteAsync(buffer.AsMemory(0,n),readToken).ConfigureAwait(false);
                    if(clock.ElapsedMilliseconds-last>=250){progress?.Report(new(total,release.Size));last=clock.ElapsedMilliseconds;}
                }
                if(total!=release.Size)throw new InvalidDataException("update_length_mismatch");
            }
            token.ThrowIfCancellationRequested();
            if(release.ExecutableSha256 is not null)
            {
                if(Hash(part)!=release.Sha256.ToUpperInvariant())throw new InvalidDataException("update_hash_mismatch");
                await ExtractExecutable(part,result,release.ExecutableSha256,release.ExecutableSize,overall.Token).ConfigureAwait(false);
                File.Delete(part);
            }
            else{Verify(part,release.Sha256);File.Move(part,result);}
            return result;
        }
        catch(OperationCanceledException)when(!token.IsCancellationRequested){if(created&&File.Exists(part))File.Delete(part);throw new IOException("update_download_timeout");}
        catch{if(created&&File.Exists(part))File.Delete(part);throw;}
    }
    internal static async Task ExtractExecutable(string zip,string result,string hash,long size,CancellationToken token)
    {
        if(size<1||size>MaximumSize)throw new InvalidDataException("invalid_update_executable_size");
        using var archive=ZipFile.OpenRead(zip);
        if(archive.Entries.Count>128||archive.Entries.Any(e=>e.FullName.Contains('\\')||e.FullName.Split('/').Any(p=>p is "." or "..")||e.FullName.StartsWith('/')||e.FullName.Contains(':')||((e.ExternalAttributes>>16)&0xF000)==0xA000))throw new InvalidDataException("unsafe_update_archive");
        var entries=archive.Entries.Where(e=>e.FullName.Equals("QATK.exe",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(entries.Length!=1||entries[0].FullName!="QATK.exe"||entries[0].Length!=size)throw new InvalidDataException("invalid_update_archive_player");
        var created=false;
        try
        {
            await using(var input=entries[0].Open())
            await using(var output=new FileStream(result,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true))
            {
                created=true;var buffer=new byte[65536];long total=0;int read;
                while((read=await input.ReadAsync(buffer,token).ConfigureAwait(false))>0){total+=read;if(total>size)throw new InvalidDataException("update_executable_too_large");await output.WriteAsync(buffer.AsMemory(0,read),token).ConfigureAwait(false);}
                if(total!=size)throw new InvalidDataException("update_executable_length_mismatch");
            }
            token.ThrowIfCancellationRequested();Verify(result,hash);
        }
        catch{if(created&&File.Exists(result))File.Delete(result);throw;}
    }
    internal static bool ValidAfkState(AfkUpdateState state)=>state is{Target:not null,Windows:{Length:>0 and <=8},Bounds:{Width:>=240 and <=16384,Height:>=150 and <=16384}}&&
        state.Target.Pid>0&&state.Windows.Any(w=>w==state.Target)&&state.Windows.All(w=>w is not null&&w.Pid==state.Target.Pid&&w.StartUtc==state.Target.StartUtc&&string.Equals(w.Exe,state.Target.Exe,StringComparison.OrdinalIgnoreCase)&&!string.IsNullOrWhiteSpace(w.Exe)&&Path.IsPathFullyQualified(w.Exe)&&Regex.IsMatch(w.Hwnd??"",@"\A0x[0-9a-fA-F]{1,16}\z"));
    internal static void RestoreUpdateWindows(AfkUpdateState? state)
    {
        if(state is null||!ValidAfkState(state))return;
        foreach(var window in state.Windows.Reverse())try{if(Native.SameProcess(window)&&Native.Follow(window) is{Minimized:true})Native.SetMinimized(window,false);}catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){ }
    }
    internal static UpdateJob ReadResume(string plan)
    {
        var full=Path.GetFullPath(plan);var root=Path.GetFullPath(UpdateRoot)+Path.DirectorySeparatorChar;
        if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase)||Path.GetFileName(full)!="update-job.json"||new FileInfo(full).Length>65536)throw new InvalidDataException("invalid_update_resume_location");
        var job=JsonSerializer.Deserialize<UpdateJob>(File.ReadAllText(full))??throw new InvalidDataException("invalid_update_resume");
        if(!string.Equals(Path.GetFullPath(job.Target),Environment.ProcessPath,StringComparison.OrdinalIgnoreCase)||job.Afk is{} afk&&!ValidAfkState(afk))throw new InvalidDataException("invalid_update_resume_target");
        if(!ValidConnection(job.Connection,job.Afk))throw new InvalidDataException("invalid_update_connection");
        Verify(job.Target,job.PayloadSha256);return job;
    }
    internal static void ConfirmResume(string plan)
    {
        var ready=Path.Combine(Path.GetDirectoryName(plan)!,"resumed");
        File.WriteAllText(ready+".tmp",Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));File.Move(ready+".tmp",ready,false);
    }
    internal static bool ValidConnection(GameConnectionChoice? connection,AfkUpdateState? afk=null)=>connection is null||
        connection.Process is{Pid:>0,StartUtc:not null,Error:null,Exe:not null} p&&p.StartUtc.Value.Kind==DateTimeKind.Utc&&
        !string.IsNullOrWhiteSpace(p.Exe)&&Path.IsPathFullyQualified(p.Exe)&&!string.IsNullOrWhiteSpace(connection.SavePath)&&Path.IsPathFullyQualified(connection.SavePath)&&
        (afk is null||p.Pid==afk.Target.Pid&&p.StartUtc==afk.Target.StartUtc&&string.Equals(p.Exe,afk.Target.Exe,StringComparison.OrdinalIgnoreCase));
    internal static void LaunchInstaller(string payload,AfkUpdateState? afk=null,GameConnectionChoice? connection=null)
    {
        var target=Environment.ProcessPath??throw new IOException("update_target_missing");
        if(!Path.GetFileName(target).Equals("QATK.exe",StringComparison.OrdinalIgnoreCase))throw new IOException("update_target_not_player");
        var directory=Path.GetDirectoryName(payload)!;var runner=Path.Combine(directory,"runner.exe");
        File.Copy(target,runner,false);using var parent=Process.GetCurrentProcess();
        if(afk is not null&&!ValidAfkState(afk))throw new InvalidDataException("invalid_afk_handoff");
        if(!ValidConnection(connection,afk))throw new InvalidDataException("invalid_update_connection");
        var job=new UpdateJob(target,Hash(target),payload,Hash(payload),parent.Id,parent.StartTime.ToUniversalTime().Ticks,afk,connection);
        var plan=Path.Combine(directory,"update-job.json");File.WriteAllText(plan,JsonSerializer.Serialize(job,Program.Json));
        var info=new ProcessStartInfo(runner){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};info.ArgumentList.Add("--apply-update");info.ArgumentList.Add(plan);
        using var helper=Process.Start(info)??throw new IOException("update_helper_start_failed");
        var ready=Path.Combine(directory,"ready");var deadline=Stopwatch.StartNew();
        while(!File.Exists(ready)&&!helper.HasExited&&deadline.Elapsed<TimeSpan.FromSeconds(15))Thread.Sleep(50);
        if(!File.Exists(ready)){File.WriteAllText(Path.Combine(directory,"cancel"),"cancel");throw new IOException("update_helper_not_ready");}
    }
    internal static void ReplaceFile(string payload,string target,string backup)
    {
        var sibling=target+".update-new";
        if(File.Exists(sibling))throw new IOException("update_staging_conflict");
        var copied=false;
        try{File.Copy(payload,sibling,false);copied=true;File.Replace(sibling,target,backup);}
        finally{if(copied&&File.Exists(sibling))File.Delete(sibling);}
    }
    internal static int Apply(string plan)
    {
        var directory=Path.GetDirectoryName(Path.GetFullPath(plan))!;var backup="";UpdateJob? job=null;bool replaced=false,parentExited=false;
        try
        {
            var expectedRoot=Path.GetFullPath(UpdateRoot)+Path.DirectorySeparatorChar;
            if(!directory.StartsWith(expectedRoot,StringComparison.OrdinalIgnoreCase)||Path.GetDirectoryName(Environment.ProcessPath)!=directory||Path.GetFileName(plan)!="update-job.json")throw new InvalidDataException("invalid_update_plan_location");
            job=JsonSerializer.Deserialize<UpdateJob>(File.ReadAllText(plan))??throw new InvalidDataException("invalid_update_plan");
            if(!ValidConnection(job.Connection,job.Afk))throw new InvalidDataException("invalid_update_connection");
            if(!Path.IsPathFullyQualified(job.Target)||Path.GetFileName(job.Target)!="QATK.exe"||Path.GetFullPath(job.Payload)!=Path.Combine(directory,"QATK.new"))throw new InvalidDataException("invalid_update_paths");
            using(var parent=Process.GetProcessById(job.ParentPid))
            {
                if(parent.StartTime.ToUniversalTime().Ticks!=job.ParentStartTicks||!string.Equals(parent.MainModule?.FileName,job.Target,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("update_parent_mismatch");
                File.WriteAllText(Path.Combine(directory,"ready"),"ready");
                if(!parent.WaitForExit(60000))throw new IOException("update_parent_still_running");
                parentExited=true;
            }
            if(File.Exists(Path.Combine(directory,"cancel")))throw new IOException("update_cancelled");
            Verify(job.Payload,job.PayloadSha256);Verify(job.Target,job.TargetSha256);
            backup=job.Target+".previous-"+Guid.NewGuid().ToString("N")+".bak";
            ReplaceFile(job.Payload,job.Target,backup);replaced=true;Verify(job.Target,job.PayloadSha256);
            var launch=new ProcessStartInfo(job.Target){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(job.Target)!,WindowStyle=ProcessWindowStyle.Hidden};launch.ArgumentList.Add("--update-handoff");launch.ArgumentList.Add(plan);
            using(var started=Process.Start(launch)??throw new IOException("updated_player_start_failed"))
            {
                var clock=Stopwatch.StartNew();var ready=Path.Combine(directory,"resumed");bool confirmed=false;
                while(!started.HasExited&&clock.Elapsed<TimeSpan.FromSeconds(30))
                {
                    if(File.Exists(ready)&&File.ReadAllText(ready)==started.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)){confirmed=true;break;}
                    Thread.Sleep(100);
                }
                if(!confirmed){if(!started.HasExited){started.Kill();started.WaitForExit(5000);}throw new IOException("updated_player_resume_unconfirmed");}
            }
            File.WriteAllText(Path.Combine(directory,"result.json"),JsonSerializer.Serialize(new{Passed=true,Backup=backup},Program.Json));return 0;
        }
        catch(Exception e)
        {
            if(parentExited)RestoreUpdateWindows(job?.Afk);
            if(replaced&&job is not null&&File.Exists(backup))
            {
                try{File.Replace(backup,job.Target,null);Process.Start(new ProcessStartInfo(job.Target){UseShellExecute=false,WindowStyle=ProcessWindowStyle.Hidden});}catch{ }
            }
            else if(parentExited&&job is not null)
            {
                try{Verify(job.Target,job.TargetSha256);Process.Start(new ProcessStartInfo(job.Target){UseShellExecute=false,WindowStyle=ProcessWindowStyle.Hidden});}catch{ }
            }
            try{File.WriteAllText(Path.Combine(directory,"result.json"),JsonSerializer.Serialize(new{Passed=false,Error=e.GetType().Name,Code=e.Message},Program.Json));}catch{ }
            return 1;
        }
    }
}
