using OpenClaw.TestSupport;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenClaw.Connection.Tests;

public sealed class SparseFixtureFileTests
{
    [Fact]
    public void ModelSizedFixture_PreservesLengthZerosAndSparseAllocation()
    {
        using var temp = new TempDirectory("local-ai-sparse-");
        string path = temp.Combine("model.gguf");
        const long length = 22_663_387_424;

        SparseFixtureFile.Create(path, length);

        Assert.Equal(length, new FileInfo(path).Length);
        using (var stream = File.OpenRead(path))
        {
            foreach (long position in new[] { 0, length / 2, length - 4096 })
            {
                stream.Position = position;
                byte[] buffer = new byte[4096];
                stream.ReadExactly(buffer);
                Assert.All(buffer, value => Assert.Equal((byte)0, value));
            }
            Assert.Equal(-1, stream.ReadByte());
        }

        if (OperatingSystem.IsWindows())
        {
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.SparseFile));
            uint low = GetCompressedFileSizeW(path, out uint high);
            int error = Marshal.GetLastPInvokeError();
            if (low == uint.MaxValue && error != 0)
                throw new Win32Exception(error);
            ulong allocatedBytes = ((ulong)high << 32) | low;
            Assert.True(allocatedBytes < 1024 * 1024, $"Allocated {allocatedBytes} bytes for a sparse fixture.");
        }

        File.Delete(path);
        Assert.False(File.Exists(path));
        temp.Dispose();
        Assert.False(Directory.Exists(temp.Path));
    }

    [Fact]
    public void Fixtures_KeepMutationsIsolatedAndRejectOverwrite()
    {
        using var first = new TempDirectory("local-ai-sparse-first-");
        using var second = new TempDirectory("local-ai-sparse-second-");
        string firstPath = first.Combine("model.gguf");
        string secondPath = second.Combine("model.gguf");
        const long length = 22_663_387_424;
        SparseFixtureFile.Create(firstPath, length);
        SparseFixtureFile.Create(secondPath, length);

        using (var stream = File.OpenWrite(firstPath))
        {
            stream.Position = length - 1;
            stream.WriteByte(42);
        }

        Assert.Throws<IOException>(() => SparseFixtureFile.Create(firstPath, length));
        using (var stream = File.OpenRead(firstPath))
        {
            stream.Position = length - 1;
            Assert.Equal(42, stream.ReadByte());
        }
        using (var stream = File.OpenRead(secondPath))
        {
            stream.Position = length - 1;
            Assert.Equal(0, stream.ReadByte());
        }

        File.Delete(firstPath);
        Assert.Equal(length, new FileInfo(secondPath).Length);
    }

    [Fact]
    public void NegativeLength_DoesNotCreateAFile()
    {
        using var temp = new TempDirectory("local-ai-sparse-invalid-");
        string path = temp.Combine("model.gguf");

        Assert.Throws<ArgumentOutOfRangeException>(() => SparseFixtureFile.Create(path, -1));

        Assert.False(File.Exists(path));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string fileName, out uint fileSizeHigh);
}
