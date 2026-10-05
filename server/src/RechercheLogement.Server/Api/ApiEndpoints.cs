using System.Reflection;
using RechercheLogement.Core.Contracts;
using RechercheLogement.Core.Services;

namespace RechercheLogement.Server.Api;

/// <summary>API utilisée par le plugin (toutes les routes exigent le token, voir <see cref="ApiTokenMiddleware"/>).</summary>
public static class ApiEndpoints
{
    public static string Version { get; } =
        typeof(ApiEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";

    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // Vérifie la connexion et le token.
        api.MapGet("/ping", (ServerSettings s) => new ServerInfo("recherche-logement", Version, s.PublicUrl));

        // Observations + actions du plugin -> état des annonces concernées.
        api.MapPost("/sync", (SyncRequest request, Catalog catalog) =>
        {
            if ((request.Observations?.Count ?? 0) > 1000 || (request.Actions?.Count ?? 0) > 1000)
                return Results.BadRequest(new { error = "Lot trop volumineux (max 1000)" });
            return Results.Ok(catalog.Sync(request));
        });

        // État d'une annonce.
        api.MapGet("/listings/{key}", (string key, Catalog catalog) =>
            catalog.View(key) is { } v ? Results.Ok(v) : Results.NotFound());

        // Sauvegarde complète.
        api.MapGet("/export", (Catalog catalog) =>
            Results.Json(catalog.Export(), JsonDefaults.Options, statusCode: 200));
    }
}
