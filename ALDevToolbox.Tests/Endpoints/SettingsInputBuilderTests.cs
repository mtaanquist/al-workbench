using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The settings tabs each POST only their own fields, so the builder's job is
/// to overlay those onto everything already stored. The checkbox reading is the
/// part with a rule worth pinning: a switch posts a hidden <c>false</c> next to
/// the checkbox so "off" and "not on this form" stay different answers.
/// </summary>
public class SettingsInputBuilderTests
{
    private static SystemSettingsView Current(bool? startTls = true) => new(
        SmtpHost: "smtp.cronus.com",
        SmtpPort: 587,
        SmtpUser: "workbench",
        HasSmtpPassword: true,
        SmtpFrom: "noreply@cronus.com",
        SmtpFromName: "AL Workbench",
        SmtpUseStartTls: startTls,
        BannerText: "Scheduled maintenance on Sunday.",
        BackupScheduleEnabled: true,
        BackupScheduleTimeUtc: new TimeOnly(2, 0),
        BackupRetentionCount: 30,
        PerTenantBackupRetentionCount: 7,
        DefaultStorageQuotaMb: 10_240,
        IndexSizeMultiplier: 1.5m,
        McpEnabled: true,
        SignupEmailDomainAllowlist: "cronus.com",
        ReleaseDownloadDomainAllowlist: "download.microsoft.com",
        DisabledTools: Array.Empty<string>(),
        BuildConcurrency: 6,
        UpdatedAt: new DateTime(2026, 8, 16, 9, 42, 0, DateTimeKind.Utc));

    private static IFormCollection Form(params (string Key, string[] Values)[] fields) =>
        new FormCollection(fields.ToDictionary(f => f.Key, f => new StringValues(f.Values)));

    [Fact]
    public void A_switch_that_is_on_posts_both_values_and_still_reads_as_on()
    {
        // What Switch.razor renders when checked: the paired hidden false, then
        // the checkbox's own true. StringValues joins these as "false,true",
        // which an equality check against the whole string reads as neither.
        var form = Form(("SmtpUseStartTls", ["false", "true"]));

        var input = SettingsInputBuilder.WithSmtp(Current(), form);

        input.SmtpUseStartTls.Should().BeTrue();
    }

    [Fact]
    public void A_switch_that_is_off_posts_false_and_turns_the_setting_off()
    {
        // The bug this pins: an unticked box used to vanish from the form
        // entirely, save null, and resolve back to true through `?? true` -- so
        // STARTTLS could not be switched off from the UI at all.
        var form = Form(("SmtpUseStartTls", ["false"]));

        var input = SettingsInputBuilder.WithSmtp(Current(startTls: true), form);

        input.SmtpUseStartTls.Should().BeFalse();
        ResolvedSmtpSettings.TryFrom(
                host: "smtp.cronus.com", port: 587, user: "workbench", password: "pw",
                from: "noreply@cronus.com", fromName: null,
                useStartTls: input.SmtpUseStartTls)!
            .UseStartTls.Should().BeFalse();
    }

    [Fact]
    public void A_tab_that_does_not_post_the_field_leaves_the_stored_value_alone()
    {
        // The General tab saving must not disturb SMTP.
        var form = Form(("BannerText", ["Back on Monday."]));

        var input = SettingsInputBuilder.WithGeneral(Current(startTls: false), form);

        input.SmtpUseStartTls.Should().BeFalse();
        input.SmtpHost.Should().Be("smtp.cronus.com");
        input.BannerText.Should().Be("Back on Monday.");
    }

    [Fact]
    public void The_tools_tab_disables_every_tool_whose_box_is_absent()
    {
        var form = Form(($"tool_{ToolKey.Mcp}", ["true"]));

        var input = SettingsInputBuilder.WithTools(Current(), form);

        input.McpEnabled.Should().BeTrue();
        input.DisabledTools.Should().NotContain(ToolKey.Mcp);
        input.DisabledTools.Should().Contain(
            ToolCatalog.All.Where(t => t.Key != ToolKey.Mcp).Select(t => t.Key));
    }

    // Issue #970: the backup schedule is typed in the organisation's zone and
    // stored as a UTC time of day. Europe/Copenhagen is UTC+1 in winter and
    // UTC+2 in summer; the stored value must not move either way.
    private static readonly TimeSpan Winter = TimeSpan.FromHours(1);
    private static readonly TimeSpan Summer = TimeSpan.FromHours(2);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void An_unchanged_schedule_round_trips_to_the_same_utc_time_in_winter_and_summer(int offsetHours)
    {
        var offset = TimeSpan.FromHours(offsetHours);
        var shown = DisplayTimeZone.TimeOfDayToDisplay(new TimeOnly(2, 0), offset);
        var form = Form(
            ("BackupScheduleEnabled", ["false", "true"]),
            ("BackupScheduleTime", [DisplayTimeZone.FormatTimeOfDay(shown)]),
            ("BackupScheduleOffsetMinutes", [((int)offset.TotalMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture)]));

        var input = SettingsInputBuilder.WithBackups(Current(), form, offset);

        shown.Should().Be(new TimeOnly(2 + offsetHours, 0));
        input.BackupScheduleTimeUtc.Should().Be(new TimeOnly(2, 0));
    }

    [Fact]
    public void A_page_shown_in_winter_and_saved_after_the_clocks_change_keeps_the_stored_time()
    {
        // Rendered at 03:00 (UTC+1); the save lands after the spring change,
        // when the zone is UTC+2. The posted offset wins over today's.
        var form = Form(
            ("BackupScheduleTime", ["03:00"]),
            ("BackupScheduleOffsetMinutes", ["60"]));

        var input = SettingsInputBuilder.WithBackups(Current(), form, Summer);

        input.BackupScheduleTimeUtc.Should().Be(new TimeOnly(2, 0));
    }

    [Fact]
    public void A_typed_time_is_read_in_the_zone_and_stored_as_utc()
    {
        var form = Form(("BackupScheduleTime", ["01:30"]));

        var input = SettingsInputBuilder.WithBackups(Current(), form, Summer);

        input.BackupScheduleTimeUtc.Should().Be(new TimeOnly(23, 30), "01:30 at UTC+2 is 23:30 UTC the day before");
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("9999")]
    public void A_missing_or_odd_offset_falls_back_to_the_zone_offset_today(string posted)
    {
        var form = Form(("BackupScheduleTime", ["03:00"]), ("BackupScheduleOffsetMinutes", [posted]));

        var input = SettingsInputBuilder.WithBackups(Current(), form, Winter);

        input.BackupScheduleTimeUtc.Should().Be(new TimeOnly(2, 0));
    }

    [Fact]
    public void A_blank_time_leaves_the_stored_schedule_alone()
    {
        var form = Form(("BackupScheduleTime", [""]));

        var input = SettingsInputBuilder.WithBackups(Current(), form, Summer);

        input.BackupScheduleTimeUtc.Should().Be(new TimeOnly(2, 0));
    }

    [Theory]
    [InlineData("4", 4)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    public void The_builds_tab_saves_its_number_and_an_empty_box_clears_it(string posted, int? expected)
    {
        var input = SettingsInputBuilder.WithBuilds(Current(), Form(("BuildConcurrency", [posted])));

        input.BuildConcurrency.Should().Be(expected);
    }

    [Fact]
    public void A_build_limit_that_is_not_a_number_is_passed_on_to_be_refused_not_cleared()
    {
        var input = SettingsInputBuilder.WithBuilds(Current(), Form(("BuildConcurrency", ["many"])));

        input.BuildConcurrency.Should().Be(0, "the service refuses 0 with the field's own message");
    }

    [Fact]
    public void Other_tabs_leave_the_build_limit_alone()
    {
        var input = SettingsInputBuilder.WithGeneral(Current(), Form(("BannerText", ["Back on Monday."])));

        input.BuildConcurrency.Should().Be(6);
    }
}
