using RechercheLogement.Core.Model;

namespace RechercheLogement.Core.Projects;

/// <summary>Contenu de la base des projets (séparée de la base du catalogue).</summary>
public sealed class ProjectSnapshot
{
    public List<SearchProject> Projects { get; init; } = [];
    public List<ProjectResult> Results { get; init; } = [];
    public List<ProjectRun> Runs { get; init; } = [];
}

/// <summary>Stockage durable des projets. Implémentations : SQLite (serveur), mémoire (tests).</summary>
public interface IProjectPersistence
{
    ProjectSnapshot Load();
    void SaveProject(SearchProject project);
    void DeleteProject(string projectId);
    void SaveResults(IReadOnlyCollection<ProjectResult> results);
    void SaveRun(ProjectRun run);
}

public sealed class InMemoryProjectPersistence : IProjectPersistence
{
    public ProjectSnapshot Load() => new();
    public void SaveProject(SearchProject project) { }
    public void DeleteProject(string projectId) { }
    public void SaveResults(IReadOnlyCollection<ProjectResult> results) { }
    public void SaveRun(ProjectRun run) { }
}

/// <summary>Résultat de l'intégration d'une recherche sur un site.</summary>
/// <param name="Found">Annonces renvoyées par le site.</param>
/// <param name="Kept">Annonces conformes aux critères du projet.</param>
/// <param name="New">Annonces jamais vues pour ce projet.</param>
public sealed record ApplyOutcome(int Found, int Kept, int New);

/// <summary>Résumé d'un projet pour la liste des projets.</summary>
public sealed record ProjectSummary(SearchProject Project, int Results, int New, IReadOnlyList<ProjectRun> Runs);

/// <summary>
/// Projets, résultats et passages, en mémoire (quelques milliers d'annonces au maximum), écrits au fil de l'eau.
/// Un verrou exécute les opérations une par une (pages web + lancements en arrière-plan).
/// </summary>
public sealed class ProjectStore
{
    private readonly IProjectPersistence _persistence;
    private readonly Func<long> _now;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, SearchProject> _projects = [];
    private readonly Dictionary<string, Dictionary<string, ProjectResult>> _results = [];
    private readonly Dictionary<(string, string), ProjectRun> _runs = [];

    /// <summary>Déclenché après chaque modification. Les pages se mettent à jour en direct.</summary>
    public event Action? Changed;

    public ProjectStore(IProjectPersistence persistence, Func<long>? now = null)
    {
        _persistence = persistence;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var snap = persistence.Load();
        foreach (var p in snap.Projects) _projects[p.Id] = p;
        foreach (var r in snap.Results) ResultsOf(r.ProjectId)[r.Key] = r;
        foreach (var r in snap.Runs) _runs[(r.ProjectId, r.Site)] = r;
    }

    public long Now => _now();

    /// <summary>Un lancement commence ou se termine : les pages rafraîchissent l'indicateur "en cours".</summary>
    public void NotifyRunning() => Changed?.Invoke();

    // ---------------------------------------------------------------- projets

    public IReadOnlyList<ProjectSummary> Summaries()
    {
        lock (_lock)
        {
            return _projects.Values.OrderBy(p => p.CreatedAt).Select(Summarize).ToList();
        }
    }

    public SearchProject? Get(string id)
    {
        lock (_lock) return _projects.GetValueOrDefault(id);
    }

    public IReadOnlyList<SearchProject> Projects()
    {
        lock (_lock) return _projects.Values.OrderBy(p => p.CreatedAt).ToList();
    }

    /// <summary>Crée ou met à jour un projet (la page de modification garde les identifiants des lieux inchangés).</summary>
    public SearchProject Save(SearchProject project)
    {
        lock (_lock)
        {
            var now = Now;
            if (project.CreatedAt == 0) project.CreatedAt = now;
            project.UpdatedAt = now;
            _projects[project.Id] = project;
            _persistence.SaveProject(project);
        }
        Changed?.Invoke();
        return project;
    }

    public void Delete(string id)
    {
        lock (_lock)
        {
            _projects.Remove(id);
            _results.Remove(id);
            foreach (var key in _runs.Keys.Where(k => k.Item1 == id).ToList()) _runs.Remove(key);
            _persistence.DeleteProject(id);
        }
        Changed?.Invoke();
    }

    /// <summary>Page des résultats ouverte : les annonces actuelles ne sont plus "nouvelles" à la prochaine visite.</summary>
    public void MarkViewed(string id)
    {
        lock (_lock)
        {
            if (!_projects.TryGetValue(id, out var p)) return;
            p.LastViewedAt = Now;
            _persistence.SaveProject(p);
        }
    }

    // ---------------------------------------------------------------- résultats

    public IReadOnlyList<ProjectResult> Results(string projectId)
    {
        lock (_lock)
        {
            return _results.TryGetValue(projectId, out var r)
                ? r.Values.OrderByDescending(x => x.FirstSeenAt).ThenByDescending(x => x.Data.PublishedAt, StringComparer.Ordinal).ToList()
                : [];
        }
    }

    /// <summary>
    /// Intègre les annonces renvoyées par un site. Seules les annonces conformes aux critères sont gardées.
    /// Une annonce déjà connue garde sa date de première vue (elle n'est plus "nouvelle").
    /// </summary>
    public ApplyOutcome ApplyResults(string projectId, string site, IEnumerable<(string SiteId, ListingData Data)> items)
    {
        int found = 0, kept = 0, added = 0;
        lock (_lock)
        {
            if (!_projects.TryGetValue(projectId, out var p)) return new(0, 0, 0);
            var now = Now;
            var results = ResultsOf(projectId);
            var changed = new List<ProjectResult>();
            foreach (var (siteId, data) in items)
            {
                found++;
                if (string.IsNullOrWhiteSpace(siteId) || !ProjectMatcher.Matches(p, data, out _)) continue;
                kept++;
                var key = $"{site}:{siteId}";
                if (!results.TryGetValue(key, out var r))
                {
                    r = new ProjectResult { ProjectId = projectId, Site = site, SiteId = siteId, FirstSeenAt = now };
                    results[key] = r;
                    added++;
                }
                r.Data = data;
                r.LastSeenAt = now;
                changed.Add(r);
            }
            if (changed.Count > 0) _persistence.SaveResults(changed);
        }
        Changed?.Invoke();
        return new(found, kept, added);
    }

    public void SetHidden(string projectId, string key, bool hidden)
    {
        lock (_lock)
        {
            if (!_results.TryGetValue(projectId, out var r) || !r.TryGetValue(key, out var item)) return;
            item.Hidden = hidden;
            _persistence.SaveResults([item]);
        }
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- passages

    public void AddRun(ProjectRun run)
    {
        lock (_lock)
        {
            _runs[(run.ProjectId, run.Site)] = run;
            if (_projects.TryGetValue(run.ProjectId, out var p) && (p.LastRunAt is null || p.LastRunAt < run.At))
            {
                p.LastRunAt = run.At;
                _persistence.SaveProject(p);
            }
            _persistence.SaveRun(run);
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<ProjectRun> Runs(string projectId)
    {
        lock (_lock) return _runs.Values.Where(r => r.ProjectId == projectId).OrderBy(r => r.Site, StringComparer.Ordinal).ToList();
    }

    // ---------------------------------------------------------------- interne

    private Dictionary<string, ProjectResult> ResultsOf(string projectId)
    {
        if (!_results.TryGetValue(projectId, out var r)) _results[projectId] = r = [];
        return r;
    }

    private ProjectSummary Summarize(SearchProject p)
    {
        var results = _results.GetValueOrDefault(p.Id)?.Values.Where(r => !r.Hidden).ToList() ?? [];
        var since = p.LastViewedAt ?? 0;
        return new ProjectSummary(
            p,
            results.Count,
            results.Count(r => r.FirstSeenAt > since),
            _runs.Values.Where(r => r.ProjectId == p.Id).OrderBy(r => r.Site, StringComparer.Ordinal).ToList());
    }
}
