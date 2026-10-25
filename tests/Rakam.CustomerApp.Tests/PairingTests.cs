using Rakam.CustomerApp.Core;
using System.Net;
using System.Text;
using System.Text.Json;

static class PairingTests
{
    public static async Task Run(string root, Action<bool,string> check)
    {
        var store = new LocalStore(Path.Combine(root,"pairing-client.db"));
        var token = new string('x',64); var installationKey = new string('a',64);
        int attempts=0; var requests=new List<string>();
        var client = new SyncClient(new HttpClient(new FakeHttp(request=>{
            if(request.RequestUri!.AbsolutePath.EndsWith("/pair"))
            {
                check(request.Headers.Authorization is null,"Pairing code exchange does not send a short bearer token");
                var body=request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();requests.Add(body);
                var value=Wire.Decode<JsonElement>(body);
                check(value.GetProperty("code").GetString()=="012345","Six-digit Persian code keeps its leading zero");
                check(value.GetProperty("installation_key").GetString()==installationKey,"Private installation key is stable for retry");
                attempts++; if(attempts==1) throw new HttpRequestException("lost response after code claim");
                return Json(new {token});
            }
            return Json(Metadata.Default with {User=new(7,"employee"),Sales=false,Office=true});
        })),store);
        try { await client.Connect("https://crm.example.com","۰۱۲۳۴۵",installationKey); } catch(HttpRequestException) { }
        var connected=await client.Connect("https://crm.example.com","۰۱۲۳۴۵",installationKey);
        check(connected==token&&attempts==2&&requests[0]==requests[1],"Lost pairing receipt retries the same claim and receives the long-lived credential");
        check(store.Setting("binding")=="https://crm.example.com|7","Short-code pairing still binds the original CRM account");
        try { await client.Connect("https://other.example.com","012345",installationKey); throw new Exception("cross-server pairing accepted"); }
        catch(SyncFailure) { check(attempts==2,"Cross-company code is rejected locally before consumption"); }
        try { await client.Connect("https://crm.example.com","12345",installationKey); throw new Exception("bad code accepted"); }
        catch(InvalidDataException) { check(attempts==2,"Malformed code is rejected without network access"); }
        Console.WriteLine("Six-digit pairing checks passed.");
    }
    private static HttpResponseMessage Json<T>(T value)=>new(HttpStatusCode.OK){Content=new StringContent(Wire.Encode(value),Encoding.UTF8,"application/json")};
}
