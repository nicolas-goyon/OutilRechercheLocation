using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RechercheLogement.Server.Sites;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Tests;

/// <summary>Relais navigateur : requêtes exécutées par le plugin pour le serveur.</summary>
public class BrowserRelayTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Site = "seloger";
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);

    private static BrowserRelay Relay(TimeSpan? timeout = null) => new(delayMs: 0, timeout: timeout);

    private static async Task<RelayRequest> Take(BrowserRelay relay)
    {
        return await relay.NextAsync(Site, TimeSpan.FromSeconds(5), default) ?? throw new Xunit.Sdk.XunitException("aucune requête");
    }

    [Fact]
    public async Task WithoutTab_ExplainsThatTheSearchWaits()
    {
        var e = await Assert.ThrowsAsync<SiteException>(() => Relay().SendJsonAsync(Site, "SeLoger", HttpMethod.Get, "https://x", null, default));
        Assert.StartsWith(BrowserRelay.NoTabPrefix, e.Message);
    }

    [Fact]
    public async Task ResponseOfThePlugin_IsReadAsJson()
    {
        var relay = Relay();
        await relay.NextAsync(Site, Short, default);

        var call = relay.SendJsonAsync(Site, "SeLoger", HttpMethod.Post, "https://x", """{"a":1}""", default);
        var request = await Take(relay);
        Assert.Equal("POST", request.Method);
        Assert.True(relay.Complete(request.Id, new RelayResult(200, """{"ok":true}""", null)));

        Assert.True((bool)(await call)["ok"]!);
    }

    [Theory]
    [InlineData(403, "<html>", "Passer la vérification")]
    [InlineData(200, "<html>captcha</html>", "Passer la vérification")]
    [InlineData(500, "panne", "HTTP 500")]
    public async Task BlockedOrFailing_StopsWithAReadableMessage(int status, string body, string expected)
    {
        var relay = Relay();
        await relay.NextAsync(Site, Short, default);

        var call = relay.SendJsonAsync(Site, "SeLoger", HttpMethod.Get, "https://x", null, default);
        relay.Complete((await Take(relay)).Id, new RelayResult(status, body, null));

        var e = await Assert.ThrowsAsync<SiteException>(() => call);
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public async Task SilentTab_TimesOut_AndLateAnswerIsIgnored()
    {
        var relay = Relay(TimeSpan.FromMilliseconds(100));
        await relay.NextAsync(Site, Short, default);

        var call = relay.SendJsonAsync(Site, "SeLoger", HttpMethod.Get, "https://x", null, default);
        var request = await Take(relay);

        await Assert.ThrowsAsync<SiteException>(() => call);
        Assert.False(relay.Complete(request.Id, new RelayResult(200, "{}", null)));
    }

    [Fact]
    public async Task OnlyOneRequestAtATime()
    {
        var relay = Relay();
        await relay.NextAsync(Site, Short, default);
        var first = relay.SendJsonAsync(Site, "SeLoger", HttpMethod.Get, "https://x/1", null, default);
        var second = relay.SendJsonAsync(Site, "SeLoger", HttpMethod.Get, "https://x/2", null, default);

        var r1 = await Take(relay);
        Assert.EndsWith("/1", r1.Url);
        Assert.Null(await relay.NextAsync(Site, Short, default));

        relay.Complete(r1.Id, new RelayResult(200, "{}", null));
        await first;
        relay.Complete((await Take(relay)).Id, new RelayResult(200, "{}", null));
        await second;
    }

    [Fact]
    public async Task ConnectedEvent_FiresOnlyWhenTheFirstTabAppears()
    {
        var relay = Relay();
        var events = 0;
        relay.SiteConnected += _ => events++;

        await relay.NextAsync(Site, Short, default);
        await relay.NextAsync(Site, Short, default);

        Assert.Equal(1, events);
    }

    [Fact]
    public async Task Api_RoundTrip()
    {
        var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("RechercheLogement:Storage", "memory");
            b.UseSetting("RechercheLogement:ApiToken", "t");
            b.UseSetting("RechercheLogement:SiteRelayDelayMs", "0");
            b.UseSetting("RechercheLogement:ProjectsAutoRun", "false");
        });
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "t");
        var relay = app.Services.GetRequiredService<BrowserRelay>();

        var unknown = await client.PostAsJsonAsync("/api/relay/inconnu/result", new { status = 200 });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var poll = client.GetFromJsonAsync<JsonNode>("/api/relay/next?site=seloger");
        while (!relay.IsConnected(Site)) await Task.Delay(10);
        var call = relay.SendJsonAsync(Site, "SeLoger", HttpMethod.Get, "https://www.seloger.com/classifiedList/1", null, default);
        var request = (await poll)!;
        Assert.Equal("GET", (string?)request["method"]);

        var done = await client.PostAsJsonAsync($"/api/relay/{request["id"]}/result", new { status = 200, body = "[1]" });
        Assert.Equal(HttpStatusCode.NoContent, done.StatusCode);
        Assert.Equal(1, (int)(await call)[0]!);
    }
}
