using System.Globalization;
using Microsoft.Data.Sqlite;

namespace RechercheLogement.Server.Storage;

/// <summary>
/// Une migration : script SQL, puis conversion éventuelle des données (JSON stocké dans les colonnes).
/// Les deux s'exécutent dans la même transaction.
/// </summary>
public sealed record Migration(string Sql, Action<SqliteConnection, SqliteTransaction>? Data = null)
{
    public static implicit operator Migration(string sql) => new(sql);
}

/// <summary>La base a été écrite par une version plus récente du serveur : démarrer risquerait de l'abîmer.</summary>
public sealed class DatabaseTooNewException(string message) : Exception(message);

/// <summary>
/// Applique les migrations d'une base SQLite (numéro actuel : PRAGMA user_version).
/// Avant de toucher à une base existante, écrit une copie dans le dossier backups/ à côté d'elle.
/// Chaque migration est une transaction : en cas d'échec, la base reste à la dernière version réussie.
/// </summary>
public static class SqliteMigrator
{
    /// <summary>Copies gardées par base.</summary>
    public const int BackupsKept = 5;
    public const string BackupFolder = "backups";

    public static void Apply(SqliteConnection c, string path, IReadOnlyList<Migration> migrations)
    {
        var version = ReadVersion(c);
        if (version > migrations.Count)
        {
            throw new DatabaseTooNewException(
                $"La base {Path.GetFileName(path)} est en version {version}, mais ce serveur ne connaît que la version {migrations.Count}. " +
                "Installer une image plus récente, ou restaurer une sauvegarde du dossier backups/ faite pour cette version.");
        }
        if (version == migrations.Count) return;

        if (version > 0) Backup(c, path, version);
        for (var v = version; v < migrations.Count; v++)
        {
            using var tx = c.BeginTransaction();
            Exec(c, tx, migrations[v].Sql);
            migrations[v].Data?.Invoke(c, tx);
            Exec(c, tx, $"PRAGMA user_version = {v + 1}");
            tx.Commit();
        }
    }

    /// <summary>Copie cohérente de la base, journal WAL compris (une copie du fichier ne le garantit pas).</summary>
    public static string Backup(SqliteConnection c, string path, int version)
    {
        var dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, BackupFolder);
        Directory.CreateDirectory(dir);
        var name = Path.GetFileNameWithoutExtension(path);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var target = Path.Combine(dir, $"{name}.v{version}.{stamp}.db");
        using var cmd = c.CreateCommand();
        cmd.CommandText = "VACUUM INTO $target";
        cmd.Parameters.AddWithValue("$target", target);
        cmd.ExecuteNonQuery();

        foreach (var old in Directory.GetFiles(dir, $"{name}.v*.db").OrderDescending(StringComparer.Ordinal).Skip(BackupsKept))
            File.Delete(old);
        return target;
    }

    private static int ReadVersion(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Exec(SqliteConnection c, SqliteTransaction tx, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
