using System.Text.Json;
using OpenClaw.WinNode.Cli;

namespace OpenClaw.WinNode.Cli.Tests;

public class EmitResultTests
{
    private static (int Exit, string Stdout, string Stderr) Run(string body, bool verbose = true)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        // EmitResult now takes a verbose flag (F-21). Tests pass verbose:true
        // by default so existing assertions about full body contents still hold.
        var exit = CliRunner.EmitResult(body, stdout, stderr, verbose);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Success_with_text_content_pretty_prints_inner_json()
    {
        var body = """
        {"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"text","text":"{\"ok\":true,\"n\":42}"}],"isError":false}}
        """;
        var (exit, stdout, stderr) = Run(body);
        Assert.Equal(0, exit);
        Assert.Equal("", stderr);
        // Pretty-printed: each property on its own line.
        Assert.Contains("\"ok\": true", stdout);
        Assert.Contains("\"n\": 42", stdout);
        Assert.Contains("\n", stdout);
    }

    [Fact]
    public void IsError_writes_text_to_stderr_and_exits_1()
    {
        var body = """
        {"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"text","text":"camera not found"}],"isError":true}}
        """;
        var (exit, stdout, stderr) = Run(body);
        Assert.Equal(1, exit);
        Assert.Equal("", stdout);
        Assert.Contains("camera not found", stderr);
    }

    [Fact]
    public void IsError_without_text_falls_back_to_default_message()
    {
        var body = """
        {"jsonrpc":"2.0","id":1,"result":{"content":[],"isError":true}}
        """;
        var (exit, stdout, stderr) = Run(body);
        Assert.Equal(1, exit);
        Assert.Equal("", stdout);
        Assert.Contains("tool execution failed", stderr);
    }

    [Fact]
    public void Jsonrpc_error_field_writes_code_and_message_to_stderr()
    {
        var body = """
        {"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"Method not found: foo"}}
        """;
        var (exit, stdout, stderr) = Run(body);
        Assert.Equal(1, exit);
        Assert.Equal("", stdout);
        Assert.Contains("-32601", stderr);
        Assert.Contains("Method not found: foo", stderr);
    }

    [Fact]
    public void Jsonrpc_error_without_message_uses_placeholder()
    {
        var body = """
        {"jsonrpc":"2.0","id":1,"error":{"code":-32000}}
        """;
        var (exit, _, stderr) = Run(body);
        Assert.Equal(1, exit);
        Assert.Contains("-32000", stderr);
        Assert.Contains("(no message)", stderr);
    }

    [Fact]
    public void Missing_result_writes_body_and_exits_1()
    {
        var body = "{\"jsonrpc\":\"2.0\",\"id\":1}";
        var (exit, stdout, stderr) = Run(body);
        Assert.Equal(1, exit);
        Assert.Equal("", stdout);
        Assert.Contains("missing 'result'", stderr, StringComparison.OrdinalIgnoreCase);
        // The full body is sanitized through SanitizeForStderr in verbose mode;
        // it has no token-shaped substrings or control chars, so it survives
        // verbatim apart from the trailing newline.
        Assert.Contains(body, stderr);
    }

    [Fact]
    public void Invalid_response_json_writes_body_and_exits_1()
    {
        var body = "not json at all";
        var (exit, stdout, stderr) = Run(body);
        Assert.Equal(1, exit);
        Assert.Equal("", stdout);
        Assert.Contains("not valid JSON", stderr);
        Assert.Contains("not json at all", stderr);
    }

    [Fact]
    public void Result_without_content_array_pretty_prints_raw_result()
    {
        var body = """
        {"jsonrpc":"2.0","id":1,"result":{"weird":"shape"}}
        """;
        var (exit, stdout, stderr) = Run(body);
        Assert.Equal(0, exit);
        Assert.Equal("", stderr);
        Assert.Contains("\"weird\": \"shape\"", stdout);
    }

    [Fact]
    public void Non_json_text_content_is_emitted_raw()
    {
        var body = """
        {"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"text","text":"plain string output"}],"isError":false}}
        """;
        var (exit, stdout, stderr) = Run(body);
        Assert.Equal(0, exit);
        Assert.Equal("", stderr);
        Assert.Contains("plain string output", stdout);
    }

    [Fact]
    public void Jsonrpc_error_without_code_defaults_to_zero()
    {
        var body = """
        {"jsonrpc":"2.0","id":1,"error":{"message":"oops"}}
        """;
        var (exit, _, stderr) = Run(body);
        Assert.Equal(1, exit);
        Assert.Contains("error 0", stderr);
        Assert.Contains("oops", stderr);
    }

    [Fact]
    public void Content_not_an_array_pretty_prints_raw_result()
    {
        var body = """
        {"jsonrpc":"2.0","id":1,"result":{"content":"not-an-array"}}
        """;
        var (exit, stdout, stderr) = Run(body);
        Assert.Equal(0, exit);
        Assert.Equal("", stderr);
        Assert.Contains("\"content\": \"not-an-array\"", stdout);
    }

    [Fact]
    public void First_content_element_without_text_pretty_prints_raw_result()
    {
        var body = """
        {"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"image","data":"abc"}]}}
        """;
        var (exit, stdout, stderr) = Run(body);
        Assert.Equal(0, exit);
        Assert.Equal("", stderr);
        Assert.Contains("\"image\"", stdout);
    }

    [Fact]
    public void First_content_text_non_string_pretty_prints_raw_result()
    {
        var body = """
        {"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"text","text":42}]}}
        """;
        var (exit, stdout, stderr) = Run(body);
        Assert.Equal(0, exit);
        Assert.Equal("", stderr);
        Assert.Contains("42", stdout);
    }

    [Fact]
    public void Result_with_empty_content_array_pretty_prints_result()
    {
        var body = """
        {"jsonrpc":"2.0","id":1,"result":{"content":[],"isError":false}}
        """;
        var (exit, stdout, stderr) = Run(body);
        Assert.Equal(0, exit);
        Assert.Equal("", stderr);
        Assert.Contains("\"content\"", stdout);
    }

    [Fact]
    public void Pretty_print_uses_json_formatting()
    {
        using var doc = JsonDocument.Parse("{\"a\":1,\"b\":[2,3]}");
        var pretty = CliRunner.PrettyPrint(doc.RootElement);
        Assert.Contains("\n", pretty);
        Assert.Contains("\"a\": 1", pretty);
    }
}
