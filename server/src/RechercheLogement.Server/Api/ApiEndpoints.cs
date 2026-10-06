using System.Reflection;
using RechercheLogement.Core.Contracts;
using RechercheLogement.Core.Services;

namespace RechercheLogement.Server.Api;

/// <summary>API du plugin. Toutes les routes exigent le token (voir <see cref="ApiTokenMiddleware"/>).</summary>
public static class ApiEndpoints
{
    public static string Version { get; } =
        typeof(ApiEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";

    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // Vérifie la connexion et le token.
        api.MapGet("/ping", (ServerSettings s) => new ServerInfo("recherche-logement", Version, s.PublicUrl));

        // Reçoit les observations et les actions du plugin. Renvoie l'état des annonces concernées.
        api.MapPost("/sync", (SyncRequest request, Catalog catalog) =>
        {
            if ((request.Observations?.Count ?? 0) > 1000 || (request.Actions?.Count ?? 0) > 1000)
                return Results.BadRequest(new { error = "Lot trop grand. Maximum : 1000 observations et 1000 actions." });
            return Results.Ok(catalog.Sync(request));
        });

        // Renvoie l'état d'une annonce.
        api.MapGet("/listings/{key}", (string key, Catalog catalog) =>
            catalog.View(key) is { } v ? Results.Ok(v) : Results.NotFound());

        // Recherches favorites (liens de recherche des sites d'annonces). Doublons permis.
        api.MapGet("/searches", (Catalog catalog) => catalog.Searches().Select(SavedSearchView.Of).ToList());

        api.MapPost("/searches", (SavedSearchRequest request, Catalog catalog) =>
        {
            try
            {
                var created = catalog.AddSearch(request.Name, request.Url, request.Note);
                return Results.Created($"/recherches#{created.Id}", SavedSearchView.Of(created));
            }
            catch (ArgumentException e)
            {
                return Results.BadRequest(new { error = e.Message });
            }
        });

        // Renvoie une sauvegarde complète.
        api.MapGet("/export", (Catalog catalog) =>
            Results.Json(catalog.Export(), JsonDefaults.Options, statusCode: 200));
    }
}
