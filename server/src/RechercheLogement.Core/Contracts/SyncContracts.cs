using RechercheLogement.Core.Model;

namespace RechercheLogement.Core.Contracts;

// Contrat de POST /api/sync — miroir de plugin/src/core/types.ts.
// JSON en camelCase, enums en camelCase ("toContact", "visitScheduled"...).

public sealed record Observation(string Site, string SiteId, string Source, ListingData Data, long SeenAt);

public enum ActionType { SetStatus, SetNote, ConfirmDuplicate, DismissDuplicate, Detach }

public sealed record SyncAction(
    string Id,
    long At,
    ActionType Type,
    string Key,
    PropertyStatus? Status = null,
    string? Note = null,
    string? OtherKey = null);

public sealed record SyncRequest(
    string? ClientVersion,
    IReadOnlyList<Observation>? Observations,
    IReadOnlyList<SyncAction>? Actions,
    IReadOnlyList<string>? Want);

public sealed record ListingRef(string Key, string Site, string? Url, string? Title, decimal? Price);

public sealed record SuggestionView(ListingRef Other, PropertyStatus OtherStatus, double Score, IReadOnlyList<string> Reasons);

public sealed record ListingView(
    string Key,
    string PropertyId,
    PropertyStatus Status,
    ContactStage? ContactStage,
    string? Note,
    IReadOnlyList<ListingRef> Siblings,
    IReadOnlyList<SuggestionView> Suggestions,
    string? WebUrl);

public sealed record SyncResponse(
    long ServerTime,
    IReadOnlyList<string> AppliedActionIds,
    IReadOnlyList<string> RejectedActionIds,
    IReadOnlyDictionary<string, ListingView> Listings);

public sealed record ServerInfo(string Name, string Version, string WebUrl);
