using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The signed-out forms (signup, signup details, accept-invite, reset-password)
/// send a refused post back with a reason code, never the sentence, so nobody
/// can send a link that makes the real page show their own text as an error
/// (#1544). The page looks the copy up in <see cref="FormErrorReasons"/>.
/// </summary>
public sealed class PreAuthFormErrorTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_rejected_reset_password_comes_back_as_a_code()
    {
        using var factory = new EndpointFactory(_db);
        using var client = factory.CreateClient();
        var antiforgery = await AntiforgeryFromAsync(client, "/forgot-password");

        using var response = await client.PostAsync("/auth/reset-password", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Token", "some-link"),
            new KeyValuePair<string, string>("Password", "short"),
            new KeyValuePair<string, string>("__RequestVerificationToken", antiforgery),
        }));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.OriginalString;
        location.Should().Be("/reset-password?token=some-link&err=Password&reason=password-short");
        location.Should().NotContain("msg=");
    }

    [Fact]
    public async Task A_rejected_signup_comes_back_as_a_code()
    {
        using var factory = new EndpointFactory(_db);
        using var client = factory.CreateClient();
        var antiforgery = await AntiforgeryFromAsync(client, "/signup");

        using var response = await client.PostAsync("/auth/signup", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Email", "kirsten@cronus.example"),
            new KeyValuePair<string, string>("DisplayName", "Kirsten Jensen"),
            new KeyValuePair<string, string>("Password", "short"),
            new KeyValuePair<string, string>("OrganizationName", "CRONUS A/S"),
            new KeyValuePair<string, string>("__RequestVerificationToken", antiforgery),
        }));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString
            .Should().Be("/signup?err=invalid&field=Password&reason=password-short");
    }

    [Fact]
    public void Every_code_round_trips_to_its_sentence()
    {
        var password = new Dictionary<string, string>();
        AuthService.ValidatePassword("short", password);

        foreach (var message in new[]
                 {
                     password["Password"],
                     InviteService.MicrosoftOnlyMessage,
                     "Enter a valid email address.",
                     "Full name must be 2-80 characters.",
                 })
        {
            var code = FormErrorReasons.CodeFor(message);
            code.Should().NotBe(FormErrorReasons.Generic, $"\"{message}\" is one the services throw");
            FormErrorReasons.MessageFor(code).Should().Be(message);
        }
    }

    [Fact]
    public void Text_the_table_does_not_hold_never_reaches_the_page()
    {
        const string phishing = "Your account is locked. Call +45 0000 0000 to restore access.";

        FormErrorReasons.CodeFor(phishing).Should().Be(FormErrorReasons.Generic);
        FormErrorReasons.MessageFor(phishing).Should().Be(FormErrorReasons.GenericMessage);
        FormErrorReasons.MessageFor(null).Should().Be(FormErrorReasons.GenericMessage);
    }

    private static async Task<string> AntiforgeryFromAsync(HttpClient client, string path)
    {
        using var page = await client.GetAsync(path);
        var match = Regex.Match(
            await page.Content.ReadAsStringAsync(),
            """name="__RequestVerificationToken"[^>]*value="([^"]+)""");
        match.Success.Should().BeTrue("the form must carry an antiforgery token");
        return match.Groups[1].Value;
    }
}
