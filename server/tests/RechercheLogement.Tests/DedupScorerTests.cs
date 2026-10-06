using RechercheLogement.Core.Dedup;
using RechercheLogement.Core.Model;

namespace RechercheLogement.Tests;

public class DedupScorerTests
{
    internal static readonly ListingData Bienici = new()
    {
        Transaction = TransactionType.Rent,
        PropertyType = "flat",
        Price = 1866,
        Surface = 88.32,
        Rooms = 3,
        Floor = 1,
        PostalCode = "75019",
        AgencyRef = "TSLAP320002342",
        Geo = new GeoPoint(48.89009, 2.37692, 50),
        DescriptionExcerpt = "exclusivite sia immobilier nous vous proposons a la location ce spacieux appartement 3 pieces de 88 32 m2 situe au 1er etage sans ascenseur au 86 avenue de flandre paris 19e",
    };

    // Le même bien sur un autre site d'annonces : prix hors charges, référence avec un préfixe.
    internal static readonly ListingData SeLoger = new()
    {
        Transaction = TransactionType.Rent,
        PropertyType = "flat",
        Price = 1801,
        Surface = 88,
        Rooms = 3,
        PostalCode = "75019",
        AgencyRef = "SIA-TSLAP320002342",
        DescriptionExcerpt = "nous vous proposons a la location ce spacieux appartement 3 pieces de 88 32 m2 situe au 1er etage sans ascenseur au 86 avenue de flandre paris 19e lumineux",
    };

    [Fact]
    public void SameProperty_OnTwoSites_ReachesAutoLinkThreshold()
    {
        var r = DedupScorer.Compare(Bienici, SeLoger);
        Assert.True(r.Score >= 0.8, $"score {r.Score} : {string.Join(", ", r.Reasons)}");
        Assert.Contains(r.Reasons, x => x.Contains("références agence"));
    }

    [Fact]
    public void SameCharacteristics_WithoutRefOrText_IsOnlyASuggestion()
    {
        var a = new ListingData { Price = 1200, Surface = 45, Rooms = 2, PostalCode = "75011" };
        var b = new ListingData { Price = 1210, Surface = 45.5, Rooms = 2, PostalCode = "75011" };
        var r = DedupScorer.Compare(a, b);
        Assert.True(r.Score is >= 0.5 and < 0.8, $"score {r.Score}");
    }

    [Fact]
    public void HardRejections()
    {
        Assert.Equal("code postal différent", DedupScorer.Compare(Bienici, SeLoger with { PostalCode = "75020" }).RejectedBy);
        Assert.Equal("surfaces trop différentes", DedupScorer.Compare(Bienici, SeLoger with { Surface = 60 }).RejectedBy);
        Assert.Equal("transaction différente", DedupScorer.Compare(Bienici, SeLoger with { Transaction = TransactionType.Buy }).RejectedBy);
        Assert.Equal("nombre de pièces différent", DedupScorer.Compare(Bienici with { Rooms = 1 }, SeLoger).RejectedBy);
    }

    [Fact]
    public void Paris16_PostalCodesAreEquivalent()
    {
        var a = new ListingData { PostalCode = "75116", Surface = 30, Price = 1000, Rooms = 1 };
        var b = new ListingData { PostalCode = "75016", Surface = 30, Price = 1000, Rooms = 1 };
        Assert.Null(DedupScorer.Compare(a, b).RejectedBy);
        Assert.Equal(DedupScorer.BlockingKey(a), DedupScorer.BlockingKey(b));
    }

    [Fact]
    public void IdenticalOriginalPhotos_AreStrongEvidence()
    {
        var a = new ListingData { PostalCode = "75019", PhotoKeys = ["photo_3c9c36dc663b5482", "photo_0a2fdb5cd3d88ed7"] };
        var b = new ListingData { PostalCode = "75019", PhotoKeys = ["photo_0a2fdb5cd3d88ed7", "photo_3c9c36dc663b5482", "x"] };
        var r = DedupScorer.Compare(a, b);
        Assert.Equal(0.7, r.Score);
    }

    [Fact]
    public void NormalizeText_RemovesAccentsAndPunctuation() =>
        Assert.Equal("exclusivite sia l appartement 88 32 m2", DedupScorer.NormalizeText("EXCLUSIVITÉ SIA — l'appartement : 88,32 m2 !"));
}
