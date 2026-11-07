using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Geo;
using RechercheLogement.Server.Sites;
using RechercheLogement.Server.Sites.Paruvendu;

namespace RechercheLogement.LiveTests;

/// <summary>ParuVendu immobilier : lieux, recherche et lecture des pages de liste, sur le vrai site.</summary>
public class ParuvenduLiveTests(ITestOutputHelper output)
{
    private readonly ParuvenduSearchAdapter _adapter = new(LiveHttp.Factory, new GeoServices(LiveHttp.Factory), delayMs: 1500);

    /// <summary>
    /// Appel au site, ou null s'il refuse l'appel depuis ce réseau (protection anti-robot, pare-feu des serveurs des
    /// clouds) : ce n'est pas un changement de format, le test le signale sans échouer.
    /// </summary>
    private async Task<T?> TryAsync<T>(Func<Task<T>> call) where T : class?
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
    public async Task Places_AreRecognized()
    {
        if (await TryAsync(() => _adapter.ResolvePlaceAsync(new PlaceQuery(PlaceKind.Auto, "Argelès-Gazost", "65400"), TransactionType.Buy, default)) is not { } commune) return;
        var department = await LiveHttp.Retry(() => _adapter.ResolvePlaceAsync(new PlaceQuery(PlaceKind.Department, "Hautes-Pyrénées"), TransactionType.Buy, default));
        var postalCode = await LiveHttp.Retry(() => _adapter.ResolvePlaceAsync(new PlaceQuery(PlaceKind.PostalCode, "65400"), TransactionType.Buy, default));

        Assert.Equal(["com:65025:65400"], commune.Ids);
        Assert.Equal(["dep:65"], department!.Ids);
        Assert.Equal(["cp:65400"], postalCode!.Ids);
    }

    [Fact]
    public async Task Search_ReturnsReadableListings()
    {
        var project = new SearchProject { Id = "live", Transaction = TransactionType.Buy, PropertyTypes = [PropertyTypes.House] };

        // Deux pages du département (60 annonces), triées par date de mise à jour.
        if (await TryAsync(() => _adapter.SearchAsync(new SearchRequest(project, ["dep:65"], 40), default)) is not { } outcome) return;

        output.WriteLine($"{outcome.Items.Count} annonces lues sur {outcome.Total}.");
        foreach (var d in outcome.Items.Select(i => i.Data).Take(10))
            output.WriteLine($"{d.City} {d.PostalCode} {d.Price} € {d.Surface} m² {d.Rooms} p. DPE {d.EnergyClass} {d.Url}");
        Assert.True(outcome.Items.Count >= 30, $"Trop peu d'annonces : {outcome.Items.Count}");
        Assert.False(outcome.Complete);
        Assert.Equal(outcome.Items.Count, outcome.Items.Select(i => i.SiteId).Distinct().Count());
        LiveChecks.MostHave(outcome.Items, "un prix", d => d.Price > 0);
        LiveChecks.MostHave(outcome.Items, "une surface", d => d.Surface > 0);
        LiveChecks.MostHave(outcome.Items, "une ville", d => !string.IsNullOrWhiteSpace(d.City));
        LiveChecks.MostHave(outcome.Items, "un code postal des Hautes-Pyrénées", d => d.PostalCode?.StartsWith("65", StringComparison.Ordinal) == true);
        LiveChecks.MostHave(outcome.Items, "une position", d => d.Geo is not null);
        LiveChecks.MostHave(outcome.Items, "une adresse d'annonce", d => d.Url?.StartsWith("https://www.paruvendu.fr/immobilier/", StringComparison.Ordinal) == true);
        LiveChecks.MostHave(outcome.Items, "des photos", d => d.Photos is { Count: > 0 });
        LiveChecks.MostHave(outcome.Items, "le type maison", d => PropertyTypes.Normalize(d.PropertyType) == PropertyTypes.House);
        Assert.DoesNotContain(outcome.Items, i => i.Data.Url!.Contains("/maison-neuve/", StringComparison.Ordinal)); // encarts ignorés
    }

    /// <summary>Commune seule (":0" : sans les 15 km autour) : toutes les annonces y sont, avec son code postal.</summary>
    [Fact]
    public async Task Search_OfACommuneStaysInTheCommune()
    {
        var project = new SearchProject { Id = "live", Transaction = TransactionType.Buy, PropertyTypes = [PropertyTypes.House] };

        if (await TryAsync(() => _adapter.SearchAsync(new SearchRequest(project, ["com:65025:65400"], 100), default)) is not { } outcome) return;

        output.WriteLine($"Argelès-Gazost : {outcome.Items.Count} annonces sur {outcome.Total}.");
        Assert.True(outcome.Complete);
        Assert.InRange(outcome.Items.Count, 1, 60); // élargie à 15 km, la recherche en donnerait plus de 150
        Assert.All(outcome.Items, i => Assert.Equal("65400", i.Data.PostalCode));
        Assert.All(outcome.Items, i => Assert.True(ProjectScope.SameCommune("Argelès-Gazost", i.Data.City), i.Data.City));
    }
}
