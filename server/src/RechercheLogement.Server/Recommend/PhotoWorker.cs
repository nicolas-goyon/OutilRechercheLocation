using RechercheLogement.Core.Model;
using RechercheLogement.Core.Services;
using RechercheLogement.Server.Components;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Recommend;

/// <summary>
/// Analyse des photos en tâche de fond : télécharge une miniature des premières photos de chaque bien (biens notés
/// d'abord, puis les plus récents) et calcule leurs descripteurs. Une photo à la fois, sans insister : les sites
/// d'annonces servent ces images à tous les visiteurs ; l'outil n'en demande pas plus qu'un visiteur qui parcourt
/// les annonces.
/// </summary>
public sealed partial class PhotoWorker(Catalog catalog, IPhotoStatsStore store, RecommendationService recommendations,
    IHttpClientFactory http, Photos.PhotoPrefetcher prefetcher, ILogger<PhotoWorker> log) : BackgroundService
{
    private const int DelayBetweenPhotosMs = 800;
    private const int BatchSize = 40;
    private const long MaxBytes = 5 * 1024 * 1024;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Bibliothèque native absente (image mal construite) : les recommandations se passent des photos.
        if (PhotoAnalyzer.SelfTest() is { } error)
        {
            LogUnavailable(log, error);
            return;
        }
        LogReady(log);
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); // laisse le serveur démarrer
        while (!stoppingToken.IsCancellationRequested)
        {
            var batch = NextPhotos().Take(BatchSize).ToList();
            if (batch.Count == 0)
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                continue;
            }
            var analyzed = 0;
            foreach (var url in batch)
            {
                if (await AnalyzeAsync(url, stoppingToken)) analyzed++;
                await Task.Delay(DelayBetweenPhotosMs, stoppingToken);
            }
            if (analyzed > 0) recommendations.Invalidate();
            LogBatch(log, analyzed, batch.Count);
        }
    }

    /// <summary>Photos à analyser : biens notés (ce que le moteur apprend), puis les biens vus le plus récemment.</summary>
    private IEnumerable<string> NextPhotos() =>
        catalog.QueryProperties(new PropertyQuery { Take = int.MaxValue })
            .OrderByDescending(s => s.Property.Status != PropertyStatus.None)
            .ThenByDescending(s => s.LastSeenAt)
            .SelectMany(s => (s.Photos ?? []).Take(RecommendationService.PhotosPerProperty))
            .Where(u => { store.Get(u, out var known); return !known; })
            .Distinct(StringComparer.Ordinal);

    private async Task<bool> AnalyzeAsync(string url, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Déjà dans le cache des photos : pas de téléchargement.
        if (prefetcher.Cache?.Find(url, Photos.PhotoSize.Small) is { } cached && await ReadAsync(cached, ct) is { } local)
        {
            var stats = PhotoAnalyzer.Analyze(local);
            store.Save(url, stats, now);
            return stats is not null;
        }
        try
        {
            using var client = http.CreateClient(SiteHttp.ClientName);
            using var res = await client.GetAsync(Format.Thumb(url, 240, 180), HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode || res.Content.Headers.ContentLength > MaxBytes)
            {
                store.Save(url, null, now);
                return false;
            }
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            var stats = bytes.Length <= MaxBytes ? PhotoAnalyzer.Analyze(bytes) : null;
            store.Save(url, stats, now);
            return stats is not null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            store.Save(url, null, now);
            return false;
        }
    }

    private static async Task<byte[]?> ReadAsync(string path, CancellationToken ct)
    {
        try
        {
            return await File.ReadAllBytesAsync(path, ct);
        }
        catch (IOException)
        {
            return null; // supprimée du cache entre-temps : téléchargée
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Analyse des photos prête")]
    private static partial void LogReady(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Analyse des photos indisponible : {Error}")]
    private static partial void LogUnavailable(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Photos analysées : {Analyzed}/{Count}")]
    private static partial void LogBatch(ILogger logger, int analyzed, int count);
}

public static class RecommendRegistration
{
    public static IServiceCollection AddRecommendations(this IServiceCollection services, ServerSettings settings, IWebHostEnvironment env)
    {
        var memory = settings.Storage.Equals("memory", StringComparison.OrdinalIgnoreCase);
        if (memory)
        {
            services.AddSingleton<IPhotoStatsStore, InMemoryPhotoStatsStore>();
        }
        else
        {
            var path = Path.GetFullPath(settings.DatabasePath, env.ContentRootPath); // chemin absolu : gardé tel quel
            // La base doit être migrée (table photo_stats) : IPersistence d'abord.
            services.AddSingleton<IPhotoStatsStore>(sp =>
            {
                sp.GetRequiredService<Core.Services.IPersistence>();
                return new SqlitePhotoStatsStore(path);
            });
        }
        services.AddSingleton<RecommendationService>();
        if (settings.AnalyzePhotos && !memory) services.AddHostedService<PhotoWorker>();
        return services;
    }
}
