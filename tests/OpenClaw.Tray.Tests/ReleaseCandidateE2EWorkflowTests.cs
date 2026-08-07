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
        Assert.Contains("INPUT_CANDIDATE_SHA256: ${{ inputs.candidate_sha256 }}", workflow);
        Assert.Contains("INPUT_CANDIDATE_VERSION: ${{ inputs.candidate_version }}", workflow);
        Assert.Contains("INPUT_WINDOWS_NODE_RELEASE_TAG: ${{ inputs.windows_node_release_tag }}", workflow);
        Assert.Contains("INPUT_WINDOWS_NODE_RELEASE_SHA: ${{ inputs.windows_node_release_sha }}", workflow);
        Assert.Contains("INPUT_ALLOW_PROTOCOL_MISMATCH: ${{ inputs.allow_protocol_mismatch }}", workflow);
        Assert.Contains("INPUT_WINDOWS_NODE_SHA: ${{ inputs.windows_node_sha }}", workflow);
    }

    [Fact]
    public void Workflow_BindsCheckoutTagAndArtifactToImmutableIdentities()
    {
        var workflow = ReadWorkflow();

        Assert.Contains("id-token: write", workflow);
        Assert.Contains("ref: ${{ inputs.windows_node_sha }}", workflow);
        Assert.Contains("fetch-depth: 0", workflow);
        Assert.Contains("$claims.job_workflow_sha -cne $env:EXPECTED_WINDOWS_NODE_SHA", workflow);
        Assert.Contains("$claims.job_workflow_ref -cne $expectedWorkflowRef", workflow);
        Assert.Contains("$tagObject.type -ne \"commit\"", workflow);
        Assert.Contains("$releaseSha -cne $env:EXPECTED_WINDOWS_NODE_RELEASE_SHA", workflow);
        Assert.Contains(@"-win-x64\.zip$", workflow);
        Assert.Contains(@"_x64\.msix$", workflow);
        Assert.Contains("$actualHash -cne $expectedHash", workflow);
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
