using System.Runtime.Versioning;
using OpenClaw.Connection.Migration;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

[SupportedOSPlatform("windows")]
public sealed class InnoMigrationStartupAdmissionTests
{
    [Fact]
    public void Evaluate_NoMigrationRecords_ContinuesWhileHoldingRuntimeLease()
    {
        using var temp = new TempDirectory();
        var binding = MigrationRecordTests.CreateRecord(temp, "completed").Binding;

        var decision = Evaluate(binding, out var lease);
        using var _ = lease;

        Assert.Equal(InnoMigrationStartupDecision.Continue, decision);
        Assert.NotNull(lease);
        Assert.Throws<IOException>(() => MigrationOperationLock.AcquireExclusive(binding));
    }

    [Fact]
    public void Evaluate_CompletedReceipt_StopsWithCompletedGuidance()
    {
        using var temp = new TempDirectory();
        var record = MigrationRecordTests.CreateRecord(temp, "completed");
        WriteReceipt(record);

        var decision = Evaluate(record.Binding, out var lease);
        using var _ = lease;

        Assert.Equal(InnoMigrationStartupDecision.Completed, decision);
        Assert.NotNull(lease);
    }

    [Fact]
    public void Evaluate_MigrationInProgress_StopsWithCheckFailed()
    {
        using var temp = new TempDirectory();
        var binding = MigrationRecordTests.CreateRecord(temp, "completed").Binding;
        using var exclusive = MigrationOperationLock.AcquireExclusive(binding);

        var decision = Evaluate(binding, out var lease);
        using var _ = lease;

        Assert.Equal(InnoMigrationStartupDecision.CheckFailed, decision);
        Assert.Null(lease);
    }

    [Fact]
    public void Evaluate_UnreadableReceipt_StopsWithCheckFailed()
    {
        using var temp = new TempDirectory();
        var binding = MigrationRecordTests.CreateRecord(temp, "completed").Binding;
        // A directory in the receipt's place cannot be opened as a file, which is an IO failure
        // rather than a corrupt receipt, so the check must fail closed.
        Directory.CreateDirectory(Path.Combine(ReceiptDirectory(binding), MigrationRecordCodec.CompletionFileName));

        var decision = Evaluate(binding, out var lease);
        using var _ = lease;

        Assert.Equal(InnoMigrationStartupDecision.CheckFailed, decision);
        Assert.NotNull(lease);
    }

    [Fact]
    public void Evaluate_CorruptReceipt_Continues()
    {
        using var temp = new TempDirectory();
        var binding = MigrationRecordTests.CreateRecord(temp, "completed").Binding;
        var directory = ReceiptDirectory(binding);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, MigrationRecordCodec.CompletionFileName), [1, 2, 3]);

        var decision = Evaluate(binding, out var lease);
        using var _ = lease;

        Assert.Equal(InnoMigrationStartupDecision.Continue, decision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Evaluate_UnavailableRuntimeLock_StillHonorsReceipt(bool withReceipt)
    {
        using var temp = new TempDirectory();
        var record = MigrationRecordTests.CreateRecord(temp, "completed");
        // A directory in the lock's place fails acquisition without proving a live migration.
        Directory.CreateDirectory(Path.Combine(ReceiptDirectory(record.Binding), "prepare.lock"));
        if (withReceipt)
            WriteReceipt(record);

        var decision = Evaluate(record.Binding, out var lease);
        using var _ = lease;

        Assert.Equal(withReceipt ? InnoMigrationStartupDecision.Completed : InnoMigrationStartupDecision.Continue,
            decision);
        Assert.Null(lease);
    }

    private static InnoMigrationStartupDecision Evaluate(MigrationBinding binding, out IDisposable? lease) =>
        new InnoMigrationStartupAdmission(binding, NullLogger.Instance).Evaluate(out lease);

    private static string ReceiptDirectory(MigrationBinding binding) =>
        Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);

    private static void WriteReceipt(MigrationRecord record)
    {
        var directory = ReceiptDirectory(record.Binding);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, MigrationRecordCodec.CompletionFileName),
            MigrationRecordCodec.Encode(record, record.CreatedUtc));
    }
}
