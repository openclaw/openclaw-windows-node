// Also compiled by Windows PowerShell 5.1 during Inno uninstall. Keep this file
// self-contained and compatible with C# 5 / .NET Framework.
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OpenClaw.Connection.Migration
{
    public sealed class MigrationBinding
    {
        public string InstallDirectory { get; set; }
        public string RoamingDirectory { get; set; }
        public string LocalDirectory { get; set; }
        public string Architecture { get; set; }
        public string UserSid { get; set; }

        public MigrationBinding()
        {
            InstallDirectory = RoamingDirectory = LocalDirectory = Architecture = UserSid = "";
        }
    }

    public sealed class MigrationRecord
    {
        public string Kind { get; set; }
        public string MigrationId { get; set; }
        public string SourceVersion { get; set; }
        public string TargetVersion { get; set; }
        public string Fingerprint { get; set; }
        public string InventoryJson { get; set; }
        public bool AutoStart { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public MigrationBinding Binding { get; set; }

        public MigrationRecord()
        {
            Kind = MigrationId = SourceVersion = TargetVersion = Fingerprint = InventoryJson = "";
            Binding = new MigrationBinding();
        }
    }

    public static class MigrationRecordCodec
    {
        public const string DirectoryName = "store-migration";
        public const string IntentFileName = "intent.dpapi";
        public const string CompletionFileName = "completed.dpapi";
        public const string PackageName = "OpenClawFoundation.OpenClaw";
        public const string PackagePublisher = "CN=4BA40A7A-B719-4C40-BF91-84AF4F1136FC";
        public const int MaximumRecordBytes = 4 * 1024 * 1024;
        private const string SourceId = "{M0LTB0T-TRAY-4PP1-D3N7}";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OpenClaw.InnoToStore.Migration.v1");

#if NET10_0_OR_GREATER
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
        public static byte[] Encode(MigrationRecord record, DateTime utcNow)
        {
            Validate(record, record.Binding, utcNow);
            if (record.CreatedUtc > utcNow)
                throw new InvalidDataException("Invalid migration creation time.");
            byte[] plain;
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write("OpenClawMigration");
                    writer.Write(1);
                    writer.Write(SourceId);
                    writer.Write(PackageName);
                    writer.Write(PackagePublisher);
                    writer.Write(record.Kind);
                    writer.Write(record.MigrationId);
                    writer.Write(record.SourceVersion);
                    writer.Write(record.TargetVersion);
                    writer.Write(record.Binding.InstallDirectory);
                    writer.Write(record.Binding.RoamingDirectory);
                    writer.Write(record.Binding.LocalDirectory);
                    writer.Write(record.Binding.Architecture);
                    writer.Write(record.Binding.UserSid);
                    writer.Write(record.CreatedUtc.Ticks);
                    writer.Write(record.ExpiresUtc.Ticks);
                    writer.Write(record.Fingerprint);
                    writer.Write(record.AutoStart);
                    writer.Write(record.InventoryJson);
                }
                plain = stream.ToArray();
            }
            try
            {
                if (plain.Length > MaximumRecordBytes / 2)
                    throw new InvalidDataException("Migration inventory exceeds the supported size.");
                return ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }

#if NET10_0_OR_GREATER
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
        public static MigrationRecord Decode(byte[] bytes, MigrationBinding expected, DateTime utcNow)
        {
            return DecodeCore(bytes, expected, utcNow, false);
        }

        // Only a newly confirmed preparation may renew an expired intent.
#if NET10_0_OR_GREATER
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
        public static MigrationRecord DecodeForRenewedConsent(byte[] bytes, MigrationBinding expected, DateTime utcNow)
        {
            return DecodeCore(bytes, expected, utcNow, true);
        }

#if NET10_0_OR_GREATER
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
        private static MigrationRecord DecodeCore(byte[] bytes, MigrationBinding expected, DateTime utcNow, bool renewConsent)
        {
            if (bytes.Length == 0 || bytes.Length > MaximumRecordBytes)
                throw new InvalidDataException("Invalid migration record size.");
            var plain = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                using (var stream = new MemoryStream(plain, false))
                using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
                {
                    if (reader.ReadString() != "OpenClawMigration" || reader.ReadInt32() != 1 ||
                        reader.ReadString() != SourceId || reader.ReadString() != PackageName ||
                        reader.ReadString() != PackagePublisher)
                        throw new InvalidDataException("Unsupported migration record contract.");
                    var record = new MigrationRecord();
                    record.Kind = reader.ReadString();
                    record.MigrationId = reader.ReadString();
                    record.SourceVersion = reader.ReadString();
                    record.TargetVersion = reader.ReadString();
                    record.Binding.InstallDirectory = reader.ReadString();
                    record.Binding.RoamingDirectory = reader.ReadString();
                    record.Binding.LocalDirectory = reader.ReadString();
                    record.Binding.Architecture = reader.ReadString();
                    record.Binding.UserSid = reader.ReadString();
                    record.CreatedUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
                    record.ExpiresUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
                    record.Fingerprint = reader.ReadString();
                    record.AutoStart = reader.ReadBoolean();
                    record.InventoryJson = reader.ReadString();
                    if (stream.Position != stream.Length)
                        throw new InvalidDataException("Migration record has unexpected trailing data.");
                    Validate(record, expected, utcNow, renewConsent);
                    return record;
                }
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }

#if NET10_0_OR_GREATER
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
        public static MigrationRecord ReadCompletion(string path, MigrationBinding expected, DateTime utcNow)
        {
            RejectReparsePoints(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length == 0 || stream.Length > MaximumRecordBytes)
                    throw new InvalidDataException("Invalid migration record size.");
                using (var reader = new BinaryReader(stream))
                {
                    var record = Decode(reader.ReadBytes((int)stream.Length), expected, utcNow);
                    if (record.Kind != "completed")
                        throw new InvalidDataException("Migration has not completed.");
                    return record;
                }
            }
        }

        public static void RejectReparsePoints(string path)
        {
            var current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Migration paths must not contain reparse points.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                current = Path.GetDirectoryName(current);
            }
        }

        private static void Validate(MigrationRecord record, MigrationBinding expected, DateTime utcNow, bool renewConsent = false)
        {
            Guid id;
#if NET10_0_OR_GREATER
            Version? version;
#else
            Version version;
#endif
            if (!Guid.TryParseExact(record.MigrationId, "D", out id) || id == Guid.Empty ||
                !Version.TryParse(record.SourceVersion, out version) ||
                (record.Binding.Architecture != "x64" && record.Binding.Architecture != "arm64") ||
                record.Binding.Architecture != expected.Architecture ||
                string.IsNullOrWhiteSpace(expected.UserSid) || record.Binding.UserSid != expected.UserSid ||
                !SamePath(record.Binding.InstallDirectory, expected.InstallDirectory) ||
                !SamePath(record.Binding.RoamingDirectory, expected.RoamingDirectory) ||
                !SamePath(record.Binding.LocalDirectory, expected.LocalDirectory))
                throw new InvalidDataException("Migration record does not match this installation or Windows user.");
            if (record.CreatedUtc.Kind != DateTimeKind.Utc ||
                record.CreatedUtc < new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                throw new InvalidDataException("Invalid migration creation time.");
            if (record.Fingerprint.Length != 64)
                throw new InvalidDataException("Invalid migration state fingerprint.");
            foreach (char character in record.Fingerprint)
                if (!Uri.IsHexDigit(character))
                    throw new InvalidDataException("Invalid migration state fingerprint.");

            if (record.Kind == "intent")
            {
                if (record.CreatedUtc > utcNow || record.ExpiresUtc != record.CreatedUtc.AddDays(30) ||
                    (!renewConsent && utcNow >= record.ExpiresUtc) ||
                    record.TargetVersion != "" || string.IsNullOrWhiteSpace(record.InventoryJson))
                    throw new InvalidDataException("Migration intent is invalid or expired. Confirm migration again.");
            }
            else if (record.Kind == "completed")
            {
                // Neither aging nor a backward clock correction may re-enable destructive uninstall.
                if (record.ExpiresUtc.Ticks != DateTime.MaxValue.Ticks ||
                    !Version.TryParse(record.TargetVersion, out version) || record.InventoryJson != "")
                    throw new InvalidDataException("Invalid completed migration receipt.");
            }
            else
                throw new InvalidDataException("Unsupported migration state.");
        }

        private static bool SamePath(string actual, string expected)
        {
            if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected) ||
                !Path.IsPathRooted(actual) || !Path.IsPathRooted(expected))
                return false;
            return string.Equals(Path.GetFullPath(actual).TrimEnd('\\'),
                Path.GetFullPath(expected).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
    }
}
