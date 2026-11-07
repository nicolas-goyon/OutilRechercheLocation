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
            return ReadJson((int)res.StatusCode, await res.Content.ReadAsStringAsync(ct), siteLabel);
        }
    }

    /// <summary>Réponse d'un site -> JSON. Refus et page anti-robot deviennent une <see cref="SiteException"/>.</summary>
    public static JsonNode ReadJson(int status, string text, string siteLabel)
    {
        if (status is (int)HttpStatusCode.Forbidden or (int)HttpStatusCode.TooManyRequests)
            throw new SiteException($"{siteLabel} refuse la requête (HTTP {status}) : protection anti-robot du site.");
        if (status is < 200 or >= 300)
            throw new SiteException($"{siteLabel} : HTTP {status} {JsonRead.Truncate(text, 160)}");
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

    /// <summary>
    /// Page de vérification anti-robot (DataDome, Cloudflare) au lieu du contenu attendu : page courte avec un de leurs
    /// marqueurs. (Une vraie page peut contenir ces scripts, par exemple PAP : la taille départage.)
    /// </summary>
    public static bool LooksLikeCaptcha(string html) =>
        html.Length < 30_000 && (html.Contains("captcha-delivery.com", StringComparison.Ordinal)
            || html.Contains("cf-chl", StringComparison.Ordinal) || html.Contains("challenge-platform", StringComparison.Ordinal)
            || html.Contains("Just a moment", StringComparison.Ordinal));

    public static Task<JsonNode> GetJsonAsync(HttpClient http, string url, string siteLabel, CancellationToken ct) =>
        SendJsonAsync(http, new HttpRequestMessage(HttpMethod.Get, url), siteLabel, ct);
}
