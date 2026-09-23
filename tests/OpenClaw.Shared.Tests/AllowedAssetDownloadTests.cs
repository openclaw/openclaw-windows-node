using System;
using OpenClaw.Shared.Audio;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class AllowedAssetDownloadTests
{
    [Fact]
    public void ValidateDownloadUri_AcceptsPinnedHttpsHosts()
    {
        AllowedAssetDownload.ValidateDownloadUri(
            new Uri("https://huggingface.co/owner/model/resolve/main/model.bin"),
            initialRequest: true);
        AllowedAssetDownload.ValidateDownloadUri(
            new Uri("https://cdn-lfs.huggingface.co/repos/model.bin"),
            initialRequest: false);
    }

    [Fact]
    public void ValidateDownloadUri_RejectsOffAllowlistRedirect()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AllowedAssetDownload.ValidateDownloadUri(
                new Uri("https://evil.example/model.bin"),
                initialRequest: false));
        Assert.Contains("untrusted host", ex.Message);
    }

    [Fact]
    public void ValidateDownloadUri_RejectsHttp()
    {
        Assert.Throws<InvalidOperationException>(() =>
            AllowedAssetDownload.ValidateDownloadUri(
                new Uri("http://huggingface.co/model.bin"),
                initialRequest: true));
    }
}
