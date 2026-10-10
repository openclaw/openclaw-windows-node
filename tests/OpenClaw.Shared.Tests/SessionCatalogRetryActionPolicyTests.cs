using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class SessionCatalogRetryActionPolicyTests
{
    [Fact]
    public void Enabled_only_when_connected_retry_required_and_idle()
        => Assert.True(SessionCatalogRetryActionPolicy.IsRetryEnabled(true, true, false, false));

    [Fact]
    public void Disabled_while_acquisition_in_progress()
    {
        // A loading acquisition keeps Retry disabled until the real terminal transition,
        // not just until the socket send completes.
        Assert.False(SessionCatalogRetryActionPolicy.IsRetryEnabled(true, true, acquisitionInProgress: true, sendInFlight: false));
        Assert.False(SessionCatalogRetryActionPolicy.CanStartRetry(true, true, true, false));
    }

    [Fact]
    public void Disabled_while_send_in_flight_or_disconnected_or_no_retry_needed()
    {
        Assert.False(SessionCatalogRetryActionPolicy.IsRetryEnabled(true, true, false, sendInFlight: true));
        Assert.False(SessionCatalogRetryActionPolicy.IsRetryEnabled(connected: false, retryRequired: true, acquisitionInProgress: false, sendInFlight: false));
        Assert.False(SessionCatalogRetryActionPolicy.IsRetryEnabled(true, retryRequired: false, acquisitionInProgress: false, sendInFlight: false));
    }

    [Fact]
    public void Prompt_visible_only_when_connected_and_retry_required()
    {
        Assert.True(SessionCatalogRetryActionPolicy.IsRetryPromptVisible(true, true));
        Assert.False(SessionCatalogRetryActionPolicy.IsRetryPromptVisible(false, true));
        Assert.False(SessionCatalogRetryActionPolicy.IsRetryPromptVisible(true, false));
    }
}
