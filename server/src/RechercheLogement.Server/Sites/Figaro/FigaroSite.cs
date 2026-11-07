using RechercheLogement.Core.Projects;
using RechercheLogement.Server.Geo;

namespace RechercheLogement.Server.Sites.Figaro;

/// <summary>
/// Figaro Immobilier (immobilier.lefigaro.fr). Recherche seulement (<see cref="FigaroSearchAdapter"/>), par le serveur
/// MCP public du site : son API web est interdite aux robots par son robots.txt. Pas de parser pour le plugin.
/// </summary>
public sealed class FigaroSite : ISiteModule, IDisposable
{
    public const string Id = "figaro";
    /// <summary>Délai entre deux appels au serveur MCP.</summary>
    public const int DefaultDelayMs = 1_500;

    private readonly FigaroMcpClient _mcp;

    public FigaroSite(IHttpClientFactory http, GeoServices geo) : this(http, geo, DefaultDelayMs) { }

    public FigaroSite(IHttpClientFactory http, GeoServices geo, int delayMs)
    {
        _mcp = new FigaroMcpClient(http, delayMs);
        Search = new FigaroSearchAdapter(_mcp, geo);
    }

    public SiteInfo Info { get; } = new(Id, "Figaro Immobilier", SiteCategory.ImmoPortal,
        "Par le serveur MCP public du site : 20 annonces par appel, la recherche est découpée par tranches de prix. Pas de recherche par code postal.");
    public FigaroSearchAdapter Search { get; }

    public void Dispose() => _mcp.Dispose();

    IListingParser? ISiteModule.Parser => null;
    ISearchAdapter? ISiteModule.Search => Search;
}
