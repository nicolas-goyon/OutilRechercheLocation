using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Geo;
using RechercheLogement.Server.Sites;
using RechercheLogement.Server.Sites.Etat;

namespace RechercheLogement.LiveTests;

/// <summary>Cessions immobilières de l'État : pages HTML du vrai site.</summary>
public class EtatLiveTests(ITestOutputHelper output)
{
    [Fact]
    public async Task List_HasCardsWithAPlace()
    {
        var html = await LiveHttp.Retry(() => LiveHttp.Factory.CreateClient("").GetStringAsync($"{EtatListingParser.Origin}/recherche-biens?page=0"));

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
        var html = await LiveHttp.Retry(() => LiveHttp.Factory.CreateClient("").GetStringAsync($"{EtatListingParser.Origin}/recherche-biens?page=0"));
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
