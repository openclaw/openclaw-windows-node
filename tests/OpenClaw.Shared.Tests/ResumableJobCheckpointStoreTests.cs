using System;
using System.Collections.Generic;
using System.IO;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// U3 checkpoint-store controls against the REAL filesystem: persisted-record reload in a NEW store instance,
/// an in-process interruption between an effect and its save, identity collision, invalid/unreadable records,
/// and a failed write. These do NOT prove process-crash / power-loss durability, cross-process exclusivity, or
/// exactly-once execution, and no session history or credentials are involved.
/// </summary>
public class ResumableJobCheckpointStoreTests
{
    // ---- Adopted verbatim from Mini-Actual-Checkpoint-Admission-420e-Counterexamples: the two FAILED cases. ----
    [Fact]
    public void MiniReview_CheckpointAdmissionCannotSaveRecordThatCannotBeReloaded()
    {
        var store = new ResumableJobCheckpointStore(NewRoot());
        var checkpoint = new ResumableJobCheckpoint("bounded-job");
        for (int i = 1; i <= 70; i++) Assert.True(checkpoint.TryMarkCompleted(new string('x', 4090) + i, i));
        Assert.False(store.TrySave(checkpoint, "synthetic-source", out _));
    }
    [Fact]
    public void MiniReview_CheckpointAdmissionRejectsCorruptedReadbackIdentityAndSteps()
    {
        var store = new ResumableJobCheckpointStore(NewRoot(), new MiniReviewCorruptingFileSystem());
        var checkpoint = new ResumableJobCheckpoint("owned-job");
        checkpoint.TryMarkCompleted("owned-step", 1);
        Assert.False(store.TrySave(checkpoint, "synthetic-source", out _));
    }
    private sealed class MiniReviewCorruptingFileSystem : IFileSystem
    {
        public bool FileExists(string p) => File.Exists(p);
        public string ReadAllText(string p) => File.ReadAllText(p);
        public void WriteAllText(string p, string text)
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<ResumableJobCheckpointRecord>(text)!;
            File.WriteAllText(p, System.Text.Json.JsonSerializer.Serialize(parsed with { JobId="different-job", CompletedStepIds=new[] { "wrong-step" } }));
        }
        public void CreateDirectory(string p) => Directory.CreateDirectory(p);
        public bool DirectoryExists(string p) => Directory.Exists(p);
        public void CopyFile(string a,string b,bool overwrite) => File.Copy(a,b,overwrite);
        public void DeleteFile(string p) => File.Delete(p);
        public void MoveFile(string a,string b,bool overwrite) => File.Move(a,b,overwrite);
        public long GetFileLength(string p) => new FileInfo(p).Length;
    }
    // ---- Adopted verbatim from Mini-Actual-Checkpoint-e0ca-Counterexamples: the four previously-FAILED cases. ----
    [Fact]
    public void MiniReview_CheckpointSaveRejectsChangedSourceIdentity()
    {
        var root = NewRoot(); var store = new ResumableJobCheckpointStore(root);
        store.TryBind("same-job", "source-a", out var first, out _);
        first!.TryMarkCompleted("step-one", 1);
        Assert.True(store.TrySave(first, "source-a", out _));
        var collision = new ResumableJobCheckpoint("same-job");
        Assert.False(store.TrySave(collision, "source-b", out _));
        Assert.Equal(ResumableJobCheckpointOutcome.Resumed, store.TryBind("same-job", "source-a", out var kept, out _));
        Assert.Equal(1, kept!.LastCompletedOrdinal);
    }
    [Fact]
    public void MiniReview_CheckpointStaleSaveCannotRegressCompletedOrdinal()
    {
        var root = NewRoot(); var store = new ResumableJobCheckpointStore(root);
        store.TryBind("same-job", "source-a", out var first, out _);
        first!.TryMarkCompleted("step-one", 1);
        Assert.True(store.TrySave(first, "source-a", out _));
        var stale = ResumableJobCheckpoint.Load("same-job", 0, Array.Empty<string>());
        Assert.False(store.TrySave(stale, "source-a", out _));
        store.TryBind("same-job", "source-a", out var kept, out _);
        Assert.Equal(1, kept!.LastCompletedOrdinal);
    }
    [Fact]
    public void MiniReview_CheckpointDistinctJobIdsCannotOverwriteSanitizedPath()
    {
        var root = NewRoot(); var store = new ResumableJobCheckpointStore(root);
        store.TryBind("job/a", "source-a", out var first, out _);
        Assert.True(store.TrySave(first!, "source-a", out _));
        store.TryBind("job_a", "source-a", out var second, out _);
        Assert.NotNull(second);
        Assert.True(store.TrySave(second!, "source-a", out _));
        Assert.Equal(ResumableJobCheckpointOutcome.Resumed, store.TryBind("job/a", "source-a", out _, out _));
    }
    [Fact]
    public void MiniReview_CheckpointInvalidOrdinalReturnsUnreadableInsteadOfThrowing()
    {
        var root = NewRoot(); var store = new ResumableJobCheckpointStore(root);
        store.TryBind("same-job", "source-a", out var first, out _);
        Assert.True(store.TrySave(first!, "source-a", out _));
        var path = Directory.GetFiles(root)[0];
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { JobId="same-job", SourceIdentity="source-a", LastCompletedOrdinal=2, CompletedStepIds=new[] { "step-one" } }));
        Assert.Equal(ResumableJobCheckpointOutcome.Unreadable, store.TryBind("same-job", "source-a", out _, out _));
    }

    private static string NewRoot() =>
        Directory.CreateTempSubdirectory("oc-u3-checkpoint-").FullName;

    // A persisted record reloads in a NEW store instance (same root) and resumes at the next ordinal. This is a
    // store-instance reload, NOT a proof of process-crash durability.
    [Fact]
    public void Persisted_record_reloads_in_a_new_store_instance_continues_at_next_ordinal()
    {
        var root = NewRoot();
        var store = new ResumableJobCheckpointStore(root);
        Assert.Equal(ResumableJobCheckpointOutcome.Started, store.TryBind("job-1", "src-a", out var first, out _));
        Assert.True(first!.TryMarkCompleted("step-a", 1));
        Assert.True(first.TryMarkCompleted("step-b", 2));
        Assert.True(store.TrySave(first, "src-a", out var saveFailure), saveFailure);

        var restarted = new ResumableJobCheckpointStore(root);
        Assert.Equal(ResumableJobCheckpointOutcome.Resumed, restarted.TryBind("job-1", "src-a", out var resumed, out _));
        Assert.Equal(2, resumed!.LastCompletedOrdinal);
        Assert.Equal(3, resumed.NextOrdinal);
        Assert.Equal(new[] { "step-a", "step-b" }, resumed.CompletedStepIds);
    }

    // In-process interruption: the effect for a step is applied, then the save is skipped (simulating an
    // interruption BEFORE the checkpoint). A later store instance never saw the effect, so the step re-runs; the
    // effect is idempotent, so the applied-effect set stays size one (no duplicate side effect). This is NOT a
    // process-crash / power-loss durability proof, and the store itself does not dedupe effects.
    [Fact]
    public void Interrupted_before_save_replays_the_effect_in_process_not_duplicating()
    {
        var root = NewRoot();
        var effects = new HashSet<string>();
        void Apply(string step) => effects.Add(step);   // idempotent by step id

        var store = new ResumableJobCheckpointStore(root);
        store.TryBind("job-2", "src-a", out var c1, out _);
        Assert.Equal(1, c1!.NextOrdinal);   // fresh checkpoint starts at ordinal 1
        Apply("effect-1");                 // effect applied ...
        // ... crash BEFORE TrySave: nothing durable is written.

        var restarted = new ResumableJobCheckpointStore(root);
        Assert.Equal(ResumableJobCheckpointOutcome.Started, restarted.TryBind("job-2", "src-a", out var c2, out _));
        Assert.Equal(0, c2!.LastCompletedOrdinal);   // the checkpoint never observed the effect
        Apply("effect-1");                 // replayed on resume (idempotent)
        Assert.True(c2.TryMarkCompleted("step-1", 1));   // reconciliation adopts the applied ordinal
        Assert.True(restarted.TrySave(c2, "src-a", out _));
        Assert.Single(effects);            // NO duplicate side effect
    }

    // A record for the SAME job but a DIFFERENT source identity must not be clobbered or resumed.
    [Fact]
    public void Identity_collision_is_preserved_not_clobbered()
    {
        var root = NewRoot();
        var store = new ResumableJobCheckpointStore(root);
        store.TryBind("job-3", "src-a", out var c, out _);
        Assert.True(c!.TryMarkCompleted("step-a", 1));
        Assert.True(store.TrySave(c, "src-a", out _));

        Assert.Equal(ResumableJobCheckpointOutcome.IdentityCollision,
            store.TryBind("job-3", "src-b", out var blocked, out var failure));
        Assert.Null(blocked);
        Assert.NotNull(failure);

        // The original receipt is intact and still resumable by its own identity.
        Assert.Equal(ResumableJobCheckpointOutcome.Resumed, store.TryBind("job-3", "src-a", out var intact, out _));
        Assert.Equal(1, intact!.LastCompletedOrdinal);
    }

    // An unreadable record is reported and PRESERVED (a corrupt receipt is not silently deleted).
    [Fact]
    public void Unreadable_record_is_preserved_not_deleted()
    {
        var root = NewRoot();
        var store = new ResumableJobCheckpointStore(root);
        store.TryBind("job-4", "src-a", out var c, out _);
        Assert.True(store.TrySave(c!, "src-a", out _));
        var path = Directory.GetFiles(root)[0];
        File.WriteAllText(path, "{ not json");

        Assert.Equal(ResumableJobCheckpointOutcome.Unreadable,
            store.TryBind("job-4", "src-a", out var none, out var failure));
        Assert.Null(none);
        Assert.NotNull(failure);
        Assert.True(File.Exists(path));   // preserved
    }

    // A failed write (atomic move throws) must leave the PREVIOUS durable checkpoint intact.
    [Fact]
    public void Failed_write_preserves_previous_checkpoint()
    {
        var root = NewRoot();
        var ok = new ResumableJobCheckpointStore(root);
        ok.TryBind("job-5", "src-a", out var c, out _);
        Assert.True(c!.TryMarkCompleted("step-a", 1));
        Assert.True(ok.TrySave(c, "src-a", out _));

        var failing = new ResumableJobCheckpointStore(root, new ThrowingMoveFileSystem());
        var c2 = ResumableJobCheckpoint.Load("job-5", 1, new[] { "step-a" });
        Assert.True(c2.TryMarkCompleted("step-b", 2));
        Assert.False(failing.TrySave(c2, "src-a", out var failure));
        Assert.NotNull(failure);

        Assert.Equal(ResumableJobCheckpointOutcome.Resumed, ok.TryBind("job-5", "src-a", out var intact, out _));
        Assert.Equal(1, intact!.LastCompletedOrdinal);   // previous durable checkpoint preserved
    }

    private sealed class ThrowingMoveFileSystem : IFileSystem
    {
        private static readonly RealFileSystem Inner = RealFileSystem.Instance;
        public bool FileExists(string path) => Inner.FileExists(path);
        public string ReadAllText(string path) => Inner.ReadAllText(path);
        public void WriteAllText(string path, string content) => Inner.WriteAllText(path, content);
        public void CreateDirectory(string path) => Inner.CreateDirectory(path);
        public bool DirectoryExists(string path) => Inner.DirectoryExists(path);
        public void CopyFile(string source, string destination, bool overwrite) => Inner.CopyFile(source, destination, overwrite);
        public void DeleteFile(string path) => Inner.DeleteFile(path);
        public long GetFileLength(string path) => Inner.GetFileLength(path);
        public void MoveFile(string source, string destination, bool overwrite) =>
            throw new IOException("simulated atomic-move failure");
    }
}