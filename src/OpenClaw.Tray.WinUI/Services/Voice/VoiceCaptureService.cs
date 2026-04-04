using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using WinRT;
using Windows.Devices.Enumeration;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Media.Render;

namespace OpenClawTray.Services.Voice;

public sealed class VoiceAudioFrameEventArgs : EventArgs
{
    public VoiceAudioFrameEventArgs(
        string? deviceId,
        string? deviceName,
        DateTime utcTimestamp,
        int sampleRateHz,
        int channelCount,
        byte[] data,
        float peakLevel)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        UtcTimestamp = utcTimestamp;
        SampleRateHz = sampleRateHz;
        ChannelCount = channelCount;
        Data = data;
        PeakLevel = peakLevel;
    }

    public string? DeviceId { get; }
    public string? DeviceName { get; }
    public DateTime UtcTimestamp { get; }
    public int SampleRateHz { get; }
    public int ChannelCount { get; }
    public byte[] Data { get; }
    public float PeakLevel { get; }
}

public sealed class VoiceCaptureSignalEventArgs : EventArgs
{
    public VoiceCaptureSignalEventArgs(
        string? deviceId,
        string? deviceName,
        DateTime utcTimestamp,
        float peakLevel)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        UtcTimestamp = utcTimestamp;
        PeakLevel = peakLevel;
    }

    public string? DeviceId { get; }
    public string? DeviceName { get; }
    public DateTime UtcTimestamp { get; }
    public float PeakLevel { get; }
}

public sealed class VoiceCaptureService : IAsyncDisposable
{
    private const float DefaultSignalThreshold = 0.015f;

    private readonly IOpenClawLogger _logger;
    private readonly object _gate = new();

    private AudioGraph? _audioGraph;
    private AudioDeviceInputNode? _deviceInputNode;
    private AudioFrameOutputNode? _frameOutputNode;
    private DeviceInformation? _activeCaptureDevice;
    private int _sampleRateHz;
    private int _channelCount;
    private bool _captureReady;
    private TaskCompletionSource<bool> _captureReadyTcs = CreateCaptureReadyTcs();

    public VoiceCaptureService(IOpenClawLogger logger)
    {
        _logger = logger;
    }

    public event EventHandler<VoiceAudioFrameEventArgs>? FrameCaptured;
    public event EventHandler<VoiceCaptureSignalEventArgs>? SignalDetected;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _audioGraph != null;
            }
        }
    }

    public string? ActiveDeviceId
    {
        get
        {
            lock (_gate)
            {
                return _activeCaptureDevice?.Id;
            }
        }
    }

    public string? ActiveDeviceName
    {
        get
        {
            lock (_gate)
            {
                return _activeCaptureDevice?.Name;
            }
        }
    }

    public async Task StartAsync(VoiceSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await StopAsync();
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _captureReady = false;
            _captureReadyTcs = CreateCaptureReadyTcs();
        }

        AudioGraph? audioGraph = null;
        AudioDeviceInputNode? deviceInputNode = null;
        AudioFrameOutputNode? frameOutputNode = null;

        try
        {
            var graphSettings = new AudioGraphSettings(AudioRenderCategory.Speech)
            {
                QuantumSizeSelectionMode = QuantumSizeSelectionMode.ClosestToDesired,
                DesiredSamplesPerQuantum = (int)ResolveDesiredSamplesPerQuantum(settings.SampleRateHz, settings.CaptureChunkMs)
            };

            var graphCreation = await AudioGraph.CreateAsync(graphSettings);
            if (graphCreation.Status != AudioGraphCreationStatus.Success || graphCreation.Graph == null)
            {
                throw new InvalidOperationException($"AudioGraph unavailable: {graphCreation.Status}");
            }

            audioGraph = graphCreation.Graph;
            var captureDevice = await ResolveCaptureDeviceAsync(settings.InputDeviceId);
            var inputCreation = await audioGraph.CreateDeviceInputNodeAsync(
                MediaCategory.Speech,
                audioGraph.EncodingProperties,
                captureDevice);

            if (inputCreation.Status != AudioDeviceNodeCreationStatus.Success || inputCreation.DeviceInputNode == null)
            {
                throw new InvalidOperationException($"Audio input node unavailable: {inputCreation.Status}");
            }

            deviceInputNode = inputCreation.DeviceInputNode;
            frameOutputNode = audioGraph.CreateFrameOutputNode(audioGraph.EncodingProperties);
            deviceInputNode.AddOutgoingConnection(frameOutputNode);

            audioGraph.QuantumStarted += OnAudioGraphQuantumStarted;
            audioGraph.UnrecoverableErrorOccurred += OnAudioGraphUnrecoverableErrorOccurred;

            lock (_gate)
            {
                _audioGraph = audioGraph;
                _deviceInputNode = deviceInputNode;
                _frameOutputNode = frameOutputNode;
                _activeCaptureDevice = captureDevice;
                _sampleRateHz = (int)audioGraph.EncodingProperties.SampleRate;
                _channelCount = (int)audioGraph.EncodingProperties.ChannelCount;
            }

            frameOutputNode.Start();
            deviceInputNode.Start();
            audioGraph.Start();

            audioGraph = null;
            deviceInputNode = null;
            frameOutputNode = null;

            _logger.Info(
                $"Voice capture graph started on {(captureDevice?.Name ?? "system default microphone")} ({captureDevice?.Id ?? "default"})");
        }
        finally
        {
            if (frameOutputNode != null)
            {
                try { frameOutputNode.Stop(); } catch { }
                try { frameOutputNode.Dispose(); } catch { }
            }

            if (deviceInputNode != null)
            {
                try { deviceInputNode.Stop(); } catch { }
                try { deviceInputNode.Dispose(); } catch { }
            }

            if (audioGraph != null)
            {
                audioGraph.QuantumStarted -= OnAudioGraphQuantumStarted;
                audioGraph.UnrecoverableErrorOccurred -= OnAudioGraphUnrecoverableErrorOccurred;
                try { audioGraph.Stop(); } catch { }
                try { audioGraph.Dispose(); } catch { }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(StopAsync());
    }

    public async Task StopAsync()
    {
        AudioGraph? audioGraph;
        AudioDeviceInputNode? deviceInputNode;
        AudioFrameOutputNode? frameOutputNode;
        string? deviceName;

        lock (_gate)
        {
            audioGraph = _audioGraph;
            _audioGraph = null;
            deviceInputNode = _deviceInputNode;
            _deviceInputNode = null;
            frameOutputNode = _frameOutputNode;
            _frameOutputNode = null;
            deviceName = _activeCaptureDevice?.Name;
            _activeCaptureDevice = null;
            _sampleRateHz = 0;
            _channelCount = 0;
        }

        if (audioGraph == null && deviceInputNode == null && frameOutputNode == null)
        {
            return;
        }

        if (audioGraph != null)
        {
            audioGraph.QuantumStarted -= OnAudioGraphQuantumStarted;
            audioGraph.UnrecoverableErrorOccurred -= OnAudioGraphUnrecoverableErrorOccurred;
        }

        try { frameOutputNode?.Stop(); } catch { }
        try { deviceInputNode?.Stop(); } catch { }
        try { audioGraph?.Stop(); } catch { }

        try { frameOutputNode?.Dispose(); } catch { }
        try { deviceInputNode?.Dispose(); } catch { }
        try { audioGraph?.Dispose(); } catch { }

        await Task.CompletedTask;
        _logger.Info($"Voice capture graph stopped{(string.IsNullOrWhiteSpace(deviceName) ? string.Empty : $" ({deviceName})")}");
    }

    public Task WaitForCaptureReadyAsync(CancellationToken cancellationToken)
    {
        Task readinessTask;

        lock (_gate)
        {
            readinessTask = _captureReady ? Task.CompletedTask : _captureReadyTcs.Task;
        }

        return readinessTask.WaitAsync(cancellationToken);
    }

    internal static uint ResolveDesiredSamplesPerQuantum(int sampleRateHz, int chunkMs)
    {
        return VoiceCaptureMath.ResolveDesiredSamplesPerQuantum(sampleRateHz, chunkMs);
    }

    internal static bool HasAudibleSignal(float peakLevel, float threshold = DefaultSignalThreshold)
    {
        return VoiceCaptureMath.HasAudibleSignal(peakLevel, threshold);
    }

    internal static float ComputePeakLevel(byte[] data)
    {
        return VoiceCaptureMath.ComputePeakLevel(data);
    }

    private async Task<DeviceInformation> ResolveCaptureDeviceAsync(string? preferredInputDeviceId)
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
        if (devices.Count == 0)
        {
            throw new InvalidOperationException("No audio capture devices are available.");
        }

        if (!string.IsNullOrWhiteSpace(preferredInputDeviceId))
        {
            var selected = devices.FirstOrDefault(device =>
                string.Equals(device.Id, preferredInputDeviceId, StringComparison.Ordinal));

            if (selected != null)
            {
                return selected;
            }

            throw new InvalidOperationException($"Selected input device '{preferredInputDeviceId}' was not found.");
        }

        var defaultId = MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default);
        var defaultDevice = devices.FirstOrDefault(device =>
            string.Equals(device.Id, defaultId, StringComparison.Ordinal));

        return defaultDevice ?? devices[0];
    }

    private void OnAudioGraphUnrecoverableErrorOccurred(AudioGraph sender, AudioGraphUnrecoverableErrorOccurredEventArgs args)
    {
        _logger.Warn($"Voice capture graph unrecoverable error: {args.Error}");
    }

    private void OnAudioGraphQuantumStarted(AudioGraph sender, object args)
    {
        try
        {
            AudioFrameOutputNode? frameOutputNode;
            string? deviceId;
            string? deviceName;
            int sampleRateHz;
            int channelCount;

            lock (_gate)
            {
                frameOutputNode = _frameOutputNode;
                deviceId = _activeCaptureDevice?.Id;
                deviceName = _activeCaptureDevice?.Name;
                sampleRateHz = _sampleRateHz;
                channelCount = _channelCount;
            }

            if (frameOutputNode == null)
            {
                return;
            }

            using var frame = frameOutputNode.GetFrame();
            if (!TryCopyAudioFrame(frame, out var bytes) || bytes.Length == 0)
            {
                return;
            }

            TaskCompletionSource<bool>? captureReadyTcs = null;

            lock (_gate)
            {
                if (!_captureReady)
                {
                    _captureReady = true;
                    captureReadyTcs = _captureReadyTcs;
                }
            }

            captureReadyTcs?.TrySetResult(true);

            var utcNow = DateTime.UtcNow;
            var peak = ComputePeakLevel(bytes);
            FrameCaptured?.Invoke(
                this,
                new VoiceAudioFrameEventArgs(
                    deviceId,
                    deviceName,
                    utcNow,
                    sampleRateHz,
                    channelCount,
                    bytes,
                    peak));

            if (HasAudibleSignal(peak))
            {
                SignalDetected?.Invoke(
                    this,
                    new VoiceCaptureSignalEventArgs(
                        deviceId,
                        deviceName,
                        utcNow,
                        peak));
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Voice capture quantum processing failed: {ex.Message}");
        }
    }

    private static bool TryCopyAudioFrame(AudioFrame frame, out byte[] bytes)
    {
        bytes = [];

        using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        var access = reference.As<IMemoryBufferByteAccess>();
        access.GetBuffer(out var data, out var capacity);

        if (data == IntPtr.Zero || capacity == 0)
        {
            return false;
        }

        bytes = new byte[capacity];
        Marshal.Copy(data, bytes, 0, (int)capacity);
        return true;
    }

    private static TaskCompletionSource<bool> CreateCaptureReadyTcs()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        void GetBuffer(out IntPtr buffer, out uint capacity);
    }
}
