using System.Text.Json;
using System.Text.Json.Serialization;

namespace RechercheLogement.Server;

/// <summary>Section "RechercheLogement" de appsettings.json. Les variables d'environnement RechercheLogement__* peuvent remplacer ces valeurs.</summary>
public sealed class ServerSettings
{
    public const string Section = "RechercheLogement";

    /// <summary>Chemin du fichier SQLite, relatif au dossier de travail. Docker : /data/recherche-logement.db.</summary>
    public string DatabasePath { get; set; } = "data/recherche-logement.db";

    /// <summary>URL du serveur local dans le navigateur. Sert aux liens renvoyés au plugin.</summary>
    public string PublicUrl { get; set; } = "http://localhost:5080";

    /// <summary>Token fixé par la configuration. Si vide, le serveur génère un token au premier démarrage et le stocke en base.</summary>
    public string? ApiToken { get; set; }

    /// <summary>Lien d'installation du plugin. La CI publie ce script, et Tampermonkey le met à jour automatiquement.</summary>
    public string PluginInstallUrl { get; set; } =
        "https://github.com/nicolas-goyon/OutilRechercheLocation/releases/latest/download/recherche-logement.user.js";

    /// <summary>
    /// Base des projets ("Mes projets"), séparée de celle du catalogue. Vide : projets.db dans le dossier
    /// de <see cref="DatabasePath"/> (Docker : /data/projets.db).
    /// </summary>
    public string? ProjectsDatabasePath { get; set; }

    /// <summary>Lancement automatique des projets (intervalle choisi dans chaque projet). false : seulement à la demande.</summary>
    public bool ProjectsAutoRun { get; set; } = true;

    /// <summary>"memory" : base en mémoire, perdue à l'arrêt (démo, tests).</summary>
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
