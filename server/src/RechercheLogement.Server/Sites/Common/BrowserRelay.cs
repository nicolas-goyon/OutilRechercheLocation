using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace RechercheLogement.Server.Sites.Common;

/// <summary>Requête à exécuter par le plugin, dans un onglet du site.</summary>
public sealed record RelayRequest(string Id, string Method, string Url, string? Body);

/// <summary>Réponse du site vue par le plugin. <paramref name="Error"/> : le navigateur n'a pas pu appeler le site.</summary>
public sealed record RelayResult(int Status, string? Body, string? Error);

/// <summary>
/// Fait passer les appels d'un site par le plugin, pour les sites qui refusent les appels hors navigateur.
/// Le plugin interroge le serveur (GET /api/relay/next), exécute la requête dans un onglet ouvert par
/// l'utilisateur, avec sa session, puis renvoie la réponse telle quelle.
///
/// Le relais reste discret : une requête à la fois, avec un délai entre deux requêtes. Il ne contourne rien :
/// si le site demande une vérification, la recherche s'arrête et l'utilisateur la passe lui-même dans son onglet.
/// </summary>
public sealed class BrowserRelay(int delayMs = 2000, TimeSpan? timeout = null, TimeProvider? clock = null)
{
    /// <summary>Début du message quand aucun onglet n'est ouvert : le serveur relance ces recherches à l'ouverture d'un onglet.</summary>
    public const string NoTabPrefix = "En attente d'un onglet";

    /// <summary>Durée d'attente d'une requête GET /api/relay/next. Inférieure au délai du plugin.</summary>
    public static readonly TimeSpan PollWait = TimeSpan.FromSeconds(25);
    /// <summary>Onglet considéré ouvert si le plugin a interrogé le serveur il y a moins de ce temps.</summary>
    private static readonly TimeSpan ConnectedWindow = TimeSpan.FromSeconds(45);

    private sealed record Pending(RelayRequest Request, TaskCompletionSource<RelayResult> Result);

    private sealed class SiteState
    {
        public readonly Channel<Pending> Queue = Channel.CreateUnbounded<Pending>();
        public readonly SemaphoreSlim OneAtATime = new(1, 1);
        public DateTimeOffset LastPoll = DateTimeOffset.MinValue;
        public DateTimeOffset LastSent = DateTimeOffset.MinValue;
        public int Polling;
    }

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(90);
    private readonly ConcurrentDictionary<string, SiteState> _sites = new();
    private readonly ConcurrentDictionary<string, Pending> _sent = new();

    /// <summary>Un onglet vient d'apparaître pour ce site (aucun n'était connecté).</summary>
    public event Action<string>? SiteConnected;

    private SiteState State(string site) => _sites.GetOrAdd(site, _ => new SiteState());

    private bool Connected(SiteState s) => s.Polling > 0 || _clock.GetUtcNow() - s.LastPoll < ConnectedWindow;

    public bool IsConnected(string site) => Connected(State(site));

    /// <summary>Exécute la requête dans le navigateur et lit le JSON, avec les mêmes erreurs qu'un appel direct.</summary>
    public async Task<JsonNode> SendJsonAsync(string site, string label, HttpMethod method, string url, string? body, CancellationToken ct)
    {
        var state = State(site);
        if (!Connected(state))
            throw new SiteException($"{NoTabPrefix} {label} : ouvrir le site dans le navigateur, avec le plugin actif. La recherche reprendra toute seule.");

        await state.OneAtATime.WaitAsync(ct);
        try
        {
            var wait = state.LastSent + TimeSpan.FromMilliseconds(delayMs) - _clock.GetUtcNow();
            if (wait > TimeSpan.Zero) await Task.Delay(wait, _clock, ct);
            state.LastSent = _clock.GetUtcNow();

            var pending = new Pending(
                new RelayRequest(Guid.NewGuid().ToString("N"), method.Method, url, body),
                new TaskCompletionSource<RelayResult>(TaskCreationOptions.RunContinuationsAsynchronously));
            await state.Queue.Writer.WriteAsync(pending, ct);
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(_timeout);
                var result = await pending.Result.Task.WaitAsync(limit.Token);
                if (result.Error is not null) throw new SiteException($"{label} injoignable depuis le navigateur : {result.Error}");
                return Blocked(label, () => SiteHttp.ReadJson(result.Status, result.Body ?? "", label));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new SiteException($"{label} : l'onglet du navigateur n'a pas répondu à temps. Vérifier qu'il est toujours ouvert.");
            }
            finally
            {
                pending.Result.TrySetCanceled();
                _sent.TryRemove(pending.Request.Id, out _);
            }
        }
        finally
        {
            state.OneAtATime.Release();
        }
    }

    private static JsonNode Blocked(string label, Func<JsonNode> read)
    {
        try
        {
            return read();
        }
        catch (SiteException e) when (e.Message.Contains("anti-robot", StringComparison.Ordinal))
        {
            throw new SiteException($"{e.Message} Passer la vérification dans l'onglet {label}, puis relancer le projet.", e);
        }
    }

    /// <summary>Prochaine requête pour ce site. Attend au plus <paramref name="wait"/>. null : rien à faire.</summary>
    public async Task<RelayRequest?> NextAsync(string site, TimeSpan wait, CancellationToken ct)
    {
        var state = State(site);
        var wasConnected = Connected(state);
        state.LastPoll = _clock.GetUtcNow();
        Interlocked.Increment(ref state.Polling);
        try
        {
            if (!wasConnected) SiteConnected?.Invoke(site);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(wait);
            while (true)
            {
                var pending = await state.Queue.Reader.ReadAsync(limit.Token);
                if (pending.Result.Task.IsCompleted) continue; // abandonnée (délai dépassé)
                _sent[pending.Request.Id] = pending;
                return pending.Request;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            Interlocked.Decrement(ref state.Polling);
            state.LastPoll = _clock.GetUtcNow();
        }
    }

    /// <summary>Résultat d'une requête. false : requête inconnue ou abandonnée.</summary>
    public bool Complete(string id, RelayResult result) =>
        _sent.TryRemove(id, out var pending) && pending.Result.TrySetResult(result);
}
