using System.Globalization;
using RechercheLogement.Core.Dedup;
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

/// <summary>Une annonce renvoyée par un site, prête à intégrer (critères et zone déjà vérifiés).</summary>
public sealed record IncomingListing(string SiteId, ListingData Data, double? DistanceM = null);

/// <summary>Bilan de l'intégration d'une recherche sur un site.</summary>
/// <param name="Kept">Annonces intégrées (conformes aux critères).</param>
/// <param name="New">Annonces jamais vues pour ce projet.</param>
/// <param name="Changed">Annonces connues dont le prix, la surface... ont changé.</param>
/// <param name="Removed">Annonces absentes d'une recherche complète : retirées du site.</param>
/// <param name="Reappeared">Annonces retirées puis de nouveau en ligne.</param>
public sealed record ApplyOutcome(int Kept, int New, int Changed = 0, int Removed = 0, int Reappeared = 0);

/// <summary>Un bien du projet : une ou plusieurs annonces (même site ou sites différents) reconnues comme le même bien.</summary>
public sealed record ProjectGroup(string GroupId, IReadOnlyList<ProjectResult> Listings)
{
    /// <summary>Annonce affichée : en ligne de préférence, la plus récemment vue.</summary>
    public ProjectResult Primary => Listings.OrderBy(l => l.Removed).ThenByDescending(l => l.LastSeenAt).First();
    public long FirstSeenAt => Listings.Min(l => l.FirstSeenAt);
    public bool Removed => Listings.All(l => l.Removed);
    public bool Hidden => Listings.All(l => l.Hidden);

    /// <summary>Historique de toutes les annonces du bien, du plus récent au plus ancien.</summary>
    public IEnumerable<(ProjectResult Listing, ResultEvent Event)> Timeline() =>
        Listings.SelectMany(l => l.History.Select(e => (l, e))).OrderByDescending(x => x.e.At);
}

/// <summary>Résumé d'un projet pour la liste des projets.</summary>
public sealed record ProjectSummary(SearchProject Project, int Properties, int New, IReadOnlyList<ProjectRun> Runs);

/// <summary>
/// Projets, résultats et passages, en mémoire (quelques milliers d'annonces au maximum), écrits au fil de l'eau.
/// Un verrou exécute les opérations une par une (pages web + lancements en arrière-plan).
///
/// Suivi des annonces : chaque annonce garde son historique (apparition, changements, retrait,
/// réapparition). Les annonces du même bien (même site ou sites différents, reconnues par
/// <see cref="DedupScorer"/>) forment un groupe.
/// </summary>
public sealed class ProjectStore
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    private readonly IProjectPersistence _persistence;
    private readonly Func<long> _now;
    private readonly Func<string, string> _siteLabel;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, SearchProject> _projects = [];
    private readonly Dictionary<string, Dictionary<string, ProjectResult>> _results = [];
    private readonly Dictionary<(string, string), ProjectRun> _runs = [];

    /// <summary>Déclenché après chaque modification. Les pages se mettent à jour en direct.</summary>
    public event Action? Changed;

    public ProjectStore(IProjectPersistence persistence, Func<long>? now = null, Func<string, string>? siteLabel = null)
    {
        _persistence = persistence;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _siteLabel = siteLabel ?? (s => s);
        var snap = persistence.Load();
        foreach (var p in snap.Projects) _projects[p.Id] = p;
        foreach (var r in snap.Results)
        {
            if (string.IsNullOrEmpty(r.GroupId)) r.GroupId = r.Key;
            ResultsOf(r.ProjectId)[r.Key] = r;
        }
        foreach (var r in snap.Runs) _runs[(r.ProjectId, r.Site)] = r;
    }

    public long Now => _now();

    /// <summary>Un lancement commence ou se termine : les pages rafraîchissent l'indicateur "en cours".</summary>
    public void NotifyRunning() => Changed?.Invoke();

    // ---------------------------------------------------------------- projets

    public IReadOnlyList<ProjectSummary> Summaries()
    {
        lock (_lock) return _projects.Values.OrderBy(p => p.CreatedAt).Select(Summarize).ToList();
    }

    public SearchProject? Get(string id)
    {
        lock (_lock) return _projects.GetValueOrDefault(id);
    }

    public IReadOnlyList<SearchProject> Projects()
    {
        lock (_lock) return _projects.Values.OrderBy(p => p.CreatedAt).ToList();
    }

    /// <summary>Crée ou met à jour un projet.</summary>
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

    /// <summary>Enregistre des données calculées (zone, lieux reconnus) sans notifier les pages.</summary>
    public void SaveQuietly(SearchProject project)
    {
        lock (_lock) _persistence.SaveProject(project);
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
        lock (_lock) return _results.TryGetValue(projectId, out var r) ? r.Values.OrderByDescending(x => x.FirstSeenAt).ToList() : [];
    }

    /// <summary>Biens du projet (annonces regroupées), les plus récents d'abord.</summary>
    public IReadOnlyList<ProjectGroup> Groups(string projectId)
    {
        lock (_lock) return GroupsOf(projectId).ToList();
    }

    /// <summary>
    /// Intègre les annonces renvoyées par un site lors d'un passage commencé à <paramref name="runStartedAt"/>.
    /// <paramref name="complete"/> : le site a renvoyé TOUTES les annonces de la recherche ; une annonce
    /// connue absente est alors marquée retirée.
    /// </summary>
    public ApplyOutcome ApplyResults(string projectId, string site, IEnumerable<IncomingListing> items, bool complete, long runStartedAt)
    {
        int kept = 0, added = 0, changedCount = 0, removed = 0, reappeared = 0;
        lock (_lock)
        {
            if (!_projects.TryGetValue(projectId, out var p)) return new(0, 0);
            var now = Now;
            var results = ResultsOf(projectId);
            var changed = new List<ProjectResult>();
            var siteLabel = _siteLabel(site);

            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.SiteId) || !ProjectMatcher.Matches(p, item.Data, out _)) continue;
                kept++;
                var key = $"{site}:{item.SiteId}";
                if (!results.TryGetValue(key, out var r))
                {
                    r = new ProjectResult { ProjectId = projectId, Site = site, SiteId = item.SiteId, FirstSeenAt = now, GroupId = key, Data = item.Data };
                    r.History.Add(new(now, ResultEventKind.Appeared, $"Publiée sur {siteLabel} : {Describe(item.Data)}"));
                    LinkToGroup(r, results.Values, now);
                    results[key] = r;
                    added++;
                }
                else
                {
                    var diff = Diff(r.Data, item.Data);
                    if (diff.Count > 0)
                    {
                        r.History.Add(new(now, ResultEventKind.Changed, string.Join(" ; ", diff)));
                        changedCount++;
                    }
                    if (r.Removed)
                    {
                        r.Removed = false;
                        r.History.Add(new(now, ResultEventKind.Reappeared, $"De nouveau en ligne sur {siteLabel} (absente {Days(now - r.LastSeenAt)})"));
                        reappeared++;
                    }
                    r.Data = item.Data;
                }
                r.LastSeenAt = now;
                r.DistanceM = item.DistanceM;
                changed.Add(r);
            }

            if (complete)
            {
                foreach (var r in results.Values.Where(r => r.Site == site && !r.Removed && r.LastSeenAt < runStartedAt))
                {
                    // Hors des critères actuels (projet modifié) : pas un retrait du site.
                    if (!ProjectMatcher.Matches(p, r.Data, out _)) continue;
                    r.Removed = true;
                    r.History.Add(new(now, ResultEventKind.Removed, $"Retirée de {siteLabel} (louée, vendue ou supprimée)"));
                    removed++;
                    changed.Add(r);
                }
            }
            if (changed.Count > 0) _persistence.SaveResults(changed.Distinct().ToList());
        }
        Changed?.Invoke();
        return new(kept, added, changedCount, removed, reappeared);
    }

    /// <summary>Masque (ou réaffiche) toutes les annonces d'un bien.</summary>
    public void SetGroupHidden(string projectId, string groupId, bool hidden)
    {
        lock (_lock)
        {
            if (!_results.TryGetValue(projectId, out var r)) return;
            var items = r.Values.Where(x => x.GroupId == groupId).ToList();
            foreach (var item in items) item.Hidden = hidden;
            _persistence.SaveResults(items);
        }
        Changed?.Invoke();
    }

    /// <summary>Sépare une annonce de son groupe (rapprochement erroné).</summary>
    public void Detach(string projectId, string key)
    {
        lock (_lock)
        {
            if (!_results.TryGetValue(projectId, out var r) || !r.TryGetValue(key, out var item)) return;
            var others = r.Values.Where(x => x.GroupId == item.GroupId && x != item).ToList();
            if (others.Count == 0) return;
            var touched = new List<ProjectResult> { item };
            if (item.GroupId == key)
            {
                // L'annonce sert d'identifiant au groupe : les autres prennent celui de la plus ancienne d'entre elles.
                var newId = others.OrderBy(x => x.FirstSeenAt).First().Key;
                foreach (var o in others) o.GroupId = newId;
                touched.AddRange(others);
            }
            else
            {
                item.GroupId = key;
            }
            item.History.Add(new(Now, ResultEventKind.Linked, "Séparée du bien (rapprochement annulé)"));
            _persistence.SaveResults(touched);
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

    private IEnumerable<ProjectGroup> GroupsOf(string projectId) =>
        (_results.GetValueOrDefault(projectId)?.Values ?? Enumerable.Empty<ProjectResult>())
            .GroupBy(r => r.GroupId)
            .Select(g => new ProjectGroup(g.Key, g.OrderBy(x => x.FirstSeenAt).ToList()))
            .OrderByDescending(g => g.FirstSeenAt);

    /// <summary>Même bien qu'une annonce déjà connue (score de doublon ≥ seuil de fusion) : rejoint son groupe.</summary>
    private void LinkToGroup(ProjectResult r, IEnumerable<ProjectResult> existing, long now)
    {
        var threshold = new DedupThresholds().AutoLink;
        ProjectResult? best = null;
        var bestScore = 0.0;
        foreach (var other in existing)
        {
            var score = DedupScorer.Compare(r.Data, other.Data).Score;
            if (score >= threshold && score > bestScore)
            {
                best = other;
                bestScore = score;
            }
        }
        if (best is null) return;
        r.GroupId = best.GroupId;
        var how = best.Site == r.Site ? $"republiée sur {_siteLabel(r.Site)} (nouvelle annonce)" : $"aussi sur {_siteLabel(r.Site)}";
        r.History.Add(new(now, ResultEventKind.Linked, $"Même bien qu'une annonce {_siteLabel(best.Site)} déjà suivie : {how} ({Math.Round(bestScore * 100)} %)"));
    }

    private ProjectSummary Summarize(SearchProject p)
    {
        var groups = GroupsOf(p.Id).Where(g => !g.Hidden && !g.Removed).ToList();
        var since = p.LastViewedAt ?? 0;
        return new ProjectSummary(
            p,
            groups.Count,
            groups.Count(g => g.FirstSeenAt > since),
            _runs.Values.Where(r => r.ProjectId == p.Id).OrderBy(r => r.Site, StringComparer.Ordinal).ToList());
    }

    /// <summary>Différences visibles entre deux versions d'une annonce : "prix 650 € → 620 €"...</summary>
    public static List<string> Diff(ListingData a, ListingData b)
    {
        var d = new List<string>();
        if (a.Price is not null && b.Price is not null && a.Price != b.Price)
            d.Add($"prix {Money(a.Price)} → {Money(b.Price)} ({(b.Price > a.Price ? "+" : "")}{Money(b.Price - a.Price)})");
        if (a.Charges is not null && b.Charges is not null && a.Charges != b.Charges) d.Add($"charges {Money(a.Charges)} → {Money(b.Charges)}");
        if (a.Surface is not null && b.Surface is not null && Math.Abs(a.Surface.Value - b.Surface.Value) >= 0.5)
            d.Add($"surface {a.Surface.Value.ToString("0.#", Fr)} → {b.Surface.Value.ToString("0.#", Fr)} m²");
        if (a.Rooms is not null && b.Rooms is not null && a.Rooms != b.Rooms) d.Add($"pièces {a.Rooms} → {b.Rooms}");
        if (a.Furnished is not null && b.Furnished is not null && a.Furnished != b.Furnished) d.Add(b.Furnished == true ? "devenue meublée" : "devenue non meublée");
        if (!string.IsNullOrEmpty(a.AgencyName) && !string.IsNullOrEmpty(b.AgencyName) && a.AgencyName != b.AgencyName) d.Add($"agence {a.AgencyName} → {b.AgencyName}");
        if (!string.IsNullOrEmpty(a.Title) && !string.IsNullOrEmpty(b.Title) && a.Title != b.Title) d.Add($"titre « {b.Title} »");
        if (DedupScorer.TextSimilarity(a.DescriptionExcerpt, b.DescriptionExcerpt) is < 0.85) d.Add("description modifiée");
        return d;
    }

    private static string Describe(ListingData d)
    {
        var parts = new List<string>();
        if (d.Price is not null) parts.Add(Money(d.Price));
        if (d.Surface is not null) parts.Add($"{d.Surface.Value.ToString("0.#", Fr)} m²");
        if (d.Rooms is not null) parts.Add($"{d.Rooms} p.");
        if (d.City is not null) parts.Add(d.City);
        if (d.AgencyName is not null) parts.Add(d.AgencyName);
        return parts.Count == 0 ? "annonce" : string.Join(", ", parts);
    }

    private static string Money(decimal? v) => v is null ? "?" : $"{v.Value.ToString("#,0.##", Fr)} €";

    private static string Days(long ms)
    {
        var days = ms / 86_400_000;
        return days < 1 ? "moins d'un jour" : $"{days} jour{(days > 1 ? "s" : "")}";
    }
}
