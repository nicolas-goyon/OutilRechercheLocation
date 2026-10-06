using System.Text.Json;
using Microsoft.Data.Sqlite;
using RechercheLogement.Core.Projects;

namespace RechercheLogement.Server.Projects;

/// <summary>
/// Base SQLite des projets ("Mes projets"), dans un fichier séparé de la base du catalogue (projets.db).
/// Chaque objet est stocké en JSON : le format des critères évoluera avec les sites ajoutés.
/// </summary>
public sealed class SqliteProjectPersistence : IProjectPersistence
{
    private static readonly string[] Migrations =
    [
        // v1
        """
        CREATE TABLE projects (
            id   TEXT PRIMARY KEY,
            json TEXT NOT NULL
        );
        CREATE TABLE project_results (
            project_id TEXT NOT NULL,
            key        TEXT NOT NULL,
            json       TEXT NOT NULL,
            PRIMARY KEY (project_id, key)
        );
        CREATE TABLE project_runs (
            project_id TEXT NOT NULL,
            site       TEXT NOT NULL,
            json       TEXT NOT NULL,
            PRIMARY KEY (project_id, site)
        );
        """,
    ];

    private readonly string _connectionString;

    public SqliteProjectPersistence(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var c = Open();
        using var read = c.CreateCommand();
        read.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(read.ExecuteScalar());
        for (var v = version; v < Migrations.Length; v++)
        {
            using var tx = c.BeginTransaction();
            Exec(c, tx, Migrations[v]);
            Exec(c, tx, $"PRAGMA user_version = {v + 1}");
            tx.Commit();
        }
    }

    public ProjectSnapshot Load()
    {
        using var c = Open();
        var snap = new ProjectSnapshot();
        foreach (var json in Strings(c, "SELECT json FROM projects"))
            if (Json<SearchProject>(json) is { } p) snap.Projects.Add(p);
        foreach (var json in Strings(c, "SELECT json FROM project_results"))
            if (Json<ProjectResult>(json) is { } r) snap.Results.Add(r);
        foreach (var json in Strings(c, "SELECT json FROM project_runs"))
            if (Json<ProjectRun>(json) is { } r) snap.Runs.Add(r);
        return snap;
    }

    public void SaveProject(SearchProject project)
    {
        using var c = Open();
        Exec(c, null, "INSERT INTO projects (id, json) VALUES ($id, $json) ON CONFLICT(id) DO UPDATE SET json = excluded.json",
            ("$id", project.Id), ("$json", ToJson(project)));
    }

    public void DeleteProject(string projectId)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, tx, "DELETE FROM projects WHERE id = $id", ("$id", projectId));
        Exec(c, tx, "DELETE FROM project_results WHERE project_id = $id", ("$id", projectId));
        Exec(c, tx, "DELETE FROM project_runs WHERE project_id = $id", ("$id", projectId));
        tx.Commit();
    }

    public void SaveResults(IReadOnlyCollection<ProjectResult> results)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var r in results)
        {
            Exec(c, tx, """
                INSERT INTO project_results (project_id, key, json) VALUES ($pid, $key, $json)
                ON CONFLICT(project_id, key) DO UPDATE SET json = excluded.json
                """,
                ("$pid", r.ProjectId), ("$key", r.Key), ("$json", ToJson(r)));
        }
        tx.Commit();
    }

    public void SaveRun(ProjectRun run)
    {
        using var c = Open();
        Exec(c, null, """
            INSERT INTO project_runs (project_id, site, json) VALUES ($pid, $site, $json)
            ON CONFLICT(project_id, site) DO UPDATE SET json = excluded.json
            """,
            ("$pid", run.ProjectId), ("$site", run.Site), ("$json", ToJson(run)));
    }

    // ------------------------------------------------------------------ helpers

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();
        return c;
    }

    private static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static List<string> Strings(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var list = new List<string>();
        while (reader.Read()) list.Add(reader.GetString(0));
        return list;
    }

    private static string ToJson<T>(T value) => JsonSerializer.Serialize(value, JsonDefaults.Options);

    private static T? Json<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonDefaults.Options);
}
