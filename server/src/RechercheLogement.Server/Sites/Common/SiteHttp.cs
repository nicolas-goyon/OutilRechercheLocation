using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RechercheLogement.Server.Sites.Common;

/// <summary>Appels HTTP des sites : client commun (en-têtes de navigateur) et erreurs lisibles.</summary>
public static class SiteHttp
{
    public const string ClientName = "sites";

    /// <summary>Envoie la requête et lit le JSON. Refus, panne et page anti-robot deviennent une <see cref="SiteException"/>.</summary>
    public static async Task<JsonNode> SendJsonAsync(HttpClient http, HttpRequestMessage request, string siteLabel, CancellationToken ct)
    {
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(request, ct);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new SiteException($"{siteLabel} ne répond pas (délai dépassé).", e);
        }
        catch (HttpRequestException e)
        {
            throw new SiteException($"{siteLabel} injoignable : {e.Message}", e);
        }
        using (res)
        {
            if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new SiteException($"{siteLabel} refuse la requête (HTTP {(int)res.StatusCode}) : protection anti-robot du site.");
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new SiteException($"{siteLabel} : HTTP {(int)res.StatusCode} {JsonRead.Truncate(text, 160)}");
            try
            {
                return JsonNode.Parse(text) ?? throw new SiteException($"{siteLabel} : réponse vide.");
            }
            catch (JsonException e)
            {
                // Page HTML (captcha) au lieu du JSON attendu.
                throw new SiteException($"{siteLabel} : réponse inattendue (page de vérification anti-robot ?).", e);
            }
        }
    }

    public static Task<JsonNode> GetJsonAsync(HttpClient http, string url, string siteLabel, CancellationToken ct) =>
        SendJsonAsync(http, new HttpRequestMessage(HttpMethod.Get, url), siteLabel, ct);
}
