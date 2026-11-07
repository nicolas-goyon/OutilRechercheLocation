using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace RechercheLogement.Server.Photos;

/// <summary>Taille d'une photo en cache : vignette des listes et carrousels, ou grande photo de la fiche.</summary>
public enum PhotoSize
{
    Small,
    Large,
}

/// <summary>
/// Photos des annonces gardées sur le disque, recompressées en WebP : une vignette (480 px au plus) pour les listes et
/// une grande photo (1600 px au plus) pour la fiche. Fichiers "ab/abcdef….s.webp" et "….l.webp", nommés d'après
/// l'empreinte SHA-256 (40 premiers caractères) de l'adresse d'origine : aucun index à tenir, le dossier est relu au démarrage.
///
/// Place limitée à <see cref="MaxBytes"/> : au-delà, <see cref="Evict"/> supprime d'abord ce qui sert le moins
/// (voir <see cref="PhotoPrefetcher"/>, qui décide de l'ordre).
/// </summary>
public sealed class PhotoCache
{
    public const int SmallSide = 480;
    public const int LargeSide = 1600;
    private const int SmallQuality = 72;
    private const int LargeQuality = 78;

    private readonly string _dir;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _bytes;

    private sealed class Entry
    {
        public long Bytes;
        public long LastAccessMs;
    }

    public PhotoCache(string dir, long maxBytes)
    {
        _dir = dir;
        MaxBytes = maxBytes;
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.EnumerateFiles(dir, "*.webp", SearchOption.AllDirectories))
        {
            var key = KeyOfFile(f);
            if (key is null) continue;
            var info = new FileInfo(f);
            var e = _entries.GetOrAdd(key, _ => new Entry());
            e.Bytes += info.Length;
            e.LastAccessMs = Math.Max(e.LastAccessMs, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds());
            _bytes += info.Length;
        }
    }

    public long MaxBytes { get; }
    public long TotalBytes => Interlocked.Read(ref _bytes);
    public int Count => _entries.Count;

    /// <summary>Empreinte de l'adresse : nom des fichiers.</summary>
    public static string Key(string url) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..40];

    public bool Contains(string url) => _entries.ContainsKey(Key(url));

    /// <summary>Fichier de la photo, ou null si elle n'est pas en cache. Note l'accès (ordre de suppression).</summary>
    public string? Find(string url, PhotoSize size)
    {
        var key = Key(url);
        if (!_entries.TryGetValue(key, out var e)) return null;
        var path = PathOf(key, size);
        if (!File.Exists(path)) return null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // L'heure du fichier garde l'accès d'un démarrage à l'autre (au plus une écriture par jour).
        if (now - e.LastAccessMs > 24 * 3600 * 1000)
        {
            try
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
            catch (IOException)
            {
                // Sans importance : seul l'ordre de suppression en dépend.
            }
        }
        e.LastAccessMs = now;
        return path;
    }

    /// <summary>
    /// Recompresse et range une photo téléchargée. Image illisible : faux.
    /// </summary>
    public bool Store(string url, byte[] original)
    {
        var encoded = Encode(original);
        if (encoded is null) return false;
        var key = Key(url);
        Directory.CreateDirectory(Path.Combine(_dir, key[..2]));
        var bytes = 0L;
        foreach (var (size, data) in new[] { (PhotoSize.Small, encoded.Value.Small), (PhotoSize.Large, encoded.Value.Large) })
        {
            var path = PathOf(key, size);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: true);
            bytes += data.Length;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var old = _entries.TryGetValue(key, out var previous) ? previous.Bytes : 0;
        _entries[key] = new Entry { Bytes = bytes, LastAccessMs = now };
        Interlocked.Add(ref _bytes, bytes - old);
        return true;
    }

    /// <summary>Vignette et grande photo en WebP. Une image plus petite que la taille visée n'est pas agrandie.</summary>
    public static (byte[] Small, byte[] Large)? Encode(byte[] original)
    {
        try
        {
            using var data = SKData.CreateCopy(original);
            using var codec = SKCodec.Create(data);
            if (codec is null) return null;
            using var bitmap = SKBitmap.Decode(codec);
            if (bitmap is null || bitmap.Width < 8 || bitmap.Height < 8) return null;
            var small = Resize(bitmap, SmallSide, SmallQuality);
            var large = Resize(bitmap, LargeSide, LargeQuality);
            return small is null || large is null ? null : (small, large);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static byte[]? Resize(SKBitmap source, int side, int quality)
    {
        var scale = Math.Min(1.0, (double)side / Math.Max(source.Width, source.Height));
        var w = Math.Max(1, (int)Math.Round(source.Width * scale));
        var h = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var resized = scale < 1 ? source.Resize(new SKImageInfo(w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)) : null;
        using var image = SKImage.FromBitmap(resized ?? source);
        using var encoded = image?.Encode(SKEncodedImageFormat.Webp, quality);
        return encoded?.ToArray();
    }

    /// <summary>
    /// Supprime des photos tant que le cache dépasse <paramref name="targetBytes"/>, dans l'ordre de
    /// <paramref name="order"/> (empreinte, date du dernier accès -> rang : le plus petit part en premier).
    /// Renvoie le nombre de photos supprimées.
    /// </summary>
    public int Evict(long targetBytes, Func<string, long, double> order)
    {
        if (TotalBytes <= targetBytes) return 0;
        var removed = 0;
        foreach (var (key, _) in _entries.Select(kv => (kv.Key, Rank: order(kv.Key, kv.Value.LastAccessMs))).OrderBy(x => x.Rank).ToList())
        {
            if (TotalBytes <= targetBytes) break;
            if (Remove(key)) removed++;
        }
        return removed;
    }

    /// <summary>Supprime les photos dont l'empreinte vérifie <paramref name="drop"/> (empreinte, dernier accès).</summary>
    public int RemoveWhere(Func<string, long, bool> drop)
    {
        var removed = 0;
        foreach (var kv in _entries.ToList())
            if (drop(kv.Key, kv.Value.LastAccessMs) && Remove(kv.Key)) removed++;
        return removed;
    }

    private bool Remove(string key)
    {
        if (!_entries.TryRemove(key, out var e)) return false;
        foreach (var size in new[] { PhotoSize.Small, PhotoSize.Large })
        {
            try
            {
                File.Delete(PathOf(key, size));
            }
            catch (IOException)
            {
                // Fichier ouvert (photo en cours d'envoi) : il partira au prochain passage.
            }
        }
        Interlocked.Add(ref _bytes, -e.Bytes);
        return true;
    }

    private string PathOf(string key, PhotoSize size) => Path.Combine(_dir, key[..2], $"{key}.{(size == PhotoSize.Small ? 's' : 'l')}.webp");

    private static string? KeyOfFile(string path)
    {
        var name = Path.GetFileName(path);
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        return dot == 40 ? name[..40] : null;
    }
}
