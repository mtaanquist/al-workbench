using ALDevToolbox.Services;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Startup;

/// <summary>
/// The authored content behind the generator: templates, modules, the
/// catalogue and application versions.
/// </summary>
public static class ContentRegistration
{
    /// <summary>Registers the template / module / catalogue services.</summary>
    public static IServiceCollection AddContent(this IServiceCollection services)
    {
        services.AddScoped<FolderTreeHydrator>();
        services.AddScoped<TemplateService>();
        services.AddScoped<ModuleService>();
        // Modules copied from a GitHub repository: the copy on save, and the
        // refresh on push, on demand and daily (#1415).
        services.AddScoped<ModuleSourceService>();
        services.AddScoped<CatalogService>();
        services.AddScoped<ApplicationVersionService>();
        services.AddScoped<TemplateImportService>();
        return services;
    }
}
