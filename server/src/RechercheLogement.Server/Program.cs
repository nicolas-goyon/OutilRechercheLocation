using RechercheLogement.Core.Services;
using RechercheLogement.Server;
using RechercheLogement.Server.Api;
using RechercheLogement.Server.Components;
using RechercheLogement.Server.Projects;
using RechercheLogement.Server.Storage;

var builder = WebApplication.CreateBuilder(args);

var settings = builder.Configuration.GetSection(ServerSettings.Section).Get<ServerSettings>() ?? new ServerSettings();
builder.Services.AddSingleton(settings);

builder.Services.AddPersistence(settings, builder.Environment);
builder.Services.AddSingleton(sp => new Catalog(
    sp.GetRequiredService<IPersistence>(),
    new CatalogOptions { PublicUrl = settings.PublicUrl }));
builder.Services.AddSingleton<ApiTokenService>();
builder.Services.AddProjects(settings, builder.Environment);

builder.Services.ConfigureHttpJsonOptions(o => JsonDefaults.Configure(o.SerializerOptions));
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

// Au démarrage : crée le token si nécessaire et l'écrit dans le journal du conteneur.
app.Services.GetRequiredService<ApiTokenService>().EnsureToken();

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/erreur", createScopeForErrors: true);

app.UseMiddleware<ApiTokenMiddleware>();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapApi();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

/// <summary>Point d'accès pour les tests d'intégration (WebApplicationFactory).</summary>
public partial class Program;
