using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using RechercheLogement.Core.Model;

namespace RechercheLogement.Server.Sites.Etat;

/// <summary>Carte de la liste des biens.</summary>
/// <param name="Path">"/biens/…" : identifiant de l'annonce.</param>
public sealed record EtatCard(string Path, string Title, string? City, string? Department, string? TypeLabel, double? Surface, string? Reference);

/// <summary>Fiche d'un bien. <paramref name="Insee"/> : code de la commune, pour trouver son code postal.</summary>
public sealed record EtatDetail(ListingData Data, string? Insee);

/// <summary>
/// Pages HTML du site des cessions immobilières de l'État (Drupal, relevé en novembre 2026) -> format commun.
/// Fonctions sans effet de bord : les tests utilisent des extraits de pages (Fixtures/etat-*.html).
/// </summary>
public static partial class EtatListingParser
{
    public const string Origin = "https://cessions.immobilier-etat.gouv.fr";
    public const string AgencyName = "Cessions immobilières de l'État";

    /// <summary>Cartes de /recherche-biens?page=N.</summary>
    public static List<EtatCard> ParseList(string html)
    {
        var cards = new List<EtatCard>();
        foreach (var chunk in html.Split("class=\"fr-card__content\"").Skip(1))
        {
            var link = CardLink().Match(chunk);
            if (!link.Success) continue;
            var tags = CardTag().Matches(chunk).Select(m => Clean(m.Groups[1].Value)).ToList();
            var place = CardPlace().Match(chunk);
            cards.Add(new EtatCard(
                link.Groups[1].Value,
                Clean(link.Groups[2].Value),
                place.Success ? Clean(place.Groups[1].Value) : null,
                place.Success ? place.Groups[2].Value.ToUpperInvariant() : null,
                tags.FirstOrDefault(t => !SurfaceText().IsMatch(t)),
                tags.Select(ParseSurface).FirstOrDefault(s => s is not null),
                Group(CardReference().Match(chunk))));
        }
        return cards;
    }

    /// <summary>Fiche /biens/… (les données de la carte complètent ce que la fiche n'a pas).</summary>
    public static EtatDetail ParseDetail(string html, EtatCard card)
    {
        var title = Group(DetailTitle().Match(html)) is { } t ? Clean(t) : card.Title;
        var tags = DetailTag().Matches(html).Select(m => Clean(m.Groups[1].Value)).ToList();
        var typeLabel = tags.FirstOrDefault(x => !SurfaceText().IsMatch(x)) ?? card.TypeLabel;
        var map = MapData().Match(html);
        var address = map.Success ? WebUtility.HtmlDecode(map.Groups[3].Value) : null;
        var insee = address is null ? null : Group(TrailingInsee().Match(address));
        var procedure = Group(Procedure().Match(html)) is { } p ? Clean(p) : null;
        var ends = Group(EndsIn().Match(html)) is { } e ? Clean(e) : null;
        var description = Group(OgDescription().Match(html)) is { } d ? WebUtility.HtmlDecode(d).Trim() : null;
        var notes = new[] { ProcedureNote(procedure), ends }.OfType<string>().ToList();

        var data = new ListingData
        {
            Url = Origin + card.Path,
            Title = title,
            Transaction = TransactionType.Buy,
            PropertyType = TypeOf(typeLabel, title),
            Surface = tags.Select(ParseSurface).FirstOrDefault(s => s is not null) ?? card.Surface,
            City = Group(DetailCity().Match(html)) is { } c ? Clean(c) : card.City,
            Geo = map.Success ? new GeoPoint(Dbl(map.Groups[2].Value), Dbl(map.Groups[1].Value)) : null,
            AgencyRef = Group(DetailReference().Match(html))?.Trim() ?? card.Reference,
            AgencyName = AgencyName,
            Photos = Photos(html),
            DescriptionExcerpt = notes.Count == 0 ? description : $"{string.Join(". ", notes)}. {description}".Trim(),
            PublishedAt = Group(Published().Match(html)) is { } date && DateTime.TryParseExact(date, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                ? at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
        };
        return new EtatDetail(data, insee);
    }

    /// <summary>
    /// Type du site -> type commun. "Logements" ne dit pas maison ou appartement : le titre tranche, sinon le type reste
    /// inconnu (l'annonce n'est pas écartée d'un projet).
    /// </summary>
    public static string? TypeOf(string? label, string? title)
    {
        var l = Normalize(label);
        var t = Normalize(title);
        if (l.Contains("logement") || l.Length == 0)
        {
            if (t.Contains("chateau") || t.Contains("manoir") || t.Contains("hotel particulier")) return PropertyTypes.Castle;
            if (t.Contains("maison") || t.Contains("villa") || t.Contains("pavillon") || t.Contains("ferme")) return PropertyTypes.House;
            if (t.Contains("appartement") || t.Contains("studio")) return PropertyTypes.Flat;
            if (t.Contains("immeuble")) return PropertyTypes.Building;
            if (t.Contains("box") || t.Contains("parking") || t.Contains("garage")) return PropertyTypes.Parking;
            return null;
        }
        return l switch
        {
            _ when l.StartsWith("maison") => PropertyTypes.House,
            _ when l.StartsWith("appartement") => PropertyTypes.Flat,
            _ when l.StartsWith("chateau") || l.StartsWith("hotels particuliers") => PropertyTypes.Castle,
            _ when l.StartsWith("foncier") || l.StartsWith("terrain") || l.StartsWith("vignes") => PropertyTypes.Terrain,
            _ when l.StartsWith("parking") => PropertyTypes.Parking,
            _ when l.StartsWith("immeuble") || l.StartsWith("ensembles immobiliers") => PropertyTypes.Building,
            _ when l.StartsWith("bureau") || l.StartsWith("boutique") || l.StartsWith("locaux commerciaux") || l.StartsWith("fonds de commerce") => PropertyTypes.Commercial,
            _ => PropertyTypes.Other,
        };
    }

    private static List<string>? Photos(string html)
    {
        var photos = PhotoSrc().Matches(html).Select(m => Origin + m.Groups[1].Value).Distinct().Take(ListingData.MaxPhotos).ToList();
        return photos.Count > 0 ? photos : null;
    }

    private static double? ParseSurface(string text)
    {
        var m = SurfaceText().Match(text);
        return m.Success && double.TryParse(m.Groups[1].Value.Replace(" ", "").Replace(" ", "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string Clean(string s) => WebSpace().Replace(WebUtility.HtmlDecode(TagRegex().Replace(s, " ")), " ").Trim();
    /// <summary>"Procédure : Appel d'offres", ou "Procédure à venir" (le site ajoute alors un conseil, inutile ici).</summary>
    private static string? ProcedureNote(string? procedure) => procedure switch
    {
        null or "" => null,
        _ when procedure.StartsWith("Procédure à venir", StringComparison.OrdinalIgnoreCase) => "Procédure à venir",
        _ => $"Procédure : {procedure}",
    };
    private static string Normalize(string? s) => Core.Dedup.DedupScorer.NormalizeText(s ?? "");
    private static string? Group(Match m) => m.Success ? m.Groups[1].Value : null;
    private static double Dbl(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    [GeneratedRegex("""<a href="(/biens/[^"?#]+)"[^>]*class="fr-card__link"[^>]*>(.*?)</a>""", RegexOptions.Singleline)]
    private static partial Regex CardLink();
    [GeneratedRegex("""<button[^>]*class="fr-tag"[^>]*>(.*?)</button>""", RegexOptions.Singleline)]
    private static partial Regex CardTag();
    [GeneratedRegex("""</i>\s*([^<]+?)\s+-\s+(\w{2,3})\s*</div>""")]
    private static partial Regex CardPlace();
    [GeneratedRegex("""Référence\s*:\s*([\w-]+)""")]
    private static partial Regex CardReference();

    [GeneratedRegex("""<div class="property-title">\s*<h1>(.*?)</h1>""", RegexOptions.Singleline)]
    private static partial Regex DetailTitle();
    [GeneratedRegex("""<span class="location-text">(.*?)</span>""", RegexOptions.Singleline)]
    private static partial Regex DetailCity();
    [GeneratedRegex("""<p class="fr-tag">(.*?)</p>""", RegexOptions.Singleline)]
    private static partial Regex DetailTag();
    [GeneratedRegex("""class="reference-text">\s*Référence\s*:\s*<span[^>]*>\s*([^<]+?)\s*</span>""")]
    private static partial Regex DetailReference();
    [GeneratedRegex(@"data-lon=""(-?[\d.]+)""\s+data-lat=""(-?[\d.]+)""\s+data-popup-text=""([^""]*)""")]
    private static partial Regex MapData();
    [GeneratedRegex(""",\s*(\w{5})\s*$""")]
    private static partial Regex TrailingInsee();
    [GeneratedRegex("""Procédure de vente</h5>\s*<p>\s*(?:par\s+)?(.*?)</p>""", RegexOptions.Singleline)]
    private static partial Regex Procedure();
    [GeneratedRegex("""<span class="fr-badge[^"]*">\s*(Fini[^<]*|Termin[^<]*)</span>""", RegexOptions.IgnoreCase)]
    private static partial Regex EndsIn();
    [GeneratedRegex(@"<meta property=""og:description"" content=""([^""]*)""")]
    private static partial Regex OgDescription();
    [GeneratedRegex("""Publié le :</p>\s*<p[^>]*>\s*(\d{2}/\d{2}/\d{4})""")]
    private static partial Regex Published();
    [GeneratedRegex("""<img[^>]+src="(/sites/default/files/[^"?]+\.(?:jpe?g|png|webp))""", RegexOptions.IgnoreCase)]
    private static partial Regex PhotoSrc();
    [GeneratedRegex("""^([\d\s ,.]+)\s*m²$""")]
    private static partial Regex SurfaceText();
    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();
    [GeneratedRegex("""\s+""")]
    private static partial Regex WebSpace();
}
