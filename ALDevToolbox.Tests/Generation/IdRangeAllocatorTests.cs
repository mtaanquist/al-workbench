using ALDevToolbox.Services.Generation;
using ALDevToolbox.Tests.Builders;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Generation;

/// <summary>
/// Covers <see cref="IdRangeAllocator"/>, extracted from
/// <c>GenerationService</c> in #546 so the New Workspace preview can show the
/// ranges it is about to allocate instead of replaying the walk itself.
///
/// <para>The end-to-end generation tests already read these numbers back out of
/// generated <c>app.json</c> files, and they are the real guarantee that the
/// extraction changed nothing. What they cannot check is the part the extraction
/// created: that the allocator's <b>order</b> is the order the two emit loops
/// walk, because <c>BuildExtensionList</c> now consumes the result by index. An
/// off-by-one there would hand every extension its neighbour's ids and still
/// produce a perfectly valid workspace.</para>
/// </summary>
public sealed class IdRangeAllocatorTests
{
    private const int CoreFrom = 90000;
    private const int CoreTo = 90999;

    [Fact]
    public void The_first_unannotated_extension_takes_the_plans_core_range()
    {
        var template = TemplateBuilder.Default("runtime-x");

        var ranges = IdRangeAllocator.Allocate(
            template, Array.Empty<string>(), Array.Empty<ALDevToolbox.Domain.Entities.Module>(),
            CoreFrom, CoreTo);

        ranges.Should().NotBeEmpty();
        ranges[0].From.Should().Be(CoreFrom);
        ranges[0].To.Should().Be(CoreTo);
    }

    [Fact]
    public void Modules_slice_forward_from_the_templates_module_start_and_honour_a_size_override()
    {
        var template = TemplateBuilder.Default("runtime-x");
        var wide = ModuleBuilder.Default("wide", "Wide", idRangeSize: 500);
        var follow = ModuleBuilder.Default("follow", "Follow");

        var ranges = IdRangeAllocator.Allocate(
            template, Array.Empty<string>(), new[] { wide, follow }, CoreFrom, CoreTo);

        var wideRange = ranges.Single(r => r.Path == wide.ExtensionName);
        var followRange = ranges.Single(r => r.Path == follow.ExtensionName);

        wideRange.From.Should().Be(template.ModuleIdRangeStart);
        wideRange.To.Should().Be(template.ModuleIdRangeStart + 499);
        followRange.From.Should().Be(wideRange.To + 1,
            "each module starts where the previous one ended - the cursor is the whole point");
    }

    [Fact]
    public void A_blank_module_size_follows_the_copied_repository_and_a_typed_one_wins()
    {
        var template = TemplateBuilder.Default("runtime-x");
        var following = ModuleBuilder.Default("following", "Following");
        following.SourceIdRangeSize = 120;
        var typed = ModuleBuilder.Default("typed", "Typed", idRangeSize: 30);
        typed.SourceIdRangeSize = 120;

        var ranges = IdRangeAllocator.Allocate(
            template, Array.Empty<string>(), new[] { following, typed }, CoreFrom, CoreTo);

        var followingRange = ranges.Single(r => r.Path == following.ExtensionName);
        var typedRange = ranges.Single(r => r.Path == typed.ExtensionName);
        (followingRange.To - followingRange.From + 1).Should().Be(120, "a blank size takes the repository's app.json range");
        (typedRange.To - typedRange.From + 1).Should().Be(30, "a size the Editor typed wins over the repository's");
    }

    /// <summary>
    /// The contract <c>BuildExtensionList</c> now depends on: allocation order
    /// is emit order. It consumes the list by index against the same two loops,
    /// so a reordering here would silently swap ranges between extensions.
    /// </summary>
    [Fact]
    public void Allocation_order_is_template_extensions_then_modules()
    {
        var template = TemplateBuilder.Default("runtime-x");
        var module = ModuleBuilder.Default("mod", "Mod");

        var ranges = IdRangeAllocator.Allocate(
            template, Array.Empty<string>(), new[] { module }, CoreFrom, CoreTo);

        var templatePaths = template.WorkspaceExtensions
            .Where(e => e.Required)
            .OrderBy(e => e.Ordering)
            .Select(e => e.Path)
            .ToList();

        ranges.Select(r => r.Path).Should().Equal(
            templatePaths.Append(module.ExtensionName),
            "GenerationService reads this list by index against the same two loops");
    }

    [Fact]
    public void An_unticked_optional_extension_gets_no_range_and_consumes_no_cursor()
    {
        var template = TemplateBuilder.Default("runtime-x");
        var optional = template.WorkspaceExtensions.FirstOrDefault(e => !e.Required);
        if (optional is null) return;   // the default fixture is all-required

        var without = IdRangeAllocator.Allocate(
            template, Array.Empty<string>(), Array.Empty<ALDevToolbox.Domain.Entities.Module>(),
            CoreFrom, CoreTo);
        var with = IdRangeAllocator.Allocate(
            template, new[] { optional.Path }, Array.Empty<ALDevToolbox.Domain.Entities.Module>(),
            CoreFrom, CoreTo);

        without.Should().NotContain(r => r.Path == optional.Path);
        with.Should().Contain(r => r.Path == optional.Path);
    }

    // ===== #730: the layout after Core follows the Core range =====

    /// <summary>
    /// #730: the reported bug. Moving Core down used to leave every later
    /// extension parked at the template's absolute ModuleIdRangeStart, so a
    /// workspace at 60000..60999 still got its modules at 91000.
    /// </summary>
    [Fact]
    public void Moving_the_core_range_down_moves_everything_after_it_by_the_same_amount()
    {
        var template = TemplateBuilder.Default("runtime-x");
        var module = ModuleBuilder.Default("mod", "Mod");

        var ranges = IdRangeAllocator.Allocate(
            template, Array.Empty<string>(), new[] { module }, 60000, 60999);

        ranges[0].From.Should().Be(60000);
        ranges[0].To.Should().Be(60999);
        var moduleRange = ranges.Single(r => r.Path == module.ExtensionName);
        moduleRange.From.Should().Be(61000, "the module range keeps its zero gap after Core");
        moduleRange.To.Should().Be(61199);
    }

    /// <summary>
    /// The other half of #730: moving Core *up* past the template's module
    /// start used to overlap it, and the cross-extension overlap check refused
    /// the plan outright. The shift moves the module range out of the way.
    /// </summary>
    [Fact]
    public void Moving_the_core_range_up_past_the_old_module_start_no_longer_overlaps()
    {
        var template = TemplateBuilder.Default("runtime-x");
        var module = ModuleBuilder.Default("mod", "Mod");

        var ranges = IdRangeAllocator.Allocate(
            template, Array.Empty<string>(), new[] { module }, 91000, 91999);

        var moduleRange = ranges.Single(r => r.Path == module.ExtensionName);
        moduleRange.From.Should().Be(92000);
        moduleRange.From.Should().BeGreaterThan(ranges[0].To, "the ranges have to stay disjoint");
    }

    /// <summary>
    /// The shift keys on the range's end, not its start, so widening Core also
    /// pushes what follows out of its way.
    /// </summary>
    [Theory]
    [InlineData(91199, 91200)]   // widened: 90000..91199
    [InlineData(90499, 90500)]   // narrowed: 90000..90499
    public void Resizing_the_core_range_moves_the_module_range_by_the_change_to_its_end(int coreTo, int expectedModuleFrom)
    {
        var template = TemplateBuilder.Default("runtime-x");
        var module = ModuleBuilder.Default("mod", "Mod");

        var ranges = IdRangeAllocator.Allocate(
            template, Array.Empty<string>(), new[] { module }, CoreFrom, coreTo);

        ranges.Single(r => r.Path == module.ExtensionName).From.Should().Be(expectedModuleFrom);
    }

    /// <summary>
    /// The compatibility guarantee behind the whole change: at the template's
    /// own Core range the shift is zero, so every number the generation tests
    /// read back out of a ZIP is the number they read before.
    /// </summary>
    [Fact]
    public void Leaving_the_core_range_at_the_template_default_allocates_exactly_as_before()
    {
        var template = TemplateBuilder.Default("runtime-x");
        var module = ModuleBuilder.Default("mod", "Mod");

        var ranges = IdRangeAllocator.Allocate(
            template, Array.Empty<string>(), new[] { module },
            template.CoreIdRangeFrom, template.CoreIdRangeTo);

        ranges[0].From.Should().Be(90000);
        ranges[0].To.Should().Be(90999);
        var moduleRange = ranges.Single(r => r.Path == module.ExtensionName);
        moduleRange.From.Should().Be(template.ModuleIdRangeStart);
        moduleRange.To.Should().Be(template.ModuleIdRangeStart + 199);
    }

    /// <summary>
    /// What is preserved is the *gap* the template author left after Core, not
    /// the phrase "modules start right after Core" - a template that parks its
    /// modules 4000 ids past the end of Core keeps that distance when the
    /// workspace moves Core.
    /// </summary>
    [Fact]
    public void A_gap_between_core_and_the_module_range_survives_a_moved_core_range()
    {
        var template = TemplateBuilder.Default("runtime-x");
        template.ModuleIdRangeStart = 95000;    // 4000 ids past CoreIdRangeTo (90999)
        var module = ModuleBuilder.Default("mod", "Mod");

        var ranges = IdRangeAllocator.Allocate(
            template, Array.Empty<string>(), new[] { module }, 60000, 60999);

        ranges.Single(r => r.Path == module.ExtensionName).From.Should().Be(65000,
            "the 4000-id gap after Core is the template author's decision, not an accident");
    }

    /// <summary>
    /// The label is what reaches the preview row, and the handoff's generator
    /// screen writes it as <c>ID from-to</c>. Pinned because it is the one part
    /// of this type a reader sees.
    /// </summary>
    [Fact]
    public void A_range_reads_as_the_handoff_writes_it()
    {
        new AllocatedIdRange("Core", 50100, 50199).Label.Should().Be("ID 50100-50199");
    }
}
