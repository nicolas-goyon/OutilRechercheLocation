using System.Globalization;
using RechercheLogement.Core.Dedup;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Services;

namespace RechercheLogement.Core.Recommend;

/// <summary>Un bien recommandé : score entre 0 et 1, et ce qui l'explique.</summary>
public sealed record Recommendation(PropertySummary Item, double Score, IReadOnlyList<string> Reasons);

/// <summary>Ce que le moteur a appris : biens notés, mode utilisé.</summary>
public sealed record RecommenderState(int Liked, int Disliked, bool Trained, int WithPhotos, int Total)
{
    /// <summary>Assez de biens notés pour recommander (au moins un bien aimé).</summary>
    public bool Ready => Liked > 0;
}

/// <summary>
/// Recommandations apprises sur les statuts : "Vue" et "À contacter" = intéresse (À contacter compte double),
/// "Pas intéressé" = n'intéresse pas. Tout est calculé ici, sans service extérieur :
/// <list type="bullet">
/// <item>chaque bien devient un vecteur : critères (prix, surface, terrain, prix au m², DPE...) centrés-réduits,
/// catégories (type, commune, département, équipements), mots des titres et descriptions (TF-IDF) et descripteurs
/// de ses photos (<see cref="PhotoStats"/>) ;</item>
/// <item>une régression logistique (descente de gradient, régularisation L2) apprend le poids de chaque élément,
/// dès 3 biens aimés et 3 écartés ;</item>
/// <item>la similarité (cosinus) avec les biens notés complète le score, et suffit seule tant qu'il y a peu de
/// biens écartés.</item>
/// </list>
/// Les raisons affichées sont les éléments qui ont le plus fait monter le score, et le bien aimé le plus proche.
/// </summary>
public sealed class Recommender
{
    private const int MinForModel = 3;
    private const int Neighbours = 5;

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Mots trop courants pour dire quelque chose d'un bien.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "avec", "dans", "pour", "plus", "sans", "sous", "vous", "nous", "votre", "notre", "cette", "elle", "sont", "etre",
        "tres", "tout", "tous", "toute", "toutes", "aussi", "ainsi", "comme", "entre", "dont", "mais", "leur", "leurs",
        "avoir", "fait", "faire", "peut", "possible", "bien", "maison", "appartement", "vente", "vendre", "louer",
        "location", "prix", "piece", "pieces", "agence", "immobilier", "immobiliere", "contact", "contactez", "visite",
        "honoraires", "charge", "acquereur", "vendeur", "euros", "environ", "situe", "situee", "proche", "proximite",
        "compose", "composee", "comprenant", "offre", "propose", "decouvrir", "venez", "annonce", "reference", "mandat",
        "conseiller", "conseillere", "independant", "information", "informations", "disponible", "georisques",
        "renseignements", "site", "pres", "niveau", "partie", "autre", "autres", "chez", "deux", "trois", "quatre",
    };

    private readonly Dictionary<string, Dictionary<string, double>> _vectors = [];
    private readonly Dictionary<string, PropertySummary> _items = [];
    private readonly Dictionary<string, double> _weights = [];
    private readonly Dictionary<string, string> _cityNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (double Mean, double Sd)> _scales = [];
    private double _bias;
    private List<(string Id, bool Liked, double Weight)> _labeled = [];

    public RecommenderState State { get; private set; } = new(0, 0, false, 0, 0);

    /// <summary>Apprend sur les biens notés de <paramref name="all"/>. <paramref name="photos"/> : descripteurs moyens par bien.</summary>
    public static Recommender Train(IReadOnlyList<PropertySummary> all, IReadOnlyDictionary<string, PhotoStats> photos)
    {
        var r = new Recommender();
        r.Build(all, photos);
        return r;
    }

    /// <summary>Score des biens <paramref name="candidates"/> (non notés), du plus recommandé au moins recommandé.</summary>
    public List<Recommendation> Recommend(IEnumerable<PropertySummary> candidates, int take)
    {
        if (!State.Ready) return [];
        return candidates
            .Where(c => c.Property.Status == PropertyStatus.None && _vectors.ContainsKey(c.Property.Id))
            .Select(c => (Item: c, Score: Score(c.Property.Id)))
            .OrderByDescending(x => x.Score)
            .Take(take)
            .Select(x => new Recommendation(x.Item, x.Score, Reasons(x.Item.Property.Id)))
            .ToList();
    }

    /// <summary>Score d'un bien connu : régression logistique et voisins, ou voisins seuls (peu de biens écartés).</summary>
    public double Score(string propertyId)
    {
        var x = _vectors[propertyId];
        var neighbours = NeighbourScore(x);
        return State.Trained ? 0.7 * Sigmoid(Dot(x) + _bias) + 0.3 * neighbours : neighbours;
    }

    // ------------------------------------------------------------------ vecteurs

    private void Build(IReadOnlyList<PropertySummary> all, IReadOnlyDictionary<string, PhotoStats> photos)
    {
        var raw = all.ToDictionary(s => s.Property.Id, s => Raw(s, photos.GetValueOrDefault(s.Property.Id)));
        foreach (var s in all) _items[s.Property.Id] = s;

        // Nombres (critères, photos) : centrés-réduits sur tous les biens ; inconnu = moyenne (0).
        foreach (var name in raw.Values.SelectMany(v => v.Keys).Where(k => k.StartsWith("n:", StringComparison.Ordinal) || k.StartsWith("p:", StringComparison.Ordinal)).Distinct())
        {
            var values = raw.Values.Where(v => v.ContainsKey(name)).Select(v => v[name]).ToList();
            var mean = values.Average();
            var sd = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / Math.Max(1, values.Count - 1));
            _scales[name] = (mean, sd < 1e-9 ? 1 : sd);
        }

        // Mots : TF-IDF sur les biens ; gardés s'ils apparaissent dans au moins 2 biens et au plus la moitié.
        var docs = all.ToDictionary(s => s.Property.Id, s => Words(s));
        var df = docs.Values.SelectMany(w => w.Keys).GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
        var n = Math.Max(1, all.Count);

        foreach (var (id, values) in raw)
        {
            var v = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (name, value) in values)
                v[name] = _scales.TryGetValue(name, out var s) ? Math.Clamp((value - s.Mean) / s.Sd, -3, 3) : value;
            var words = docs[id]
                .Where(w => df[w.Key] >= 2 && df[w.Key] <= Math.Max(2, n / 2))
                .ToDictionary(w => "w:" + w.Key, w => (1 + Math.Log(w.Value)) * Math.Log((double)n / df[w.Key]));
            var norm = Math.Sqrt(words.Values.Sum(x => x * x));
            foreach (var (word, weight) in words) v[word] = norm > 0 ? 1.5 * weight / norm : 0;
            _vectors[id] = v;
        }

        _labeled = all
            .Where(s => s.Property.Status != PropertyStatus.None)
            .Select(s => (s.Property.Id, Liked: s.Property.Status != PropertyStatus.Rejected,
                Weight: s.Property.Status switch { PropertyStatus.ToContact => 2.0, PropertyStatus.Rejected => 1.5, _ => 1.0 }))
            .ToList();
        var liked = _labeled.Count(l => l.Liked);
        var disliked = _labeled.Count - liked;
        var trained = liked >= MinForModel && disliked >= MinForModel;
        if (trained) Fit();
        State = new RecommenderState(liked, disliked, trained, photos.Count(p => _items.ContainsKey(p.Key)), all.Count);
    }

    /// <summary>Valeurs brutes d'un bien : n: nombres, p: photos, c: catégories, f: équipements (+1 / -1).</summary>
    private Dictionary<string, double> Raw(PropertySummary s, PhotoStats? photo)
    {
        var d = s.Details ?? new ListingData();
        var v = new Dictionary<string, double>(StringComparer.Ordinal);
        void Num(string name, double? value) { if (value is { } x && double.IsFinite(x)) v["n:" + name] = x; }
        void Cat(string name, string? value) { if (!string.IsNullOrWhiteSpace(value)) v[$"c:{name}={DedupScorer.NormalizeText(value)}"] = 1; }

        var price = (double?)s.Price;
        var surface = s.Surface ?? d.Surface;
        Num("price", price > 0 ? Math.Log(price.Value) : null);
        Num("surface", surface > 0 ? Math.Log(surface.Value) : null);
        Num("ppm2", price > 0 && surface > 0 ? Math.Log(price.Value / surface.Value) : null);
        Num("land", d.LandSurface > 0 ? Math.Log(1 + d.LandSurface.Value) : null);
        Num("rooms", s.Rooms ?? d.Rooms);
        Num("bedrooms", d.Bedrooms);
        Num("bathrooms", d.Bathrooms);
        Num("dpe", EnergyClasses.Normalize(d.EnergyClass) is { } e ? e[0] - 'A' + 1 : null);
        Num("year", d.YearBuilt);

        Cat("type", PropertyTypes.Normalize(d.PropertyType));
        Cat("subtype", d.Subtype);
        if (s.PostalCode is { Length: >= 2 } cp) Cat("dep", cp[..2]);
        if (s.City is { } city)
        {
            Cat("city", city);
            _cityNames[DedupScorer.NormalizeText(city)] = city;
        }
        // Avoir un terrain compte en soi (un terrain inconnu vaut la moyenne dans "n:land").
        if (d.LandSurface > 0 || d.Has(Features.Garden) == true) Cat("land", "oui");
        if (d.NewBuild == true) Cat("new", "oui");
        if (d.WorkNeeded == true) Cat("work", "oui");
        Cat("sale", d.SaleKind);
        foreach (var (feature, present) in d.Features ?? new Dictionary<string, bool>())
            v["f:" + feature] = present ? 1 : -1;

        if (photo is not null)
            for (var i = 0; i < PhotoStats.Names.Count; i++) v["p:" + PhotoStats.Names[i]] = photo.Values[i];
        return v;
    }

    /// <summary>Mots du titre et de la description : normalisés, 4 lettres au moins, sans pluriel, sans mots vides.</summary>
    private static Dictionary<string, int> Words(PropertySummary s)
    {
        var d = s.Details;
        var text = DedupScorer.NormalizeText($"{s.Title} {d?.Subtype} {d?.DescriptionExcerpt}");
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 4 && !t.Any(char.IsAsciiDigit) && !StopWords.Contains(t))
            .Select(t => t.Length > 4 && (t.EndsWith('s') || t.EndsWith('x')) ? t[..^1] : t)
            .Where(w => !StopWords.Contains(w));
        foreach (var w in words) counts[w] = counts.GetValueOrDefault(w) + 1;
        return counts;
    }

    // ------------------------------------------------------------------ apprentissage

    /// <summary>
    /// Régression logistique pondérée : classes équilibrées (autant de poids pour "aime" que pour "n'aime pas"),
    /// régularisation L2 forte (peu d'exemples), descente de gradient sur tous les exemples.
    /// </summary>
    private void Fit()
    {
        var posWeight = _labeled.Where(l => l.Liked).Sum(l => l.Weight);
        var negWeight = _labeled.Where(l => !l.Liked).Sum(l => l.Weight);
        var samples = _labeled.Select(l => (X: _vectors[l.Id], Label: l.Liked ? 1.0 : 0.0, W: l.Weight * (l.Liked ? 0.5 / posWeight : 0.5 / negWeight))).ToList();
        const double rate = 0.5, lambda = 0.02;
        for (var it = 0; it < 400; it++)
        {
            var grad = new Dictionary<string, double>(StringComparer.Ordinal);
            var gradBias = 0.0;
            foreach (var (x, label, w) in samples)
            {
                var err = (Sigmoid(Dot(x) + _bias) - label) * w;
                gradBias += err;
                foreach (var (k, value) in x) grad[k] = grad.GetValueOrDefault(k) + err * value;
            }
            foreach (var k in grad.Keys.Union(_weights.Keys).ToList())
                _weights[k] = _weights.GetValueOrDefault(k) - rate * (grad.GetValueOrDefault(k) + lambda * _weights.GetValueOrDefault(k));
            _bias -= rate * gradBias;
        }
    }

    private double Dot(Dictionary<string, double> x) => x.Sum(kv => _weights.GetValueOrDefault(kv.Key) * kv.Value);

    private static double Sigmoid(double z) => 1 / (1 + Math.Exp(-z));

    /// <summary>
    /// Voisins : similarité avec les biens notés les plus proches, + pour un bien aimé, - pour un bien écarté,
    /// ramenée entre 0 et 1.
    /// </summary>
    private double NeighbourScore(Dictionary<string, double> x)
    {
        var near = _labeled
            .Select(l => (Sim: Cosine(x, _vectors[l.Id]), l.Liked, l.Weight))
            .OrderByDescending(n => n.Sim)
            .Take(Neighbours)
            .ToList();
        if (near.Count == 0) return 0;
        var total = near.Sum(n => n.Weight);
        var signed = near.Sum(n => n.Weight * n.Sim * (n.Liked ? 1 : -1)) / total;
        return Math.Clamp((signed + 1) / 2, 0, 1);
    }

    private static double Cosine(Dictionary<string, double> a, Dictionary<string, double> b)
    {
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        var dot = small.Sum(kv => large.TryGetValue(kv.Key, out var v) ? kv.Value * v : 0);
        var na = Math.Sqrt(a.Values.Sum(v => v * v));
        var nb = Math.Sqrt(b.Values.Sum(v => v * v));
        return na <= 0 || nb <= 0 ? 0 : dot / (na * nb);
    }

    // ------------------------------------------------------------------ explications

    /// <summary>Ce qui a le plus fait monter le score (3 au plus), puis le bien aimé le plus proche.</summary>
    private List<string> Reasons(string id)
    {
        var x = _vectors[id];
        var item = _items[id];
        var reasons = new List<string>();
        if (State.Trained)
        {
            foreach (var (name, _) in x.Select(kv => (kv.Key, Contribution: _weights.GetValueOrDefault(kv.Key) * kv.Value))
                         .Where(c => c.Contribution > 0.05).OrderByDescending(c => c.Contribution))
            {
                if (Describe(name, x[name], item) is { } text && !reasons.Contains(text)) reasons.Add(text);
                if (reasons.Count == 3) break;
            }
        }
        var closest = _labeled.Where(l => l.Liked)
            .Select(l => (l.Id, Sim: Cosine(x, _vectors[l.Id])))
            .OrderByDescending(l => l.Sim)
            .FirstOrDefault();
        if (closest.Id is not null && closest.Sim > 0.2 && _items[closest.Id] is var liked)
            reasons.Add($"ressemble à « {liked.Title ?? "un bien"} » ({Labels.Of(liked.Property.Status).ToLowerInvariant()})");
        return reasons;
    }

    /// <summary>Un élément du vecteur, en français : "grand terrain (1 850 m²)", "mot « montagne »", "piscine"...</summary>
    private string? Describe(string name, double value, PropertySummary s)
    {
        var d = s.Details ?? new ListingData();
        var high = value > 0;
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        var key = name[(colon + 1)..];
        switch (name[..colon])
        {
            case "w":
                return $"mot « {key} »";
            case "f":
                return high ? Features.Label(key).ToLowerInvariant() : $"sans {Features.Label(key).ToLowerInvariant()}";
            case "c":
                var eq = key.IndexOf('=', StringComparison.Ordinal);
                var (cat, val) = (key[..eq], key[(eq + 1)..]);
                return cat switch
                {
                    "type" => $"type : {PropertyTypes.Label(val).ToLowerInvariant()}",
                    "subtype" => $"type : {d.Subtype?.ToLowerInvariant() ?? val}",
                    "city" => $"commune : {_cityNames.GetValueOrDefault(val, val)}",
                    "dep" => $"département {val}",
                    "land" => "avec terrain",
                    "new" => "neuf",
                    "work" => "travaux à prévoir",
                    "sale" => SaleKinds.Label(d.SaleKind).ToLowerInvariant(),
                    _ => null,
                };
            case "p":
                return key switch
                {
                    "green" => high ? "photos très vertes (nature)" : "photos peu vertes",
                    "sky" => high ? "beaucoup de ciel sur les photos" : null,
                    "snow" => high ? "photos claires (neige, blanc)" : null,
                    "warm" => high ? "tons chauds (bois, pierre)" : null,
                    "bright" => high ? "photos lumineuses" : "photos sombres",
                    "saturation" => high ? "couleurs vives" : "couleurs douces",
                    "detail" => high ? "photos riches en détails" : "photos épurées",
                    "dark" => high ? "photos sombres" : null,
                    _ => null,
                };
            case "n":
                return key switch
                {
                    "price" when s.Price is { } p => $"prix plutôt {(high ? "élevé" : "bas")} ({p.ToString("#,0", Fr)} €)",
                    "surface" when (s.Surface ?? d.Surface) is { } sf => $"{(high ? "grande" : "petite")} surface ({sf.ToString("#,0", Fr)} m²)",
                    "ppm2" => $"prix au m² plutôt {(high ? "élevé" : "bas")}",
                    "land" when d.LandSurface is { } land => $"{(high ? "grand" : "petit")} terrain ({land.ToString("#,0", Fr)} m²)",
                    "rooms" when (s.Rooms ?? d.Rooms) is { } r => $"{r} pièces",
                    "bedrooms" when d.Bedrooms is { } b => $"{b} chambres",
                    "bathrooms" when d.Bathrooms is { } b => $"{b} salle(s) de bain",
                    "dpe" when d.EnergyClass is { } e => $"DPE {e}",
                    "year" when d.YearBuilt is { } y => $"construction {(high ? "récente" : "ancienne")} ({y})",
                    _ => null,
                };
            default:
                return null;
        }
    }
}
