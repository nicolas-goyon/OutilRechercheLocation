using RechercheLogement.Core.Projects;

namespace RechercheLogement.Server.Projects;

public static class ProjectsRegistration
{
    /// <summary>"Mes projets" : base séparée, collecteurs des sites, lanceur et planificateur.</summary>
    public static IServiceCollection AddProjects(this IServiceCollection services, ServerSettings settings, IWebHostEnvironment env)
    {
        if (settings.Storage.Equals("memory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IProjectPersistence, InMemoryProjectPersistence>();
        }
        else
        {
            var catalogPath = Path.IsPathRooted(settings.DatabasePath) ? settings.DatabasePath : Path.Combine(env.ContentRootPath, settings.DatabasePath);
            var path = string.IsNullOrWhiteSpace(settings.ProjectsDatabasePath)
                ? Path.Combine(Path.GetDirectoryName(catalogPath) ?? env.ContentRootPath, "projets.db")
                : Path.IsPathRooted(settings.ProjectsDatabasePath) ? settings.ProjectsDatabasePath : Path.Combine(env.ContentRootPath, settings.ProjectsDatabasePath);
            services.AddSingleton<IProjectPersistence>(_ => new SqliteProjectPersistence(path));
        }
        services.AddSingleton(sp => new ProjectStore(sp.GetRequiredService<IProjectPersistence>()));
        services.AddCollectors();
        services.AddSingleton<ProjectRunner>();
        if (settings.ProjectsAutoRun) services.AddHostedService<ProjectScheduler>();
        return services;
    }
}
