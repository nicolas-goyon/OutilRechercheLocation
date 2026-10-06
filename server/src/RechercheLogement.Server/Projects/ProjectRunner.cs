using System.Collections.Concurrent;
using RechercheLogement.Core.Projects;

namespace RechercheLogement.Server.Projects;

/// <summary>
/// Lance un projet : pour chaque site activé, résout les lieux (une fois, puis gardés dans le projet),
/// interroge le site, garde les annonces conformes aux critères et note le passage (OK ou erreur lisible).
/// Un seul lancement à la fois (on reste discret avec les sites).
/// </summary>
public sealed class ProjectRunner(ProjectStore store, IEnumerable<ISiteCollector> collectors, ILogger<ProjectRunner> log)
{
    private readonly Dictionary<string, ISiteCollector> _collectors = collectors.ToDictionary(c => c.SiteId);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _running = new();

    /// <summary>Site en cours d'interrogation pour ce projet (pour la page), sinon null.</summary>
    public string? RunningSite(string projectId) => _running.GetValueOrDefault(projectId);

    public bool HasCollector(string siteId) => _collectors.ContainsKey(siteId);

    public async Task RunAsync(string projectId, CancellationToken ct)
    {
        if (_running.ContainsKey(projectId)) return;
        _running[projectId] = "…";
        store.NotifyRunning();
        await _gate.WaitAsync(ct);
        try
        {
            var project = store.Get(projectId);
            if (project is null) return;
            foreach (var siteId in project.Sites.Distinct())
            {
                if (SourceSites.Get(siteId) is not { Available: true } site || !_collectors.TryGetValue(siteId, out var collector)) continue;
                _running[projectId] = site.Label;
                store.NotifyRunning();
                await RunSiteAsync(project, site, collector, ct);
            }
        }
        finally
        {
            _running.TryRemove(projectId, out _);
            _gate.Release();
            store.NotifyRunning();
        }
    }

    private async Task RunSiteAsync(SearchProject project, SourceSite site, ISiteCollector collector, CancellationToken ct)
    {
        var now = store.Now;
        try
        {
            var ids = new List<string>();
            var unknown = new List<string>();
            var resolved = false;
            foreach (var loc in project.Locations)
            {
                if (!loc.SiteIds.TryGetValue(site.Id, out var known) || known.Count == 0)
                {
                    var match = await collector.ResolveLocationAsync(loc.Query, project.Transaction, ct);
                    if (match is null)
                    {
                        unknown.Add(loc.Query);
                        continue;
                    }
                    loc.SiteIds[site.Id] = known = match.Ids;
                    loc.Labels[site.Id] = match.Label;
                    resolved = true;
                }
                ids.AddRange(known);
            }
            if (resolved) store.Save(project);
            if (ids.Count == 0)
            {
                store.AddRun(new ProjectRun(project.Id, site.Id, now, false, 0, 0,
                    unknown.Count > 0 ? $"Lieu non reconnu par {site.Label} : {string.Join(", ", unknown)}." : "Aucun lieu dans le projet."));
                return;
            }

            var items = await collector.SearchAsync(project, ids.Distinct().ToList(), ct);
            var outcome = store.ApplyResults(project.Id, site.Id, items.Select(i => (i.SiteId, i.Data)));
            var message = $"{outcome.Kept} annonce(s) conforme(s) sur {outcome.Found}, dont {outcome.New} nouvelle(s).";
            if (unknown.Count > 0) message += $" Lieu non reconnu : {string.Join(", ", unknown)}.";
            store.AddRun(new ProjectRun(project.Id, site.Id, now, true, outcome.Kept, outcome.New, message));
        }
        catch (CollectorException e)
        {
            log.LogWarning("Projet {Project}, {Site} : {Message}", project.Name, site.Label, e.Message);
            store.AddRun(new ProjectRun(project.Id, site.Id, now, false, 0, 0, e.Message));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Projet {Project}, {Site} : erreur inattendue", project.Name, site.Label);
            store.AddRun(new ProjectRun(project.Id, site.Id, now, false, 0, 0, $"Erreur inattendue : {e.Message}"));
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
