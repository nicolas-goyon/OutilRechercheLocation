using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using RechercheLogement.Core.Contracts;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Core.Services;
using RechercheLogement.Server.Photos;
using RechercheLogement.Server.Recommend;
using SkiaSharp;

namespace RechercheLogement.Tests;

/// <summary>Cache des photos : ordre des téléchargements, compression, place limitée, adresse /photo.</summary>
public sealed class PhotoCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rl-photos-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static byte[] Jpeg(int width, int height, SKColor color)
    {
        using var bmp = new SKBitmap(width, height);
        bmp.Erase(color);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static List<string> Photos(string id, int count) => Enumerable.Range(0, count).Select(i => $"https://cdn/{id}/{i}.jpg").ToList();

    [Fact]
    public void Plan_ByLevel_ThenPhotoByPhoto()
    {
        var wanted = new[]
        {
            new PhotoPlan.Wanted("list1", Photos("list1", 8), PhotoPlan.Depth, PhotoPlan.List),
            new PhotoPlan.Wanted("open", Photos("open", 12), PhotoPlan.DeepDepth, PhotoPlan.Focus),
            new PhotoPlan.Wanted("list2", Photos("list2", 2), PhotoPlan.Depth, PhotoPlan.List),
        };
        var urls = PhotoPlan.Urls(wanted, u => u == "https://cdn/list1/0.jpg").Select(x => x.Url).ToList();

        // Bien ouvert : ses 10 premières photos d'abord.
        Assert.Equal(Photos("open", 10), urls.Take(10));
        // Liste : 1re photo de chaque bien (sauf celle déjà en cache), puis la 2e…, 5 au plus par bien.
        Assert.Equal(new[] { "https://cdn/list2/0.jpg", "https://cdn/list1/1.jpg", "https://cdn/list2/1.jpg", "https://cdn/list1/2.jpg" }, urls.Skip(10).Take(4));
        Assert.Equal(10 + 4 + 2, urls.Count);
    }

    [Fact]
    public void Eviction_PhotosThatServeLeastGoFirst()
    {
        const long now = 1_000_000;
        var gone = PhotoPlan.EvictionRank(null, 0, now, now);
        var rejected = PhotoPlan.EvictionRank(PropertyStatus.Rejected, 0, now, now);
        var seen = PhotoPlan.EvictionRank(PropertyStatus.Seen, 0, now, now);
        var far = PhotoPlan.EvictionRank(PropertyStatus.None, 7, now, now);
        var first = PhotoPlan.EvictionRank(PropertyStatus.None, 0, now, now);
        Assert.True(gone < rejected && rejected < seen && seen < far && far < first);
        Assert.True(PhotoPlan.EvictionRank(PropertyStatus.None, 0, now / 2, now) < first); // regardée il y a longtemps
    }

    [Fact]
    public void Store_CompressesToWebp_TwoSizes_AndSurvivesARestart()
    {
        var cache = new PhotoCache(_dir, long.MaxValue);
        const string url = "https://file.bienici.com/photo/a.jpg";
        Assert.True(cache.Store(url, Jpeg(3000, 2000, new SKColor(40, 120, 200))));
        Assert.False(cache.Store("https://x/bad.jpg", [1, 2, 3]));

        using (var small = SKBitmap.Decode(cache.Find(url, PhotoSize.Small)))
        using (var large = SKBitmap.Decode(cache.Find(url, PhotoSize.Large)))
        {
            Assert.Equal((480, 320), (small.Width, small.Height));
            Assert.Equal((1600, 1067), (large.Width, large.Height));
        }
        Assert.StartsWith("RIFF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(cache.Find(url, PhotoSize.Small)!), 0, 4), StringComparison.Ordinal);
        Assert.Null(cache.Find("https://autre", PhotoSize.Small));

        var reopened = new PhotoCache(_dir, long.MaxValue);
        Assert.True(reopened.Contains(url));
        Assert.Equal(cache.TotalBytes, reopened.TotalBytes);
    }

    [Fact]
    public void Evict_FollowsTheOrder_UntilUnderTheLimit()
    {
        var cache = new PhotoCache(_dir, long.MaxValue);
        var urls = Enumerable.Range(0, 4).Select(i => $"https://cdn/p{i}.jpg").ToList();
        foreach (var (u, i) in urls.Select((u, i) => (u, i))) cache.Store(u, Jpeg(800, 600, new SKColor((byte)(i * 60), 80, 80)));

        // Rang : p3 d'abord, puis p2…
        var rank = urls.ToDictionary(PhotoCache.Key, u => -urls.IndexOf(u));
        var removed = cache.Evict(cache.TotalBytes - 1, (key, _) => rank[key]); // une photo de trop
        Assert.Equal(1, removed);
        Assert.False(cache.Contains(urls[3]));
        Assert.True(cache.Contains(urls[0]));

        Assert.Equal(1, cache.RemoveWhere((key, _) => key == PhotoCache.Key(urls[1])));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public async Task Endpoint_ServesTheCachedPhoto_OrRedirectsToTheSite()
    {
        const string url = "https://file.bienici.com/photo/b.jpg";
        new PhotoCache(Path.Combine(_dir, "photos"), long.MaxValue).Store(url, Jpeg(1000, 800, SKColors.Green));

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("RechercheLogement:DatabasePath", Path.Combine(_dir, "test.db"));
            b.UseSetting("RechercheLogement:AnalyzePhotos", "false");
            b.UseSetting("RechercheLogement:ProjectsAutoRun", "false");
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var hit = await client.GetAsync($"/photo?u={Uri.EscapeDataString(url)}&w=320&h=200");
        Assert.Equal(HttpStatusCode.OK, hit.StatusCode);
        Assert.Equal("image/webp", hit.Content.Headers.ContentType?.MediaType);
        using (var img = SKBitmap.Decode(await hit.Content.ReadAsByteArrayAsync())) Assert.Equal(480, img.Width);

        var large = await client.GetAsync($"/photo?u={Uri.EscapeDataString(url)}");
        using (var img = SKBitmap.Decode(await large.Content.ReadAsByteArrayAsync())) Assert.Equal(1000, img.Width); // jamais agrandie

        const string other = "https://file.bienici.com/photo/c.jpg";
        var miss = await client.GetAsync($"/photo?u={Uri.EscapeDataString(other)}&w=320&h=200");
        Assert.Equal(HttpStatusCode.Redirect, miss.StatusCode);
        Assert.Equal($"{other}?width=320&height=200&fit=cover", miss.Headers.Location?.ToString());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/photo?u=javascript:alert(1)")).StatusCode);
    }

    /// <summary>
    /// Bout en bout : trois biens, on ouvre le 2e. Ses 5 premières photos arrivent d'abord, au rythme des pages
    /// regardées ; le bien refusé n'est jamais téléchargé en tâche de fond.
    /// </summary>
    [Fact]
    public async Task Prefetcher_OpenedPropertyFirst_RejectedNeverInBackground()
    {
        var catalog = new Catalog(new InMemoryPersistence());
        ListingData Data(string id, decimal price) => new() { Title = id, PostalCode = "65240", Price = price, Surface = 100, Photos = Photos(id, 7) };
        catalog.Sync(new SyncRequest("t",
        [
            new Observation("bienici", "a", "api", Data("a", 100_000), 1),
            new Observation("bienici", "b", "api", Data("b", 200_000), 1),
            new Observation("bienici", "c", "api", Data("c", 300_000), 1),
        ], null, null));
        var ids = catalog.QueryProperties(new PropertyQuery()).ToDictionary(p => p.Title!, p => p.Property.Id);
        catalog.SetStatus(ids["c"], PropertyStatus.Rejected);

        var http = new FakeImages(Jpeg(400, 300, SKColors.Brown));
        var cache = new PhotoCache(_dir, long.MaxValue);
        using var prefetcher = new PhotoPrefetcher(cache, catalog, new ProjectStore(new InMemoryProjectPersistence()),
            new RecommendationService(catalog, new InMemoryPhotoStatsStore()), http, NullLogger<PhotoPrefetcher>.Instance)
        { StartDelay = TimeSpan.Zero };
        prefetcher.FocusProperty(ids["b"]);
        await prefetcher.StartAsync(default);
        for (var i = 0; i < 100 && http.Requested.Count < 6; i++) await Task.Delay(100);
        await prefetcher.StopAsync(default);

        Assert.Equal(Photos("b", 5), http.Requested.Take(5));
        Assert.All(Photos("b", 5), u => Assert.True(cache.Contains(u)));
        Assert.Equal("https://cdn/a/0.jpg", http.Requested[5]); // puis les biens non notés
        Assert.DoesNotContain(http.Requested, u => u.Contains("/c/", StringComparison.Ordinal));
    }

    private sealed class FakeImages(byte[] image) : DelegatingHandler, IHttpClientFactory
    {
        public List<string> Requested { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requested) Requested.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(image) });
        }
    }
}
