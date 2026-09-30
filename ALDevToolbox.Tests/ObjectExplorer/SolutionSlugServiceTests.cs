using ALDevToolbox.Data.Migrations;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// A solution's slug through <see cref="ProjectService"/>: derived on create, kept on a
/// rename, typed or regenerated on request, unique per org among active solutions - and
/// the lookups the readable routes use, including the environment one on
/// <see cref="UpgradeFleetService"/>, plus the migration's backfill of existing rows.
/// </summary>
public sealed class SolutionSlugServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private const int OwnerUserId = 9200;

    public SolutionSlugServiceTests()
    {
        using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = OwnerUserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "slug-owner@example.com",
            PasswordHash = "x",
            DisplayName = "Owner",
            Role = UserRole.Editor,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.SaveChanges();
        _db.OrgContext.CurrentUserId = OwnerUserId;
    }

    public void Dispose() => _db.Dispose();

    private ProjectService Svc(ALDevToolbox.Data.AppDbContext ctx)
    {
        var access = new ProjectAccess(ctx, _db.OrgContext);
        var discovery = new ProjectDiscoveryService(
            ctx, _db.OrgContext, access, new ProjectDiscoveryQueue(), NullLogger<ProjectDiscoveryService>.Instance);
        return new ProjectService(ctx, _db.OrgContext, access, discovery, NullLogger<ProjectService>.Instance);
    }

    private static ProjectInput Input(string name, string? slug = null, string? shortName = null) =>
        new(name, shortName, "dk", Array.Empty<ProjectRepositoryInput>(), slug);

    private async Task<string?> SlugOf(int id)
    {
        await using var ctx = _db.NewContext();
        return await ctx.OeProjects.AsNoTracking().Where(p => p.Id == id).Select(p => p.Slug).SingleAsync();
    }

    [Fact]
    public async Task Create_derives_the_slug_from_the_name()
    {
        await using var ctx = _db.NewContext();
        var id = await Svc(ctx).CreateProjectAsync(Input("Jørgensen Møbler A/S"));

        (await SlugOf(id)).Should().Be("jorgensen-mobler-a-s");
    }

    [Fact]
    public async Task Create_prefers_the_short_name()
    {
        await using var ctx = _db.NewContext();
        var id = await Svc(ctx).CreateProjectAsync(Input("AB Jensen A/S", shortName: "ABJ"));

        (await SlugOf(id)).Should().Be("abj");
    }

    [Fact]
    public async Task Two_names_that_fold_the_same_get_distinct_slugs()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var first = await svc.CreateProjectAsync(Input("CRONUS A/S"));
        var second = await svc.CreateProjectAsync(Input("CRONUS A-S"));
        var third = await svc.CreateProjectAsync(Input("Cronus (A S)"));

        (await SlugOf(first)).Should().Be("cronus-a-s");
        (await SlugOf(second)).Should().Be("cronus-a-s-2");
        (await SlugOf(third)).Should().Be("cronus-a-s-3");
    }

    [Fact]
    public async Task A_deleted_solution_frees_its_slug()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var old = await svc.CreateProjectAsync(Input("CRONUS"));
        await svc.SoftDeleteProjectAsync(old);

        var fresh = await svc.CreateProjectAsync(Input("CRONUS"));

        (await SlugOf(fresh)).Should().Be("cronus");
    }

    [Fact]
    public async Task A_rename_keeps_the_slug_unless_one_is_asked_for()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var id = await svc.CreateProjectAsync(Input("CRONUS"));

        await svc.UpdateProjectAsync(id, Input("CRONUS Danmark"));
        (await SlugOf(id)).Should().Be("cronus");

        await svc.UpdateProjectAsync(id, Input("CRONUS Danmark", slug: ""));
        (await SlugOf(id)).Should().Be("cronus-danmark");

        await svc.UpdateProjectAsync(id, Input("CRONUS Danmark", slug: " CRONUS-DK "));
        (await SlugOf(id)).Should().Be("cronus-dk");
    }

    [Fact]
    public async Task A_pasted_link_is_read_as_its_last_part()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var id = await svc.CreateProjectAsync(Input("CRONUS"));

        await svc.UpdateProjectAsync(id, Input("CRONUS", "https://workbench.example/solutions/Cronus-DK/"));

        (await SlugOf(id)).Should().Be("cronus-dk");
    }

    [Theory]
    [InlineData("cronus dk", "Use lowercase letters")]
    [InlineData("42", "at least one letter")]
    [InlineData("new", "reserved")]
    public async Task A_typed_slug_that_breaks_the_rules_is_refused_on_its_field(string slug, string message)
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var id = await svc.CreateProjectAsync(Input("CRONUS"));

        var act = () => svc.UpdateProjectAsync(id, Input("CRONUS", slug));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Slug").WhoseValue.Should().Contain(message);
        (await SlugOf(id)).Should().Be("cronus");
    }

    [Fact]
    public async Task A_typed_slug_another_solution_has_is_refused()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        await svc.CreateProjectAsync(Input("CRONUS"));
        var other = await svc.CreateProjectAsync(Input("Fabrikam"));

        var act = () => svc.UpdateProjectAsync(other, Input("Fabrikam", "cronus"));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Slug");
    }

    [Fact]
    public async Task Slug_lookups_find_active_solutions_and_ignore_case()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var id = await svc.CreateProjectAsync(Input("CRONUS"));
        var gone = await svc.CreateProjectAsync(Input("Fabrikam"));
        await svc.SoftDeleteProjectAsync(gone);

        (await svc.FindIdBySlugAsync("cronus")).Should().Be(id);
        (await svc.FindIdBySlugAsync("CRONUS")).Should().Be(id);
        (await svc.FindIdBySlugAsync("fabrikam")).Should().BeNull();
        (await svc.FindIdBySlugAsync("42")).Should().BeNull();
        (await svc.FindVisibleSlugAsync(id)).Should().Be("cronus");
        (await svc.FindVisibleSlugAsync(gone)).Should().BeNull();
    }

    [Fact]
    public async Task A_slug_in_another_organisation_is_not_found()
    {
        await using (var seed = _db.NewContext())
        {
            seed.OeProjects.Add(new OeProject
            {
                OrganizationId = TestDb.OtherOrgId,
                Name = "Elsewhere",
                Slug = "elsewhere",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        (await Svc(ctx).FindIdBySlugAsync("elsewhere")).Should().BeNull();
    }

    [Fact]
    public async Task An_environment_is_found_by_solution_slug_and_name()
    {
        int envId;
        await using (var ctx = _db.NewContext())
        {
            var projectId = await Svc(ctx).CreateProjectAsync(Input("CRONUS"));
            var env = new OeProjectEnvironment
            {
                OrganizationId = TestDb.DefaultOrgId,
                ProjectId = projectId,
                Name = "Production",
                Type = "Production",
                FetchedAt = DateTime.UtcNow,
            };
            ctx.OeProjectEnvironments.Add(env);
            ctx.OeProjectEnvironments.Add(new OeProjectEnvironment
            {
                OrganizationId = TestDb.DefaultOrgId,
                ProjectId = projectId,
                Name = "Old",
                Type = "Sandbox",
                FetchedAt = DateTime.UtcNow,
                MissingSince = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
            envId = env.Id;
        }

        await using var read = _db.NewContext();
        var fleet = new UpgradeFleetService(read, _db.OrgContext, new ProjectAccess(read, _db.OrgContext),
            new EnvironmentRefreshQueue(), NullLogger<UpgradeFleetService>.Instance);

        (await fleet.FindEnvironmentIdAsync("cronus", "Production")).Should().Be(envId);
        (await fleet.FindEnvironmentIdAsync("cronus", "production")).Should().Be(envId);
        (await fleet.FindEnvironmentIdAsync("cronus", "Old")).Should().BeNull();
        (await fleet.FindEnvironmentIdAsync("fabrikam", "Production")).Should().BeNull();

        var row = await fleet.GetEnvironmentAsync(envId);
        row!.Fleet.Href().Should().Be("/environments/cronus/Production");
        row.Fleet.Href("apps").Should().Be("/environments/cronus/Production/apps");
        row.Fleet.SolutionHref.Should().Be("/solutions/cronus");
    }

    [Fact]
    public async Task The_backfill_never_leaves_two_active_solutions_on_one_slug()
    {
        // "CRONUS 5" folds to what the second "CRONUS" would be suffixed to when its id
        // is 5, and long all-digit names get both the prefix and the suffix.
        var ids = new List<int>();
        await using (var seed = _db.NewContext())
        {
            // Names are unique, short names are not: the second row collides through its short name.
            var rows = new (string Name, string? ShortName)[]
            {
                ("CRONUS", null), ("CRONUS Holding", "CRONUS"), ("cronus!", null),
                (new string('1', 70), null), (new string('1', 70) + "!", null),
            };
            foreach (var (name, shortName) in rows)
            {
                var p = new OeProject
                {
                    OrganizationId = TestDb.DefaultOrgId,
                    Name = name,
                    ShortName = shortName,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                seed.OeProjects.Add(p);
                await seed.SaveChangesAsync();
                ids.Add(p.Id);
            }
            // The clash the id suffix cannot see on its own.
            seed.OeProjects.Add(new OeProject
            {
                OrganizationId = TestDb.DefaultOrgId,
                Name = $"CRONUS {ids[1]}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        // As the migration runs it: the backfill first, then the unique index, which
        // is what would fail - and block startup - if two active rows shared a slug.
        await using (var run = _db.NewContext())
        {
            await run.Database.ExecuteSqlRawAsync("DROP INDEX ix_oe_projects_organization_id_slug;");
            await run.Database.ExecuteSqlRawAsync(AddSolutionSlug.BackfillSql);
            await run.Database.ExecuteSqlRawAsync(
                "CREATE UNIQUE INDEX ix_oe_projects_organization_id_slug ON oe_projects (organization_id, slug) WHERE deleted_at IS NULL;");
        }

        await using var read = _db.NewContext();
        var slugs = await read.OeProjects.AsNoTracking()
            .Where(p => p.DeletedAt == null && p.Slug != null)
            .Select(p => p.Slug!)
            .ToListAsync();
        slugs.Should().OnlyHaveUniqueItems();
        slugs.Should().OnlyContain(s => SolutionSlug.IsValid(s));
    }

    [Fact]
    public async Task The_backfill_gives_existing_solutions_unique_valid_slugs()
    {
        var names = new[] { "CRONUS A/S", "CRONUS A-S", "Jørgensen Møbler", "2024", "New", "!!!" };
        var ids = new List<int>();
        await using (var seed = _db.NewContext())
        {
            foreach (var name in names)
            {
                var p = new OeProject
                {
                    OrganizationId = TestDb.DefaultOrgId,
                    Name = name,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                seed.OeProjects.Add(p);
                await seed.SaveChangesAsync();
                ids.Add(p.Id);
            }
            var withShort = new OeProject
            {
                OrganizationId = TestDb.DefaultOrgId,
                Name = "AB Jensen A/S",
                ShortName = "ABJ",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            seed.OeProjects.Add(withShort);
            await seed.SaveChangesAsync();
            ids.Add(withShort.Id);
            // A deleted row may share a slug with an active one; the index ignores it.
            seed.OeProjects.Add(new OeProject
            {
                OrganizationId = TestDb.DefaultOrgId,
                Name = "CRONUS A/S (old)",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                DeletedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using (var run = _db.NewContext())
        {
            await run.Database.ExecuteSqlRawAsync(AddSolutionSlug.BackfillSql);
        }

        await using var read = _db.NewContext();
        var slugs = await read.OeProjects.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .OrderBy(p => p.Id)
            .Select(p => p.Slug!)
            .ToListAsync();

        slugs.Should().Equal(
            "cronus-a-s",
            $"cronus-a-s-{ids[1]}",
            "jorgensen-mobler",
            "solution-2024",
            "solution-new",
            "solution",
            "abj");
        slugs.Should().OnlyContain(s => SolutionSlug.IsValid(s));
    }
}
