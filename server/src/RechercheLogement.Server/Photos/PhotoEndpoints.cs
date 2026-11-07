using RechercheLogement.Server.Components;

namespace RechercheLogement.Server.Photos;

public static class PhotoEndpoints
{
    /// <summary>
    /// GET /photo?u=&lt;adresse d'origine&gt;&amp;w=320&amp;h=200 : la photo en cache (vignette si w et h ≤ 480, sinon grande
    /// photo) ; absente : redirection vers le site (miniature de son CDN), et son bien passe en tête des
    /// téléchargements. Le serveur ne télécharge jamais une adresse reçue ici : seulement celles du catalogue.
    /// </summary>
    public static void MapPhotos(this IEndpointRouteBuilder app) =>
        app.MapGet("/photo", (string u, int? w, int? h, PhotoPrefetcher prefetcher, HttpContext ctx) =>
        {
            if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return Results.BadRequest();
            int width = w ?? 0, height = h ?? 0;
            var size = width is > 0 and <= PhotoCache.SmallSide && height is >= 0 and <= PhotoCache.SmallSide ? PhotoSize.Small : PhotoSize.Large;
            if (prefetcher.Cache?.Find(u, size) is { } path)
            {
                // L'adresse d'origine ne change pas de contenu : le navigateur peut garder la photo.
                ctx.Response.Headers.CacheControl = "private, max-age=604800";
                return Results.File(path, "image/webp");
            }
            prefetcher.OnScreen(u);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Redirect(width > 0 ? Format.Thumb(u, width, height) : u);
        });

    public static IServiceCollection AddPhotoCache(this IServiceCollection services, ServerSettings settings, IWebHostEnvironment env)
    {
        var memory = settings.Storage.Equals("memory", StringComparison.OrdinalIgnoreCase);
        PhotoCache? cache = null;
        if (settings.PhotoCache && !memory)
        {
            var db = Path.GetFullPath(settings.DatabasePath, env.ContentRootPath);
            var dir = string.IsNullOrWhiteSpace(settings.PhotoCachePath)
                ? Path.Combine(Path.GetDirectoryName(db) ?? env.ContentRootPath, "photos")
                : Path.GetFullPath(settings.PhotoCachePath, env.ContentRootPath);
            cache = new PhotoCache(dir, (long)(Math.Max(0.1, settings.PhotoCacheMaxGb) * 1024 * 1024 * 1024));
        }
        services.AddSingleton(sp => new PhotoPrefetcher(cache, sp.GetRequiredService<Core.Services.Catalog>(),
            sp.GetRequiredService<Core.Projects.ProjectStore>(), sp.GetRequiredService<Recommend.RecommendationService>(),
            sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ILogger<PhotoPrefetcher>>()));
        services.AddHostedService(sp => sp.GetRequiredService<PhotoPrefetcher>());
        return services;
    }
}
