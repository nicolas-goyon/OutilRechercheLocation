using System.Text.Json.Nodes;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Sites.Bienici;
using RechercheLogement.Server.Sites.Etat;
using RechercheLogement.Server.Sites.Seloger;

namespace RechercheLogement.Tests;

/// <summary>
/// Informations détaillées des annonces (terrain, DPE, équipements...) : lecture par chaque connecteur, critères
/// envoyés aux sites, vérification par l'outil. Les noms des champs ont été relevés sur les sites en novembre 2026.
/// </summary>
public class ListingDetailsTests
{
    private static SearchProject Project(Action<SearchProject>? set = null)
    {
        var p = new SearchProject { Id = "p", Transaction = TransactionType.Buy, PropertyTypes = [PropertyTypes.House] };
        set?.Invoke(p);
        return p;
    }

    // ------------------------------------------------------------------ modèle commun

    [Theory]
    [InlineData("d", "D")]
    [InlineData(" B ", "B")]
    [InlineData("Performance énergétique : G", "G")]
    [InlineData("NS", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void EnergyClass_IsNormalized(string? value, string? expected) => Assert.Equal(expected, EnergyClasses.Normalize(value));

    [Fact]
    public void EnergyClass_UpToAndWorse()
    {
        Assert.Equal(new[] { "A", "B", "C" }, EnergyClasses.UpTo("C"));
        Assert.True(EnergyClasses.Worse("E", "D"));
        Assert.False(EnergyClasses.Worse("D", "D"));
        Assert.False(EnergyClasses.Worse(null, "D"));
        Assert.False(EnergyClasses.Worse("G", null));
    }

    [Fact]
    public void Features_AreMergedOneByOne()
    {
        var api = new ListingData { Features = Features.Of((Features.Pool, true), (Features.Cellar, false)) };
        var page = new ListingData { Features = Features.Of((Features.Cellar, true), (Features.Garden, true)) };

        var replaced = api.Merge(page, fillOnly: false);
        Assert.Equal(true, replaced.Has(Features.Pool));
        Assert.Equal(true, replaced.Has(Features.Cellar));
        Assert.Equal(true, replaced.Has(Features.Garden));
        Assert.Null(replaced.Has(Features.Elevator));

        var filled = api.Merge(page, fillOnly: true);
        Assert.Equal(false, filled.Has(Features.Cellar)); // déjà connu : gardé
        Assert.Equal(true, filled.Has(Features.Garden));
    }

    [Fact]
    public void SameAs_ComparesFeaturesByContent()
    {
        var a = new ListingData { LandSurface = 500, Features = Features.Of((Features.Pool, true), (Features.Garden, true)) };
        var b = new ListingData { LandSurface = 500, Features = Features.Of((Features.Garden, true), (Features.Pool, true)) };
        Assert.True(a.SameAs(b));
        Assert.False(a.SameAs(b with { Features = Features.Of((Features.Pool, false)) }));
        Assert.False(a.SameAs(b with { EnergyClass = "C" }));
    }

    // ------------------------------------------------------------------ vérification par l'outil

    [Fact]
    public void Matcher_ChecksTheNewCriteria_AndKeepsUnknownValues()
    {
        var p = Project(p =>
        {
            p.LandSurfaceMin = 1000;
            p.EnergyClassMax = "D";
            p.YearBuiltMin = 1950;
            p.BathroomsMin = 2;
            p.RequiredFeatures = [Features.Pool, Features.Fireplace];
            p.NewBuild = false;
            p.WorkNeeded = false;
        });
        var good = new ListingData { LandSurface = 1500, EnergyClass = "C", YearBuilt = 1980, Bathrooms = 2, NewBuild = false, WorkNeeded = false, Features = Features.Of((Features.Pool, true)) };
        Assert.True(ProjectMatcher.Matches(p, good, out _));
        Assert.True(ProjectMatcher.Matches(p, new ListingData(), out _)); // rien de connu : gardée

        void Fails(ListingData d, string reason)
        {
            Assert.False(ProjectMatcher.Matches(p, d, out var why));
            Assert.Equal(reason, why);
        }
        Fails(good with { LandSurface = 400 }, "terrain");
        Fails(good with { EnergyClass = "F" }, "DPE F");
        Fails(good with { YearBuilt = 1900 }, "année de construction");
        Fails(good with { Bathrooms = 1 }, "salles de bain");
        Fails(good with { Features = Features.Of((Features.Pool, true), (Features.Fireplace, false)) }, "sans cheminée");
        Fails(good with { NewBuild = true }, "neuf");
        Fails(good with { WorkNeeded = true }, "travaux à prévoir");
    }

    [Fact]
    public void Matcher_DropsLifeAnnuities_Unless_Asked()
    {
        var viager = new ListingData { SaleKind = SaleKinds.LifeAnnuity };
        Assert.False(ProjectMatcher.Matches(Project(), viager, out var why));
        Assert.Equal("viager", why);
        Assert.True(ProjectMatcher.Matches(Project(p => p.ExcludeLifeAnnuity = false), viager, out _));
        Assert.True(ProjectMatcher.Matches(Project(), new ListingData { SaleKind = SaleKinds.Auction }, out _));
    }

    // ------------------------------------------------------------------ Bien'ici

    [Fact]
    public void Bienici_ReadsTheDetails()
    {
        var ad = JsonNode.Parse("""
            {
              "id": "ag1-42", "adType": "lifeAnnuitySale", "propertyType": "house", "propertySubtype": "barn",
              "landSurfaceArea": 425, "energyClassification": "E", "greenhouseGazClassification": "d",
              "yearOfConstruction": 1800, "bathroomsQuantity": 1, "showerRoomsQuantity": 1, "heating": "gaz individuel",
              "exposition": "ouest", "newProperty": false, "workToDo": true,
              "hasGarden": true, "hasPool": false, "terracesQuantity": 0, "hasBalcony": false, "garagesQuantity": 1,
              "parkingPlacesQuantity": 0, "hasCellar": false, "hasFirePlace": true, "isDisabledPeopleFriendly": false
            }
            """)!;
        var d = BieniciListingParser.ParseAd(ad)!.Data;

        Assert.Equal(TransactionType.Buy, d.Transaction);
        Assert.Equal(SaleKinds.LifeAnnuity, d.SaleKind);
        Assert.Equal("Grange", d.Subtype);
        Assert.Equal(425, d.LandSurface);
        Assert.Equal(("E", "D"), (d.EnergyClass, d.GhgClass));
        Assert.Equal(1800, d.YearBuilt);
        Assert.Equal(2, d.Bathrooms);
        Assert.Equal("gaz individuel", d.Heating);
        Assert.Equal("ouest", d.Exposure);
        Assert.Equal((false, true), (d.NewBuild, d.WorkNeeded));
        Assert.Equal(true, d.Has(Features.Garden));
        Assert.Equal(false, d.Has(Features.Pool));
        Assert.Equal(false, d.Has(Features.Terrace));
        Assert.Equal(true, d.Has(Features.Garage));
        Assert.Equal(false, d.Has(Features.Parking));
        Assert.Equal(true, d.Has(Features.Fireplace));
        Assert.Null(d.Has(Features.Elevator));
    }

    [Fact]
    public void Bienici_IgnoresHeatingCodesAndUnknownSubtypes()
    {
        var d = BieniciListingParser.ParseAd(JsonNode.Parse("""{ "id": "x", "heating": "7", "propertySubtype": "unknown", "landSurfaceArea": 0 }""")!)!.Data;
        Assert.Null(d.Heating);
        Assert.Null(d.Subtype);
        Assert.Null(d.LandSurface);
        Assert.Null(d.Features);
    }

    [Fact]
    public void Bienici_SendsTheCriteriaItKnows()
    {
        var p = Project(p =>
        {
            p.LandSurfaceMin = 1000;
            p.LandSurfaceMax = 5000;
            p.EnergyClassMax = "C";
            p.NewBuild = false;
            p.WorkNeeded = false;
            p.RequiredFeatures = [Features.Pool, Features.Garage, Features.Parking, Features.AirConditioning];
        });
        var f = BieniciSearchAdapter.BuildFilters(p, ["-7467"], 0);

        Assert.Equal(1000, (double)f["minGardenSurfaceArea"]!);
        Assert.Equal(5000, (double)f["maxGardenSurfaceArea"]!);
        Assert.Equal("[\"A\",\"B\",\"C\"]", f["energyClassification"]!.ToJsonString());
        Assert.False((bool)f["newProperty"]!);
        Assert.Null(f["workToDo"]); // "sans travaux" : vérifié par l'outil
        Assert.True((bool)f["hasPool"]!);
        Assert.True((bool)f["hasParking"]!);
        Assert.Null(f["hasAirConditioning"]); // pas de filtre sur le site
    }

    // ------------------------------------------------------------------ SeLoger

    [Fact]
    public void Seloger_ReadsTheListDetails()
    {
        var c = JsonNode.Parse("""
            {
              "id": "262XRHGFRHPC",
              "hardFacts": { "title": "Maison à vendre • neuf", "keyfacts": ["5 pièces", "156 m²", "750 m² de terrain"],
                             "facts": [{ "type": "plotSpace", "value": "750 m² de terrain", "splitValue": "750" }] },
              "energyClass": "D",
              "legacyTracking": { "year_of_construction": 2027 },
              "rawData": { "distributionType": "BUY", "propertyType": "HOUSE", "propertySubType": "VILLA", "nbroom": 5, "surface": { "main": 156 } }
            }
            """)!;
        var d = SelogerListingParser.ParseClassified(c)!.Data;

        Assert.Equal(750, d.LandSurface);
        Assert.Equal("D", d.EnergyClass);
        Assert.Equal(2027, d.YearBuilt);
        Assert.Equal("Villa", d.Subtype);
        Assert.True(d.NewBuild);
        Assert.Null(d.SaleKind);
    }

    [Theory]
    [InlineData("Maison à vendre", null, false)]
    [InlineData("Maison à vendre • neuf", null, true)]
    [InlineData("Maison en viager • occupé", null, false)]
    [InlineData("Maison", "NEW_BUILD_UNIT", true)]
    [InlineData("Maison • neuf", "RESALE", false)]
    [InlineData(null, null, null)]
    public void Seloger_NewBuild(string? heading, string? subType, bool? expected) =>
        Assert.Equal(expected, SelogerListingParser.NewBuild(heading, subType));

    [Fact]
    public void Seloger_LifeAnnuity_FromTheTitle()
    {
        var c = JsonNode.Parse("""{ "id": "v", "hardFacts": { "title": "Maison en viager • occupé" }, "rawData": { "distributionType": "BUY" } }""")!;
        Assert.Equal(SaleKinds.LifeAnnuity, SelogerListingParser.ParseClassified(c)!.Data.SaleKind);
    }

    [Fact]
    public void Seloger_SendsTheCriteriaItKnows()
    {
        var p = Project(p =>
        {
            p.LandSurfaceMin = 1000;
            p.YearBuiltMin = 1950;
            p.YearBuiltMax = 2000;
            p.EnergyClassMax = "B";
            p.RequiredFeatures = [Features.Terrace, Features.Balcony, Features.Pool, Features.Fireplace];
        });
        var c = SelogerSearchAdapter.BuildSearch(p, ["AD06FR65"], 1)["criteria"]!;

        Assert.Equal(1000, (double)c["plotSpaceMin"]!);
        Assert.Equal(1950, (int)c["yearOfConstructionMin"]!);
        Assert.Equal(2000, (int)c["yearOfConstructionMax"]!);
        Assert.Equal("[\"A\",\"B\"]", c["energyCertificateClass"]!.ToJsonString());
        Assert.Equal("[\"Balcony_Terrace\",\"Swimming_Pool\"]", c["featuresIncluded"]!.ToJsonString());
        Assert.Equal("[\"Resale\",\"New_Build\",\"Projected\"]", c["projectTypes"]!.ToJsonString()); // viagers écartés
    }

    [Fact]
    public void Seloger_ProjectTypes()
    {
        Assert.Equal(["Resale"], SelogerSearchAdapter.ProjectTypes(Project(p => p.NewBuild = false))!);
        Assert.Equal(["New_Build", "Projected"], SelogerSearchAdapter.ProjectTypes(Project(p => p.NewBuild = true))!);
        Assert.Equal(["Resale", "Life_Annuity"], SelogerSearchAdapter.ProjectTypes(Project(p => { p.NewBuild = false; p.ExcludeLifeAnnuity = false; }))!);
        Assert.Null(SelogerSearchAdapter.ProjectTypes(Project(p => p.ExcludeLifeAnnuity = false))); // rien à préciser
        Assert.Null(SelogerSearchAdapter.ProjectTypes(Project(p => p.Transaction = TransactionType.Rent)));
    }

    // ------------------------------------------------------------------ État

    [Fact]
    public void Etat_ReadsTheDetailTiles()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "etat-fiche-maison.html"));
        var card = new EtatCard("/biens/ancienne-maison-eclusiere-en-bord-de-seine", "Ancienne maison", "Poses", "27", "Maisons", 80, "260270001");
        var d = EtatListingParser.ParseDetail(html, card).Data;

        Assert.Equal(1283, d.LandSurface);
        Assert.Equal(("G", "C"), (d.EnergyClass, d.GhgClass));
        Assert.Equal(2, d.Bedrooms);
        Assert.Equal(1, d.Bathrooms);
        Assert.Equal(1, d.Floor);
        Assert.True(d.WorkNeeded);
        Assert.Equal("mixte bois-Fuel/electrique", d.Heating);
        Assert.Equal(1411m, d.PropertyTax);
        Assert.Equal(SaleKinds.Auction, d.SaleKind);
        Assert.Equal(true, d.Has(Features.Garden));
        Assert.Equal(true, d.Has(Features.Cellar));
        Assert.Equal(false, d.Has(Features.Pool));
        Assert.Equal(true, d.Has(Features.Terrace));
        Assert.Null(d.Has(Features.Balcony));
    }

    [Theory]
    [InlineData("Travaux à prévoir", true)]
    [InlineData("A rénover", true)]
    [InlineData("Très bon état d'entretien", false)]
    [InlineData("Correct", false)]
    [InlineData("Sous-sol", null)]
    public void Etat_WorkNeeded(string state, bool? expected) => Assert.Equal(expected, EtatListingParser.WorkNeeded(state));

    [Theory]
    [InlineData("Appel d'offres", SaleKinds.Tender)]
    [InlineData("Adjudication", SaleKinds.Auction)]
    [InlineData("Vente par enchères en ligne", SaleKinds.Auction)]
    [InlineData("Vente amiable", null)]
    public void Etat_SaleKind(string procedure, string? expected) => Assert.Equal(expected, EtatListingParser.SaleKind(procedure));
}
