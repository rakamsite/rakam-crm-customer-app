using Rakam.CustomerApp.Core;
using System.Net;
using System.Text;
using System.Text.Json;

static class OfficeTests
{
    public static async Task Run(string root, Action<bool,string> check)
    {
        var core = new LocalStore(Path.Combine(root,"office-core.db"));
        var office = new OfficeStore(Path.Combine(root,"office-local.db"));
        core.Set("binding","https://crm.example.com|7"); office.Bind("https://crm.example.com|7");
        var path = Path.Combine(root,"receipt.txt"); await File.WriteAllTextAsync(path,"local receipt");
        var file = office.AddFile(path,2097152); File.Delete(path);
        check(new OfficeStore(office.Path).File(file.Id).Bytes.Length>0,"Office attachment survives deletion of the original and app restart");
        var command = new OfficeCommand(Guid.NewGuid().ToString(),"topic.create",null,null,JsonSerializer.SerializeToElement(new { title="offline",body="saved locally" }),[file.Id]);
        office.Save(command);
        try { office.Save(command with { Data=JsonSerializer.SerializeToElement(new {title="must not replace uncertain delivery"}) }); throw new Exception("changed UUID payload accepted"); }
        catch (InvalidDataException) { }
        check(office.Rows().Single().Command.Data.GetProperty("title").GetString()=="offline","Office command UUID is immutable and durable");
        var accepted=new HashSet<string>();int attempts=0,uploads=0;
        var meta=Metadata.Default with { User=new(7,"employee"),Sales=false,Office=true };
        var handler=new FakeHttp(request=>{
            var url=request.RequestUri!;
            if(url.AbsolutePath.EndsWith("/uploads")){uploads++;return Json(new {token=Guid.NewGuid().ToString()});}
            if(url.AbsolutePath.EndsWith("/commands")){
                var payload=Wire.Decode<JsonElement>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                var id=payload.GetProperty("uuid").GetString()!;accepted.Add(id);attempts++;
                if(attempts==1)throw new HttpRequestException("lost receipt after commit");
                return Json(new OfficeReceipt(id,11));
            }
            return Json(new OfficePage([],null));
        });
        var http=new HttpClient(handler);var connection=new SyncClient(http,core);var sync=new OfficeSync(http,office,connection);
        try {await sync.Run("https://crm.example.com",new string('a',64),meta);}catch(HttpRequestException){}
        check(office.Rows().Single().State=="queued","Lost office response retains the queue and attachments");
        await sync.Run("https://crm.example.com",new string('a',64),meta);
        check(attempts==2&&accepted.Count==1&&uploads==1,"Office retries preserve command UUID and uploaded file token");
        check(office.Rows().Count==0,"Office delivery removed only after a successful snapshot");
        office.Replace(new() { ["settings"]=[],["topics"]=[Wire.Decode<JsonElement>("{\"id\":1,\"title\":\"old\"}")],["entries"]=[],["reports"]=[],["announcements"]=[] });
        var pending=new OfficeCommand(Guid.NewGuid().ToString(),"topic.act",1,JsonSerializer.SerializeToElement(1),JsonSerializer.SerializeToElement(new {action="message",body="do not lose"}),[]);
        office.Save(pending);
        var conflictHttp=new HttpClient(new FakeHttp(request=>request.RequestUri!.AbsolutePath.EndsWith("/commands")?
            new HttpResponseMessage(HttpStatusCode.Conflict){Content=new StringContent("{\"message\":\"changed while offline\"}")}:
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        try{await new OfficeSync(conflictHttp,office,new SyncClient(conflictHttp,core)).Run("https://crm.example.com",new string('a',64),meta);}catch(SyncFailure){}
        check(office.Rows().Single().State=="error"&&office.Rows().Single().Command.Data.GetProperty("body").GetString()=="do not lose","Stale office operation preserves editable text instead of overwriting newer work");
        check(office.State().Sections["topics"].Single().GetProperty("title").GetString()=="old","Partial office snapshot preserves the previous cache");
        var offlineFile=Path.Combine(root,"offline.txt");await File.WriteAllTextAsync(offlineFile,"keep file inside backup");var staged=office.AddFile(offlineFile,1000);
        var bundle=DesktopBackup.Save(core,office,Path.Combine(root,"desktop-backups"));
        var restoredCore=new LocalStore(Path.Combine(root,"restored-core.db"));var restoredOffice=new OfficeStore(Path.Combine(root,"restored-office.db"));
        DesktopBackup.Restore(bundle,restoredCore,restoredOffice);
        check(restoredOffice.Rows().Count==1&&restoredOffice.File(staged.Id).Bytes.Length>0,"Combined backup restores separate office DB, pending text and attachment bytes");
        restoredOffice.CacheAttachment(33,Encoding.UTF8.GetBytes("cached"));
        var destination=Path.Combine(root,"cached-download.txt");
        await new OfficeSync(conflictHttp,restoredOffice,new SyncClient(conflictHttp,restoredCore)).Download("https://crm.example.com","unused",33,destination);
        check(await File.ReadAllTextAsync(destination)=="cached","Previously received attachment remains usable offline");
        restoredOffice.ClearCache();check(restoredOffice.CachedAttachment(33) is null&&restoredOffice.Rows().Count==1,"Revocation clears received data without deleting unsent work");
        var noSales = new SyncClient(new HttpClient(new FakeHttp(request=>request.RequestUri!.AbsolutePath.EndsWith("/metadata")?Json(meta):throw new Exception("Employee must never request sales endpoints"))),core);
        await noSales.Run("https://crm.example.com",new string('a',64));check(true,"Office-only users never access sales synchronization");
        Console.WriteLine("Office offline and synchronization checks passed.");
    }
    private static HttpResponseMessage Json<T>(T value)=>new(HttpStatusCode.OK){Content=new StringContent(Wire.Encode(value),Encoding.UTF8,"application/json")};
}
