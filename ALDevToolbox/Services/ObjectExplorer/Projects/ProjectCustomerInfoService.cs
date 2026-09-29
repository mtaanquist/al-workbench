using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// What support looks up about a customer: where their Business Central runs, how to
/// get in, who to call. Reading follows the solution's visibility, and so does writing -
/// a Public solution is everyone's to correct, a Read-only or Private one its teams'.
/// The hosting type is the exception and stays a manager's call. See
/// <c>.design/solution-customer-info.md</c>.
/// </summary>
public sealed class ProjectCustomerInfoService
{
    public const int VersionMaxLength = 50;
    public const int ClientUrlMaxLength = 500;
    public const int VoiceAccountNumberMaxLength = 30;

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly ILogger<ProjectCustomerInfoService> _logger;

    public ProjectCustomerInfoService(
        AppDbContext db,
        IOrganizationContext orgContext,
        ProjectAccess access,
        ILogger<ProjectCustomerInfoService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _access = access;
        _logger = logger;
    }

    /// <summary>
    /// The basics for one solution, or null when it does not exist in this organisation.
    /// Throws <see cref="ProjectAccessDeniedException"/> for a Private solution the
    /// caller has no grant on.
    /// </summary>
    public async Task<CustomerBasics?> GetBasicsAsync(int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);
        var basics = await _db.OeProjects
            .AsNoTracking()
            .Where(p => p.Id == projectId && p.DeletedAt == null)
            .Select(p => new CustomerBasics(
                p.HostingType, p.BcVersion, p.LicenseType, p.UserExperience,
                p.ClientUrl, p.VoiceAccountNumber, p.BcTenantId, null))
            .FirstOrDefaultAsync(ct);
        if (basics is null) return null;
        var production = await ReadProductionFactsAsync(_db, [projectId], ct);
        return production.TryGetValue(projectId, out var facts) ? basics with { Production = facts } : basics;
    }

    /// <summary>
    /// What Business Central last reported about each solution's production
    /// environment, for the solutions where that overrides what was typed: online, with a
    /// connection configured, and a current Production environment that has told us a
    /// version or an address. A solution missing from the answer keeps its typed values.
    /// <para>
    /// Only Production counts - a sandbox's version is not what support means by "the
    /// customer's version" - and among several the first by name. One read whatever the
    /// number of solutions, and it never touches a secret: whether one is stored is the
    /// whole of the connection test. <paramref name="projectIds"/> null means every
    /// solution in the organisation, for a caller that has already decided which rows
    /// it shows. See <c>.design/solution-customer-info.md</c>, "Hosting and the basics".
    /// </para>
    /// </summary>
    public static async Task<Dictionary<int, ProductionEnvironmentFacts>> ReadProductionFactsAsync(
        AppDbContext db, IReadOnlyCollection<int>? projectIds, CancellationToken ct)
    {
        if (projectIds is { Count: 0 }) return new();

        var connected = ProjectConnectionService.ConfiguredProjects(db)
            .Where(p => p.HostingType == null || p.HostingType == ProjectHostingType.MicrosoftCloud);
        if (projectIds is not null) connected = connected.Where(p => projectIds.Contains(p.Id));
        var connectedIds = connected.Select(p => p.Id);

        var rows = await db.OeProjectEnvironments.AsNoTracking()
            .Where(EnvironmentQueries.NotSoftDeleted)
            .Where(e => connectedIds.Contains(e.ProjectId)
                && e.MissingSince == null
                && e.Type.Trim().ToUpper() == "PRODUCTION"
                && (e.Version != null || e.WebClientLoginUrl != null))
            .Select(e => new { e.ProjectId, e.Name, e.Version, e.WebClientLoginUrl, e.FetchedAt })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.ProjectId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(r => new ProductionEnvironmentFacts(r.Name, Blank(r.Version), Blank(r.WebClientLoginUrl), r.FetchedAt))
                    .First());

        static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Saves the basics. Every field is optional. Refuses an on-premises hosting type
    /// while Business Central online reports environments for the solution: the
    /// surfaces that show them disappear for an on-premises solution, and live ones
    /// must not be hidden.
    /// <para>
    /// The tenant id is the customer's Microsoft tenant, which an on-premises customer
    /// has too. For an online solution the Business Central tab owns it (changing it
    /// there resets the connection), so it is only written from here when the solution
    /// is on-premises and that tab is gone.
    /// </para>
    /// </summary>
    public async Task SaveBasicsAsync(int projectId, CustomerBasicsInput input, CancellationToken ct = default)
    {
        _ = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("Saving customer information needs an authenticated request.");

        var errors = new Dictionary<string, string>();
        var version = Clean(input.BcVersion);
        var url = Clean(input.ClientUrl);
        var voice = Clean(input.VoiceAccountNumber);

        if (version?.Length > VersionMaxLength)
            errors["BcVersion"] = $"Keep the version under {VersionMaxLength} characters, e.g. 'BC 25.3' or 'NAV 2018 CU12'.";
        if (url is not null)
        {
            if (url.Length > ClientUrlMaxLength)
                errors["ClientUrl"] = $"Keep the address under {ClientUrlMaxLength} characters.";
            else if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                     || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                errors["ClientUrl"] = "Enter the full address, starting with https:// or http://.";
        }
        if (voice?.Length > VoiceAccountNumberMaxLength)
            errors["VoiceAccountNumber"] = $"Keep the Voice account number under {VoiceAccountNumberMaxLength} characters.";

        var project = await _db.OeProjects
            .FirstOrDefaultAsync(p => p.Id == projectId && p.DeletedAt == null, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string> { ["HostingType"] = "This solution no longer exists." });
        // Where it is hosted decides which tabs the solution has, and used to be checked
        // separately because editing the rest of this page was deliberately wider than
        // managing the solution. It is the same set now - managing a Public solution is
        // everyone in the organisation - so one check covers the page and the field.
        await EnsureCanEditAsync(project, ct);

        var onPremises = input.HostingType is not (null or ProjectHostingType.MicrosoftCloud);
        Guid? tenantId = null;
        if (onPremises)
        {
            var environments = await _db.OeProjectEnvironments.CountAsync(e => e.ProjectId == project.Id, ct);
            if (environments > 0)
            {
                errors["HostingType"] = environments == 1
                    ? "Business Central online reports an environment for this solution, so it can't be on-premises."
                    : $"Business Central online reports {environments} environments for this solution, so it can't be on-premises.";
            }

            if (Clean(input.TenantId) is { } typed)
            {
                if (Guid.TryParse(typed, out var parsed)) tenantId = parsed;
                else errors["TenantId"] = "A tenant ID looks like 11111111-2222-3333-4444-555555555555.";
            }
        }

        // While Business Central reports the version and the address, the typed ones are
        // hidden, not replaced: a form opened before the connection was made must not
        // overwrite them, and they come back if the connection goes. Nor can what it
        // carried for them fail the save of fields the person could see.
        var fromBusinessCentral = !onPremises
            && (await ReadProductionFactsAsync(_db, [project.Id], ct)).ContainsKey(project.Id);
        if (fromBusinessCentral)
        {
            errors.Remove("BcVersion");
            errors.Remove("ClientUrl");
        }

        if (errors.Count > 0) throw new PlanValidationException(errors);

        project.HostingType = input.HostingType;
        if (!fromBusinessCentral)
        {
            project.BcVersion = version;
            project.ClientUrl = url;
        }
        project.LicenseType = input.LicenseType;
        project.UserExperience = input.UserExperience;
        project.VoiceAccountNumber = voice;
        if (onPremises) project.BcTenantId = tenantId;
        project.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Saved customer basics for project {ProjectId}; hosting {HostingType}.", project.Id, project.HostingType);
    }

    /// <summary>
    /// Everything the Customer tab shows, read one after the other. The tab's sections
    /// are siblings on one circuit and so share this service's <c>DbContext</c>, which
    /// allows one operation at a time - letting each section read for itself on first
    /// render is how "a second operation was started" happens (#741).
    /// </summary>
    public async Task<CustomerInfoSnapshot?> GetAllAsync(int projectId, CancellationToken ct = default)
    {
        var basics = await GetBasicsAsync(projectId, ct);
        if (basics is null) return null;
        return new CustomerInfoSnapshot(
            basics,
            await GetNotesAsync(projectId, ct) ?? new CustomerNotes(null, null, null),
            await ListContactsAsync(projectId, ct),
            await ListPeopleAsync(projectId, ct),
            await ListIntegrationsAsync(projectId, ct));
    }

    /// <summary>
    /// Hosting and version for the Solutions list's columns, by solution id. The caller
    /// passes the ids of rows it is already showing in full - never a locked row's, whose
    /// name is all its viewer may see.
    /// </summary>
    public async Task<Dictionary<int, CustomerListFacts>> ListFactsAsync(IReadOnlyCollection<int> projectIds, CancellationToken ct = default)
    {
        if (projectIds.Count == 0) return new();
        var rows = await _db.OeProjects.AsNoTracking()
            .Where(p => projectIds.Contains(p.Id) && p.DeletedAt == null)
            .Select(p => new { p.Id, p.HostingType, p.BcVersion, p.ClientUrl })
            .ToListAsync(ct);
        var production = await ReadProductionFactsAsync(_db, projectIds, ct);
        return rows.ToDictionary(p => p.Id, p => production.TryGetValue(p.Id, out var facts)
            ? new CustomerListFacts(p.HostingType, facts.Version, facts.WebClientLoginUrl, true)
            : new CustomerListFacts(p.HostingType, p.BcVersion, p.ClientUrl, false));
    }

    // ── Getting in, and notes ───────────────────────────────────────────

    public const int NotesMaxLength = 4000;

    public async Task<CustomerNotes?> GetNotesAsync(int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);
        return await _db.OeProjects.AsNoTracking()
            .Where(p => p.Id == projectId && p.DeletedAt == null)
            .Select(p => new CustomerNotes(p.AccessDescription, p.HostingNotes, p.KnowledgeNotes))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Plain text, deliberately not encrypted - see the design doc. Blank clears.</summary>
    public async Task SaveNotesAsync(int projectId, CustomerNotes input, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        void Check(string field, string? value, string what)
        {
            if (Clean(value)?.Length > NotesMaxLength)
                errors[field] = $"Keep {what} under {NotesMaxLength} characters.";
        }
        Check("AccessDescription", input.AccessDescription, "how to get in");
        Check("HostingNotes", input.HostingNotes, "the hosting notes");
        Check("KnowledgeNotes", input.KnowledgeNotes, "the notes");

        var project = await LoadManagedProjectAsync(projectId, ct);
        if (errors.Count > 0) throw new PlanValidationException(errors);

        project.AccessDescription = Clean(input.AccessDescription);
        project.HostingNotes = Clean(input.HostingNotes);
        project.KnowledgeNotes = Clean(input.KnowledgeNotes);
        project.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Saved customer notes for project {ProjectId}.", projectId);
    }

    // ── Contacts ────────────────────────────────────────────────────────

    /// <summary>The customer's own people first, then by name.</summary>
    public async Task<List<CustomerContact>> ListContactsAsync(int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);
        var rows = await _db.OeProjectContacts.AsNoTracking()
            .Where(c => c.ProjectId == projectId)
            .Select(c => new CustomerContact(c.Id, c.Type, c.Name, c.Company, c.Email, c.Phone))
            .ToListAsync(ct);
        return rows.OrderBy(c => c.Type).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The one person to ring at each customer, by solution id: the first of the customer's
    /// own contacts by name, preferring one with a phone number. For a page that lists many
    /// solutions at once - an upgrade's lines - so it reads every solution's contacts in
    /// one query rather than one per row. A solution with no customer contact, or one the
    /// caller cannot see, is absent from the answer rather than refused: the list it feeds
    /// already shows only what the caller may see, and a blank cell says "nobody recorded".
    /// </summary>
    public async Task<Dictionary<int, CustomerContact>> ListCustomerContactsAsync(
        IReadOnlyCollection<int> projectIds, CancellationToken ct = default)
    {
        if (projectIds.Count == 0) return new();
        var snapshot = await _access.GetSnapshotAsync(ct);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);

        var rows = await _db.OeProjectContacts.AsNoTracking()
            .Where(c => projectIds.Contains(c.ProjectId) && c.Type == ProjectContactType.Customer)
            .Where(c => _db.OeProjects.Where(visible).Any(p => p.Id == c.ProjectId && p.DeletedAt == null))
            .Select(c => new { c.ProjectId, Contact = new CustomerContact(c.Id, c.Type, c.Name, c.Company, c.Email, c.Phone) })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.ProjectId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => r.Contact)
                    .OrderBy(c => string.IsNullOrWhiteSpace(c.Phone))
                    .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .First());
    }

    /// <summary>Adds a contact, or changes one when <paramref name="contactId"/> is given.</summary>
    public async Task SaveContactAsync(int projectId, int? contactId, CustomerContactInput input, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        var name = Clean(input.Name);
        var company = Clean(input.Company);
        var email = Clean(input.Email);
        var phone = Clean(input.Phone);

        if (name is null) errors["Name"] = "Enter the contact's name.";
        else if (name.Length > 100) errors["Name"] = "Keep the name under 100 characters.";
        if (company?.Length > 100) errors["Company"] = "Keep the company under 100 characters.";
        if (email is not null && (email.Length > 200 || !email.Contains('@') || email.Contains(' ')))
            errors["Email"] = "Enter an email address like name@cronus.example.";
        if (phone?.Length > 50) errors["Phone"] = "Keep the phone number under 50 characters.";
        if (name is not null && email is null && phone is null)
            errors["Email"] = "Enter an email address or a phone number, so there is a way to reach them.";

        var project = await LoadManagedProjectAsync(projectId, ct);
        if (errors.Count > 0) throw new PlanValidationException(errors);

        OeProjectContact row;
        if (contactId is { } id)
        {
            row = await _db.OeProjectContacts.FirstOrDefaultAsync(c => c.Id == id && c.ProjectId == projectId, ct)
                ?? throw Gone("Name", "That contact no longer exists.");
        }
        else
        {
            row = new OeProjectContact { OrganizationId = project.OrganizationId, ProjectId = projectId, CreatedAt = DateTime.UtcNow };
            _db.OeProjectContacts.Add(row);
        }

        row.Type = input.Type;
        row.Name = name!;
        row.Company = company;
        row.Email = email;
        row.Phone = phone;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteContactAsync(int projectId, int contactId, CancellationToken ct = default)
    {
        await LoadManagedProjectAsync(projectId, ct);
        var row = await _db.OeProjectContacts.FirstOrDefaultAsync(c => c.Id == contactId && c.ProjectId == projectId, ct);
        if (row is null) return;
        _db.OeProjectContacts.Remove(row);
        await _db.SaveChangesAsync(ct);
    }

    // ── Who knows this customer ─────────────────────────────────────────

    public async Task<List<CustomerPerson>> ListPeopleAsync(int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);
        var rows = await _db.OeProjectPeople.AsNoTracking()
            .Where(p => p.ProjectId == projectId)
            .Select(p => new CustomerPerson(p.Id, p.UserId, p.User!.DisplayName, p.User.Email, p.Role, p.Areas))
            .ToListAsync(ct);
        return rows.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The other direction of the "who knows this customer" list: which solutions a
    /// colleague is on, and as what. <paramref name="person"/> matches a user's email
    /// exactly or any part of their name, ignoring case, so "anne" finds Anne Hansen.
    /// Only solutions the caller can see are answered - a Private one they are not on is
    /// left out, not listed with its name - which is the same rule the per-solution list
    /// applies through <see cref="ListPeopleAsync"/>. Ordered by colleague, then solution.
    /// Built for the <c>list_customer_knowledge</c> MCP tool; the Customer tab only ever
    /// reads one solution.
    /// </summary>
    public async Task<List<KnownCustomer>> ListCustomersKnownByAsync(string person, CancellationToken ct = default)
    {
        var needle = Clean(person);
        if (needle is null) return [];
        var lowered = needle.ToLower();

        var snapshot = await _access.GetSnapshotAsync(ct);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);
        var rows = await _db.OeProjectPeople.AsNoTracking()
            .Where(p => p.User!.Email.ToLower() == lowered || p.User.DisplayName.ToLower().Contains(lowered))
            .Where(p => _db.OeProjects.Where(visible).Any(x => x.Id == p.ProjectId && x.DeletedAt == null))
            .Select(p => new KnownCustomer(
                p.ProjectId, p.Project!.Name, p.UserId, p.User!.DisplayName, p.User.Email, p.Role, p.Areas))
            .ToListAsync(ct);
        return rows
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The organisation's active users, for the picker.</summary>
    public async Task<List<CustomerPersonOption>> ListAssignableUsersAsync(CancellationToken ct = default)
    {
        var rows = await _db.Users.AsNoTracking()
            .Where(u => u.Status == Domain.Entities.UserStatus.Active)
            .Select(u => new CustomerPersonOption(u.Id, u.DisplayName, u.Email))
            .ToListAsync(ct);
        return rows.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Adds one of our people, or changes their role and areas. One row per person.</summary>
    public async Task SavePersonAsync(int projectId, int? personId, CustomerPersonInput input, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        var areas = Clean(input.Areas);
        if (areas?.Length > 250) errors["Areas"] = "Keep the areas under 250 characters.";

        var project = await LoadManagedProjectAsync(projectId, ct);

        // The query filter keeps this to the caller's own organisation.
        if (input.UserId is not { } userId || !await _db.Users.AnyAsync(u => u.Id == userId, ct))
            errors["UserId"] = "Choose one of your colleagues.";
        else if (await _db.OeProjectPeople.AnyAsync(p => p.ProjectId == projectId && p.UserId == userId && p.Id != personId, ct))
            errors["UserId"] = "They are already on the list. Change their role or areas there.";

        if (errors.Count > 0) throw new PlanValidationException(errors);

        OeProjectPerson row;
        if (personId is { } id)
        {
            row = await _db.OeProjectPeople.FirstOrDefaultAsync(p => p.Id == id && p.ProjectId == projectId, ct)
                ?? throw Gone("UserId", "That entry no longer exists.");
        }
        else
        {
            row = new OeProjectPerson { OrganizationId = project.OrganizationId, ProjectId = projectId, CreatedAt = DateTime.UtcNow };
            _db.OeProjectPeople.Add(row);
        }

        row.UserId = input.UserId!.Value;
        row.Role = input.Role;
        row.Areas = areas;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeletePersonAsync(int projectId, int personId, CancellationToken ct = default)
    {
        await LoadManagedProjectAsync(projectId, ct);
        var row = await _db.OeProjectPeople.FirstOrDefaultAsync(p => p.Id == personId && p.ProjectId == projectId, ct);
        if (row is null) return;
        _db.OeProjectPeople.Remove(row);
        await _db.SaveChangesAsync(ct);
    }

    // ── Integrations ────────────────────────────────────────────────────

    public async Task<List<CustomerIntegration>> ListIntegrationsAsync(int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);
        var rows = await _db.OeProjectIntegrations.AsNoTracking()
            .Where(i => i.ProjectId == projectId)
            .Select(i => new CustomerIntegration(i.Id, i.Name, i.Direction))
            .ToListAsync(ct);
        return rows.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task SaveIntegrationAsync(int projectId, int? integrationId, CustomerIntegrationInput input, CancellationToken ct = default)
    {
        var name = Clean(input.Name);
        var project = await LoadManagedProjectAsync(projectId, ct);
        if (name is null) throw Gone("Name", "Enter what Business Central is integrated with.");
        if (name.Length > 100) throw Gone("Name", "Keep the name under 100 characters.");

        OeProjectIntegration row;
        if (integrationId is { } id)
        {
            row = await _db.OeProjectIntegrations.FirstOrDefaultAsync(i => i.Id == id && i.ProjectId == projectId, ct)
                ?? throw Gone("Name", "That integration no longer exists.");
        }
        else
        {
            row = new OeProjectIntegration { OrganizationId = project.OrganizationId, ProjectId = projectId, CreatedAt = DateTime.UtcNow };
            _db.OeProjectIntegrations.Add(row);
        }

        row.Name = name;
        row.Direction = input.Direction;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteIntegrationAsync(int projectId, int integrationId, CancellationToken ct = default)
    {
        await LoadManagedProjectAsync(projectId, ct);
        var row = await _db.OeProjectIntegrations.FirstOrDefaultAsync(i => i.Id == integrationId && i.ProjectId == projectId, ct);
        if (row is null) return;
        _db.OeProjectIntegrations.Remove(row);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>The tracked solution, once the caller is known to be allowed to edit its customer information.</summary>
    private async Task<OeProject> LoadManagedProjectAsync(int projectId, CancellationToken ct)
    {
        _ = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("Changing customer information needs an authenticated request.");
        var project = await _db.OeProjects.FirstOrDefaultAsync(p => p.Id == projectId && p.DeletedAt == null, ct)
            ?? throw Gone("Name", "This solution no longer exists.");
        await EnsureCanEditAsync(project, ct);
        return project;
    }

    /// <summary>
    /// Managing the solution, with a sentence that says which solution refused and why.
    /// The generic refusal names the owner and the admins, which stopped being the whole
    /// answer once teams could manage - and a person who has just been told "no" on a
    /// phone number needs to know whether to ask somebody or to change the level.
    /// </summary>
    private async Task EnsureCanEditAsync(OeProject project, CancellationToken ct)
    {
        if (await _access.CanManageAsync(project.Id, project.CreatedByUserId, ct)) return;
        throw new ProjectAccessDeniedException(project.Visibility == ProjectVisibility.ReadOnly
            ? "This solution is read-only for people outside its teams. Ask one of them, or its owner or an administrator, to make the change."
            : "Only this solution's teams, its owner and your administrators can change it.");
    }

    private static PlanValidationException Gone(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// The basics as read. <paramref name="TenantId"/> is the Business Central connection's,
/// shown for reference. <paramref name="BcVersion"/> and <paramref name="ClientUrl"/> are
/// what was typed; <paramref name="Production"/>, when set, is what Business Central
/// reported instead, and the <c>Effective</c> pair is the one to show.
/// </summary>
public sealed record CustomerBasics(
    ProjectHostingType? HostingType,
    string? BcVersion,
    ProjectLicenseType? LicenseType,
    ProjectUserExperience? UserExperience,
    string? ClientUrl,
    string? VoiceAccountNumber,
    Guid? TenantId,
    ProductionEnvironmentFacts? Production = null)
{
    public bool IsOnPremises => HostingType is not (null or ProjectHostingType.MicrosoftCloud);

    /// <summary>True when the version and the address come from the production environment rather than from what was typed.</summary>
    public bool FromBusinessCentral => Production is not null;

    /// <summary>When Business Central was last asked, while <see cref="FromBusinessCentral"/>.</summary>
    public DateTime? FetchedAt => Production?.FetchedAt;

    public string? EffectiveVersion => Production is { } p ? p.Version : BcVersion;
    public string? EffectiveClientUrl => Production is { } p ? p.WebClientLoginUrl : ClientUrl;

    /// <summary>True when there is nothing to show yet, which is the tab's first-run state.</summary>
    public bool IsEmpty => HostingType is null && EffectiveVersion is null && LicenseType is null
        && UserExperience is null && EffectiveClientUrl is null && VoiceAccountNumber is null;
}

/// <summary>What Business Central last reported for a solution's production environment. See <see cref="ProjectCustomerInfoService.ReadProductionFactsAsync"/>.</summary>
public sealed record ProductionEnvironmentFacts(string EnvironmentName, string? Version, string? WebClientLoginUrl, DateTime FetchedAt);

public sealed record CustomerBasicsInput(
    ProjectHostingType? HostingType,
    string? BcVersion,
    ProjectLicenseType? LicenseType,
    ProjectUserExperience? UserExperience,
    string? ClientUrl,
    string? VoiceAccountNumber,
    string? TenantId = null);

public sealed record CustomerNotes(string? AccessDescription, string? HostingNotes, string? KnowledgeNotes)
{
    public bool IsEmpty => AccessDescription is null && HostingNotes is null && KnowledgeNotes is null;
}

public sealed record CustomerContact(int Id, ProjectContactType Type, string Name, string? Company, string? Email, string? Phone);
public sealed record CustomerContactInput(ProjectContactType Type, string? Name, string? Company, string? Email, string? Phone);

public sealed record CustomerPerson(int Id, int UserId, string Name, string Email, ProjectPersonRole Role, string? Areas);
public sealed record CustomerPersonOption(int UserId, string Name, string Email);

/// <summary>One solution a colleague knows, from <see cref="ProjectCustomerInfoService.ListCustomersKnownByAsync"/>.</summary>
public sealed record KnownCustomer(
    int ProjectId, string ProjectName, int UserId, string Name, string Email, ProjectPersonRole Role, string? Areas);
public sealed record CustomerPersonInput(int? UserId, ProjectPersonRole Role, string? Areas);

public sealed record CustomerIntegration(int Id, string Name, ProjectIntegrationDirection Direction);
public sealed record CustomerIntegrationInput(string? Name, ProjectIntegrationDirection Direction);

/// <summary>The whole Customer tab in one read. See <see cref="ProjectCustomerInfoService.GetAllAsync"/>.</summary>
public sealed record CustomerInfoSnapshot(
    CustomerBasics Basics,
    CustomerNotes Notes,
    List<CustomerContact> Contacts,
    List<CustomerPerson> People,
    List<CustomerIntegration> Integrations);

/// <summary>
/// A row's hosting, and the version and address to show for it - Business Central's when
/// <paramref name="FromBusinessCentral"/>, otherwise what was typed.
/// </summary>
public sealed record CustomerListFacts(ProjectHostingType? HostingType, string? BcVersion, string? ClientUrl = null, bool FromBusinessCentral = false);
