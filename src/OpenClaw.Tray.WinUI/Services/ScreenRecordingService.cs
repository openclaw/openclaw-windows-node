using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using WinRT;

namespace OpenClawTray.Services;

/// <summary>
/// Records the screen using Windows.Graphics.Capture and encodes to MP4 via MediaTranscoder.
/// </summary>
internal sealed class ScreenRecordingService : IDisposable
{
    private readonly IOpenClawLogger _logger;
    private readonly ConcurrentDictionary<string, ActiveSession> _sessions = new();

    private const int MaxFps        = 60;
    private const int MinFps        = 1;
    private const int MinDurationMs = 250;
    private const int MaxDurationMs = 60_000;
    private const int PoolBuffers   = 2;

    // BGRA frame buffer safety cap: ~500 MB across all queued frames.
    // At 1080p (8 MB/frame) this allows ~62 frames; at 720p (~4 MB) ~125 frames.
    // Frames beyond this limit are dropped to prevent OOM on long/high-fps recordings.
    private const long MaxFrameBufferBytes = 500L * 1024 * 1024;

    public ScreenRecordingService(IOpenClawLogger logger)
    {
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<ScreenRecordResult> RecordAsync(ScreenRecordArgs args)
    {
        var durationMs  = Math.Clamp(args.DurationMs, MinDurationMs, MaxDurationMs);
        var fps         = Math.Clamp(args.Fps, MinFps, MaxFps);
        var screenIndex = args.ScreenIndex;

        _logger.Info($"[ScreenRecording] duration={durationMs}ms fps={fps} screen={screenIndex}");

        var item    = CreateCaptureItem(screenIndex);
        var width   = item.Size.Width;
        var height  = item.Size.Height;
        var d3d     = CreateDirect3DDevice();

        Direct3D11CaptureFramePool? pool    = null;
        GraphicsCaptureSession?     session = null;
        var latestFrame = (Direct3D11CaptureFrame?)null;
        using var ready = new SemaphoreSlim(0, 1);
        var frames = new List<byte[]>();
        var frameBytes = (long)width * height * 4; // BGRA bytes per frame

        try
        {
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                d3d,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                PoolBuffers,
                new global::Windows.Graphics.SizeInt32 { Width = width, Height = height });

            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;

            pool.FrameArrived += (p, _) =>
            {
                var f = p.TryGetNextFrame();
                if (f == null) return;
                Interlocked.Exchange(ref latestFrame, f)?.Dispose();
                try { ready.Release(); } catch { /* already signaled */ }
            };

            session.StartCapture();

            var intervalMs  = 1000 / fps;
            var deadline    = DateTime.UtcNow.AddMilliseconds(durationMs);
            var nextCapture = DateTime.UtcNow;

            while (DateTime.UtcNow < deadline)
            {
                var waitMs = (int)(nextCapture - DateTime.UtcNow).TotalMilliseconds;
                if (waitMs > 0)
                    await Task.Delay(waitMs);

                if (!await ready.WaitAsync(intervalMs * 2))
                    continue;

                var frame = Interlocked.Exchange(ref latestFrame, null);
                if (frame == null) continue;

                using (frame)
                {
                    if (frames.Count * frameBytes >= MaxFrameBufferBytes)
                    {
                        _logger.Warn($"[ScreenRecording] Frame buffer cap reached ({MaxFrameBufferBytes / 1024 / 1024} MB), stopping early.");
                        break;
                    }

                    try
                    {
                        var bmp = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
                        frames.Add(ExtractBitmapBytes(bmp));
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"[ScreenRecording] Frame skipped: {ex.Message}");
                    }
                }

                nextCapture = nextCapture.AddMilliseconds(intervalMs);
            }
        }
        finally
        {
            session?.Dispose();
            pool?.Dispose();
            Interlocked.Exchange(ref latestFrame, null)?.Dispose();
        }

        _logger.Info($"[ScreenRecording] Captured {frames.Count} frames, encoding...");

        var base64 = await EncodeToMp4Async(frames, width, height, fps);
        var filePath = SaveToTempFile(base64);

        return new ScreenRecordResult
        {
            Format      = "mp4",
            Base64      = base64,
            FilePath    = filePath,
            DurationMs  = durationMs,
            Fps         = fps,
            ScreenIndex = screenIndex,
            Width       = width,
            Height      = height,
            HasAudio    = false,
        };
    }

    public Task<string> StartAsync(ScreenRecordStartArgs args)
    {
        var fps         = Math.Clamp(args.Fps, MinFps, MaxFps);
        var screenIndex = args.ScreenIndex;

        _logger.Info($"[ScreenRecording] start fps={fps} screen={screenIndex}");

        var item   = CreateCaptureItem(screenIndex);
        var width  = item.Size.Width;
        var height = item.Size.Height;
        var d3d    = CreateDirect3DDevice();

        var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            d3d,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            PoolBuffers,
            new global::Windows.Graphics.SizeInt32 { Width = width, Height = height });

        var captureSession = pool.CreateCaptureSession(item);
        captureSession.IsCursorCaptureEnabled = false;

        var session = new ActiveSession(screenIndex, fps, width, height, pool, captureSession, _logger);
        _sessions[session.Id] = session;

        _logger.Info($"[ScreenRecording] started session {session.Id}");
        return Task.FromResult(session.Id);
    }

    public async Task<ScreenRecordResult> StopAsync(string recordingId)
    {
        if (!_sessions.TryRemove(recordingId, out var session))
            throw new KeyNotFoundException($"Recording session '{recordingId}' not found");

        _logger.Info($"[ScreenRecording] stopping session {recordingId}...");

        List<byte[]> frames;
        int width, height, fps, screenIndex, durationMs;
        using (session)
        {
            (frames, durationMs) = await session.StopAsync();
            width       = session.Width;
            height      = session.Height;
            fps         = session.Fps;
            screenIndex = session.ScreenIndex;
        }

        _logger.Info($"[ScreenRecording] session {recordingId}: {frames.Count} frames, encoding...");
        var base64 = await EncodeToMp4Async(frames, width, height, fps);
        var filePath = SaveToTempFile(base64);

        return new ScreenRecordResult
        {
            Format      = "mp4",
            Base64      = base64,
            FilePath    = filePath,
            DurationMs  = durationMs,
            Fps         = fps,
            ScreenIndex = screenIndex,
            Width       = width,
            Height      = height,
            HasAudio    = false,
        };
    }

    public void Dispose()
    {
        foreach (var kv in _sessions)
        {
            if (_sessions.TryRemove(kv.Key, out var s))
                try { s.Dispose(); } catch { }
        }
    }

    // ── Temp file ─────────────────────────────────────────────────────────────

    private string SaveToTempFile(string base64)
    {
        var dir = Path.Combine(Path.GetTempPath(), "openclaw");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"openclaw-screen-record-{Guid.NewGuid()}.mp4");
        File.WriteAllBytes(path, Convert.FromBase64String(base64));
        _logger.Info($"[ScreenRecording] Saved to {path}");
        return path;
    }

    // ── Encoding ──────────────────────────────────────────────────────────────

    private static async Task<string> EncodeToMp4Async(
        List<byte[]> frames, int width, int height, int fps)
    {
        if (frames.Count == 0)
            throw new InvalidOperationException("No frames to encode");

        var encWidth  = (uint)(width  & ~1);
        var encHeight = (uint)(height & ~1);
        var fi = new[] { 0 };

        MediaStreamSource MakeMss()
        {
            fi[0] = 0;
            var inputProps = VideoEncodingProperties.CreateUncompressed(
                MediaEncodingSubtypes.Nv12, encWidth, encHeight);
            inputProps.FrameRate.Numerator   = (uint)fps;
            inputProps.FrameRate.Denominator = 1;
            var mss = new MediaStreamSource(new VideoStreamDescriptor(inputProps));
            mss.BufferTime = TimeSpan.Zero;
            mss.SampleRequested += (_, e) =>
            {
                if (fi[0] >= frames.Count) { e.Request.Sample = null; return; }
                var nv12 = BgraToNv12(frames[fi[0]], width, height, (int)encWidth, (int)encHeight);
                var ts   = TimeSpan.FromTicks((long)(fi[0] * 10_000_000.0 / fps));
                var dur  = TimeSpan.FromTicks((long)(10_000_000.0 / fps));
                var dw   = new DataWriter();
                dw.WriteBytes(nv12);
                var sample = MediaStreamSample.CreateFromBuffer(dw.DetachBuffer(), ts);
                sample.Duration = dur;
                e.Request.Sample = sample;
                fi[0]++;
            };
            return mss;
        }

        MediaEncodingProfile MakeProfile()
        {
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
            profile.Video.Width                 = encWidth;
            profile.Video.Height                = encHeight;
            profile.Video.Bitrate               = 4_000_000;
            profile.Video.FrameRate.Numerator   = (uint)fps;
            profile.Video.FrameRate.Denominator = 1;
            profile.Audio = null;
            return profile;
        }

        foreach (var hwEnabled in new[] { true, false })
        {
            using var output = new InMemoryRandomAccessStream();
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = hwEnabled };
            PrepareTranscodeResult result;
            try
            {
                result = await transcoder
                    .PrepareMediaStreamSourceTranscodeAsync(MakeMss(), output, MakeProfile());
            }
            catch (System.Runtime.InteropServices.COMException) when (hwEnabled)
            {
                continue;
            }
            if (!result.CanTranscode) continue;
            await result.TranscodeAsync();
            var size = (uint)output.Size;
            if (size == 0) continue;
            var dr = new DataReader(output.GetInputStreamAt(0));
            await dr.LoadAsync(size);
            var bytes = new byte[size];
            dr.ReadBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

        throw new InvalidOperationException("No encoder available (hardware or software)");
    }

    private static byte[] BgraToNv12(byte[] bgra, int srcWidth, int srcHeight,
        int encWidth, int encHeight)
    {
        var nv12 = new byte[encWidth * encHeight * 3 / 2];
        for (int y = 0; y < encHeight; y++)
        for (int x = 0; x < encWidth; x++)
        {
            int i = (y * srcWidth + x) * 4;
            int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            nv12[y * encWidth + x] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
        }
        int uvBase = encWidth * encHeight;
        for (int y = 0; y < encHeight; y += 2)
        for (int x = 0; x < encWidth; x += 2)
        {
            int i    = (y * srcWidth + x) * 4;
            int b    = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            int uvIdx = uvBase + (y / 2) * encWidth + x;
            nv12[uvIdx]     = (byte)(((-38 * r -  74 * g + 112 * b + 128) >> 8) + 128);
            nv12[uvIdx + 1] = (byte)(((112 * r -  94 * g -  18 * b + 128) >> 8) + 128);
        }
        return nv12;
    }

    // ── D3D11 / WinRT interop ─────────────────────────────────────────────────

    // IID_IDXGIDevice
    private static readonly Guid IID_DXGIDevice =
        new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    private static IDirect3DDevice CreateDirect3DDevice()
    {
        // D3D_DRIVER_TYPE_HARDWARE=1, D3D11_CREATE_DEVICE_BGRA_SUPPORT=0x20, D3D11_SDK_VERSION=7
        D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7,
            out var d3dPtr, IntPtr.Zero, IntPtr.Zero);

        var iid = IID_DXGIDevice;
        Marshal.QueryInterface(d3dPtr, ref iid, out var dxgiPtr);
        Marshal.Release(d3dPtr);

        NativeCreateDirect3D11DeviceFromDXGIDevice(dxgiPtr, out var winrtPtr);
        Marshal.Release(dxgiPtr);

        var device = MarshalInterface<IDirect3DDevice>.FromAbi(winrtPtr);
        Marshal.Release(winrtPtr);
        return device;
    }

    private static GraphicsCaptureItem CreateCaptureItem(int screenIndex)
    {
        var monitors = GetMonitorHandles();
        if (screenIndex < 0 || screenIndex >= monitors.Count)
            screenIndex = 0;

        const string classId = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var iid = typeof(IGraphicsCaptureItemInterop).GUID;

        WindowsCreateString(classId, classId.Length, out var hstring);
        try
        {
            RoGetActivationFactory(hstring, ref iid, out var factoryPtr);
            var factory = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            Marshal.Release(factoryPtr);

            var itemIid = new Guid("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90"); // IInspectable
            factory.CreateForMonitor(monitors[screenIndex], in itemIid, out var itemPtr);

            var item = MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
            Marshal.Release(itemPtr);
            return item;
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    private static List<IntPtr> GetMonitorHandles()
    {
        var handles = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (hMon, _, ref _, _) => { handles.Add(hMon); return true; },
            IntPtr.Zero);
        return handles;
    }

    private static byte[] ExtractBitmapBytes(SoftwareBitmap bitmap)
    {
        var capacity = (uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4);
        var buf      = new global::Windows.Storage.Streams.Buffer(capacity);
        bitmap.CopyToBuffer(buf);
        using var dr = DataReader.FromBuffer(buf);
        var bytes = new byte[buf.Length];
        dr.ReadBytes(bytes);
        return bytes;
    }

    // ── P/Invoke declarations ─────────────────────────────────────────────────

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, uint DriverType, IntPtr Software, uint Flags,
        IntPtr pFeatureLevels, uint FeatureLevels, uint SDKVersion,
        out IntPtr ppDevice, IntPtr pFeatureLevel, IntPtr ppImmediateContext);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    private static extern int NativeCreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll")]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(
        IntPtr runtimeClassId, ref Guid iid, out IntPtr factory);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    private delegate bool MonitorEnumProc(
        IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        void CreateForWindow(IntPtr hwnd, in Guid riid, out IntPtr ppv);
        void CreateForMonitor(IntPtr hMonitor, in Guid riid, out IntPtr ppv);
    }

    // ── Active session (start/stop) ───────────────────────────────────────────

    private sealed class ActiveSession : IDisposable
    {
        public readonly string Id          = Guid.NewGuid().ToString("N")[..12];
        public readonly int    ScreenIndex;
        public readonly int    Fps;
        public readonly int    Width;
        public readonly int    Height;

        private readonly IOpenClawLogger              _logger;
        private readonly List<byte[]>                 _frames     = new();
        private readonly object                       _framesLock = new();
        private readonly CancellationTokenSource      _cts        = new();
        private readonly Direct3D11CaptureFramePool   _pool;
        private readonly GraphicsCaptureSession       _session;
        private readonly DateTime                     _startedAt  = DateTime.UtcNow;
        private volatile Direct3D11CaptureFrame?      _latestFrame;
        private readonly SemaphoreSlim                _ready      = new(0, 1);
        private readonly Task                         _captureTask;

        public ActiveSession(int screenIndex, int fps, int width, int height,
            Direct3D11CaptureFramePool pool, GraphicsCaptureSession session,
            IOpenClawLogger logger)
        {
            ScreenIndex = screenIndex; Fps = fps; Width = width; Height = height;
            _pool = pool; _session = session; _logger = logger;

            pool.FrameArrived += OnFrameArrived;
            session.StartCapture();
            _captureTask = RunAsync(_cts.Token);
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool pool, object _)
        {
            var f = pool.TryGetNextFrame();
            if (f == null) return;
            Interlocked.Exchange(ref _latestFrame, f)?.Dispose();
            try { _ready.Release(); } catch { /* already signaled */ }
        }

        private async Task RunAsync(CancellationToken ct)
        {
            var intervalMs  = 1000 / Fps;
            var nextCapture = DateTime.UtcNow;
            var frameBytes  = (long)Width * Height * 4;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var waitMs = (int)(nextCapture - DateTime.UtcNow).TotalMilliseconds;
                    if (waitMs > 0) await Task.Delay(waitMs, ct);

                    if (!await _ready.WaitAsync(intervalMs * 2, ct)) continue;
                }
                catch (OperationCanceledException) { break; }

                var frame = Interlocked.Exchange(ref _latestFrame, null);
                if (frame == null) continue;

                using (frame)
                {
                    int frameCount;
                    lock (_framesLock) frameCount = _frames.Count;
                    if (frameCount * frameBytes >= MaxFrameBufferBytes)
                    {
                        _logger.Warn($"[ScreenRecording] Session {Id}: frame buffer cap reached ({MaxFrameBufferBytes / 1024 / 1024} MB), stopping capture.");
                        _cts.Cancel();
                        break;
                    }

                    try
                    {
                        var bmp   = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
                        var bytes = ExtractBitmapBytes(bmp);
                        lock (_framesLock) _frames.Add(bytes);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"[ScreenRecording] Session {Id} frame skipped: {ex.Message}");
                    }
                }

                nextCapture = nextCapture.AddMilliseconds(intervalMs);
            }
        }

        public async Task<(List<byte[]> frames, int durationMs)> StopAsync()
        {
            _cts.Cancel();
            try { await _captureTask; } catch (OperationCanceledException) { } catch { }

            var durationMs = (int)(DateTime.UtcNow - _startedAt).TotalMilliseconds;
            List<byte[]> snapshot;
            lock (_framesLock) snapshot = new List<byte[]>(_frames);
            return (snapshot, durationMs);
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _session.Dispose(); } catch { }
            try { _pool.Dispose(); } catch { }
            Interlocked.Exchange(ref _latestFrame, null)?.Dispose();
            _cts.Dispose();
            _ready.Dispose();
        }
    }
}
