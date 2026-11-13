using System.Collections;
using System.Text;
using System.Text.Json;
using OpenClaw.TestSupport;

namespace OpenClaw.Shared.Tests;

public sealed class NativeUiExceptionDiagnosticsTests
{
    private const string Invocation = "00112233445566778899aabbccddeeff";
    private const string Revision = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void ReadyAndException_AreFlushedBeforeDispose_WithOnlyTheAllowlistedSchema()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("metadata.jsonl");
        using var fallback = new StringWriter();
        using var sink = NativeUiExceptionDiagnostics.Open(path, Invocation, Revision, fallbackWriter: fallback.WriteLine);
        var ready = Assert.Single(Read(path));
        Assert.Equal("ready", ready.GetProperty("source").GetString());
        Assert.Equal(1, ready.GetProperty("sequence").GetInt32());
        Assert.Equal(Environment.ProcessId, ready.GetProperty("processId").GetInt32());
        Assert.Equal(Environment.CurrentManagedThreadId, ready.GetProperty("managedThreadId").GetInt32());
        Assert.Equal(Invocation, ready.GetProperty("invocationId").GetString());
        Assert.Equal(Revision, ready.GetProperty("configuredSourceRevision").GetString());

        var original = new PoisonException();
        using var scope = sink.BeginChatScope();
        scope.SetPhase(NativeUiPhase.ChatBody);
        var dispatch = sink.BeginDispatch();
        sink.RecordException(NativeUiExceptionSource.DispatchCatch, () => original, dispatch);
        var record = Assert.Single(Read(path), r => r.GetProperty("source").GetString() == "DispatchCatch");
        Assert.Equal(new[]
        {
            "configuredSourceRevision", "exceptionAvailable", "exceptionReadFailed", "exceptionType", "invocationId",
            "lastStartedDispatchId", "lastStartedDispatchState", "managedThreadId", "phase", "processId",
            "reportedHResult", "reportedHResultHex", "schema", "scopeId", "sequence", "source", "sourceDispatchId"
        }, record.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.True(record.GetProperty("exceptionAvailable").GetBoolean());
        Assert.False(record.GetProperty("exceptionReadFailed").GetBoolean());
        Assert.Equal("PoisonException", record.GetProperty("exceptionType").GetString());
        Assert.Equal(-2147467259, record.GetProperty("reportedHResult").GetInt32());
        Assert.Equal("0x80004005", record.GetProperty("reportedHResultHex").GetString());
        Assert.Equal("ChatBody", record.GetProperty("phase").GetString());
        Assert.Equal(1, record.GetProperty("scopeId").GetInt64());
        Assert.Equal(1, record.GetProperty("sourceDispatchId").GetInt64());
        Assert.Equal("faulted", record.GetProperty("lastStartedDispatchState").GetString());
        Assert.DoesNotContain("PRIVATE", ReadText(path), StringComparison.Ordinal);
        Assert.False(sink.WriteFailed);
        Assert.Equal(string.Empty, fallback.ToString());
    }

    [Fact]
    public void NullOrFailedExceptionProjection_IsExplicitNotAnInventedHResult()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("metadata.jsonl");
        using var sink = NativeUiExceptionDiagnostics.Open(path, Invocation, "PRIVATE invalid revision");
        sink.RecordException(NativeUiExceptionSource.WinUiUnhandled, () => null);
        sink.RecordException(NativeUiExceptionSource.WinUiUnhandled, () => throw new PoisonException());
        var records = Read(path);
        Assert.Equal(3, records.Length);
        Assert.False(records[1].GetProperty("exceptionReadFailed").GetBoolean());
        Assert.True(records[2].GetProperty("exceptionReadFailed").GetBoolean());
        foreach (var record in records)
        {
            Assert.False(record.GetProperty("exceptionAvailable").GetBoolean());
            Assert.Equal(JsonValueKind.Null, record.GetProperty("exceptionType").ValueKind);
            Assert.Equal(JsonValueKind.Null, record.GetProperty("reportedHResult").ValueKind);
            Assert.Equal(JsonValueKind.Null, record.GetProperty("reportedHResultHex").ValueKind);
            Assert.Equal(JsonValueKind.Null, record.GetProperty("configuredSourceRevision").ValueKind);
        }
        Assert.DoesNotContain("PRIVATE", ReadText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownEnumsAndGenericTypes_CannotIntroduceArbitrarySchemaValues()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("PRIVATE-metadata.jsonl");
        using var sink = NativeUiExceptionDiagnostics.Open(path, Invocation, Revision);
        sink.SetPhase((NativeUiPhase)int.MaxValue);
        sink.RecordException((NativeUiExceptionSource)int.MaxValue, () => new GenericException<PoisonException>());
        var record = Read(path)[^1];
        Assert.Equal("Unknown", record.GetProperty("source").GetString());
        Assert.Equal("Unknown", record.GetProperty("phase").GetString());
        Assert.Equal("GenericException_1", record.GetProperty("exceptionType").GetString());
        Assert.DoesNotContain("PoisonException", ReadText(path), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", ReadText(path), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("write")]
    [InlineData("flush")]
    public void InitializationFailure_EmitsFailureSignalAndNeverEscapes(string fault)
    {
        using var directory = new TempDirectory();
        var stream = new FaultStream { Fault = fault };
        var signals = 0;
        using var sink = NativeUiExceptionDiagnostics.Open(directory.Combine("metadata.jsonl"), Invocation, Revision,
            () => signals++, _ => fault == "open" ? throw new IOException("PRIVATE open failure") : stream);
        Assert.True(sink.WriteFailed);
        Assert.Equal(1, signals);
        Assert.Equal(fault == "open" ? 0 : 1, stream.DisposeCount);
        sink.RecordException(NativeUiExceptionSource.WinUiUnhandled, () => throw new PoisonException());
        Assert.Equal(1, signals);
    }

    [Fact]
    public void ScopesAndDispatches_DoNotAttributeOlderCompletionToNewerWork()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("metadata.jsonl");
        using var sink = NativeUiExceptionDiagnostics.Open(path, Invocation, Revision);
        using var first = sink.BeginChatScope();
        var oldDispatch = sink.BeginDispatch();
        using var second = sink.BeginChatScope();
        var newDispatch = sink.BeginDispatch();
        first.Dispose();
        first.SetPhase(NativeUiPhase.ChatClosed);
        sink.CompleteDispatch(oldDispatch);
        second.SetPhase(NativeUiPhase.ChatBody);
        sink.RecordException(NativeUiExceptionSource.DispatchCatch, () => null, oldDispatch);
        var observed = Read(path)[^1];
        Assert.Equal(2, observed.GetProperty("scopeId").GetInt64());
        Assert.Equal(2, observed.GetProperty("lastStartedDispatchId").GetInt64());
        Assert.Equal(1, observed.GetProperty("sourceDispatchId").GetInt64());
        Assert.Equal("running", observed.GetProperty("lastStartedDispatchState").GetString());
        sink.CompleteDispatch(newDispatch);
        second.Dispose();
        sink.RecordException(NativeUiExceptionSource.WinUiUnhandled, () => null);
        var late = Read(path)[^1];
        Assert.Equal(0, late.GetProperty("scopeId").GetInt64());
        Assert.Equal("Idle", late.GetProperty("phase").GetString());
        Assert.Equal("completed", late.GetProperty("lastStartedDispatchState").GetString());
        Assert.Equal(0, late.GetProperty("sourceDispatchId").GetInt64());
    }

    [Fact]
    public void ConcurrentRecords_HaveUniqueMonotonicIdsAndWholeLines()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("metadata.jsonl");
        using (var sink = NativeUiExceptionDiagnostics.Open(path, Invocation, Revision))
        {
            Parallel.For(0, 32, _ =>
            {
                var id = sink.BeginDispatch();
                sink.RecordException(NativeUiExceptionSource.DispatchCatch, () => null, id);
            });
        }
        var records = Read(path);
        Assert.Equal(34, records.Length);
        Assert.Equal(Enumerable.Range(1, 34), records.Select(r => r.GetProperty("sequence").GetInt32()));
        Assert.Equal(Enumerable.Range(1, 32).Select(i => (long)i),
            records.Where(r => r.GetProperty("source").GetString() == "DispatchCatch")
                .Select(r => r.GetProperty("sourceDispatchId").GetInt64()).Order());
        Assert.Equal("closed", records[^1].GetProperty("source").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Overflow_IsExplicitAndStopsAtBothRecordAndByteBudgets(bool longType)
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("metadata.jsonl");
        using var stream = new MemoryStream();
        using var fallback = new StringWriter();
        using var sink = NativeUiExceptionDiagnostics.Open(path, Invocation, Revision, createStream: _ => stream,
            fallbackWriter: fallback.WriteLine);
        Exception? error = longType
            ? new ExceptionWithAnIntentionallyLongMetadataTypeNameForTheDiagnosticByteBudgetContractWhichMustBeTruncatedWithoutReadingAnyContentOrPayloadBeyondTheAllowedName()
            : null;
        for (var i = 0; i < 4200; i++)
            sink.RecordException(NativeUiExceptionSource.WinUiUnhandled, () => error);
        var bytes = stream.ToArray();
        var records = ReadRecords(Encoding.UTF8.GetString(bytes));
        Assert.Equal("overflow", records[^1].GetProperty("source").GetString());
        Assert.Single(records, r => r.GetProperty("source").GetString() == "overflow");
        Assert.InRange(bytes.Length, 1, 2 * 1024 * 1024);
        Assert.All(Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => Assert.InRange(Encoding.UTF8.GetByteCount(line) + 1, 1, 1024));
        if (longType)
        {
            Assert.InRange(records.Length, 2, 4095); // Byte limit, before the record cap.
            Assert.Equal(128, records[1].GetProperty("exceptionType").GetString()!.Length);
        }
        else Assert.Equal(4096, records.Length); // Record limit, independently of bytes.
        sink.RecordException(NativeUiExceptionSource.WinUiUnhandled, () => throw new PoisonException());
        sink.Dispose();
        Assert.Equal(bytes, stream.ToArray());
        Assert.False(sink.WriteFailed);
        var fallbackRecords = ReadRecords(fallback.ToString());
        Assert.Equal(16, fallbackRecords.Length);
        Assert.Equal("fallbackOverflow", fallbackRecords[^1].GetProperty("source").GetString());
        Assert.All(fallbackRecords, r => Assert.Equal("overflow", r.GetProperty("fileCapture").GetString()));
    }

    [Theory]
    [InlineData("write", false)]
    [InlineData("flush", false)]
    [InlineData("dispose", false)]
    [InlineData("write", true)]
    [InlineData("flush", true)]
    [InlineData("dispose", true)]
    public void IoAndFailureReporterFaults_NeverReplaceTheSameOriginalException(string fault, bool reporterThrows)
    {
        using var directory = new TempDirectory();
        var stream = new FaultStream();
        var signals = 0;
        using var sink = NativeUiExceptionDiagnostics.Open(directory.Combine("metadata.jsonl"), Invocation, Revision,
            () => { signals++; if (reporterThrows) throw new PoisonException(); }, _ => stream);
        Assert.Equal(1, stream.FlushCount); // Ready went through the real flush boundary.
        stream.Fault = fault;
        var original = new PoisonException();
        var observed = Record.Exception((Action)(() =>
        {
            try { throw original; }
            catch (Exception caught)
            {
                sink.RecordException(NativeUiExceptionSource.WinUiUnhandled, () => caught);
                throw;
            }
            finally { sink.Dispose(); }
        }));
        Assert.Same(original, observed);
        Assert.True(sink.WriteFailed);
        Assert.Equal(1, signals);
        Assert.Equal(1, stream.DisposeCount);
    }

    [Fact]
    public void ExclusiveDestination_RejectsReuseWithoutOverwritingAndSeparatesInvocations()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("metadata.jsonl");
        var failures = 0;
        using var first = NativeUiExceptionDiagnostics.Open(path, Invocation, Revision);
        var before = ReadBytes(path);
        using var duplicate = NativeUiExceptionDiagnostics.Open(path, Invocation, Revision, () => failures++);
        Assert.True(duplicate.WriteFailed);
        Assert.Equal(1, failures);
        Assert.Equal(before, ReadBytes(path));
        const string secondInvocation = "ffeeddccbbaa99887766554433221100";
        Assert.True(Guid.TryParseExact(secondInvocation, "N", out _));
        var secondPath = directory.Combine("second.jsonl");
        using var second = NativeUiExceptionDiagnostics.Open(secondPath, secondInvocation, Revision);
        Assert.False(second.WriteFailed);
        Assert.Equal(secondInvocation, Assert.Single(Read(secondPath)).GetProperty("invocationId").GetString());
        first.Dispose();
        second.RecordException(NativeUiExceptionSource.WinUiUnhandled, () => null);
        Assert.Equal(2, Read(secondPath).Length);
        // Dispose releases our handle but keeps the curated artifact, not a live append target.
        using var owned = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(owned.CanWrite);
    }

    [Fact]
    public void InvalidDestinationOrIdentity_FailsExplicitlyWhileUnsetPathDoesNoFileIo()
    {
        using var directory = new TempDirectory();
        var signals = 0;
        using var disabled = NativeUiExceptionDiagnostics.Open(null, Invocation, Revision,
            () => signals++, _ => throw new PoisonException());
        disabled.RecordException(NativeUiExceptionSource.WinUiUnhandled, () => throw new PoisonException());
        Assert.False(disabled.WriteFailed);
        Assert.Equal(0, signals);
        using var invalid = NativeUiExceptionDiagnostics.Open(directory.Combine("invalid.jsonl"), "PRIVATE", Revision,
            () => signals++);
        Assert.True(invalid.WriteFailed);
        Assert.Equal(1, signals);
        Assert.False(File.Exists(directory.Combine("invalid.jsonl")));
        using var unavailable = NativeUiExceptionDiagnostics.Open(directory.Combine("missing", "metadata.jsonl"), Invocation, Revision,
            () => signals++);
        Assert.True(unavailable.WriteFailed);
        Assert.Equal(2, signals);
    }

    [Theory]
    [InlineData(false, "disabled")]
    [InlineData(true, "unavailable")]
    public void MissingFileCapture_ReportsContentFreeExceptionToFallback(bool requestFile, string outcome)
    {
        using var directory = new TempDirectory();
        using var fallback = new StringWriter();
        var signals = 0;
        var path = directory.Combine("missing", "PRIVATE.jsonl");
        using var sink = NativeUiExceptionDiagnostics.Open(requestFile ? path : null, Invocation, Revision,
            () => signals++, fallbackWriter: fallback.WriteLine);
        var reads = 0;
        sink.RecordException(NativeUiExceptionSource.WinUiUnhandled, () => { reads++; return new PoisonException(); });
        var record = Assert.Single(ReadRecords(fallback.ToString()));
        Assert.Equal(new[]
        {
            "exceptionAvailable", "exceptionReadFailed", "exceptionType", "fallbackSequence", "fileCapture",
            "processId", "reportedHResult", "reportedHResultHex", "schema", "source"
        }, record.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("WinUiUnhandled", record.GetProperty("source").GetString());
        Assert.Equal(outcome, record.GetProperty("fileCapture").GetString());
        Assert.Equal(1, record.GetProperty("schema").GetInt32());
        Assert.Equal(1, record.GetProperty("fallbackSequence").GetInt32());
        Assert.Equal(Environment.ProcessId, record.GetProperty("processId").GetInt32());
        Assert.True(record.GetProperty("exceptionAvailable").GetBoolean());
        Assert.False(record.GetProperty("exceptionReadFailed").GetBoolean());
        Assert.Equal("PoisonException", record.GetProperty("exceptionType").GetString());
        Assert.Equal(-2147467259, record.GetProperty("reportedHResult").GetInt32());
        Assert.Equal("0x80004005", record.GetProperty("reportedHResultHex").GetString());
        Assert.DoesNotContain("PRIVATE", fallback.ToString(), StringComparison.Ordinal);
        Assert.InRange(Encoding.UTF8.GetByteCount(fallback.ToString()), 1, 1024);
        Assert.Equal(1, reads);
        Assert.Equal(requestFile ? 1 : 0, signals);
        Assert.Equal(requestFile, sink.WriteFailed);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("flush")]
    public void FileFailure_ReportsTheExceptionWhoseWriteFailed(string fault)
    {
        using var directory = new TempDirectory();
        using var fallback = new StringWriter();
        var stream = new FaultStream();
        using var sink = NativeUiExceptionDiagnostics.Open(directory.Combine("metadata.jsonl"), Invocation, Revision,
            failureSignal: () => { }, createStream: _ => stream, fallbackWriter: fallback.WriteLine);
        stream.Fault = fault;
        sink.RecordException(NativeUiExceptionSource.ChatUnhandled, () => new PoisonException());
        var record = Assert.Single(ReadRecords(fallback.ToString()));
        Assert.Equal("failed", record.GetProperty("fileCapture").GetString());
        Assert.Equal("ChatUnhandled", record.GetProperty("source").GetString());
        Assert.Equal("PoisonException", record.GetProperty("exceptionType").GetString());
        Assert.True(sink.WriteFailed);
        Assert.DoesNotContain("PRIVATE", fallback.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledFallback_PreservesNullAndFailedProjectionWithoutInventingHResult(bool getterThrows)
    {
        using var fallback = new StringWriter();
        using var sink = NativeUiExceptionDiagnostics.Open(null, Invocation, Revision, fallbackWriter: fallback.WriteLine);
        sink.RecordException((NativeUiExceptionSource)int.MaxValue,
            () => getterThrows ? throw new PoisonException() : null);
        var record = Assert.Single(ReadRecords(fallback.ToString()));
        Assert.Equal("Unknown", record.GetProperty("source").GetString());
        Assert.False(record.GetProperty("exceptionAvailable").GetBoolean());
        Assert.Equal(getterThrows, record.GetProperty("exceptionReadFailed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("exceptionType").ValueKind);
        Assert.Equal(JsonValueKind.Null, record.GetProperty("reportedHResult").ValueKind);
        Assert.Equal(JsonValueKind.Null, record.GetProperty("reportedHResultHex").ValueKind);
        Assert.DoesNotContain("PRIVATE", fallback.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledFallback_IsBoundedAndDisposedSinkDoesNotReadLateException()
    {
        using var fallback = new StringWriter();
        using var sink = NativeUiExceptionDiagnostics.Open(null, Invocation, Revision, fallbackWriter: fallback.WriteLine);
        var reads = 0;
        for (var i = 0; i < 40; i++)
            sink.RecordException(NativeUiExceptionSource.WinUiUnhandled, () => { reads++; return new PoisonException(); });
        var records = ReadRecords(fallback.ToString());
        Assert.Equal(16, records.Length);
        Assert.Equal(16, reads);
        Assert.Equal(Enumerable.Range(1, 16), records.Select(r => r.GetProperty("fallbackSequence").GetInt32()));
        Assert.Equal("fallbackOverflow", records[^1].GetProperty("source").GetString());
        Assert.False(records[^1].GetProperty("exceptionAvailable").GetBoolean());
        Assert.All(records, r => Assert.Equal("disabled", r.GetProperty("fileCapture").GetString()));
        Assert.All(fallback.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries),
            line => Assert.InRange(Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length, 1, 1024));
        Assert.InRange(Encoding.UTF8.GetByteCount(fallback.ToString()), 1, 16 * 1024);
        Assert.DoesNotContain("PRIVATE", fallback.ToString(), StringComparison.Ordinal);

        using var lateFallback = new StringWriter();
        var closed = NativeUiExceptionDiagnostics.Open(null, Invocation, Revision, fallbackWriter: lateFallback.WriteLine);
        closed.Dispose();
        closed.RecordException(NativeUiExceptionSource.WinUiUnhandled, () => { reads++; return new PoisonException(); });
        Assert.Equal(16, reads);
        Assert.Equal(string.Empty, lateFallback.ToString());
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("unavailable")]
    [InlineData("failed")]
    public void FallbackWriterFailure_NeverReplacesOriginalExceptionOrItsThrowSite(string mode)
    {
        using var directory = new TempDirectory();
        var stream = new FaultStream();
        var writes = 0;
        using var sink = NativeUiExceptionDiagnostics.Open(mode == "disabled" ? null : directory.Combine("metadata.jsonl"),
            Invocation, Revision, failureSignal: () => throw new IOException("PRIVATE failure reporter"),
            createStream: _ => mode == "unavailable" ? throw new IOException("PRIVATE open") : stream,
            fallbackWriter: _ => { writes++; throw new IOException("PRIVATE fallback writer"); });
        stream.Fault = "write";
        var original = new InvalidOperationException("PRIVATE original");
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => ThrowOriginal(original)));
        var primary = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(original);
        var observed = Record.Exception((Action)(() =>
        {
            try { primary.Throw(); }
            catch (Exception caught)
            {
                sink.RecordException(NativeUiExceptionSource.WinUiUnhandled, () => caught);
                throw;
            }
            finally { sink.Dispose(); }
        }));
        Assert.Same(original, observed);
        Assert.Contains(nameof(ThrowOriginal), original.StackTrace!, StringComparison.Ordinal);
        Assert.Equal(1, writes);
        Assert.Equal(mode != "disabled", sink.WriteFailed);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowOriginal(Exception original) => throw original;

    [Fact]
    public void HarnessHooks_KeepPermanentHandlerAndSameExceptionPropagation_WithoutContentReporters()
    {
        // Retire this wiring guard when the in-process WinUI harness has a native
        // injected-exception test. The behavioral sink tests above are not UI proof.
        var root = ProductionSourceFiles.FindRepoRoot();
        var app = File.ReadAllText(Path.Combine(root, "tests/OpenClaw.Tray.UITests/TestApp.cs"));
        var ui = File.ReadAllText(Path.Combine(root, "tests/OpenClaw.Tray.UITests/UIThreadFixture.cs"));
        var chat = File.ReadAllText(Path.Combine(root, "tests/OpenClaw.Tray.UITests/ReactorChatLayoutProofTests.cs"));
        var wrapper = File.ReadAllText(Path.Combine(root, "scripts/Invoke-CiTest.ps1"));
        Assert.Contains("UnhandledException += CreateExceptionReporter(diagnostics, NativeUiExceptionSource.WinUiUnhandled)", app);
        Assert.Contains("diagnostics.RecordException(source, () => args.Exception)", app);
        Assert.DoesNotContain("args.Handled", app);
        Assert.DoesNotContain("args.Message", app);
        Assert.DoesNotContain("args.Message", chat);
        Assert.Contains("TestApp.CreateExceptionReporter(ui.Diagnostics, NativeUiExceptionSource.ChatUnhandled)", chat);
        Assert.Contains("Diagnostics.RecordException(NativeUiExceptionSource.DispatchCatch, () => ex, diagnosticDispatch);", ui);
        Assert.Contains("tcs.SetException(ex);", ui);
        Assert.Contains("Join-Path $ResultsDirectory 'native-ui-exceptions.jsonl'", wrapper);
        Assert.Contains("$env:OPENCLAW_UI_EXCEPTION_INVOCATION_ID = $invocation.id", wrapper);
        Assert.Contains("$env:OPENCLAW_UI_EXCEPTION_METADATA_PATH = $previousMetadataPath", wrapper);
        Assert.Contains("$env:OPENCLAW_UI_EXCEPTION_INVOCATION_ID = $previousMetadataInvocation", wrapper);
    }

    // Readers must share the already-open writer on Windows. The sink itself never
    // shares write access, so another diagnostic writer still cannot join it.
    private static byte[] ReadBytes(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var bytes = new MemoryStream();
        file.CopyTo(bytes);
        return bytes.ToArray();
    }
    private static string ReadText(string path) => Encoding.UTF8.GetString(ReadBytes(path));
    private static JsonElement[] Read(string path) => ReadRecords(ReadText(path));
    private static JsonElement[] ReadRecords(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }).ToArray();

    private sealed class PoisonException : Exception
    {
        public PoisonException() : base("PRIVATE message", new Exception("PRIVATE inner"))
        {
            HResult = unchecked((int)0x80004005);
            Source = "PRIVATE source";
            HelpLink = "https://private.invalid/secret";
        }
        public override string Message => throw new InvalidOperationException("Message must not be read");
        public override string? StackTrace => throw new InvalidOperationException("StackTrace must not be read");
        public override IDictionary Data => throw new InvalidOperationException("Data must not be read");
        public override string ToString() => throw new InvalidOperationException("ToString must not be read");
    }

    private sealed class ExceptionWithAnIntentionallyLongMetadataTypeNameForTheDiagnosticByteBudgetContractWhichMustBeTruncatedWithoutReadingAnyContentOrPayloadBeyondTheAllowedName : Exception { }

    private sealed class GenericException<T> : Exception { }

    private sealed class FaultStream : MemoryStream
    {
        public string? Fault { get; set; }
        public int FlushCount { get; private set; }
        public int DisposeCount { get; private set; }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Fault == "write") throw new IOException("PRIVATE write failure");
            base.Write(buffer);
        }
        public override void Flush()
        {
            if (Fault == "flush") throw new IOException("PRIVATE flush failure");
            FlushCount++;
            base.Flush();
        }
        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            base.Dispose(disposing);
            if (Fault == "dispose") throw new IOException("PRIVATE dispose failure");
        }
    }
}
