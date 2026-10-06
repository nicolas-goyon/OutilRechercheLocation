using RechercheLogement.Core.Model;
using RechercheLogement.Core.Services;
using RechercheLogement.Server.Storage;

namespace RechercheLogement.Tests;

public class SavedSearchTests
{
    private long _now = 1_000_000;

    private const string Bienici = "https://www.bienici.com/recherche/location/rodez-12000/2-pieces-et-plus?prix-max=700";
    private const string Seloger = "https://www.seloger.com/classified-search?distributionTypes=Rent&estateTypes=Apartment&locations=AD08FR13100&priceMax=700";

    [Theory]
    [InlineData(Bienici, "bienici")]
    [InlineData(Seloger, "seloger")]
    [InlineData("https://www.leboncoin.fr/recherche?category=10", "leboncoin")]
    [InlineData("https://www.exemple-immo.fr/liste", "exemple-immo.fr")]
    public void SiteOf_RecognizesTheSites(string url, string site) => Assert.Equal(site, SavedSearch.SiteOf(url));

    [Theory]
    [InlineData("  www.bienici.com/recherche  ", "https://www.bienici.com/recherche")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("", null)]
    public void NormalizeUrl_AcceptsOnlyHttp(string input, string? expected) => Assert.Equal(expected, SavedSearch.NormalizeUrl(input));

    [Fact]
    public void Searches_AllowDuplicates_AndKeepTheirOrder()
    {
        var c = new Catalog(new InMemoryPersistence(), new CatalogOptions { Now = () => _now++ });
        var a = c.AddSearch("Rodez 2p", Bienici);
        var b = c.AddSearch("Rodez 2p (bis)", Bienici, "même URL, autre note");
        var s = c.AddSearch(null, Seloger);

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal("Recherche SeLoger", s.Name);
        Assert.Equal(new[] { a.Id, b.Id, s.Id }, c.Searches().Select(x => x.Id));

        var copy = c.DuplicateSearch(a.Id)!;
        Assert.Equal("Rodez 2p (copie)", copy.Name);
        Assert.Equal(new[] { a.Id, copy.Id, b.Id, s.Id }, c.Searches().Select(x => x.Id));

        c.MoveSearch(b.Id, -1);
        Assert.Equal(new[] { a.Id, b.Id, copy.Id, s.Id }, c.Searches().Select(x => x.Id));

        c.DeleteSearch(copy.Id);
        Assert.Equal(3, c.Searches().Count);
        Assert.Throws<ArgumentException>(() => c.AddSearch("x", "pas une url"));
    }

    [Fact]
    public void Searches_SurviveARestart_WithSqlite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rl-test-{Guid.NewGuid():N}.db");
        try
        {
            var c = new Catalog(new SqlitePersistence(path), new CatalogOptions { Now = () => _now++ });
            var a = c.AddSearch("Rodez", Bienici, "note");
            c.AddSearch("Bordeaux", Seloger);
            c.UpdateSearch(a.Id, "Rodez 700 €", Bienici + "&surface-min=40", null);
            c.MarkSearchOpened(a.Id);

            var restarted = new Catalog(new SqlitePersistence(path));
            var list = restarted.Searches();
            Assert.Equal(2, list.Count);
            Assert.Equal("Rodez 700 €", list[0].Name);
            Assert.EndsWith("surface-min=40", list[0].Url);
            Assert.Null(list[0].Note);
            Assert.NotNull(list[0].LastOpenedAt);
            Assert.Equal("seloger", list[1].Site);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
    }
}
