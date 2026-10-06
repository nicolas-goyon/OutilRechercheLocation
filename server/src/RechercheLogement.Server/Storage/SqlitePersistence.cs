using System.Text.Json;
using Microsoft.Data.Sqlite;
using RechercheLogement.Core.Model;
using RechercheLogement.Core.Services;

namespace RechercheLogement.Server.Storage;

/// <summary>
/// Stockage SQLite. Les données des annonces sont en JSON, car le schéma de
/// ListingData change avec les sites d'annonces. Les colonnes servent au tri,
/// au filtrage et à l'inspection manuelle de la base.
///
/// Migrations : liste ordonnée de scripts SQL. Le numéro actuel est dans PRAGMA user_version.
/// </summary>
public sealed class SqlitePersistence : IPersistence
{
    private static readonly string[] Migrations =
    [
        // v1
        """
        CREATE TABLE properties (
            id                TEXT PRIMARY KEY,
            status            TEXT NOT NULL,
            contact_stage     TEXT NULL,
            note              TEXT NULL,
            created_at        INTEGER NOT NULL,
            updated_at        INTEGER NOT NULL,
            status_changed_at INTEGER NULL
        );
        CREATE TABLE listings (
            key           TEXT PRIMARY KEY,
            site          TEXT NOT NULL,
            site_id       TEXT NOT NULL,
            property_id   TEXT NOT NULL,
            first_seen_at INTEGER NOT NULL,
            last_seen_at  INTEGER NOT NULL,
            updated_at    INTEGER NOT NULL,
            title         TEXT NULL,
            price         REAL NULL,
            surface       REAL NULL,
            postal_code   TEXT NULL,
            url           TEXT NULL,
            data_json     TEXT NOT NULL,
            sources_json  TEXT NOT NULL,
            price_history_json TEXT NOT NULL
        );
        CREATE INDEX ix_listings_property ON listings(property_id);
        CREATE INDEX ix_listings_postal ON listings(postal_code);
        CREATE TABLE duplicate_links (
            a            TEXT NOT NULL,
            b            TEXT NOT NULL,
            score        REAL NOT NULL,
            reasons_json TEXT NOT NULL,
            state        TEXT NOT NULL,
            decided_by   TEXT NULL,
            created_at   INTEGER NOT NULL,
            updated_at   INTEGER NOT NULL,
            PRIMARY KEY (a, b)
        );
        CREATE TABLE property_events (
            id          INTEGER PRIMARY KEY,
            property_id TEXT NOT NULL,
            at          INTEGER NOT NULL,
            kind        TEXT NOT NULL,
            text        TEXT NOT NULL
        );
        CREATE INDEX ix_events_property ON property_events(property_id);
        CREATE TABLE applied_actions (
            id         TEXT PRIMARY KEY,
            applied_at INTEGER NOT NULL
        );
        CREATE TABLE settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """,
        // v2 : recherches favorites (liens de recherche des sites d'annonces). Doublons permis.
        """
        CREATE TABLE saved_searches (
            id             TEXT PRIMARY KEY,
            name           TEXT NOT NULL,
            url            TEXT NOT NULL,
            site           TEXT NOT NULL,
            note           TEXT NULL,
            sort_order     INTEGER NOT NULL,
            created_at     INTEGER NOT NULL,
            updated_at     INTEGER NOT NULL,
            last_opened_at INTEGER NULL
        );
        """,
    ];

    private readonly string _connectionString;

    public SqlitePersistence(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        Migrate();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        var version = Convert.ToInt32(Scalar(c, "PRAGMA user_version"));
        for (var v = version; v < Migrations.Length; v++)
        {
            using var tx = c.BeginTransaction();
            Exec(c, tx, Migrations[v]);
            Exec(c, tx, $"PRAGMA user_version = {v + 1}");
            tx.Commit();
        }
    }

    public StoreSnapshot Load()
    {
        using var c = Open();
        var snap = new StoreSnapshot();

        foreach (var r in Query(c, "SELECT id, status, contact_stage, note, created_at, updated_at, status_changed_at FROM properties"))
        {
            snap.Properties.Add(new Property
            {
                Id = r.GetString(0),
                Status = Enum.Parse<PropertyStatus>(r.GetString(1)),
                ContactStage = r.IsDBNull(2) ? null : Enum.Parse<ContactStage>(r.GetString(2)),
                Note = r.IsDBNull(3) ? null : r.GetString(3),
                CreatedAt = r.GetInt64(4),
                UpdatedAt = r.GetInt64(5),
                StatusChangedAt = r.IsDBNull(6) ? null : r.GetInt64(6),
            });
        }

        foreach (var r in Query(c, "SELECT key, site, site_id, property_id, first_seen_at, last_seen_at, updated_at, data_json, sources_json, price_history_json FROM listings"))
        {
            snap.Listings.Add(new Listing
            {
                Key = r.GetString(0),
                Site = r.GetString(1),
                SiteId = r.GetString(2),
                PropertyId = r.GetString(3),
                FirstSeenAt = r.GetInt64(4),
                LastSeenAt = r.GetInt64(5),
                UpdatedAt = r.GetInt64(6),
                Data = Json<ListingData>(r.GetString(7)) ?? new(),
                Sources = Json<List<string>>(r.GetString(8)) ?? [],
                PriceHistory = Json<List<PricePoint>>(r.GetString(9)) ?? [],
            });
        }

        foreach (var r in Query(c, "SELECT a, b, score, reasons_json, state, decided_by, created_at, updated_at FROM duplicate_links"))
        {
            snap.Links.Add(new DuplicateLink
            {
                A = r.GetString(0),
                B = r.GetString(1),
                Score = r.GetDouble(2),
                Reasons = Json<List<string>>(r.GetString(3)) ?? [],
                State = Enum.Parse<LinkState>(r.GetString(4)),
                DecidedBy = r.IsDBNull(5) ? null : r.GetString(5),
                CreatedAt = r.GetInt64(6),
                UpdatedAt = r.GetInt64(7),
            });
        }

        foreach (var r in Query(c, "SELECT id, property_id, at, kind, text FROM property_events ORDER BY id"))
        {
            snap.Events.Add(new PropertyEvent(r.GetInt64(0), r.GetString(1), r.GetInt64(2), Enum.Parse<PropertyEventKind>(r.GetString(3)), r.GetString(4)));
        }

        // Seules les actions récentes servent à l'idempotence. Un plugin ne renvoie pas une action de plus de 90 jours.
        var since = DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeMilliseconds();
        foreach (var r in Query(c, "SELECT id FROM applied_actions WHERE applied_at >= $since", ("$since", since)))
            snap.AppliedActionIds.Add(r.GetString(0));

        foreach (var r in Query(c, "SELECT key, value FROM settings"))
            snap.Settings[r.GetString(0)] = r.GetString(1);

        foreach (var r in Query(c, "SELECT id, name, url, site, note, sort_order, created_at, updated_at, last_opened_at FROM saved_searches"))
        {
            snap.Searches.Add(new SavedSearch
            {
                Id = r.GetString(0),
                Name = r.GetString(1),
                Url = r.GetString(2),
                Site = r.GetString(3),
                Note = r.IsDBNull(4) ? null : r.GetString(4),
                Order = r.GetInt32(5),
                CreatedAt = r.GetInt64(6),
                UpdatedAt = r.GetInt64(7),
                LastOpenedAt = r.IsDBNull(8) ? null : r.GetInt64(8),
            });
        }

        return snap;
    }

    public void Commit(ChangeSet changes)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();

        foreach (var p in changes.Properties.Values)
        {
            Exec(c, tx, """
                INSERT INTO properties (id, status, contact_stage, note, created_at, updated_at, status_changed_at)
                VALUES ($id, $status, $stage, $note, $created, $updated, $changed)
                ON CONFLICT(id) DO UPDATE SET status = excluded.status, contact_stage = excluded.contact_stage, note = excluded.note,
                    created_at = excluded.created_at, updated_at = excluded.updated_at, status_changed_at = excluded.status_changed_at
                """,
                ("$id", p.Id), ("$status", p.Status.ToString()), ("$stage", p.ContactStage?.ToString()), ("$note", p.Note),
                ("$created", p.CreatedAt), ("$updated", p.UpdatedAt), ("$changed", p.StatusChangedAt));
        }

        foreach (var l in changes.Listings.Values)
        {
            Exec(c, tx, """
                INSERT INTO listings (key, site, site_id, property_id, first_seen_at, last_seen_at, updated_at, title, price, surface, postal_code, url,
                    data_json, sources_json, price_history_json)
                VALUES ($key, $site, $siteId, $pid, $first, $last, $updated, $title, $price, $surface, $cp, $url, $data, $sources, $history)
                ON CONFLICT(key) DO UPDATE SET property_id = excluded.property_id, last_seen_at = excluded.last_seen_at, updated_at = excluded.updated_at,
                    title = excluded.title, price = excluded.price, surface = excluded.surface, postal_code = excluded.postal_code, url = excluded.url,
                    data_json = excluded.data_json, sources_json = excluded.sources_json, price_history_json = excluded.price_history_json
                """,
                ("$key", l.Key), ("$site", l.Site), ("$siteId", l.SiteId), ("$pid", l.PropertyId), ("$first", l.FirstSeenAt),
                ("$last", l.LastSeenAt), ("$updated", l.UpdatedAt), ("$title", l.Data.Title), ("$price", (double?)l.Data.Price),
                ("$surface", l.Data.Surface), ("$cp", l.Data.PostalCode), ("$url", l.Data.Url), ("$data", ToJson(l.Data)),
                ("$sources", ToJson(l.Sources)), ("$history", ToJson(l.PriceHistory)));
        }

        foreach (var id in changes.DeletedProperties)
            Exec(c, tx, "DELETE FROM properties WHERE id = $id", ("$id", id));

        foreach (var l in changes.Links.Values)
        {
            Exec(c, tx, """
                INSERT INTO duplicate_links (a, b, score, reasons_json, state, decided_by, created_at, updated_at)
                VALUES ($a, $b, $score, $reasons, $state, $by, $created, $updated)
                ON CONFLICT(a, b) DO UPDATE SET score = excluded.score, reasons_json = excluded.reasons_json, state = excluded.state,
                    decided_by = excluded.decided_by, updated_at = excluded.updated_at
                """,
                ("$a", l.A), ("$b", l.B), ("$score", l.Score), ("$reasons", ToJson(l.Reasons)), ("$state", l.State.ToString()),
                ("$by", l.DecidedBy), ("$created", l.CreatedAt), ("$updated", l.UpdatedAt));
        }

        foreach (var e in changes.Events)
        {
            Exec(c, tx, """
                INSERT INTO property_events (id, property_id, at, kind, text) VALUES ($id, $pid, $at, $kind, $text)
                ON CONFLICT(id) DO UPDATE SET property_id = excluded.property_id
                """,
                ("$id", e.Id), ("$pid", e.PropertyId), ("$at", e.At), ("$kind", e.Kind.ToString()), ("$text", e.Text));
        }

        foreach (var (id, at) in changes.AppliedActions)
            Exec(c, tx, "INSERT OR IGNORE INTO applied_actions (id, applied_at) VALUES ($id, $at)", ("$id", id), ("$at", at));

        foreach (var (k, v) in changes.Settings)
            Exec(c, tx, "INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$k", k), ("$v", v));

        foreach (var x in changes.Searches.Values)
        {
            Exec(c, tx, """
                INSERT INTO saved_searches (id, name, url, site, note, sort_order, created_at, updated_at, last_opened_at)
                VALUES ($id, $name, $url, $site, $note, $order, $created, $updated, $opened)
                ON CONFLICT(id) DO UPDATE SET name = excluded.name, url = excluded.url, site = excluded.site, note = excluded.note,
                    sort_order = excluded.sort_order, updated_at = excluded.updated_at, last_opened_at = excluded.last_opened_at
                """,
                ("$id", x.Id), ("$name", x.Name), ("$url", x.Url), ("$site", x.Site), ("$note", x.Note), ("$order", x.Order),
                ("$created", x.CreatedAt), ("$updated", x.UpdatedAt), ("$opened", x.LastOpenedAt));
        }

        foreach (var id in changes.DeletedSearches)
            Exec(c, tx, "DELETE FROM saved_searches WHERE id = $id", ("$id", id));

        tx.Commit();
    }

    // ------------------------------------------------------------------ helpers

    private static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static IEnumerable<SqliteDataReader> Query(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) yield return reader;
    }

    private static string ToJson<T>(T value) => JsonSerializer.Serialize(value, JsonDefaults.Options);

    private static T? Json<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonDefaults.Options);
}

public static class StorageRegistration
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, ServerSettings settings, IWebHostEnvironment env)
    {
        if (settings.Storage.Equals("memory", StringComparison.OrdinalIgnoreCase))
            return services.AddSingleton<IPersistence, InMemoryPersistence>();
        var path = Path.IsPathRooted(settings.DatabasePath) ? settings.DatabasePath : Path.Combine(env.ContentRootPath, settings.DatabasePath);
        return services.AddSingleton<IPersistence>(_ => new SqlitePersistence(path));
    }
}
