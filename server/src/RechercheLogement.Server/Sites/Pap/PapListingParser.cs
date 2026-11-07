using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Text;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Sites.Pap;

/// <summary>Annonce de la liste PAP, avant lecture de sa fiche.</summary>
/// <param name="Path">"/annonces/maison-ibos-r464301065".</param>
public sealed record PapCard(string Id, string Path, ListingData Data);

/// <summary>
/// Pages HTML de PAP (www.pap.fr, relevé en novembre 2026) -> format commun. Pas de JSON pour les résultats :
/// la liste (div.search-list-item-alt) donne prix, ville, pièces, chambres, surface, classe DPE, photos ; la fiche
/// ajoute le code postal, le terrain, le GES, la date de publication, la référence et la description complète
/// (JSON-LD). Fonctions sans effet de bord, testées sur des extraits réels (Fixtures/pap-*.html).
/// </summary>
public static partial class PapListingParser
{
    public const string Origin = "https://www.pap.fr";

    /// <summary>
    /// Annonces de la page. Écartées : encarts (prix au m², programmes neufs d'autres sites) et annonces
    /// « à proximité » que PAP ajoute quand la zone en a peu (repérées par "proximite" dans leur data-gaq).
    /// </summary>
    public static List<PapCard> ParseList(string html)
    {
        var cards = new List<PapCard>();
        foreach (var chunk in html.Split("class=\"search-list-item-alt\"").Skip(1))
        {
            var link = ItemTitle().Match(chunk);
            if (!link.Success) continue;
            var desc = Description().Match(chunk);
            if (desc.Success && desc.Groups[1].Value.Contains("proximite", StringComparison.OrdinalIgnoreCase)) continue;
            var path = link.Groups[1].Value;
            var id = link.Groups[2].Value;
            var body = link.Groups[3].Value;
            var place = Clean(Group(PlaceSpan().Match(body)));
            var (city, cp) = SplitPlace(place);
            var tags = Tag().Matches(body).Select(m => Clean(m.Groups[1].Value)).OfType<string>().ToList();
            var photos = ListPhoto().Matches(chunk).Select(m => FullPhoto(m.Groups[1].Value)).Distinct().Take(ListingData.MaxPhotos).ToList();
            var data = new ListingData
            {
                Url = Origin + path,
                Title = TitleOf(path, tags),
                Transaction = TransactionType.Buy,
                PropertyType = TypeOfPath(path),
                Price = Price(Group(ItemPrice().Match(body))),
                Rooms = Count(tags, "pièce"),
                Bedrooms = Count(tags, "chambre"),
                Surface = Surface(tags.FirstOrDefault(t => t.EndsWith("m²", StringComparison.Ordinal) && !t.StartsWith("Terrain", StringComparison.Ordinal))),
                City = city,
                PostalCode = cp ?? Group(SlugPostalCode().Match(path)),
                EnergyClass = EnergyClasses.Normalize(Group(ListDpe().Match(chunk))),
                Photos = photos.Count > 0 ? photos : null,
                DescriptionExcerpt = desc.Success ? JsonRead.Excerpt(WebUtility.HtmlDecode(desc.Groups[2].Value)) : null,
            };
            cards.Add(new PapCard(id, path, data));
        }
        return cards;
    }

    /// <summary>Page suivante de la recherche ("Suivante"). Absente, ou pages « à proximité » : null.</summary>
    public static string? NextPage(string html) =>
        Group(NextLink().Match(html)) is { } next && !next.Contains("/proximite/", StringComparison.Ordinal) ? next : null;

    /// <summary>Fiche d'une annonce : complète les données de la liste.</summary>
    public static ListingData ParseDetail(string html, ListingData card)
    {
        var ld = JsonLd(html);
        var description = JsonRead.Str(ld?["description"]);
        var address = ld?["address"];
        var tags = DetailTag().Matches(html).Select(m => Clean(m.Groups[1].Value)).OfType<string>().ToList();
        var date = Group(Published().Match(html));
        var photos = JsonRead.Strings(ld?["image"]);
        var reference = Group(Reference().Match(html))?.Trim();
        var seller = JsonRead.Str(ld?["offers"]?["seller"]?["name"]);
        var (city, cp) = SplitPlace(Clean(Group(DetailPlace().Match(html))));
        return card with
        {
            PostalCode = JsonRead.Str(address?["postalCode"]) ?? cp ?? card.PostalCode,
            City = JsonRead.Str(address?["addressLocality"]) ?? city ?? card.City,
            Price = JsonRead.Dec(ld?["offers"]?["price"]) ?? card.Price,
            LandSurface = Surface(tags.FirstOrDefault(t => t.StartsWith("Terrain", StringComparison.Ordinal))),
            EnergyClass = EnergyClasses.Normalize(Group(EnergyActive().Match(html))) ?? card.EnergyClass,
            GhgClass = EnergyClasses.Normalize(Group(GhgActive().Match(html))),
            PublishedAt = date is null ? null : DateTime.TryParseExact(Clean(date), "dd MMMM yyyy", CultureInfo.GetCultureInfo("fr-FR"), DateTimeStyles.None, out var d)
                ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            AgencyRef = reference,
            AgencyName = seller is null or "Particulier" ? null : seller,
            Photos = photos.Count > 0 ? photos.Take(ListingData.MaxPhotos).ToList() : card.Photos,
            DescriptionExcerpt = JsonRead.Excerpt(description) ?? card.DescriptionExcerpt,
            OtherRefs = RefExtractor.FromText(description) is { Count: > 0 } refs ? refs : null,
            PropertyTax = Price(Group(PropertyTax().Match(description ?? ""))),
        };
    }

    /// <summary>Suggestions de lieux (/json/ac-geo?q=) : [{ id, name }] avec name "Arreau (65240)", "65240", "Hautes-Pyrénées - 65".</summary>
    public static List<(long Id, string Name)> ParsePlaces(JsonNode json) =>
        (json as JsonArray ?? new JsonArray())
            .Select(n => (Id: JsonRead.Dbl(n?["id"]), Name: JsonRead.Str(n?["name"])))
            .Where(x => x.Id is not null && x.Name is not null)
            .Select(x => ((long)x.Id!.Value, WebUtility.HtmlDecode(x.Name!)))
            .ToList();

    /// <summary>"/annonces/maison-ibos-r464301065" -> maison ; appartement, terrain, parking... sinon "autre".</summary>
    public static string TypeOfPath(string path)
    {
        var kind = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Split('-')[0];
        return kind switch
        {
            "maison" => PropertyTypes.House,
            "appartement" or "studio" => PropertyTypes.Flat,
            "terrain" => PropertyTypes.Terrain,
            "parking" or "garage" or "box" => PropertyTypes.Parking,
            "immeuble" => PropertyTypes.Building,
            "local" or "bureau" or "boutique" => PropertyTypes.Commercial,
            "loft" => PropertyTypes.Loft,
            _ => PropertyTypes.Other,
        };
    }

    /// <summary>Photo de la liste (petite, .webp) -> photo de la fiche (grande, .jpg) : "…/5-p2.webp" -> "…/5-p1.jpg".</summary>
    private static string FullPhoto(string url) => SmallPhoto().Replace(url, "-p1.jpg");

    private static string TitleOf(string path, List<string> tags)
    {
        var type = PropertyTypes.Label(TypeOfPath(path)).Split(',')[0];
        return string.Join(" ", new[] { type }.Concat(tags.Where(t => !t.Contains("chambre", StringComparison.Ordinal))));
    }

    /// <summary>"Poueyferré (65100) à 24km de Arreau" -> ("Poueyferré", "65100") ; "Ibos" -> ("Ibos", null).</summary>
    private static (string? City, string? PostalCode) SplitPlace(string? place)
    {
        if (string.IsNullOrWhiteSpace(place)) return (null, null);
        var m = CityAndPostal().Match(place);
        return m.Success ? (m.Groups[1].Value.Trim(), m.Groups[2].Value) : (place.Trim(), null);
    }

    private static int? Count(List<string> tags, string word) =>
        tags.FirstOrDefault(t => t.Contains(word, StringComparison.Ordinal)) is { } tag && int.TryParse(Group(Digits().Match(tag)), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>"155 m²", "Terrain 500 m²", "1.200 m²" -> nombre.</summary>
    private static double? Surface(string? text) =>
        text is null ? null : double.TryParse(Group(Number().Match(text.Replace(".", "", StringComparison.Ordinal)))?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>"239.000 €" -> 239000 (le point sépare les milliers).</summary>
    private static decimal? Price(string? text) =>
        text is null ? null : decimal.TryParse(new string(text.Where(char.IsAsciiDigit).ToArray()), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

    private static JsonNode? JsonLd(string html)
    {
        foreach (Match m in JsonLdScript().Matches(html))
        {
            try
            {
                if (JsonNode.Parse(m.Groups[1].Value) is { } node && JsonRead.Str(node["@type"]) == "Product") return node;
            }
            catch (System.Text.Json.JsonException)
            {
                // Bloc JSON-LD mal formé : on essaie le suivant.
            }
        }
        return null;
    }

    private static string? Clean(string? s) => s is null ? null : Spaces().Replace(WebUtility.HtmlDecode(Tags().Replace(s, " ")), " ").Trim();
    private static string? Group(Match m) => m.Success ? m.Groups[1].Value : null;

    [GeneratedRegex("""<a class="item-title" href="(/annonces/[^"]+)" name="(\d+)"[^>]*>(.*?)</a>""", RegexOptions.Singleline)]
    private static partial Regex ItemTitle();
    [GeneratedRegex("""<p class="item-description[^"]*" data-gaq="([^"]*)"[^>]*>(.*?)</p>""", RegexOptions.Singleline)]
    private static partial Regex Description();
    [GeneratedRegex("""<span class="h1">(.*?)</span>""", RegexOptions.Singleline)]
    private static partial Regex PlaceSpan();
    [GeneratedRegex("""<span class="item-price[^"]*">(.*?)</span>""", RegexOptions.Singleline)]
    private static partial Regex ItemPrice();
    [GeneratedRegex("""<li>(.*?)</li>""", RegexOptions.Singleline)]
    private static partial Regex Tag();
    [GeneratedRegex(@"<img src=""(https://cdn\.pap\.fr/photos/[^""]+)""")]
    private static partial Regex ListPhoto();
    [GeneratedRegex("""item-thumb-dpe-([a-g])\b""")]
    private static partial Regex ListDpe();
    [GeneratedRegex("""-p2\.webp$""")]
    private static partial Regex SmallPhoto();
    [GeneratedRegex("""-(\d{5})-r\d+$""")]
    private static partial Regex SlugPostalCode();
    [GeneratedRegex(@"id=""pagination-next"" href=""([^""]+)""")]
    private static partial Regex NextLink();
    [GeneratedRegex("""<li>\s*<strong>(.*?)</strong>\s*</li>""", RegexOptions.Singleline)]
    private static partial Regex DetailTag();
    [GeneratedRegex("""<div class="item-description[^"]*">\s*<h2[^>]*>(.*?)</h2>""", RegexOptions.Singleline)]
    private static partial Regex DetailPlace();
    [GeneratedRegex("""Publié le\s+(\d{1,2}\s+\p{L}+\s+\d{4})""")]
    private static partial Regex Published();
    [GeneratedRegex("""Réf\.\s*:\s*([^\s<]+)""")]
    private static partial Regex Reference();
    [GeneratedRegex("""class="energy-indice".*?<li class="active">\s*([A-G])\s*</li>""", RegexOptions.Singleline)]
    private static partial Regex EnergyActive();
    [GeneratedRegex("""class="ges-indice".*?<li class="active">\s*([A-G])\s*</li>""", RegexOptions.Singleline)]
    private static partial Regex GhgActive();
    [GeneratedRegex("""<script type="application/ld\+json">(.*?)</script>""", RegexOptions.Singleline)]
    private static partial Regex JsonLdScript();
    [GeneratedRegex("""[Tt]axe foncière\s*:?\s*([\d\s .]+)\s*€""")]
    private static partial Regex PropertyTax();
    [GeneratedRegex("""^(.*?)\s*\((\d{5})\)""")]
    private static partial Regex CityAndPostal();
    [GeneratedRegex("""(\d+)""")]
    private static partial Regex Digits();
    [GeneratedRegex("""(\d+(?:,\d+)?)""")]
    private static partial Regex Number();
    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
    [GeneratedRegex("""\s+""")]
    private static partial Regex Spaces();
}
