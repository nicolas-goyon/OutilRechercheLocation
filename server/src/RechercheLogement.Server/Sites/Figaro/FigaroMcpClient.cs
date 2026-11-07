using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Sites.Figaro;

/// <summary>
/// Client du serveur MCP public de Figaro Immobilier (https://immobilier.lefigaro.fr/mcp, « Streamable HTTP ») :
/// initialize -> en-tête mcp-session-id -> notifications/initialized -> tools/call. Réponses en JSON ou en flux SSE
/// ("data: {…}"). L'API web du site est interdite par son robots.txt : ce serveur est la voie prévue pour les outils.
/// Un appel à la fois, au moins <paramref name="delayMs"/> entre deux appels.
/// </summary>
public sealed class FigaroMcpClient(IHttpClientFactory factory, int delayMs) : IDisposable
{
    public const string Endpoint = "https://immobilier.lefigaro.fr/mcp";
    private const string Label = "Figaro Immobilier";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastCall = DateTimeOffset.MinValue;
    private string? _session;
    private int _nextId = 1;

    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Appelle un outil et renvoie son "structuredContent". Erreur de l'outil (isError, par exemple « Aucune
    /// localisation trouvée ») : null. Session expirée : une nouvelle session, une fois.
    /// </summary>
    public async Task<JsonNode?> CallToolAsync(string name, JsonObject arguments, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                _session ??= await InitializeAsync(ct);
                var (status, body) = await PostAsync(Request("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments.DeepClone() }), _session, ct);
                if (status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest && attempt == 0)
                {
                    _session = null; // session oubliée par le serveur
                    continue;
                }
                var result = ReadResponse(status, body)?["result"];
                return JsonRead.Bool(result?["isError"]) == true ? null : result?["structuredContent"];
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> InitializeAsync(CancellationToken ct)
    {
        var (status, body, session) = await SendAsync(Request("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-03-26",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "recherche-logement", ["version"] = "1.0" },
        }), null, ct);
        ReadResponse(status, body);
        if (session is null) throw new SiteException($"{Label} : pas de session MCP.");
        await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, session, ct);
        return session;
    }

    private JsonObject Request(string method, JsonObject parameters) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = _nextId++, ["method"] = method, ["params"] = parameters };

    private async Task<(HttpStatusCode, string)> PostAsync(JsonObject message, string session, CancellationToken ct)
    {
        var (status, body, _) = await SendAsync(message, session, ct);
        return (status, body);
    }

    private async Task<(HttpStatusCode Status, string Body, string? Session)> SendAsync(JsonObject message, string? session, CancellationToken ct)
    {
        var wait = _lastCall + TimeSpan.FromMilliseconds(delayMs) - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Accept.Clear();
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            if (session is not null) req.Headers.TryAddWithoutValidation("mcp-session-id", session);
            using var res = await factory.CreateClient(SiteHttp.ClientName).SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            var id = res.Headers.TryGetValues("mcp-session-id", out var values) ? values.FirstOrDefault() : null;
            return (res.StatusCode, body, id ?? session);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new SiteException($"{Label} ne répond pas (délai dépassé).", e);
        }
        catch (HttpRequestException e)
        {
            throw new SiteException($"{Label} injoignable : {e.Message}", e);
        }
        finally
        {
            _lastCall = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Réponse JSON-RPC, en JSON ou en flux SSE (dernière ligne "data:"). Erreur JSON-RPC : exception.</summary>
    public static JsonNode? ReadResponse(HttpStatusCode status, string body)
    {
        var text = body.TrimStart();
        if (text.StartsWith("event:", StringComparison.Ordinal) || text.StartsWith("data:", StringComparison.Ordinal))
        {
            text = body.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("data:", StringComparison.Ordinal))
                .Select(l => l[5..].Trim()).LastOrDefault() ?? "";
        }
        var json = SiteHttp.ReadJson((int)status, text, Label);
        if (json["error"] is { } error)
            throw new SiteException($"{Label} : {JsonRead.Str(error["message"]) ?? "erreur MCP"}");
        return json;
    }
}
