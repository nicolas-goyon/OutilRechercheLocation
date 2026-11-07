using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Sites;
using RechercheLogement.Server.Sites.Bienici;

namespace RechercheLogement.LiveTests;

/// <summary>Bien'ici : lieux, recherche et lecture des annonces, sur le vrai site.</summary>
public class BieniciLiveTests(ITestOutputHelper output)
{
    private readonly BieniciSearchAdapter _adapter = new(LiveHttp.Factory);

    [Fact]
    public async Task Places_AreRecognized()
    {
        var commune = await LiveHttp.Retry(() => _adapter.ResolvePlaceAsync(new PlaceQuery(PlaceKind.Auto, "Arreau", "65240"), TransactionType.Buy, default));
        var department = await LiveHttp.Retry(() => _adapter.ResolvePlaceAsync(new PlaceQuery(PlaceKind.Department, "Hautes-Pyrénées"), TransactionType.Buy, default));
        var postalCode = await LiveHttp.Retry(() => _adapter.ResolvePlaceAsync(new PlaceQuery(PlaceKind.PostalCode, "65170"), TransactionType.Buy, default));

        Assert.NotEmpty(commune!.Ids);
        Assert.Contains("département", department!.Label);
        Assert.NotEmpty(postalCode!.Ids);
    }

    /// <summary>
    /// Noms portés par des dizaines de communes (Bien'ici ne propose que 10 lieux) et commune nouvelle inconnue de
    /// Bien'ici (Val-de-Sos) : toutes reconnues, et jamais une homonyme d'un autre département.
    /// </summary>
    [Theory]
    [InlineData("Saint-Michel", "64220")]
    [InlineData("Mont", "64300")]
    [InlineData("Sainte-Marie", "65370")]
    [InlineData("Villeneuve", "09800")]
    [InlineData("Val-de-Sos", "09220")]
    public async Task CommonNames_AreRecognizedInTheirDepartment(string name, string cp)
    {
        var place = await LiveHttp.Retry(() => _adapter.ResolvePlaceAsync(new PlaceQuery(PlaceKind.Auto, name, cp), TransactionType.Buy, default));
        Assert.NotNull(place);
        output.WriteLine($"{name} ({cp}) -> {place.Label} : {string.Join(", ", place.Ids)}");
        Assert.Contains(cp, place.Label, StringComparison.Ordinal); // "Saint-Michel (64220)", pas une homonyme
    }

    [Fact]
    public async Task Search_ReturnsReadableListings()
    {
        var place = await LiveHttp.Retry(() => _adapter.ResolvePlaceAsync(new PlaceQuery(PlaceKind.Department, "Hautes-Pyrénées"), TransactionType.Buy, default));
        var project = new SearchProject { Id = "live", Transaction = TransactionType.Buy, PropertyTypes = [PropertyTypes.House] };

        var outcome = await LiveHttp.Retry(() => _adapter.SearchAsync(new SearchRequest(project, place!.Ids, 40), default));

        output.WriteLine($"{outcome.Items.Count} annonces lues sur {outcome.Total}.");
        Assert.True(outcome.Items.Count >= 10, $"Trop peu d'annonces : {outcome.Items.Count}");
        LiveChecks.MostHave(outcome.Items, "un prix", d => d.Price > 0);
        LiveChecks.MostHave(outcome.Items, "une surface", d => d.Surface > 0);
        LiveChecks.MostHave(outcome.Items, "un code postal des Hautes-Pyrénées", d => d.PostalCode?.StartsWith("65", StringComparison.Ordinal) == true);
        LiveChecks.MostHave(outcome.Items, "une adresse d'annonce", d => d.Url?.StartsWith("https://www.bienici.com/", StringComparison.Ordinal) == true);
        LiveChecks.MostHave(outcome.Items, "des photos", d => d.Photos is { Count: > 0 });
        LiveChecks.MostHave(outcome.Items, "le type maison", d => PropertyTypes.Normalize(d.PropertyType) == PropertyTypes.House);
    }

    /// <summary>
    /// Filtres détaillés (terrain, DPE, piscine, ancien) : Bien'ici les applique, et les annonces renvoyées les
    /// respectent. Un filtre ignoré par le site (nom changé) laisserait passer des annonces hors critères.
    /// </summary>
    [Fact]
    public async Task DetailedFilters_AreAppliedBySite()
    {
        var place = await LiveHttp.Retry(() => _adapter.ResolvePlaceAsync(new PlaceQuery(PlaceKind.Department, "Hautes-Pyrénées"), TransactionType.Buy, default));
        var project = new SearchProject
        {
            Id = "live",
            Transaction = TransactionType.Buy,
            PropertyTypes = [PropertyTypes.House],
            LandSurfaceMin = 1000,
            EnergyClassMax = "D",
            NewBuild = false,
            RequiredFeatures = [Features.Pool],
        };

        var outcome = await LiveHttp.Retry(() => _adapter.SearchAsync(new SearchRequest(project, place!.Ids, 24), default));

        output.WriteLine($"{outcome.Items.Count} annonces lues sur {outcome.Total}.");
        Assert.True(outcome.Items.Count >= 3, $"Trop peu d'annonces : {outcome.Items.Count}");
        LiveChecks.MostHave(outcome.Items, "un terrain d'au moins 1 000 m²", d => d.LandSurface >= 1000);
        LiveChecks.MostHave(outcome.Items, "un DPE de A à D", d => d.EnergyClass is "A" or "B" or "C" or "D");
        LiveChecks.MostHave(outcome.Items, "une piscine", d => d.Has(Features.Pool) == true);
        // Bien'ici n'a pas de filtre "viager" : il en renvoie, et l'outil les écarte ensuite.
        var sansViager = outcome.Items.Where(i => i.Data.SaleKind != SaleKinds.LifeAnnuity).ToList();
        output.WriteLine($"{outcome.Items.Count - sansViager.Count} viager(s) écarté(s) par l'outil.");
        Assert.All(sansViager, i => Assert.True(ProjectMatcher.Matches(project, i.Data, out var why), $"{i.Data.Url} : {why}"));
        Assert.All(outcome.Items.Except(sansViager), i => Assert.False(ProjectMatcher.Matches(project, i.Data, out _)));
    }
}

internal static class LiveChecks
{
    /// <summary>Au moins 80 % des annonces ont ce champ : une annonce incomplète arrive, un champ toujours vide signale un changement du site.</summary>
    public static void MostHave(IReadOnlyList<CollectedItem> items, string what, Func<ListingData, bool> has)
    {
        var count = items.Count(i => has(i.Data));
        Assert.True(count >= items.Count * 0.8, $"{count}/{items.Count} annonces ont {what} : le format du site a peut-être changé.");
    }
}
