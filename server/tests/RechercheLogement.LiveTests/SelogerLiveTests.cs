using RechercheLogement.Server.Sites.Seloger;

namespace RechercheLogement.LiveTests;

/// <summary>
/// SeLoger refuse les appels hors navigateur : le serveur passe par le plugin (BrowserRelay), ce qu'une CI ne peut pas
/// reproduire. Ce test vérifie seulement ce qui est vérifiable : si l'API répond quand même, son format est toujours lu ;
/// si elle refuse (cas attendu), le test le signale sans échouer.
/// </summary>
public class SelogerLiveTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Autocomplete_IsReadable_WhenNotBlocked()
    {
        var url = "https://www.seloger.com/search-mfe-bff/autocomplete/suggestion?query=Arreau&limit=5&distributionType=Buy";
        using var res = await LiveHttp.Factory.CreateClient("").GetAsync(url);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode || !text.TrimStart().StartsWith('{'))
        {
            output.WriteLine($"SeLoger refuse l'appel hors navigateur (HTTP {(int)res.StatusCode}) : attendu, la recherche passe par le plugin.");
            return;
        }
        var place = SelogerSearchAdapter.PickPlace(System.Text.Json.Nodes.JsonNode.Parse(text)!);
        Assert.NotNull(place);
        output.WriteLine($"SeLoger a répondu : {place.Label}");
    }
}
