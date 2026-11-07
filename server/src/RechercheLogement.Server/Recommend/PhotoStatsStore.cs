using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RechercheLogement.Core.Recommend;

namespace RechercheLogement.Server.Recommend;

/// <summary>Descripteurs des photos déjà analysées, par URL. Tout est gardé en mémoire ; la base garde une copie.</summary>
public interface IPhotoStatsStore
{
    /// <summary>Photo analysée : ses descripteurs ; échec récent : null (<paramref name="known"/> vrai) ; jamais vue : known faux.</summary>
    PhotoStats? Get(string url, out bool known);

    /// <summary><paramref name="stats"/> null : échec (photo introuvable ou illisible), réessayée après une semaine.</summary>
    void Save(string url, PhotoStats? stats, long at);
}

public sealed class InMemoryPhotoStatsStore : IPhotoStatsStore
{
    private readonly ConcurrentDictionary<string, (PhotoStats? Stats, long At)> _items = new(StringComparer.Ordinal);

    public PhotoStats? Get(string url, out bool known)
    {
        known = _items.TryGetValue(url, out var e) && (e.Stats is not null || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - e.At < RetryAfterMs);
        return known ? e.Stats : null;
    }

    public void Save(string url, PhotoStats? stats, long at) => _items[url] = (stats, at);

    /// <summary>Une photo en échec est réessayée après une semaine.</summary>
    internal const long RetryAfterMs = 7L * 24 * 3600 * 1000;
}

/// <summary>Table photo_stats de la base principale (créée par la migration v4 de <see cref="Storage.SqlitePersistence"/>).</summary>
public sealed class SqlitePhotoStatsStore : IPhotoStatsStore
{
    private readonly string _connectionString;
    private readonly InMemoryPhotoStatsStore _cache = new();

    public SqlitePhotoStatsStore(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT url, json, at FROM photo_stats";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            _cache.Save(r.GetString(0), r.IsDBNull(1) ? null : JsonSerializer.Deserialize<PhotoStats>(r.GetString(1)), r.GetInt64(2));
    }

    public PhotoStats? Get(string url, out bool known) => _cache.Get(url, out known);

    public void Save(string url, PhotoStats? stats, long at)
    {
        _cache.Save(url, stats, at);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO photo_stats (url, json, at) VALUES ($u, $j, $a) ON CONFLICT(url) DO UPDATE SET json = $j, at = $a";
        cmd.Parameters.AddWithValue("$u", url);
        cmd.Parameters.AddWithValue("$j", stats is null ? DBNull.Value : JsonSerializer.Serialize(stats));
        cmd.Parameters.AddWithValue("$a", at);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }
}
