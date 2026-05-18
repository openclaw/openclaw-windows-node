using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public class ChatAttachmentTests
{
    [Fact]
    public void FromFile_RejectsFileLargerThanMaxSize()
    {
        // Create a temp file larger than the limit
        var tempFile = Path.GetTempFileName();
        try
        {
            // Write just over the limit (MaxSizeBytes + 1 byte)
            using (var fs = File.OpenWrite(tempFile))
            {
                fs.SetLength(ChatAttachment.MaxSizeBytes + 1);
            }

            var ex = Assert.Throws<InvalidOperationException>(() => ChatAttachment.FromFile(tempFile));
            Assert.Contains("too large", ex.Message);
            Assert.Contains("10.0 MB", ex.Message);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task FromFileAsync_RejectsFileLargerThanMaxSize()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            using (var fs = File.OpenWrite(tempFile))
            {
                fs.SetLength(ChatAttachment.MaxSizeBytes + 1);
            }

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => ChatAttachment.FromFileAsync(tempFile));
            Assert.Contains("too large", ex.Message);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void FromFile_AcceptsFileAtMaxSize()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            using (var fs = File.OpenWrite(tempFile))
            {
                fs.SetLength(ChatAttachment.MaxSizeBytes);
            }

            var attachment = ChatAttachment.FromFile(tempFile);
            Assert.Equal(ChatAttachment.MaxSizeBytes, attachment.SizeBytes);
            Assert.NotEmpty(attachment.Content);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void FromFile_ThrowsForMissingFile()
    {
        Assert.Throws<FileNotFoundException>(() =>
            ChatAttachment.FromFile("/nonexistent/path/to/file.txt"));
    }

    [Fact]
    public async Task FromFileAsync_ThrowsForMissingFile()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => ChatAttachment.FromFileAsync("/nonexistent/path/to/file.txt"));
    }

    [Fact]
    public void FromFile_InfersCorrectMimeType()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "test_attachment.png");
        try
        {
            File.WriteAllBytes(tempFile, new byte[] { 0x89, 0x50, 0x4E, 0x47 }); // PNG magic bytes

            var attachment = ChatAttachment.FromFile(tempFile);
            Assert.Equal("image/png", attachment.MimeType);
            Assert.Equal("image", attachment.Type);
            Assert.Equal("test_attachment.png", attachment.FileName);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void FromFile_SetsTypeToFileForNonImages()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "test_doc.pdf");
        try
        {
            File.WriteAllBytes(tempFile, new byte[] { 0x25, 0x50, 0x44, 0x46 }); // PDF magic bytes

            var attachment = ChatAttachment.FromFile(tempFile);
            Assert.Equal("application/pdf", attachment.MimeType);
            Assert.Equal("file", attachment.Type);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void FormatSize_FormatsCorrectly()
    {
        var small = new ChatAttachment { SizeBytes = 512 };
        Assert.Equal("512 B", small.FormatSize());

        var medium = new ChatAttachment { SizeBytes = 1536 };
        Assert.Equal("1.5 KB", medium.FormatSize());

        var large = new ChatAttachment { SizeBytes = 5 * 1024 * 1024 };
        Assert.Equal("5.0 MB", large.FormatSize());
    }

    [Fact]
    public void MaxSizeBytes_Is10MB()
    {
        Assert.Equal(10L * 1024 * 1024, ChatAttachment.MaxSizeBytes);
    }
}
