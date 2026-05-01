using System;
using System.Text;
using System.Text.Json;
using OpenClawTray.Onboarding.Services;

namespace OpenClaw.Tray.Tests;

public class SetupCodeDecoderTests
{
    /// <summary>Helper to encode a JSON string as base64url.</summary>
    private static string ToBase64Url(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    #region Valid decode

    [Fact]
    public void Decode_ValidCode_ReturnsUrlAndToken()
    {
        var json = """{"url":"ws://localhost:18789","bootstrapToken":"mytoken"}""";
        var code = ToBase64Url(json);

        var result = SetupCodeDecoder.Decode(code);

        Assert.True(result.Success);
        Assert.Equal("ws://localhost:18789", result.Url);
        Assert.Equal("mytoken", result.Token);
    }

    [Fact]
    public void Decode_StandardBase64_AlsoWorks()
    {
        var json = """{"url":"ws://localhost:18789","bootstrapToken":"tok"}""";
        // Use standard base64 (with + / =) instead of base64url
        var code = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        var result = SetupCodeDecoder.Decode(code);

        Assert.True(result.Success);
        Assert.Equal("ws://localhost:18789", result.Url);
    }

    [Fact]
    public void Decode_UrlOnly_SucceedsWithNullToken()
    {
        var json = """{"url":"wss://gateway.example.com"}""";
        var code = ToBase64Url(json);

        var result = SetupCodeDecoder.Decode(code);

        Assert.True(result.Success);
        Assert.Equal("wss://gateway.example.com", result.Url);
        Assert.Null(result.Token);
    }

    [Fact]
    public void Decode_TokenOnly_SucceedsWithNullUrl()
    {
        var json = """{"bootstrapToken":"abc123"}""";
        var code = ToBase64Url(json);

        var result = SetupCodeDecoder.Decode(code);

        Assert.True(result.Success);
        Assert.Null(result.Url);
        Assert.Equal("abc123", result.Token);
    }

    [Fact]
    public void Decode_HttpUrl_AlsoValid()
    {
        var json = """{"url":"http://localhost:18789","bootstrapToken":"tok"}""";
        var code = ToBase64Url(json);

        var result = SetupCodeDecoder.Decode(code);

        Assert.True(result.Success);
        Assert.Equal("http://localhost:18789", result.Url);
    }

    #endregion

    #region Size limits

    [Fact]
    public void Decode_CodeOver2048Chars_ReturnsError()
    {
        var code = new string('A', 2049);
        var result = SetupCodeDecoder.Decode(code);

        Assert.False(result.Success);
        Assert.Contains("2048", result.Error);
    }

    [Fact]
    public void Decode_CodeExactly2048Chars_DoesNotRejectOnSize()
    {
        // 2048 chars is within limit — may still fail on base64/JSON but not on size
        var code = new string('A', 2048);
        var result = SetupCodeDecoder.Decode(code);
        // Should not get the "exceeds 2048" error
        Assert.True(result.Error == null || !result.Error.Contains("2048"));
    }

    [Fact]
    public void Decode_TokenOver512Chars_TokenIsNull()
    {
        var longToken = new string('x', 513);
        var json = $$$"""{"url":"ws://localhost:18789","bootstrapToken":"{{{longToken}}}"}""";
        var code = ToBase64Url(json);

        var result = SetupCodeDecoder.Decode(code);

        Assert.True(result.Success);
        Assert.Null(result.Token);
    }

    #endregion

    #region Error cases

    [Fact]
    public void Decode_EmptyString_ReturnsError()
    {
        var result = SetupCodeDecoder.Decode("");
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Decode_Whitespace_ReturnsError()
    {
        var result = SetupCodeDecoder.Decode("   ");
        Assert.False(result.Success);
    }

    [Fact]
    public void Decode_MalformedBase64_ReturnsError()
    {
        var result = SetupCodeDecoder.Decode("!!!not-base64!!!");
        Assert.False(result.Success);
        Assert.Contains("base64", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decode_ValidBase64_InvalidJson_ReturnsError()
    {
        var code = ToBase64Url("this is not json");
        var result = SetupCodeDecoder.Decode(code);

        Assert.False(result.Success);
        Assert.Contains("JSON", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decode_InvalidGatewayUrl_ReturnsError()
    {
        var json = """{"url":"ftp://bad-scheme.example.com","bootstrapToken":"tok"}""";
        var code = ToBase64Url(json);

        var result = SetupCodeDecoder.Decode(code);

        Assert.False(result.Success);
        Assert.Contains("Invalid gateway URL", result.Error);
    }

    [Fact]
    public void Decode_MissingUrlField_SucceedsWithNullUrl()
    {
        var json = """{"other":"data"}""";
        var code = ToBase64Url(json);

        var result = SetupCodeDecoder.Decode(code);

        Assert.True(result.Success);
        Assert.Null(result.Url);
        Assert.Null(result.Token);
    }

    #endregion
}
