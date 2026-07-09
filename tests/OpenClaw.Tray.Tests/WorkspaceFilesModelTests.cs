using System;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Pages;
using Xunit;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Unit coverage for <see cref="WorkspaceFilesModel"/> — the pure projection
/// behind the Workspace page session file rail. Exercises the mapping from the
/// typed gateway DTOs (<see cref="SessionFileList"/> / <see cref="SessionFileEntry"/>
/// / <see cref="SessionFileBrowser"/>) into view rows, plus search/path,
/// metadata (size/modified/missing), sort tiering, and unsupported/empty paths.
/// </summary>
public class WorkspaceFilesModelTests
{
    private static SessionFileEntry File(
        string path, string? name = null, string? kind = null,
        bool missing = false, long? size = null, DateTime? updatedAt = null) => new()
    {
        Path = path,
        Name = name ?? "",
        Kind = kind,
        Missing = missing,
        Size = size,
        UpdatedAt = updatedAt,
    };

    private static SessionFileList List(params SessionFileEntry[] files) => new()
    {
        Key = "agent:main:main",
        Root = "/home/agent/project",
        Files = files,
        IsSupported = true,
    };

    // ── File list mapping ───────────────────────────────────────────────

    [Fact]
    public void FromSessionFileList_MapsRootAndBasicFields()
    {
        var state = WorkspaceFilesModel.FromSessionFileList(List(
            File("AGENTS.md", size: 2048),
            File("plan.md", size: 512)));

        Assert.True(state.Supported);
        Assert.Equal("/home/agent/project", state.WorkspacePath);
        Assert.Equal(2, state.Entries.Count);
        var agents = Assert.Single(state.Entries, e => e.Name == "AGENTS.md");
        Assert.Equal("AGENTS.md", agents.RelativePath);
        Assert.Equal("AGENTS.md", agents.RequestPath);
        Assert.Equal(2048, agents.Size);
        Assert.True(agents.Exists);
    }

    [Fact]
    public void FromSessionFileList_DerivesNameFromPathAndExposesDirectory()
    {
        var state = WorkspaceFilesModel.FromSessionFileList(List(
            File("src/app/main.ts", size: 100)));

        var entry = Assert.Single(state.Entries);
        Assert.Equal("main.ts", entry.Name);
        Assert.Equal("src/app/main.ts", entry.RelativePath);
        Assert.Equal("src/app", WorkspaceFilesModel.DirectoryOf(entry.RelativePath));
    }

    [Fact]
    public void FromSessionFileList_NormalizesBackslashForDisplayButKeepsRequestPath()
    {
        var state = WorkspaceFilesModel.FromSessionFileList(List(
            File(@"src\app\main.ts", name: "main.ts")));

        var entry = Assert.Single(state.Entries);
        Assert.Equal("src/app/main.ts", entry.RelativePath);
        // Request path stays verbatim so sessions.files.get matches the gateway.
        Assert.Equal(@"src\app\main.ts", entry.RequestPath);
    }

    [Fact]
    public void FromSessionFileList_KeepsMissingFiles()
    {
        var state = WorkspaceFilesModel.FromSessionFileList(List(
            File("gone.md", missing: true),
            File("here.md")));

        Assert.Equal(2, state.Entries.Count);
        Assert.False(Assert.Single(state.Entries, e => e.Name == "gone.md").Exists);
        Assert.True(Assert.Single(state.Entries, e => e.Name == "here.md").Exists);
    }

    [Fact]
    public void FromSessionFileList_MapsKindToTouchedAndRead()
    {
        var state = WorkspaceFilesModel.FromSessionFileList(List(
            File("wrote.md", kind: "modified"),
            File("saw.md", kind: "read")));

        Assert.True(Assert.Single(state.Entries, e => e.Name == "wrote.md").Touched);
        Assert.False(Assert.Single(state.Entries, e => e.Name == "wrote.md").Read);
        Assert.True(Assert.Single(state.Entries, e => e.Name == "saw.md").Read);
    }

    [Fact]
    public void FromSessionFileList_MapsUpdatedAtToUtc()
    {
        var state = WorkspaceFilesModel.FromSessionFileList(List(
            File("a.md", updatedAt: new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc))));

        var entry = Assert.Single(state.Entries);
        Assert.Equal(
            new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero),
            entry.ModifiedUtc!.Value);
    }

    [Fact]
    public void FromSessionFileList_NegativeSizeBecomesNull()
    {
        var state = WorkspaceFilesModel.FromSessionFileList(List(
            File("a.md", size: -1),
            File("b.md")));

        Assert.Null(Assert.Single(state.Entries, e => e.Name == "a.md").Size);
        Assert.Null(Assert.Single(state.Entries, e => e.Name == "b.md").Size);
    }

    [Fact]
    public void FromSessionFileList_SkipsEntriesWithoutPathOrName()
    {
        var state = WorkspaceFilesModel.FromSessionFileList(List(
            File(""),
            File("valid.md")));

        var entry = Assert.Single(state.Entries);
        Assert.Equal("valid.md", entry.Name);
    }

    [Fact]
    public void FromSessionFileList_MergesBrowserDirectoriesNotInFiles()
    {
        var list = new SessionFileList
        {
            Key = "agent:main:main",
            Root = "/root",
            Files = new[] { File("README.md", kind: "read") },
            Browser = new SessionFileBrowser
            {
                Entries = new[]
                {
                    new SessionFileBrowserEntry { Path = "src", Name = "src", Kind = "directory", SessionKind = "modified" },
                    // Duplicate of a file entry — the richer file entry wins.
                    new SessionFileBrowserEntry { Path = "README.md", Name = "README.md", Kind = "file" },
                },
            },
            IsSupported = true,
        };

        var state = WorkspaceFilesModel.FromSessionFileList(list);

        Assert.Equal(2, state.Entries.Count);
        var dir = Assert.Single(state.Entries, e => e.Name == "src");
        Assert.True(dir.IsDirectory);
        Assert.True(dir.Touched); // SessionKind "modified"
        Assert.False(dir.CanPreview);
    }

    [Fact]
    public void FromSessionFileList_MapsBrowserPathSearchAndTruncation()
    {
        var list = new SessionFileList
        {
            Key = "agent:main:main",
            Root = "/root",
            Browser = new SessionFileBrowser
            {
                Path = @"src\app\",
                ParentPath = "src",
                Search = "view",
                Truncated = true,
                Entries = Array.Empty<SessionFileBrowserEntry>(),
            },
            IsSupported = true,
        };

        var state = WorkspaceFilesModel.FromSessionFileList(list);

        Assert.Equal("src/app", state.BrowserPath);
        Assert.Equal("src", state.BrowserParentPath);
        Assert.Equal("view", state.BrowserSearch);
        Assert.True(state.BrowserTruncated);
    }

    [Fact]
    public void FromSessionFileList_BrowserOnlyFileIsNotPreviewable()
    {
        var list = new SessionFileList
        {
            Key = "agent:main:main",
            Browser = new SessionFileBrowser
            {
                Entries = new[]
                {
                    new SessionFileBrowserEntry { Path = "src/browser-only.ts", Name = "browser-only.ts", Kind = "file" },
                    new SessionFileBrowserEntry { Path = "src/touched.ts", Name = "touched.ts", Kind = "file", SessionKind = "read" },
                },
            },
            IsSupported = true,
        };

        var state = WorkspaceFilesModel.FromSessionFileList(list);

        var browserOnly = Assert.Single(state.Entries, e => e.Name == "browser-only.ts");
        Assert.False(browserOnly.IsSessionFile);
        Assert.False(browserOnly.CanPreview);

        var touched = Assert.Single(state.Entries, e => e.Name == "touched.ts");
        Assert.True(touched.IsSessionFile);
        Assert.True(touched.CanPreview);
        Assert.True(touched.Read);
    }

    [Fact]
    public void FromSessionFileList_PathIdentityIsCaseSensitive()
    {
        // Distinct paths differing only by case must not collide into one row.
        var state = WorkspaceFilesModel.FromSessionFileList(List(
            File("Readme.md"),
            File("README.md")));

        Assert.Equal(2, state.Entries.Count);
    }

    [Fact]
    public void FromLegacyAgentFilesList_MapsExistingFilesAsPreviewableFallbackRows()
    {
        using var doc = JsonDocument.Parse("""
            {
              "workspace": "C:\\repo",
              "files": [
                { "path": "C:\\repo\\README.md", "name": "README.md", "size": 2048, "exists": true },
                { "name": "gone.md", "missing": true }
              ]
            }
            """);

        var state = WorkspaceFilesModel.FromLegacyAgentFilesList(doc.RootElement);

        Assert.True(state.Supported);
        Assert.Equal(@"C:\repo", state.WorkspacePath);
        Assert.Equal(2, state.Entries.Count);

        var readme = Assert.Single(state.Entries, e => e.Name == "README.md");
        Assert.True(readme.IsSessionFile);
        Assert.True(readme.CanPreview);
        Assert.True(readme.Exists);
        Assert.Equal(2048, readme.Size);
        Assert.Equal("README.md", readme.RequestPath);

        var missing = Assert.Single(state.Entries, e => e.Name == "gone.md");
        Assert.False(missing.Exists);
        Assert.True(missing.CanPreview);
    }

    [Fact]
    public void FromAgentWorkspaceList_MapsAllWorkspaceFilesAsPreviewable()
    {
        using var doc = JsonDocument.Parse("""
            {
              "agentId": "main",
              "path": "src",
              "parentPath": "",
              "entries": [
                { "path": "src", "name": "src", "kind": "directory", "updatedAtMs": 1700000000000 },
                { "path": "src/app.cs", "name": "app.cs", "kind": "file", "size": 42, "updatedAtMs": 1700000000123 },
                { "path": "README.md", "name": "README.md", "kind": "file" }
              ],
              "totalEntries": 3,
              "offset": 0
            }
            """);

        var state = WorkspaceFilesModel.FromAgentWorkspaceList(doc.RootElement);

        Assert.True(state.Supported);
        Assert.Equal("Agent workspace", state.WorkspacePath);
        Assert.Equal("src", state.BrowserPath);
        Assert.Equal(string.Empty, state.BrowserParentPath);
        Assert.False(state.BrowserTruncated);

        var directory = Assert.Single(state.Entries, e => e.Name == "src");
        Assert.True(directory.IsDirectory);
        Assert.False(directory.CanPreview);
        Assert.False(directory.IsSessionFile);

        var app = Assert.Single(state.Entries, e => e.Name == "app.cs");
        Assert.False(app.IsDirectory);
        Assert.True(app.CanPreview);
        Assert.False(app.IsSessionFile);
        Assert.False(app.Read);
        Assert.False(app.Touched);
        Assert.Equal(42, app.Size);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000123), app.ModifiedUtc);

        var readme = Assert.Single(state.Entries, e => e.Name == "README.md");
        Assert.True(readme.CanPreview);
    }

    [Fact]
    public void FromAgentWorkspaceList_DetectsPaginationTruncation()
    {
        using var doc = JsonDocument.Parse("""
            {
              "path": "src/app",
              "parentPath": "src",
              "entries": [
                { "path": "src/app/a.txt", "name": "a.txt", "kind": "file" }
              ],
              "totalEntries": 5,
              "offset": 0
            }
            """);

        var state = WorkspaceFilesModel.FromAgentWorkspaceList(doc.RootElement);

        Assert.True(state.BrowserTruncated);
        Assert.Equal("src", state.BrowserParentPath);
    }

    // ── Unsupported / null handling ─────────────────────────────────────

    [Fact]
    public void FromSessionFileList_UnsupportedResult_FlagsUnsupported()
    {
        var state = WorkspaceFilesModel.FromSessionFileList(
            new SessionFileList { Key = "k", IsSupported = false });

        Assert.False(state.Supported);
        Assert.Empty(state.Entries);
    }

    [Fact]
    public void FromSessionFileList_Null_ReturnsSupportedEmpty()
    {
        var state = WorkspaceFilesModel.FromSessionFileList(null!);

        Assert.True(state.Supported);
        Assert.Empty(state.Entries);
        Assert.Equal(string.Empty, state.WorkspacePath);
    }

    [Fact]
    public void FromSessionFileList_NullFilesCollection_DoesNotThrow()
    {
        var state = WorkspaceFilesModel.FromSessionFileList(
            new SessionFileList { Key = "k", Root = "/r", Files = null!, IsSupported = true });

        Assert.True(state.Supported);
        Assert.Empty(state.Entries);
        Assert.Equal("/r", state.WorkspacePath);
    }

    // ── Search / filter ─────────────────────────────────────────────────

    private static System.Collections.Generic.IReadOnlyList<WorkspaceFilesModel.WorkspaceFileEntry> SampleEntries() =>
        WorkspaceFilesModel.FromSessionFileList(List(
            File("src/app/main.ts"),
            File("src/app/utils.ts"),
            File("README.md"))).Entries;

    [Fact]
    public void Filter_EmptyQuery_ReturnsAll()
    {
        var entries = SampleEntries();
        Assert.Equal(3, WorkspaceFilesModel.Filter(entries, "  ").Count);
        Assert.Equal(3, WorkspaceFilesModel.Filter(entries, null).Count);
    }

    [Fact]
    public void Filter_MatchesNameAndPath_CaseInsensitive()
    {
        var entries = SampleEntries();

        Assert.Single(WorkspaceFilesModel.Filter(entries, "readme"));
        Assert.Equal(2, WorkspaceFilesModel.Filter(entries, "SRC/APP").Count);
        Assert.Single(WorkspaceFilesModel.Filter(entries, "utils"));
        Assert.Empty(WorkspaceFilesModel.Filter(entries, "nomatch"));
    }

    // ── Sort tiering ────────────────────────────────────────────────────

    [Fact]
    public void Sort_EditedBeforeReadBeforeUntouched()
    {
        var entries = WorkspaceFilesModel.FromSessionFileList(List(
            File("plain.md"),
            File("seen.md", kind: "read"),
            File("wrote.md", kind: "modified"))).Entries;

        Assert.Equal("wrote.md", entries[0].Name);
        Assert.Equal("seen.md", entries[1].Name);
        Assert.Equal("plain.md", entries[2].Name);
    }

    [Fact]
    public void Sort_DirectoriesFirst()
    {
        var list = new SessionFileList
        {
            Key = "k",
            Files = new[] { File("zeta.md", kind: "modified") },
            Browser = new SessionFileBrowser
            {
                Entries = new[]
                {
                    new SessionFileBrowserEntry { Path = "lib", Name = "lib", Kind = "directory" },
                },
            },
            IsSupported = true,
        };

        var entries = WorkspaceFilesModel.FromSessionFileList(list).Entries;

        Assert.True(entries[0].IsDirectory);
        Assert.Equal("lib", entries[0].Name);
        Assert.Equal("zeta.md", entries[1].Name);
    }

    // ── Formatting helpers ──────────────────────────────────────────────

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2.0 KB")]
    [InlineData(5_242_880, "5.0 MB")]
    public void FormatSize_ProducesHumanReadable(long bytes, string expected)
    {
        Assert.Equal(expected, WorkspaceFilesModel.FormatSize(bytes));
    }

    [Fact]
    public void FormatSize_NullOrNegative_IsEmpty()
    {
        Assert.Equal(string.Empty, WorkspaceFilesModel.FormatSize(null));
        Assert.Equal(string.Empty, WorkspaceFilesModel.FormatSize(-5));
    }

    [Fact]
    public void DirectoryOf_RootFile_IsEmpty()
    {
        Assert.Equal(string.Empty, WorkspaceFilesModel.DirectoryOf("README.md"));
        Assert.Equal("a/b", WorkspaceFilesModel.DirectoryOf("a/b/c.md"));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData(" /src/app/ ", "src/app")]
    [InlineData(@"src\app", "src/app")]
    public void NormalizeBrowserPath_NormalizesForGatewayPathState(string? input, string expected)
    {
        Assert.Equal(expected, WorkspaceFilesModel.NormalizeBrowserPath(input));
    }
}
