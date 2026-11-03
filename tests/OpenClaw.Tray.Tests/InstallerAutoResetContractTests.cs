using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Installer-level Local AI recovery contract: when the setup pipeline fails with
/// <c>PipelineFailureCode.LocalAiReceiptUnusable</c>, the installer removes the unusable
/// installation automatically and reruns the pipeline, instead of asking the user to click
/// Reset Local AI. Source-text contract tests because <c>ProgressPage</c> and
/// <c>SetupWindow</c> are WinUI types that require a XAML host to instantiate.
/// </summary>
public sealed class InstallerAutoResetContractTests
{
    [Fact]
    public void ProgressPage_AutoResetsOnlyOnUnusableReceiptAndOnlyOnce()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "Pages", "ProgressPage.xaml.cs"));

        // The automatic reset is gated on the typed failure code and bounded to one attempt,
        // so unrelated failures never reset and a failing reset cannot loop the pipeline.
        Assert.Contains(
            "result.FailureCode == PipelineFailureCode.LocalAiReceiptUnusable &&",
            source);
        Assert.Contains(
            "!autoResetAttempted && setupOwner is { } resetOwner",
            source);
        AssertInOrder(
            source,
            "autoResetAttempted = true;",
            "resetOwner.ResetLocalAiAsync(cts.Token)");
        Assert.Contains(
            "if (prompt.Removed)",
            source);
    }

    [Fact]
    public void ProgressPage_RerunsPipelineAfterSuccessfulAutoReset()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "Pages", "ProgressPage.xaml.cs"));
        string method = ExtractMethod(source, "private async Task StartPipelineAsync()");

        // A successful automatic reset continues into a fresh pipeline run instead of the
        // failure page. The progress presentation is rebuilt before the retry.
        AssertInOrder(
            method,
            "autoResetAttempted = true;",
            "prompt.Removed",
            "_pipelineFinished = false;",
            "continue;");
        Assert.InRange(
            method.IndexOf("_installationProgress = new(", StringComparison.Ordinal),
            method.IndexOf("prompt.Removed", StringComparison.Ordinal),
            method.IndexOf("continue;", StringComparison.Ordinal));
    }

    [Fact]
    public void ProgressPage_LostRunLockNeverNavigatesWithUnusableReceiptCode()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "Pages", "ProgressPage.xaml.cs"));
        string method = ExtractMethod(source, "private async Task StartPipelineAsync()");

        // When the automatic reset succeeds but the window cannot reclaim the setup run lock,
        // ReclaimSetupLock has already navigated to the lock-contention page. The pipeline
        // must not navigate again with the LocalAiReceiptUnusable failure code, which would
        // replace that message and offer another destructive reset for a reset that already ran.
        AssertInOrder(
            method,
            "if (prompt.LockLost)",
            "return; // ReclaimSetupLock already navigated to the lock-contention page.");
    }

    [Fact]
    public void SetupWindow_AutomaticResetYieldsRunLockBeforeRunningAndReclaimsAfter()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "SetupWindow.xaml.cs"));
        string method = ExtractMethod(source, "internal async Task<LocalAiResetPrompt> ResetLocalAiAsync(CancellationToken ct)");

        // The reset engine is its own setup run and refuses while this window holds the run
        // lock, so the lock must be released before the tray reset starts and reclaimed even
        // when the reset fails. A failed reclaim must surface as an error so the automatic
        // retry cannot run the pipeline without the lock.
        AssertInOrder(
            method,
            "bool yieldedLock = _setupLock is not null;",
            "_setupLock?.Dispose();",
            "_setupLock = null;",
            "_localAiHost.ResetLocalAiAsync(lifetime.Token)");
        AssertInOrder(
            method,
            "finally",
            "if (yieldedLock && !ReclaimSetupLock(out var lockError))",
            "error ??= lockError;");
    }

    [Fact]
    public void SetupWindow_AutomaticResetIsCancelledWhenWindowCloses()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "SetupWindow.xaml.cs"));
        string method = ExtractMethod(source, "internal async Task<LocalAiResetPrompt> ResetLocalAiAsync(CancellationToken ct)");

        // Closing the setup window must cancel the destructive engine child; the linked
        // lifetime token reaches the tray reset, which kills and drains the child before
        // reporting. Without this, reset work would continue after the window dies.
        Assert.Contains(
            "CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token)",
            method);
    }

    private static string ExtractMethod(string source, string methodSignature)
    {
        int start = source.IndexOf(methodSignature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected to find method starting with '{methodSignature}'.");
        int braceDepth = 0;
        int bodyStart = source.IndexOf('{', start);
        Assert.True(bodyStart >= 0, "Expected an opening brace for the method body.");
        for (int index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
                braceDepth++;
            else if (source[index] == '}')
            {
                braceDepth--;
                if (braceDepth == 0)
                    return source[start..(index + 1)];
            }
        }
        throw new InvalidOperationException("Unbalanced braces while extracting method body.");
    }

    private static void AssertInOrder(string source, params string[] fragments)
    {
        int previousIndex = -1;
        foreach (string fragment in fragments)
        {
            int index = source.IndexOf(fragment, previousIndex + 1, StringComparison.Ordinal);
            Assert.True(index > previousIndex, $"Expected '{fragment}' after the previous fragment.");
            previousIndex = index;
        }
    }
}