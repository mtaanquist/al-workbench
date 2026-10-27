using System.IO.Compression;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Tests.Configuration;

/// <summary>
/// Verifies generation reads from the acting user's organisation_settings,
/// organization_files and organization_assets — never another tenant's
/// (Milestone P3.14 done-when bullet 4).
/// </summary>
public sealed class CrossOrgConfigIsolationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Generation_uses_only_the_acting_orgs_logo()
    {
        // Two organisations, each with a different logo. The Default org
        // generates a workspace; the resulting ZIP must carry the Default
        // org's bytes, not Other's.
        await using (var ctx = _db.NewContext())
        {
            ctx.OrganizationAssets.AddRange(
                new OrganizationAsset
                {
                    OrganizationId = TestDb.DefaultOrgId,
                    Kind = OrganizationAssetKind.Logo,
                    ContentType = "image/png",
                    Content = new byte[] { 0xDE, 0xFA, 0x01 },
                    UpdatedAt = DateTime.UtcNow,
                },
                new OrganizationAsset
                {
                    OrganizationId = TestDb.OtherOrgId,
                    Kind = OrganizationAssetKind.Logo,
                    ContentType = "image/png",
                    Content = new byte[] { 0x07, 0x77, 0x07 },
                    UpdatedAt = DateTime.UtcNow,
                });
            ctx.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-default", organizationId: TestDb.DefaultOrgId));
            await ctx.SaveChangesAsync();
        }

        using var zip = await GenerateAsAsync(TestDb.DefaultOrgId, "runtime-default");

        var logoEntry = zip.GetEntry("AcmeCustomer/.assets/images/logo.png");
        logoEntry.Should().NotBeNull("the workspace pulls the acting org's logo");
        using var ms = new MemoryStream();
        await using (var s = logoEntry!.Open()) await s.CopyToAsync(ms);
        ms.ToArray().Should().Equal(new byte[] { 0xDE, 0xFA, 0x01 });
    }

    [Fact]
    public async Task Each_org_generates_its_own_template_with_its_own_files()
    {
        // Same template key and same file path in both orgs, each template
        // opted into its own org's file. Only the tenant filter tells the two
        // apart, so a leak shows up as the other org's content.
        await SeedTemplateWithEditorConfigAsync(TestDb.DefaultOrgId, "shared-key", "default-org-content");
        await SeedTemplateWithEditorConfigAsync(TestDb.OtherOrgId, "shared-key", "other-org-content");

        using (var defaultZip = await GenerateAsAsync(TestDb.DefaultOrgId, "shared-key"))
        {
            ReadEntry(defaultZip, "AcmeCustomer/.editorconfig").Should().Be("default-org-content");
        }
        using var otherZip = await GenerateAsAsync(TestDb.OtherOrgId, "shared-key");
        ReadEntry(otherZip, "AcmeCustomer/.editorconfig").Should().Be("other-org-content");
    }

    [Fact]
    public async Task Another_orgs_template_cannot_be_generated()
    {
        await SeedTemplateWithEditorConfigAsync(TestDb.DefaultOrgId, "default-only", "default-org-content");

        var act = () => GenerateAsAsync(TestDb.OtherOrgId, "default-only");

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("TemplateKey");
    }

    [Fact]
    public async Task GetForAsync_refuses_a_different_org_while_a_request_is_in_scope()
    {
        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);

        // Another org: refused.
        var act = () => svc.GetForAsync(TestDb.OtherOrgId);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cross-organisation config read*");

        // Same org is allowed (no throw).
        var ownAct = () => svc.GetForAsync(TestDb.DefaultOrgId);
        await ownAct.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetForAsync_allows_any_org_when_no_request_is_in_scope()
    {
        // Pre-auth / seed / bootstrap callers have no org in scope and may load
        // any org's config to populate it. See #489.
        _db.OrgContext.CurrentOrganizationId = null;
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);

        var act = () => svc.GetForAsync(TestDb.OtherOrgId);
        await act.Should().NotThrowAsync();
    }

    private async Task SeedTemplateWithEditorConfigAsync(int orgId, string key, string content)
    {
        await using var ctx = _db.NewContext();
        var file = new OrganizationFile
        {
            OrganizationId = orgId,
            Path = ".editorconfig",
            Content = content,
            Ordering = 0,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OrganizationFiles.Add(file);
        // Saved first so the join below has a key to point at.
        await ctx.SaveChangesAsync();

        var template = TemplateBuilder.Default(key, organizationId: orgId);
        template.IncludedFiles.Add(new RuntimeTemplateIncludedFile
        {
            OrganizationId = orgId,
            OrganizationFileId = file.Id,
            Ordering = 0,
        });
        ctx.RuntimeTemplates.Add(template);
        await ctx.SaveChangesAsync();
    }

    private async Task<ZipArchive> GenerateAsAsync(int orgId, string templateKey)
    {
        _db.OrgContext.CurrentOrganizationId = orgId;
        var ctx = _db.NewContext();
        var mustache = new MustacheRenderer(NullLogger<MustacheRenderer>.Instance);
        var gen = new GenerationService(
            ctx,
            _db.NewOrganizationConfigService(ctx),
            new FolderTreeHydrator(ctx),
            _db.OrgContext,
            mustache,
            new WorkspaceZipBuilder(mustache, new WorkspaceConfigService(ctx)),
            NullLogger<GenerationService>.Instance);

        var archive = await gen.GenerateWorkspaceAsync(PlanBuilder.WorkspacePlan(templateKey: templateKey));
        return new ZipArchive(archive.Stream, ZipArchiveMode.Read);
    }

    private static string ReadEntry(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        entry.Should().NotBeNull("{0} should be in the ZIP", path);
        using var reader = new StreamReader(entry!.Open());
        return reader.ReadToEnd();
    }
}
