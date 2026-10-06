using System.Net;
using RechercheLogement.Server.Sites.Bienici;
using RechercheLogement.Server.Sites.Common;
using RechercheLogement.Server.Sites.Seloger;

namespace RechercheLogement.Server.Sites;

public static class SitesRegistration
{
    /// <summary>Client HTTP commun + un module par site. Ajouter un site : une ligne ici.</summary>
    public static IServiceCollection AddSites(this IServiceCollection services)
    {
        services.AddHttpClient(SiteHttp.ClientName, c =>
            {
                c.Timeout = TimeSpan.FromSeconds(30);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36");
                c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fr-FR,fr;q=0.9");
                c.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All });

        services.AddSingleton<ISiteModule, BieniciSite>();
        services.AddSingleton<ISiteModule, SelogerSite>();
        services.AddSingleton<SiteRegistry>();
        return services;
    }
}
