using ALDevToolbox.Components.Email;
using ALDevToolbox.Services.Email;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Tests.Email;

/// <summary>
/// <see cref="EmailRenderer"/> as the app registers it, rendering in a request-like
/// scope the way the endpoints and, later, the notification senders use it.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class EmailRendererHostTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public EmailRendererHostTests()
    {
        _factory = new EndpointFactory(_db);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task The_registered_renderer_renders_an_email_from_a_scope()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var renderer = scope.ServiceProvider.GetRequiredService<EmailRenderer>();

        var content = await SiteAdminTestEmail.RenderAsync(renderer, "Mads");

        content.HtmlBody.Should().Contain("Hi Mads,");
        content.TextBody.Should().StartWith("AL Workbench");
    }
}
