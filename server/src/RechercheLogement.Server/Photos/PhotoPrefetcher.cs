using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;
using RechercheLogement.Core.Services;
using RechercheLogement.Server.Projects;
using RechercheLogement.Server.Recommend;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Photos;

/// <summary>Biens dont il faut des photos, par priorité (0 : la plus haute).</summary>
public static class PhotoPlan
{
    /// <summary>Photos gardées par bien ; après un défilement du carrousel ou de la galerie : <see cref="DeepDepth"/>.</summary>
    public const int Depth = 5;
    public const int DeepDepth = 10;

    /// <summary>Niveaux : bien ouvert, biens affichés à l'écran, liste de la page, recommandations du projet.</summary>
    public const int Focus = 0, OnScreen = 1, List = 2, Recommended = 3;

    public sealed record Wanted(string PropertyId, IReadOnlyList<string> Photos, int Depth, int Level);

    /// <summary>
    /// Adresses à télécharger, dans l'ordre : par niveau, puis photo par photo (la 1re photo de chaque bien du
    /// niveau, puis la 2e…) pour que toutes les vignettes visibles arrivent vite. <paramref name="skip"/> : déjà en
    /// cache, ou échec récent.
    /// </summary>
    public static IEnumerable<(string Url, int Level)> Urls(IEnumerable<Wanted> wanted, Func<string, bool> skip)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var level in wanted.GroupBy(w => w.Level).OrderBy(g => g.Key))
        {
            var items = level.ToList();
            var rounds = items.Count == 0 ? 0 : items.Max(w => Math.Min(w.Depth, w.Photos.Count));
            for (var i = 0; i < rounds; i++)
            {
                var round = i;
                var urls = items.Where(w => round < Math.Min(w.Depth, w.Photos.Count)).Select(w => w.Photos[round]);
                foreach (var url in urls.Where(u => seen.Add(u) && !skip(u))) yield return (url, level.Key);
            }
        }
    }

    /// <summary>
    /// Rang de suppression d'une photo en cache (le plus petit part en premier) : photos de biens disparus, puis de
    /// biens « pas intéressé », puis vus ou à contacter, puis au-delà des 5 premières ; à rang égal, la plus
    /// anciennement regardée.
    /// </summary>
    public static double EvictionRank(PropertyStatus? status, int photoIndex, long lastAccessMs, long nowMs)
    {
        var group = status switch
        {
            null => 0,
            PropertyStatus.Rejected => 1,
            PropertyStatus.None => photoIndex < Depth ? 4 : 3,
            _ => 2,
        };
        return group + 0.99 * Math.Clamp((double)lastAccessMs / Math.Max(1, nowMs), 0, 1);
    }
}

/// <summary>
/// Téléchargement des photos en tâche de fond, vers <see cref="PhotoCache"/>. Les pages disent ce que l'on regarde :
///  1. le bien ouvert (<see cref="FocusProperty"/>) ;
///  2. les biens dont le navigateur vient de demander une photo absente du cache (<see cref="OnScreen"/> : les
///     images ne sont chargées qu'à l'approche de l'écran), et ceux dont on fait défiler le carrousel ;
///  3. les biens de la liste affichée (<see cref="ShowList"/>) ;
///  4. sinon, les biens non notés du dernier projet ouvert, du plus au moins recommandé.
/// Les 5 premières photos de chaque bien (10 après un défilement du carrousel). Les biens vus, à contacter ou
/// refusés ne sont téléchargés que si on les regarde (niveaux 1 et 2).
///
/// Sans insister auprès des sites : une photo à la fois, au plus une toutes les 2 s par site en tâche de fond
/// (0,3 s pour ce que l'on regarde : un visiteur qui ouvre l'annonce en charge autant).
/// </summary>
public sealed partial class PhotoPrefetcher : BackgroundService
{
    private const long SignalLifetimeMs = 15 * 60 * 1000;
    private const int MaxOnScreen = 60;
    private const int MaxList = 80;
    private const int MaxRecommended = 300;
    private const long MaxDownloadBytes = 15 * 1024 * 1024;
    private static readonly TimeSpan BackgroundSpacing = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ForegroundSpacing = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan FailureRetry = TimeSpan.FromDays(1);

    private readonly Catalog _catalog;
    private readonly ProjectStore _projects;
    private readonly RecommendationService _recommendations;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<PhotoPrefetcher> _log;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly Dictionary<string, DateTimeOffset> _failed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _hostFree = new(StringComparer.OrdinalIgnoreCase);

    private (string Id, long At)? _focus;
    private readonly List<(string Id, long At)> _onScreen = [];
    private readonly Dictionary<string, long> _deep = new(StringComparer.Ordinal);
    private (List<string> Ids, long At) _list = ([], 0);
    private string? _projectId;
    private (List<string> Ids, long At, string? Project) _recommended = ([], 0, null);
    private Snapshot? _snapshot;

    /// <summary>Biens du catalogue par identifiant, et photo -> bien et rang de la photo (refait quand le catalogue change).</summary>
    private sealed record Snapshot(Dictionary<string, PropertySummary> Properties, Dictionary<string, (string PropertyId, int Index)> Photos);

    public PhotoPrefetcher(PhotoCache? cache, Catalog catalog, ProjectStore projects, RecommendationService recommendations,
        IHttpClientFactory http, ILogger<PhotoPrefetcher> log)
    {
        Cache = cache;
        _catalog = catalog;
        _projects = projects;
        _recommendations = recommendations;
        _http = http;
        _log = log;
        _catalog.Changed += OnCatalogChanged;
    }

    /// <summary>Attente avant le premier téléchargement (le serveur finit de démarrer).</summary>
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>null : cache désactivé (stockage en mémoire, ou réglage PhotoCache à false).</summary>
    public PhotoCache? Cache { get; }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ------------------------------------------------------------------ signaux des pages

    /// <summary>Fiche d'un bien ouverte.</summary>
    public void FocusProperty(string propertyId)
    {
        if (Cache is null) return;
        lock (_lock) _focus = (propertyId, Now);
        Wake();
    }

    /// <summary>Carrousel ou galerie défilés : jusqu'à 10 photos pour ce bien, et il passe devant.</summary>
    public void Deepen(string propertyId)
    {
        if (Cache is null) return;
        lock (_lock)
        {
            var known = _deep.ContainsKey(propertyId);
            _deep[propertyId] = Now;
            if (known && _onScreen.Count > 0 && _onScreen[0].Id == propertyId) return;
            PushOnScreen(propertyId);
        }
        Wake();
    }

    /// <summary>Le navigateur a demandé une photo absente du cache : son bien est à l'écran.</summary>
    public void OnScreen(string photoUrl)
    {
        if (Cache is null) return;
        if (!Current().Photos.TryGetValue(photoUrl, out var p)) return;
        lock (_lock) PushOnScreen(p.PropertyId);
        Wake();
    }

    /// <summary>Liste affichée par une page (dans l'ordre de la page), et projet choisi.</summary>
    public void ShowList(IEnumerable<string> propertyIds, string? projectId)
    {
        if (Cache is null) return;
        lock (_lock)
        {
            _list = (propertyIds.Take(MaxList).ToList(), Now);
            if (projectId is not null) _projectId = projectId;
        }
        Wake();
    }

    /// <summary>Projet choisi dans la barre latérale : ses biens les mieux recommandés passent en tâche de fond.</summary>
    public void UseProject(string? projectId)
    {
        if (Cache is null || projectId is null) return;
        lock (_lock) _projectId = projectId;
        Wake();
    }

    private void PushOnScreen(string id)
    {
        _onScreen.RemoveAll(x => x.Id == id);
        _onScreen.Insert(0, (id, Now));
        if (_onScreen.Count > MaxOnScreen) _onScreen.RemoveRange(MaxOnScreen, _onScreen.Count - MaxOnScreen);
    }

    private void Wake()
    {
        try
        {
            if (_signal.CurrentCount == 0) _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Déjà réveillé.
        }
    }

    private void OnCatalogChanged()
    {
        lock (_lock)
        {
            _snapshot = null;
            // Catalogue encore vide au calcul précédent (démarrage) : la liste est refaite tout de suite.
            if (_recommended.Ids.Count == 0) _recommended = ([], 0, _recommended.Project);
        }
        Wake();
    }

    private Snapshot Current()
    {
        lock (_lock)
        {
            if (_snapshot is not null) return _snapshot;
        }
        var properties = new Dictionary<string, PropertySummary>(StringComparer.Ordinal);
        var photos = new Dictionary<string, (string, int)>(StringComparer.Ordinal);
        foreach (var s in _catalog.QueryProperties(new PropertyQuery { Take = int.MaxValue }))
        {
            properties[s.Property.Id] = s;
            var list = s.Photos ?? [];
            for (var i = 0; i < list.Count; i++) photos.TryAdd(list[i], (s.Property.Id, i));
        }
        var snapshot = new Snapshot(properties, photos);
        lock (_lock) return _snapshot = snapshot;
    }

    // ------------------------------------------------------------------ boucle

    /// <summary>Biens voulus, du plus au moins prioritaire (signaux expirés après 15 min).</summary>
    public List<PhotoPlan.Wanted> Wanted()
    {
        var now = Now;
        (string Id, long At)? focus;
        List<string> screen, list;
        Dictionary<string, long> deep;
        string? project;
        lock (_lock)
        {
            focus = _focus is { } f && now - f.At < SignalLifetimeMs ? f : null;
            screen = _onScreen.Where(x => now - x.At < SignalLifetimeMs).Select(x => x.Id).ToList();
            list = now - _list.At < SignalLifetimeMs ? [.. _list.Ids] : [];
            deep = _deep.Where(kv => now - kv.Value < SignalLifetimeMs * 4).ToDictionary();
            project = _projectId;
        }

        var wanted = new List<PhotoPlan.Wanted>();
        var properties = Current().Properties;
        void Add(string id, int level, bool anyStatus)
        {
            if (!properties.TryGetValue(id, out var s)) return;
            if (!anyStatus && s.Property.Status != PropertyStatus.None) return;
            wanted.Add(new PhotoPlan.Wanted(id, s.Photos ?? [], deep.ContainsKey(id) ? PhotoPlan.DeepDepth : PhotoPlan.Depth, level));
        }
        if (focus is { } fo) Add(fo.Id, PhotoPlan.Focus, anyStatus: true);
        foreach (var id in screen) Add(id, PhotoPlan.OnScreen, anyStatus: true);
        foreach (var id in list) Add(id, PhotoPlan.List, anyStatus: false);
        foreach (var id in RecommendedIds(project, now)) Add(id, PhotoPlan.Recommended, anyStatus: false);
        return wanted;
    }

    /// <summary>Biens non notés du projet, du plus au moins recommandé ; refait toutes les 5 min.</summary>
    private List<string> RecommendedIds(string? projectId, long now)
    {
        lock (_lock)
        {
            if (_recommended.Project == projectId && now - _recommended.At < 5 * 60 * 1000) return _recommended.Ids;
        }
        Func<Listing, bool>? scope = null;
        if (projectId is not null && _projects.Get(projectId) is not null)
        {
            var selection = new ProjectSelection(_projects);
            selection.Select(projectId);
            scope = selection.Scope;
        }
        var ids = _recommendations.Recommend(scope, MaxRecommended).Items.Select(r => r.Item.Property.Id).ToList();
        if (ids.Count == 0) // pas encore assez de biens notés : les plus récents
        {
            ids = _catalog.QueryProperties(new PropertyQuery { Statuses = new HashSet<PropertyStatus> { PropertyStatus.None }, Scope = scope, Take = MaxRecommended })
                .Select(s => s.Property.Id).ToList();
        }
        lock (_lock) _recommended = (ids, now, projectId);
        return ids;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Cache is null) return;
        LogReady(_log, Cache.Count, Cache.TotalBytes / (1024 * 1024));
        await Task.Delay(StartDelay, stoppingToken);
        var lastMaintenance = DateTimeOffset.MinValue;
        var stored = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow - lastMaintenance > TimeSpan.FromHours(1) || stored >= 50)
            {
                Maintain();
                lastMaintenance = DateTimeOffset.UtcNow;
                stored = 0;
            }

            var (next, wait) = Next();
            if (next is { } n)
            {
                if (await DownloadAsync(n.Url, stoppingToken)) stored++;
                lock (_lock) _hostFree[Host(n.Url)] = DateTimeOffset.UtcNow + (n.Level <= PhotoPlan.OnScreen ? ForegroundSpacing : BackgroundSpacing);
                continue;
            }
            // Rien à faire tout de suite : attend un signal d'une page, ou que le site suivant soit disponible.
            await _signal.WaitAsync(wait, stoppingToken);
        }
    }

    /// <summary>Photo suivante dont le site est disponible ; sinon le temps à attendre.</summary>
    private ((string Url, int Level)? Next, TimeSpan Wait) Next()
    {
        if (Cache is not { } cache) return (null, TimeSpan.FromMinutes(5));
        var now = DateTimeOffset.UtcNow;
        var wait = TimeSpan.FromMinutes(5);
        bool Skip(string url)
        {
            if (cache.Contains(url)) return true;
            lock (_lock) return _failed.TryGetValue(url, out var at) && now - at < FailureRetry;
        }
        // Le cache plein ne se remplit plus de photos de fond : seulement de ce que l'on regarde.
        var full = cache.TotalBytes >= cache.MaxBytes * 0.95;
        var checkedUrls = 0;
        foreach (var candidate in PhotoPlan.Urls(Wanted(), Skip))
        {
            if (full && candidate.Level >= PhotoPlan.List) break;
            if (!Uri.TryCreate(candidate.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) continue;
            DateTimeOffset free;
            lock (_lock) free = _hostFree.GetValueOrDefault(uri.Host);
            if (free <= now) return (candidate, TimeSpan.Zero);
            if (free - now < wait) wait = free - now;
            if (++checkedUrls > 500) break;
        }
        return (null, wait);
    }

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";

    private async Task<bool> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Clear();
            req.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/*,*/*;q=0.8");
            using var res = await _http.CreateClient(SiteHttp.ClientName).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode || res.Content.Headers.ContentLength > MaxDownloadBytes) return Failed(url);
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length <= MaxDownloadBytes && Cache!.Store(url, bytes) || Failed(url);
        }
        catch (Exception e) when (e is HttpRequestException or IOException || e is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Failed(url);
        }
    }

    private bool Failed(string url)
    {
        lock (_lock) _failed[url] = DateTimeOffset.UtcNow;
        return false;
    }

    /// <summary>
    /// Ménage : photos de biens refusés non regardées depuis 7 jours, puis, au-delà de la place permise, ce qui sert
    /// le moins (<see cref="PhotoPlan.EvictionRank"/>) jusqu'à 90 % de la limite.
    /// </summary>
    public void Maintain()
    {
        if (Cache is not { } cache) return;
        var now = Now;
        var snapshot = Current();
        var byKey = new Dictionary<string, (PropertyStatus Status, int Index)>(StringComparer.Ordinal);
        foreach (var (url, (id, index)) in snapshot.Photos)
            byKey.TryAdd(PhotoCache.Key(url), (snapshot.Properties[id].Property.Status, index));
        var week = 7L * 24 * 3600 * 1000;
        var rejected = cache.RemoveWhere((key, at) => byKey.TryGetValue(key, out var p) && p.Status == PropertyStatus.Rejected && now - at > week);
        var evicted = cache.Evict(cache.TotalBytes > cache.MaxBytes ? (long)(cache.MaxBytes * 0.9) : cache.MaxBytes,
            (key, at) => byKey.TryGetValue(key, out var p) ? PhotoPlan.EvictionRank(p.Status, p.Index, at, now) : PhotoPlan.EvictionRank(null, 0, at, now));
        lock (_lock)
        {
            foreach (var old in _failed.Where(kv => DateTimeOffset.UtcNow - kv.Value > FailureRetry).Select(kv => kv.Key).ToList()) _failed.Remove(old);
        }
        if (rejected + evicted > 0) LogCleaned(_log, rejected + evicted, cache.TotalBytes / (1024 * 1024));
    }

    public override void Dispose()
    {
        _catalog.Changed -= OnCatalogChanged;
        _signal.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cache des photos : {Count} photo(s), {Megabytes} Mo")]
    private static partial void LogReady(ILogger logger, int count, long megabytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cache des photos : {Count} photo(s) supprimée(s), {Megabytes} Mo")]
    private static partial void LogCleaned(ILogger logger, int count, long megabytes);
}
