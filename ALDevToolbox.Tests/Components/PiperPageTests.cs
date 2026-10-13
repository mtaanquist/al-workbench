using System.Text;
using PiperPage = ALDevToolbox.Components.Pages.Piper;
using ALDevToolbox.Services;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Piper's input box used a <c>@bind</c> on <c>oninput</c>, which sends the
/// whole box as one SignalR message per keystroke. The hub drops the
/// connection on any message over 32 KB, so pasting a few thousand item
/// numbers broke the page, and every keystroke after it broke it again
/// (issue #1253). Long input now arrives as a stream instead.
/// </summary>
public sealed class PiperPageTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public PiperPageTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.JSInterop.SetupModule("./Components/Pages/Piper.razor.js");
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void The_input_box_does_not_post_its_whole_value_per_keystroke()
    {
        var page = _ctx.Render<PiperPage>();

        page.Find("#piper-input").HasAttribute("blazor:oninput").Should().BeFalse(
            "a bound oninput sends the whole box in one message, which the hub refuses past 32 KB");
    }

    [Fact]
    public async Task A_paste_over_the_message_limit_is_read_as_a_stream_and_transformed()
    {
        var page = _ctx.Render<PiperPage>();
        // 6,000 item numbers, one per line: about 48 KB, well past the size that used to
        // close the connection.
        var items = Enumerable.Range(1000000, 6000).Select(i => i.ToString()).ToList();
        var pasted = string.Join("\n", items);
        Encoding.UTF8.GetByteCount(pasted).Should().BeGreaterThan(32 * 1024);

        await page.InvokeAsync(() => page.Instance.OnLargeInputChanged(new FakeStreamReference(pasted)));

        page.Find("#piper-output").TextContent.Should().Be(string.Join("|", items));
        page.Find(".piper-meta").TextContent.Should().StartWith("6000 items");
    }

    [Fact]
    public async Task Input_past_the_size_cap_is_refused_with_a_message_rather_than_read()
    {
        var page = _ctx.Render<PiperPage>();

        await page.InvokeAsync(() => page.Instance.OnLargeInputChanged(
            new FakeStreamReference(length: PiperTransform.MaxInputLength + 1L)));

        page.Find(".piper-meta").TextContent.Should().Contain("Input is too large");
        page.Find("#piper-output").TextContent.Should().BeEmpty();
    }

    [Fact]
    public async Task Short_input_still_arrives_as_a_plain_call()
    {
        var page = _ctx.Render<PiperPage>();

        await page.InvokeAsync(() => page.Instance.OnInputChanged("1001\n1005\n1007"));

        page.Find("#piper-output").TextContent.Should().Be("1001|1005|1007");
    }

    /// <summary>
    /// Stands in for the browser's stream. Like the framework's own, it refuses
    /// before reading anything when the length is over the caller's cap.
    /// </summary>
    private sealed class FakeStreamReference : IJSStreamReference
    {
        private readonly byte[] _bytes;

        public FakeStreamReference(string text)
        {
            _bytes = Encoding.UTF8.GetBytes(text);
            Length = _bytes.Length;
        }

        public FakeStreamReference(long length)
        {
            _bytes = [];
            Length = length;
        }

        public long Length { get; }

        public ValueTask<Stream> OpenReadStreamAsync(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            if (Length > maxAllowedSize)
            {
                throw new ArgumentOutOfRangeException(nameof(maxAllowedSize));
            }
            return ValueTask.FromResult<Stream>(new MemoryStream(_bytes));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
