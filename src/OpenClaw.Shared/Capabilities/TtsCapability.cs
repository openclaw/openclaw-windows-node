using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Capabilities;

public sealed class TtsCapability : NodeCapabilityBase
{
    public const string SpeakCommand = "tts.speak";
    public const string WindowsProvider = "windows";
    public const string ElevenLabsProvider = "elevenlabs";
    public const int MaxTextLength = 5000;

    private static readonly string[] _commands = [SpeakCommand];

    public override string Category => "tts";
    public override IReadOnlyList<string> Commands => _commands;

    public event Func<TtsSpeakArgs, CancellationToken, Task<TtsSpeakResult>>? SpeakRequested;

    public TtsCapability(IOpenClawLogger logger) : base(logger)
    {
    }

    public static string ResolveProvider(string? requestedProvider, string? configuredProvider)
    {
        var provider = string.IsNullOrWhiteSpace(requestedProvider)
            ? configuredProvider
            : requestedProvider;

        return string.IsNullOrWhiteSpace(provider)
            ? WindowsProvider
            : provider.Trim().ToLowerInvariant();
    }

    public override Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
        => ExecuteAsync(request, CancellationToken.None);

    public override async Task<NodeInvokeResponse> ExecuteAsync(
        NodeInvokeRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Command, SpeakCommand, StringComparison.Ordinal))
            return Error($"Unknown command: {request.Command}");

        var text = GetStringArg(request.Args, "text")?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return Error("Missing required text");
        if (text.Length > MaxTextLength)
            return Error($"TTS text exceeds {MaxTextLength} characters.");

        if (SpeakRequested == null)
            return Error("TTS speak not available");

        var args = new TtsSpeakArgs
        {
            Text = text,
            Provider = NormalizeOptional(GetStringArg(request.Args, "provider")),
            VoiceId = NormalizeOptional(GetStringArg(request.Args, "voiceId")),
            Model = NormalizeOptional(GetStringArg(request.Args, "model")),
            Interrupt = GetBoolArg(request.Args, "interrupt")
        };

        Logger.Info($"tts.speak: provider={args.Provider ?? "(default)"}, chars={args.Text.Length}, interrupt={args.Interrupt}");

        try
        {
            var result = await SpeakRequested(args, cancellationToken).ConfigureAwait(false);
            return Success(new
            {
                spoken = result.Spoken,
                provider = result.Provider,
                contentType = result.ContentType,
                durationMs = result.DurationMs
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Error("Speak canceled");
        }
        catch (Exception ex)
        {
            Logger.Error("TTS speak failed", ex);
            return Error($"Speak failed: {ex.Message}");
        }
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class TtsSpeakArgs
{
    public string Text { get; set; } = "";
    public string? Provider { get; set; }
    public string? VoiceId { get; set; }
    public string? Model { get; set; }
    public bool Interrupt { get; set; }
}

public sealed class TtsSpeakResult
{
    public bool Spoken { get; set; } = true;
    public string Provider { get; set; } = TtsCapability.WindowsProvider;
    public string? ContentType { get; set; }
    public int? DurationMs { get; set; }
}
