namespace OpenClaw.TestSupport.Ui;

public sealed class ContractAutomationException(string message, Exception innerException)
    : Exception(message, innerException);

public sealed class ContractCaptureException(string message, Exception innerException)
    : Exception(message, innerException);

public sealed class ContractAssertionException(string message) : Exception(message);
