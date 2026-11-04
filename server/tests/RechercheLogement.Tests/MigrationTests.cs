using Microsoft.Data.Sqlite;
using RechercheLogement.Server.Projects;
using RechercheLogement.Server.Storage;

namespace RechercheLogement.Tests;

/// <summary>Montées de version des bases SQLite : données conservées, sauvegarde, garde-fous.</summary>
public sealed class MigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rl-migrations-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "base.db");
    private string BackupDir => Path.Combine(_dir, SqliteMigrator.BackupFolder);

    public MigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection($"Data Source={Db};Pooling=False");
        c.Open();
        return c;
    }

    private void Apply(IReadOnlyList<Migration> migrations)
    {
        using var c = Open();
        SqliteMigrator.Apply(c, Db, migrations);
    }

    private long Scalar(string sql)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private void Exec(string sql)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static TheoryData<int> CatalogVersions()
    {
        var data = new TheoryData<int>();
        for (var v = 1; v <= SqlitePersistence.Migrations.Length; v++) data.Add(v);
        return data;
    }

    [Theory]
    [MemberData(nameof(CatalogVersions))]
    public void Catalog_KeepsDataFromEveryOlderVersion(int version)
    {
        Apply(SqlitePersistence.Migrations[..version]);
        Exec("INSERT INTO properties (id, status, created_at, updated_at) VALUES ('p1', 'Seen', 1, 1)");

        var snapshot = new SqlitePersistence(Db).Load();

        Assert.Equal("p1", Assert.Single(snapshot.Properties).Id);
        Assert.Equal(SqlitePersistence.Migrations.Length, Scalar("PRAGMA user_version"));
    }

    [Fact]
    public void Projects_MigrateFromVersionOne()
    {
        Apply(SqliteProjectPersistence.Migrations[..1]);
        Exec("""INSERT INTO projects (id, json) VALUES ('p1', '{"id":"p1","name":"Chalet"}')""");

        var snapshot = new SqliteProjectPersistence(Db).Load();

        Assert.Equal("Chalet", Assert.Single(snapshot.Projects).Name);
    }

    [Fact]
    public void Upgrade_WritesAReadableBackupOfTheOldVersion()
    {
        Migration[] v1 = ["CREATE TABLE t (x INTEGER); INSERT INTO t VALUES (7);"];
        Apply(v1);

        Apply([.. v1, "UPDATE t SET x = 8;"]);

        var backup = Assert.Single(Directory.GetFiles(BackupDir));
        Assert.Contains(".v1.", backup);
        using var c = new SqliteConnection($"Data Source={backup};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT x FROM t";
        Assert.Equal(7L, cmd.ExecuteScalar());
    }

    [Fact]
    public void NoBackup_ForNewBaseOrWhenAlreadyUpToDate()
    {
        Migration[] v1 = ["CREATE TABLE t (x INTEGER);"];
        Apply(v1);
        Apply(v1);
        Assert.False(Directory.Exists(BackupDir));
    }

    [Fact]
    public void FailedMigration_LeavesLastGoodVersionAndABackup()
    {
        Migration[] v1 = ["CREATE TABLE t (x INTEGER);"];
        Apply(v1);

        Assert.ThrowsAny<SqliteException>(() => Apply([.. v1, "INSERT INTO t VALUES (1); INSERT INTO absente VALUES (1);"]));

        Assert.Equal(1, Scalar("PRAGMA user_version"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM t"));
        Assert.Single(Directory.GetFiles(BackupDir));
    }

    [Fact]
    public void DatabaseNewerThanServer_IsRefusedAndUntouched()
    {
        Migration[] v1 = ["CREATE TABLE t (x INTEGER);"];
        Apply([.. v1, "ALTER TABLE t ADD COLUMN y INTEGER;"]);

        var e = Assert.Throws<DatabaseTooNewException>(() => Apply(v1));

        Assert.Contains("version 2", e.Message);
        Assert.Equal(2, Scalar("PRAGMA user_version"));
    }

    [Fact]
    public void DataStep_ConvertsJsonInTheSameTransaction()
    {
        Migration[] v1 = ["""CREATE TABLE t (json TEXT); INSERT INTO t VALUES ('{"a":1}');"""];
        Apply(v1);

        Apply([.. v1, new Migration("", (c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """UPDATE t SET json = json_set(json, '$.b', 2)""";
            cmd.ExecuteNonQuery();
        })]);

        Assert.Equal(2, Scalar("SELECT json_extract(json, '$.b') FROM t"));
    }

    [Fact]
    public void Backups_AreLimited()
    {
        var migrations = new List<Migration> { "CREATE TABLE t (x INTEGER);" };
        Apply(migrations);
        for (var i = 0; i < SqliteMigrator.BackupsKept + 2; i++)
        {
            migrations.Add("INSERT INTO t VALUES (1);");
            Apply(migrations);
        }
        Assert.Equal(SqliteMigrator.BackupsKept, Directory.GetFiles(BackupDir).Length);
    }
}
