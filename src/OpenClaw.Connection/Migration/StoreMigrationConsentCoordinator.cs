namespace OpenClaw.Connection.Migration;

public enum StoreMigrationConsentState
{
    Declined,
    WaitingForInnoExit,
    ReadyForAdoption,
    Blocked
}

public sealed record StoreMigrationConsentDecision(StoreMigrationConsentState State);

public interface IInnoInstanceProbe
{
    bool IsRunning();
}

/// <summary>
/// Converts Store-side consent into a safe handoff to a future adoption owner.
/// It neither acquires migration ownership nor changes source state.
/// </summary>
public sealed class StoreMigrationConsentCoordinator(IInnoInstanceProbe innoInstance)
{
    public StoreMigrationConsentDecision Begin(StoreMigrationStartupDecision admission, bool consented)
    {
        if (admission.State != StoreMigrationStartupState.ConsentRequired ||
            admission.Installation is null)
            return new(StoreMigrationConsentState.Blocked);

        return consented
            ? CheckInnoInstance()
            : new(StoreMigrationConsentState.Declined);
    }

    public StoreMigrationConsentDecision Retry() => CheckInnoInstance();

    private StoreMigrationConsentDecision CheckInnoInstance() =>
        new(innoInstance.IsRunning()
            ? StoreMigrationConsentState.WaitingForInnoExit
            : StoreMigrationConsentState.ReadyForAdoption);
}
