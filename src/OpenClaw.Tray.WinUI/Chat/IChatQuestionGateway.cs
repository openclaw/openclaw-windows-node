using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

public interface IChatQuestionGateway
{
    bool IsConnected { get; }
    event EventHandler<ConnectionStatus>? StatusChanged;
    event EventHandler<GatewayQuestionEvent>? QuestionReceived;
    event EventHandler? QuestionProtocolError;
    Task<GatewayQuestionList> ListQuestionsAsync();
    Task<GatewayQuestionRequest> GetQuestionAsync(string id);
    Task<string> ResolveQuestionAsync(string id, IReadOnlyDictionary<string, string[]>? answers);
}
