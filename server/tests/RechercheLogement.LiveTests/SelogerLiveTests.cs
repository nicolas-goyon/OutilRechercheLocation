using RechercheLogement.Server.Sites;
using RechercheLogement.Server.Sites.Seloger;

namespace RechercheLogement.LiveTests;

/// <summary>
/// SeLoger refuse les appels hors navigateur : le serveur passe par le plugin (BrowserRelay), ce qu'une CI ne peut pas
/// reproduire. Ce test vérifie seulement ce qui est vérifiable : si l'API répond quand même, son format est toujours lu ;
/// si elle refuse (cas attendu), le test le signale sans échouer.
/// </summary>
public class SelogerLiveTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("65240", PlaceKind.PostalCode)]
    [InlineData("Arreau", PlaceKind.Auto)]
    [InlineData("Hautes-Pyrénées", PlaceKind.Department)]
    public async Task Autocomplete_IsReadable_WhenNotBlocked(string query, PlaceKind kind)
    {
        var url = $"https://www.seloger.com/search-mfe-bff/autocomplete/suggestion?query={Uri.EscapeDataString(query)}&limit=5&distributionType=Buy";
        using var res = await LiveHttp.Factory.CreateClient("").GetAsync(url);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode || !text.TrimStart().StartsWith('{'))
        {
            output.WriteLine($"SeLoger refuse l'appel hors navigateur (HTTP {(int)res.StatusCode}) : attendu, la recherche passe par le plugin.");
            return;
        }
        // Même règle que le serveur : un lieu non reconnu ici le serait aussi pendant les recherches des projets.
        var place = SelogerSearchAdapter.PickPlace(System.Text.Json.Nodes.JsonNode.Parse(text)!, new PlaceQuery(kind, query));
        Assert.NotNull(place);
        output.WriteLine($"SeLoger a répondu : {place.Label}");
    }
}
