using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RechercheLogement.Core.Dedup;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Text;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Sites.GreenAcres;

/// <summary>Carte de la liste : identifiant Green-Acres ("Apu1pbz5axanjk33") et données lues sur la carte.</summary>
public sealed record GreenAcresCard(string Id, ListingData Data);

/// <summary>
/// Page de résultats. <paramref name="Cards"/> : seulement les vrais résultats (pas les annonces « à proximité »).
/// <paramref name="Conditions"/> : recherche telle que le site l'a comprise (champ UrlConditions).
/// </summary>
public sealed record GreenAcresPage(int Total, string? Conditions, IReadOnlyList<GreenAcresCard> Cards);

/// <summary>
/// Pages HTML de Green-Acres (ASP.NET, rendu côté serveur, relevé en novembre 2026) -> format commun.
/// Liste : /maison-a-vendre?… (sans code postal, GPS ni DPE). Fiche : /fr/properties/immobilier/&lt;slug&gt;/&lt;id&gt;.htm.
/// Fonctions sans effet de bord : les tests utilisent des extraits de pages réelles (Fixtures/greenacres-*.html).
/// </summary>
public static partial class GreenAcresListingParser
{
    public const string Origin = "https://www.green-acres.fr";

    /// <summary>
    /// Cartes d'une page de résultats. Quand la recherche a peu de résultats, le site complète la page avec des
    /// annonces « à proximité » (d'autres communes, voire d'autres départements), placées après div.actual-criteria :
    /// elles sont ignorées, et la liste ne dépasse jamais le total annoncé.
    /// </summary>
    public static GreenAcresPage ParseList(string html)
    {
        var total = Group(TotalNumber().Match(html)) is { } t ? int.Parse(t, CultureInfo.InvariantCulture) : 0;
        var conditions = Group(UrlConditions().Match(html)) is { } c ? WebUtility.HtmlDecode(c) : null;
        var nearby = html.IndexOf("class=\"actual-criteria\"", StringComparison.Ordinal);
        var results = nearby >= 0 ? html[..nearby] : html;
        var cards = new List<GreenAcresCard>();
        var chunks = results.Split(CardStart);
        foreach (var raw in chunks.Skip(1))
        {
            // La dernière carte s'arrête avant la pagination et le bloc de questionnaire.
            var chunk = raw;
            foreach (var end in (string[])["class=\"announce-card hidden\"", "id=\"pagination-listing"])
                if (chunk.IndexOf(end, StringComparison.Ordinal) is var i and >= 0) chunk = chunk[..i];
            if (ParseCard(chunk) is { } card && cards.All(x => x.Id != card.Id)) cards.Add(card);
        }
        return new GreenAcresPage(total, conditions, cards.Take(Math.Max(total, 0)).ToList());
    }

    private const string CardStart = "<div class=\"announce-card   ";

    private static GreenAcresCard? ParseCard(string chunk)
    {
        var head = CardHead().Match(chunk);
        if (!head.Success) return null;
        var id = head.Groups[1].Value;
        var url = DecodeLink(head.Groups[2].Value);
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in InfoTag().Matches(chunk)) tags.TryAdd(Normalize(Clean(m.Groups[1].Value)), Clean(m.Groups[2].Value));
        var labels = TagLabel().Matches(chunk).Select(m => Clean(m.Groups[1].Value)).ToList();
        var title = Group(CardTitle().Match(chunk)) is { } ti ? WebUtility.HtmlDecode(ti).Trim() : null;
        var typeLabel = tags.GetValueOrDefault("type de bien");
        var place = Group(CardPlace().Match(chunk)) is { } p ? Clean(p) : null;
        var description = Group(CardDescription().Match(chunk)) is { } d ? WebUtility.HtmlDecode(d) : null;
        // Agence (company-parent), sinon le nom affiché seul (négociateur ou agence, selon l'annonce).
        var agency = new[] { Group(CompanyParent().Match(chunk)), Group(CompanyName().Match(chunk)) }
            .Select(a => a is null ? null : Clean(a)).FirstOrDefault(a => !string.IsNullOrEmpty(a));
        var newBuild = Normalize(typeLabel).Contains("programme neuf", StringComparison.Ordinal) || labels.Contains("Neuf") ? true : (bool?)null;

        var photos = ThumbSrc().Matches(chunk).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();
        if (Group(RemainingSlides().Match(chunk)) is { } remaining)
        {
            try
            {
                photos.AddRange(JsonRead.Strings(JsonNode.Parse(WebUtility.HtmlDecode(remaining))));
            }
            catch (System.Text.Json.JsonException)
            {
                // Liste illisible : les trois premières photos suffisent.
            }
        }

        var data = new ListingData
        {
            Url = url,
            Title = title,
            Transaction = TransactionType.Buy,
            PropertyType = TypeOf(typeLabel, title),
            Subtype = SubtypeOf(typeLabel),
            Price = Price(Group(CardPrice().Match(chunk))),
            Surface = SquareMeters(tags.GetValueOrDefault("surface habitable")),
            Rooms = Rooms(tags.GetValueOrDefault("pieces")),
            Bedrooms = Whole(tags.GetValueOrDefault("chambres")),
            LandSurface = Land(tags.GetValueOrDefault("terrain")),
            City = place is null ? null : CityOf(place),
            AgencyName = agency,
            Photos = Photos(photos, id),
            DescriptionExcerpt = JsonRead.Excerpt(description),
            NewBuild = newBuild,
            SaleKind = LifeAnnuity(title, typeLabel),
        };
        return new GreenAcresCard(id, data);
    }

    /// <summary>
    /// Fiche de l'annonce : code postal, GPS approximatif, DPE et GES, équipements, référence, toutes les photos.
    /// Ce que la fiche n'a pas vient de la carte.
    /// </summary>
    public static ListingData ParseDetail(string html, GreenAcresCard card)
    {
        var c = card.Data;
        var characteristics = Characteristics(html);
        // Les deux listes de la fiche se répètent en partie : comptes (salles d'eau) sur la première seulement.
        var main = characteristics.Select(x => Normalize(x.Text)).ToList();
        var texts = main.Concat(FeatureDescriptions(html).Select(Normalize)).ToList();
        var details = AgencyDetails(html);
        var reference = details.GetValueOrDefault("reference");
        var mandate = details.GetValueOrDefault("numero de mandat");
        var description = Group(DetailDescription().Match(html)) is { } d ? WebUtility.HtmlDecode(Br().Replace(d, " ")) : null;
        var otherRefs = RefExtractor.FromText(description)
            .Prepend(mandate).OfType<string>()
            .Where(r => !string.Equals(r, reference, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Icône et texte : l'icône "o-door" sert aussi à "Entrée séparée" ou "Cave".
        string? Characteristic(string icon, string word) =>
            characteristics.Where(x => x.Icon == icon && Normalize(x.Text).Contains(word, StringComparison.Ordinal)).Select(x => x.Text).FirstOrDefault();
        bool? Has(params string[] words) => texts.Any(t => words.Any(w => ContainsWord(t, w))) ? true : null;

        return c with
        {
            Url = Group(OgUrl().Match(html)) is { } u && u.StartsWith(Origin, StringComparison.Ordinal) ? u : c.Url,
            Price = Price(Group(DetailPrice().Match(html)) is { } price ? WebUtility.HtmlDecode(price) : null) ?? c.Price,
            PostalCode = Group(Zip().Match(html)) ?? Group(LocationPostalCode().Match(html)) ?? c.PostalCode,
            City = Group(Locality().Match(html)) is { } city ? WebUtility.HtmlDecode(city).Trim() : c.City,
            Geo = Geo(html) ?? c.Geo,
            Surface = SquareMeters(Characteristic("o-arrows-out", "surface")) ?? c.Surface,
            Rooms = Rooms(Characteristic("o-door", "piece") ?? Characteristic("o-door", "studio")) ?? c.Rooms,
            Bedrooms = Whole(Characteristic("o-bed", "chambre")) ?? c.Bedrooms,
            LandSurface = Land(Characteristic("o-tree", "terrain")) ?? c.LandSurface,
            Exposure = Characteristic("o-compass", "")?.ToLowerInvariant() ?? c.Exposure,
            Bathrooms = Bathrooms(main) ?? Bathrooms(texts) ?? c.Bathrooms,
            Heating = characteristics.Select(x => x.Text).FirstOrDefault(t => t.StartsWith("Chauffage", StringComparison.OrdinalIgnoreCase)) ?? c.Heating,
            YearBuilt = texts.Select(t => Group(YearBuilt().Match(t))).OfType<string>().Select(y => (int?)int.Parse(y, CultureInfo.InvariantCulture)).FirstOrDefault() ?? c.YearBuilt,
            WorkNeeded = WorkNeeded(texts) ?? c.WorkNeeded,
            EnergyClass = EnergyClasses.Normalize(Group(EnergyLabel().Match(html))) ?? c.EnergyClass,
            GhgClass = EnergyClasses.Normalize(Group(GhgLabel().Match(html))) ?? c.GhgClass,
            AgencyName = c.AgencyName ?? (Group(Agency().Match(html)) is { } a ? Clean(a) : null),
            AgencyRef = reference ?? c.AgencyRef,
            OtherRefs = otherRefs.Count > 0 ? otherRefs : c.OtherRefs,
            Photos = Photos(DetailPhoto().Matches(html).Select(m => m.Value), card.Id) ?? c.Photos,
            DescriptionExcerpt = JsonRead.Excerpt(description) ?? c.DescriptionExcerpt,
            PublishedAt = Group(SeenSince().Match(html)) is { } date && DateTime.TryParseExact(date, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                ? at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : c.PublishedAt,
            Features = Core.Model.Features.Of(
                (Core.Model.Features.Garden, Has("jardin")),
                (Core.Model.Features.Pool, Has("piscine")),
                (Core.Model.Features.Terrace, Has("terrasse", "terrasses")),
                (Core.Model.Features.Balcony, Has("balcon", "balcons")),
                (Core.Model.Features.Garage, Has("garage", "garages")),
                (Core.Model.Features.Parking, Has("parking", "parkings")),
                (Core.Model.Features.Cellar, Has("cave", "caves")),
                (Core.Model.Features.Fireplace, Has("cheminee", "cheminees")),
                (Core.Model.Features.Elevator, Has("ascenseur")),
                (Core.Model.Features.AirConditioning, Has("climatisation"))) ?? c.Features,
        };
    }

    /// <summary>
    /// "Type de bien" de la carte -> type commun. Les maisons de toutes sortes (villa, ferme, chalet, longère…) sont
    /// des maisons ; « Maison de ville » est une maison de ville. Programme neuf, ou libellé inconnu : le titre tranche,
    /// sinon le type reste inconnu (l'annonce n'est pas écartée d'un projet).
    /// </summary>
    public static string? TypeOf(string? label, string? title)
    {
        var l = Normalize(label);
        if (l.Length > 0 && !l.Contains("programme", StringComparison.Ordinal) && TypeOfText(l) is { } type) return type;
        return TypeOfText(Normalize(title));
    }

    private static string? TypeOfText(string t)
    {
        if (t.Length == 0) return null;
        if (t.Contains("viager", StringComparison.Ordinal) && !t.Contains("maison", StringComparison.Ordinal) && !t.Contains("appartement", StringComparison.Ordinal)) return null;
        if (t.Contains("maison de ville", StringComparison.Ordinal)) return PropertyTypes.TownHouse;
        if (t.Contains("chateau", StringComparison.Ordinal) || t.Contains("manoir", StringComparison.Ordinal) || t.Contains("hotel particulier", StringComparison.Ordinal)) return PropertyTypes.Castle;
        if (t.Contains("loft", StringComparison.Ordinal)) return PropertyTypes.Loft;
        if (t.Contains("appartement", StringComparison.Ordinal) || t.Contains("studio", StringComparison.Ordinal) || t.Contains("duplex", StringComparison.Ordinal)) return PropertyTypes.Flat;
        if (t.Contains("immeuble", StringComparison.Ordinal)) return PropertyTypes.Building;
        if (t.StartsWith("terrain", StringComparison.Ordinal)) return PropertyTypes.Terrain;
        if (t.Contains("commerce", StringComparison.Ordinal) || t.Contains("local", StringComparison.Ordinal) || t.Contains("bureau", StringComparison.Ordinal)) return PropertyTypes.Commercial;
        if (t.StartsWith("parking", StringComparison.Ordinal) || t.StartsWith("garage", StringComparison.Ordinal) || t.StartsWith("box", StringComparison.Ordinal)) return PropertyTypes.Parking;
        string[] houses = ["maison", "villa", "ferme", "chalet", "longere", "mas", "bastide", "moulin", "bungalow", "pavillon", "corps de ferme", "propriete", "gite"];
        return houses.Any(h => t == h || t.StartsWith(h + " ", StringComparison.Ordinal)) ? PropertyTypes.House : null;
    }

    /// <summary>Précision du type ("Chalet", "Maison de campagne") ; les libellés génériques n'en sont pas.</summary>
    public static string? SubtypeOf(string? label) => Normalize(label) switch
    {
        "" or "maison" or "appartement" or "terrain" or "immeuble" or "programme neuf" => null,
        _ => label!.Trim(),
    };

    /// <summary>"Trie-sur-Baïse (Hautes-Pyrénées)" -> "Trie-sur-Baïse".</summary>
    private static string CityOf(string place) => place.LastIndexOf(" (", StringComparison.Ordinal) is var i and > 0 ? place[..i].Trim() : place.Trim();

    /// <summary>Lien de la carte encodé en base64 (data-o). Autre site qu'un lien Green-Acres : ignoré.</summary>
    private static string? DecodeLink(string base64)
    {
        try
        {
            var url = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return url.StartsWith(Origin + "/", StringComparison.Ordinal) ? url : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Photos de l'annonce seulement (la fiche montre aussi des annonces similaires), en grand format.</summary>
    private static List<string>? Photos(IEnumerable<string> urls, string id)
    {
        var photos = urls
            .Where(u => u.Contains($"/{id}/", StringComparison.Ordinal) && u.StartsWith("https://", StringComparison.Ordinal))
            .Select(u => u.Replace("/miniPhotos/", "/Photos/", StringComparison.Ordinal))
            .Distinct()
            .Select(u => (Url: u, Index: Group(PhotoIndex().Match(u)) is { } n ? int.Parse(n, CultureInfo.InvariantCulture) : int.MaxValue))
            .OrderBy(p => p.Index)
            .Select(p => p.Url)
            .Take(ListingData.MaxPhotos).ToList();
        return photos.Count > 0 ? photos : null;
    }

    /// <summary>Caractéristiques de la fiche : icône ("o-bed") et texte ("3 chambres").</summary>
    private static List<(string Icon, string Text)> Characteristics(string html)
    {
        var start = html.IndexOf("class=\"main-characteristics all-characteristics\"", StringComparison.Ordinal);
        if (start < 0) return [];
        var end = html.IndexOf("</ul>", start, StringComparison.Ordinal);
        var block = end > start ? html[start..end] : html[start..];
        return CharacteristicItem().Matches(block).Select(m => (m.Groups[1].Value, Clean(m.Groups[2].Value))).Where(x => x.Item2.Length > 0).ToList();
    }

    /// <summary>Seconde liste de la fiche ("1 parking (box)", "Orientation: Sud, Est").</summary>
    private static IEnumerable<string> FeatureDescriptions(string html) =>
        Group(FeatureDesc().Match(html)) is { } block ? ListItem().Matches(block).Select(m => Clean(m.Groups[1].Value)) : [];

    /// <summary>Encadré de l'agence : libellé normalisé ("reference", "numero de mandat") -> valeur. Chaque li à part.</summary>
    private static Dictionary<string, string> AgencyDetails(string html)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Group(AgencyDetailsBlock().Match(html)) is not { } block) return details;
        foreach (var li in block.Split("<li").Skip(1))
            if (LabelValue().Match(li) is { Success: true } m && Clean(m.Groups[2].Value) is { Length: > 0 } value)
                details.TryAdd(Normalize(Clean(m.Groups[1].Value)), value);
        return details;
    }

    /// <summary>window.advert = {"coordinates":{"latitude":…,"longitude":…,"precise":false}, …} : position souvent approximative.</summary>
    private static GeoPoint? Geo(string html)
    {
        if (Group(AdvertScript().Match(html)) is not { } json) return null;
        try
        {
            var coords = JsonNode.Parse(json)?["coordinates"];
            var lat = JsonRead.Dbl(coords?["latitude"]);
            var lon = JsonRead.Dbl(coords?["longitude"]);
            if (lat is null || lon is null) return null;
            // Position non précise : flou de l'ordre du kilomètre (relevé : quelques centaines de mètres du centre).
            return new GeoPoint(lat.Value, lon.Value, JsonRead.Bool(coords?["precise"]) == true ? null : 1000);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>"1 salle d'eau", "2 salles de bain" : total des salles de bain et d'eau.</summary>
    private static int? Bathrooms(IEnumerable<string> texts)
    {
        var counts = texts.Select(t => BathroomText().Match(t)).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        return counts.Count > 0 ? counts.Sum() : null;
    }

    private static bool? WorkNeeded(IEnumerable<string> texts)
    {
        var all = string.Join(" | ", texts);
        if (all.Contains("a renover", StringComparison.Ordinal) || all.Contains("travaux", StringComparison.Ordinal) || all.Contains("a restaurer", StringComparison.Ordinal)) return true;
        if (all.Contains("bon etat", StringComparison.Ordinal) || all.Contains("excellent etat", StringComparison.Ordinal) || all.Contains("refait a neuf", StringComparison.Ordinal)) return false;
        return null;
    }

    private static string? LifeAnnuity(string? title, string? label) =>
        Normalize(title).Contains("viager", StringComparison.Ordinal) || Normalize(label).Contains("viager", StringComparison.Ordinal) ? SaleKinds.LifeAnnuity : null;

    /// <summary>"88 000 €" -> 88000. Autre devise (dollars si le pays du visiteur n'est pas fixé) : inconnu.</summary>
    private static decimal? Price(string? html)
    {
        if (html is null) return null;
        var text = Clean(html);
        if (!text.Contains('€', StringComparison.Ordinal)) return null;
        return Number(text) is { } n ? (decimal)n : null;
    }

    /// <summary>"85 m²", "181 m² de surface habitable" -> m². Pieds carrés (sq ft) : inconnu.</summary>
    private static double? SquareMeters(string? text) => text is not null && text.Contains("m²", StringComparison.Ordinal) ? Number(text) : null;

    /// <summary>"1 503 m² de terrain" -> 1503 ; "1,7 hectares de terrain" -> 17000.</summary>
    private static double? Land(string? text)
    {
        if (text is null) return null;
        if (Normalize(text).Contains("hectare", StringComparison.Ordinal)) return Number(text) is { } ha ? Math.Round(ha * 10_000) : null;
        return SquareMeters(text);
    }

    /// <summary>"4 pièces" -> 4 ; "Studio" -> 1 ; "3 à 4 pièces" (programme neuf) -> 3.</summary>
    private static int? Rooms(string? text) => Normalize(text) == "studio" ? 1 : Whole(text);

    private static int? Whole(string? text) => Number(text) is { } n ? (int)n : null;

    /// <summary>Premier nombre du texte : "1 503 m²" -> 1503 ; "1,7 hectares" -> 1.7.</summary>
    private static double? Number(string? text)
    {
        if (text is null) return null;
        var m = NumberText().Match(text.Replace(' ', ' ').Replace(' ', ' '));
        return m.Success && double.TryParse(m.Value.Replace(" ", "", StringComparison.Ordinal).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static bool ContainsWord(string normalized, string word) =>
        (" " + normalized + " ").Contains(" " + word + " ", StringComparison.Ordinal);

    private static string Clean(string s) => WebSpace().Replace(WebUtility.HtmlDecode(TagRegex().Replace(s, " ")).Replace(' ', ' '), " ").Trim()
        .Replace("⁦", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);
    private static string Normalize(string? s) => DedupScorer.NormalizeText(s ?? "");
    private static string? Group(Match m) => m.Success ? m.Groups[1].Value : null;

    [GeneratedRegex("""id="listing-total-number" value="(\d+)"[^>]*>""")]
    private static partial Regex TotalNumber();
    [GeneratedRegex("""name="UrlConditions" type="hidden" value="([^"]*)"[^>]*>""")]
    private static partial Regex UrlConditions();
    [GeneratedRegex("""data-advertid="(\w+)" data-o="([A-Za-z0-9+/=]+)"[^>]*>""")]
    private static partial Regex CardHead();
    [GeneratedRegex("""class="announce-info" title="([^"]*)"[^>]*>""")]
    private static partial Regex CardTitle();
    [GeneratedRegex("""class="info-price"[^>]*>(.*?)</strong>""", RegexOptions.Singleline)]
    private static partial Regex CardPrice();
    [GeneratedRegex("""class="announce-localisation">(.*?)</div>""", RegexOptions.Singleline)]
    private static partial Regex CardPlace();
    [GeneratedRegex("""class="info-tag[^"]*" title="([^"]*)">(.*?)</div>""", RegexOptions.Singleline)]
    private static partial Regex InfoTag();
    [GeneratedRegex("""<span class="tag__label">(.*?)</span>""", RegexOptions.Singleline)]
    private static partial Regex TagLabel();
    [GeneratedRegex("""data-thumb-src="([^"]+)"[^>]*>""")]
    private static partial Regex ThumbSrc();
    [GeneratedRegex("""data-remaining-slides="([^"]*)"[^>]*""")]
    private static partial Regex RemainingSlides();
    [GeneratedRegex("""class="description-details">(.*?)</div>""", RegexOptions.Singleline)]
    private static partial Regex CardDescription();
    [GeneratedRegex("""class="company-parent">(.*?)</div>""", RegexOptions.Singleline)]
    private static partial Regex CompanyParent();
    [GeneratedRegex("""class="company-name">(.*?)</div>""", RegexOptions.Singleline)]
    private static partial Regex CompanyName();

    [GeneratedRegex("""<meta property="og:url" content="([^"]*)"[^>]*>""")]
    private static partial Regex OgUrl();
    [GeneratedRegex("""data-probe-format="advert_price">(.*?)</div>""", RegexOptions.Singleline)]
    private static partial Regex DetailPrice();
    [GeneratedRegex("""item_zip: '(\d{5})'""")]
    private static partial Regex Zip();
    [GeneratedRegex("""id="location-map-link"[^>]*>[^<]*\((\d{5})\)</a>""")]
    private static partial Regex LocationPostalCode();
    [GeneratedRegex("""<meta itemprop="addressLocality" content="([^"]*)"[^>]*>""")]
    private static partial Regex Locality();
    [GeneratedRegex("""window\.advert = (\{[^\n]*\});""")]
    private static partial Regex AdvertScript();
    [GeneratedRegex("""#(o-[a-z0-9-]+)"></use></svg>\s*</div>\s*<div class="description">\s*<p>(.*?)</p>""", RegexOptions.Singleline)]
    private static partial Regex CharacteristicItem();
    [GeneratedRegex("""<ul class="main-feature-desc">(.*?)</ul>""", RegexOptions.Singleline)]
    private static partial Regex FeatureDesc();
    [GeneratedRegex("""<li>(.*?)</li>""", RegexOptions.Singleline)]
    private static partial Regex ListItem();
    [GeneratedRegex("""<ul class="agency-details">(.*?)</ul>""", RegexOptions.Singleline)]
    private static partial Regex AgencyDetailsBlock();
    [GeneratedRegex("""<label>(.*?)</label>\s*<p>(.*?)</p>""", RegexOptions.Singleline)]
    private static partial Regex LabelValue();
    [GeneratedRegex("""class="agency-info__agency"[^>]*>(.*?)</a>""", RegexOptions.Singleline)]
    private static partial Regex Agency();
    [GeneratedRegex("""<div class="description-text" dir="auto">(.*?)</div>""", RegexOptions.Singleline)]
    private static partial Regex DetailDescription();
    [GeneratedRegex(@"\(DPE\), ([A-G])""")]
    private static partial Regex EnergyLabel();
    [GeneratedRegex(@"\(GES\), ([A-G])""")]
    private static partial Regex GhgLabel();
    [GeneratedRegex("""https://lb1\.green-acres\.com/[^"'\s?]+/Photos/[^"'\s?]+\.jpg(?:\?\d+)?""")]
    private static partial Regex DetailPhoto();
    [GeneratedRegex("""_(\d+)\.jpg""")]
    private static partial Regex PhotoIndex();
    [GeneratedRegex("""Vu \d+ fois depuis le (\d{2}/\d{2}/\d{4})""")]
    private static partial Regex SeenSince();
    [GeneratedRegex("""^(\d+) salles? (?:d eau|de bains?)\b""")]
    private static partial Regex BathroomText();
    [GeneratedRegex("""(?:construit|construction|annee)\D{0,15}\b(1[6-9]\d\d|20\d\d)\b""")]
    private static partial Regex YearBuilt();
    [GeneratedRegex("""<br\s*/?>""")]
    private static partial Regex Br();
    [GeneratedRegex(@"\d[\d ]*(?:[.,]\d+)?")]
    private static partial Regex NumberText();
    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();
    [GeneratedRegex("""\s+""")]
    private static partial Regex WebSpace();
}
