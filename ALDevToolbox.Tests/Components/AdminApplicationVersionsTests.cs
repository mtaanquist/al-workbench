using ALDevToolbox.Components.Pages.Admin;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Smoke test for the application-version catalogue editor. Same shape as
/// AdminCatalogTests — a permanent blank ghost row as the add affordance plus a
/// populated state — plus the key <c>pattern=</c> mirror of the server's
/// "lowercase letters, digits, hyphens" rule.
/// </summary>
public sealed class AdminApplicationVersionsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public AdminApplicationVersionsTests()
    {
        // The editor registers an Excel-paste handler via JS interop in
        // OnAfterRenderAsync; loose mode lets those calls no-op under bunit.
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("admin@example.com");
        auth.SetRoles("Admin");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ApplicationVersionService>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void Empty_catalogue_renders_a_single_blank_ghost_row_and_no_add_button()
    {
        var cut = _ctx.Render<AdminApplicationVersions>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("tbody tr").Should().HaveCount(1,
                "an empty catalogue still shows the always-present blank ghost row");
            cut.FindAll("button").Any(b => b.TextContent.Contains("Add row"))
                .Should().BeFalse("the explicit Add button was replaced by the inline ghost row");
        });
    }

    [Fact]
    public async Task Populated_catalogue_renders_one_row_per_entry_with_a_kebab_case_key_pattern()
    {
        await using (var seed = _db.NewContext())
        {
            seed.ApplicationVersions.Add(new ApplicationVersion
            {
                OrganizationId = TestDb.DefaultOrgId,
                Key = "bc-2026-w1",
                Name = "Business Central 2026 Release Wave 1",
                Application = "28.0.0.0",
                Runtime = "15.2",
                Ordering = 0,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            seed.ApplicationVersions.Add(new ApplicationVersion
            {
                OrganizationId = TestDb.DefaultOrgId,
                Key = "bc-2025-w2",
                Name = "Business Central 2025 Release Wave 2",
                Application = "27.0.0.0",
                Runtime = "15.0",
                Ordering = 1,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<AdminApplicationVersions>();

        cut.WaitForAssertion(() =>
        {
            // Two seeded rows plus the always-present trailing ghost row.
            var rows = cut.FindAll("tbody tr");
            rows.Should().HaveCount(3);

            var keyInput = cut.Find("tbody tr td input[type=text]");
            keyInput.HasAttribute("pattern").Should().BeTrue();
            // HTML anchors pattern= itself, so the server's regex without its anchors.
            keyInput.GetAttribute("pattern").Should().Be(ValidationPatterns.Key.ToString().TrimStart('^').TrimEnd('$'),
                "the HTML pattern= mirrors the server's kebab-case rule on the Key column");
        });
    }
}
