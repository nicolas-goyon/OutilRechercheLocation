using System.Text.Json.Nodes;
using RechercheLogement.Server.Sites;
using RechercheLogement.Server.Sites.Common;
using RechercheLogement.Server.Sites.Seloger;

namespace RechercheLogement.Tests;

/// <summary>
/// Suggestions SeLoger depuis novembre 2026 : libellé = nom seul ("Espelette"), sans département, suivi de
/// suggestions de recherches ("Espelette maison", "64 250000€ ou moins maison").
/// </summary>
public class SelogerPlacesTests
{
    private static JsonObject Suggestions(params (string Text, string Id)[] items) => new JsonObject
    {
        ["items"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject
        {
            ["text"] = i.Text,
            ["criteria"] = new JsonObject { ["location"] = new JsonObject { ["placeIds"] = new JsonArray(i.Id) } },
        }).ToArray()),
    };

    [Fact]
    public void CommuneWithPostalCode_IsSearchedByItsPostalCode()
    {
        var adapter = (ISearchAdapter)new SelogerSearchAdapter(new BrowserRelay());
        Assert.Equal(new PlaceQuery(PlaceKind.PostalCode, "64250"), adapter.Prepare(new PlaceQuery(PlaceKind.Auto, "Espelette", "64250")));
        Assert.Equal(new PlaceQuery(PlaceKind.Auto, "Espelette"), adapter.Prepare(new PlaceQuery(PlaceKind.Auto, "Espelette")));
        Assert.Equal(new PlaceQuery(PlaceKind.Department, "Aveyron"), adapter.Prepare(new PlaceQuery(PlaceKind.Department, "Aveyron")));
    }

    [Fact]
    public void PostalCode_TakesTheExactPostalCode()
    {
        var json = Suggestions(("64 250000€ ou moins maison", "AD06FR65"), ("64250", "POCOFR4113"), ("64250 maison ou appartement", "POCOFR4113"));
        var place = SelogerSearchAdapter.PickPlace(json, new PlaceQuery(PlaceKind.PostalCode, "64250"))!;
        Assert.Equal("POCOFR4113", Assert.Single(place.Ids));
    }

    [Fact]
    public void Commune_TakesOnlyTheSameName()
    {
        var json = Suggestions(("Saint-Pierre-d'Oléron", "AD08FR6329"), ("Saint-Pierre", "AD08RE16"));
        Assert.Equal("AD08RE16", SelogerSearchAdapter.PickPlace(json, new PlaceQuery(PlaceKind.Auto, "saint pierre"))!.Ids[0]);
        // Avec un code postal, un nom seul ne suffit pas : ce Saint-Pierre est à La Réunion.
        Assert.Null(SelogerSearchAdapter.PickPlace(json, new PlaceQuery(PlaceKind.Auto, "Saint-Pierre", "64500")));
        Assert.Null(SelogerSearchAdapter.PickPlace(json, new PlaceQuery(PlaceKind.Auto, "Saint-Pierre-du-Mont")));
    }

    [Fact]
    public void Department_TakesTheSameName()
    {
        var json = Suggestions(("Pyrénées-Atlantiques maison", "AD06FR65"), ("Pyrénées-Atlantiques", "AD06FR65"));
        Assert.Equal("Pyrénées-Atlantiques (département)", SelogerSearchAdapter.PickPlace(json, new PlaceQuery(PlaceKind.Department, "pyrenees atlantiques"))!.Label);
    }
}
