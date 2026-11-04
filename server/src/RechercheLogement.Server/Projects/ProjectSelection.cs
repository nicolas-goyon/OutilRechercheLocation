using RechercheLogement.Core.Model;
using RechercheLogement.Core.Projects;

namespace RechercheLogement.Server.Projects;

/// <summary>
/// Projet choisi dans la barre latérale (un par onglet de navigateur ; le choix est mémorisé par le navigateur).
/// Les pages limitent leurs données à <see cref="Scope"/>. Aucun projet : tout le catalogue.
/// </summary>
public sealed class ProjectSelection(ProjectStore store)
{
    public string? ProjectId { get; private set; }
    public event Action? Changed;

    public SearchProject? Project => ProjectId is null ? null : store.Get(ProjectId);

    public void Select(string? projectId)
    {
        projectId = string.IsNullOrEmpty(projectId) || store.Get(projectId) is null ? null : projectId;
        if (projectId == ProjectId) return;
        ProjectId = projectId;
        Changed?.Invoke();
    }

    /// <summary>Annonces du projet : résultats de ses lancements, et annonces qui respectent ses critères et sa zone.</summary>
    public Func<Listing, bool>? Scope
    {
        get
        {
            if (Project is not { } project) return null;
            var results = store.Results(project.Id).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
            return l => results.Contains(l.Key) || ProjectScope.Contains(project, l.Data);
        }
    }

    /// <summary>Recherches visibles : celles du projet, et celles qui ne sont liées à aucun projet.</summary>
    public IEnumerable<SavedSearch> Visible(IEnumerable<SavedSearch> searches) =>
        ProjectId is null ? searches : searches.Where(s => s.ProjectId is null || s.ProjectId == ProjectId);
}
