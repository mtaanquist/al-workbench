using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The count and the sentence behind the "are you sure" before a deployment that installs
/// right away. The service test covers the call to Business Central; these cover what is
/// counted and how it reads.
/// </summary>
public sealed class OpenSessionsCheckTests
{
    private static BcSession Session(string user, string clientType = "WebClient") =>
        new(1, user, clientType, null, "", "", "", "", "", null, "", null);

    [Fact]
    public void Counts_each_person_once_and_splits_off_delegated_users()
    {
        var check = OpenSessionsCheck.From("Production",
        [
            Session("ola@cronus.com"),
            Session("ola@cronus.com", "Tablet"),
            Session("anna@cronus.com", "Phone"),
            Session("USER_E5EE0099AFAB445E8B604FE18E05FC1A"),
            Session("user_0B2C1E9F5A6D4C3B8E7F6A5D4C3B2A10"),
        ]);

        check.EndUsers.Should().Be(2);
        check.DelegatedUsers.Should().Be(2);
        check.NeedsConfirmation.Should().BeTrue();
    }

    [Theory]
    [InlineData("Background")]
    [InlineData("ChildSession")]
    [InlineData("NAS")]
    [InlineData("ODataV4")]
    [InlineData("WebServiceClient")]
    [InlineData("Api")]
    public void Leaves_out_integrations_and_sessions_that_run_without_anybody_online(string clientType)
    {
        var check = OpenSessionsCheck.From("Production", [Session("ola@cronus.com", clientType), Session("")]);

        check.Should().Be(new OpenSessionsCheck("Production", 0, 0));
        check.NeedsConfirmation.Should().BeFalse("nobody is online, so there is nothing to ask");
    }

    [Fact]
    public void Asks_with_both_counts()
    {
        new OpenSessionsCheck("Production", 3, 2).Question.Should().Be(
            "There are 3 end-users online and 2 delegated users online in Production. Are you sure you want to deploy the build?");
    }

    [Fact]
    public void Asks_in_the_singular_for_one()
    {
        new OpenSessionsCheck("Test", 1, 1).Question.Should().Be(
            "There is 1 end-user online and 1 delegated user online in Test. Are you sure you want to deploy the build?");
        new OpenSessionsCheck("Test", 0, 1).Question.Should().Be(
            "There are 0 end-users online and 1 delegated user online in Test. Are you sure you want to deploy the build?");
    }

    [Fact]
    public void Says_why_it_could_not_tell_and_still_asks()
    {
        var check = OpenSessionsCheck.Unknown("Production", "The Business Central client secret has expired");

        check.NeedsConfirmation.Should().BeTrue();
        check.Question.Should().Be(
            "Couldn't check who is signed in to Production. The Business Central client secret has expired. Are you sure you want to deploy the build?");
        OpenSessionsCheck.Unknown("Production", "Business Central didn't answer.").Question.Should().Be(
            "Couldn't check who is signed in to Production. Business Central didn't answer. Are you sure you want to deploy the build?");
    }
}
