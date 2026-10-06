using System.Text.RegularExpressions;
using RechercheLogement.Core.Dedup;

namespace RechercheLogement.Core.Text;

/// <summary>
/// Références d'annonce et SIREN d'agence écrits en clair. Mêmes règles que le plugin
/// (plugin/src/shared/text.ts : extractRefs, extractSiren).
/// </summary>
public static partial class RefExtractor
{
    /// <summary>
    /// "Réf. : LA2100-REGOURD12", "Référence annonce : 1653L1653", "Mandat n° 2024-118".
    /// Jetons de 3 caractères ou plus contenant un chiffre ; années seules ignorées. Au plus 4, sans doublon.
    /// </summary>
    public static List<string> FromText(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(text)) return list;
        var plain = Tags().Replace(text, " ").Replace(' ', ' ').Replace(' ', ' ');
        foreach (var re in new[] { RefPattern(), MandatePattern() })
        {
            foreach (Match m in re.Matches(plain))
            {
                var token = m.Groups[1].Value.ToUpperInvariant();
                if (token.Length < 3 || !token.Any(char.IsAsciiDigit) || Year().IsMatch(token)) continue;
                var norm = DedupScorer.NormalizeRef(token);
                if (norm is null || list.Exists(r => DedupScorer.NormalizeRef(r) == norm)) continue;
                list.Add(token);
                if (list.Count >= 4) return list;
            }
        }
        return list;
    }

    /// <summary>SIREN (9 chiffres, clé de Luhn) dans des mentions légales : "RCS Rodez 407 797 521", "SIRET : 325 539 286 00105".</summary>
    public static string? SirenFromText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var plain = Tags().Replace(text, " ");
        foreach (Match m in Siret().Matches(plain))
        {
            var d = Digits(m.Groups[1].Value);
            if (d.Length == 14 && Luhn(d[..9])) return d[..9];
        }
        foreach (Match m in Rcs().Matches(plain))
        {
            var d = Digits(m.Groups[1].Value);
            if (d.Length == 9 && Luhn(d)) return d;
        }
        return null;
    }

    /// <summary>"407 797 521" ou SIRET -> SIREN validé, sinon null.</summary>
    public static string? NormalizeSiren(string? value)
    {
        var d = Digits(value ?? "");
        if (d.Length == 9 && Luhn(d)) return d;
        if (d.Length == 14 && Luhn(d[..9])) return d[..9];
        return null;
    }

    private static string Digits(string s) => new(s.Where(char.IsAsciiDigit).ToArray());

    private static bool Luhn(string digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
        }
        return sum % 10 == 0;
    }

    private const string Token = @"([A-Z0-9](?:[A-Z0-9_./-]{1,28}[A-Z0-9])?)";

    [GeneratedRegex(@"\br[ée]f(?:[ée]rences?)?\b\.?\s*(?:de\s+l['’]\s*annonce|d['’]annonce|annonce|du\s+bien|bien|agence|interne|mandat|dossier)?\s*(?:n[°ºo]\.?)?\s*[:#]?\s*" + Token, RegexOptions.IgnoreCase)]
    private static partial Regex RefPattern();

    [GeneratedRegex(@"\bmandat\s*(?:n[°ºo]\.?|num[ée]ro)?\s*[:#]?\s*" + Token, RegexOptions.IgnoreCase)]
    private static partial Regex MandatePattern();

    [GeneratedRegex(@"^(19|20)\d{2}$")]
    private static partial Regex Year();

    [GeneratedRegex(@"\bSIRET\b\D{0,15}((?:\d[\s.]?){13}\d)", RegexOptions.IgnoreCase)]
    private static partial Regex Siret();

    [GeneratedRegex(@"\b(?:RCS|SIREN)\b[^0-9]{0,40}?((?:\d[\s.]?){8}\d)(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex Rcs();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}
