using System.Text.RegularExpressions;

namespace OpenClaw.Shared.Tests.Architecture;

/// <summary>
/// Validates the machine-readable ownership ledger in <c>docs/ARCHITECTURE.md</c>.
/// The ledger is the anti-regression backbone of the architecture refactor: it
/// records which responsibilities have been extracted out of god objects and
/// which must not be re-added. This test keeps the ledger structurally sound so
/// it stays enforceable rather than aspirational. It intentionally validates
/// *structure*, not prose, so ordinary edits do not make it brittle.
/// </summary>
public sealed class ArchitectureLedgerConsistencyTests
{
    private static readonly string[] ExpectedColumns =
    [
        "id", "status", "old_owner", "closed_responsibility", "new_owner",
        "allowed_residue", "invariant", "guard_test", "guard_type", "retirement_condition",
    ];

    private static readonly HashSet<string> ValidStatuses =
        new(StringComparer.Ordinal) { "planned", "authoritative", "closed" };

    private static readonly HashSet<string> ValidGuardTypes =
        new(StringComparer.Ordinal) { "behavioral", "golden", "source-shape", "review-only" };

    private static readonly Regex GuardTestNameRegex =
        new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$", RegexOptions.Compiled);

    private static bool IsPlaceholder(string value)
        => string.IsNullOrWhiteSpace(value)
           || value == "-"
           || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Ledger_is_present_and_well_formed()
    {
        var rows = ParseLedger(out var header);

        Assert.Equal(ExpectedColumns, header);
        Assert.NotEmpty(rows);

        foreach (var row in rows)
        {
            Assert.Equal(ExpectedColumns.Length, row.Cells.Count);
            foreach (var cell in row.Cells)
            {
                Assert.False(string.IsNullOrWhiteSpace(cell),
                    $"Ledger row '{row.Id}' has an empty cell; use '-' for genuinely empty cells.");
            }
        }
    }

    [Fact]
    public void Ledger_ids_are_unique()
    {
        var rows = ParseLedger(out _);
        var duplicates = rows
            .GroupBy(r => r.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0,
            $"Duplicate ledger ids: {string.Join(", ", duplicates)}");
    }

    [Fact]
    public void Ledger_status_and_guard_type_values_are_valid()
    {
        var rows = ParseLedger(out _);
        foreach (var row in rows)
        {
            Assert.True(ValidStatuses.Contains(row.Status),
                $"Ledger row '{row.Id}' has invalid status '{row.Status}'.");
            Assert.True(ValidGuardTypes.Contains(row.GuardType),
                $"Ledger row '{row.Id}' has invalid guard_type '{row.GuardType}'.");
        }
    }

    [Fact]
    public void Authoritative_and_closed_rows_are_guarded_or_review_only()
    {
        var rows = ParseLedger(out _);
        foreach (var row in rows.Where(r => r.Status is "authoritative" or "closed"))
        {
            if (row.GuardType == "review-only")
            {
                Assert.False(IsPlaceholder(row.GuardTest),
                    $"Ledger row '{row.Id}' is a review-only guard for a '{row.Status}' " +
                    "responsibility and must record a real rationale in guard_test, not a placeholder.");
                continue;
            }

            Assert.False(IsPlaceholder(row.GuardTest),
                $"Ledger row '{row.Id}' is '{row.Status}' but names no guard_test. " +
                "Authoritative/closed responsibilities must be protected by a guard test " +
                "or documented as a review-only guard.");

            Assert.True(GuardTestNameRegex.IsMatch(row.GuardTest),
                $"Ledger row '{row.Id}' guard_test '{row.GuardTest}' is not a valid " +
                "'Type.Method' (or 'Namespace.Type.Method') reference.");
        }
    }

    [Fact]
    public void Named_guard_tests_exist_in_test_sources()
    {
        var rows = ParseLedger(out _);
        var testsRoot = LocateTestsRoot();
        var sources = Directory
            .EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p =>
                !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText)
            .ToList();

        foreach (var row in rows.Where(r => r.GuardType is "behavioral" or "golden"))
        {
            // Rows without a concrete named guard are validated elsewhere.
            if (IsPlaceholder(row.GuardTest) || !GuardTestNameRegex.IsMatch(row.GuardTest))
            {
                continue;
            }

            var lastDot = row.GuardTest.LastIndexOf('.');
            var typeName = row.GuardTest[..lastDot];
            var methodName = row.GuardTest[(lastDot + 1)..];
            var simpleType = typeName[(typeName.LastIndexOf('.') + 1)..];

            var classPattern = new Regex($@"\b(class|record|struct)\s+{Regex.Escape(simpleType)}\b");
            var methodPattern = new Regex($@"\b{Regex.Escape(methodName)}\s*\(");

            var found = sources.Any(src => classPattern.IsMatch(src) && methodPattern.IsMatch(src));
            Assert.True(found,
                $"Ledger row '{row.Id}' names guard_test '{row.GuardTest}', but no method " +
                $"'{methodName}' was found in a type '{simpleType}' under tests/. If you renamed or " +
                "removed the guard test, update docs/ARCHITECTURE.md in the same change.");
        }
    }

    [Fact]
    public void Source_shape_guards_declare_a_retirement_condition()
    {
        var rows = ParseLedger(out _);
        foreach (var row in rows.Where(r => r.GuardType == "source-shape"))
        {
            Assert.False(row.RetirementCondition == "-" || string.IsNullOrWhiteSpace(row.RetirementCondition),
                $"Ledger row '{row.Id}' is a source-shape guard and must set a concrete " +
                "retirement_condition so it does not ossify into a brittle permanent test.");
        }
    }

    private static List<LedgerRow> ParseLedger(out string[] header)
    {
        var path = LocateArchitectureDoc();
        var text = File.ReadAllText(path);

        var match = Regex.Match(
            text,
            @"<!--\s*LEDGER:BEGIN\s*-->(?<body>.*?)<!--\s*LEDGER:END\s*-->",
            RegexOptions.Singleline);
        Assert.True(match.Success,
            "docs/ARCHITECTURE.md is missing the <!-- LEDGER:BEGIN --> / <!-- LEDGER:END --> markers.");

        var lines = match.Groups["body"].Value
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith('|'))
            .ToList();

        Assert.True(lines.Count >= 3,
            "Ledger must contain a header row, a separator row, and at least one data row.");

        header = SplitRow(lines[0]);

        // lines[1] must be the markdown separator (---). Validate it rather than
        // blindly skipping it, so a removed separator can't silently drop the
        // first data row from validation.
        var separator = SplitRow(lines[1]);
        Assert.True(separator.Length == header.Length,
            "Ledger separator row column count does not match the header row.");
        Assert.All(separator, cell => Assert.Matches("^:?-{3,}:?$", cell));

        var rows = new List<LedgerRow>();
        foreach (var line in lines.Skip(2))
        {
            var cells = SplitRow(line);
            rows.Add(new LedgerRow(cells));
        }

        return rows;
    }

    private static string[] SplitRow(string line)
    {
        // Trim the leading/trailing pipe, then split on the interior pipes.
        var trimmed = line.Trim().Trim('|');
        return trimmed.Split('|').Select(c => c.Trim()).ToArray();
    }

    private static string LocateArchitectureDoc()
    {
        // Honor an explicit repo-root override (used in linked git worktrees),
        // otherwise walk up from the test bin folder to find docs/ARCHITECTURE.md.
        var repoRoot = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            var direct = Path.Combine(repoRoot, "docs", "ARCHITECTURE.md");
            if (File.Exists(direct)) return direct;
        }

        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "docs", "ARCHITECTURE.md");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException(
            "Could not locate docs/ARCHITECTURE.md from the test working directory. " +
            "Set OPENCLAW_REPO_ROOT to the repository root when running in a linked worktree.");
    }

    private static string LocateTestsRoot()
    {
        var repoRoot = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            var direct = Path.Combine(repoRoot, "tests");
            if (Directory.Exists(direct)) return direct;
        }

        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "tests");
            if (Directory.Exists(Path.Combine(candidate, "OpenClaw.Shared.Tests"))) return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            "Could not locate the tests/ directory from the test working directory. " +
            "Set OPENCLAW_REPO_ROOT to the repository root when running in a linked worktree.");
    }

    private sealed class LedgerRow
    {
        public LedgerRow(string[] cells)
        {
            Cells = cells;
        }

        public IReadOnlyList<string> Cells { get; }

        public string Id => Cells.Count > 0 ? Cells[0] : "";
        public string Status => Cells.Count > 1 ? Cells[1] : "";
        public string GuardTest => Cells.Count > 7 ? Cells[7] : "";
        public string GuardType => Cells.Count > 8 ? Cells[8] : "";
        public string RetirementCondition => Cells.Count > 9 ? Cells[9] : "";
    }
}
