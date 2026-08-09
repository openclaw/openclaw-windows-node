namespace OpenClaw.Tray.Tests;

public sealed class ReleaseCandidateE2EWorkflowTests
{
    private static string ReadWorkflow()
    {
        return File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            ".github",
            "workflows",
            "release-candidate-e2e.yml"));
    }

    [Fact]
    public void PowerShellRunBlocks_DoNotInterpolateWorkflowCallInputs()
    {
        var workflow = ReadWorkflow();

        foreach (var runBlock in ExtractRunBlocks(workflow))
            Assert.DoesNotContain("${{ inputs.", runBlock);

        Assert.Contains("INPUT_CANDIDATE_ARTIFACT_NAME: ${{ inputs.candidate_artifact_name }}", workflow);
        Assert.Contains("INPUT_CANDIDATE_ARTIFACT_RUN_ID: ${{ inputs.candidate_artifact_run_id }}", workflow);
        Assert.Contains("INPUT_CANDIDATE_SHA256: ${{ inputs.candidate_sha256 }}", workflow);
        Assert.Contains("INPUT_CANDIDATE_VERSION: ${{ inputs.candidate_version }}", workflow);
        Assert.Contains("INPUT_WINDOWS_NODE_SOURCE: ${{ inputs.windows_node_source }}", workflow);
        Assert.Contains("INPUT_WINDOWS_NODE_SOURCE_SHA: ${{ inputs.windows_node_source_sha }}", workflow);
        Assert.Contains("INPUT_WINDOWS_NODE_WORKFLOW_SHA: ${{ inputs.windows_node_workflow_sha }}", workflow);
        Assert.Contains("INPUT_WINDOWS_NODE_RELEASE_TAG: ${{ inputs.windows_node_release_tag }}", workflow);
        Assert.Contains("INPUT_WINDOWS_NODE_RELEASE_ASSET_NAME: ${{ inputs.windows_node_release_asset_name }}", workflow);
        Assert.Contains("INPUT_WINDOWS_NODE_RELEASE_ASSET_SHA256: ${{ inputs.windows_node_release_asset_sha256 }}", workflow);
        Assert.Contains("INPUT_ALLOW_PROTOCOL_MISMATCH: ${{ inputs.allow_protocol_mismatch }}", workflow);
    }

    [Fact]
    public void Workflow_BindsCheckoutTagAndArtifactToImmutableIdentities()
    {
        var workflow = ReadWorkflow();

        Assert.Contains("actions: read", workflow);
        Assert.Contains("id-token: write", workflow);
        Assert.Contains("run-id: ${{ inputs.candidate_artifact_run_id }}", workflow);
        Assert.Contains("github-token: ${{ github.token }}", workflow);
        Assert.Contains(
            "$env:INPUT_CANDIDATE_ARTIFACT_RUN_ID -notmatch '^[1-9][0-9]{0,19}$'",
            workflow);
        Assert.Contains("$candidateArtifactRunId -gt 9007199254740991", workflow);
        Assert.Contains("ref: ${{ inputs.windows_node_source_sha }}", workflow);
        Assert.Contains("windows_node_source must be release or main.", workflow);
        Assert.Contains("main mode must not receive Windows-node release artifact inputs.", workflow);
        Assert.Contains("windows_node_source_sha must be a full lowercase Git SHA.", workflow);
        Assert.Contains("windows_node_workflow_sha must be a full lowercase Git SHA.", workflow);
        Assert.Contains("$claims.job_workflow_sha -cne $env:EXPECTED_WINDOWS_NODE_WORKFLOW_SHA", workflow);
        Assert.Contains("$claims.job_workflow_ref -cne $expectedWorkflowRef", workflow);
        Assert.Contains("Windows-node checkout revision mismatch", workflow);
        Assert.Contains("$tagObject.type -ne \"commit\"", workflow);
        Assert.Contains("$releaseSha -cne $env:EXPECTED_WINDOWS_NODE_SOURCE_SHA", workflow);
        Assert.Contains("$asset.name -cne $env:EXPECTED_WINDOWS_NODE_RELEASE_ASSET_NAME", workflow);
        Assert.Contains("$releaseDeclaredHash -cne $env:EXPECTED_WINDOWS_NODE_RELEASE_ASSET_SHA256", workflow);
        Assert.Contains(@"-win-x64\.zip$", workflow);
        Assert.Contains(@"_x64\.msix$", workflow);
        Assert.Contains("$actualHash -cne $env:EXPECTED_WINDOWS_NODE_RELEASE_ASSET_SHA256", workflow);
        Assert.Contains("OPENCLAW_E2E_GATEWAY_VERSION: ${{ inputs.candidate_version }}", workflow);
        Assert.Contains("OPENCLAW_E2E_GATEWAY_PACKAGE_TGZ: ${{ steps.candidate.outputs.tarball }}", workflow);
    }

    [Fact]
    public void Workflow_BuildsOnlyTheDeclaredMainSourceTray()
    {
        var workflow = ReadWorkflow();

        Assert.Contains("if: inputs.windows_node_source == 'release'", workflow);
        Assert.Contains("if: inputs.windows_node_source == 'main'", workflow);
        Assert.Contains("Expected exactly one win-x64 OpenClaw.Tray.WinUI.exe from Windows-node main", workflow);
        Assert.Contains("OPENCLAW_E2E_TRAY_EXE: ${{ inputs.windows_node_source == 'release' && steps.windows_node_release_artifact.outputs.tray_exe || steps.windows_node_main_source.outputs.tray_exe }}", workflow);
    }

    [Fact]
    public void Workflow_RestoresHostedBuildPrerequisites()
    {
        var workflow = ReadWorkflow();

        const string e2eRestore = "dotnet restore tests/OpenClaw.E2ETests -r win-x64";
        const string e2eNoRestoreBuild = "dotnet build tests/OpenClaw.E2ETests -c Debug -r win-x64 --no-restore";

        var e2eRestoreIndex = workflow.IndexOf(e2eRestore, StringComparison.Ordinal);
        var e2eNoRestoreBuildIndex = workflow.IndexOf(e2eNoRestoreBuild, StringComparison.Ordinal);

        Assert.Contains("fetch-depth: 0", workflow);
        Assert.Contains("dotnet restore src/OpenClaw.Tray.WinUI -r win-x64", workflow);
        Assert.True(e2eRestoreIndex >= 0);
        Assert.Equal(e2eRestoreIndex, workflow.LastIndexOf(e2eRestore, StringComparison.Ordinal));
        Assert.True(e2eNoRestoreBuildIndex >= 0);
        Assert.True(e2eRestoreIndex < e2eNoRestoreBuildIndex);
    }

    [Fact]
    public void Workflow_RecordsOnlyExplicitProtocolMismatchOutcome()
    {
        var workflow = ReadWorkflow();

        Assert.Contains("outcome: ${{ steps.e2e.outputs.outcome }}", workflow);
        Assert.Contains(
            "$result.source -cnotin \"app.status.nodeError\", \"openclaw-tray.log.node_rx_error_code\"",
            workflow);
        Assert.Contains("$result.code -cne \"PROTOCOL_MISMATCH\"", workflow);
        Assert.Contains("\"outcome=protocol_mismatch\" >> $env:GITHUB_OUTPUT", workflow);
        Assert.Contains("\"outcome=passed\" >> $env:GITHUB_OUTPUT", workflow);
        Assert.Contains("$executed -lt 1", workflow);
    }

    private static IEnumerable<string> ExtractRunBlocks(string workflow)
    {
        var lines = workflow.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].TrimStart().Equals("run: |", StringComparison.Ordinal))
                continue;

            var runIndent = lines[index].Length - lines[index].TrimStart().Length;
            var block = new List<string>();
            for (index++; index < lines.Length; index++)
            {
                var line = lines[index];
                if (line.Length > 0 &&
                    line.Length - line.TrimStart().Length <= runIndent)
                {
                    index--;
                    break;
                }

                block.Add(line);
            }

            yield return string.Join('\n', block);
        }
    }
}
