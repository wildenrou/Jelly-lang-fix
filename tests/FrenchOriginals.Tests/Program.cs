using System.Text.Json;
using Jellyfin.Plugin.FrenchOriginals;
using Jellyfin.Plugin.FrenchOriginals.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;

var tests = new (string Name, Func<Task> Run)[]
{
    ("French ISO codes, regions, and unknown/non-French exclusion", () => Sync(() => {
        foreach (var x in new[] { "fr", "fra", "fre", "FR", " fr-CA ", "fr_FR" }) Assert(Rules.IsFrench(x));
        foreach (var x in new string?[] { null, "", "en", "French", "en,fr", "français", "fraud" }) Assert(!Rules.IsFrench(x));
    })),
    ("Only French-original movies and series qualify", () => Sync(() => {
        Assert(Rules.Eligible(Movie(), null));
        Assert(!Rules.Eligible(Movie("en"), null));
        Assert(!Rules.Eligible(Movie(null), null));
        Assert(!Rules.Eligible(new Folder { OriginalLanguage = "fr" }, null));
    })),
    ("Series-child locks, original language, and explicit overrides", () => Sync(() => {
        var series = new Series { OriginalLanguage = "fr" };
        var ep = new Episode();
        Assert(Rules.Eligible(ep, series));
        ep.PreferredMetadataLanguage = "en"; Assert(!Rules.Eligible(ep, series));
        ep.PreferredMetadataLanguage = "fr-CA"; Assert(Rules.Eligible(ep, series));
        ep.OriginalLanguage = "en"; Assert(!Rules.Eligible(ep, series));
        ep.OriginalLanguage = "fr"; series.IsLocked = true; Assert(!Rules.Eligible(ep, series));
        Assert(!Rules.Eligible(ep, null));
    })),
    ("Identity requires a shared ID and rejects conflicting IDs", () => Sync(() => {
        var a = Movie(); var b = Movie(); Assert(Rules.MatchingIdentity(a,b));
        b.ProviderIds.Clear(); Assert(!Rules.MatchingIdentity(a,b));
        b.ProviderIds["Tmdb"] = "different"; Assert(!Rules.MatchingIdentity(a,b));
        b.ProviderIds["Tmdb"] = a.ProviderIds["Tmdb"];
        a.ProviderIds["Imdb"] = "tt123"; b.ProviderIds["Imdb"] = "tt456"; Assert(!Rules.MatchingIdentity(a,b));
    })),
    ("Blank provider fields never erase existing text", () => Sync(() => {
        var item = Movie(); var c = Rules.Plan(item,new(" ",null,"fake"));
        Assert(c.Name == item.Name && c.Overview == item.Overview && c.Language == "fr");
    })),
    ("Individual metadata locks and French regional preference preserved", () => Sync(() => {
        var item = Movie(); item.LockedFields = [MetadataField.Name,MetadataField.Overview]; item.PreferredMetadataLanguage = "fr-CA";
        var c = Rules.Plan(item,new("Titre","Résumé","fake"));
        Assert(c.Name == item.Name && c.Overview == item.Overview && c.Language == "fr-CA");
    })),
    ("Preview performs zero library writes and creates no completion markers", async () => {
        using var f = new Fixture(); f.Add(Movie()); await f.Run(preview:true);
        Assert(f.Items.Writes == 0 && f.State.LoadCompleted().Count == 0);
        Assert(f.Report().Status == "Preview complete");
    }),
    ("Apply verifies narrow changes and next run is idempotent", async () => {
        using var f = new Fixture(); var m=Movie(); m.Tags=["keep"]; m.OfficialRating="PG"; f.Add(m);
        var oldProtected=Rules.ProtectedFingerprint(m); await f.Run(); await f.Run();
        Assert(f.Items.Writes==1 && f.Lookup.Calls==1 && m.Name=="Titre français" && m.Overview=="Résumé français");
        Assert(Rules.ProtectedFingerprint(m)==oldProtected && f.Report().AlreadyComplete==1);
    }),
    ("Already-French items receive the first text update", async () => {
        using var f = new Fixture(); var m=Movie(); m.PreferredMetadataLanguage="fr"; f.Add(m); await f.Run();
        Assert(m.Name=="Titre français" && f.Items.Writes==1);
    }),
    ("Unchanged French items are hidden and remembered without a write", async () => {
        using var f=new Fixture(); var m=Movie(); m.Name="Titre français"; m.Overview="Résumé français"; m.PreferredMetadataLanguage="fr"; f.Add(m);
        await f.Run(); Assert(f.Items.Writes==0 && f.Report().Items.Count==0 && f.Report().NoChangeNeeded==1);
        Assert(f.State.LoadCompleted()[m.Id].Fingerprint is not null);
        await f.Run(); Assert(f.Lookup.Calls==1 && f.Report().AlreadyComplete==1);
    }),
    ("Unchanged preview rows are hidden without completion markers", async () => {
        using var f=new Fixture(); var m=Movie(); m.Name="Titre français"; m.Overview="Résumé français"; m.PreferredMetadataLanguage="fr"; f.Add(m);
        await f.Run(preview:true); Assert(f.Report().Items.Count==0 && f.Report().NoChangeNeeded==1);
        Assert(f.Items.Writes==0 && f.State.LoadCompleted().Count==0);
    }),
    ("Summary-only updates do not present the same French title as a rename", async () => {
        using var f=new Fixture(); var m=Movie(); m.Name="Titre français"; m.PreferredMetadataLanguage="fr"; f.Add(m);
        await f.Run(); var row=f.Report().Items.Single();
        Assert(f.Items.Writes==1 && m.Overview=="Résumé français" && row.NewTitle is null && row.Action=="Updated summary");
    }),
    ("Language preference updates retain and do not repeat an unchanged title", async () => {
        using var f=new Fixture(); var m=Movie(); m.Name="Titre français"; m.Overview="Résumé français"; f.Add(m);
        await f.Run(); var row=f.Report().Items.Single();
        Assert(f.Items.Writes==1 && m.PreferredMetadataLanguage=="fr" && row.NewTitle is null && row.Action=="Updated language preference");
    }),
    ("Preview accurately distinguishes title and summary changes", async () => {
        using var f=new Fixture(); var m=Movie(); m.PreferredMetadataLanguage="fr"; f.Add(m);
        await f.Run(preview:true); var row=f.Report().Items.Single();
        Assert(row.NewTitle=="Titre français" && row.Action=="Would update title, summary" && f.Items.Writes==0);
    }),
    ("Unknown originals, English originals, and globally locked items remain untouched", async () => {
        using var f = new Fixture(); f.Add(Movie("en")); f.Add(Movie(null)); var m=Movie(); m.IsLocked=true; f.Add(m); await f.Run();
        Assert(f.Lookup.Calls==0 && f.Items.Writes==0);
    }),
    ("Change guard aborts entire batch before writes or provider calls", async () => {
        using var f = new Fixture(); f.Add(Movie()); f.Add(Movie());
        await Throws<InvalidOperationException>(()=>f.Run(limit:0,max:1));
        Assert(f.Lookup.Calls==0 && f.Items.Writes==0);
    }),
    ("Library busy guard performs zero writes", async () => {
        using var f=new Fixture(); f.Add(Movie()); f.Items.Busy=true;
        await Throws<InvalidOperationException>(()=>f.Run()); Assert(f.Items.Writes==0);
    }),
    ("Library becoming busy during lookup stops before a write", async () => {
        using var f=new Fixture(); f.Add(Movie()); f.Lookup.OnFetch=()=>f.Items.Busy=true;
        await Throws<InvalidOperationException>(()=>f.Run()); Assert(f.Items.Writes==0);
    }),
    ("Unavailable metadata leaves language and text unchanged", async () => {
        using var f=new Fixture(); var m=Movie(); f.Add(m); f.Lookup.Result=null; await f.Run();
        Assert(f.Items.Writes==0 && m.PreferredMetadataLanguage=="en" && f.State.LoadCompleted()[m.Id].Fingerprint is null);
    }),
    ("Failed lookup rotates behind unattempted items", async () => {
        using var f=new Fixture(); f.Add(Movie()); f.Add(Movie()); f.Lookup.Result=null;
        await f.Run(limit:1); var first=f.Lookup.LastId;
        await f.Run(limit:1); Assert(first!=f.Lookup.LastId);
    }),
    ("Partial translation preserves missing text and stays retryable", async () => {
        using var f=new Fixture(); var m=Movie(); f.Add(m); f.Lookup.Result=new("Titre français",null,"fake");
        await f.Run(); Assert(m.Name=="Titre français" && m.Overview=="English overview");
        Assert(f.State.LoadCompleted()[m.Id].Fingerprint is null);
        f.Lookup.Result=new("Titre français","Résumé français","fake"); await f.Run();
        Assert(m.Overview=="Résumé français" && f.State.LoadCompleted()[m.Id].Fingerprint is not null);
    }),
    ("Concurrent edit during provider call is preserved", async () => {
        using var f=new Fixture(); var m=Movie(); f.Add(m); f.Lookup.OnFetch=()=>m.Name="Manual edit";
        await f.Run(); Assert(f.Items.Writes==0 && m.Name=="Manual edit");
    }),
    ("Image fingerprints ignore incidental row order without mutating artwork", () => Sync(() => {
        var m=Movie(); m.ImageInfos=Images(); var original=m.ImageInfos.ToArray();
        var fingerprint=Rules.ProtectedFingerprint(m); var legacy=Rules.ProtectedFingerprint(m,canonicalImageOrder:false);
        Assert(m.ImageInfos.SequenceEqual(original));
        m.ImageInfos=m.ImageInfos.Reverse().ToArray();
        Assert(Rules.ProtectedFingerprint(m)==fingerprint);
        Assert(Rules.ProtectedFingerprint(m,canonicalImageOrder:false)!=legacy);
        Assert(m.ImageInfos.SequenceEqual(original.Reverse()));
    })),
    ("Reordered image rows verify after save and stay complete on later reads", async () => {
        using var f=new Fixture(); var m=Movie(); m.ImageInfos=Images(); f.Add(m); f.Items.VerifyAtStore=true;
        f.Items.OnSave=item=>item.ImageInfos=item.ImageInfos.Reverse().ToArray();
        await f.Run(); Assert(f.Report().Changed==1 && f.Report().Status=="Completed");
        Assert(!f.JournalEntries().Any(e=>e.GetProperty("State").GetString()=="verification-failed"));
        m.ImageInfos=m.ImageInfos.Reverse().ToArray();
        await f.Run(); Assert(f.Items.Writes==1 && f.Lookup.Calls==1 && f.Report().AlreadyComplete==1);
    }),
    ("Artwork removal, addition, replacement, type, case and duplicate changes still stop the batch", async () => {
        Action<BaseItem>[] edits=[
            m=>m.ImageInfos=m.ImageInfos.Skip(1).ToArray(),
            m=>m.ImageInfos=[..m.ImageInfos,new ItemImageInfo {Path="/art/extra.jpg",Type=ImageType.Backdrop}],
            m=>m.ImageInfos[0].Path="/art/replaced.jpg",
            m=>m.ImageInfos[0].Type=ImageType.Banner,
            m=>m.ImageInfos[0].Path=m.ImageInfos[0].Path.ToUpperInvariant(),
            m=>m.ImageInfos=[..m.ImageInfos,m.ImageInfos[0]]
        ];
        foreach(var edit in edits)
        {
            using var f=new Fixture(); var first=Movie(); first.ImageInfos=Images(); var second=Movie(); second.ImageInfos=Images();
            f.Add(first); f.Add(second); f.Items.OnSave=edit; f.Items.VerifyAtStore=true;
            await Throws<MetadataVerificationException>(()=>f.Run());
            Assert(f.Items.Writes==1 && f.State.LoadCompleted().Count==0 && f.Report().Items.Single().Detail!.Contains("Fields: Images"));
        }
    }),
    ("Existing completion history remains valid after upgrading image fingerprints", async () => {
        using var f=new Fixture(); var m=Movie(); m.ImageInfos=Images(); f.Add(m);
        var legacy=Rules.CompletionKey(m,true,canonicalImageOrder:false);
        Assert(legacy!=Rules.CompletionKey(m,true));
        f.State.SaveCompleted(new(){{m.Id,new(legacy,DateTime.UtcNow)}});
        await f.Run(); Assert(f.Lookup.Calls==0 && f.Items.Writes==0 && f.Report().AlreadyComplete==1);
    }),
    ("Legacy completion compatibility never hides a real artwork change", async () => {
        using var f=new Fixture(); var m=Movie(); m.ImageInfos=Images(); f.Add(m);
        f.State.SaveCompleted(new(){{m.Id,new(Rules.CompletionKey(m,true,canonicalImageOrder:false),DateTime.UtcNow)}});
        m.ImageInfos[0].Path="/art/new-poster.jpg";
        await f.Run(); Assert(f.Lookup.Calls==1 && f.Items.Writes==1 && f.Report().AlreadyComplete==0);
    }),
    ("Unrelated metadata change fails verification and stops later writes", async () => {
        using var f=new Fixture(); f.Add(Movie()); f.Add(Movie()); f.Items.OnSave=m=>m.CustomRating="unexpected";
        await Throws<InvalidOperationException>(()=>f.Run()); Assert(f.Items.Writes==1 && f.State.LoadCompleted().Count==0);
        var row=f.Report().Items.Single(); Assert(row.Action=="Verification failed" && row.Detail!.Contains("CustomRating"));
        var entries=f.JournalEntries(); var failed=entries.Single(e=>e.GetProperty("State").GetString()=="verification-failed");
        var difference=failed.GetProperty("Data").GetProperty("Differences").EnumerateArray().Single();
        Assert(difference.GetProperty("Field").GetString()=="CustomRating");
        Assert(difference.GetProperty("Expected").ValueKind==JsonValueKind.Null && difference.GetProperty("Actual").GetString()=="unexpected");
        Assert(entries[^1].GetProperty("State").GetString()=="run-end");
    }),
    ("Store-side verification failures preserve expected and actual title in the audit", async () => {
        using var f=new Fixture(); f.Add(Movie()); f.Add(Movie()); f.Items.VerifyAtStore=true; f.Items.OnSave=m=>m.Name="Unexpected title";
        await Throws<MetadataVerificationException>(()=>f.Run());
        Assert(f.Items.Writes==1 && f.Report().Changed==0 && f.State.LoadCompleted().Count==0);
        var data=f.JournalEntries().Single(e=>e.GetProperty("State").GetString()=="verification-failed").GetProperty("Data");
        var diff=data.GetProperty("Differences").EnumerateArray().Single();
        Assert(diff.GetProperty("Field").GetString()=="Name" && diff.GetProperty("Expected").GetString()=="Titre français");
        Assert(diff.GetProperty("Actual").GetString()=="Unexpected title" && data.GetProperty("Before").GetProperty("Name").GetString()=="English title");
    }),
    ("Missing read-back item is recorded as a verification failure", async () => {
        using var f=new Fixture(); f.Add(Movie()); f.Items.OnSave=m=>f.Items.Data.Remove(m.Id);
        await Throws<MetadataVerificationException>(()=>f.Run());
        Assert(f.Report().Items.Single().Detail!.Contains("Fields: Item"));
        Assert(f.State.LoadCompleted().Count==0);
    }),
    ("Save exceptions identify the failed item without marking it complete", async () => {
        using var f=new Fixture(); var m=Movie(); f.Add(m); f.Items.OnSave=_=>throw new IOException("Synthetic save failure");
        await Throws<IOException>(()=>f.Run());
        Assert(f.Report().Items.Single().Id==m.Id && f.Report().Items.Single().Action=="Update failed");
        Assert(f.Report().Changed==0 && f.State.LoadCompleted().Count==0);
    }),
    ("Cancellation before commit changes nothing", async () => {
        using var f=new Fixture(); f.Add(Movie()); using var c=new CancellationTokenSource(); f.Lookup.OnFetch=c.Cancel;
        await Throws<OperationCanceledException>(()=>f.Run(token:c.Token)); Assert(f.Items.Writes==0 && f.Report().Status=="Cancelled");
    }),
    ("Interrupted run resumes without rewriting completed item", async () => {
        using var f=new Fixture(); f.Add(Movie()); f.Add(Movie()); using var c=new CancellationTokenSource(); f.Items.OnSave=_=>c.Cancel();
        await Throws<OperationCanceledException>(()=>f.Run(token:c.Token)); Assert(f.Items.Writes==1);
        f.Items.OnSave=null; await f.Run(); Assert(f.Items.Writes==2 && f.State.LoadCompleted().Count==2);
    }),
    ("A second concurrent run is rejected", async () => {
        using var f=new Fixture(); f.Add(Movie()); var entered=new TaskCompletionSource(); var release=new TaskCompletionSource();
        f.Lookup.Wait=async()=>{entered.SetResult();await release.Task;};
        var first=f.Run(); await entered.Task; await Throws<InvalidOperationException>(()=>f.Run()); release.SetResult(); await first;
        Assert(f.Items.Writes==1);
    }),
    ("Corrupt completion file fails closed", async () => {
        using var f=new Fixture(); f.Add(Movie()); Directory.CreateDirectory(f.Path); File.WriteAllText(System.IO.Path.Combine(f.Path,"completed.json"),"bad json");
        await Throws<JsonException>(()=>f.Run()); Assert(f.Items.Writes==0);
    }),
    ("Audit directory failure prevents library changes", async () => {
        using var f=new Fixture(); f.Add(Movie()); Directory.CreateDirectory(f.Path); File.WriteAllText(System.IO.Path.Combine(f.Path,"journals"),"blocking file");
        await Throws<IOException>(()=>f.Run()); Assert(f.Items.Writes==0);
    }),
    ("Audit records before and after values around the verified write", async () => {
        using var f=new Fixture(); var m=Movie(); f.Add(m); await f.Run();
        var lines=File.ReadAllLines(Directory.GetFiles(System.IO.Path.Combine(f.Path,"journals"))[0]);
        var states=lines.Select(s=>JsonDocument.Parse(s).RootElement.GetProperty("State").GetString()).ToArray();
        Assert(Array.IndexOf(states,"prepared")<Array.IndexOf(states,"verified"));
        Assert(lines.Any(s=>s.Contains("English title")) && lines.Any(s=>s.Contains("fran")));
    }),
    ("Language-only mode makes no provider requests", async () => {
        using var f=new Fixture(); var m=Movie(); f.Add(m); await f.Run(updateText:false);
        Assert(f.Lookup.Calls==0 && m.Name=="English title" && m.PreferredMetadataLanguage=="fr");
    }),
    ("Invalid scope and numeric settings rejected", () => Sync(() => {
        var c=new PluginConfiguration(); ThrowsSync(()=>c.Snapshot());
        c.LibraryIds=[Guid.NewGuid().ToString()]; c.MaxChanges=0; ThrowsSync(()=>c.Snapshot());
        c.MaxChanges=80; c.ItemIds="not an id"; ThrowsSync(()=>c.Snapshot());
    }))
};
var failures=0;
foreach(var test in tests) { try { await test.Run(); Console.WriteLine("PASS " + test.Name); } catch(Exception ex) { failures++; Console.WriteLine("FAIL " + test.Name + ": " + ex); } }
Console.WriteLine($"{tests.Length-failures}/{tests.Length} tests passed");
return failures==0 ? 0 : 1;

static void Assert(bool condition) { if(!condition) throw new Exception("Assertion failed"); }
static Task Sync(Action action) {action();return Task.CompletedTask;}
static async Task Throws<T>(Func<Task> action) where T:Exception {try {await action();}catch(T){return;}throw new Exception("Expected " + typeof(T).Name);}
static void ThrowsSync(Action action) {try{action();}catch(InvalidOperationException){return;}throw new Exception("Expected configuration rejection");}
static Movie Movie(string? lang="fr") => new() { Id=Guid.NewGuid(),Name="English title",Overview="English overview",OriginalLanguage=lang!,PreferredMetadataLanguage="en",ProviderIds=new(StringComparer.OrdinalIgnoreCase){{"Tmdb","12345"}} };
static ItemImageInfo[] Images() => [
    new() { Path="/art/backdrop-z.jpg",Type=ImageType.Backdrop },
    new() { Path="/art/poster.jpg",Type=ImageType.Primary },
    new() { Path="/art/backdrop-a.jpg",Type=ImageType.Backdrop }
];

sealed class Fixture : IDisposable
{
    public string Path {get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"french-originals-tests-"+Guid.NewGuid().ToString("N"));
    public FakeItems Items {get;}=new(); public FakeLookup Lookup {get;}=new(); public StateStore State {get;} public MetadataTaskService Service {get;}
    public Fixture(){State=new(Path);Service=new(Items,Lookup,State,NullLogger<MetadataTaskService>.Instance);}
    public void Add(BaseItem item)=>Items.Data.Add(item.Id,item);
    public Task Run(bool preview=false,int limit=25,int max=80,bool updateText=true,CancellationToken token=default) => Service.Run(new(preview,[Guid.NewGuid()],updateText,true,false,[],limit,max,0,30,30),new Progress<double>(),token);
    public RunReport Report()=>JsonSerializer.Deserialize<RunReport>(State.ReadReport())!;
    public JsonElement[] JournalEntries()=>File.ReadAllLines(Directory.GetFiles(System.IO.Path.Combine(Path,"journals")).Single())
        .Select(line=>JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
    public void Dispose(){if(Directory.Exists(Path))Directory.Delete(Path,true);}
}
sealed class FakeItems : IItemStore
{
    public bool Busy {get;set;} public int Writes {get;private set;} public Dictionary<Guid,BaseItem> Data {get;}=[]; public Action<BaseItem>? OnSave {get;set;}
    public bool VerifyAtStore {get;set;}
    public IEnumerable<WorkItem> Inventory(RunOptions o,CancellationToken t)=>Data.Keys.Select(id=>new WorkItem(id,null));
    public BaseItem? Read(Guid id)=>Data.GetValueOrDefault(id);
    public Task Save(BaseItem item,TextChange c,CancellationToken t)
    {
        t.ThrowIfCancellationRequested(); if(Busy)throw new InvalidOperationException("Busy");
        var before=Rules.Snapshot(item);var protectedBefore=Rules.ProtectedMetadata(item);
        item.Name=c.Name!;item.Overview=c.Overview!;item.PreferredMetadataLanguage=c.Language!;Writes++;OnSave?.Invoke(item);
        if(VerifyAtStore)Rules.Verify(before,protectedBefore,c,Read(item.Id));
        return Task.CompletedTask;
    }
}
sealed class FakeLookup : ITextLookup
{
    public int Calls {get;private set;} public Guid LastId {get;private set;} public Action? OnFetch {get;set;} public Func<Task>? Wait {get;set;}
    public LocalizedText? Result {get;set;}=new("Titre français","Résumé français","fake");
    public async Task<LocalizedText?> Fetch(BaseItem item,int timeout,CancellationToken t) {Calls++;LastId=item.Id;OnFetch?.Invoke();if(Wait is not null)await Wait();return Result;}
}
