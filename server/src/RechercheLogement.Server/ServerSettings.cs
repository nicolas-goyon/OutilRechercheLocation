using System.Text.Json;
using System.Text.Json.Serialization;

namespace RechercheLogement.Server;

/// <summary>Section "RechercheLogement" de appsettings.json (surchargeable par variables d'environnement RechercheLogement__*).</summary>
public sealed class ServerSettings
{
    public const string Section = "RechercheLogement";

    /// <summary>Chemin du fichier SQLite (relatif au dossier de travail). Docker : /data/recherche-logement.db.</summary>
    public string DatabasePath { get; set; } = "data/recherche-logement.db";

    /// <summary>URL par laquelle le navigateur atteint le site (liens renvoyés au plugin).</summary>
    public string PublicUrl { get; set; } = "http://localhost:5080";

    /// <summary>Token imposé par configuration. Vide : généré au premier démarrage et stocké en base.</summary>
    public string? ApiToken { get; set; }

    /// <summary>"memory" pour une base non persistante (démo, tests).</summary>
    public string Storage { get; set; } = "sqlite";
}

public static class JsonDefaults
{
    public static void Configure(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(o);
        return o;
    }
}
