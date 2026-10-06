using RechercheLogement.Core.Contracts;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Services;

namespace RechercheLogement.Tests;

public class CatalogTests
{
    private long _now = 1_000_000;
    private readonly InMemoryPersistence _db = new();

    private Catalog NewCatalog() => new(_db, new CatalogOptions { Now = () => _now++ });

    private static Observation Obs(string site, string id, ListingData data, string source = "card") => new(site, id, source, data, 0);

    private static SyncAction Status(string id, string key, PropertyStatus s, long at = 0) => new(id, at, ActionType.SetStatus, key, Status: s);

    private static SyncRequest Req(IEnumerable<Observation>? obs = null, IEnumerable<SyncAction>? actions = null, IEnumerable<string>? want = null) =>
        new("test", obs?.ToList(), actions?.ToList(), want?.ToList());

    [Fact]
    public void RejectedOnOneSite_IsHiddenOnTheOther_AfterAutoMerge()
    {
        var c = NewCatalog();
        c.Sync(Req([Obs("bienici", "hektor", DedupScorerTests.Bienici, "api")], [Status("a1", "bienici:hektor", PropertyStatus.Rejected, _now)]));

        var res = c.Sync(Req([Obs("seloger", "999", DedupScorerTests.SeLoger)]));

        var view = res.Listings["seloger:999"];
        Assert.Equal(PropertyStatus.Rejected, view.Status);
        Assert.Equal("bienici:hektor", Assert.Single(view.Siblings).Key);
    }

    [Fact]
    public void Actions_AreIdempotent_AndUnknownKeysAreRejected()
    {
        var c = NewCatalog();
        c.Sync(Req([Obs("bienici", "x", new ListingData { PostalCode = "75001" })]));

        var r1 = c.Sync(Req(actions: [Status("a1", "bienici:x", PropertyStatus.Seen, _now), Status("a2", "bienici:nope", PropertyStatus.Seen, _now)]));
        Assert.Equal(new[] { "a1" }, r1.AppliedActionIds);
        Assert.Equal(new[] { "a2" }, r1.RejectedActionIds);

        var r2 = c.Sync(Req(actions: [Status("a1", "bienici:x", PropertyStatus.Seen, _now)]));
        Assert.Equal(new[] { "a1" }, r2.AppliedActionIds);
        Assert.Single(c.GetProperty(c.PropertyIdOf("bienici:x")!)!.Events, e => e.Kind == PropertyEventKind.StatusChanged);
    }

    [Fact]
    public void OfflineAction_OlderThanAWebsiteChange_DoesNotOverwriteIt()
    {
        var c = NewCatalog();
        c.Sync(Req([Obs("bienici", "x", new ListingData { PostalCode = "75001" })]));
        var actionTime = _now;                       // clic dans le plugin, serveur arrêté...
        var pid = c.PropertyIdOf("bienici:x")!;
        _now += 10;
        c.SetStatus(pid, PropertyStatus.ToContact);  // ...puis décision plus récente sur le site
        c.Sync(Req(actions: [Status("late", "bienici:x", PropertyStatus.Rejected, actionTime)]));
        Assert.Equal(PropertyStatus.ToContact, c.GetProperty(pid)!.Summary.Property.Status);
    }

    [Fact]
    public void Suggestion_ThenConfirm_MergesAndKeepsOlderProperty()
    {
        var c = NewCatalog();
        c.Sync(Req([Obs("bienici", "a", new ListingData { Price = 1200, Surface = 45, Rooms = 2, PostalCode = "75011" })]));
        var older = c.PropertyIdOf("bienici:a")!;
        c.SetStatus(older, PropertyStatus.Seen);

        var res = c.Sync(Req([Obs("pap", "b", new ListingData { Price = 1210, Surface = 45.5, Rooms = 2, PostalCode = "75011" })]));
        var s = Assert.Single(res.Listings["pap:b"].Suggestions);
        Assert.Equal(PropertyStatus.Seen, s.OtherStatus);
        Assert.Single(c.PendingDuplicates());

        c.Sync(Req(actions: [new SyncAction("c1", _now, ActionType.ConfirmDuplicate, "pap:b", OtherKey: "bienici:a")]));
        Assert.Equal(older, c.PropertyIdOf("pap:b"));
        Assert.Equal(PropertyStatus.Seen, c.View("pap:b")!.Status);
        Assert.Empty(c.PendingDuplicates());
        Assert.Equal(1, c.Stats().Properties);
    }

    [Fact]
    public void Detach_UndoesAWrongMerge_AndIsNeverSuggestedAgain()
    {
        var c = NewCatalog();
        c.Sync(Req([Obs("bienici", "hektor", DedupScorerTests.Bienici, "api")], [Status("a1", "bienici:hektor", PropertyStatus.Rejected, _now)]));
        c.Sync(Req([Obs("seloger", "999", DedupScorerTests.SeLoger)]));

        c.Detach("seloger:999");
        Assert.Equal(PropertyStatus.None, c.View("seloger:999")!.Status);

        var res = c.Sync(Req([Obs("seloger", "999", DedupScorerTests.SeLoger with { Price = 1790 })]));
        Assert.Empty(res.Listings["seloger:999"].Siblings);
        Assert.Empty(res.Listings["seloger:999"].Suggestions);
    }

    [Fact]
    public void CardData_DoesNotOverwriteApiData_AndPriceChangesAreTracked()
    {
        var c = NewCatalog();
        c.Sync(Req([Obs("bienici", "x", new ListingData { Price = 1000, Surface = 30.4, PostalCode = "75001" }, "api")]));
        c.Sync(Req([Obs("bienici", "x", new ListingData { Price = 950, Surface = 30 })]));
        var l = c.GetProperty(c.PropertyIdOf("bienici:x")!)!.Summary.Listings[0];
        Assert.Equal(30.4, l.Data.Surface);
        Assert.Equal(1000, l.Data.Price);

        c.Sync(Req([Obs("bienici", "x", new ListingData { Price = 950 }, "api")]));
        Assert.Equal(950, l.Data.Price);
        Assert.Equal(1000, Assert.Single(l.PriceHistory).Price);
    }

    [Fact]
    public void ContactStage_ImpliesToContact_AndIsReportedToThePlugin()
    {
        var c = NewCatalog();
        c.Sync(Req([Obs("bienici", "x", new ListingData { PostalCode = "75001" })]));
        var pid = c.PropertyIdOf("bienici:x")!;
        c.SetContactStage(pid, ContactStage.VisitScheduled);
        var v = c.View("bienici:x")!;
        Assert.Equal(PropertyStatus.ToContact, v.Status);
        Assert.Equal(ContactStage.VisitScheduled, v.ContactStage);
        Assert.EndsWith($"/biens/{pid}", v.WebUrl);
    }

    [Fact]
    public void State_SurvivesARestart()
    {
        var c = NewCatalog();
        c.Sync(Req([Obs("bienici", "hektor", DedupScorerTests.Bienici, "api")], [Status("a1", "bienici:hektor", PropertyStatus.Rejected, _now)]));
        c.Sync(Req([Obs("seloger", "999", DedupScorerTests.SeLoger)]));
        c.SetNote(c.PropertyIdOf("seloger:999")!, "Trop loin du métro");

        var restarted = NewCatalog();
        var v = restarted.View("seloger:999")!;
        Assert.Equal(PropertyStatus.Rejected, v.Status);
        Assert.Equal("Trop loin du métro", v.Note);
        Assert.Single(v.Siblings);
        // L'idempotence reste valide après un redémarrage.
        Assert.Equal(new[] { "a1" }, restarted.Sync(Req(actions: [Status("a1", "bienici:hektor", PropertyStatus.Seen, _now)])).AppliedActionIds);
        Assert.Equal(PropertyStatus.Rejected, restarted.View("bienici:hektor")!.Status);
    }

    [Fact]
    public void Query_FiltersByStatusAndText()
    {
        var c = NewCatalog();
        c.Sync(Req([
            Obs("bienici", "a", new ListingData { Title = "Studio Montmartre", Price = 800, PostalCode = "75018" }),
            Obs("bienici", "b", new ListingData { Title = "2 pièces Bastille", Price = 1400, PostalCode = "75011" }),
        ]));
        c.SetStatus(c.PropertyIdOf("bienici:b")!, PropertyStatus.Rejected);

        var notRejected = c.QueryProperties(new PropertyQuery { Statuses = new HashSet<PropertyStatus> { PropertyStatus.None } });
        Assert.Equal("Studio Montmartre", Assert.Single(notRejected).Title);
        Assert.Single(c.QueryProperties(new PropertyQuery { Text = "bastille" }));
        Assert.Single(c.QueryProperties(new PropertyQuery { MaxPrice = 1000 }));
    }
}
