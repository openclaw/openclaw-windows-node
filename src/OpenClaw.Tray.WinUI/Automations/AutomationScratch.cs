using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClaw.Shared.Cron;
using static Microsoft.UI.Reactor.Factories;
using static OpenClawTray.Automations.AutomationsVisuals;

namespace OpenClawTray.Automations;

internal sealed record AutomationScratchProps(AutomationsHost Host, CronJob Job);

internal sealed record ScratchState(
    CronScratchSnapshot? Snapshot,
    string Draft,
    bool Busy,
    bool Conflict,
    bool Redacted,
    string? Message,
    bool MessageIsError);

/// <summary>
/// Per-job scratch. Loaded only on request (it is private working state), saved
/// with compare-and-swap, and never written when its display had to be redacted.
/// </summary>
internal sealed class AutomationScratch : Component<AutomationScratchProps>
{
    private static readonly ScratchState Initial = new(null, "", false, false, false, null, false);

    public override Element Render()
    {
        var (host, job) = Props;
        var t = host.Text;
        var (state, setState) = UseState(Initial, threadSafe: true);
        var bytes = Encoding.UTF8.GetByteCount(state.Draft);

        async Task RunAsync(Func<IOperatorGatewayClient, Task<ScratchState>> operation)
        {
            var client = host.Client();
            if (client is null || state.Busy) return;
            setState(state with { Busy = true, Message = null });
            try
            {
                setState(await operation(client));
            }
            catch (Exception ex)
            {
                setState(state with { Busy = false, Message = ex.Message, MessageIsError = true });
            }
        }

        ScratchState FromSnapshot(CronScratchSnapshot snapshot, string? message)
        {
            // Display sanitization must never round-trip into the stored scratch.
            var display = TokenSanitizer.Sanitize(snapshot.Content);
            return new ScratchState(snapshot, display, false, false, display != snapshot.Content, message, false);
        }

        Task Load() => RunAsync(async client => FromSnapshot(await client.GetCronScratchAsync(job.Id), null));

        Task Save(string? content) => RunAsync(async client =>
        {
            var snapshot = state.Snapshot!;
            if (content is not null && Encoding.UTF8.GetByteCount(content) > snapshot.MaxBytes)
                return state with { Busy = false, Message = t["Scratch_TooLarge"], MessageIsError = true };
            var result = await client.SetCronScratchAsync(job.Id, content, snapshot.CurrentRevision);
            if (result.Conflict)
                return state with { Busy = false, Conflict = true, Message = t["Scratch_Conflict"], MessageIsError = true };
            return FromSnapshot(result.Snapshot ?? snapshot, content is null ? t["Scratch_Removed"] : t["Scratch_Saved"]);
        });

        var header = VStack(4,
            SectionTitle(t["Scratch_Title"]),
            Secondary(t["Scratch_Help"], 13));

        Element body;
        if (state.Snapshot is null)
        {
            body = HStack(Gap,
                GlyphButton(GlyphNote, t["Scratch_Load"], () => _ = Load(), "AutomationScratchLoad", !state.Busy),
                state.Busy ? ProgressRing().Size(16, 16) : Empty());
        }
        else
        {
            var snapshot = state.Snapshot;
            var canSave = !state.Busy && !state.Conflict && !state.Redacted;
            body = VStack(Gap,
                snapshot.Exists ? Empty() : Secondary(t["Scratch_Empty"]),
                state.Redacted
                    ? (InfoBar(null, t["Scratch_Redacted"]) with { Severity = InfoBarSeverity.Warning, IsClosable = false })
                    : Empty(),
                TextBox(state.Draft, text => setState(state with { Draft = text, Message = null }))
                    .AcceptsReturn(true)
                    .TextWrapping(TextWrapping.Wrap)
                    // Read-only while a save or reload is in flight so its result cannot overwrite newer typing.
                    .IsReadOnly(state.Redacted || state.Busy)
                    .FontFamily(MonoFont)
                    .MinHeight(220)
                    .MaxHeight(520)
                    .AutomationName(t["Scratch_Content"])
                    .AutomationId("AutomationScratchEditor"),
                TextBlock(t.Format("Scratch_Bytes", bytes, snapshot.MaxBytes)).FontSize(12)
                    .Foreground(bytes > snapshot.MaxBytes ? Theme.SystemCritical : Theme.SecondaryText)
                    .AutomationId("AutomationScratchBytes"),
                HStack(Gap,
                    GlyphButton(GlyphOk, t["Scratch_Save"], () => _ = Save(state.Draft), "AutomationScratchSave", canSave && bytes <= snapshot.MaxBytes, accent: true),
                    GlyphButton(GlyphRefresh, t["Scratch_Reload"], () => _ = Load(), "AutomationScratchReload", !state.Busy),
                    GlyphButton(GlyphDelete, t["Scratch_Remove"], () => _ = Save(null), "AutomationScratchRemove", !state.Busy && !state.Conflict && snapshot.Exists),
                    state.Busy ? ProgressRing().Size(16, 16) : Empty()));
        }

        var message = state.Message is null
            ? Empty()
            : state.MessageIsError
                ? ErrorText(state.Message, "AutomationScratchMessage")
                : Secondary(state.Message, 13).AutomationId("AutomationScratchMessage");

        return ScrollView(Surface(VStack(12, header, body, message))
                .Margin(PagePadding, 0, PagePadding, PagePadding)
                .MaxWidth(ReadingWidth)
                .HAlign(HorizontalAlignment.Left))
            .AutomationId("AutomationScratch");
    }
}
