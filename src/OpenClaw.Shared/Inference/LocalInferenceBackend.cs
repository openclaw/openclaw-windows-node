namespace OpenClaw.Shared.Inference;

public sealed record LocalInferenceModel(
    string Name,
    long? Size,
    DateTimeOffset? ModifiedAt,
    string? Family,
    string? ParameterSize,
    string? Quantization,
    int? ContextWindow,
    IReadOnlyList<string>? Capabilities,
    bool Loaded);

public sealed record LocalInferenceChatRequest(
    string Model,
    string Prompt,
    string? System,
    double? Temperature,
    int MaxTokens,
    int TimeoutMs);

public sealed record LocalInferenceUsage(int? PromptTokens, int? CompletionTokens);

public sealed record LocalInferenceTimings(double? LoadMs, double? TotalMs);

public sealed record LocalInferenceChatResult(
    string Provider,
    string Model,
    string Response,
    LocalInferenceUsage? Usage,
    LocalInferenceTimings? Timings);

public interface ILocalInferenceBackend
{
    string ProviderId { get; }

    Task<IReadOnlyList<LocalInferenceModel>> ListModelsAsync(
        CancellationToken cancellationToken = default);

    Task<LocalInferenceChatResult> ChatAsync(
        LocalInferenceChatRequest request,
        CancellationToken cancellationToken = default);
}
