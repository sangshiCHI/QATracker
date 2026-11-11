namespace Companion.Diagnostics;

// Metadata is only a short-lived optimization: unchanged metadata still forces a full read after 5s.
internal sealed class AfkReadCache(Func<string,CancellationToken,Task<SaveSnapshot>>? read=null,Func<long>? clock=null)
{
    SaveSnapshot? cached;string? previousPath,bytesHash;long length,readAt;DateTime written;
    public async Task<SaveSnapshot> Read(string path,bool enabled,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!enabled){cached=null;bytesHash=null;return await (read??SaveSource.Read)(path,token);}
        var name=Path.GetFullPath(path);var before=new FileInfo(name);before.Refresh();
        if(!before.Exists){cached=null;bytesHash=null;throw new FileNotFoundException("save_missing");}
        var now=(clock??(()=>Environment.TickCount64))();
        if(cached is not null&&name==previousPath&&before.Length==length&&before.LastWriteTimeUtc==written&&now>=readAt&&now-readAt<5000)return cached;
        var previous=name==previousPath?cached:null;var previousHash=bytesHash;cached=null;bytesHash=null;
        SaveSnapshot sample;string? hash=null;
        if(read is null){var result=await SaveSource.ReadCached(name,token,previousHash,previous);sample=result.Snapshot;hash=result.BytesHash;}
        else sample=await read(name,token);
        token.ThrowIfCancellationRequested();
        var after=new FileInfo(name);after.Refresh();
        if(after.Exists&&before.Length==after.Length&&before.LastWriteTimeUtc==after.LastWriteTimeUtc)
        {cached=sample;bytesHash=hash;previousPath=name;length=after.Length;written=after.LastWriteTimeUtc;readAt=(clock??(()=>Environment.TickCount64))();}
        return sample;
    }
}
