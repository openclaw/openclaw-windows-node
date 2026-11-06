using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Principal;
using Windows.Foundation.Metadata;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;

namespace OpenClawTray.Services;

/// <summary>
/// Reads the CURRENT Windows user's display name and picture through the documented
/// User.GetDefault() -> GetPropertyAsync / GetPictureAsync contract (capability userAccountInformation).
/// It never enumerates users, never compares names or SIDs, and never logs or persists values.
/// Elevated, impersonating, or session 0 processes fail closed because the default user is not trustworthy there.
/// </summary>
internal sealed class WindowsAccountIdentityProvider : IWindowsAccountIdentityProvider
{
    private const int AccessDenied = unchecked((int)0x80070005);

    public async Task<AccountIdentityResult> ReadAsync(CancellationToken cancellationToken)
    {
        // User.GetDefault is UniversalApiContract 12 (Windows build 20348); the app supports older builds.
        if (!ApiInformation.IsMethodPresent("Windows.System.User", "GetDefault"))
            return AccountIdentityResult.Unavailable(AccountFieldStatus.Unsupported);
        if (!IsTrustedInteractiveContext())
            return AccountIdentityResult.Unavailable(AccountFieldStatus.ContextUnsupported);

        var user = User.GetDefault();
        if (user is null)
            return AccountIdentityResult.Unavailable(AccountFieldStatus.Unsupported);

        var (nameStatus, name) = await ReadNameAsync(user, cancellationToken);
        var (photoStatus, photo) = await ReadPhotoAsync(user, cancellationToken);
        return new AccountIdentityResult(nameStatus, name, photoStatus, photo);
    }

    private static bool IsTrustedInteractiveContext()
    {
        if (!Environment.UserInteractive || Process.GetCurrentProcess().SessionId == 0)
            return false;
        // GetCurrent(true) returns null unless the thread is impersonating.
        if (WindowsIdentity.GetCurrent(true) is not null)
            return false;
        using var identity = WindowsIdentity.GetCurrent();
        return !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static AccountFieldStatus MapFailure(Exception ex) => ex switch
    {
        UnauthorizedAccessException => AccountFieldStatus.Denied,
        COMException { HResult: AccessDenied } => AccountFieldStatus.Denied,
        _ => AccountFieldStatus.Failed,
    };

    private static async Task<(AccountFieldStatus, string?)> ReadNameAsync(User user, CancellationToken cancellationToken)
    {
        try
        {
            var value = await user.GetPropertyAsync(KnownUserProperties.DisplayName).AsTask(cancellationToken);
            return ProfileNamePolicy.TryNormalize(value as string, out var name)
                ? (AccountFieldStatus.Available, name)
                : (AccountFieldStatus.Empty, null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            return (MapFailure(ex), null);
        }
    }

    private static async Task<(AccountFieldStatus, AccountPhotoDraft?)> ReadPhotoAsync(User user, CancellationToken cancellationToken)
    {
        try
        {
            var reference = await user.GetPictureAsync(UserPictureSize.Size208x208).AsTask(cancellationToken);
            if (reference is null)
                return (AccountFieldStatus.Empty, null);

            using var stream = await reference.OpenReadAsync().AsTask(cancellationToken);
            if (stream.Size is 0 or > (ulong)AccountPhotoPolicy.MaxSourceBytes)
                return (stream.Size == 0 ? AccountFieldStatus.Empty : AccountFieldStatus.TooLarge, null);

            var source = new byte[stream.Size];
            using (var reader = new DataReader(stream))
            {
                await reader.LoadAsync((uint)source.Length).AsTask(cancellationToken);
                reader.ReadBytes(source);
                reader.DetachStream();
            }

            var gate = AccountPhotoPolicy.CheckSource(source.Length, source);
            if (gate != AccountFieldStatus.Available)
                return (gate, null);
            if (AccountPhotoPolicy.TryCreateDraft(source, out var direct))
                return (AccountFieldStatus.Available, direct);

            var reencoded = await ReencodeAsync(source, cancellationToken);
            return AccountPhotoPolicy.TryCreateDraft(reencoded, out var draft) && draft is not null
                ? (AccountFieldStatus.Available, draft)
                : (AccountFieldStatus.TooLarge, null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or COMException or InvalidOperationException
            or ArgumentException or IOException)
        {
            return (MapFailure(ex), null);
        }
    }

    /// <summary>Decodes the first frame only and re-encodes it as JPEG, scaling down but never up.</summary>
    private static async Task<byte[]> ReencodeAsync(byte[] source, CancellationToken cancellationToken)
    {
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(source.AsBuffer()).AsTask(cancellationToken);
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input).AsTask(cancellationToken);
        var longest = Math.Max(decoder.PixelWidth, decoder.PixelHeight);
        var scale = longest > AccountPhotoPolicy.OutputEdge ? (double)AccountPhotoPolicy.OutputEdge / longest : 1.0;
        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(1, (uint)(decoder.PixelWidth * scale)),
            ScaledHeight = Math.Max(1, (uint)(decoder.PixelHeight * scale)),
        };
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);

        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output).AsTask(cancellationToken);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            transform.ScaledWidth, transform.ScaledHeight, 96, 96, pixels.DetachPixelData());
        await encoder.FlushAsync().AsTask(cancellationToken);

        var bytes = new byte[output.Size];
        output.Seek(0);
        using var reader = new DataReader(output);
        await reader.LoadAsync((uint)bytes.Length).AsTask(cancellationToken);
        reader.ReadBytes(bytes);
        reader.DetachStream();
        return bytes;
    }
}
