// Narrow file operation for the Inno model choice, also compiled by PowerShell 5.1.
// Example: Run(dataDirectory, 0) previews; Run(dataDirectory, preview.EligibleMask)
// removes only that selection after the installer has obtained explicit consent.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace OpenClaw.Shared.Audio
{
    /// <summary>Measured catalog files, not a recursive app-data or shared-cache inventory.</summary>
    public sealed class KokoroUninstallReport
    {
        public int EligibleMask { get; internal set; }
        public long VerifiedBytes { get; internal set; }
        public long RetainedBytes { get; internal set; }
        public long RemovedBytes { get; internal set; }
        public bool Complete { get; internal set; }
        public string Text { get; internal set; }

        internal KokoroUninstallReport() { Complete = true; Text = string.Empty; }
    }

    /// <summary>Verifies catalog contents and deletes by the same locked handle, never recursively.</summary>
    public static class KokoroUninstallFiles
    {
        private const int DefaultScanTimeoutSeconds = 120;
        private const uint GenericRead = 0x80000000, DeleteAccess = 0x10000;
        private const uint ShareRead = 1, OpenExisting = 3;
        private const uint OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000;
        private const uint DirectoryAttribute = 0x10, ReparseAttribute = 0x400;

        /// <summary>Zero selection is read-only. Bits select only the fixed, shipped catalog.</summary>
        public static KokoroUninstallReport Run(string dataDirectory, int selectionMask)
        {
            int seconds;
            if (!int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_MODEL_UNINSTALL_TIMEOUT_SECONDS"), out seconds)
                || seconds < 10 || seconds > 600) seconds = DefaultScanTimeoutSeconds;
            return Inspect(dataDirectory, KokoroDownloadCatalog.All, selectionMask,
                CancellationToken.None, TimeSpan.FromSeconds(seconds));
        }

        internal static KokoroUninstallReport Inspect(string dataDirectory, KokoroDownloadAsset[] assets,
            int selectionMask, CancellationToken cancellationToken, TimeSpan timeout)
        {
            if (assets.Length > 30 || selectionMask < 0 || (selectionMask >> assets.Length) != 0)
                throw new ArgumentOutOfRangeException("selectionMask");
            var root = Path.GetFullPath(dataDirectory);
            var drive = Path.GetPathRoot(root);
            if (root.StartsWith(@"\\", StringComparison.Ordinal) || drive == null || drive.Length != 3)
                throw new ArgumentException("Only an ordinary local app-data path can be checked.");

            var report = new KokoroUninstallReport();
            var lines = new StringBuilder();
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < assets.Length; i++)
            {
                var asset = assets[i];
                // Catalog entries, not caller-provided paths, define the deletion boundary.
                if (Path.GetFileName(asset.PackId) != asset.PackId || asset.PackId == ".."
                    || Path.GetFileName(asset.FileName) != asset.FileName || asset.FileName == "..")
                    throw new InvalidDataException("Invalid Kokoro catalog path.");
                var path = Path.Combine(root, "models", "kokoro", asset.PackId, asset.FileName);
                var selected = (selectionMask & (1 << i)) != 0;
                long measured = 0;
                bool hasMeasurement = false;
                var ancestors = new List<SafeFileHandle>();
                try
                {
                    CheckBudget(clock, timeout, cancellationToken);
                    var directory = Path.GetDirectoryName(path);
                    if (directory == null) throw new IOException("Model directory is unavailable.");
                    LockAncestors(directory, ancestors);
                    using (var handle = Open(path, GenericRead | (selected ? DeleteAccess : 0), ShareRead, OpenReparsePoint))
                    {
                        var info = Information(handle);
                        if ((info.Attributes & (DirectoryAttribute | ReparseAttribute)) != 0 || info.Links != 1)
                            throw new IOException("Not an ordinary, single-link model file.");
                        using (var stream = new FileStream(handle, FileAccess.Read))
                        {
                            measured = stream.Length;
                            hasMeasurement = true;
                            var digest = Hash(stream, clock, timeout, cancellationToken);
                            if (!string.Equals(digest, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                            {
                                report.RetainedBytes += measured;
                                lines.AppendLine("Retained (content not verified): " + path + " (" + Bytes(measured) + ")");
                                if (selected) report.Complete = false;
                                continue;
                            }
                            if (selected)
                            {
                                CheckBudget(clock, timeout, cancellationToken);
                                // The handle denies writes/renames; all ancestors deny replacement.
                                // Hash verification and deletion therefore refer to the same file.
                                var disposition = new FileDisposition { DeleteFile = true };
                                if (!SetFileInformationByHandle(handle, 4, ref disposition, 1))
                                    throw new Win32Exception(Marshal.GetLastWin32Error());
                            }
                            else
                            {
                                report.EligibleMask |= 1 << i;
                                report.VerifiedBytes += measured;
                            }
                        }
                    }
                    if (selected)
                    {
                        report.RemovedBytes += measured;
                        lines.AppendLine("Removed: " + path + " (" + Bytes(measured) + ")");
                    }
                    else
                    {
                        report.RetainedBytes += measured;
                        lines.AppendLine("Verified download, kept unless selected: " + path + " (" + Bytes(measured) + ")");
                    }
                }
                catch (Win32Exception ex)
                {
                    if (ex.NativeErrorCode == 2 || ex.NativeErrorCode == 3)
                        lines.AppendLine("Absent: " + path);
                    else
                        RetainFailure(report, lines, path, measured, hasMeasurement, "Windows error " + ex.NativeErrorCode);
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException) && !(ex is UnauthorizedAccessException)
                        && !(ex is OperationCanceledException) && !(ex is TimeoutException)) throw;
                    RetainFailure(report, lines, path, measured, hasMeasurement,
                        ex is OperationCanceledException ? "cancelled" :
                        ex is TimeoutException ? "scan deadline reached" : "could not safely verify or remove");
                }
                finally
                {
                    for (int j = ancestors.Count - 1; j >= 0; j--) ancestors[j].Dispose();
                }
            }

            report.Text = "Verified downloads available for selection: " + Bytes(report.VerifiedBytes) + Environment.NewLine
                + "Removed model file bytes: " + Bytes(report.RemovedBytes) + Environment.NewLine
                + "Measured retained catalog files: " + Bytes(report.RetainedBytes)
                + (report.Complete ? "." : " (incomplete; some files could not be checked or removed).")
                + Environment.NewLine + Environment.NewLine + lines
                + Environment.NewLine + "Other files and directories are retained; their sizes were not scanned."
                + Environment.NewLine + "Retained data root: " + root
                + Environment.NewLine + "This choice does not remove settings.json, mcp-token.txt, gateways.json, gateways, other models, partial downloads, or shared caches."
                + Environment.NewLine + "Gateway removal is a separate choice and may remove its own credentials and state.";
            return report;
        }

        private static string Bytes(long value) { return value.ToString("N0", CultureInfo.CurrentCulture) + " bytes"; }

        private static void RetainFailure(KokoroUninstallReport report, StringBuilder lines, string path,
            long measured, bool hasMeasurement, string reason)
        {
            report.Complete = false;
            if (hasMeasurement) report.RetainedBytes += measured;
            lines.AppendLine("Retained / not verified: " + path + " ("
                + (hasMeasurement ? Bytes(measured) + "; " : "size unknown; ") + reason + ")");
        }

        private static void CheckBudget(Stopwatch clock, TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (clock.Elapsed >= timeout) throw new TimeoutException();
        }

        private static string Hash(Stream stream, Stopwatch clock, TimeSpan timeout, CancellationToken token)
        {
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[81920];
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    CheckBudget(clock, timeout, token);
                    sha.TransformBlock(buffer, 0, count, buffer, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                var digest = sha.Hash;
                if (digest == null) throw new IOException("Model hash is unavailable.");
                return BitConverter.ToString(digest).Replace("-", "");
            }
        }

        private static void LockAncestors(string directory, List<SafeFileHandle> handles)
        {
            var paths = new Stack<string>();
            for (var path = directory; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path) ?? string.Empty) paths.Push(path);
            // Deny write as well as delete sharing: an otherwise pinned directory
            // must not become a reparse point before its next descendant is opened.
            // Request read access so Windows participates in data-access sharing checks;
            // an attribute-only/zero-access handle does not establish this fence.
            while (paths.Count != 0)
            {
                var handle = Open(paths.Pop(), GenericRead, ShareRead, OpenReparsePoint | BackupSemantics);
                handles.Add(handle);
                var info = Information(handle);
                if ((info.Attributes & ReparseAttribute) != 0 || (info.Attributes & DirectoryAttribute) == 0)
                    throw new IOException("An ancestor is redirected or is not a directory.");
            }
        }

        private static SafeFileHandle Open(string path, uint access, uint share, uint flags)
        {
            var handle = CreateFile(path, access, share, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        private static FileInformation Information(SafeFileHandle handle)
        {
            FileInformation info;
            if (!GetFileInformationByHandle(handle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return info;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDisposition { [MarshalAs(UnmanagedType.U1)] internal bool DeleteFile; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
            ref FileDisposition info, uint size);
    }
}
