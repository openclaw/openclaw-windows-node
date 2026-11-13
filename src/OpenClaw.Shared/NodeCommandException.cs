namespace OpenClaw.Shared;

/// <summary>A command-owned failure that execution infrastructure must preserve for its adapter.</summary>
public abstract class NodeCommandException(string message) : Exception(message);
