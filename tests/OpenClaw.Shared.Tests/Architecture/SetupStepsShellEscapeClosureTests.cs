using System;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace OpenClaw.Shared.Tests.Architecture;

/// <summary>
/// Source-shape guard for the ledger row <c>setup-shellescape-closed</c>. PR 3 migrated
/// <c>SetupSteps</c>' WSL quoting to <see cref="OpenClaw.Shared.WslShellQuoting"/> and deleted
/// the two divergent private <c>ShellEscape</c> helpers (one escape-only, one fully-wrapped).
/// Re-adding a local <c>ShellEscape</c> anywhere under <c>OpenClaw.SetupEngine</c> would silently
/// reintroduce the divergent wrap-semantics bug this refactor closed — a wrong variant yields an
/// unquoted or double-quoted WSL argument, i.e. a broken or injectable setup script — so this
/// test fails if such a helper (or any call to one) reappears anywhere in that project.
///
/// Scans every file under <c>src/OpenClaw.SetupEngine/</c> rather than just <c>SetupSteps.cs</c>,
/// since the E0 one-file-per-step split moved the WSL-command-building steps that this guard
/// protects (e.g. <c>ValidateWslLockdownStep</c>, <c>ConfigureGatewayStep</c>,
/// <c>CreateWslInstanceStep</c>) out of that single file.
/// </summary>
public sealed class SetupStepsShellEscapeClosureTests
{
    [Fact]
    public void SetupEngine_DoesNotReintroduce_PrivateShellEscape()
    {
        var setupEngineFiles = ProductionSourceFiles.All
            .Where(f => f.Path.Contains(
                System.IO.Path.DirectorySeparatorChar + "OpenClaw.SetupEngine" + System.IO.Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(setupEngineFiles);

        var shellEscapeReference = new Regex(@"\bShellEscape\s*\(");
        foreach (var file in setupEngineFiles)
        {
            Assert.False(
                shellEscapeReference.IsMatch(file.Text),
                $"{file.Path} must not declare or call a local ShellEscape helper; build WSL command " +
                "lines with OpenClaw.Shared.WslShellQuoting (EscapePosixSingleQuoteInner for a value the " +
                "caller wraps itself, QuotePosixSingleQuote for a standalone token). " +
                "See docs/ARCHITECTURE.md -> wsl-posix-quoting / setup-shellescape-closed.");
        }
    }
}
