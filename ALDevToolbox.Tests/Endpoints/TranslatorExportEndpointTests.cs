using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// "Export a copy" through the real host. A translation started from the
/// compiler's generated <c>.g.xlf</c> keeps the generated text as its base, and
/// that text says its target language is its source one; the export used to
/// hand it back that way, so a <c>da-DK.xlf</c> full of Danish declared
/// <c>target-language="en-US"</c> and AL ignored it (#1250).
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class TranslatorExportEndpointTests : IDisposable
{
    private const string AlexEmail = "alex@cronus.example";
    private const string Password = "correct horse battery staple";

    private const string Generated = """
        <?xml version="1.0" encoding="utf-8"?>
        <xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2">
          <file datatype="xml" source-language="en-US" target-language="en-US" original="PaymentImport">
            <body>
              <group id="body">
                <trans-unit id="Table 1 - Field 2 - Property Caption" size-unit="char">
                  <source>Amount</source>
                </trans-unit>
              </group>
            </body>
          </file>
        </xliff>
        """;

    private const string Edits = """{"Table 1 - Field 2 - Property Caption":{"t":"Beloeb","s":"translated"}}""";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_copy_of_a_translation_started_from_the_generated_file_names_its_language()
    {
        await SeedUserAsync();
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory);

        var exported = await ExportAsync(client, Generated, "da-DK");

        exported.Should().Contain("target-language=\"da-DK\"");
        exported.Should().Contain("source-language=\"en-US\"");
        exported.Should().Contain("Beloeb");
    }

    [Fact]
    public async Task A_language_file_keeps_the_language_it_already_names()
    {
        await SeedUserAsync();
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory);
        var danish = Generated.Replace("target-language=\"en-US\"", "target-language=\"da-DK\"", StringComparison.Ordinal);

        var exported = await ExportAsync(client, danish, "sv-SE");

        exported.Should().Contain("target-language=\"da-DK\"",
            "a file that already says which language it is in is left byte for byte as it was");
    }

    /// <summary>
    /// #1268 (L48): the memory learns what the user changed, not the whole
    /// file again - a unit already translated in the file gains no hit from a
    /// save that did not touch it.
    /// </summary>
    [Fact]
    public async Task Export_teaches_the_memory_only_the_units_that_were_edited()
    {
        await SeedUserAsync();
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory);
        var danish = Generated
            .Replace("target-language=\"en-US\"", "target-language=\"da-DK\"", StringComparison.Ordinal)
            .Replace("</group>", """
                    <trans-unit id="Table 1 - Field 3 - Property Caption" size-unit="char">
                      <source>Posting Date</source>
                      <target state="translated">Bogfoeringsdato</target>
                    </trans-unit>
                  </group>
                """, StringComparison.Ordinal);

        await ExportAsync(client, danish, "da-DK");

        // Learning runs after the download has been sent, so give it a moment.
        List<string> learned = [];
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await using var ctx = _db.NewContext();
            learned = ctx.TranslationMemory.Select(e => e.SourceText).ToList();
            if (learned.Count > 0) break;
            await Task.Delay(100);
        }
        learned.Should().Equal("Amount");
    }

    // ---- helpers -----------------------------------------------------------

    private static async Task<string> ExportAsync(HttpClient client, string originalXml, string targetLanguage)
    {
        var token = await TokenFromAsync(client, "/notifications");
        using var response = await client.PostAsync("/api/translator/export", new FormUrlEncodedContent(
        [
            new("FileName", "PaymentImport.da-DK.xlf"),
            new("OriginalXml", originalXml),
            new("Edits", Edits),
            new("TargetLanguage", targetLanguage),
            new("__RequestVerificationToken", token),
        ]));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task SeedUserAsync()
    {
        await using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = AlexEmail,
            DisplayName = "Alex Hansen",
            PasswordHash = new AuthService(ctx, NullLogger<AuthService>.Instance, TimeProvider.System).HashPassword(Password),
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task<HttpClient> SignInAsync(EndpointFactory factory)
    {
        var client = factory.CreateClient();
        var token = await TokenFromAsync(client, "/login");
        using var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(
        [
            new("Email", AlexEmail), new("Password", Password), new("__RequestVerificationToken", token),
        ]));
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().NotContain("err=");
        return client;
    }

    private static async Task<string> TokenFromAsync(HttpClient client, string url)
    {
        using var page = await client.GetAsync(url);
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var match = Regex.Match(await page.Content.ReadAsStringAsync(),
            """name="__RequestVerificationToken"[^>]*value="([^"]+)""");
        match.Success.Should().BeTrue("the page must carry an antiforgery token");
        return match.Groups[1].Value;
    }
}
