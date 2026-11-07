using RechercheLogement.Core.Contracts;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Services;

namespace RechercheLogement.Tests;

/// <summary>Historique du bien : annonce trouvée, puis ses changements (prix, surface, DPE, équipements...).</summary>
public class ListingChangesTests
{
    private long _now = 1_000_000;

    private Catalog NewCatalog() => new(new InMemoryPersistence(), new CatalogOptions { Now = () => _now++ });

    private static SyncRequest Req(string source, ListingData data) =>
        new("test", [new Observation("bienici", "x", source, data, 0)], null, null);

    private static readonly ListingData First = new()
    {
        Title = "Maison 5 pièces",
        Price = 250_000,
        Surface = 120,
        PostalCode = "65240",
        EnergyClass = "E",
        Photos = ["a", "b"],
        DescriptionExcerpt = "maison de village",
        Features = Features.Of((Features.Pool, false), (Features.Garden, true)),
    };

    [Fact]
    public void Describe_ListsWhatChanged()
    {
        var after = First with
        {
            Price = 239_000,
            EnergyClass = "D",
            Photos = ["a", "b", "c"],
            DescriptionExcerpt = "maison de village rénovée",
            Features = Features.Of((Features.Pool, true), (Features.Garden, false)),
        };
        Assert.Equal(
            ["prix 250 000 € → 239 000 € (−4,4 %)", "DPE E → D", "ajouté : piscine", "retiré : jardin", "photos 2 → 3", "description modifiée"],
            ListingChanges.Describe(First, after).Select(s => s.Replace(' ', ' ').Replace(' ', ' ')));
    }

    [Fact]
    public void Describe_IgnoresInformationThatOnlyAppears()
    {
        // Deuxième lecture plus complète (terrain, chambres) : rien n'a changé dans l'annonce.
        Assert.Empty(ListingChanges.Describe(First with { LandSurface = null, Bedrooms = null }, First with { LandSurface = 800, Bedrooms = 3 }));
        Assert.Null(ListingChanges.EventText("bienici", First, First));
    }

    [Fact]
    public void Catalog_RecordsTheNewListing_AndItsChanges()
    {
        var c = NewCatalog();
        c.Sync(Req("api", First));
        c.Sync(Req("api", First with { Price = 239_000 }));
        c.Sync(Req("api", First with { Price = 239_000 })); // relu sans changement : rien de plus

        var events = c.GetProperty(c.PropertyIdOf("bienici:x")!)!.Events;
        var added = Assert.Single(events, e => e.Kind == PropertyEventKind.ListingAdded);
        Assert.StartsWith("Bien'ici : annonce trouvée", added.Text, StringComparison.Ordinal);
        var changed = Assert.Single(events, e => e.Kind == PropertyEventKind.ListingChanged);
        Assert.StartsWith("Bien'ici : prix 250", changed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_DoesNotCompareACardWithTheApi()
    {
        // La carte arrondit la surface (120,3 -> 120) : l'API qui suit n'est pas un changement de l'annonce.
        var c = NewCatalog();
        c.Sync(Req("card", First));
        c.Sync(Req("api", First with { Surface = 120.3 }));

        var events = c.GetProperty(c.PropertyIdOf("bienici:x")!)!.Events;
        Assert.DoesNotContain(events, e => e.Kind == PropertyEventKind.ListingChanged);
    }
}
