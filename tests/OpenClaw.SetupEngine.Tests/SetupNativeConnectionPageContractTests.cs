namespace OpenClaw.SetupEngine.Tests;

public sealed class SetupNativeConnectionPageContractTests
{
    [Fact]
    public void Editor_RevalidatesNextAndRejectsStaleOrPartialCommitSuccess()
    {
        var page = ReadPage();
        Assert.Contains("await args.Host.ConnectAsync(request, operation.Token)", page);
        Assert.Contains("await args.Host.CheckAsync(request, operation.Token)", page);
        Assert.Contains("if (_closed || generation != _generation)", page);
        Assert.Contains("_blocked = result.GatewayCommitted || result.RequiresAttention;", page);
        Assert.True(page.IndexOf("_incompleteCommitError = result.Error", StringComparison.Ordinal) <
            page.IndexOf("if (_closed || generation != _generation)", StringComparison.Ordinal));
        Assert.Contains("throw new InvalidOperationException(_incompleteCommitError);", page);
        Assert.Contains("if (connect && result.GatewayCommitted)", page);
        Assert.Contains("GatewayUrl = SetupNativeConnectionInputResolver.Resolve(request).GatewayUrl,", page);
        Assert.True(page.IndexOf("if (!result.Success)", StringComparison.Ordinal) <
            page.IndexOf("args.Connected(result)", StringComparison.Ordinal));
    }

    [Fact]
    public void Editor_CloseAndDraftChangeDiscardStagingAfterCancellationDrains()
    {
        var page = ReadPage();
        Assert.Contains("Page, IAsyncDisposable", page);
        Assert.Contains("OnNavigatedFrom(NavigationEventArgs e) => CloseInBackground()", page);
        Assert.Contains("_operation?.Cancel();", page);
        Assert.Contains("AsyncEventHandlerGuard.Run(host.DiscardCheckAsync", page);
        Assert.Contains("_closeTask ??= CloseAsync()", page);
        Assert.Contains("var host = _host;", page);
        Assert.True(page.IndexOf("await _pending;", StringComparison.Ordinal) <
            page.IndexOf("await host.DiscardCheckAsync();", StringComparison.Ordinal));
        Assert.Contains("CodeInput.Password = TokenInput.Password = \"\";", page);
    }

    private static string ReadPage()
    {
        var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); root is null && directory is not null;
             directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "OpenClaw.SetupEngine.UI")))
                root = directory.FullName;
        return File.ReadAllText(Path.Combine(root ?? throw new DirectoryNotFoundException("Repository root not found."),
            "src", "OpenClaw.SetupEngine.UI", "Pages", "SetupNativeConnectionPage.xaml.cs"));
    }
}
