using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Sites;
using RechercheLogement.Server.Sites.GreenAcres;

namespace RechercheLogement.LiveTests;

/// <summary>Green-Acres : pages HTML et autocomplétion du vrai site. Peu de requêtes, au moins une seconde d'écart.</summary>
public class GreenAcresLiveTests(ITestOutputHelper output)
{
    private static readonly GreenAcresSearchAdapter Adapter = new(GreenAcresSearchAdapter.CreateClient(), delayMs: 1200);

    /// <summary>
    /// Appel au site, ou null s'il refuse (pare-feu qui bloque les serveurs des clouds, comme ceux de la CI) : ce n'est
    /// pas un changement de format, le test le signale sans échouer.
    /// </summary>
    private async Task<T?> CallAsync<T>(Func<Task<T>> call) where T : class?
    {
        try
        {
            return await LiveHttp.Retry(call);
        }
        catch (SiteException e) when (e.Message.Contains("refuse", StringComparison.Ordinal))
        {
            output.WriteLine($"Le site refuse l'appel depuis ce réseau : non vérifiable ici. {e.Message}");
            return null;
        }
    }

    [Fact]
    public async Task Commune_IsRecognizedWithItsPostalCode()
    {
        if (await CallAsync(() => Adapter.ResolvePlaceAsync(new PlaceQuery(PlaceKind.Auto, "Campan", "65710"), TransactionType.Buy, default)) is not { } place)
            return;

        output.WriteLine($"{place.Label} : {string.Join(", ", place.Ids)}");
        Assert.Equal(["city_33034"], place.Ids);
    }

    [Fact]
    public async Task Search_ReadsListingsAndTheirDetails()
    {
        var project = new SearchProject { Id = "live", Transaction = TransactionType.Buy, PropertyTypes = [PropertyTypes.House] };

        if (await CallAsync(() => Adapter.SearchAsync(new SearchRequest(project, ["dp_65"], 5), default)) is not { } outcome) return;

        output.WriteLine($"{outcome.Items.Count} annonces lues sur {outcome.Total}.");
        foreach (var i in outcome.Items)
            output.WriteLine($"{i.SiteId} {i.Data.Price} € {i.Data.Surface} m² {i.Data.PostalCode} {i.Data.City} DPE {i.Data.EnergyClass} {i.Data.Photos?.Count} photos {i.Data.Url}");
        Assert.True(outcome.Total > 20, $"{outcome.Total} maisons dans les Hautes-Pyrénées : le site a peut-être changé.");
        Assert.Equal(5, outcome.Items.Count);
        Assert.False(outcome.Complete);
        LiveChecks.MostHave(outcome.Items, "un prix", d => d.Price > 0);
        LiveChecks.MostHave(outcome.Items, "une surface", d => d.Surface > 0);
        LiveChecks.MostHave(outcome.Items, "un code postal des Hautes-Pyrénées", d => d.PostalCode?.StartsWith("65", StringComparison.Ordinal) == true);
        LiveChecks.MostHave(outcome.Items, "une ville", d => !string.IsNullOrWhiteSpace(d.City));
        LiveChecks.MostHave(outcome.Items, "une position", d => d.Geo is not null);
        LiveChecks.MostHave(outcome.Items, "une adresse d'annonce", d => d.Url?.StartsWith("https://www.green-acres.fr/fr/properties/", StringComparison.Ordinal) == true);
        LiveChecks.MostHave(outcome.Items, "des photos", d => d.Photos is { Count: > 0 });
        LiveChecks.MostHave(outcome.Items, "le type maison", d => PropertyTypes.Normalize(d.PropertyType) is PropertyTypes.House or PropertyTypes.TownHouse);
    }

    /// <summary>
    /// Code postal "pc_…" construit sans appel : le site l'accepte sans élargir la recherche (CheckConditions lève une
    /// erreur sinon), et ne renvoie que des annonces de ce code postal.
    /// </summary>
    [Fact]
    public async Task PostalCode_IsAcceptedWithoutWidening()
    {
        var project = new SearchProject { Id = "live", Transaction = TransactionType.Buy, PropertyTypes = [PropertyTypes.House] };

        if (await CallAsync(() => Adapter.SearchAsync(new SearchRequest(project, ["pc_65200"], 2), default)) is not { } outcome) return;

        output.WriteLine($"Code postal 65200 : {outcome.Items.Count} annonces lues sur {outcome.Total}.");
        Assert.True(outcome.Total > 0, "Aucune maison à 65200 (Bagnères-de-Bigorre) : pc_ n'est peut-être plus reconnu.");
        Assert.All(outcome.Items, i => Assert.Equal("65200", i.Data.PostalCode));
    }
}
