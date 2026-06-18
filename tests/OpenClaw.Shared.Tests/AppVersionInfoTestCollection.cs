using Xunit;

namespace OpenClaw.Shared.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppVersionInfoTestCollection
{
    public const string Name = "AppVersionInfo";
}
