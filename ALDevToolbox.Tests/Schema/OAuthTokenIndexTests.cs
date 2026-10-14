using ALDevToolbox.Data;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Schema;

/// <summary>
/// #1259: disabling a user revokes every OAuth token for that subject.
/// OpenIddict's own index on <c>oauth_tokens</c> leads with
/// <c>application_id</c>, so the lookup needs an index of its own.
/// </summary>
public sealed class OAuthTokenIndexTests
{
    [Fact]
    public void Oauth_tokens_have_an_index_led_by_subject()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only")
            .Options;
        using var ctx = new AppDbContext(options);

        var tokens = ctx.Model.GetEntityTypes().Single(e => e.GetTableName() == "oauth_tokens");

        tokens.GetIndexes()
            .Should().Contain(i => i.Properties[0].GetColumnName() == "subject"
                && i.GetDatabaseName() == "ix_oauth_tokens_subject");
    }
}
