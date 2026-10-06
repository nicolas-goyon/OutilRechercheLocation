using RechercheLogement.Core.Model;

namespace RechercheLogement.Core.Services;

/// <summary>Tout le contenu de la base. Le serveur le charge en mémoire au démarrage (usage local, un seul utilisateur).</summary>
public sealed class StoreSnapshot
{
    public List<Listing> Listings { get; init; } = [];
    public List<Property> Properties { get; init; } = [];
    public List<DuplicateLink> Links { get; init; } = [];
    public List<PropertyEvent> Events { get; init; } = [];
    public HashSet<string> AppliedActionIds { get; init; } = [];
    public Dictionary<string, string> Settings { get; init; } = [];
}

/// <summary>Modifications d'une opération. La persistance les écrit en une seule fois (atomique).</summary>
public sealed class ChangeSet
{
    public Dictionary<string, Listing> Listings { get; } = [];
    public Dictionary<string, Property> Properties { get; } = [];
    public HashSet<string> DeletedProperties { get; } = [];
    public Dictionary<(string, string), DuplicateLink> Links { get; } = [];
    public List<PropertyEvent> Events { get; } = [];
    public List<(string Id, long At)> AppliedActions { get; } = [];
    public Dictionary<string, string> Settings { get; } = [];

    public bool IsEmpty =>
        Listings.Count == 0 && Properties.Count == 0 && DeletedProperties.Count == 0 && Links.Count == 0
        && Events.Count == 0 && AppliedActions.Count == 0 && Settings.Count == 0;

    public void Touch(Listing l) => Listings[l.Key] = l;
    public void Touch(Property p)
    {
        Properties[p.Id] = p;
        DeletedProperties.Remove(p.Id);
    }
    public void Delete(Property p)
    {
        Properties.Remove(p.Id);
        DeletedProperties.Add(p.Id);
    }
    public void Touch(DuplicateLink l) => Links[(l.A, l.B)] = l;
}

/// <summary>Stockage durable. Implémentations : SQLite (serveur), mémoire (tests).</summary>
public interface IPersistence
{
    StoreSnapshot Load();
    void Commit(ChangeSet changes);
}

/// <summary>Persistance en mémoire, pour les tests et la démonstration.</summary>
public sealed class InMemoryPersistence : IPersistence
{
    private readonly StoreSnapshot _data = new();
    public int Commits { get; private set; }

    public StoreSnapshot Load() => _data;

    public void Commit(ChangeSet c)
    {
        // Le catalogue utilise déjà les mêmes instances. Cette méthode met seulement les listes à jour.
        foreach (var l in c.Listings.Values) if (!_data.Listings.Contains(l)) _data.Listings.Add(l);
        foreach (var p in c.Properties.Values) if (!_data.Properties.Contains(p)) _data.Properties.Add(p);
        _data.Properties.RemoveAll(p => c.DeletedProperties.Contains(p.Id));
        foreach (var l in c.Links.Values) if (!_data.Links.Contains(l)) _data.Links.Add(l);
        foreach (var e in c.Events)
        {
            _data.Events.RemoveAll(x => x.Id == e.Id);
            _data.Events.Add(e);
        }
        foreach (var (id, _) in c.AppliedActions) _data.AppliedActionIds.Add(id);
        foreach (var (k, v) in c.Settings) _data.Settings[k] = v;
        Commits++;
    }
}
