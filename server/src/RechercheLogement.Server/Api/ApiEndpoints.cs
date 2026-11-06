using System.Reflection;
using RechercheLogement.Core.Contracts;
using RechercheLogement.Core.Projects;
using RechercheLogement.Core.Services;
using RechercheLogement.Server.Projects;
using RechercheLogement.Server.Sites.Common;

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

        // Relais : prochaine requête à exécuter dans un onglet du site (attente longue), puis sa réponse.
        api.MapGet("/relay/next", async (string site, BrowserRelay relay, CancellationToken ct) =>
            await relay.NextAsync(site, BrowserRelay.PollWait, ct) is { } request ? Results.Ok(request) : Results.NoContent());
        api.MapPost("/relay/{id}/result", (string id, RelayResult result, BrowserRelay relay) =>
            relay.Complete(id, result) ? Results.NoContent() : Results.NotFound());

        // Recherches du serveur sur un site (panneau du plugin) : en cours, ou à lancer.
        api.MapGet("/relay/status", (string site, ProjectRunner runner, ProjectStore store) =>
            new RelayStatus(runner.RunningOn(site)
                .Select(r => new RelayRun(r.ProjectId, store.Get(r.ProjectId)?.Name ?? r.ProjectId, r.Step))
                .ToList(),
                store.Projects().Count(p => p.Active && p.Sites.Contains(site))));
        api.MapPost("/relay/run", (string site, ProjectRunner runner, ProjectStore store) =>
        {
            var ids = store.Projects().Where(p => p.Active && p.Sites.Contains(site)).Select(p => p.Id).ToList();
            foreach (var id in ids) _ = Task.Run(() => runner.RunAsync(id, CancellationToken.None, onlySite: site));
            return new RelayStarted(ids.Count);
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

/// <summary>Recherches en cours sur un site, et nombre de projets actifs qui l'utilisent.</summary>
public sealed record RelayStatus(IReadOnlyList<RelayRun> Running, int Projects);

public sealed record RelayRun(string ProjectId, string Project, string Step);

public sealed record RelayStarted(int Projects);
