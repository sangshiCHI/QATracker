using System.Net;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace Companion.Diagnostics;

internal static class UpdateVerification
{
    sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){token.ThrowIfCancellationRequested();return Task.FromResult(respond(request));}}
    sealed class InlineProgress(Action<UpdateProgress> action):IProgress<UpdateProgress>{public void Report(UpdateProgress value)=>action(value);}
    sealed class StalledStream():MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default){await Task.Delay(Timeout.Infinite,token);return 0;}
    }
    internal static async Task<int> Run(string output)
    {
        Directory.CreateDirectory(output);var checks=new Dictionary<string,bool>();
        var sample=new JsonObject{["draft"]=false,["prerelease"]=false,["tag_name"]="v0.2",["body"]="修复窗口跟随\n改进百科搜索",["assets"]=new JsonArray(new JsonObject{["name"]="QATK.exe",["size"]=100,["digest"]="sha256:"+new string('a',64),["browser_download_url"]="https://github.com/sangshiCHI/QATracker/releases/download/v0.2/QATK.exe"})};
        bool Rejected(Action<JsonObject> change){var copy=(JsonObject)sample.DeepClone();change(copy);try{SoftwareUpdate.Parse(copy.ToJsonString());return false;}catch(Exception e)when(e is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException){return true;}}
        async Task<bool> Fails(Func<Task> action){try{await action();return false;}catch(Exception e)when(e is IOException or InvalidDataException or HttpRequestException or OperationCanceledException){return true;}}
        var release=SoftwareUpdate.Parse(sample.ToJsonString())!;
        checks["new stable version with version and changelog"]=release.Version=="v0.2"&&release.Notes.Contains("窗口跟随");
        foreach(var field in new[]{"draft","prerelease"}){var copy=sample.DeepClone();copy[field]=true;checks["ignore "+field]=SoftwareUpdate.Parse(copy.ToJsonString()) is null;}
        var same=sample.DeepClone();same["tag_name"]="v0.1";checks["same version never prompts"]=SoftwareUpdate.Parse(same.ToJsonString()) is null;
        var old=sample.DeepClone();old["tag_name"]="v0.0.9";checks["older version never downgrades"]=SoftwareUpdate.Parse(old.ToJsonString()) is null;
        checks["bad version rejected"]=Rejected(r=>r["tag_name"]="v0.2;calc");
        checks["missing changelog rejected"]=Rejected(r=>r["body"]="");
        checks["missing checksum rejected"]=Rejected(r=>r["assets"]![0]!["digest"]=null);
        checks["bad checksum rejected"]=Rejected(r=>r["assets"]![0]!["digest"]="sha256:bad");
        checks["oversize asset rejected"]=Rejected(r=>r["assets"]![0]!["size"]=SoftwareUpdate.MaximumSize+1);
        checks["missing player asset rejected"]=Rejected(r=>r["assets"]![0]!["name"]="unknown.exe");
        checks["other repository rejected"]=Rejected(r=>r["assets"]![0]!["browser_download_url"]="https://github.com/other/repo/releases/download/v0.2/QATK.exe");
        checks["tag mismatch rejected"]=Rejected(r=>r["assets"]![0]!["browser_download_url"]="https://github.com/sangshiCHI/QATracker/releases/download/v0.3/QATK.exe");
        checks["http file credentials and unknown hosts rejected"]=new[]{"http://github.com/sangshiCHI/QATracker/releases/download/v0.2/QATK.exe","file:///C:/QATK.exe","https://evil.example/file.exe","https://user:pass@github.com/sangshiCHI/QATracker/releases/download/v0.2/QATK.exe"}.All(s=>!SoftwareUpdate.SafeUrl(new(s)));
        bool readonlyRequest=false;
        using(var client=new HttpClient(new Handler(request=>{readonlyRequest=request.Method==HttpMethod.Get&&request.Headers.Authorization is null&&request.Content is null&&request.Headers.UserAgent.ToString()=="QATK/0.1";return new(HttpStatusCode.NotFound);}))){checks["no releases does not invent an update"]=await SoftwareUpdate.Check(CancellationToken.None,client) is null;}
        checks["update check anonymous read only"]=readonlyRequest;
        checks["metadata allowlist does not extend executable trust"]=SoftwareUpdate.SafeUrl(new(SoftwareUpdate.CdnUrl),true)&&!SoftwareUpdate.SafeUrl(new(SoftwareUpdate.CdnUrl))&&!SoftwareUpdate.SafeUrl(new("https://cdn.jsdelivr.net/gh/other/repo@main/latest.json"),true);
        using(var client=new HttpClient(new Handler(r=>r.RequestUri!.AbsoluteUri==SoftwareUpdate.CdnUrl?new(HttpStatusCode.OK){Content=new StringContent(sample.ToJsonString())}:new(HttpStatusCode.Forbidden))))checks["free metadata mirror works when API and raw unavailable"]=(await SoftwareUpdate.Check(CancellationToken.None,client))?.Version=="v0.2";
        using(var client=new HttpClient(new Handler(r=>r.RequestUri!.AbsoluteUri==SoftwareUpdate.LatestUrl?new(HttpStatusCode.OK){Content=new StringContent(sample.ToJsonString())}:new(HttpStatusCode.NotFound))))checks["missing mirror falls back to official release"]=(await SoftwareUpdate.Check(CancellationToken.None,client))?.Version=="v0.2";
        using(var client=new HttpClient(new Handler(r=>new(r.RequestUri!.AbsoluteUri==SoftwareUpdate.LatestUrl?HttpStatusCode.Forbidden:HttpStatusCode.NotFound))))checks["missing mirror and blocked API is not latest"]=await Fails(async()=>{await SoftwareUpdate.Check(CancellationToken.None,client);});
        using(var client=new HttpClient(new Handler(r=>{var copy=(JsonObject)sample.DeepClone();if(r.RequestUri!.AbsoluteUri==SoftwareUpdate.LatestUrl){copy["tag_name"]="v0.3";copy["assets"]![0]!["browser_download_url"]="https://github.com/sangshiCHI/QATracker/releases/download/v0.3/QATK.exe";}return new(HttpStatusCode.OK){Content=new StringContent(copy.ToJsonString())};})))checks["stale mirror does not hide newer official release"]=(await SoftwareUpdate.Check(CancellationToken.None,client))?.Version=="v0.3";
        using(var client=new HttpClient(new Handler(r=>{var copy=(JsonObject)sample.DeepClone();if(r.RequestUri!.AbsoluteUri==SoftwareUpdate.LatestUrl)copy["assets"]![0]!["digest"]="sha256:"+new string('b',64);return new(HttpStatusCode.OK){Content=new StringContent(copy.ToJsonString())};})))checks["same version conflicting hashes rejected"]=await Fails(async()=>{await SoftwareUpdate.Check(CancellationToken.None,client);});
        checks["asset filename and query must match exact release"]=Rejected(r=>r["assets"]![0]!["browser_download_url"]="https://github.com/sangshiCHI/QATracker/releases/download/v0.2/not-QATK.exe")&&Rejected(r=>r["assets"]![0]!["browser_download_url"]="https://github.com/sangshiCHI/QATracker/releases/download/v0.2/QATK.exe?other=1");
        var domestic=(JsonObject)sample.DeepClone();domestic["assets"]![0]!["name"]="QATK.zip";domestic["assets"]![0]!["browser_download_url"]="https://gitee.com/sangshichi/QATracker/releases/download/v0.2/QATK.zip";domestic["assets"]![0]!["executable_digest"]="sha256:"+new string('b',64);domestic["assets"]![0]!["executable_size"]=200;
        checks["domestic zip metadata retains inner executable integrity"]=SoftwareUpdate.Parse(domestic.ToJsonString()) is {ExecutableSize:200,ExecutableSha256:not null};
        var missingInner=(JsonObject)domestic.DeepClone();missingInner["assets"]![0]!["executable_digest"]="";
        checks["zip without inner checksum rejected"]=await Fails(()=>{SoftwareUpdate.Parse(missingInner.ToJsonString());return Task.CompletedTask;});
        using(var client=new HttpClient(new Handler(r=>r.RequestUri!.AbsoluteUri==SoftwareUpdate.DomesticUrl?new(HttpStatusCode.OK){Content=new StringContent(domestic.ToJsonString())}:new(HttpStatusCode.Forbidden))))checks["domestic feed works without github"]=(await SoftwareUpdate.Check(CancellationToken.None,client))?.Download.Host=="gitee.com";
        checks["domestic metadata redirects are repository scoped"]=SoftwareUpdate.SafeUrl(new(SoftwareUpdate.DomesticRawUrl),true)&&!SoftwareUpdate.SafeUrl(new("https://raw.giteeusercontent.com/other/repo/raw/main/companion/releases/latest.json"),true);
        var signed=SoftwareUpdate.DomesticRawUrl+"?signature=Public_Test-012%3D";
        checks["domestic signed metadata URL accepted only as metadata"]=SoftwareUpdate.SafeUrl(new(signed),true)&&!SoftwareUpdate.SafeUrl(new(signed));
        checks["signed metadata trust stays repository and file scoped"]=!SoftwareUpdate.SafeUrl(new("https://raw.giteeusercontent.com/other/repo/raw/main/companion/releases/latest.json?signature=x"),true)&&!SoftwareUpdate.SafeUrl(new(SoftwareUpdate.DomesticRawUrl.Replace("latest.json","other.json")+"?signature=x"),true);
        checks["signed metadata rejects unknown parameters fragments and oversized signatures"]=new[]{SoftwareUpdate.DomesticRawUrl+"?signature=x&next=evil",SoftwareUpdate.DomesticRawUrl+"?token=x",SoftwareUpdate.DomesticRawUrl+"?signature=x#other",SoftwareUpdate.DomesticRawUrl+"?signature="+new string('a',2049)}.All(u=>!SoftwareUpdate.SafeUrl(new(u),true));
        try{
            using var client=new HttpClient(new Handler(r=>{if(r.RequestUri!.AbsoluteUri==SoftwareUpdate.DomesticUrl){var redirect=new HttpResponseMessage(HttpStatusCode.Redirect);redirect.Headers.Location=new(signed);return redirect;}return r.RequestUri.AbsoluteUri==signed?new(HttpStatusCode.OK){Content=new StringContent(domestic.ToJsonString())}:new(HttpStatusCode.Forbidden);}));
            checks["signed domestic redirect works when all GitHub sources fail"]=(await SoftwareUpdate.Check(CancellationToken.None,client))?.Download.Host=="gitee.com";
        }catch(InvalidDataException){checks["signed domestic redirect works when all GitHub sources fail"]=false;}
        try{
            using var client=new HttpClient(new Handler(r=>{if(r.RequestUri!.AbsoluteUri==SoftwareUpdate.DomesticUrl){var redirect=new HttpResponseMessage(HttpStatusCode.Redirect);redirect.Headers.Location=new("https://evil.example/latest.json");return redirect;}return r.RequestUri.AbsoluteUri==SoftwareUpdate.LatestUrl?new(HttpStatusCode.OK){Content=new StringContent(sample.ToJsonString())}:new(HttpStatusCode.Forbidden);}));
            checks["invalid mirror cannot poison a healthy official source"]=(await SoftwareUpdate.Check(CancellationToken.None,client))?.Version=="v0.2";
        }catch(InvalidDataException){checks["invalid mirror cannot poison a healthy official source"]=false;}
        try{
            using var client=new HttpClient(new Handler(r=>r.RequestUri!.AbsoluteUri==SoftwareUpdate.DomesticRawUrl?new(HttpStatusCode.OK){Content=new StringContent(domestic.ToJsonString())}:new(HttpStatusCode.Forbidden)));
            checks["direct domestic raw endpoint independently available"]=(await SoftwareUpdate.Check(CancellationToken.None,client))?.Download.Host=="gitee.com";
        }catch(HttpRequestException){checks["direct domestic raw endpoint independently available"]=false;}
        var native=(JsonObject)sample.DeepClone();((JsonArray)native["assets"]!).Add(new JsonObject{["name"]="QATK.zip",["size"]=50,["digest"]="sha256:"+new string('c',64),["browser_download_url"]="https://github.com/sangshiCHI/QATracker/releases/download/v0.2/QATK.zip"});
        checks["native github API zip does not hide verified exe fallback"]=SoftwareUpdate.Parse(native.ToJsonString())?.Download.AbsolutePath.EndsWith("/QATK.exe")==true;
        var mirror=(JsonObject)domestic.DeepClone();mirror["assets"]![0]!["executable_digest"]="sha256:"+new string('a',64);mirror["assets"]![0]!["executable_size"]=100;
        using(var client=new HttpClient(new Handler(r=>new(HttpStatusCode.OK){Content=new StringContent((r.RequestUri!.AbsoluteUri==SoftwareUpdate.DomesticUrl?mirror:sample).ToJsonString())})))checks["same executable across zip and exe mirrors selects domestic"]=(await SoftwareUpdate.Check(CancellationToken.None,client))?.Download.Host=="gitee.com";
        using(var client=new HttpClient(new Handler(_=>new(HttpStatusCode.Forbidden))))checks["rate limit is failure not latest version"]=await Fails(async()=>{await SoftwareUpdate.Check(CancellationToken.None,client);});
        using(var client=new HttpClient(new Handler(_=>new(HttpStatusCode.OK){Content=new StringContent(new string('x',1024*1024+1))})))checks["metadata size bounded"]=await Fails(async()=>{await SoftwareUpdate.Check(CancellationToken.None,client);});
        using(var client=new HttpClient(new Handler(_=>{var r=new HttpResponseMessage(HttpStatusCode.Redirect);r.Headers.Location=new("https://evil.example/file.exe");return r;})))checks["unsafe redirect rejected before second request"]=await Fails(async()=>{await SoftwareUpdate.Download(release,Path.Combine(output,"bad-redirect"),CancellationToken.None,client);});
        using(var client=new HttpClient(new Handler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[100])})))checks["hash mismatch never stages executable"]=await Fails(async()=>{await SoftwareUpdate.Download(release,Path.Combine(output,"bad-hash"),CancellationToken.None,client);})&&!File.Exists(Path.Combine(output,"bad-hash","QATK.new"))&&!File.Exists(Path.Combine(output,"bad-hash","download.part"));
        using(var client=new HttpClient(new Handler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[99])})))checks["wrong length rejected"]=await Fails(async()=>{await SoftwareUpdate.Download(release,Path.Combine(output,"bad-length"),CancellationToken.None,client);});
        using(var cancel=new CancellationTokenSource())using(var client=new HttpClient(new Handler(_=>new(HttpStatusCode.OK)))){cancel.Cancel();checks["cancel download cannot create staged exe"]=await Fails(async()=>{await SoftwareUpdate.Download(release,Path.Combine(output,"cancel"),cancel.Token,client);})&&!File.Exists(Path.Combine(output,"cancel","QATK.new"));}
        var own=Environment.ProcessPath!;var hash=SoftwareUpdate.Hash(own);SoftwareUpdate.Verify(own,hash);checks["current x64 player verified"]=true;
        var ownRelease=release with{Size=new FileInfo(own).Length,Sha256=hash};
        var zip=Path.Combine(output,"player.zip");using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create))archive.CreateEntryFromFile(own,"QATK.exe",CompressionLevel.Fastest);
        var zipped=ownRelease with{Sha256=SoftwareUpdate.Hash(zip),Size=new FileInfo(zip).Length,ExecutableSha256=hash,ExecutableSize=new FileInfo(own).Length};
        using(var client=new HttpClient(new Handler(_=>new(HttpStatusCode.OK){Content=new StreamContent(File.OpenRead(zip))})))checks["verified zip extracts exact x64 player"] = SoftwareUpdate.Hash(await SoftwareUpdate.Download(zipped,Path.Combine(output,"verified-zip"),CancellationToken.None,client))==hash;
        var zipBad=Path.Combine(output,"zip-bad.new");checks["wrong inner digest removes extracted file"]=await Fails(()=>SoftwareUpdate.ExtractExecutable(zip,zipBad,new string('0',64),new FileInfo(own).Length,CancellationToken.None))&&!File.Exists(zipBad);
        foreach(var name in new[]{"../outside.txt","QATK.exe"})
        {
            var unsafeZip=Path.Combine(output,name=="QATK.exe"?"duplicate.zip":"traversal.zip");using(var archive=ZipFile.Open(unsafeZip,ZipArchiveMode.Create)){archive.CreateEntry("QATK.exe");archive.CreateEntry(name);}
            checks["reject archive "+name]=await Fails(()=>SoftwareUpdate.ExtractExecutable(unsafeZip,zipBad,hash,new FileInfo(own).Length,CancellationToken.None))&&!File.Exists(zipBad);
        }
        File.WriteAllText(zipBad,"keep");checks["zip staging conflict preserves existing file"]=await Fails(()=>SoftwareUpdate.ExtractExecutable(zip,zipBad,hash,new FileInfo(own).Length,CancellationToken.None))&&File.ReadAllText(zipBad)=="keep";
        var reported=new List<UpdateProgress>();
        using(var client=new HttpClient(new Handler(_=>new(HttpStatusCode.OK){Content=new StreamContent(File.OpenRead(own))})))
        {var staged=await SoftwareUpdate.Download(ownRelease,Path.Combine(output,"verified-download"),CancellationToken.None,client,new InlineProgress(reported.Add));checks["verified x64 download produces exact hash"]=SoftwareUpdate.Hash(staged)==hash;}
        checks["download progress bounded and monotonic"]=reported.Count>1&&reported[0].Received==0&&reported.All(p=>p.Total==ownRelease.Size&&p.Received>=0&&p.Received<=p.Total)&&reported.Zip(reported.Skip(1)).All(p=>p.First.Received<=p.Second.Received);
        using(var cancel=new CancellationTokenSource())using(var client=new HttpClient(new Handler(_=>new(HttpStatusCode.OK){Content=new StreamContent(File.OpenRead(own))})))checks["cancel during streaming deletes only partial download"]=await Fails(async()=>{await SoftwareUpdate.Download(ownRelease,Path.Combine(output,"cancel-stream"),cancel.Token,client,new InlineProgress(p=>{if(p.Received>0)cancel.Cancel();}));})&&!File.Exists(Path.Combine(output,"cancel-stream","download.part"));
        var occupied=Path.Combine(output,"occupied");Directory.CreateDirectory(occupied);File.WriteAllText(Path.Combine(occupied,"download.part"),"keep");
        using(var client=new HttpClient(new Handler(_=>new(HttpStatusCode.OK){Content=new StreamContent(File.OpenRead(own))})))checks["existing staging file never deleted on conflict"]=await Fails(async()=>{await SoftwareUpdate.Download(ownRelease,occupied,CancellationToken.None,client);})&&File.ReadAllText(Path.Combine(occupied,"download.part"))=="keep";
        using(var client=new HttpClient(new Handler(_=>new(HttpStatusCode.OK){Content=new StreamContent(new StalledStream())})))checks["stalled body times out and removes partial file"]=await Fails(async()=>{await SoftwareUpdate.Download(ownRelease,Path.Combine(output,"stalled"),CancellationToken.None,client);})&&!File.Exists(Path.Combine(output,"stalled","download.part"));
        var replace=Path.Combine(output,"replacement");Directory.CreateDirectory(replace);var target=Path.Combine(replace,"QATK.exe");var payload=Path.Combine(replace,"payload");var backup=Path.Combine(replace,"previous.bak");
        File.WriteAllText(target,"old");File.WriteAllText(payload,"new");SoftwareUpdate.ReplaceFile(payload,target,backup);
        checks["atomic replacement retains old backup"]=File.ReadAllText(target)=="new"&&File.ReadAllText(backup)=="old";
        File.Replace(backup,target,null);checks["extra copy rollback restores exact original"]=File.ReadAllText(target)=="old";
        File.WriteAllText(target+".update-new","unrelated");checks["staging conflict never overwrites old file"]=await Fails(()=>{SoftwareUpdate.ReplaceFile(payload,target,backup);return Task.CompletedTask;})&&File.ReadAllText(target)=="old"&&File.ReadAllText(target+".update-new")=="unrelated";
        checks["invalid helper plan cannot replace files"]=SoftwareUpdate.Apply(Path.Combine(output,"not-a-job.json"))==1&&File.ReadAllText(target)=="old";
        checks["normal update job remains backward readable without AFK"]=System.Text.Json.JsonSerializer.Deserialize<UpdateJob>("{\"Target\":\"QATK.exe\",\"TargetSha256\":\"old\",\"Payload\":\"QATK.new\",\"PayloadSha256\":\"new\",\"ParentPid\":1,\"ParentStartTicks\":1}")?.Afk is null;
        var process=new ProcessObservation(123,new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc),Path.Combine(output,"game.exe"),"0x10",null);
        var connection=new GameConnectionChoice(process,Path.Combine(output,"selected-save.json"));
        checks["valid selected connection can travel in private update plan"]=SoftwareUpdate.ValidConnection(connection);
        checks["legacy update without selected connection remains valid"]=SoftwareUpdate.ValidConnection(null);
        checks["relative save path cannot travel in update plan"]=!SoftwareUpdate.ValidConnection(connection with{SavePath="relative.json"});
        checks["unknown selected process lifetime cannot travel in plan"]=!SoftwareUpdate.ValidConnection(connection with{Process=process with{StartUtc=null}});
        var selectedJob=new UpdateJob("target","old","payload","new",1,1,Connection:connection);
        checks["selected source and process survive update serialization"]=System.Text.Json.JsonSerializer.Deserialize<UpdateJob>(System.Text.Json.JsonSerializer.Serialize(selectedJob))?.Connection==connection;
        checks["resume plan outside private update directory rejected"]=await Fails(()=>{SoftwareUpdate.ReadResume(Path.Combine(output,"not-a-job.json"));return Task.CompletedTask;});
        var store=new SessionStore(Path.Combine(output,"update-lifecycle.sqlite3"));var settings=new CompanionSettings{GameExe=own};
        var firstTick=new TaskCompletionSource();var runtime=new CompanionRuntime(settings,store,_=>firstTick.TrySetResult(),true);runtime.TogglePause();runtime.Start();
        await firstTick.Task.WaitAsync(TimeSpan.FromSeconds(15));runtime.StopForUpdate();await runtime.Completion!;
        var unfinished=store.History().Single();checks["update shutdown leaves resumable isolated session"]=runtime.UpdateRestartSafe&&unfinished.EndTime is null;
        var secondTick=new TaskCompletionSource();var restarted=new CompanionRuntime(settings,store,_=>secondTick.TrySetResult(),true);restarted.TogglePause();restarted.Start();
        await secondTick.Task.WaitAsync(TimeSpan.FromSeconds(15));restarted.Stop();await restarted.Completion!;
        var resumed=store.History().Single();checks["restarted helper keeps session ID, normal exit still ends"]=resumed.SessionId==unfinished.SessionId&&resumed.EndReason=="assistant_closed"&&resumed.EndTime is not null;
        var passed=checks.Values.All(x=>x);Program.Save(output,"update-result.json",new{Passed=passed,Cases=checks.Count,Checks=checks,Synthetic=true,Notice="Controlled metadata/download/file replacement checks. No live published release or production executable replacement performed."});return passed?0:1;
    }
}

