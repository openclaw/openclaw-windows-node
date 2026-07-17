using System;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace OpenClaw.Shared.Tests.Architecture;

/// <summary>
/// Source-shape guard for the ledger row <c>setup-shellescape-closed</c>. PR 3 migrated
/// <c>SetupSteps</c>' WSL quoting to <see cref="OpenClaw.Shared.WslShellQuoting"/> and deleted
/// the two divergent private <c>ShellEscape</c> helpers (one escape-only, one fully-wrapped).
/// Re-adding a local <c>ShellEscape</c> to <c>SetupSteps</c> would silently reintroduce the
/// divergent wrap-semantics bug this refactor closed — a wrong variant yields an unquoted or
/// double-quoted WSL argument, i.e. a broken or injectable setup script — so this test fails
/// if such a helper (or any call to one) reappears in <c>SetupSteps.cs</c>.
/// </summary>
public sealed class SetupStepsShellEscapeClosureTests
{
    [Fact]
    public void SetupSteps_DoesNotReintroduce_PrivateShellEscape()
    {
        var setupSteps = ProductionSourceFiles.All
            .FirstOrDefault(f => f.Path.EndsWith("SetupSteps.cs", StringComparison.Ordinal));

        Assert.NotNull(setupSteps);

        var shellEscapeReference = new Regex(@"\bShellEscape\s*\(");
        Assert.False(
            shellEscapeReference.IsMatch(setupSteps!.Text),
            "SetupSteps.cs must not declare or call a local ShellEscape helper; build WSL command " +
            "lines with OpenClaw.Shared.WslShellQuoting (EscapePosixSingleQuoteInner for a value the " +
            "caller wraps itself, QuotePosixSingleQuote for a standalone token). " +
            "See docs/ARCHITECTURE.md -> wsl-posix-quoting / setup-shellescape-closed.");
    }
}
