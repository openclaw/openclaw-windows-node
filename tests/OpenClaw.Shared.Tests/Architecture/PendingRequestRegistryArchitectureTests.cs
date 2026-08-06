using System.Collections.Concurrent;
using System.Reflection;

namespace OpenClaw.Shared.Tests.Architecture;

public sealed class PendingRequestRegistryArchitectureTests
{
    [Fact]
    public void OpenClawGatewayClient_DelegatesPendingBookkeepingToRegistry()
    {
        var fields = typeof(OpenClawGatewayClient).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);

        var registryField = Assert.Single(
            fields,
            field => field.FieldType == typeof(PendingRequestRegistry));
        Assert.Equal("_pendingRequests", registryField.Name);

        var prohibitedFields = new[]
        {
            "_pendingRequestMethods",
            "_pendingChatSendRequests",
            "_pendingWizardResponses",
            "_pendingApprovalResolves",
            "_pendingRequestLock",
            "_pendingChatSendLock",
        };
        Assert.DoesNotContain(fields, field => prohibitedFields.Contains(field.Name));
        Assert.DoesNotContain(fields, field => IsLegacyPendingStore(field.FieldType));
    }

    private static bool IsLegacyPendingStore(Type fieldType)
    {
        if (!fieldType.IsGenericType)
            return false;

        var definition = fieldType.GetGenericTypeDefinition();
        if (definition != typeof(Dictionary<,>) &&
            definition != typeof(ConcurrentDictionary<,>))
        {
            return false;
        }

        var arguments = fieldType.GetGenericArguments();
        return arguments[0] == typeof(string) &&
               (arguments[1] == typeof(string) ||
                arguments[1].IsGenericType &&
                arguments[1].GetGenericTypeDefinition() == typeof(TaskCompletionSource<>));
    }
}
