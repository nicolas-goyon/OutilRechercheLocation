using RechercheLogement.Core.Model;
using RechercheLogement.Core.Recommend;
using RechercheLogement.Core.Services;

namespace RechercheLogement.Tests;

/// <summary>
/// Recommandations : des chalets avec terrain en montagne aimés, des appartements en ville écartés. Les biens non
/// notés qui ressemblent aux premiers doivent passer devant ceux qui ressemblent aux seconds.
/// </summary>
public class RecommenderTests
{
    private static int _next;

    private static PropertySummary Item(PropertyStatus status, string title, decimal price, double surface, double? land,
        string city, string cp, string description, string type = PropertyTypes.House, bool? pool = null)
    {
        var id = $"p{++_next}";
        var data = new ListingData
        {
            Title = title,
            PropertyType = type,
            Price = price,
            Surface = surface,
            LandSurface = land,
            City = city,
            PostalCode = cp,
            DescriptionExcerpt = description,
            Features = Features.Of((Features.Pool, pool)),
        };
        return new PropertySummary(new Property { Id = id, Status = status }, [], title, price, surface, null, cp, city, null, null, 0, Details: data);
    }

    private static PropertySummary Chalet(PropertyStatus status, int i) => Item(status, $"Chalet {i}", 320_000 + i * 10_000, 120 + i * 5, 1500 + i * 300,
        "Saint-Lary-Soulan", "65170", "chalet bois montagne vue pistes jardin calme");

    private static PropertySummary Flat(PropertyStatus status, int i) => Item(status, $"Appartement {i}", 180_000 + i * 5_000, 45 + i * 3, null,
        "Tarbes", "65000", "appartement centre ville ascenseur commerces tramway", PropertyTypes.Flat);

    [Fact]
    public void LikesAndDislikes_RankSimilarListingsFirst()
    {
        List<PropertySummary> all =
        [
            Chalet(PropertyStatus.ToContact, 1), Chalet(PropertyStatus.Seen, 2), Chalet(PropertyStatus.Seen, 3),
            Flat(PropertyStatus.Rejected, 1), Flat(PropertyStatus.Rejected, 2), Flat(PropertyStatus.Rejected, 3),
        ];
        var chalet = Chalet(PropertyStatus.None, 4);
        var flat = Flat(PropertyStatus.None, 4);
        all.AddRange([chalet, flat]);

        var r = Recommender.Train(all, new Dictionary<string, PhotoStats>());

        Assert.True(r.State.Trained);
        Assert.Equal((3, 3), (r.State.Liked, r.State.Disliked));
        var recos = r.Recommend(all, 10);
        Assert.Equal(new[] { chalet.Property.Id, flat.Property.Id }, recos.Select(x => x.Item.Property.Id));
        Assert.True(recos[0].Score > 0.6, $"{recos[0].Score}");
        Assert.True(recos[1].Score < 0.4, $"{recos[1].Score}");
        Assert.NotEmpty(recos[0].Reasons);
        Assert.Contains(recos[0].Reasons, x => x.StartsWith("ressemble à « Chalet", StringComparison.Ordinal));
    }

    [Fact]
    public void Reasons_SayWhatMattered()
    {
        List<PropertySummary> all =
        [
            .. Enumerable.Range(1, 4).Select(i => Chalet(PropertyStatus.Seen, i)),
            .. Enumerable.Range(1, 4).Select(i => Flat(PropertyStatus.Rejected, i)),
        ];
        var candidate = Chalet(PropertyStatus.None, 9);
        all.Add(candidate);

        var reasons = Recommender.Train(all, new Dictionary<string, PhotoStats>()).Recommend(all, 1).Single().Reasons;

        // Terrain, type, commune ou mots de la description : ce qui distingue les chalets aimés.
        // Au plus 3 éléments du modèle + le bien aimé le plus proche, tous lisibles.
        Assert.InRange(reasons.Count, 2, 4);
        Assert.All(reasons, x => Assert.DoesNotContain(":", x.Split(' ')[0], StringComparison.Ordinal));
        Assert.StartsWith("ressemble à « Chalet", reasons[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyLikes_UsesSimilarity()
    {
        List<PropertySummary> all = [Chalet(PropertyStatus.Seen, 1)];
        var chalet = Chalet(PropertyStatus.None, 2);
        var flat = Flat(PropertyStatus.None, 1);
        all.AddRange([chalet, flat]);

        var r = Recommender.Train(all, new Dictionary<string, PhotoStats>());

        Assert.True(r.State.Ready);
        Assert.False(r.State.Trained);
        Assert.Equal(chalet.Property.Id, r.Recommend(all, 10)[0].Item.Property.Id);
    }

    [Fact]
    public void NothingLiked_NoRecommendation()
    {
        List<PropertySummary> all = [Flat(PropertyStatus.Rejected, 1), Chalet(PropertyStatus.None, 1)];
        var r = Recommender.Train(all, new Dictionary<string, PhotoStats>());
        Assert.False(r.State.Ready);
        Assert.Empty(r.Recommend(all, 10));
    }

    [Fact]
    public void Photos_CountInTheScore()
    {
        // Mêmes biens, seules les photos diffèrent : vertes pour les aimés, grises pour les écartés.
        var green = new PhotoStats(0.6, 0.2, 0.05, 0.1, 0.05, 0.6, 0.5, 0.3);
        var grey = new PhotoStats(0.02, 0.05, 0.3, 0.05, 0.3, 0.4, 0.1, 0.6);
        List<PropertySummary> all = [];
        var photos = new Dictionary<string, PhotoStats>();
        for (var i = 0; i < 8; i++)
        {
            var liked = i % 2 == 0;
            var s = Item(liked ? PropertyStatus.Seen : PropertyStatus.Rejected, "Maison", 250_000, 100, null, "Arreau", "65240", "maison");
            all.Add(s);
            photos[s.Property.Id] = liked ? green : grey;
        }
        var a = Item(PropertyStatus.None, "Maison", 250_000, 100, null, "Arreau", "65240", "maison");
        var b = Item(PropertyStatus.None, "Maison", 250_000, 100, null, "Arreau", "65240", "maison");
        all.AddRange([a, b]);
        photos[a.Property.Id] = green;
        photos[b.Property.Id] = grey;

        var recos = Recommender.Train(all, photos).Recommend(all, 2);

        Assert.Equal(a.Property.Id, recos[0].Item.Property.Id);
        Assert.Contains("photos très vertes (nature)", recos[0].Reasons);
    }

    [Fact]
    public void PhotoStats_Average()
    {
        var avg = PhotoStats.Average([new PhotoStats(1, 0, 0, 0, 0, 1, 0, 0), new PhotoStats(0, 1, 0, 0, 0, 0, 0, 0)])!;
        Assert.Equal((0.5, 0.5, 0.5), (avg.Green, avg.Sky, avg.Bright));
        Assert.Null(PhotoStats.Average([]));
    }
}
