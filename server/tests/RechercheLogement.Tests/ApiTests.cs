using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace RechercheLogement.Tests;

/// <summary>Tests d'intégration HTTP : token, et format JSON que le plugin attend.</summary>
public class ApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Token = "integration-token";

    private HttpClient Client(string? token)
    {
        var client = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("RechercheLogement:Storage", "memory");
            b.UseSetting("RechercheLogement:ApiToken", Token);
        }).CreateClient();
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("mauvais-token")]
    public async Task Api_RequiresTheToken(string? token)
    {
        var res = await Client(token).GetAsync("/api/ping");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Health_IsPublic()
    {
        var res = await Client(null).GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Sync_UsesThePluginJsonFormat()
    {
        var body = JsonNode.Parse("""
            {
              "clientVersion": "0.2.0",
              "observations": [{ "site": "bienici", "siteId": "x1", "source": "card", "seenAt": 1,
                                 "data": { "title": "Appartement 2 pièces 40 m²", "price": 1200, "surface": 40, "rooms": 2, "postalCode": "75011", "transaction": "rent" } }],
              "actions": [{ "id": "a1", "at": 1, "type": "setStatus", "key": "bienici:x1", "status": "toContact" }],
              "want": []
            }
            """);
        var res = await Client(Token).PostAsJsonAsync("/api/sync", body);
        res.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        Assert.Equal("a1", json["appliedActionIds"]![0]!.GetValue<string>());
        var view = json["listings"]!["bienici:x1"]!;
        Assert.Equal("toContact", view["status"]!.GetValue<string>());
        Assert.Equal("pending", view["contactStage"]!.GetValue<string>());
        Assert.NotNull(view["siblings"]);
    }
}
