using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Sites.Paruvendu;

/// <summary>Annonce d'une page de liste. <paramref name="Department"/> : "65" (la liste n'a pas le code postal).</summary>
public sealed record ParuvenduCard(string Id, ListingData Data, string? Department);

/// <summary>Page de liste : annonces (sans les encarts sponsorisés) et nombre total d'annonces de la recherche.</summary>
public sealed record ParuvenduPage(List<ParuvenduCard> Cards, int? Total);

/// <summary>
/// Pages HTML de ParuVendu immobilier (relevé en novembre 2026) -> format commun. Pas de JSON de résultats : chaque
/// annonce est un bloc <c>div.blocAnnonce[data-id]</c> de la liste. Fonctions sans effet de bord : les tests utilisent
/// des extraits de pages réelles (Fixtures/paruvendu-*.html).
/// </summary>
public static partial class ParuvenduListingParser
{
    public const string Origin = "https://www.paruvendu.fr";

    /// <summary>
    /// Annonces d'une page de liste. Les encarts "Maison neuve" des constructeurs (code annonceur "MFC…", 2 par
    /// page) sont ignorés : ils ne font pas partie des résultats de la recherche (ni du total).
    /// </summary>
    public static ParuvenduPage ParseList(string html, TransactionType transaction)
    {
        var cards = new List<ParuvenduCard>();
        foreach (var block in Blocks(html))
        {
            var head = BlockHead().Match(block);
            if (!head.Success || head.Groups[2].Value.StartsWith("MFC", StringComparison.Ordinal)) continue;
            var link = Link().Match(block);
            if (!link.Success || link.Groups[1].Value.Contains("/maison-neuve/", StringComparison.Ordinal)) continue;
            if (ParseCard(head.Groups[1].Value, link.Groups[1].Value, block, transaction) is { } card) cards.Add(card);
        }
        var total = Group(OfferCount().Match(html)) is { } t && int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : (int?)null;
        return new ParuvenduPage(cards, total);
    }

    /// <summary>
    /// Blocs d'annonces : du début d'un bloc au suivant. Le dernier s'arrête à la mention des prix ou au pixel de
    /// statistiques qui suivent la liste (sinon il emporterait le reste de la page).
    /// </summary>
    private static IEnumerable<string> Blocks(string html)
    {
        var starts = BlockStart().Matches(html).Select(m => m.Index).ToList();
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1] : html.Length;
            var block = html[starts[i]..end];
            foreach (var marker in BlockEnds)
                if (block.IndexOf(marker, StringComparison.Ordinal) is var at and > 0) block = block[..at];
            yield return block;
        }
    }

    private static readonly string[] BlockEnds = ["<!-- Mention Prix -->", "statfo/invt/visitesAnnonces"];

    private static ParuvenduCard? ParseCard(string id, string href, string block, TransactionType transaction)
    {
        var url = href.StartsWith("http", StringComparison.Ordinal) ? href : Origin + href;
        var lines = HeadingLines(Group(Heading().Match(block)) ?? "");
        if (lines.Count == 0) return null;
        var typeLabel = lines[0];
        var place = lines.Select(l => CityLine().Match(l)).FirstOrDefault(m => m.Success);
        var badges = Badge().Matches(block).Select(m => Clean(m.Groups[1].Value)).ToList();
        var description = Group(Description().Match(block));
        var (type, subtype) = TypeOf(typeLabel);
        var data = new ListingData
        {
            Url = url,
            // "Maison 220 m² Odos (65)" ; la mention "proche de …" des recherches par commune est retirée.
            Title = string.Join(' ', lines.Where(l => !l.StartsWith("proche de", StringComparison.OrdinalIgnoreCase))),
            Transaction = url.Contains("/location/", StringComparison.Ordinal) ? TransactionType.Rent
                : url.Contains("/vente/", StringComparison.Ordinal) ? TransactionType.Buy : transaction,
            PropertyType = type,
            Subtype = subtype,
            Price = Number(Group(Price().Match(block)) is { } price ? Clean(price) : null) is { } p ? (decimal)p : null,
            Surface = lines.Select(l => SurfaceText().Match(l)).Where(m => m.Success).Select(m => Number(m.Groups[1].Value)).FirstOrDefault(),
            Rooms = Whole(Group(Rooms().Match(block))),
            Bedrooms = Whole(Group(Bedrooms().Match(block))),
            LandSurface = Number(Group(Land().Match(block))),
            City = place?.Groups[1].Value.Trim(),
            EnergyClass = EnergyClasses.Normalize(Group(Energy().Match(block))),
            AgencyName = Group(Agency().Match(block)) is { } agency ? Clean(agency) : null,
            Photos = Photos(block),
            DescriptionExcerpt = description is null ? null : JsonRead.Excerpt(WebUtility.HtmlDecode(description)),
            SaleKind = badges.Any(b => Normalize(b) == "viager") ? SaleKinds.LifeAnnuity : null,
            NewBuild = badges.Any(b => Normalize(b) == "maison neuve") ? true : null,
        };
        return new ParuvenduCard(id, data, place?.Groups[2].Value.ToUpperInvariant());
    }

    /// <summary>
    /// Lignes du titre de la carte : type ("Maison"), surface ("220 m²", parfois absente), "Odos (65)", et en
    /// recherche par commune "proche de …".
    /// </summary>
    private static List<string> HeadingLines(string span) =>
        WebUtility.HtmlDecode(TagRegex().Replace(span.Replace("m<sup>2</sup>", "m²", StringComparison.Ordinal), " "))
            .Split('\n').Select(l => WebSpace().Replace(l, " ").Trim()).Where(l => l.Length > 0).ToList();

    /// <summary>
    /// Type du site (libellé de la carte) -> type commun, et précision gardée telle que le site l'écrit ("Chalet").
    /// Libellés relevés dans le panneau des filtres (novembre 2026). Inconnu : type null (l'annonce n'est pas écartée).
    /// </summary>
    public static (string? Type, string? Subtype) TypeOf(string? label)
    {
        var l = Normalize(label);
        return l switch
        {
            "maison" => (PropertyTypes.House, null),
            "villa" or "chalet" or "moulin" or "ferme" => (PropertyTypes.House, label!.Trim()),
            "propriete chateau" or "hotel particulier" => (PropertyTypes.Castle, label!.Trim()),
            "appartement" => (PropertyTypes.Flat, null),
            "duplex triplex" => (PropertyTypes.Flat, label!.Trim()),
            "loft" => (PropertyTypes.Loft, null),
            "atelier" or "plateau" => (PropertyTypes.Loft, label!.Trim()),
            "terrain" => (PropertyTypes.Terrain, null),
            "immeuble" => (PropertyTypes.Building, null),
            "parking garage" => (PropertyTypes.Parking, null),
            "chambre" or "peniche" or "remise grange" => (PropertyTypes.Other, label!.Trim()),
            "autres" or "autre" => (PropertyTypes.Other, null),
            _ => (null, null),
        };
    }

    /// <summary>
    /// Code postal d'une fiche : le premier "Ville (65310)" dont la ville est celle de l'annonce (le titre de la page,
    /// ou chez un particulier l'encadré d'alerte). La fiche cite aussi des annonces similaires d'autres communes.
    /// </summary>
    public static string? PostalCodeOfDetail(string html, string city, string? department)
    {
        var wanted = Normalize(city);
        if (wanted.Length == 0) return null;
        foreach (Match m in CityWithPostalCode().Matches(html))
        {
            var cp = m.Groups[2].Value;
            if (department is not null && !ProjectScope.DepartmentOf(cp).Contains(department)) continue;
            var name = Normalize(WebUtility.HtmlDecode(m.Groups[1].Value));
            if (name == wanted || name.EndsWith(" " + wanted, StringComparison.Ordinal)) return cp;
        }
        return null;
    }

    /// <summary>Photos de la carte (6 au plus), en grand format (le site les sert en w=320 dans la liste).</summary>
    private static List<string>? Photos(string block)
    {
        var photos = PhotoUrl().Matches(block).Select(m => WebUtility.HtmlDecode(m.Value).Replace("w=320", "w=1000", StringComparison.Ordinal))
            .Distinct().Take(ListingData.MaxPhotos).ToList();
        return photos.Count > 0 ? photos : null;
    }

    /// <summary>Premier nombre : "242 000 €" -> 242000 ; "1 248" -> 1248 (espaces insécables des milliers).</summary>
    private static double? Number(string? text)
    {
        if (text is null) return null;
        var m = NumberText().Match(text.Replace(' ', ' ').Replace(' ', ' '));
        return m.Success && double.TryParse(m.Value.Replace(" ", "", StringComparison.Ordinal).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static int? Whole(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string Clean(string s) => WebSpace().Replace(WebUtility.HtmlDecode(TagRegex().Replace(s, " ")), " ").Trim();
    private static string Normalize(string? s) => Core.Dedup.DedupScorer.NormalizeText(s ?? "");
    private static string? Group(Match m) => m.Success ? m.Groups[1].Value : null;

    [GeneratedRegex("""class="blocAnnonce[\s"]""")]
    private static partial Regex BlockStart();
    [GeneratedRegex(@"^class=""blocAnnonce[^""]*""\s+data-id=""(\d+)""\s+data-cdp=""([^""]*)""")]
    private static partial Regex BlockHead();
    [GeneratedRegex(@"<a href=""(/immobilier/[^""]+|https://www\.paruvendu\.fr/immobilier/[^""]+)""")]
    private static partial Regex Link();
    [GeneratedRegex("""<h3[^>]*>\s*<a[^>]*>\s*<span>(.*?)</span>""", RegexOptions.Singleline)]
    private static partial Regex Heading();
    [GeneratedRegex("""^(.+?)\s*\((\d[\dAB]|\d{3})\)$""")]
    private static partial Regex CityLine();
    [GeneratedRegex("""^(\d[\d\s,.]*)\s*m²$""")]
    private static partial Regex SurfaceText();
    [GeneratedRegex("""<!-- Prix -->\s*<div>(.*?)</div>""", RegexOptions.Singleline)]
    private static partial Regex Price();
    /// <summary>"5 pièces", "3/4 pièces" (le minimum), "15 + pièces".</summary>
    [GeneratedRegex("""<li class="text-xs[^"]*">\s*(\d+)\s*(?:/\s*\d+\s*)?\+?\s*pièces?""")]
    private static partial Regex Rooms();
    [GeneratedRegex(""">\s*(\d+)\s+chambres?\s*<""")]
    private static partial Regex Bedrooms();
    [GeneratedRegex(""">\s*Terrain\s+(\d[\d\s  ]*)\s*m<sup>2</sup>""")]
    private static partial Regex Land();
    [GeneratedRegex("""NoteEnerg_([A-G])\b""")]
    private static partial Regex Energy();
    [GeneratedRegex("""<span class="text-xxs uppercase[^"]*">\s*([^<]+?)\s*</span>""")]
    private static partial Regex Badge();
    [GeneratedRegex("""<p class="text-sm text-justify line-clamp-5[^"]*">(.*?)</p>""", RegexOptions.Singleline)]
    private static partial Regex Description();
    [GeneratedRegex(@"title=""Toutes les annonces de ([^""]+)""")]
    private static partial Regex Agency();
    [GeneratedRegex("""https://img\.paruvendu\.fr/media_ext/[^'"\s<>]+""")]
    private static partial Regex PhotoUrl();
    [GeneratedRegex(@"""offerCount"":(\d+)")]
    private static partial Regex OfferCount();
    [GeneratedRegex("""([^<>()\n]{1,80}?)\s*\((\d{5})\)""")]
    private static partial Regex CityWithPostalCode();
    [GeneratedRegex(@"\d[\d ]*(?:[.,]\d+)?")]
    private static partial Regex NumberText();
    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();
    [GeneratedRegex("""\s+""")]
    private static partial Regex WebSpace();
}
