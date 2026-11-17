// Creates an unstarted external focus target. Use Create(true), then Start;
// the caller owns readiness checks and termination of that exact Process.
using System.Diagnostics;

namespace OpenClaw.Tray.UITests;

internal static class OwnedFocusProcess
{
    public static Process Create(bool editor)
    {
        var script = $$"""
            Add-Type -AssemblyName System.Windows.Forms
            $form = New-Object System.Windows.Forms.Form
            $form.Text = 'OpenClaw owned focus proof'
            $form.StartPosition = 'Manual'
            $form.Size = New-Object System.Drawing.Size(320, 160)
            $form.Location = New-Object System.Drawing.Point(
                ([System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea.Right - $form.Width - 16), 16)
            $form.TopMost = $true
            $control = New-Object System.Windows.Forms.{{(editor ? "TextBox" : "Button")}}
            $control.Name = 'OwnedFocusControl'
            $control.AutoSize = $false
            $control.Dock = 'Fill'
            $form.Controls.Add($control)
            [void]$form.ShowDialog()
            """;
        var process = new Process
        {
            StartInfo = new()
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-STA", "-EncodedCommand",
            Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) })
            process.StartInfo.ArgumentList.Add(argument);
        return process;
    }
}
