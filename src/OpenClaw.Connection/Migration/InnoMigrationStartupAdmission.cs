using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using OpenClaw.Shared;

namespace OpenClaw.Connection.Migration;

public enum InnoMigrationStartupDecision
{
    Continue,
    Completed,
    CheckFailed
}

/// <summary>
/// Decides, without presenting UI, whether an unpackaged Inno install may start after a Store
/// migration. Acquires the runtime lease before reading the completion receipt. Normal startup
/// shows the guidance in a dialog; headless setup writes it to stderr.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InnoMigrationStartupAdmission(MigrationBinding binding, IOpenClawLogger logger)
{
    /// <param name="runtimeLease">
    /// Retained by the caller until process exit, whatever the decision. Null when the lock
    /// could not be acquired.
    /// </param>
    public InnoMigrationStartupDecision Evaluate(out IDisposable? runtimeLease)
    {
        runtimeLease = null;
        // Acquisition must precede the receipt check, including when no receipt exists yet.
        // The caller retains this handle until process exit, even if startup or shutdown fails.
        // An unavailable lock says nothing about whether migration completed, so it must not
        // block startup on its own. InnoSourceActivityVerifier still detects this process by
        // image path, so the Store side keeps its interlock without a lease.
        try
        {
            runtimeLease = MigrationOperationLock.AcquireRuntime(binding);
        }
        catch (IOException ex) when (MigrationOperationLock.IsBusy(ex))
        {
            logger.Error("Migration state is locked by an in-progress migration; Inno startup blocked.");
            return InnoMigrationStartupDecision.CheckFailed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidDataException or ArgumentException or NotSupportedException or
                                   InvalidOperationException or System.Security.SecurityException)
        {
            // An unreachable lock says nothing about whether a migration completed. Only an
            // exclusive holder does, and that case is handled above. The receipt check below
            // stays authoritative and fails closed on its own, so continuing here loses
            // same-session coordination rather than safety. Refusing to launch would lock a
            // user who never migrated out of their app over an inaccessible zero-byte file.
            logger.Warn($"Migration runtime lock unavailable ({ex.GetType().Name}); continuing without it.");
        }

        var path = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName,
            MigrationRecordCodec.CompletionFileName);
        try
        {
            MigrationRecordCodec.ReadCompletion(path, binding, DateTime.UtcNow);
            logger.Info("Completed Store migration blocks normal Inno startup.");
            return InnoMigrationStartupDecision.Completed;
        }
        catch (FileNotFoundException) { return InnoMigrationStartupDecision.Continue; }
        catch (DirectoryNotFoundException) { return InnoMigrationStartupDecision.Continue; }
        catch (Exception ex) when (ex is InvalidDataException or CryptographicException or
                                   EndOfStreamException or ArgumentException or FormatException or
                                   DecoderFallbackException)
        {
            logger.Warn($"Migration completion receipt rejected ({ex.GetType().Name}).");
            return InnoMigrationStartupDecision.Continue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Error($"Migration completion check unavailable ({ex.GetType().Name}); Inno startup blocked.");
            return InnoMigrationStartupDecision.CheckFailed;
        }
    }
}
