using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using RechercheLogement.Core.Dedup;

namespace RechercheLogement.Server.Sites.Common;

/// <summary>Lecture tolérante du JSON des sites (valeur absente, mauvais type, plage [min, max]).</summary>
public static partial class JsonRead
{
    public static decimal? Dec(JsonNode? n) => Value(n) is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : null;
    public static double? Dbl(JsonNode? n) => Value(n) is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
    public static int? Int(JsonNode? n) => Value(n) is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
    public static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    public static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    /// <summary>Chaînes d'un tableau JSON (les valeurs vides sont ignorées).</summary>
    public static List<string> Strings(JsonNode? n) =>
        (n as JsonArray)?.Select(x => x?.ToString()).Where(x => !string.IsNullOrEmpty(x)).Cast<string>().ToList() ?? [];

    /// <summary>Bien'ici renvoie parfois une plage ([min, max]) au lieu d'une valeur : on garde la première.</summary>
    private static JsonNode? Value(JsonNode? n) => n is JsonArray a ? (a.Count > 0 ? a[0] : null) : n;

    /// <summary>Extrait normalisé d'une description (sans HTML), 600 caractères au plus.</summary>
    public static string? Excerpt(string? text) =>
        text is null ? null : Truncate(DedupScorer.NormalizeText(Tags().Replace(text, " ")), 600);

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}
