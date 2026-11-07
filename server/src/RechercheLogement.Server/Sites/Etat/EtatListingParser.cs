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
        var tiles = Tiles(html);
        string? Tile(string key) => tiles.GetValueOrDefault(key);
        var land = Tile("existence d un terrain");
        var outdoor = Normalize(Tile("presence de balcons ou terrasses"));

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
            Rooms = Whole(Tile("nombre de pieces")),
            Bedrooms = Whole(Tile("nombre de chambres")),
            Bathrooms = Whole(Tile("nombre de salles de bain")),
            Floor = Floor(Tile("etage")),
            LandSurface = Number(Tile("superficie du terrain")) ?? (land is not null && land.Contains("m²", StringComparison.Ordinal) ? Number(land) : null),
            EnergyClass = EnergyClasses.Normalize(Group(EnergyLabel().Match(html))),
            GhgClass = EnergyClasses.Normalize(Group(GhgLabel().Match(html))),
            Heating = Tile("type de chauffage") ?? Tile("type de chauffage climatisation"),
            WorkNeeded = WorkNeeded(Tile("etat")),
            PropertyTax = Number(Tile("montant de la taxe fonciere")) is { } tax ? (decimal)tax : null,
            SaleKind = SaleKind(Tile("type de vente") ?? procedure),
            Features = Features.Of(
                (Features.Garden, Normalize(land).Contains("jardin") ? true : null),
                (Features.Pool, Presence(Tile("presence d une piscine"))),
                (Features.Cellar, Presence(Tile("presence d une cave"))),
                (Features.Elevator, Presence(Tile("presence d ascenseurs"))),
                (Features.Terrace, outdoor.Contains("terrasse") ? true : outdoor == "non" ? false : null),
                (Features.Balcony, outdoor.Contains("balcon") ? true : outdoor == "non" ? false : null),
                (Features.Garage, Presence(Tile("presence d un parking box"))),
                (Features.Parking, Tile("nombre de places de parking") is not null || Tile("type de parking") is not null ? true : null)),
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
            _ when l.StartsWith("maison", StringComparison.Ordinal) => PropertyTypes.House,
            _ when l.StartsWith("appartement", StringComparison.Ordinal) => PropertyTypes.Flat,
            _ when l.StartsWith("chateau", StringComparison.Ordinal) || l.StartsWith("hotels particuliers", StringComparison.Ordinal) => PropertyTypes.Castle,
            _ when l.StartsWith("foncier", StringComparison.Ordinal) || l.StartsWith("terrain", StringComparison.Ordinal) || l.StartsWith("vignes", StringComparison.Ordinal) => PropertyTypes.Terrain,
            _ when l.StartsWith("parking", StringComparison.Ordinal) => PropertyTypes.Parking,
            _ when l.StartsWith("immeuble", StringComparison.Ordinal) || l.StartsWith("ensembles immobiliers", StringComparison.Ordinal) => PropertyTypes.Building,
            _ when l.StartsWith("bureau", StringComparison.Ordinal) || l.StartsWith("boutique", StringComparison.Ordinal) || l.StartsWith("locaux commerciaux", StringComparison.Ordinal) || l.StartsWith("fonds de commerce", StringComparison.Ordinal) => PropertyTypes.Commercial,
            _ => PropertyTypes.Other,
        };
    }

    /// <summary>
    /// Tuiles "Caractéristiques détaillées du bien" : titre normalisé ("superficie du terrain") -> valeur. Leur liste
    /// dépend du type de bien (relevé sur 35 fiches en novembre 2026).
    /// </summary>
    public static Dictionary<string, string> Tiles(string html)
    {
        var tiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in TileRegex().Matches(html))
            if (Clean(m.Groups[2].Value) is { Length: > 0 } value) tiles.TryAdd(Normalize(Clean(m.Groups[1].Value)), value);
        return tiles;
    }

    /// <summary>"Non" -> false ; "2 caves", "oui", "Garage" -> true ; vide : inconnu.</summary>
    private static bool? Presence(string? value) => Normalize(value) switch
    {
        "" => null,
        "non" or "aucun" or "aucune" or "0" => false,
        _ => true,
    };

    /// <summary>"Travaux à prévoir", "A rénover" -> true ; "Très bon état d'entretien", "Correct" -> false.</summary>
    public static bool? WorkNeeded(string? state)
    {
        var s = Normalize(state);
        if (s.Length == 0) return null;
        if (s.Contains("travaux") || s.Contains("renov") || s.Contains("rehabilit") || s.Contains("mauvais") || s.Contains("ruine")) return true;
        if (s.Contains("bon") || s.Contains("correct") || s.Contains("neuf") || s.Contains("excellent")) return false;
        return null;
    }

    /// <summary>"Vente par enchères en ligne", "Adjudication" -> enchères ; "Appel d'offres" -> appel d'offres.</summary>
    public static string? SaleKind(string? procedure)
    {
        var s = Normalize(procedure);
        if (s.Contains("enchere") || s.Contains("adjudication")) return SaleKinds.Auction;
        if (s.Contains("appel d offre")) return SaleKinds.Tender;
        return null;
    }

    /// <summary>Premier nombre : "1283,00" -> 1283 ; "2 178 m²" -> 2178 ; "4411€" -> 4411.</summary>
    private static double? Number(string? text)
    {
        if (text is null) return null;
        var m = NumberText().Match(text.Replace("\u00a0", " ", StringComparison.Ordinal).Replace("\u202f", " ", StringComparison.Ordinal));
        return m.Success && double.TryParse(m.Value.Replace(" ", "", StringComparison.Ordinal).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>Nombre entier seul ("4") ; une phrase ("Immeuble de 6 étages") n'en est pas un.</summary>
    private static int? Whole(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>"1" -> 1 ; "Rez de chaussée" -> 0 ; plusieurs étages ("Rez de chaussée et 1er étage") : inconnu.</summary>
    private static int? Floor(string? text)
    {
        if (Whole(text) is { } n) return n;
        return Normalize(text) == "rez de chaussee" ? 0 : null;
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
    [GeneratedRegex("""<h3 class="fr-tile__title">(.*?)</h3>\s*<p class="fr-tile__desc">(.*?)</p>""", RegexOptions.Singleline)]
    private static partial Regex TileRegex();
    [GeneratedRegex("""Performance énergétique\s*:\s*([A-G])\b""")]
    private static partial Regex EnergyLabel();
    [GeneratedRegex("""Gaz à effet de serre\s*:\s*([A-G])\b""")]
    private static partial Regex GhgLabel();
    [GeneratedRegex(@"\d[\d ]*(?:[.,]\d+)?")]
    private static partial Regex NumberText();
    [GeneratedRegex("""\s+""")]
    private static partial Regex WebSpace();
}
