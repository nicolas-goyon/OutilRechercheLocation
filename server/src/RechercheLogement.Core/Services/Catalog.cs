using RechercheLogement.Core.Contracts;
using RechercheLogement.Core.Dedup;
using RechercheLogement.Core.Model;

namespace RechercheLogement.Core.Services;

public sealed class CatalogOptions
{
    public DedupThresholds Thresholds { get; init; } = new();
    /// <summary>URL publique du site local, pour les liens "ouvrir sur le site" renvoyés au plugin.</summary>
    public string PublicUrl { get; init; } = "http://localhost:5080";
    public Func<long> Now { get; init; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>
/// Cœur du serveur : annonces, biens, doublons, statuts.
///
/// Toutes les données sont en mémoire (quelques milliers d'annonces au plus) et
/// chaque opération écrit ses modifications via <see cref="IPersistence"/> dans
/// une transaction. Les opérations sont sérialisées par un verrou : usage local,
/// un seul utilisateur, le plugin et le site web en parallèle.
/// </summary>
public sealed class Catalog
{
    private readonly IPersistence _persistence;
    private readonly CatalogOptions _options;
    private readonly Lock _lock = new();

    private readonly Dictionary<string, Listing> _listings = [];
    private readonly Dictionary<string, Property> _properties = [];
    private readonly Dictionary<(string, string), DuplicateLink> _links = [];
    private readonly List<PropertyEvent> _events = [];
    private readonly HashSet<string> _appliedActions = [];
    private readonly Dictionary<string, string> _settings = [];
    private readonly Dictionary<string, HashSet<string>> _byProperty = [];
    private readonly Dictionary<string, HashSet<string>> _byBlock = [];
    private readonly Dictionary<string, List<DuplicateLink>> _linksByListing = [];
    private long _nextEventId = 1;

    /// <summary>Déclenché après chaque modification (le site web se rafraîchit en direct).</summary>
    public event Action? Changed;

    public Catalog(IPersistence persistence, CatalogOptions? options = null)
    {
        _persistence = persistence;
        _options = options ?? new CatalogOptions();
        var snap = persistence.Load();
        foreach (var p in snap.Properties) _properties[p.Id] = p;
        foreach (var l in snap.Listings) AddListingToIndexes(l);
        foreach (var l in snap.Links) IndexLink(l);
        _events.AddRange(snap.Events);
        _nextEventId = _events.Count == 0 ? 1 : _events.Max(e => e.Id) + 1;
        _appliedActions.UnionWith(snap.AppliedActionIds);
        foreach (var (k, v) in snap.Settings) _settings[k] = v;
    }

    private long Now => _options.Now();

    // =====================================================================
    // Synchronisation avec le plugin
    // =====================================================================

    public SyncResponse Sync(SyncRequest request)
    {
        var applied = new List<string>();
        var rejected = new List<string>();
        var keys = new HashSet<string>();

        Mutate(c =>
        {
            // 1. Observations d'abord : une action peut viser une annonce vue dans le même lot.
            foreach (var obs in request.Observations ?? [])
            {
                if (string.IsNullOrWhiteSpace(obs.Site) || string.IsNullOrWhiteSpace(obs.SiteId)) continue;
                var l = Observe(c, obs);
                keys.Add(l.Key);
            }

            // 2. Actions, dans l'ordre chronologique, idempotentes (identifiant unique).
            foreach (var a in (request.Actions ?? []).OrderBy(a => a.At))
            {
                keys.Add(a.Key);
                if (a.OtherKey is not null) keys.Add(a.OtherKey);
                if (_appliedActions.Contains(a.Id))
                {
                    applied.Add(a.Id);
                    continue;
                }
                if (ApplyAction(c, a)) applied.Add(a.Id);
                else rejected.Add(a.Id);
                _appliedActions.Add(a.Id);
                c.AppliedActions.Add((a.Id, Now));
            }
        });

        foreach (var k in request.Want ?? []) keys.Add(k);

        lock (_lock)
        {
            var views = new Dictionary<string, ListingView>();
            foreach (var k in keys)
            {
                var v = BuildView(k);
                if (v is not null) views[k] = v;
            }
            return new SyncResponse(Now, applied, rejected, views);
        }
    }

    private Listing Observe(ChangeSet c, Observation obs)
    {
        var key = Listing.MakeKey(obs.Site, obs.SiteId);
        var now = Now;
        var changed = false;

        if (!_listings.TryGetValue(key, out var l))
        {
            var p = NewProperty(c, now);
            l = new Listing { Key = key, Site = obs.Site, SiteId = obs.SiteId, PropertyId = p.Id, FirstSeenAt = now, LastSeenAt = now, UpdatedAt = now };
            AddListingToIndexes(l);
            changed = true;
        }

        var oldBlock = DedupScorer.BlockingKey(l.Data);
        // L'API du site est plus précise que la carte HTML : une carte ne l'écrase pas.
        var fillOnly = obs.Source == "card" && l.Sources.Contains("api");
        var merged = l.Data.Merge(obs.Data, fillOnly);
        if (!merged.SameAs(l.Data))
        {
            if (merged.Price is not null && l.Data.Price is not null && merged.Price != l.Data.Price)
            {
                l.PriceHistory.Add(new PricePoint(now, l.Data.Price.Value));
                if (l.PriceHistory.Count > 20) l.PriceHistory.RemoveAt(0);
            }
            l.Data = merged;
            changed = true;
        }
        if (!l.Sources.Contains(obs.Source))
        {
            l.Sources.Add(obs.Source);
            changed = true;
        }
        // Granularité d'une heure pour lastSeenAt : pas de réécriture à chaque affichage.
        if (now - l.LastSeenAt > 3_600_000)
        {
            l.LastSeenAt = now;
            changed = true;
        }

        if (changed)
        {
            l.UpdatedAt = now;
            var newBlock = DedupScorer.BlockingKey(l.Data);
            if (newBlock != oldBlock) _byBlock.GetValueOrDefault(oldBlock)?.Remove(key);
            AddTo(_byBlock, newBlock, key);
            c.Touch(l);
            RunDedup(c, l);
        }
        return l;
    }

    private bool ApplyAction(ChangeSet c, SyncAction a)
    {
        if (!_listings.TryGetValue(a.Key, out var l)) return false;
        var p = _properties[l.PropertyId];
        switch (a.Type)
        {
            case ActionType.SetStatus when a.Status is not null:
                // Dernière décision gagnante : une action plus ancienne qu'un changement fait sur le site est ignorée.
                if (p.StatusChangedAt > a.At) return true;
                SetStatusCore(c, p, a.Status.Value, a.At, "plugin");
                return true;
            case ActionType.SetNote:
                SetNoteCore(c, p, a.Note);
                return true;
            case ActionType.ConfirmDuplicate when a.OtherKey is not null && _listings.ContainsKey(a.OtherKey):
                ConfirmCore(c, a.Key, a.OtherKey, "user");
                return true;
            case ActionType.DismissDuplicate when a.OtherKey is not null && _listings.ContainsKey(a.OtherKey):
                DismissCore(c, a.Key, a.OtherKey);
                return true;
            case ActionType.Detach:
                DetachCore(c, a.Key);
                return true;
            default:
                return false;
        }
    }

    // =====================================================================
    // Opérations du site web
    // =====================================================================

    public void SetStatus(string propertyId, PropertyStatus status) => Mutate(c =>
    {
        if (_properties.TryGetValue(propertyId, out var p)) SetStatusCore(c, p, status, Now, "site");
    });

    public void SetContactStage(string propertyId, ContactStage stage) => Mutate(c =>
    {
        if (!_properties.TryGetValue(propertyId, out var p) || p.ContactStage == stage) return;
        if (p.Status != PropertyStatus.ToContact) SetStatusCore(c, p, PropertyStatus.ToContact, Now, "site");
        p.ContactStage = stage;
        p.UpdatedAt = Now;
        c.Touch(p);
        AddEvent(c, p.Id, PropertyEventKind.ContactStageChanged, Labels.Of(stage));
    });

    public void SetNote(string propertyId, string? note) => Mutate(c =>
    {
        if (_properties.TryGetValue(propertyId, out var p)) SetNoteCore(c, p, note);
    });

    public void AddComment(string propertyId, string text) => Mutate(c =>
    {
        if (_properties.ContainsKey(propertyId) && !string.IsNullOrWhiteSpace(text))
            AddEvent(c, propertyId, PropertyEventKind.Comment, text.Trim());
    });

    public void ConfirmDuplicate(string a, string b) => Mutate(c =>
    {
        if (_listings.ContainsKey(a) && _listings.ContainsKey(b)) ConfirmCore(c, a, b, "user");
    });

    public void DismissDuplicate(string a, string b) => Mutate(c =>
    {
        if (_listings.ContainsKey(a) && _listings.ContainsKey(b)) DismissCore(c, a, b);
    });

    public void Detach(string key) => Mutate(c => DetachCore(c, key));

    public string? GetSetting(string key)
    {
        lock (_lock) return _settings.GetValueOrDefault(key);
    }

    public void SetSetting(string key, string value) => Mutate(c =>
    {
        _settings[key] = value;
        c.Settings[key] = value;
    }, notify: false);

    // =====================================================================
    // Lecture (site web)
    // =====================================================================

    public IReadOnlyList<PropertySummary> QueryProperties(PropertyQuery q)
    {
        lock (_lock)
        {
            IEnumerable<PropertySummary> items = _properties.Values.Select(Summarize);
            if (q.Statuses is { Count: > 0 }) items = items.Where(s => q.Statuses.Contains(s.Property.Status));
            if (q.Site is not null) items = items.Where(s => s.Listings.Any(l => l.Site == q.Site));
            if (!string.IsNullOrWhiteSpace(q.PostalCode)) items = items.Where(s => s.PostalCode?.StartsWith(q.PostalCode.Trim(), StringComparison.Ordinal) == true);
            if (q.MaxPrice is not null) items = items.Where(s => s.Price is null || s.Price <= q.MaxPrice);
            if (q.MinSurface is not null) items = items.Where(s => s.Surface is null || s.Surface >= q.MinSurface);
            if (!string.IsNullOrWhiteSpace(q.Text))
            {
                var t = DedupScorer.NormalizeText(q.Text);
                items = items.Where(s => DedupScorer.NormalizeText($"{s.Title} {s.City} {s.District} {s.Property.Note}").Contains(t, StringComparison.Ordinal));
            }
            items = q.Sort switch
            {
                PropertySort.PriceAsc => items.OrderBy(s => s.Price ?? decimal.MaxValue),
                PropertySort.PriceDesc => items.OrderByDescending(s => s.Price ?? 0),
                PropertySort.SurfaceDesc => items.OrderByDescending(s => s.Surface ?? 0),
                PropertySort.LastUpdate => items.OrderByDescending(s => s.Property.UpdatedAt),
                _ => items.OrderByDescending(s => s.LastSeenAt),
            };
            return items.Skip(q.Skip).Take(q.Take).ToList();
        }
    }

    public PropertyDetail? GetProperty(string propertyId)
    {
        lock (_lock)
        {
            if (!_properties.TryGetValue(propertyId, out var p)) return null;
            var listings = ListingsOf(p.Id).ToList();
            var suggestions = listings
                .SelectMany(l => SuggestionsFor(l).Select(s => new PendingDuplicate(s.Link, l, s.Other, p.Status, _properties[s.Other.PropertyId].Status)))
                .ToList();
            var events = _events.Where(e => e.PropertyId == p.Id).OrderByDescending(e => e.At).ToList();
            return new PropertyDetail(Summarize(p), events, suggestions);
        }
    }

    public string? PropertyIdOf(string listingKey)
    {
        lock (_lock) return _listings.GetValueOrDefault(listingKey)?.PropertyId;
    }

    public IReadOnlyList<PendingDuplicate> PendingDuplicates()
    {
        lock (_lock)
        {
            return _links.Values
                .Where(l => l.State == LinkState.Suggested)
                .Select(l => (Link: l, A: _listings.GetValueOrDefault(l.A), B: _listings.GetValueOrDefault(l.B)))
                .Where(x => x.A is not null && x.B is not null && x.A.PropertyId != x.B.PropertyId)
                .OrderByDescending(x => x.Link.Score)
                .Select(x => new PendingDuplicate(x.Link, x.A!, x.B!, _properties[x.A!.PropertyId].Status, _properties[x.B!.PropertyId].Status))
                .ToList();
        }
    }

    public CatalogStats Stats()
    {
        lock (_lock)
        {
            return new CatalogStats(
                _listings.Count,
                _properties.Count,
                _properties.Values.GroupBy(p => p.Status).ToDictionary(g => g.Key, g => g.Count()),
                _properties.Values.Where(p => p.Status == PropertyStatus.ToContact)
                    .GroupBy(p => p.ContactStage ?? ContactStage.Pending).ToDictionary(g => g.Key, g => g.Count()),
                _listings.Values.GroupBy(l => l.Site).ToDictionary(g => g.Key, g => g.Count()),
                PendingDuplicates().Count);
        }
    }

    /// <summary>Copie complète (sauvegarde JSON).</summary>
    public StoreSnapshot Export()
    {
        lock (_lock)
        {
            return new StoreSnapshot
            {
                Listings = [.. _listings.Values],
                Properties = [.. _properties.Values],
                Links = [.. _links.Values],
                Events = [.. _events],
            };
        }
    }

    public ListingView? View(string key)
    {
        lock (_lock) return BuildView(key);
    }

    // =====================================================================
    // Interne
    // =====================================================================

    private void Mutate(Action<ChangeSet> work, bool notify = true)
    {
        var c = new ChangeSet();
        lock (_lock)
        {
            work(c);
            if (!c.IsEmpty) _persistence.Commit(c);
        }
        if (notify && !c.IsEmpty) Changed?.Invoke();
    }

    private void SetStatusCore(ChangeSet c, Property p, PropertyStatus status, long at, string origin)
    {
        if (p.Status == status) return;
        var before = p.Status;
        p.Status = status;
        p.StatusChangedAt = at;
        p.UpdatedAt = Now;
        if (status == PropertyStatus.ToContact) p.ContactStage ??= ContactStage.Pending;
        c.Touch(p);
        AddEvent(c, p.Id, PropertyEventKind.StatusChanged, $"{Labels.Of(before)} → {Labels.Of(status)} ({origin})");
    }

    private void SetNoteCore(ChangeSet c, Property p, string? note)
    {
        var n = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (n == p.Note) return;
        p.Note = n;
        p.UpdatedAt = Now;
        c.Touch(p);
        AddEvent(c, p.Id, PropertyEventKind.Note, n ?? "(note effacée)");
    }

    private void RunDedup(ChangeSet c, Listing l)
    {
        if (!_byBlock.TryGetValue(DedupScorer.BlockingKey(l.Data), out var bucket)) return;
        var now = Now;
        foreach (var otherKey in bucket.ToList())
        {
            if (otherKey == l.Key) continue;
            var other = _listings[otherKey];
            if (other.PropertyId == l.PropertyId) continue;
            var existing = _links.GetValueOrDefault(DuplicateLink.Order(l.Key, otherKey));
            if (existing is not null && existing.State != LinkState.Suggested) continue; // décision déjà prise

            var r = DedupScorer.Compare(l.Data, other.Data);
            if (r.Score >= _options.Thresholds.AutoLink)
            {
                var link = EnsureLink(c, l.Key, otherKey);
                link.Score = r.Score;
                link.Reasons = [.. r.Reasons];
                ConfirmCore(c, l.Key, otherKey, "auto");
            }
            else if (r.Score >= _options.Thresholds.Suggest)
            {
                var link = EnsureLink(c, l.Key, otherKey);
                link.Score = r.Score;
                link.Reasons = [.. r.Reasons];
                link.State = LinkState.Suggested;
                link.UpdatedAt = now;
                c.Touch(link);
            }
            else if (existing is not null)
            {
                // Les données ont changé, ce n'est plus crédible : on retire la suggestion.
                existing.State = LinkState.Dismissed;
                existing.DecidedBy = "auto";
                existing.UpdatedAt = now;
                c.Touch(existing);
            }
        }
    }

    private void ConfirmCore(ChangeSet c, string a, string b, string decidedBy)
    {
        var link = EnsureLink(c, a, b);
        link.State = LinkState.Confirmed;
        link.DecidedBy = decidedBy;
        link.UpdatedAt = Now;
        c.Touch(link);
        var pa = _listings[a].PropertyId;
        var pb = _listings[b].PropertyId;
        if (pa == pb) return;
        // On garde le bien le plus ancien : son URL sur le site local et son historique restent stables.
        var (keep, drop) = _properties[pa].CreatedAt <= _properties[pb].CreatedAt ? (pa, pb) : (pb, pa);
        MergeProperties(c, keep, drop, decidedBy);
    }

    private void DismissCore(ChangeSet c, string a, string b)
    {
        var link = EnsureLink(c, a, b);
        link.State = LinkState.Dismissed;
        link.DecidedBy = "user";
        link.UpdatedAt = Now;
        c.Touch(link);
    }

    private void DetachCore(ChangeSet c, string key)
    {
        if (!_listings.TryGetValue(key, out var l)) return;
        var siblings = ListingsOf(l.PropertyId).Where(x => x.Key != key).ToList();
        if (siblings.Count == 0) return;
        var now = Now;
        foreach (var s in siblings)
        {
            var link = EnsureLink(c, key, s.Key);
            link.State = LinkState.Dismissed;
            link.DecidedBy = "user";
            link.UpdatedAt = now;
            c.Touch(link);
        }
        var old = l.PropertyId;
        var p = NewProperty(c, now);
        _byProperty.GetValueOrDefault(old)?.Remove(key);
        l.PropertyId = p.Id;
        l.UpdatedAt = now;
        AddTo(_byProperty, p.Id, key);
        c.Touch(l);
        AddEvent(c, old, PropertyEventKind.Detached, $"Annonce {key} dissociée");
    }

    /// <summary>Fusionne le bien <paramref name="drop"/> dans <paramref name="keep"/>. Statut : la décision la plus récente.</summary>
    private void MergeProperties(ChangeSet c, string keep, string drop, string decidedBy)
    {
        var pk = _properties[keep];
        var pd = _properties[drop];
        var now = Now;
        if (pd.Status != PropertyStatus.None && (pk.Status == PropertyStatus.None || (pd.StatusChangedAt ?? 0) > (pk.StatusChangedAt ?? 0)))
        {
            pk.Status = pd.Status;
            pk.StatusChangedAt = pd.StatusChangedAt;
            pk.ContactStage = pd.ContactStage ?? pk.ContactStage;
        }
        if (pd.Note is not null) pk.Note = pk.Note is null ? pd.Note : $"{pk.Note}\n{pd.Note}";
        pk.CreatedAt = Math.Min(pk.CreatedAt, pd.CreatedAt);
        pk.UpdatedAt = now;
        foreach (var k in ListingsOf(drop).Select(x => x.Key).ToList())
        {
            var l = _listings[k];
            l.PropertyId = keep;
            l.UpdatedAt = now;
            AddTo(_byProperty, keep, k);
            c.Touch(l);
        }
        // L'historique suit le bien conservé.
        for (var i = 0; i < _events.Count; i++)
        {
            if (_events[i].PropertyId != drop) continue;
            _events[i] = _events[i] with { PropertyId = keep };
            c.Events.Add(_events[i]);
        }
        _byProperty.Remove(drop);
        _properties.Remove(drop);
        c.Delete(pd);
        c.Touch(pk);
        AddEvent(c, keep, PropertyEventKind.Merged, decidedBy == "auto" ? "Doublon fusionné automatiquement" : "Doublon confirmé et fusionné");
    }

    private Property NewProperty(ChangeSet c, long now)
    {
        var p = new Property { Id = Property.NewId(), Status = PropertyStatus.None, CreatedAt = now, UpdatedAt = now };
        _properties[p.Id] = p;
        c.Touch(p);
        return p;
    }

    private DuplicateLink EnsureLink(ChangeSet c, string x, string y)
    {
        var (a, b) = DuplicateLink.Order(x, y);
        if (!_links.TryGetValue((a, b), out var link))
        {
            var now = Now;
            link = new DuplicateLink { A = a, B = b, State = LinkState.Suggested, CreatedAt = now, UpdatedAt = now };
            IndexLink(link);
            c.Touch(link);
        }
        return link;
    }

    private void AddEvent(ChangeSet c, string propertyId, PropertyEventKind kind, string text)
    {
        var e = new PropertyEvent(_nextEventId++, propertyId, Now, kind, text);
        _events.Add(e);
        c.Events.Add(e);
    }

    private IEnumerable<Listing> ListingsOf(string propertyId) =>
        (_byProperty.GetValueOrDefault(propertyId) ?? []).Select(k => _listings[k]);

    private IEnumerable<(DuplicateLink Link, Listing Other)> SuggestionsFor(Listing l) =>
        (_linksByListing.GetValueOrDefault(l.Key) ?? [])
            .Where(x => x.State == LinkState.Suggested)
            .Select(x => (Link: x, Other: _listings.GetValueOrDefault(x.Other(l.Key))))
            .Where(x => x.Other is not null && x.Other.PropertyId != l.PropertyId)
            .Select(x => (x.Link, x.Other!))
            .OrderByDescending(x => x.Link.Score);

    private ListingView? BuildView(string key)
    {
        if (!_listings.TryGetValue(key, out var l)) return null;
        var p = _properties[l.PropertyId];
        var siblings = ListingsOf(p.Id).Where(x => x.Key != key).Select(Ref).ToList();
        var suggestions = SuggestionsFor(l)
            .Select(s => new SuggestionView(Ref(s.Other), _properties[s.Other.PropertyId].Status, s.Link.Score, s.Link.Reasons))
            .ToList();
        return new ListingView(key, p.Id, p.Status, p.Status == PropertyStatus.ToContact ? p.ContactStage ?? ContactStage.Pending : null,
            p.Note, siblings, suggestions, $"{_options.PublicUrl.TrimEnd('/')}/biens/{p.Id}");
    }

    private static ListingRef Ref(Listing l) => new(l.Key, l.Site, l.Data.Url, l.Data.Title, l.Data.Price);

    private PropertySummary Summarize(Property p)
    {
        var listings = ListingsOf(p.Id).OrderByDescending(l => l.Sources.Contains("api")).ThenBy(l => l.FirstSeenAt).ToList();
        var main = listings.FirstOrDefault();
        return new PropertySummary(
            p,
            listings,
            main?.Data.Title,
            listings.Select(l => l.Data.Price).FirstOrDefault(x => x is not null),
            listings.Select(l => l.Data.Surface).FirstOrDefault(x => x is not null),
            listings.Select(l => l.Data.Rooms).FirstOrDefault(x => x is not null),
            listings.Select(l => l.Data.PostalCode).FirstOrDefault(x => x is not null),
            listings.Select(l => l.Data.City).FirstOrDefault(x => x is not null),
            listings.Select(l => l.Data.District).FirstOrDefault(x => x is not null),
            listings.SelectMany(l => l.Data.Photos ?? []).FirstOrDefault(),
            listings.Count == 0 ? 0 : listings.Max(l => l.LastSeenAt));
    }

    private void AddListingToIndexes(Listing l)
    {
        _listings[l.Key] = l;
        AddTo(_byProperty, l.PropertyId, l.Key);
        AddTo(_byBlock, DedupScorer.BlockingKey(l.Data), l.Key);
        if (!_properties.ContainsKey(l.PropertyId))
        {
            // Réparation : annonce orpheline (base modifiée à la main...).
            _properties[l.PropertyId] = new Property { Id = l.PropertyId, CreatedAt = l.FirstSeenAt, UpdatedAt = l.UpdatedAt };
        }
    }

    private void IndexLink(DuplicateLink link)
    {
        _links[(link.A, link.B)] = link;
        if (!_linksByListing.TryGetValue(link.A, out var la)) _linksByListing[link.A] = la = [];
        if (!_linksByListing.TryGetValue(link.B, out var lb)) _linksByListing[link.B] = lb = [];
        la.Add(link);
        lb.Add(link);
    }

    private static void AddTo(Dictionary<string, HashSet<string>> map, string k, string v)
    {
        if (!map.TryGetValue(k, out var s)) map[k] = s = [];
        s.Add(v);
    }
}

// ------------------------------------------------------------------ modèles de lecture

public enum PropertySort { LastSeen, LastUpdate, PriceAsc, PriceDesc, SurfaceDesc }

public sealed record PropertyQuery
{
    public IReadOnlySet<PropertyStatus>? Statuses { get; init; }
    public string? Site { get; init; }
    public string? PostalCode { get; init; }
    public decimal? MaxPrice { get; init; }
    public double? MinSurface { get; init; }
    public string? Text { get; init; }
    public PropertySort Sort { get; init; } = PropertySort.LastSeen;
    public int Skip { get; init; }
    public int Take { get; init; } = 200;
}

public sealed record PropertySummary(
    Property Property,
    IReadOnlyList<Listing> Listings,
    string? Title,
    decimal? Price,
    double? Surface,
    int? Rooms,
    string? PostalCode,
    string? City,
    string? District,
    string? Photo,
    long LastSeenAt);

public sealed record PendingDuplicate(DuplicateLink Link, Listing A, Listing B, PropertyStatus StatusA, PropertyStatus StatusB);

public sealed record PropertyDetail(PropertySummary Summary, IReadOnlyList<PropertyEvent> Events, IReadOnlyList<PendingDuplicate> Suggestions);

public sealed record CatalogStats(
    int Listings,
    int Properties,
    IReadOnlyDictionary<PropertyStatus, int> ByStatus,
    IReadOnlyDictionary<ContactStage, int> ByContactStage,
    IReadOnlyDictionary<string, int> BySite,
    int PendingDuplicates);
