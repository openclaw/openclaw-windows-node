using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace OpenClaw.TestSupport;

public static class NativeTestFailureTrace
{
    public static IReadOnlyList<string> Describe(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var lines = new List<string>();
        var depth = 0;
        for (Exception? current = error; current is not null && depth < 4; depth++, current = current.InnerException)
        {
            lines.Add($"native-ui-exception depth={depth} type={current.GetType().FullName} hresult=0x{current.HResult.ToString("X8", CultureInfo.InvariantCulture)}");
            var frames = new StackTrace(current, false).GetFrames();
            for (var index = 0; index < Math.Min(frames.Length, 32); index++)
            {
                if (frames[index].GetMethod() is { } method)
                    lines.Add($"native-ui-frame {method.DeclaringType?.FullName}.{method.Name}");
            }
        }
        return lines;
    }

    public static void Write(Exception error)
    {
        try
        {
            // xUnit buffers Console.Error until a test completes. Bypass that buffer so
            // a native fail-fast cannot discard this deliberately message-free trace.
            using var writer = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                AutoFlush = true
            };
            foreach (var line in Describe(error))
                writer.WriteLine(line);
        }
        catch (Exception outputError) when (outputError is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Native UI diagnostic output unavailable ({0}).", outputError.GetType().Name);
        }
    }
}
