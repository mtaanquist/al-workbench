using ALDevToolbox.Components.Shared;
using ALDevToolbox.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Pins the public surface of <see cref="ConfirmDialog"/> — the
/// <c>Task&lt;bool&gt;</c> returned by <c>OpenAsync()</c> must resolve on
/// every dismissal path (Confirm button, Cancel button, backdrop click,
/// Escape key). Regressions here are silent until a user hits Delete, so
/// the dialog is worth covering even though it's a single component.
///
/// PR 15a moved it off the legacy <c>.confirm-modal</c> family onto the design
/// system's <c>.modal-layer</c> / <c>.modal-backdrop</c> / <c>.confirm-dialog</c>,
/// and added two things worth pinning alongside the dismissal paths. The head's
/// glyph and the panel's red tint are **derived** from
/// <c>ConfirmButtonClass</c> rather than passed, so a refactor that tidies the
/// derived flag away drops the tint from every destructive confirm at once
/// without a single caller changing. And Escape only works because focus is
/// moved into the dialog on open: the markup's <c>autofocus</c> is honoured by
/// browsers when the element arrives with the document, not when Blazor inserts
/// it later, so before that fix the trigger kept focus, Tab walked the page
/// behind the scrim and the keydown handler on the layer never fired.
/// </summary>
public sealed class ConfirmDialogTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public ConfirmDialogTests()
    {
        // ConfirmDialog calls ElementReference.FocusAsync() from the focus
        // sentinels — that's a JS interop hop. Default bUnit mode throws on
        // unregistered invocations; loose mode returns the default value so
        // we don't have to stub each call individually.
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        // The head always renders a glyph now, so the catalogue has to be here.
        // Without it the render throws and every assertion below reports the
        // dialog as simply not open, which reads like a logic bug in OpenAsync.
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void A_dialog_opened_on_its_own_parameters_re_voices_itself_while_open()
    {
        // The Upgrades page's update-now confirm carries the choice between running an
        // update now and booking it for later, and the two make opposite promises: one is
        // irreversible, the other is cancellable until it fires. So the title, the button
        // and its colour have to follow the choice without closing the dialog.
        var cut = _ctx.Render<ConfirmDialog>(p => p
            .Add(c => c.Title, "Start these updates?")
            .Add(c => c.ConfirmLabel, "Start the updates")
            .Add(c => c.ConfirmButtonClass, "btn--danger"));

        cut.InvokeAsync(() => cut.Instance.OpenAsync());
        cut.Find(".confirm-dialog__title").TextContent.Should().Be("Start these updates?");
        cut.Find(".confirm-dialog").ClassList.Should().Contain("confirm-dialog--danger");

        cut.Render(p => p
            .Add(c => c.Title, "Book these updates?")
            .Add(c => c.ConfirmLabel, "Book for 20:00 on 30 Aug")
            .Add(c => c.ConfirmButtonClass, "btn"));

        cut.Find(".confirm-dialog__title").TextContent.Should().Be("Book these updates?");
        cut.Find(".confirm-dialog__actions .btn:last-of-type").TextContent.Trim()
            .Should().Be("Book for 20:00 on 30 Aug");
        cut.Find(".confirm-dialog").ClassList.Should().NotContain("confirm-dialog--danger",
            "booking is cancellable, so it must not wear the irreversible action's tint");
    }

    [Fact]
    public void Per_invocation_overrides_outrank_the_markup_while_the_dialog_is_open()
    {
        // The other half of the same rule: a caller that chose this invocation's words
        // must not have them overwritten by the markup's defaults on the next render.
        var cut = _ctx.Render<ConfirmDialog>(p => p.Add(c => c.Title, "Default title"));

        cut.InvokeAsync(() => cut.Instance.OpenAsync("Delete this build?", "It can't be undone.", "Delete"));
        cut.Find(".confirm-dialog__title").TextContent.Should().Be("Delete this build?");

        cut.Render(p => p.Add(c => c.Title, "Something else entirely"));

        cut.Find(".confirm-dialog__title").TextContent.Should().Be("Delete this build?");
    }

    [Fact]
    public void ConfirmDisabled_holds_the_button_for_a_reason_the_caller_owns()
    {
        var cut = _ctx.Render<ConfirmDialog>(p => p
            .Add(c => c.Title, "Book these updates?")
            .Add(c => c.ConfirmLabel, "Book it")
            .Add(c => c.ConfirmDisabled, true));

        cut.InvokeAsync(() => cut.Instance.OpenAsync());

        cut.Find(".confirm-dialog__actions .btn:last-of-type")
            .HasAttribute("disabled").Should().BeTrue(
                "the caller can see the choice in its own body is not yet valid - a booking time that has already passed");

        cut.Render(p => p
            .Add(c => c.Title, "Book these updates?")
            .Add(c => c.ConfirmLabel, "Book it")
            .Add(c => c.ConfirmDisabled, false));

        cut.Find(".confirm-dialog__actions .btn:last-of-type")
            .HasAttribute("disabled").Should().BeFalse();
    }

    [Theory]
    [InlineData(false, "Book it")]
    [InlineData(true, "Cancel")]
    public void Focus_lands_on_a_button_that_can_take_it(bool confirmDisabled, string focused)
    {
        // A disabled button cannot take focus, and focusing one does nothing, so a dialog
        // opened with its confirm held (the licence import, before a file is chosen) left
        // focus on the page behind it and Escape never reached the layer.
        var cut = _ctx.Render<ConfirmDialog>(p => p
            .Add(c => c.ConfirmLabel, "Book it")
            .Add(c => c.ConfirmDisabled, confirmDisabled));

        cut.InvokeAsync(() => cut.Instance.OpenAsync());

        var target = (ElementReference)_ctx.JSInterop.VerifyFocusAsyncInvoke().Arguments[0]!;
        var button = cut.FindAll(".confirm-dialog__actions button").Single(b => b.TextContent.Trim() == focused);
        button.GetAttribute("blazor:elementReference").Should().Be(target.Id);
    }

    [Fact]
    public void Renders_nothing_until_OpenAsync_is_called()
    {
        var cut = _ctx.Render<ConfirmDialog>(p => p
            .Add(c => c.Title, "Delete X?"));

        cut.FindAll("div.modal-layer").Should().BeEmpty(
            "the dialog is closed by default — the parent component opens it on demand");
    }

    [Fact]
    public async Task Confirm_button_resolves_OpenAsync_with_true()
    {
        var cut = _ctx.Render<ConfirmDialog>();

        Task<bool>? resultTask = null;
        // Brace-body lambda so InvokeAsync sees an Action, not Func<Task<bool>>
        // — otherwise it awaits OpenAsync's task, which never completes until
        // we click the button below, and the test deadlocks.
        await cut.InvokeAsync(() =>
        {
            resultTask = cut.Instance.OpenAsync("Delete X?", "Are you sure?", "Delete");
        });

        cut.Find("h2.confirm-dialog__title").TextContent.Should().Be("Delete X?");
        cut.Find(".confirm-dialog__body").TextContent.Should().Be("Are you sure?");

        // The confirm button is the second .btn — it carries the danger class.
        cut.Find("button.btn--danger").Click();

        (await resultTask!).Should().BeTrue();
        cut.FindAll("div.modal-layer").Should().BeEmpty("Confirm closes the dialog");
    }

    [Fact]
    public async Task A_grace_period_holds_the_yes_back_and_cancelling_in_it_answers_no()
    {
        var cut = _ctx.Render<ConfirmDialog>(p => p.Add(c => c.GraceSeconds, 30));
        Task<bool>? resultTask = null;
        await cut.InvokeAsync(() => { resultTask = cut.Instance.OpenAsync("Start?", "It can't be recalled.", "Start"); });

        cut.Find("button.btn--danger").Click();

        resultTask!.IsCompleted.Should().BeFalse("the caller must not hear yes until the bar has run out");
        cut.Find(".confirm-dialog__grace").TextContent.Should().Contain("Nothing has been sent yet");

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel - send nothing").Click();

        (await resultTask).Should().BeFalse();
        cut.FindAll("div.modal-layer").Should().BeEmpty();
    }

    [Fact]
    public async Task A_grace_period_that_runs_out_answers_yes_and_so_does_start_now()
    {
        var cut = _ctx.Render<ConfirmDialog>(p => p.Add(c => c.GraceSeconds, 1));
        Task<bool>? ranOut = null;
        await cut.InvokeAsync(() => { ranOut = cut.Instance.OpenAsync("Start?", "", "Start"); });
        cut.Find("button.btn--danger").Click();
        (await ranOut!.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        cut.Render(p => p.Add(c => c.GraceSeconds, 30));
        Task<bool>? skipped = null;
        await cut.InvokeAsync(() => { skipped = cut.Instance.OpenAsync("Start?", "", "Start"); });
        cut.Find("button.btn--danger").Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Start now").Click();
        (await skipped!).Should().BeTrue();
    }

    [Fact]
    public async Task A_dialog_with_work_to_show_stays_open_after_yes_until_the_caller_closes_it()
    {
        var cut = _ctx.Render<ConfirmDialog>(p => p
            .Add(c => c.WorkingContent, b => b.AddMarkupContent(0, "<p class=\"working\">2 of 5</p>")));
        Task<bool>? resultTask = null;
        await cut.InvokeAsync(() => { resultTask = cut.Instance.OpenAsync("Start?", "Sure?", "Start"); });

        cut.Find("button.btn--danger").Click();

        (await resultTask!).Should().BeTrue();
        cut.Find(".working").TextContent.Should().Be("2 of 5");
        cut.FindAll(".confirm-dialog__actions").Should().BeEmpty("there is no question left to answer");

        // Escape must not dismiss a run that is still going.
        cut.Find("div.modal-layer").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        cut.FindAll(".working").Should().HaveCount(1);

        await cut.InvokeAsync(() => cut.Instance.Close());
        cut.FindAll("div.modal-layer").Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_button_resolves_OpenAsync_with_false()
    {
        var cut = _ctx.Render<ConfirmDialog>();

        Task<bool>? resultTask = null;
        await cut.InvokeAsync(() =>
        {
            resultTask = cut.Instance.OpenAsync("Delete X?", "Are you sure?", "Delete");
        });

        // The cancel button is the first <button class="btn"> inside the
        // actions row — it does not carry the danger modifier.
        var cancel = cut.FindAll("button.btn")
            .First(b => !(b.GetAttribute("class") ?? string.Empty).Contains("btn--danger"));
        cancel.Click();

        (await resultTask!).Should().BeFalse();
        cut.FindAll("div.modal-layer").Should().BeEmpty();
    }

    [Fact]
    public async Task Backdrop_click_resolves_OpenAsync_with_false()
    {
        var cut = _ctx.Render<ConfirmDialog>();

        Task<bool>? resultTask = null;
        await cut.InvokeAsync(() =>
        {
            resultTask = cut.Instance.OpenAsync("Delete X?", "Are you sure?", "Delete");
        });

        cut.Find("div.modal-backdrop").Click();

        (await resultTask!).Should().BeFalse(
            "clicking outside the panel is a cancel affordance — standard modal behaviour");
    }

    [Fact]
    public async Task Escape_keydown_resolves_OpenAsync_with_false()
    {
        var cut = _ctx.Render<ConfirmDialog>();

        Task<bool>? resultTask = null;
        await cut.InvokeAsync(() =>
        {
            resultTask = cut.Instance.OpenAsync("Delete X?", "Are you sure?", "Delete");
        });

        cut.Find("div.modal-layer").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        (await resultTask!).Should().BeFalse(
            "the comment on OnKeyDown calls Escape out as the standard dialog affordance");
    }

    [Fact]
    public async Task Reopening_resolves_the_previous_task_with_false_and_starts_a_fresh_one()
    {
        var cut = _ctx.Render<ConfirmDialog>();

        Task<bool>? first = null;
        await cut.InvokeAsync(() =>
        {
            first = cut.Instance.OpenAsync("First?", "msg", "Yes");
        });

        Task<bool>? second = null;
        await cut.InvokeAsync(() =>
        {
            second = cut.Instance.OpenAsync("Second?", "msg", "Yes");
        });

        (await first!).Should().BeFalse(
            "OpenAsync's comment promises the previous unresolved task is replaced — "
            + "leaking the prior caller's continuation would be a memory hazard");

        cut.Find("h2.confirm-dialog__title").TextContent.Should().Be("Second?");
        cut.Find("button.btn--danger").Click();
        (await second!).Should().BeTrue();
    }

    [Theory]
    [InlineData("btn--danger", true)]
    [InlineData("btn--primary", false)]
    public async Task The_panel_tint_and_glyph_follow_the_confirm_button(string buttonClass, bool expectDanger)
    {
        var cut = _ctx.Render<ConfirmDialog>();

        await cut.InvokeAsync(() =>
        {
            cut.Instance.OpenAsync("Title?", "msg", "Go", buttonClass);
        });

        cut.Find("div.confirm-dialog").ClassList.Should()
            .Match(c => c.Contains("confirm-dialog--danger") == expectDanger,
                "a caller that asked for a red confirm button has already said the action is "
                + "destructive — nothing else in the markup states that coupling");

        cut.FindAll("span.confirm-dialog__icon svg").Should().NotBeEmpty(
            "the head is icon + title in both moods: the glyph changes, it does not vanish");
    }

    [Fact]
    public async Task A_confirm_with_no_message_renders_no_empty_body()
    {
        var cut = _ctx.Render<ConfirmDialog>();

        await cut.InvokeAsync(() =>
        {
            cut.Instance.OpenAsync("Discard changes?", string.Empty, "Discard");
        });

        cut.FindAll("div.confirm-dialog__body").Should().BeEmpty(
            "the body carries its own padding, so an empty one leaves a gap under the title");
    }

    [Fact]
    public async Task The_layer_is_a_labelled_modal_holding_a_backdrop_and_a_panel()
    {
        var cut = _ctx.Render<ConfirmDialog>();

        await cut.InvokeAsync(() =>
        {
            cut.Instance.OpenAsync("Delete X?", "Are you sure?", "Delete");
        });

        var layer = cut.Find("div.modal-layer");
        layer.GetAttribute("role").Should().Be("dialog");
        layer.GetAttribute("aria-modal").Should().Be("true");
        layer.GetAttribute("aria-labelledby").Should().Be(cut.Find("h2.confirm-dialog__title").Id);

        layer.QuerySelector("div.modal-backdrop").Should().NotBeNull(
            "the scrim is what says the page behind is out of play — and it is only "
            + "full-screen because .modal-layer is the fixed, full-cover parent it "
            + "resolves its own `inset: 0` against");
        layer.QuerySelector("div.confirm-dialog").Should().NotBeNull();
    }
}
