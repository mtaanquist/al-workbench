using ALDevToolbox.Services.Notifications;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Notifications;

/// <summary>The one-line detail the build and deployment notifiers show under a title.</summary>
public sealed class NotificationTextTests
{
    [Fact]
    public void A_message_starting_with_a_newline_gives_its_first_real_line()
    {
        NotificationText.FirstLine("\n\r\n  error AL0118: 'Foo' is not defined\nsecond line")
            .Should().Be("error AL0118: 'Foo' is not defined");
    }

    [Fact]
    public void A_long_line_is_cut_at_the_limit_and_says_so()
    {
        var line = NotificationText.FirstLine(new string('x', NotificationText.MaxLineLength + 50));

        line.Should().Be(new string('x', NotificationText.MaxLineLength) + "...");
    }

    [Fact]
    public void A_line_at_the_limit_is_kept_whole()
    {
        var message = new string('x', NotificationText.MaxLineLength);

        NotificationText.FirstLine(message).Should().Be(message);
    }
}
