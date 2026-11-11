using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Companion.Diagnostics;

internal static class OnlinePresenceVerification
{
    sealed class Handler:HttpMessageHandler
    {
        public List<(string Url,string Method,string Body)> Requests=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Requests.Add((request.RequestUri!.AbsoluteUri,request.Method.Method,await request.Content!.ReadAsStringAsync(token)));
            return new(HttpStatusCode.NoContent);
        }
    }
    internal static async Task<int> Run(string output)
    {
        var checks=new Dictionary<string,bool>();var id=Guid.NewGuid().ToString("N");
        var payload=OnlinePresence.Payload(id,"0.1.2","beat");using var parsed=JsonDocument.Parse(payload);
        checks["only three public liveness fields"]=parsed.RootElement.EnumerateObject().Select(p=>p.Name).Order().SequenceEqual(new[]{"id","kind","version"});
        checks["random identifier has no persistent machine identity"]=id.Length==32&&id!=Guid.NewGuid().ToString("N");
        bool Rejected(string value,string version,string kind){try{OnlinePresence.Payload(value,version,kind);return false;}catch(ArgumentException){return true;}}
        checks["account and raw IP cannot serve as ID"]=Rejected("player@example.com","0.1.2","beat")&&Rejected("192.0.2.1","0.1.2","beat");
        checks["version metadata bounded"]=Rejected(id,"0.1.2 player-file.json","beat");
        checks["arbitrary event names rejected"]=Rejected(id,"0.1.2","save_uploaded");
        using var handler=new Handler();using var client=new HttpClient(handler);
        checks["anonymous heartbeat succeeds on no-content response"]=await OnlinePresence.Send(client,id,"0.1.2","beat",CancellationToken.None);
        await OnlinePresence.Send(client,id,"0.1.2","leave",CancellationToken.None);
        checks["one fixed HTTPS endpoint"]=handler.Requests.All(r=>r.Url==OnlinePresence.Endpoint&&r.Method=="POST");
        checks["exit uses same temporary ID"]=JsonDocument.Parse(handler.Requests[1].Body).RootElement.GetProperty("id").GetString()==id;
        checks["exit is distinct from heartbeat"]=JsonDocument.Parse(handler.Requests[1].Body).RootElement.GetProperty("kind").GetString()=="leave";
        Program.Save(output,"online-result.json",new{Passed=checks.Values.All(x=>x),Cases=checks.Count,Checks=checks,Synthetic=true,NetworkUsed=false});
        return checks.Values.All(x=>x)?0:1;
    }
}
