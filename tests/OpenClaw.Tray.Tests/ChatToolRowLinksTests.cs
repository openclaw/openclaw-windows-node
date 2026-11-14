using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Chat;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatToolRowLinksTests
{
    private static ChatTimelineItem Tool(string name, object args, string? output = null,
        ChatToolCallStatus status = ChatToolCallStatus.Success, JsonObject? displayArgs = null)
    {
        var element = JsonSerializer.SerializeToElement(args);
        return new("t", ChatTimelineItemKind.ToolCall, "", ToolName: name, ToolResult: status, ToolOutput: output,
            ToolArgs: displayArgs ?? NativeToolProjector.ExtractSafeToolDisplayArgs(element),
            ToolInputDetails: NativeToolProjector.ProjectInputDetails(element, name));
    }

    [Theory]
    [InlineData("read", "skills/SKILL.md", "skills/SKILL.md")]
    [InlineData("edit", @"src\App.cs", "src/App.cs")]
    [InlineData("write", "./notes/drafts/../new.md", "notes/new.md")]
    public void FileRows_LinkTheirOwnWorkspaceRelativePath(string tool, string path, string expected) =>
        Assert.Equal(expected, ChatToolRowLinks.WorkspaceFilePath(Tool(tool, new { path })));

    [Theory]
    [InlineData(@"C:\repo\src\App.cs")]
    [InlineData("C:relative.txt")]
    [InlineData("/etc/passwd")]
    [InlineData(@"\Windows\win.ini")]
    [InlineData(@"\\server\share\secret.txt")]
    [InlineData("//server/share/secret.txt")]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    [InlineData(@"src\..\..\outside.txt")]
    [InlineData("~/.ssh/id_rsa")]
    [InlineData("file:///etc/passwd")]
    [InlineData("notes.txt:hidden")]
    public void FileRows_NeverLinkPathsThatLeaveTheWorkspace(string path)
    {
        Assert.Null(ChatToolRowLinks.WorkspaceRelativePath(path));
        Assert.Null(ChatToolRowLinks.WorkspaceFilePath(Tool("read", new { path })));
    }

    [Theory]
    [InlineData("keys/abcdef…wxyz.pem")]
    [InlineData("keys/***.txt")]
    [InlineData("keys/-----BEGIN KEY-----\n…redacted…\n-----END KEY-----")]
    public void FileRows_RejectSecretMaskShapes(string path) =>
        Assert.Null(ChatToolRowLinks.WorkspaceRelativePath(path));

    [Fact]
    public void FileRows_UseOtherPathKeysAndIgnoreNonFileTools()
    {
        Assert.Equal("a.ipynb", ChatToolRowLinks.WorkspaceFilePath(Tool("notebook_edit", new { notebook_path = "a.ipynb" })));
        Assert.Equal("b.txt", ChatToolRowLinks.WorkspaceFilePath(Tool("read_file", new { file_path = "b.txt" })));
        Assert.Null(ChatToolRowLinks.WorkspaceFilePath(Tool("exec", new { command = "cat a.txt" })));
        Assert.Null(ChatToolRowLinks.WorkspaceFilePath(Tool("grep", new { pattern = "x", path = "src" })));
        Assert.Null(ChatToolRowLinks.WorkspaceFilePath(Tool("read", new { })));
    }

    [Fact]
    public void FileRows_RejectPathsThatDisplaySanitizationChanged()
    {
        Assert.Null(ChatToolRowLinks.WorkspaceFilePath(Tool("read", new { path = "a\u200Bb.txt" })));
        var cut = Tool("read", new { path = new string('d', 300) + ".txt" });
        Assert.Null(ChatToolRowLinks.WorkspaceFilePath(cut with { ToolInputDetails = null }));
    }

    [Fact]
    public void FileRows_CacheThePathPerInput()
    {
        var entry = Tool("read", new { path = "docs/../docs/a.md" });
        var first = ChatToolRowLinks.WorkspaceFilePath(entry);
        Assert.Equal("docs/a.md", first);
        Assert.Same(first, ChatToolRowLinks.WorkspaceFilePath(entry with { Id = "rerender", ToolResult = ChatToolCallStatus.Error }));
    }

    [Theory]
    [InlineData("*** Begin Patch\n*** Update File: a.cs\n-x\n*** Update File: b.cs\n+y\n*** End Patch")]
    [InlineData("*** Begin Patch\n*** Update File: a.cs\n*** Move to: b.cs\n@@\n-x\n+y\n*** End Patch")]
    [InlineData("*** Begin Patch\n*** Delete File: a.cs\n*** End Patch")]
    [InlineData("not a patch")]
    public void PatchRows_PlainPathArgumentCannotBypassOperationChecks(string patch) =>
        Assert.Null(ChatToolRowLinks.WorkspaceFilePath(Tool("apply_patch", new { path = "a.cs", input = patch })));

    [Fact]
    public void PatchRows_RawTextInputAndPrefixedToolNamesLink()
    {
        const string patch = "*** Begin Patch\n*** Update File: src/a.cs\n@@\n-x\n+y\n*** End Patch";
        var element = JsonSerializer.SerializeToElement(patch);
        var raw = new ChatTimelineItem("t", ChatTimelineItemKind.ToolCall, "", ToolName: "apply_patch",
            ToolResult: ChatToolCallStatus.Success, ToolInputDetails: NativeToolProjector.ProjectInputDetails(element, "apply_patch"));
        Assert.Equal("src/a.cs", ChatToolRowLinks.WorkspaceFilePath(raw));
        Assert.Equal("src/a.cs", ChatToolRowLinks.WorkspaceFilePath(Tool("codex_apply_patch", new { input = patch })));
    }

    [Fact]
    public void SpawnRows_LinkTheListedSessionIdEvenWhenTheResultSpellsItDifferently()
    {
        var launch = Tool("sessions_spawn", new { label = "Researcher" }, """{"childSessionKey":"Agent:Main:Subagent:ABC"}""");
        ChatThread[] children = [new() { Id = "agent:main:subagent:abc", Title = "", IsSubagent = true, ParentSessionKey = "main" }];
        var key = ChatToolRowPresentation.Resolve(launch, children).Subagent?.ChildSessionKey;
        Assert.Equal("agent:main:subagent:abc", key);
        Assert.True(ChatToolRowLinks.IsOpenableSubagent(children, "main", key!));
    }

    [Fact]
    public void RowLinks_KeepTheRunningShimmer()
    {
        var renderer = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "ToolCallCardRenderer.cs"));
        var start = renderer.IndexOf("private static Element BuildRowLink(", StringComparison.Ordinal);
        var link = renderer[start..renderer.IndexOf(".WithKey(\"row-link\")", start, StringComparison.Ordinal)];
        Assert.Contains(".WithTextShimmer(running)", link);
    }

    [Fact]
    public void SubagentLinks_OpenOnlyDirectUnarchivedSubagentsOfThisConversation()
    {
        ChatThread[] threads =
        [
            new() { Id = "child", Title = "", IsSubagent = true, ParentSessionKey = "main" },
            new() { Id = "archived", Title = "", IsSubagent = true, ParentSessionKey = "main", IsArchived = true },
            new() { Id = "other-parent", Title = "", IsSubagent = true, ParentSessionKey = "elsewhere" },
            new() { Id = "ordinary", Title = "", ParentSessionKey = "main" },
        ];
        Assert.True(ChatToolRowLinks.IsOpenableSubagent(threads, "main", "child"));
        Assert.False(ChatToolRowLinks.IsOpenableSubagent(threads, "main", "archived"));
        Assert.False(ChatToolRowLinks.IsOpenableSubagent(threads, "main", "other-parent"));
        Assert.False(ChatToolRowLinks.IsOpenableSubagent(threads, "main", "ordinary"));
        Assert.False(ChatToolRowLinks.IsOpenableSubagent(threads, "main", "unknown"));
    }

    [Theory]
    [InlineData("*** Begin Patch\n*** Update File: src/a.cs\n@@\n-x\n+y\n*** End Patch", "src/a.cs")]
    [InlineData("*** Begin Patch\n*** Add File: src/new.cs\n+x\n*** End Patch", "src/new.cs")]
    [InlineData("diff --git a/src/a.cs b/src/a.cs\n--- a/src/a.cs\n+++ b/src/a.cs\n@@ -1 +1 @@\n-x\n+y", "src/a.cs")]
    [InlineData("*** Begin Patch\n*** Delete File: src/a.cs\n*** End Patch", null)]
    [InlineData("*** Begin Patch\n*** Update File: a.cs\n*** Move to: b.cs\n@@\n-x\n+y\n*** End Patch", null)]
    [InlineData("*** Begin Patch\n*** Update File: a.cs\n-x\n*** Update File: b.cs\n+y\n*** End Patch", null)]
    [InlineData("--- a/a.md\n+++ b/a.md\n@@ -1,2 +1,2 @@\n--- old rule\n+++ new rule\n keep", "a.md")]
    [InlineData("*** Begin Patch\n*** Update File: a.md\n@@\n  *** Update File: b.md\n-x\n+y\n*** End Patch", "a.md")]
    public void PatchRows_LinkOnlyASingleSurvivingFile(string patch, string? expected) =>
        Assert.Equal(expected, ChatToolRowLinks.WorkspaceFilePath(Tool("apply_patch", new { input = patch })));

    [Fact]
    public void SpawnRows_CarryTheChildSessionTheirOwnResultNamesForTheLink()
    {
        const string output = """{"status":"accepted","childSessionKey":"agent:main:subagent:abc","runId":"r"}""";
        var launch = Tool("sessions_spawn", new { label = "Researcher", task = "find" }, output);
        ChatThread[] children = [new() { Id = "agent:main:subagent:abc", Title = "", IsSubagent = true, ParentSessionKey = "main" }];
        Assert.Equal("agent:main:subagent:abc", ChatToolRowPresentation.Resolve(launch, children).Subagent?.ChildSessionKey);
        // Before the snapshot lists the child, the key is still known; CanOpenSession decides whether it links.
        Assert.Equal("agent:main:subagent:abc", ChatToolRowPresentation.Resolve(launch, []).Subagent?.ChildSessionKey);
        Assert.Null(ChatToolRowPresentation.Resolve(
            Tool("sessions_spawn", new { label = "Researcher" }, "Spawned agent:main:subagent:abc"), children).Subagent?.ChildSessionKey);
    }
}
