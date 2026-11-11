using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Companion.Diagnostics;

internal sealed class OnlinePresence:IDisposable
{
    internal const string Endpoint="https://questaway-simulator.wiki/api/qatk-online";
    readonly string id=Guid.NewGuid().ToString("N");
    readonly string version=typeof(OnlinePresence).Assembly.GetName().Version!.ToString(3);
    readonly CancellationTokenSource lifetime=new();
    readonly HttpClient client=new(new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=false,UseDefaultCredentials=false}){Timeout=TimeSpan.FromSeconds(8)};
    Task? running;
    internal static string Payload(string id,string version,string kind)
    {
        if(!Regex.IsMatch(id,@"\A[a-f0-9]{32}\z")||!Regex.IsMatch(version,@"\A\d{1,5}\.\d{1,5}\.\d{1,5}(?:\.\d{1,5})?\z")||kind is not ("beat" or "leave"))throw new ArgumentException("invalid_presence");
        return JsonSerializer.Serialize(new{id,version,kind});
    }
    internal static async Task<bool> Send(HttpClient client,string id,string version,string kind,CancellationToken token)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,Endpoint){Content=new StringContent(Payload(id,version,kind),Encoding.UTF8,"application/json")};
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false);
        return response.StatusCode==System.Net.HttpStatusCode.NoContent;
    }
    internal void Start(){running??=Run();}
    async Task Run()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(8,20)),lifetime.Token).ConfigureAwait(false);
            while(!lifetime.IsCancellationRequested)
            {
                try{await Send(client,id,version,"beat",lifetime.Token).ConfigureAwait(false);}
                catch(Exception e)when(e is HttpRequestException or IOException or OperationCanceledException){ }
                await Task.Delay(TimeSpan.FromSeconds(180+Random.Shared.Next(0,16)),lifetime.Token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException){ }
    }
    internal async Task Stop()
    {
        lifetime.Cancel();if(running is null)return;
        await running.ConfigureAwait(false);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try{await Send(client,id,version,"leave",timeout.Token).ConfigureAwait(false);}
        catch(Exception e)when(e is HttpRequestException or IOException or OperationCanceledException){ }
    }
    public void Dispose(){lifetime.Cancel();client.Dispose();lifetime.Dispose();}
}
