using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Dashboard;

/// <summary>
/// Rules behind the /admin dashboard's cues and its "Needs attention" column.
/// Each queue has a definition of "waiting" that a screenshot cannot check —
/// a rejected signup, a revoked invitation and a deleted release all look the
/// same as the live ones until you ask which ones the query counts.
/// </summary>
public sealed class DashboardServiceTests : IDisposable
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private DashboardService NewService(AppDbContext ctx) => new(ctx);

    // ---- the pending-signup queue ----

    [Fact]
    public async Task Only_undecided_signups_are_waiting()
    {
        await using (var seed = _db.NewContext())
        {
            seed.SignupRequests.AddRange(
                Signup("first@cronus.example", Now.AddDays(-9), SignupDecision.Pending),
                Signup("second@cronus.example", Now.AddDays(-2), SignupDecision.Pending),
                Signup("approved@cronus.example", Now.AddDays(-30), SignupDecision.Approved),
                Signup("rejected@cronus.example", Now.AddDays(-40), SignupDecision.Rejected));
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var data = await NewService(ctx).GetAdminDashboardAsync();

        data.Signups.Count.Should().Be(2);
        data.Signups.Any.Should().BeTrue();
        // The oldest one is what the row names, because "2 waiting" and
        // "2 waiting, one of them for nine days" are different situations.
        data.Signups.Label.Should().Be("first@cronus.example");
        data.Signups.At.Should().BeCloseTo(Now.AddDays(-9), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task No_signups_waiting_reports_an_empty_queue()
    {
        await using var ctx = _db.NewContext();
        var data = await NewService(ctx).GetAdminDashboardAsync();

        data.Signups.Should().Be(PendingQueue.Empty);
        data.Signups.Any.Should().BeFalse();
        data.Signups.At.Should().BeNull();
    }

    /// <summary>
    /// The tenant fence, for every query this service makes rather than for one
    /// of them. `DashboardService` has no `IgnoreQueryFilters()` and must never
    /// grow one; seeding a full set of another organisation's rows and asserting
    /// the whole result is empty is the cheapest way to keep that true — a new
    /// query that forgot the filter fails here without anyone adding a test.
    /// </summary>
    [Fact]
    public async Task Nothing_another_organisation_owns_reaches_this_one()
    {
        await using (var seed = _db.NewContext())
        {
            var other = TestDb.OtherOrgId;

            var signup = Signup("theirs@other.example", Now.AddDays(-8), SignupDecision.Pending);
            signup.OrganizationId = other;
            seed.SignupRequests.Add(signup);

            var suggestion = Suggestion("Their recipe", Now.AddDays(-8), RecipeSuggestionDecision.Pending);
            suggestion.OrganizationId = other;
            seed.RecipeSuggestions.Add(suggestion);

            var inviter = NewUser("their-admin@other.example");
            inviter.OrganizationId = other;
            seed.Users.Add(inviter);
            await seed.SaveChangesAsync();

            var invite = Invitation("their-invitee@other.example", inviter.Id, Now.AddDays(-3));
            invite.OrganizationId = other;
            seed.Invites.Add(invite);

            var failed = Release("Their failed import", "failed", Now);
            failed.OrganizationId = other;
            var ready = Release("Their BC 26", "ready", Now);
            ready.OrganizationId = other;
            seed.OeReleases.AddRange(failed, ready);

            var template = TemplateBuilder.Default("theirs", organizationId: other);
            seed.RuntimeTemplates.Add(template);
            var module = ModuleBuilder.Default("their-module", organizationId: other);
            seed.Modules.Add(module);
            var recipe = RecipeBuilder.Default("Their recipe", organizationId: other);
            seed.Recipes.Add(recipe);
            seed.ApplicationVersions.Add(new ApplicationVersion
            {
                OrganizationId = other, Key = "their-bc26", Name = "Their BC 26",
                Application = "26.0.0.0", Runtime = "15.0",
                CreatedAt = Now, UpdatedAt = Now,
            });
            seed.WellKnownDependencies.Add(new WellKnownDependency
            {
                OrganizationId = other, DepId = Guid.NewGuid().ToString(),
                DepName = "Theirs", DepPublisher = "Microsoft", DepVersionDefault = "26.0.0.0",
                CreatedAt = Now, UpdatedAt = Now,
            });
            seed.OeProjects.Add(new ALDevToolbox.Domain.Entities.ObjectExplorer.OeProject
            {
                OrganizationId = other, Name = "Their project",
                CreatedAt = Now, UpdatedAt = Now,
            });

            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        (await svc.GetAdminDashboardAsync()).Should().Be(new AdminDashboardData(
            PendingQueue.Empty, PendingQueue.Empty, PendingQueue.Empty, PendingQueue.Empty,
            CountWithStamp.Empty, CountWithStamp.Empty, CountWithStamp.Empty,
            CountWithStamp.Empty, CountWithStamp.Empty, CountWithStamp.Empty));
        (await svc.GetToolCountsAsync()).Should().Be(ToolCounts.Empty);
    }

    // ---- the pending recipe-suggestion queue ----

    [Fact]
    public async Task Only_undecided_recipe_suggestions_are_waiting()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RecipeSuggestions.AddRange(
                Suggestion("Post a sales invoice", Now.AddDays(-4), RecipeSuggestionDecision.Pending),
                Suggestion("Already approved", Now.AddDays(-11), RecipeSuggestionDecision.Approved),
                Suggestion("Already rejected", Now.AddDays(-12), RecipeSuggestionDecision.Rejected));
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var data = await NewService(ctx).GetAdminDashboardAsync();

        data.RecipeSuggestions.Count.Should().Be(1);
        data.RecipeSuggestions.Label.Should().Be("Post a sales invoice");
    }

    // ---- invitations that ran out ----

    [Fact]
    public async Task Only_unaccepted_unrevoked_expired_invitations_are_waiting()
    {
        await using (var seed = _db.NewContext())
        {
            var inviter = NewUser("admin@cronus.example");
            seed.Users.Add(inviter);
            await seed.SaveChangesAsync();

            seed.Invites.AddRange(
                Invitation("expired@cronus.example", inviter.Id, Now.AddDays(-3)),
                Invitation("still-open@cronus.example", inviter.Id, Now.AddDays(4)),
                Invitation("accepted@cronus.example", inviter.Id, Now.AddDays(-6), acceptedAt: Now.AddDays(-7)),
                Invitation("revoked@cronus.example", inviter.Id, Now.AddDays(-9), revokedAt: Now.AddDays(-10)));
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var data = await NewService(ctx).GetAdminDashboardAsync();

        // An invitation that was accepted or revoked is finished business; one
        // that has not expired yet is nobody's problem yet.
        data.ExpiredInvites.Count.Should().Be(1);
        data.ExpiredInvites.Label.Should().Be("expired@cronus.example");
    }

    // ---- release imports that failed ----

    [Fact]
    public async Task Only_live_failed_releases_are_waiting()
    {
        await using (var seed = _db.NewContext())
        {
            seed.OeReleases.AddRange(
                Release("BC 26 broke", "failed", Now.AddHours(-2)),
                Release("BC 25 also broke", "failed", Now.AddDays(-3)),
                Release("BC 24", "ready", Now.AddDays(-1)),
                Release("BC 23 deleted", "failed", Now.AddDays(-1), deletedAt: Now));
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var data = await NewService(ctx).GetAdminDashboardAsync();

        data.FailedImports.Count.Should().Be(2);
        // Newest first here, unlike the approval queues: the useful thing about
        // a failed import is the one that just failed, not the stale one.
        data.FailedImports.Label.Should().Be("BC 26 broke");
    }

    [Fact]
    public async Task A_failed_pipeline_build_is_not_a_failed_release_import()
    {
        await using (var seed = _db.NewContext())
        {
            var build = Release("CRONUS Sales Extension #247", "failed", Now);
            build.Kind = "project";
            seed.OeReleases.Add(build);
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var data = await NewService(ctx).GetAdminDashboardAsync();

        // Pipeline builds share the releases table with real imports. They
        // belong to whoever ran the build and already carry a status in
        // Pipelines; counting them here would put a permanent red row reading
        // "a release import failed" on the dashboard of every org that builds.
        data.FailedImports.Should().Be(PendingQueue.Empty);
    }

    // ---- the content cues ----

    [Fact]
    public async Task Content_cues_count_live_rows_and_report_the_newest_change()
    {
        var older = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        await using (var seed = _db.NewContext())
        {
            var kept = TemplateBuilder.Default("kept");
            kept.UpdatedAt = older;
            var alsoKept = TemplateBuilder.Default("also-kept");
            alsoKept.UpdatedAt = newer;
            var gone = TemplateBuilder.Default("gone");
            gone.UpdatedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            gone.DeletedAt = newer;
            seed.RuntimeTemplates.AddRange(kept, alsoKept, gone);
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var data = await NewService(ctx).GetAdminDashboardAsync();

        data.Templates.Count.Should().Be(2);
        // The soft-deleted row is the newest of the three; if it leaked into the
        // aggregate the cue would claim a change nobody made.
        data.Templates.At.Should().Be(newer);
    }

    [Fact]
    public async Task Deprecated_rows_still_count_for_the_admin_cue()
    {
        await using (var seed = _db.NewContext())
        {
            var live = RecipeBuilder.Default("Live recipe");
            var deprecated = RecipeBuilder.Default("Deprecated recipe");
            deprecated.Deprecated = true;
            seed.Recipes.AddRange(live, deprecated);
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var data = await NewService(ctx).GetAdminDashboardAsync();

        // Admin lists show deprecated rows, so the cue has to agree with the
        // page it drills into. Only the launcher's tile meta hides them.
        data.Recipes.Count.Should().Be(2);
    }

    [Fact]
    public async Task An_empty_organisation_reports_zeroes_rather_than_nulls()
    {
        await using var ctx = _db.NewContext();
        var data = await NewService(ctx).GetAdminDashboardAsync();

        data.Templates.Should().Be(CountWithStamp.Empty);
        data.Modules.Should().Be(CountWithStamp.Empty);
        data.Recipes.Should().Be(CountWithStamp.Empty);
        data.ApplicationVersions.Should().Be(CountWithStamp.Empty);
        data.CatalogEntries.Should().Be(CountWithStamp.Empty);
        data.Users.Should().Be(CountWithStamp.Empty);
        data.FailedImports.Should().Be(PendingQueue.Empty);
    }

    [Fact]
    public async Task Users_cue_skips_accounts_still_waiting_for_approval()
    {
        var signedIn = new DateTime(2026, 7, 4, 9, 30, 0, DateTimeKind.Utc);

        await using (var seed = _db.NewContext())
        {
            var active = NewUser("active@cronus.example");
            active.LastLoginAt = signedIn;
            var disabled = NewUser("disabled@cronus.example");
            disabled.Status = UserStatus.Disabled;
            var pending = NewUser("pending@cronus.example");
            pending.Status = UserStatus.Pending;
            seed.Users.AddRange(active, disabled, pending);
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var data = await NewService(ctx).GetAdminDashboardAsync();

        // A disabled account is still an account an admin manages; a pending one
        // is the signups queue, and counting it in both would double-report it.
        data.Users.Count.Should().Be(2);
        data.Users.At.Should().Be(signedIn);
    }

    [Fact]
    public async Task A_brand_new_organisation_has_users_who_have_never_signed_in()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Users.AddRange(NewUser("one@cronus.example"), NewUser("two@cronus.example"));
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var data = await NewService(ctx).GetAdminDashboardAsync();

        // Two accounts and no sign-in is a real state, and the nullable Max
        // overload is the only thing standing between it and a thrown
        // "sequence contains no elements". The cue reads "No one has signed in
        // yet" off this null.
        data.Users.Should().Be(new CountWithStamp(2, null));
    }

    // ---- the launcher's tile meta ----

    [Fact]
    public async Task Tool_counts_match_the_page_each_tile_links_to()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default("pickable"));
            var deprecatedTemplate = TemplateBuilder.Default("deprecated");
            deprecatedTemplate.Deprecated = true;
            seed.RuntimeTemplates.Add(deprecatedTemplate);

            seed.Recipes.Add(RecipeBuilder.Default("Pickable recipe"));
            var deprecatedRecipe = RecipeBuilder.Default("Deprecated recipe");
            deprecatedRecipe.Deprecated = true;
            seed.Recipes.Add(deprecatedRecipe);

            var build = Release("CRONUS Sales Extension #247", "ready", Now);
            build.Kind = "project";
            var emptySymbols = Release("CRONUS Banking 28.6.0.0 (symbols)", "ready", Now);
            emptySymbols.Kind = "third_party";
            emptySymbols.DedupKey = "symbols:aaaa:28.6.0.0";
            seed.OeReleases.AddRange(
                emptySymbols,
                Release("BC 26", "ready", Now),
                Release("BC 25 still importing", "ingesting", Now),
                Release("BC 24 failed", "failed", Now),
                build);

            var liveProject = new ALDevToolbox.Domain.Entities.ObjectExplorer.OeProject
            {
                OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS Sales Extension",
                CreatedAt = Now, UpdatedAt = Now,
            };
            var goneProject = new ALDevToolbox.Domain.Entities.ObjectExplorer.OeProject
            {
                OrganizationId = TestDb.DefaultOrgId, Name = "Retired",
                CreatedAt = Now, UpdatedAt = Now, DeletedAt = Now,
            };
            seed.OeProjects.AddRange(liveProject, goneProject);
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var counts = await NewService(ctx).GetToolCountsAsync();

        // Each count has to match the page its tile links to, or the number is
        // uncheckable. /templates lists deprecated templates with a badge...
        counts.Templates.Should().Be(2);
        // ...and /cookbook hides deprecated recipes.
        counts.Recipes.Should().Be(1);
        // A release that is still importing or has failed cannot be browsed, so
        // promising it on the tile would be a lie the user finds out one click
        // later; a pipeline build is not listed by /object-explorer at all, and
        // neither is a symbols package from the feeds with no source in it (#1092).
        counts.Releases.Should().Be(1);
        counts.Projects.Should().Be(1);
    }

    // ---- fixtures ----

    private static SignupRequest Signup(string email, DateTime requestedAt, SignupDecision decision) => new()
    {
        OrganizationId = TestDb.DefaultOrgId,
        Email = email,
        RequestedAt = requestedAt,
        Decision = decision,
        DecidedAt = decision == SignupDecision.Pending ? null : requestedAt.AddHours(1),
    };

    private static RecipeSuggestion Suggestion(string title, DateTime requestedAt, RecipeSuggestionDecision decision) => new()
    {
        OrganizationId = TestDb.DefaultOrgId,
        Title = title,
        Description = "Synthetic suggestion used in tests.",
        Keywords = "test",
        Type = ALDevToolbox.Domain.ValueObjects.RecipeType.Snippet,
        RequestedAt = requestedAt,
        Decision = decision,
        DecidedAt = decision == RecipeSuggestionDecision.Pending ? null : requestedAt.AddHours(1),
    };

    private static Invite Invitation(
        string email, int invitedByUserId, DateTime expiresAt,
        DateTime? acceptedAt = null, DateTime? revokedAt = null) => new()
    {
        OrganizationId = TestDb.DefaultOrgId,
        Email = email,
        Role = UserRole.User,
        TokenHash = Guid.NewGuid().ToString("N"),
        CreatedAt = expiresAt.AddDays(-7),
        ExpiresAt = expiresAt,
        AcceptedAt = acceptedAt,
        RevokedAt = revokedAt,
        InvitedByUserId = invitedByUserId,
    };

    private static OeRelease Release(string label, string status, DateTime updatedAt, DateTime? deletedAt = null) => new()
    {
        OrganizationId = TestDb.DefaultOrgId,
        Label = label,
        Status = status,
        ImportedAt = updatedAt,
        CreatedAt = updatedAt,
        UpdatedAt = updatedAt,
        DeletedAt = deletedAt,
    };

    private static User NewUser(string email) => new()
    {
        OrganizationId = TestDb.DefaultOrgId,
        Email = email,
        DisplayName = email.Split('@')[0],
        PasswordHash = "not-a-real-hash",
        Role = UserRole.Admin,
        Status = UserStatus.Active,
        CreatedAt = Now.AddDays(-30),
    };
}
