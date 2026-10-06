using System.Collections.Concurrent;
using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Sites;

namespace RechercheLogement.Server.Projects;

/// <summary>
/// Lance un projet. Pour chaque site activé :
///  1. Zone → lieux du site. Mode "lieux" : les saisies telles quelles. Modes rayon / temps de trajet : les
///     codes postaux des communes de la zone, ou les départements si la zone est très grande ou si le site
///     ne connaît pas les codes postaux (zone plus large, filtrée ensuite chez nous).
///  2. Lieux reconnus par le site (gardés en cache dans le projet).
///  3. Recherche avec les filtres du site, puis filtre local : critères du projet et forme exacte de la zone.
///  4. Intégration : nouvelles annonces, changements, retraits, réapparitions, regroupement des doublons.
/// Un seul lancement à la fois (on reste discret avec les sites).
/// </summary>
public sealed class ProjectRunner(ProjectStore store, SiteRegistry sites, AreaPlanner planner, ILogger<ProjectRunner> log)
{
    /// <summary>Au-delà, la recherche par codes postaux passe aux départements.</summary>
    private const int MaxPostalCodes = 80;
    /// <summary>Annonces récupérées au plus par site : mode lieux / zone (zone plus large, filtrée chez nous).</summary>
    private const int MaxResultsPlaces = 300;
    private const int MaxResultsArea = 1000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _running = new();

    /// <summary>Étape en cours pour ce projet (pour la page), sinon null.</summary>
    public string? RunningSite(string projectId) => _running.GetValueOrDefault(projectId);

    public async Task RunAsync(string projectId, CancellationToken ct)
    {
        if (!_running.TryAdd(projectId, "en attente")) return;
        store.NotifyRunning();
        await _gate.WaitAsync(ct);
        try
        {
            var project = store.Get(projectId);
            if (project is null) return;

            if (AreaPlanner.NeedsPlan(project) && !AreaPlanner.IsFresh(project, store.Now))
            {
                SetStep(projectId, "calcul de la zone");
                try
                {
                    project.Area = await planner.BuildAsync(project, store.Now, ct);
                    project.PlaceCache.Clear(); // nouvelle zone : nouveaux lieux à reconnaître
                    store.SaveQuietly(project);
                }
                catch (SiteException e)
                {
                    foreach (var siteId in project.Sites) store.AddRun(new ProjectRun(project.Id, siteId, store.Now, false, 0, 0, $"Zone : {e.Message}"));
                    return;
                }
            }

            foreach (var siteId in project.Sites.Distinct())
            {
                if (sites.Get(siteId) is not { Available: true } entry) continue;
                SetStep(projectId, entry.Info.Label);
                await RunSiteAsync(project, entry.Info, entry.Module!.Search!, ct);
            }
        }
        finally
        {
            _running.TryRemove(projectId, out _);
            _gate.Release();
            store.NotifyRunning();
        }
    }

    private void SetStep(string projectId, string step)
    {
        _running[projectId] = step;
        store.NotifyRunning();
    }

    /// <summary>Lieux à demander au site, selon le mode de zone et ce que le site sait chercher.</summary>
    public static (List<PlaceQuery> Queries, string How) PlaceQueries(SearchProject p, PlaceKinds supported)
    {
        if (p.LocationMode == LocationMode.Places || p.Area is null)
            return (p.Locations.Select(ToQuery).ToList(), "lieux saisis");
        var postalCodes = p.Area.PostalCodes();
        if (supported.HasFlag(PlaceKinds.PostalCode) && postalCodes.Count is > 0 and <= MaxPostalCodes)
            return (postalCodes.Select(cp => new PlaceQuery(PlaceKind.PostalCode, cp)).ToList(), $"{postalCodes.Count} code(s) postal(aux) de la zone");
        if (supported.HasFlag(PlaceKinds.Department))
            return (p.Area.Departments.Values.Select(d => new PlaceQuery(PlaceKind.Department, d)).ToList(), $"département(s) {string.Join(", ", p.Area.Departments.Values)}");
        return (postalCodes.Select(cp => new PlaceQuery(PlaceKind.PostalCode, cp)).ToList(), $"{postalCodes.Count} code(s) postal(aux)");
    }

    /// <summary>Lieu du projet -> lieu à demander au site.</summary>
    public static PlaceQuery ToQuery(ProjectLocation l) => l.Type switch
    {
        PlaceType.PostalCode => new PlaceQuery(PlaceKind.PostalCode, l.Code ?? l.Query),
        PlaceType.Department => new PlaceQuery(PlaceKind.Department, l.Query),
        PlaceType.Commune => new PlaceQuery(PlaceKind.Auto, l.Query, l.PostalCodes.FirstOrDefault()),
        _ => new PlaceQuery(PlaceKind.Auto, l.Query),
    };

    private async Task RunSiteAsync(SearchProject project, SiteInfo site, ISearchAdapter search, CancellationToken ct)
    {
        var started = store.Now;
        try
        {
            // 1-2. Lieux du site (cache du projet).
            var (queries, how) = PlaceQueries(project, search.SupportedPlaces);
            if (!project.PlaceCache.TryGetValue(site.Id, out var cache)) project.PlaceCache[site.Id] = cache = [];
            var ids = new List<string>();
            var unknown = new List<string>();
            var cacheChanged = false;
            foreach (var q in queries)
            {
                if (!cache.TryGetValue(q.CacheKey, out var place))
                {
                    place = await search.ResolvePlaceAsync(q, project.Transaction, ct);
                    cache[q.CacheKey] = place;
                    cacheChanged = true;
                    await Task.Delay(200, ct);
                }
                if (place is null) unknown.Add(q.Text);
                else ids.AddRange(place.Ids);
            }
            if (cacheChanged) store.SaveQuietly(project);
            if (ids.Count == 0)
            {
                store.AddRun(new ProjectRun(project.Id, site.Id, started, false, 0, 0,
                    unknown.Count > 0 ? $"Lieu non reconnu par {site.Label} : {string.Join(", ", unknown.Take(5))}." : "Aucun lieu dans le projet."));
                return;
            }

            // 3. Recherche, puis zone exacte.
            var max = project.LocationMode == LocationMode.Places ? MaxResultsPlaces : MaxResultsArea;
            var outcome = await search.SearchAsync(new SearchRequest(project, ids.Distinct().ToList(), max), ct);
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
                incoming.Add(new IncomingListing(item.SiteId, item.Data, distance));
            }

            // 4. Intégration et suivi.
            var applied = store.ApplyResults(project.Id, site.Id, incoming, outcome.Complete, started);
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
public sealed class ProjectScheduler(ProjectRunner runner, ProjectStore store, ILogger<ProjectScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
