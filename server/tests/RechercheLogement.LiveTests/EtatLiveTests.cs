using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Geo;
using RechercheLogement.Server.Sites;
using RechercheLogement.Server.Sites.Etat;

namespace RechercheLogement.LiveTests;

/// <summary>Cessions immobilières de l'État : pages HTML du vrai site.</summary>
public class EtatLiveTests(ITestOutputHelper output)
{
    /// <summary>
    /// Première page de la liste, ou null si le site refuse l'appel (pare-feu qui bloque les serveurs des clouds,
    /// comme ceux de la CI) : ce n'est pas un changement de format, le test le signale sans échouer.
    /// </summary>
    private async Task<string?> FirstPageAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{EtatListingParser.Origin}/recherche-biens?page=0");
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd("text/html");
        using var res = await LiveHttp.Factory.CreateClient("").SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        if (res.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            output.WriteLine($"Le site refuse l'appel depuis ce réseau (HTTP 403) : non vérifiable ici. Début de la réponse : {body[..Math.Min(body.Length, 300)]}");
            return null;
        }
        res.EnsureSuccessStatusCode();
        return body;
    }

    [Fact]
    public async Task List_HasCardsWithAPlace()
    {
        if (await LiveHttp.Retry(FirstPageAsync) is not { } html) return;

        var cards = EtatListingParser.ParseList(html);

        output.WriteLine($"{cards.Count} biens sur la première page.");
        Assert.True(cards.Count >= 5, $"{cards.Count} carte(s) lue(s) : le format de la liste a peut-être changé.");
        Assert.All(cards, c => Assert.NotNull(c.Department));
        Assert.All(cards, c => Assert.StartsWith("/biens/", c.Path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_ReadsTheDetailsOfADepartment()
    {
        // Département d'un bien en ligne aujourd'hui : le site compte peu de biens, il ne faut pas en supposer un.
        if (await LiveHttp.Retry(FirstPageAsync) is not { } html) return;
        var department = EtatListingParser.ParseList(html).First().Department!;
        var adapter = new EtatSearchAdapter(LiveHttp.Factory, new GeoServices(LiveHttp.Factory), delayMs: 1000);
        var project = new SearchProject { Id = "live", Transaction = TransactionType.Buy };

        var outcome = await LiveHttp.Retry(() => adapter.SearchAsync(new SearchRequest(project, [$"dep:{department}"], 3), default));

        output.WriteLine($"Département {department} : {outcome.Items.Count} bien(s) lus.");
        Assert.True(outcome.Complete, "La liste n'a pas été lue jusqu'au bout.");
        Assert.NotEmpty(outcome.Items);
        Assert.All(outcome.Items, i =>
        {
            Assert.False(string.IsNullOrWhiteSpace(i.Data.Title));
            Assert.NotNull(i.Data.Geo);
            Assert.NotNull(i.Data.PostalCode);
            Assert.StartsWith(EtatListingParser.Origin, i.Data.Url, StringComparison.Ordinal);
        });
    }
}
