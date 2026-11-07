using RechercheLogement.Core.Model;
using RechercheLogement.Core.Recommend;
using RechercheLogement.Core.Services;

namespace RechercheLogement.Server.Recommend;

/// <summary>
/// Recommandations du serveur : le moteur (<see cref="Recommender"/>) est réentraîné quand le catalogue change
/// (nouveau statut, nouvelle annonce) ou que de nouvelles photos sont analysées, au plus une fois toutes les 20 s.
/// </summary>
public sealed class RecommendationService : IDisposable
{
    /// <summary>Photos analysées par bien : les premières, celles que montrent les cartes.</summary>
    public const int PhotosPerProperty = 3;

    private readonly Catalog _catalog;
    private readonly IPhotoStatsStore _photos;
    private readonly Lock _lock = new();
    private Recommender? _model;
    private long _builtAt;
    private bool _stale = true;

    public RecommendationService(Catalog catalog, IPhotoStatsStore photos)
    {
        _catalog = catalog;
        _photos = photos;
        _catalog.Changed += Invalidate;
    }

    /// <summary>Le modèle est à refaire (statut changé, photos analysées).</summary>
    public void Invalidate()
    {
        lock (_lock) _stale = true;
    }

    /// <summary>Biens non notés du périmètre <paramref name="scope"/>, du plus au moins recommandé.</summary>
    public (RecommenderState State, List<Recommendation> Items) Recommend(Func<Listing, bool>? scope, int take)
    {
        var model = Model();
        var candidates = _catalog.QueryProperties(new PropertyQuery { Statuses = new HashSet<PropertyStatus> { PropertyStatus.None }, Scope = scope, Take = int.MaxValue });
        return (model.State, model.Recommend(candidates, take));
    }

    private Recommender Model()
    {
        lock (_lock)
        {
            var now = Environment.TickCount64;
            if (_model is not null && (!_stale || now - _builtAt < 20_000)) return _model;
            var all = _catalog.QueryProperties(new PropertyQuery { Take = int.MaxValue });
            var photos = new Dictionary<string, PhotoStats>();
            foreach (var s in all)
            {
                var stats = (s.Photos ?? []).Take(PhotosPerProperty).Select(u => _photos.Get(u, out _)).OfType<PhotoStats>().ToList();
                if (PhotoStats.Average(stats) is { } avg) photos[s.Property.Id] = avg;
            }
            _model = Recommender.Train(all, photos);
            _builtAt = now;
            _stale = false;
            return _model;
        }
    }

    public void Dispose() => _catalog.Changed -= Invalidate;
}
