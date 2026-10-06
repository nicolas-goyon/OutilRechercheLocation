using System.Security.Cryptography;
using System.Text;
using RechercheLogement.Core.Services;

namespace RechercheLogement.Server.Api;

/// <summary>
/// Token commun au plugin et au serveur.
///
/// Ordre de priorité :
///  1. configuration (RechercheLogement__ApiToken).
///  2. valeur stockée en base.
///  3. valeur aléatoire générée au premier démarrage (32 octets, base64url), puis stockée en base.
///
/// La page Paramètres affiche le token. Le journal l'affiche aussi au démarrage.
/// </summary>
public sealed class ApiTokenService(Catalog catalog, ServerSettings settings, ILogger<ApiTokenService> logger)
{
    private const string SettingKey = "apiToken";
    private byte[]? _current;

    public bool FromConfiguration => !string.IsNullOrWhiteSpace(settings.ApiToken);

    public string EnsureToken()
    {
        var token = Current;
        logger.LogInformation("Token API du plugin : {Token}  (le coller dans le plugin : panneau 🏠 > Connexion)", token);
        return token;
    }

    public string Current
    {
        get
        {
            if (FromConfiguration) return settings.ApiToken!.Trim();
            var stored = catalog.GetSetting(SettingKey);
            if (!string.IsNullOrEmpty(stored)) return stored;
            return Regenerate();
        }
    }

    /// <summary>Génère un nouveau token. Le serveur refuse immédiatement l'ancien token. Coller le nouveau token dans le plugin.</summary>
    public string Regenerate()
    {
        if (FromConfiguration) throw new InvalidOperationException("La configuration fixe le token (RechercheLogement__ApiToken). Impossible de le régénérer.");
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        catalog.SetSetting(SettingKey, token);
        _current = null;
        return token;
    }

    public bool IsValid(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        _current ??= Encoding.UTF8.GetBytes(Current);
        var given = Encoding.UTF8.GetBytes(candidate);
        return CryptographicOperations.FixedTimeEquals(given, _current);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Chaque route /api/* exige "Authorization: Bearer &lt;token&gt;".</summary>
public sealed class ApiTokenMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ApiTokenService tokens)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            var header = context.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
            if (!tokens.IsValid(token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await context.Response.WriteAsJsonAsync(new { error = "Token API absent ou incorrect." });
                return;
            }
        }
        await next(context);
    }
}
