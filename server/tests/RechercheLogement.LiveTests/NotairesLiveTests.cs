using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Sites;
using RechercheLogement.Server.Sites.Notaires;

namespace RechercheLogement.LiveTests;

/// <summary>
/// Immobilier des Notaires de France : lieux et recherche sur la vraie API. Le site demande 10 s entre deux requêtes
/// (robots.txt) : ces tests sont lents à dessein et lisent peu d'annonces.
/// </summary>
public class NotairesLiveTests(ITestOutputHelper output)
{
    /// <summary>Un seul adaptateur pour les deux tests : le délai de 10 s vaut aussi d'un test à l'autre.</summary>
    private static readonly NotairesSearchAdapter Adapter = new(LiveHttp.Factory);

    /// <summary>
    /// Appel au site, ou null s'il refuse depuis ce réseau (pare-feu, anti-robot) : ce n'est pas un changement de
    /// format, le test le signale sans échouer.
    /// </summary>
    private async Task<T?> OrRefusedAsync<T>(Func<Task<T?>> call) where T : class
    {
        try
        {
            return await LiveHttp.Retry(call);
        }
        catch (SiteException e) when (e.Message.Contains("refuse", StringComparison.Ordinal) || e.Message.Contains("injoignable", StringComparison.Ordinal))
        {
            output.WriteLine($"Le site refuse l'appel depuis ce réseau : non vérifiable ici. {e.Message}");
            return null;
        }
    }

    [Fact]
    public async Task Places_AreRecognized()
    {
        var adapter = Adapter;

        var postalCode = await OrRefusedAsync(() => adapter.ResolvePlaceAsync(new PlaceQuery(PlaceKind.PostalCode, "65200"), TransactionType.Buy, default));
        if (postalCode is null) return;
        var commune = await OrRefusedAsync(() => adapter.ResolvePlaceAsync(new PlaceQuery(PlaceKind.Auto, "Arreau", "65240"), TransactionType.Buy, default));

        output.WriteLine($"{postalCode.Label} : {postalCode.Ids.Count} localités ; {commune?.Label} : {string.Join(", ", commune?.Ids ?? [])}");
        Assert.True(postalCode.Ids.Count >= 10, "Le code postal 65200 compte plus de 20 localités.");
        Assert.Contains("loc:28605", postalCode.Ids); // Bagnères-de-Bigorre
        Assert.Equal(["loc:28579"], commune!.Ids);
    }

    [Fact]
    public async Task Search_ReturnsReadableListings()
    {
        var adapter = Adapter;
        var project = new SearchProject { Id = "live", Transaction = TransactionType.Buy, PropertyTypes = [PropertyTypes.House] };

        // 1 page de liste + 4 fiches + leurs offices : environ 1 min 30.
        var outcome = await OrRefusedAsync<SearchOutcome>(async () => await adapter.SearchAsync(new SearchRequest(project, NotairesSearchAdapter.Department("Hautes-Pyrénées")!.Ids, 4), default));
        if (outcome is null) return;

        output.WriteLine($"{outcome.Items.Count} annonces lues sur {outcome.Total}.");
        foreach (var i in outcome.Items)
            output.WriteLine($"{i.Data.Url} | {i.Data.Title} | {i.Data.Price} € | {i.Data.Surface} m² | {i.Data.PostalCode} {i.Data.City} | DPE {i.Data.EnergyClass} | {i.Data.Photos?.Count} photos | {i.Data.AgencyName}");
        Assert.True(outcome.Total >= 10, $"Trop peu d'annonces de maisons dans le 65 : {outcome.Total}");
        Assert.Equal(4, outcome.Items.Count);
        LiveChecks.MostHave(outcome.Items, "un prix", d => d.Price > 0);
        LiveChecks.MostHave(outcome.Items, "une surface", d => d.Surface > 0);
        LiveChecks.MostHave(outcome.Items, "un code postal des Hautes-Pyrénées", d => d.PostalCode?.StartsWith("65", StringComparison.Ordinal) == true);
        LiveChecks.MostHave(outcome.Items, "une ville", d => !string.IsNullOrWhiteSpace(d.City));
        LiveChecks.MostHave(outcome.Items, "une adresse d'annonce", d => d.Url?.StartsWith("https://www.immo", StringComparison.Ordinal) == true);
        LiveChecks.MostHave(outcome.Items, "des photos de la fiche", d => d.Photos is { Count: > 1 });
        LiveChecks.MostHave(outcome.Items, "le nom de l'office", d => !string.IsNullOrWhiteSpace(d.AgencyName));
        LiveChecks.MostHave(outcome.Items, "le type maison", d => d.PropertyType == PropertyTypes.House);
        Assert.All(outcome.Items, i => Assert.Null(i.Data.Geo));
    }
}
