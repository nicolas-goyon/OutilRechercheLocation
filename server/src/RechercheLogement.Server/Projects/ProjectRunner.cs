using System.Collections.Concurrent;
using RechercheLogement.Core.Contracts;
using RechercheLogement.Core.Projects;
using RechercheLogement.Core.Services;
using RechercheLogement.Server.Sites;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.Server.Projects;

/// <summary>
/// Lance un projet. Pour chaque site activé :
///  1. Zone → lieux du site. Mode "lieux" : les saisies telles quelles. Modes rayon / temps de trajet : les
///     codes postaux des communes de la zone, ou les départements si la zone est très grande ou si le site
///     ne connaît pas les codes postaux (zone plus large, filtrée ensuite chez nous).
///  2. Lieux reconnus par le site (gardés en cache dans le projet).
///  3. Recherche avec les filtres du site, puis filtre local : critères du projet et forme exacte de la zone.
///  4. Intégration : nouvelles annonces, changements, retraits, réapparitions, regroupement des doublons,
///     puis envoi au catalogue.
/// Les sites d'un projet tournent en parallèle, mais une seule recherche à la fois par site, tous projets
/// confondus (on reste discret avec chaque site). L'avancement de chaque site est visible (<see cref="Progress"/>).
/// </summary>
public sealed class ProjectRunner(ProjectStore store, Catalog catalog, SiteRegistry sites, AreaPlanner planner, ILogger<ProjectRunner> log) : IDisposable
{
    /// <summary>Au-delà, la recherche par codes postaux passe aux départements.</summary>
    private const int MaxPostalCodes = 80;
    /// <summary>Annonces récupérées au plus par site : mode lieux / zone (zone plus large, filtrée chez nous).</summary>
    private const int MaxResultsPlaces = 300;
    private const int MaxResultsArea = 1000;

    /// <summary>Une recherche à la fois par site.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _siteGates = new();
    /// <summary>Calcul de la zone d'un projet (avant ses sites).</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _planGates = new();
    /// <summary>(projet, site) en cours -> étape affichée ("lieux 12/746", "page 3"...).</summary>
    private readonly ConcurrentDictionary<(string Project, string Site), string> _progress = new();
    private readonly ConcurrentDictionary<string, string> _planning = new();
    private long _lastNotify;

    public void Dispose()
    {
        foreach (var g in _siteGates.Values.Concat(_planGates.Values)) g.Dispose();
    }

    /// <summary>Sites en cours pour ce projet, avec leur étape. Vide : rien en cours.</summary>
    public IReadOnlyDictionary<string, string> Progress(string projectId) =>
        _progress.Where(x => x.Key.Project == projectId).ToDictionary(x => x.Key.Site, x => x.Value);

    /// <summary>Résumé pour l'en-tête ("Bien'ici, SeLoger"), sinon null.</summary>
    public string? RunningSite(string projectId)
    {
        if (_planning.TryGetValue(projectId, out var step)) return step;
        var running = Progress(projectId).Keys.Select(sites.Label).ToList();
        return running.Count == 0 ? null : string.Join(", ", running);
    }

    /// <summary>Recherches en cours sur ce site : (projet, étape).</summary>
    public IReadOnlyList<(string ProjectId, string Step)> RunningOn(string site) =>
        _progress.Where(x => x.Key.Site == site).Select(x => (x.Key.Project, x.Value)).ToList();

    public bool IsRunning(string projectId, string site) => _progress.ContainsKey((projectId, site));

    /// <summary>Lance un projet. <paramref name="onlySite"/> : un seul de ses sites (relance, reprise d'une recherche en attente).</summary>
    public async Task RunAsync(string projectId, CancellationToken ct, string? onlySite = null)
    {
        var project = store.Get(projectId);
        if (project is null) return;
        if (!await PlanAsync(project, ct)) return;
        await AddFormerNamesAsync(project, ct);

        var targets = project.Sites.Distinct()
            .Where(s => onlySite is null || s == onlySite)
            .Select(sites.Get)
            .Where(e => e is { Available: true })
            .Select(e => e!)
            .Where(e => _progress.TryAdd((projectId, e.Info.Id), "en attente"))
            .ToList();
        if (targets.Count == 0) return;
        lock (project)
        {
            foreach (var t in targets) project.PlaceCache.TryAdd(t.Info.Id, []);
        }
        Notify(force: true);
        await Task.WhenAll(targets.Select(t => RunSiteGatedAsync(project, t, ct)));
    }

    /// <summary>Anciens noms des communes nouvelles : facultatif, une panne n'empêche pas la recherche.</summary>
    private async Task AddFormerNamesAsync(SearchProject project, CancellationToken ct)
    {
        try
        {
            if (await planner.AddFormerNamesAsync(project, ct)) lock (project) store.SaveQuietly(project);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Projet {Project} : anciennes communes non récupérées", project.Name);
        }
    }

    /// <summary>Zone des modes rayon et temps de trajet. false : calcul impossible (erreur notée sur chaque site).</summary>
    private async Task<bool> PlanAsync(SearchProject project, CancellationToken ct)
    {
        if (!AreaPlanner.NeedsPlan(project) || AreaPlanner.IsFresh(project, store.Now)) return true;
        var gate = _planGates.GetOrAdd(project.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (AreaPlanner.IsFresh(project, store.Now)) return true;
            _planning[project.Id] = "calcul de la zone";
            Notify(force: true);
            project.Area = await planner.BuildAsync(project, store.Now, ct);
            lock (project)
            {
                project.PlaceCache.Clear(); // nouvelle zone : nouveaux lieux à reconnaître
                store.SaveQuietly(project);
            }
            return true;
        }
        catch (SiteException e)
        {
            foreach (var siteId in project.Sites) store.AddRun(new ProjectRun(project.Id, siteId, store.Now, false, 0, 0, $"Zone : {e.Message}"));
            return false;
        }
        finally
        {
            _planning.TryRemove(project.Id, out _);
            gate.Release();
            Notify(force: true);
        }
    }

    private async Task RunSiteGatedAsync(SearchProject project, SiteEntry entry, CancellationToken ct)
    {
        var key = (project.Id, entry.Info.Id);
        var gate = _siteGates.GetOrAdd(entry.Info.Id, _ => new SemaphoreSlim(1, 1));
        var acquired = false;
        try
        {
            if (!gate.Wait(0, ct))
            {
                SetProgress(key, "en attente d'une autre recherche sur ce site", force: true);
                await gate.WaitAsync(ct);
            }
            acquired = true;
            SetProgress(key, "démarrage", force: true);
            await RunSiteAsync(project, entry.Info, entry.Module!.Search!, step => SetProgress(key, step), ct);
        }
        finally
        {
            if (acquired) gate.Release();
            _progress.TryRemove(key, out _);
            Notify(force: true);
        }
    }

    private void SetProgress((string, string) key, string step, bool force = false)
    {
        _progress[key] = step;
        Notify(force);
    }

    /// <summary>Prévient les pages ouvertes, au plus deux fois par seconde (avancement).</summary>
    private void Notify(bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastNotify) < 500) return;
        Interlocked.Exchange(ref _lastNotify, now);
        store.NotifyRunning();
    }

    /// <summary>Un onglet vient de s'ouvrir : relance les recherches de ce site restées en attente d'un onglet.</summary>
    public async Task ResumeWaitingAsync(string site, CancellationToken ct)
    {
        var waiting = store.Projects()
            .Where(p => p.Active && store.Runs(p.Id).Any(r => r.Site == site && r.Message.StartsWith(BrowserRelay.NoTabPrefix, StringComparison.Ordinal)))
            .Select(p => p.Id).ToList();
        foreach (var id in waiting) await RunAsync(id, ct, onlySite: site);
    }

    /// <summary>Lieux à demander au site, selon le mode de zone et ce que le site sait chercher.</summary>
    public static (List<PlaceQuery> Queries, string How) PlaceQueries(SearchProject p, PlaceKinds supported)
    {
        if (p.LocationMode == LocationMode.Places || p.Area is null)
            return (p.Locations.SelectMany(ToQueries).ToList(), "lieux saisis");
        var postalCodes = p.Area.PostalCodes();
        if (supported.HasFlag(PlaceKinds.PostalCode) && postalCodes.Count is > 0 and <= MaxPostalCodes)
            return (postalCodes.Select(cp => new PlaceQuery(PlaceKind.PostalCode, cp)).ToList(), $"{postalCodes.Count} code(s) postal(aux) de la zone");
        if (supported.HasFlag(PlaceKinds.Department))
            return (p.Area.Departments.Values.Select(d => new PlaceQuery(PlaceKind.Department, d)).ToList(), $"département(s) {string.Join(", ", p.Area.Departments.Values)}");
        return (postalCodes.Select(cp => new PlaceQuery(PlaceKind.PostalCode, cp)).ToList(), $"{postalCodes.Count} code(s) postal(aux)");
    }

    /// <summary>Lieu du projet -> lieux à demander au site. Une région est demandée département par département.</summary>
    public static IEnumerable<PlaceQuery> ToQueries(ProjectLocation l) =>
        l is { Type: PlaceType.Region, Departments.Count: > 0 }
            ? l.Departments.Select(d => new PlaceQuery(PlaceKind.Department, d))
            : [ToQuery(l)];

    /// <summary>Lieu du projet -> lieu à demander au site.</summary>
    public static PlaceQuery ToQuery(ProjectLocation l) => l.Type switch
    {
        PlaceType.PostalCode => new PlaceQuery(PlaceKind.PostalCode, l.Code ?? l.Query),
        PlaceType.Department => new PlaceQuery(PlaceKind.Department, l.Query),
        PlaceType.Commune => new PlaceQuery(PlaceKind.Auto, l.Query, l.PostalCodes.FirstOrDefault()),
        _ => new PlaceQuery(PlaceKind.Auto, l.Query),
    };

    private async Task RunSiteAsync(SearchProject project, SiteInfo site, ISearchAdapter search, Action<string> progress, CancellationToken ct)
    {
        var started = store.Now;
        try
        {
            // 1-2. Lieux du site (cache du projet).
            var (asked, how) = PlaceQueries(project, search.SupportedPlaces);
            // Lieux tels que le site les cherche ; plusieurs communes d'un même code postal = une seule requête.
            var groups = asked.GroupBy(q => search.Prepare(q).CacheKey).ToList();
            var queries = groups.Select(g => search.Prepare(g.First())).ToList();
            var names = groups.Select(g => string.Join(", ", g.Select(q => q.Text).Distinct())).ToList();
            // Le cache d'un site n'est modifié que par ce site ; le verrou protège l'enregistrement du projet, qui
            // lit les caches de tous les sites.
            Dictionary<string, CachedPlace?> cache;
            lock (project) cache = project.PlaceCache.TryGetValue(site.Id, out var c) ? c : project.PlaceCache[site.Id] = [];
            var ids = new List<string>();
            var unknown = new List<string>();
            var cacheChanged = false;
            var done = 0;
            for (var i = 0; i < queries.Count; i++)
            {
                var q = queries[i];
                done++;
                CachedPlace? place;
                bool known;
                // Lieu non reconnu la dernière fois : redemandé (le site ou la règle de choix ont pu changer).
                lock (project) known = cache.TryGetValue(q.CacheKey, out place) && place is not null;
                if (!known)
                {
                    progress($"reconnaissance des lieux {done}/{queries.Count}");
                    place = await search.ResolvePlaceAsync(q, project.Transaction, ct);
                    lock (project) cache[q.CacheKey] = place;
                    cacheChanged = true;
                    // Lieux déjà reconnus gardés même si la suite échoue (onglet fermé...) : la reprise repart d'ici.
                    if (done % 20 == 0) lock (project) store.SaveQuietly(project);
                    await Task.Delay(200, ct);
                }
                if (place is null) unknown.Add(names[i]);
                else ids.AddRange(place.Ids);
            }
            // Lieux qui ne sont plus dans le projet (ou anciennes clés) : retirés du cache.
            var keys = queries.Select(q => q.CacheKey).ToHashSet();
            lock (project)
            {
                foreach (var stale in cache.Keys.Where(k => !keys.Contains(k)).ToList())
                {
                    cache.Remove(stale);
                    cacheChanged = true;
                }
                if (cacheChanged) store.SaveQuietly(project);
            }
            if (ids.Count == 0)
            {
                store.AddRun(new ProjectRun(project.Id, site.Id, started, false, 0, 0,
                    unknown.Count > 0 ? $"Lieu non reconnu par {site.Label} : {string.Join(", ", unknown.Take(5))}." : "Aucun lieu dans le projet."));
                return;
            }

            // 3. Recherche, puis zone exacte.
            var max = project.LocationMode == LocationMode.Places ? MaxResultsPlaces : MaxResultsArea;
            progress("recherche des annonces");
            var outcome = await search.SearchAsync(new SearchRequest(project, ids.Distinct().ToList(), max, progress), ct);
            progress("enregistrement");
            var incoming = new List<IncomingListing>();
            var outside = 0;
            foreach (var item in outcome.Items)
            {
                double? distance = null;
                if (project.LocationMode != LocationMode.Places && project.Area is { } area)
                {
                    if (AreaFilter.Check(area, item.Data, out distance) == AreaFilter.Verdict.Outside)
                    {
                        outside++;
                        continue;
                    }
                }
                else if (project.LocationMode == LocationMode.Places && ProjectScope.OutsidePlaces(project, item.Data))
                {
                    outside++;
                    continue;
                }
                incoming.Add(new IncomingListing(item.SiteId, item.Data, distance));
            }

            // 4. Intégration et suivi.
            var applied = store.ApplyResults(project.Id, site.Id, incoming, outcome.Complete, started);
            // Mêmes statuts et mêmes doublons que les annonces vues avec le plugin.
            catalog.Sync(new SyncRequest("projet", incoming.Select(i => new Observation(site.Id, i.SiteId, "api", i.Data, started)).ToList(), null, null));
            var parts = new List<string> { $"{applied.Kept} annonce(s) retenue(s) sur {outcome.Items.Count} reçue(s)" };
            if (applied.New > 0) parts.Add($"{applied.New} nouvelle(s)");
            if (applied.Changed > 0) parts.Add($"{applied.Changed} modifiée(s)");
            if (applied.Removed > 0) parts.Add($"{applied.Removed} retirée(s)");
            if (applied.Reappeared > 0) parts.Add($"{applied.Reappeared} réapparue(s)");
            if (outside > 0) parts.Add($"{outside} hors zone écartée(s)");
            var message = $"{string.Join(", ", parts)}. Recherche par {how}.";
            if (!outcome.Complete) message += $" Limite atteinte : {outcome.Items.Count} annonces les plus récentes sur {outcome.Total} (retraits non détectés).";
            if (unknown.Count > 0) message += $" Lieu(x) non reconnu(s) : {string.Join(", ", unknown.Take(5))}{(unknown.Count > 5 ? "…" : "")}.";
            store.AddRun(new ProjectRun(project.Id, site.Id, started, true, applied.Kept, applied.New, message));
        }
        catch (SiteException e)
        {
            log.LogWarning("Projet {Project}, {Site} : {Message}", project.Name, site.Label, e.Message);
            store.AddRun(new ProjectRun(project.Id, site.Id, started, false, 0, 0, e.Message));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Projet {Project}, {Site} : erreur inattendue", project.Name, site.Label);
            store.AddRun(new ProjectRun(project.Id, site.Id, started, false, 0, 0, $"Erreur inattendue : {e.Message}"));
        }
    }
}

/// <summary>Lance automatiquement les projets dont l'intervalle (AutoRefreshHours) est écoulé. Vérification chaque minute.</summary>
public sealed class ProjectScheduler(ProjectRunner runner, ProjectStore store, BrowserRelay relay, ILogger<ProjectScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void Resume(string site) => _ = Task.Run(async () =>
        {
            try
            {
                await runner.ResumeWaitingAsync(site, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Reprise des recherches en attente de {Site}", site);
            }
        }, stoppingToken);
        relay.SiteConnected += Resume;
        using var unsubscribe = new Unsubscribe(() => relay.SiteConnected -= Resume);

        // Laisse le serveur démarrer avant le premier passage.
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            foreach (var p in store.Projects().Where(p => p.IsDue(store.Now)))
            {
                try
                {
                    await runner.RunAsync(p.Id, stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogError(e, "Lancement automatique du projet {Project}", p.Name);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

file sealed class Unsubscribe(Action action) : IDisposable
{
    public void Dispose() => action();
}
