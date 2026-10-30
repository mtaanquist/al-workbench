using System.Net;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Waiting out an install when Business Central says slow down: a poll answered with a
/// wait longer than the HTTP handler sits out is not a failed poll, so a run of them
/// cannot give up on an install Business Central is still running.
/// </summary>
public sealed class BcAppOperationPollerTests
{
    private sealed class ThrottledThenDone : UnreachableAppManagementClient
    {
        private int _throttlesLeft;
        public int Polls { get; private set; }
        public ThrottledThenDone(int throttles) => _throttlesLeft = throttles;

        public override Task<BcAppOperation?> GetAppOperationAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, Guid operationId, CancellationToken ct = default)
        {
            Polls++;
            if (_throttlesLeft-- > 0)
            {
                throw new BcApiException(HttpStatusCode.TooManyRequests, "slow down") { RetryAfter = TimeSpan.FromMilliseconds(100) };
            }
            return Task.FromResult<BcAppOperation?>(Operation(BcAppOperationStatus.Succeeded));
        }
    }

    private static BcAppOperation Operation(BcAppOperationStatus status) => new(
        Guid.NewGuid(), Guid.NewGuid(), "install", status, status.ToString(), "", "1.0.0.0", null,
        "", "", "", false, "", null, null, null);

    [Fact]
    public async Task Being_told_to_wait_is_not_counted_as_a_failed_poll()
    {
        var apps = new ThrottledThenDone(BcAppOperationPoller.MaxConsecutivePollErrors + 2);

        var result = await BcAppOperationPoller.PollUntilTerminalAsync(
            apps, "token", "BusinessCentral", "Production", Operation(BcAppOperationStatus.Running),
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        result.Completed.Should().BeTrue("the install finished; only the answers were held back");
        apps.Polls.Should().Be(BcAppOperationPoller.MaxConsecutivePollErrors + 3);
    }

    [Fact]
    public async Task A_wait_past_the_deadline_ends_as_unconfirmed_rather_than_failed()
    {
        var apps = new ThrottledThenDone(int.MaxValue);

        var result = await BcAppOperationPoller.PollUntilTerminalAsync(
            apps, "token", "BusinessCentral", "Production", Operation(BcAppOperationStatus.Running),
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

        result.IsUnconfirmed.Should().BeTrue();
    }
}
