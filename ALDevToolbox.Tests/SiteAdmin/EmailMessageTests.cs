using ALDevToolbox.Services;
using ALDevToolbox.Services.Operations;
using AwesomeAssertions;
using MimeKit;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// Header and body shaping for outgoing mail, asserted through
/// <see cref="SmtpEmailService.BuildMessage"/> so no SMTP server is involved.
///
/// This is the half of sending that fails quietly. A MimeKit change that alters
/// address or subject encoding still delivers, and only shows up as mail that
/// renders wrong in someone's client, so the "send a test email" smoke check in
/// SiteAdmin passes right through it. Every assertion here goes through a
/// serialise-and-reparse round trip rather than reading the properties back,
/// because the properties return what was set regardless of what would actually
/// go on the wire.
/// </summary>
public sealed class EmailMessageTests
{
    private static ResolvedSmtpSettings Smtp(string from = "noreply@example.com", string? fromName = "AL Workbench") =>
        new(Host: "smtp.example.com", Port: 587, User: null, Password: null,
            From: from, FromName: fromName, UseStartTls: true);

    /// <summary>
    /// Writes the message out and reads it back, so assertions see the encoded
    /// form a receiving client would parse rather than the in-memory values.
    /// </summary>
    private static MimeMessage RoundTrip(MimeMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        stream.Position = 0;
        return MimeMessage.Load(stream);
    }

    [Fact]
    public void From_pairs_the_display_name_with_the_configured_address()
    {
        var message = RoundTrip(SmtpEmailService.BuildMessage(
            Smtp(), "user@example.com", new EmailContent("Subject", "<p>Body</p>")));

        var from = message.From.Mailboxes.Single();
        from.Name.Should().Be("AL Workbench");
        from.Address.Should().Be("noreply@example.com");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void From_falls_back_to_the_bare_address_when_no_display_name_is_set(string? fromName)
    {
        var message = RoundTrip(SmtpEmailService.BuildMessage(
            Smtp(fromName: fromName), "user@example.com", new EmailContent("Subject", "<p>Body</p>")));

        var from = message.From.Mailboxes.Single();
        from.Address.Should().Be("noreply@example.com");
        from.Name.Should().BeNullOrEmpty("a blank display name must not become part of the address");
    }

    [Fact]
    public void Recipient_survives_the_round_trip()
    {
        var message = RoundTrip(SmtpEmailService.BuildMessage(
            Smtp(), "user@example.com", new EmailContent("Subject", "<p>Body</p>")));

        message.To.Mailboxes.Single().Address.Should().Be("user@example.com");
    }

    [Theory]
    [InlineData("Reset your password")]
    // Non-ASCII has to survive RFC 2047 encoding on the way out and decoding on
    // the way back. This is the case a library upgrade is most likely to break.
    [InlineData("Nulstil din adgangskode (æøå)")]
    [InlineData("Passwort zurücksetzen — CRONUS A/S")]
    public void Subject_survives_the_round_trip(string subject)
    {
        var message = RoundTrip(SmtpEmailService.BuildMessage(
            Smtp(), "user@example.com", new EmailContent(subject, "<p>Body</p>")));

        message.Subject.Should().Be(subject);
    }

    [Fact]
    public void A_display_name_with_non_ascii_characters_survives_the_round_trip()
    {
        var message = RoundTrip(SmtpEmailService.BuildMessage(
            Smtp(fromName: "AL Workbench — Ærø"), "user@example.com", new EmailContent("Subject", "<p>Body</p>")));

        var from = message.From.Mailboxes.Single();
        from.Name.Should().Be("AL Workbench — Ærø");
        from.Address.Should().Be("noreply@example.com");
    }

    [Fact]
    public void Body_is_sent_as_html()
    {
        const string html = "<p>Hello <strong>CRONUS A/S</strong></p>";

        var message = RoundTrip(SmtpEmailService.BuildMessage(
            Smtp(), "user@example.com", new EmailContent("Subject", html)));

        message.Body.Should().BeOfType<TextPart>();
        var body = (TextPart)message.Body;
        body.ContentType.MimeType.Should().Be("text/html");
        // MIME terminates the body with a line break, so the round trip adds one.
        // Trim it rather than baking it into the expectation, which would also
        // make the assertion depend on the platform's line ending.
        body.Text.TrimEnd('\r', '\n').Should().Be(html);
    }

    [Fact]
    public void Html_body_with_non_ascii_content_survives_the_round_trip()
    {
        // Long enough to push the encoder past a single line, which is where a
        // transfer-encoding change would corrupt the content rather than the headers.
        var html = "<p>" + string.Concat(Enumerable.Repeat("Ærø, Ålborg og Østerbro. ", 20)) + "</p>";

        var message = RoundTrip(SmtpEmailService.BuildMessage(
            Smtp(), "user@example.com", new EmailContent("Subject", html)));

        // Trailing line break trimmed for the same reason as above.
        message.Body.Should().BeOfType<TextPart>()
            .Which.Text.TrimEnd('\r', '\n').Should().Be(html);
    }

    [Fact]
    public void A_plain_text_part_makes_the_body_multipart_alternative_with_text_first()
    {
        var message = RoundTrip(SmtpEmailService.BuildMessage(
            Smtp(), "user@example.com", new EmailContent("Subject", "<p>Hello</p>", "Hello")));

        var alternative = message.Body.Should().BeOfType<MultipartAlternative>().Subject;
        alternative.Select(p => p.ContentType.MimeType).Should().Equal("text/plain", "text/html");
        alternative.TextBody!.TrimEnd('\r', '\n').Should().Be("Hello");
        alternative.HtmlBody!.TrimEnd('\r', '\n').Should().Be("<p>Hello</p>");
    }
}
