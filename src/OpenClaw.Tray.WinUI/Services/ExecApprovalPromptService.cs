using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using OpenClaw.Shared;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

public sealed class ExecApprovalPromptService : IExecApprovalPromptHandler
{
    private readonly IOpenClawLogger _logger;

    public ExecApprovalPromptService(
        DispatcherQueue dispatcherQueue,
        Func<FrameworkElement?> rootProvider,
        IOpenClawLogger logger)
    {
        _logger = logger;
    }

    public Task<ExecApprovalPromptDecision> RequestAsync(
        ExecApprovalPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<ExecApprovalPromptDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(ExecApprovalPromptDecision.Deny("Approval prompt was cancelled"));

        var thread = new Thread(() =>
        {
            try
            {
                var decision = ShowTaskDialog(request);
                _logger.Info($"[ExecApproval] Prompt decision: {decision.Kind}");
                tcs.TrySetResult(decision);
            }
            catch (Exception ex)
            {
                _logger.Warn($"[ExecApproval] Prompt failed: {ex.Message}");
                tcs.TrySetResult(ExecApprovalPromptDecision.Deny("Approval prompt failed"));
            }
        })
        {
            IsBackground = true,
            Name = "OpenClaw Exec Approval Prompt"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return tcs.Task;
    }

    private static ExecApprovalPromptDecision ShowTaskDialog(ExecApprovalPromptRequest request)
    {
        var text =
            "A remote agent wants to run a local command on this Windows machine." +
            Environment.NewLine + Environment.NewLine +
            request.Command +
            Environment.NewLine + Environment.NewLine +
            $"Shell: {request.Shell ?? "auto"}" +
            Environment.NewLine +
            $"Reason: {request.Reason}";

        var buttons = new[]
        {
            new TaskDialogButton(AllowOnceButtonId, "Allow once"),
            new TaskDialogButton(AlwaysAllowButtonId, "Always allow"),
            new TaskDialogButton(DenyButtonId, "Deny")
        };

        var buttonsPtr = IntPtr.Zero;
        try
        {
            var buttonSize = Marshal.SizeOf<TaskDialogButton>();
            buttonsPtr = Marshal.AllocHGlobal(buttonSize * buttons.Length);
            for (var i = 0; i < buttons.Length; i++)
            {
                Marshal.StructureToPtr(buttons[i], buttonsPtr + i * buttonSize, false);
            }

            var config = new TaskDialogConfig
            {
                cbSize = (uint)Marshal.SizeOf<TaskDialogConfig>(),
                dwFlags = TaskDialogFlags.AllowDialogCancellation,
                pszWindowTitle = "Approve local command?",
                pszMainInstruction = "Approve local command?",
                pszContent = text,
                cButtons = (uint)buttons.Length,
                pButtons = buttonsPtr,
                nDefaultButton = DenyButtonId,
                cxWidth = 260
            };

            var hresult = TaskDialogIndirect(ref config, out var button, out _, out _);
            if (hresult < 0)
                Marshal.ThrowExceptionForHR(hresult);

            return button switch
            {
                AllowOnceButtonId => ExecApprovalPromptDecision.AllowOnce(),
                AlwaysAllowButtonId => ExecApprovalPromptDecision.AlwaysAllow(),
                _ => ExecApprovalPromptDecision.Deny()
            };
        }
        catch (DllNotFoundException)
        {
            return ShowMessageBoxFallback(text);
        }
        catch (EntryPointNotFoundException)
        {
            return ShowMessageBoxFallback(text);
        }
        finally
        {
            if (buttonsPtr != IntPtr.Zero)
            {
                var buttonSize = Marshal.SizeOf<TaskDialogButton>();
                for (var i = 0; i < buttons.Length; i++)
                {
                    Marshal.DestroyStructure<TaskDialogButton>(buttonsPtr + i * buttonSize);
                }
                Marshal.FreeHGlobal(buttonsPtr);
            }
        }
    }

    private static ExecApprovalPromptDecision ShowMessageBoxFallback(string text)
    {
        var fallbackText = text +
            Environment.NewLine + Environment.NewLine +
            "Yes = Allow once" +
            Environment.NewLine +
            "No or Cancel = Deny";

        var result = MessageBoxW(
            IntPtr.Zero,
            fallbackText,
            "Approve local command?",
            MessageBoxFlags.YesNoCancel |
            MessageBoxFlags.IconWarning |
            MessageBoxFlags.TopMost |
            MessageBoxFlags.SetForeground |
            MessageBoxFlags.DefaultButton3);

        return result switch
        {
            MessageBoxYes => ExecApprovalPromptDecision.AllowOnce(),
            _ => ExecApprovalPromptDecision.Deny()
        };
    }

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int TaskDialogIndirect(
        ref TaskDialogConfig taskConfig,
        out int button,
        out int radioButton,
        [MarshalAs(UnmanagedType.Bool)] out bool verificationFlagChecked);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, MessageBoxFlags type);

    private const int AllowOnceButtonId = 1001;
    private const int AlwaysAllowButtonId = 1002;
    private const int DenyButtonId = 1003;
    private const int MessageBoxYes = 6;

    [Flags]
    private enum TaskDialogFlags : uint
    {
        AllowDialogCancellation = 0x0008
    }

    [Flags]
    private enum MessageBoxFlags : uint
    {
        YesNoCancel = 0x00000003,
        IconWarning = 0x00000030,
        DefaultButton3 = 0x00000200,
        TopMost = 0x00040000,
        SetForeground = 0x00010000
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TaskDialogButton
    {
        public int nButtonID;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string pszButtonText;

        public TaskDialogButton(int id, string text)
        {
            nButtonID = id;
            pszButtonText = text;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TaskDialogConfig
    {
        public uint cbSize;
        public IntPtr hwndParent;
        public IntPtr hInstance;
        public TaskDialogFlags dwFlags;
        public uint dwCommonButtons;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pszWindowTitle;

        public IntPtr hMainIcon;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pszMainInstruction;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pszContent;

        public uint cButtons;
        public IntPtr pButtons;
        public int nDefaultButton;
        public uint cRadioButtons;
        public IntPtr pRadioButtons;
        public int nDefaultRadioButton;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pszVerificationText;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pszExpandedInformation;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pszExpandedControlText;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pszCollapsedControlText;

        public IntPtr hFooterIcon;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pszFooter;

        public IntPtr pfCallback;
        public IntPtr lpCallbackData;
        public uint cxWidth;
    }
}
