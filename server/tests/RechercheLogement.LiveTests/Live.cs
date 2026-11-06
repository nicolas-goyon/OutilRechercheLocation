using System.Net;
using RechercheLogement.Server.Sites.Common;

namespace RechercheLogement.LiveTests;

/// <summary>Accès réseau des tests : mêmes en-têtes que le serveur (SitesRegistration), et quelques nouvelles tentatives.</summary>
internal sealed class LiveHttp : IHttpClientFactory
{
    public static readonly LiveHttp Factory = new();

    private static readonly HttpClient Client = Create();

    public HttpClient CreateClient(string name) => Client;

    private static HttpClient Create()
    {
        var c = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36");
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fr-FR,fr;q=0.9");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        return c;
    }

    /// <summary>Panne réseau passagère : jusqu'à 3 essais, espacés. Une erreur de format, elle, échoue tout de suite.</summary>
    public static async Task<T> Retry<T>(Func<Task<T>> call)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (Exception e) when (attempt < 3 && Transient(e))
            {
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
            }
        }
    }

    private static bool Transient(Exception e) => e switch
    {
        HttpRequestException or TaskCanceledException => true,
        RechercheLogement.Server.Sites.SiteException s => s.Message.Contains("injoignable", StringComparison.Ordinal) || s.Message.Contains("HTTP 5", StringComparison.Ordinal),
        _ => false,
    };
}
