using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// Which third-party modules a customer has. The organisation keeps a short catalogue;
/// an online solution is matched against what Business Central last reported as
/// installed, an on-premises one has its modules typed in. See
/// <c>.design/solution-customer-info.md</c>, "Modules".
/// </summary>
public sealed class CustomerModuleService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly ILogger<CustomerModuleService> _logger;

    public CustomerModuleService(
        AppDbContext db, IOrganizationContext orgContext, ProjectAccess access, ILogger<CustomerModuleService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _access = access;
        _logger = logger;
    }

    // ── The catalogue ───────────────────────────────────────────────────

    /// <summary>
    /// The catalogue by name, with how many solutions have each module either way - each
    /// solution counted once, and an online one only for what its card shows.
    /// </summary>
    public async Task<List<CatalogModule>> ListCatalogAsync(CancellationToken ct = default)
    {
        var modules = await _db.CustomerModules.AsNoTracking().ToListAsync(ct);
        var typed = await _db.OeProjectModules.AsNoTracking()
            .Where(m => OnPremisesProjectIds().Contains(m.ProjectId))
            .Select(m => new { m.ModuleId, m.ProjectId })
            .ToListAsync(ct);
        var installed = await MatchInstalledAsync(modules, OnlineProjectIds(), ct);

        return modules
            .Select(m => new CatalogModule(m.Id, m.Name, m.Publisher, m.AppId,
                typed.Where(t => t.ModuleId == m.Id).Select(t => t.ProjectId)
                    .Concat(installed.GetValueOrDefault(m.Id) ?? [])
                    .Distinct().Count()))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Apps Business Central has reported somewhere in the organisation that are not in
    /// the catalogue yet, most widespread first - so adding a module is a pick, not a
    /// GUID typed from memory. Microsoft's own apps are left out: every customer has them.
    /// </summary>
    public async Task<List<SeenApp>> ListSeenAppsAsync(CancellationToken ct = default)
    {
        var known = await _db.CustomerModules.AsNoTracking()
            .Where(m => m.AppId != null).Select(m => m.AppId!.Value).ToListAsync(ct);
        var rows = await _db.OeEnvironmentApps.AsNoTracking()
            .Where(a => !known.Contains(a.AppId) && a.Publisher != "Microsoft"
                && a.Environment!.Project!.DeletedAt == null)
            .GroupBy(a => new { a.AppId, a.Name, a.Publisher })
            .Select(g => new { g.Key.AppId, g.Key.Name, g.Key.Publisher, Solutions = g.Select(a => a.Environment!.ProjectId).Distinct().Count() })
            .ToListAsync(ct);
        return rows
            .GroupBy(r => r.AppId)
            .Select(g => g.OrderByDescending(r => r.Solutions).First())
            .OrderByDescending(r => r.Solutions).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new SeenApp(r.AppId, r.Name, r.Publisher, r.Solutions))
            .ToList();
    }

    public async Task SaveCatalogModuleAsync(int? moduleId, CatalogModuleInput input, CancellationToken ct = default)
    {
        var orgId = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("Changing the module catalogue needs an authenticated request.");

        var errors = new Dictionary<string, string>();
        var name = Clean(input.Name);
        var publisher = Clean(input.Publisher);
        Guid? appId = null;

        if (name is null) errors["Name"] = "Enter the module's name.";
        else if (name.Length > 250) errors["Name"] = "Keep the name under 250 characters.";
        if (publisher?.Length > 250) errors["Publisher"] = "Keep the publisher under 250 characters.";
        if (Clean(input.AppId) is { } typed)
        {
            if (Guid.TryParse(typed, out var parsed)) appId = parsed;
            else errors["AppId"] = "An app ID looks like 11111111-2222-3333-4444-555555555555. Leave it empty for a module that isn't a Business Central app.";
        }

        if (name is not null && await _db.CustomerModules.AnyAsync(m => m.Name == name && m.Id != moduleId, ct))
            errors["Name"] = "There is already a module with that name.";
        if (appId is { } id && await _db.CustomerModules.AnyAsync(m => m.AppId == id && m.Id != moduleId, ct))
            errors["AppId"] = "Another module in the catalogue already has that app ID.";
        if (errors.Count > 0) throw new PlanValidationException(errors);

        CustomerModule row;
        if (moduleId is { } existing)
        {
            row = await _db.CustomerModules.FirstOrDefaultAsync(m => m.Id == existing, ct)
                ?? throw Invalid("Name", "That module no longer exists.");
        }
        else
        {
            row = new CustomerModule { OrganizationId = orgId, CreatedAt = DateTime.UtcNow };
            _db.CustomerModules.Add(row);
        }

        row.Name = name!;
        row.Publisher = publisher;
        row.AppId = appId;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Saved customer module {ModuleName}.", name);
    }

    /// <summary>Removes a module from the catalogue, and with it every version typed against it.</summary>
    public async Task DeleteCatalogModuleAsync(int moduleId, CancellationToken ct = default)
    {
        _ = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("Changing the module catalogue needs an authenticated request.");
        var row = await _db.CustomerModules.FirstOrDefaultAsync(m => m.Id == moduleId, ct);
        if (row is null) return;
        _db.CustomerModules.Remove(row);
        await _db.SaveChangesAsync(ct);
    }

    // ── One solution's modules ──────────────────────────────────────────

    /// <summary>
    /// What a solution has. For an online solution that is the catalogue matched against
    /// what its production environments last reported - by app id, or by name and
    /// publisher for a catalogue entry without one. For an on-premises one it is what
    /// somebody typed.
    /// </summary>
    public async Task<SolutionModules> GetSolutionModulesAsync(int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);
        var project = await _db.OeProjects.AsNoTracking()
            .Where(p => p.Id == projectId && p.DeletedAt == null)
            .Select(p => new { p.HostingType })
            .FirstOrDefaultAsync(ct);
        if (project is null) return new SolutionModules(false, [], null, null, 0);

        var catalogCount = await _db.CustomerModules.CountAsync(ct);
        var onPremises = project.HostingType is not (null or ProjectHostingType.MicrosoftCloud);
        if (onPremises)
        {
            var typed = await _db.OeProjectModules.AsNoTracking()
                .Where(m => m.ProjectId == projectId)
                .Select(m => new SolutionModule(m.Id, m.ModuleId, m.Module!.Name, m.Module.Publisher, m.Version, m.Note, null))
                .ToListAsync(ct);
            return new SolutionModules(false, Sorted(typed), null, null, catalogCount);
        }

        var chosen = (await ChooseEnvironmentsAsync(_db.OeProjects.Where(p => p.Id == projectId).Select(p => p.Id), ct))
            .GetValueOrDefault(projectId);
        if (chosen is null) return new SolutionModules(true, [], null, null, catalogCount);

        var apps = await _db.OeEnvironmentApps.AsNoTracking().Where(a => a.EnvironmentId == chosen.Id).ToListAsync(ct);
        var catalog = await _db.CustomerModules.AsNoTracking().ToListAsync(ct);
        var matched = new List<SolutionModule>();
        foreach (var module in catalog)
        {
            var app = MatchApp(module, apps);
            if (app is not null)
                matched.Add(new SolutionModule(0, module.Id, module.Name, module.Publisher ?? app.Publisher, app.Version, null, chosen.Id));
        }

        return new SolutionModules(true, Sorted(matched), chosen.Name,
            apps.Count == 0 ? null : apps.Max(a => a.FetchedAt), catalogCount, chosen.Id);
    }

    /// <summary>Records a catalogue module on an on-premises solution, or changes its version and note.</summary>
    public async Task SaveSolutionModuleAsync(int projectId, int? rowId, SolutionModuleInput input, CancellationToken ct = default)
    {
        var project = await LoadManagedProjectAsync(projectId, ct);
        if (!project.IsOnPremises)
            throw Invalid("ModuleId", "This solution is on Business Central online, so its modules are read from the environment rather than typed in.");

        var errors = new Dictionary<string, string>();
        var version = Clean(input.Version);
        var note = Clean(input.Note);
        if (version?.Length > 50) errors["Version"] = "Keep the version under 50 characters.";
        if (note?.Length > 250) errors["Note"] = "Keep the note under 250 characters.";
        if (input.ModuleId is not { } moduleId || !await _db.CustomerModules.AnyAsync(m => m.Id == moduleId, ct))
            errors["ModuleId"] = "Choose a module from the catalogue.";
        else if (await _db.OeProjectModules.AnyAsync(m => m.ProjectId == projectId && m.ModuleId == moduleId && m.Id != rowId, ct))
            errors["ModuleId"] = "That module is already listed. Change its version there.";
        if (errors.Count > 0) throw new PlanValidationException(errors);

        OeProjectModule row;
        if (rowId is { } id)
        {
            row = await _db.OeProjectModules.FirstOrDefaultAsync(m => m.Id == id && m.ProjectId == projectId, ct)
                ?? throw Invalid("ModuleId", "That entry no longer exists.");
        }
        else
        {
            row = new OeProjectModule { OrganizationId = project.OrganizationId, ProjectId = projectId, CreatedAt = DateTime.UtcNow };
            _db.OeProjectModules.Add(row);
        }

        row.ModuleId = input.ModuleId!.Value;
        row.Version = version;
        row.Note = note;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteSolutionModuleAsync(int projectId, int rowId, CancellationToken ct = default)
    {
        await LoadManagedProjectAsync(projectId, ct);
        var row = await _db.OeProjectModules.FirstOrDefaultAsync(m => m.Id == rowId && m.ProjectId == projectId, ct);
        if (row is null) return;
        _db.OeProjectModules.Remove(row);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The ids of the solutions that have a module, typed in or installed - the Solutions
    /// list's filter. Callers intersect it with what the viewer may see.
    /// </summary>
    public async Task<HashSet<int>> ListProjectIdsWithModuleAsync(int moduleId, CancellationToken ct = default)
    {
        var module = await _db.CustomerModules.AsNoTracking().FirstOrDefaultAsync(m => m.Id == moduleId, ct);
        if (module is null) return [];

        // Typed rows only count for an on-premises solution: one that moved online keeps
        // its old rows, but its card reads the environment and no longer shows them.
        var ids = await _db.OeProjectModules.AsNoTracking()
            .Where(m => m.ModuleId == moduleId && OnPremisesProjectIds().Contains(m.ProjectId))
            .Select(m => m.ProjectId).ToListAsync(ct);
        // The one environment the card reads from, matched the way the card matches, so
        // the filter never lists a customer whose card does not show the module - such
        // as one that only has it on a sandbox next to production (#1515).
        var installed = await MatchInstalledAsync([module], OnlineProjectIds(), ct);
        ids.AddRange(installed.GetValueOrDefault(module.Id) ?? []);
        return ids.ToHashSet();
    }

    /// <summary>
    /// One module across many solutions, each read the way
    /// <see cref="GetSolutionModulesAsync"/> reads it - typed for on-premises,
    /// the chosen environment's report for online - but in a fixed handful of
    /// queries rather than several per solution. A solution that does not
    /// have the module still gets an entry, with no <see cref="ModuleHolding.Module"/>.
    /// </summary>
    /// <remarks>
    /// No per-solution access check: callers pass only solutions the viewer can
    /// already see (the MCP tool intersects with the visibility-filtered list).
    /// </remarks>
    public async Task<List<ModuleHolding>> GetModuleAcrossSolutionsAsync(
        int moduleId, IReadOnlyCollection<int> projectIds, CancellationToken ct = default)
    {
        var module = await _db.CustomerModules.AsNoTracking().FirstOrDefaultAsync(m => m.Id == moduleId, ct);
        if (module is null || projectIds.Count == 0) return [];

        var projects = await _db.OeProjects.AsNoTracking()
            .Where(p => projectIds.Contains(p.Id) && p.DeletedAt == null)
            .Select(p => new { p.Id, p.HostingType })
            .ToListAsync(ct);
        var onPremisesIds = projects
            .Where(p => p.HostingType is not (null or ProjectHostingType.MicrosoftCloud))
            .Select(p => p.Id).ToList();
        var onlineIds = projects.Select(p => p.Id).Except(onPremisesIds).ToList();

        var typed = await _db.OeProjectModules.AsNoTracking()
                .Where(m => onPremisesIds.Contains(m.ProjectId) && m.ModuleId == moduleId)
                .Select(m => new { m.ProjectId, Row = new SolutionModule(m.Id, m.ModuleId, m.Module!.Name, m.Module.Publisher, m.Version, m.Note, null) })
                .ToListAsync(ct);

        var chosen = await ChooseEnvironmentsAsync(_db.OeProjects.Where(p => onlineIds.Contains(p.Id)).Select(p => p.Id), ct);
        var chosenIds = chosen.Values.Select(e => e.Id).ToList();

        var fetched = await _db.OeEnvironmentApps.AsNoTracking()
                .Where(a => chosenIds.Contains(a.EnvironmentId))
                .GroupBy(a => a.EnvironmentId)
                .Select(g => new { EnvironmentId = g.Key, FetchedAt = g.Max(a => a.FetchedAt) })
                .ToDictionaryAsync(g => g.EnvironmentId, g => g.FetchedAt, ct);

        // Narrowed in SQL to the module's app id, or to its name for a catalogue
        // entry without one; the publisher check stays with the shared matcher.
        var appQuery = _db.OeEnvironmentApps.AsNoTracking().Where(a => chosenIds.Contains(a.EnvironmentId));
        var moduleName = module.Name.ToLower();
        appQuery = module.AppId is { } appId
            ? appQuery.Where(a => a.AppId == appId)
            : appQuery.Where(a => a.Name.ToLower() == moduleName);
        var apps = await appQuery.ToListAsync(ct);

        var result = new List<ModuleHolding>(projects.Count);
        foreach (var project in projects)
        {
            if (onPremisesIds.Contains(project.Id))
            {
                result.Add(new ModuleHolding(project.Id, false,
                    typed.FirstOrDefault(t => t.ProjectId == project.Id)?.Row, null, null));
                continue;
            }

            if (chosen.GetValueOrDefault(project.Id) is not { } environment)
            {
                result.Add(new ModuleHolding(project.Id, true, null, null, null));
                continue;
            }

            var app = MatchApp(module, apps.Where(a => a.EnvironmentId == environment.Id));
            result.Add(new ModuleHolding(project.Id, true,
                app is null ? null : new SolutionModule(0, module.Id, module.Name, module.Publisher ?? app.Publisher, app.Version, null, environment.Id),
                environment.Name,
                fetched.TryGetValue(environment.Id, out var at) ? at : null));
        }
        return result;
    }

    // ── Which environment an online solution is read from ──────────────

    /// <summary>The environment an online solution's modules are read from.</summary>
    private sealed record ReadEnvironment(int Id, int ProjectId, string Name);

    /// <summary>The online, not deleted solutions, as a query to compose into others.</summary>
    private IQueryable<int> OnlineProjectIds() =>
        _db.OeProjects.AsNoTracking()
            .Where(p => p.DeletedAt == null
                && (p.HostingType == null || p.HostingType == ProjectHostingType.MicrosoftCloud))
            .Select(p => p.Id);

    /// <summary>The on-premises, not deleted solutions: the ones whose modules are typed in.</summary>
    private IQueryable<int> OnPremisesProjectIds() =>
        _db.OeProjects.AsNoTracking()
            .Where(p => p.DeletedAt == null
                && p.HostingType != null && p.HostingType != ProjectHostingType.MicrosoftCloud)
            .Select(p => p.Id);

    /// <summary>
    /// For each of the given solutions, the one environment its modules are read from:
    /// production first, so a solution with only sandboxes still has something to show,
    /// then by name. A deleted or missing environment is never the one read from: its app
    /// list is whatever was installed on the day it went, not what the customer has now.
    /// The card, the Solutions list filter and the catalogue counts all go through here so
    /// they cannot disagree about which customers have a module (#1515).
    /// </summary>
    private async Task<Dictionary<int, ReadEnvironment>> ChooseEnvironmentsAsync(IQueryable<int> projectIds, CancellationToken ct)
    {
        var environments = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(EnvironmentQueries.NotSoftDeleted)
            .Where(e => projectIds.Contains(e.ProjectId) && e.MissingSince == null)
            .Select(e => new { e.Id, e.ProjectId, e.Name, e.Type })
            .ToListAsync(ct);
        return environments
            .GroupBy(e => e.ProjectId)
            .ToDictionary(g => g.Key, g => g
                .OrderByDescending(e => string.Equals(e.Type, "Production", StringComparison.OrdinalIgnoreCase))
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(e => new ReadEnvironment(e.Id, e.ProjectId, e.Name))
                .First());
    }

    /// <summary>
    /// For each catalogue module, the given solutions whose read environment has it
    /// installed. Only the apps that could match are loaded: by the modules' app ids, or
    /// their names for an entry without one; the publisher check stays with the matcher.
    /// </summary>
    private async Task<Dictionary<int, List<int>>> MatchInstalledAsync(
        IReadOnlyCollection<CustomerModule> modules, IQueryable<int> projectIds, CancellationToken ct)
    {
        if (modules.Count == 0) return [];
        var chosen = await ChooseEnvironmentsAsync(projectIds, ct);
        var chosenIds = chosen.Values.Select(e => e.Id).ToList();
        var appIds = modules.Where(m => m.AppId is not null).Select(m => m.AppId!.Value).ToList();
        var names = modules.Where(m => m.AppId is null).Select(m => m.Name.ToLower()).ToList();
        var apps = await _db.OeEnvironmentApps.AsNoTracking()
            .Where(a => chosenIds.Contains(a.EnvironmentId)
                && (appIds.Contains(a.AppId) || names.Contains(a.Name.ToLower())))
            .ToListAsync(ct);
        var byEnvironment = apps.ToLookup(a => a.EnvironmentId);

        return modules.ToDictionary(m => m.Id, m => chosen.Values
            .Where(e => MatchApp(m, byEnvironment[e.Id]) is not null)
            .Select(e => e.ProjectId)
            .ToList());
    }

    /// <summary>A catalogue entry's app among an environment's: by app id, or by name and publisher.</summary>
    private static OeEnvironmentApp? MatchApp(CustomerModule module, IEnumerable<OeEnvironmentApp> apps) =>
        module.AppId is { } id
            ? apps.FirstOrDefault(a => a.AppId == id)
            : apps.FirstOrDefault(a =>
                string.Equals(a.Name, module.Name, StringComparison.OrdinalIgnoreCase)
                && (module.Publisher is null || string.Equals(a.Publisher, module.Publisher, StringComparison.OrdinalIgnoreCase)));

    private static List<SolutionModule> Sorted(IEnumerable<SolutionModule> modules) =>
        modules.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private async Task<OeProject> LoadManagedProjectAsync(int projectId, CancellationToken ct)
    {
        _ = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("Changing a solution's modules needs an authenticated request.");
        var project = await _db.OeProjects.FirstOrDefaultAsync(p => p.Id == projectId && p.DeletedAt == null, ct)
            ?? throw Invalid("ModuleId", "This solution no longer exists.");
        if (!await _access.CanManageAsync(project.Id, project.CreatedByUserId, ct))
        {
            throw new ProjectAccessDeniedException(project.Visibility == ProjectVisibility.ReadOnly
                ? "This solution is read-only for people outside its teams. Ask one of them, or its owner or an administrator, to make the change."
                : "Only this solution's teams, its owner and your administrators can change it.");
        }
        return project;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static PlanValidationException Invalid(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });
}

public sealed record CatalogModule(int Id, string Name, string? Publisher, Guid? AppId, int Solutions);
public sealed record CatalogModuleInput(string? Name, string? Publisher, string? AppId);

/// <summary>An app some environment reported that the catalogue does not have yet.</summary>
public sealed record SeenApp(Guid AppId, string Name, string Publisher, int Solutions);

/// <param name="RowId">The typed row's id, or 0 for a module read from an environment.</param>
/// <param name="EnvironmentId">Where it was read from, for an online solution.</param>
public sealed record SolutionModule(int RowId, int ModuleId, string Name, string? Publisher, string? Version, string? Note, int? EnvironmentId);

public sealed record SolutionModuleInput(int? ModuleId, string? Version, string? Note);

/// <summary>One solution's answer for one module, from <see cref="CustomerModuleService.GetModuleAcrossSolutionsAsync"/>.</summary>
/// <param name="Module">The module as this solution has it, or null when it does not.</param>
/// <param name="EnvironmentName">The environment read from, for an online solution that has one.</param>
/// <param name="FetchedAt">When that environment last reported its apps.</param>
public sealed record ModuleHolding(
    int ProjectId, bool ReadFromEnvironment, SolutionModule? Module, string? EnvironmentName, DateTime? FetchedAt);

/// <param name="ReadFromEnvironment">True for an online solution, whose list is read rather than typed.</param>
/// <param name="EnvironmentName">The environment the list came from, when there is one.</param>
/// <param name="FetchedAt">When Business Central last answered; null when it has not been read yet.</param>
/// <param name="CatalogCount">How many modules the catalogue has, so an empty list can say why it is empty.</param>
public sealed record SolutionModules(
    bool ReadFromEnvironment,
    List<SolutionModule> Modules,
    string? EnvironmentName,
    DateTime? FetchedAt,
    int CatalogCount,
    int? EnvironmentId = null);
