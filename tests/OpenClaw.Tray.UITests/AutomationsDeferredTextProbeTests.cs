// Mounted regression and event diagnostics for AutomationEditor's native text binding.
// Run with FullyQualifiedName~AutomationsDeferredTextProbeTests. Event artifacts contain
// synthetic markers and lengths only.
using System.Text.Json;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Xunit.Abstractions;
using OpenClawTray.Automations;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Exercises the production editor binding and records native event/model/render ordering.
/// Owned-window UIA client writes are not real-keystroke or cross-process application proof.
/// </summary>
[Collection(UICollection.Name)]
public sealed class AutomationsDeferredTextProbeTests(UIThreadFixture ui, ITestOutputHelper output)
{
    private const string Seed = "probe-seed";
    private const string Primed = "probe-programmatic-baseline";
    private const string Edit = "probe-edit-value";
    private const string Replacement = "probe-replacement";
    private const int SettleTurns = 6;

    public enum ControlKind { TextBox, AutoSuggestBox }
    public enum Trigger
    {
        UnrelatedRenderAfterNativeInput,
        NonTextPropAfterNativeInput,
        UnrelatedRenderQueuedBeforeEdit,
        UnrelatedRenderAfterCommit,
        LegitimateReplacementAfterCommit,
    }

    [Theory]
    [InlineData(ControlKind.TextBox, Trigger.UnrelatedRenderAfterNativeInput)]
    [InlineData(ControlKind.TextBox, Trigger.NonTextPropAfterNativeInput)]
    [InlineData(ControlKind.TextBox, Trigger.UnrelatedRenderQueuedBeforeEdit)]
    [InlineData(ControlKind.TextBox, Trigger.UnrelatedRenderAfterCommit)]
    [InlineData(ControlKind.TextBox, Trigger.LegitimateReplacementAfterCommit)]
    [InlineData(ControlKind.AutoSuggestBox, Trigger.UnrelatedRenderAfterNativeInput)]
    [InlineData(ControlKind.AutoSuggestBox, Trigger.NonTextPropAfterNativeInput)]
    [InlineData(ControlKind.AutoSuggestBox, Trigger.UnrelatedRenderQueuedBeforeEdit)]
    [InlineData(ControlKind.AutoSuggestBox, Trigger.UnrelatedRenderAfterCommit)]
    [InlineData(ControlKind.AutoSuggestBox, Trigger.LegitimateReplacementAfterCommit)]
    public async Task ControlledField_RecordsNativeModelRenderOrdering(ControlKind kind, Trigger trigger)
    {
        var probe = new Probe(kind);
        ReactorHostControl? host = null;
        Window? window = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                host = new ReactorHostControl();
                host.Mount(_ => Component<ProbeView, ProbeProps>(new(probe)));
                window = new Window { Content = host };
                window.AppWindow.Move(new global::Windows.Graphics.PointInt32(-32000, -32000));
                window.Activate();
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                probe.Control is { IsLoaded: true, XamlRoot: not null }
                && (probe.Control is TextBox || FindDescendants<TextBox>(probe.Control).Any()),
                "mounted diagnostic input"));
            await ui.RunOnUIAsync(() =>
            {
                probe.AttachInput();
                probe.Mark("programmatic-prime");
                probe.Replace!(Primed);
            });
            // Observe the same programmatic-prime boundary as the failing baseline.
            // Never inject, clear or otherwise manipulate Reactor's suppression state.
            await SettleAsync();
            await ui.RunOnUIAsync(async () =>
            {
                probe.Mark("ready-after-prime");
                Assert.True(probe.NativeText == Primed && probe.Rendered == Primed,
                    "The controlled programmatic baseline did not render.");
                if (trigger == Trigger.UnrelatedRenderQueuedBeforeEdit)
                {
                    Assert.True(probe.Control!.DispatcherQueue.TryEnqueue(() =>
                    {
                        probe.Mark("queued-render-request");
                        probe.Bump();
                    }), "Dispatcher rejected the diagnostic render.");
                }
                probe.Mark("uia-input-begin");
                // AutoSuggestBox's template TextBox takes the same native edit path;
                // assigning AutoSuggestBox.Text directly would be ProgrammaticChange.
                await NativeTextValueInput.SetAsync(window!, probe.Input!, Edit);
                probe.Mark("uia-input-returned");
                if (trigger == Trigger.UnrelatedRenderAfterNativeInput)
                {
                    probe.Mark("post-native-input-render-request");
                    probe.Bump();
                }
                else if (trigger == Trigger.NonTextPropAfterNativeInput)
                {
                    probe.Mark(kind == ControlKind.TextBox ? "post-native-input-readonly" : "post-native-input-disabled");
                    probe.SetNonTextProp!(true);
                }
            });
            await SettleAsync();
            var inputCommitted = false;
            await ui.RunOnUIAsync(() =>
            {
                inputCommitted = probe.NativeText == Edit && probe.Model == Edit;
                probe.Mark(inputCommitted ? "input-committed" : "input-not-committed");
                if (trigger == Trigger.UnrelatedRenderAfterCommit)
                    probe.Bump();
                else if (trigger == Trigger.LegitimateReplacementAfterCommit)
                    probe.Replace!(Replacement);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                probe.Mark("final");
                var expected = trigger == Trigger.LegitimateReplacementAfterCommit ? Replacement : Edit;
                Assert.True(probe.NativeText == expected && probe.Model == expected && probe.Rendered == expected,
                    $"{kind}/{trigger}: final text/model/render must be {Marker(expected)}. See the event sequence.");
                if (trigger is Trigger.UnrelatedRenderAfterCommit or Trigger.LegitimateReplacementAfterCommit)
                    Assert.True(inputCommitted, "The edit was not committed before the final operation; replacement must not mask lost input.");
            });
        }
        finally
        {
            var events = Array.Empty<string>();
            try
            {
                await ui.RunOnUIAsync(() =>
                {
                    probe.Mark("teardown");
                    events = probe.Events.ToArray();
                    probe.Detach();
                    try
                    {
                        host?.Dispose();
                    }
                    finally
                    {
                        if (window is not null) { window.Content = null; window.Close(); }
                    }
                });
            }
            finally
            {
                output.WriteLine(string.Join(Environment.NewLine, events));
                var root = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS");
                if (!string.IsNullOrWhiteSpace(root))
                {
                    Directory.CreateDirectory(root);
                    File.WriteAllText(Path.Combine(root, $"deferred-text-{kind}-{trigger}.json"),
                        JsonSerializer.Serialize(new { Control = kind.ToString(), Variant = trigger.ToString(), Events = events }));
                }
                else
                {
                    output.WriteLine("Artifact directory not configured; diagnostic events are in test output only.");
                }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshBlankName_RetainsNativeEditAcrossImmediateReconcile(bool reconcileAfterNativeInput)
    {
        await ui.ResetContainerAsync();
        ReactorHostControl? host = null;
        TextBox? input = null;
        Element? current = null;
        var model = "";
        var events = new List<string>();
        var sequence = 0;
        TextChangedEventHandler? changed = null;
        global::Windows.Foundation.TypedEventHandler<TextBox, TextBoxTextChangingEventArgs>? changing = null;
        void Mark(string phase) => events.Add(
            $"{++sequence:D3} {phase} native={Value(input?.Text ?? "")} model={Value(model)}");
        Element Field() => AutomationTextInput.TextBox(model, value =>
            {
                Mark($"model-callback value={Value(value)}");
                model = value;
            }).AutomationName("Fresh blank Name probe").Set(native => native.TabIndex = 0);
        void Reconcile()
        {
            var next = Field();
            var result = host!.Reconciler.Reconcile(current, next, input, static () => { });
            Assert.Same(input, result);
            current = next;
            host.Content = result;
        }
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                host = new ReactorHostControl { Width = 560 };
                current = Field();
                input = Assert.IsType<TextBox>(host.Reconciler.Mount(current, static () => { }));
                host.Content = input;
                ui.Container.Children.Add(host);
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => input is { IsLoaded: true, XamlRoot: not null, ActualWidth: > 0 }, "fresh blank native Name mounted"));
            await ui.RunOnUIAsync(async () =>
            {
                Assert.Equal("", input!.Text);
                Assert.Equal("", model);
                changing = (_, _) => Mark("native-TextChanging");
                changed = (_, _) => Mark("native-TextChanged");
                input.TextChanging += changing;
                input.TextChanged += changed;
                Mark("unprimed-input-begin");
                await NativeTextValueInput.SetAsync(ui.TestWindow, input, Edit);
                Mark("uia-input-returned");
                if (reconcileAfterNativeInput)
                {
                    // Reconcile immediately after the native client input returns. The event trace,
                    // not this flag, establishes whether TextChanged already ran.
                    Mark("unrelated-reconcile-begin");
                    Reconcile();
                    Mark("unrelated-reconcile-end");
                }
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Mark("settled-input");
                Assert.Equal(Edit, input!.Text);
                Assert.Equal(Edit, model);
                // A native input preservation fix must still apply intentional owner changes.
                model = Replacement;
                Reconcile();
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Mark("settled-replacement");
                Assert.Equal(Replacement, input!.Text);
                Assert.Equal(Replacement, model);
                model = "";
                Reconcile();
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Mark("settled-reset");
                Assert.Equal("", input!.Text);
                Assert.Equal("", model);
            });
        }
        finally
        {
            try
            {
                await ui.RunOnUIAsync(() =>
                {
                    Mark("teardown");
                    if (input is not null)
                    {
                        if (changing is not null) input.TextChanging -= changing;
                        if (changed is not null) input.TextChanged -= changed;
                    }
                    if (host is not null)
                    {
                        ui.Container.Children.Remove(host);
                        host.Content = null;
                        host.Dispose();
                    }
                });
            }
            finally
            {
                output.WriteLine(string.Join(Environment.NewLine, events));
                var root = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS");
                if (!string.IsNullOrWhiteSpace(root))
                {
                    Directory.CreateDirectory(root);
                    File.WriteAllText(Path.Combine(root, $"deferred-text-fresh-name-immediate-{reconcileAfterNativeInput}.json"),
                        JsonSerializer.Serialize(new { ReconcileAfterNativeInput = reconcileAfterNativeInput, Events = events }));
                }
            }
        }
    }

    [Theory]
    [InlineData(ControlKind.TextBox)]
    [InlineData(ControlKind.AutoSuggestBox)]
    public async Task Binding_UsesCurrentCallbackAndRetiresItOnUnmount(ControlKind kind)
    {
        await ui.ResetContainerAsync();
        ReactorHostControl? host = null;
        FrameworkElement? control = null;
        Element? current = null;
        var oldValues = new List<string>();
        var currentValues = new List<string>();
        Element Field(string value, Action<string> changed) => kind == ControlKind.TextBox
            ? AutomationTextInput.TextBox(value, changed)
            : AutomationTextInput.AutoSuggestBox(value, changed);
        void Reconcile(string value)
        {
            var next = Field(value, currentValues.Add);
            Assert.Same(control, host!.Reconciler.Reconcile(current, next, control, static () => { }));
            current = next;
        }
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                host = new ReactorHostControl { Width = 560 };
                current = Field(Seed, oldValues.Add);
                control = Assert.IsAssignableFrom<FrameworkElement>(host.Reconciler.Mount(current, static () => { }));
                host.Content = control;
                ui.Container.Children.Add(host);
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                control is { IsLoaded: true, XamlRoot: not null }
                && (control is TextBox || FindDescendants<TextBox>(control).Any()), "callback lifetime input mounted"));
            await ui.RunOnUIAsync(async () =>
            {
                Assert.Empty(oldValues); // Initial owner value is not a user edit.
                Reconcile(Seed); // Same text, new callback, same native control.
                var input = control is TextBox box ? box : Assert.Single(FindDescendants<TextBox>(control!));
                await NativeTextValueInput.SetAsync(ui.TestWindow, input, Edit);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Empty(oldValues);
                Assert.Equal(new[] { Edit }, currentValues);
                Reconcile(Replacement);
                Reconcile("");
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var nativeControl = Assert.IsAssignableFrom<FrameworkElement>(control);
                var property = kind == ControlKind.TextBox
                    ? global::Microsoft.UI.Xaml.Controls.TextBox.TextProperty
                    : global::Microsoft.UI.Xaml.Controls.AutoSuggestBox.TextProperty;
                Assert.Equal("", (string)nativeControl.GetValue(property));
                Assert.Equal(new[] { Edit }, currentValues); // Model writes never echo into either callback.
                host!.Reconciler.UnmountChild(nativeControl);
                host.Content = null;
                nativeControl.SetValue(property, "after-unmount");
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Empty(oldValues);
                Assert.Equal(new[] { Edit }, currentValues);
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (host is null) return;
                ui.Container.Children.Remove(host);
                host.Content = null;
                host.Dispose();
            });
        }
    }

    private async Task SettleAsync()
    {
        // Drain queued UI work without sleeping or retrying an input assertion.
        for (var index = 0; index < SettleTurns; index++)
            await ui.YieldToRenderAsync();
    }

    private static string Marker(string value) => value switch
    {
        Seed => "seed",
        Primed => "primed",
        Edit => "edit",
        Replacement => "replacement",
        "" => "empty",
        _ => "other",
    };

    private static string Value(string value) => $"{Marker(value)}:{value.Length}";

    private sealed class Probe(ControlKind kind)
    {
        public ControlKind Kind { get; } = kind;
        public List<string> Events { get; } = [];
        public string Model { get; set; } = Seed;
        public string Rendered { get; set; } = Seed;
        public FrameworkElement? Control { get; private set; }
        public TextBox? Input { get; private set; }
        public Action<int>? SetTick { get; set; }
        public Action<bool>? SetNonTextProp { get; set; }
        public Action<string>? Replace { get; set; }
        private int _tick;
        private int _sequence;

        public string NativeText => Control switch
        {
            TextBox box => box.Text,
            AutoSuggestBox box => box.Text,
            _ => "",
        };

        public void Mark(string label) => Events.Add(
            $"{++_sequence:D3} {label} native={Value(NativeText)} input={(Input is null ? "absent" : Value(Input.Text))}" +
            $" model={Value(Model)} rendered={Value(Rendered)}");

        public void Bump() => SetTick!(++_tick);

        public void Attach(FrameworkElement control)
        {
            Control = control;
            if (control is AutoSuggestBox box)
                box.TextChanged += AutoTextChanged;
            Mark("mount");
        }

        public void AttachInput()
        {
            Input = Control is TextBox box ? box : Assert.Single(FindDescendants<TextBox>(Control!));
            Input.TextChanging += TextChanging;
            Input.TextChanged += TextChanged;
            Mark("input-observers-attached");
        }

        public void Detach()
        {
            if (Input is { } input)
            {
                input.TextChanging -= TextChanging;
                input.TextChanged -= TextChanged;
            }
            if (Control is AutoSuggestBox box)
                box.TextChanged -= AutoTextChanged;
        }

        private void TextChanging(TextBox sender, TextBoxTextChangingEventArgs args) => Mark("native-TextChanging");
        private void TextChanged(object sender, TextChangedEventArgs args) => Mark("native-TextChanged");
        private void AutoTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) =>
            Mark($"native-AutoTextChanged reason={args.Reason}");
    }

    private sealed record ProbeProps(Probe Probe);

    private sealed class ProbeView : Component<ProbeProps>
    {
        public override Element Render()
        {
            var probe = Props.Probe;
            var (text, setText) = UseState(Seed);
            var (tick, setTick) = UseState(0);
            var (nonTextProp, setNonTextProp) = UseState(false);
            probe.Rendered = text;
            probe.SetTick = setTick;
            probe.SetNonTextProp = setNonTextProp;
            probe.Replace = value =>
            {
                probe.Mark($"replacement-request value={Value(value)}");
                probe.Model = value;
                setText(value);
            };
            probe.Mark($"render tick={tick} non-text-prop={nonTextProp}");
            void Changed(string value)
            {
                probe.Mark($"model-setter value={Value(value)}");
                probe.Model = value;
                setText(value);
            }
            Element field = probe.Kind == ControlKind.TextBox
                ? AutomationTextInput.TextBox(text, Changed).AutomationName("Deferred text probe").IsReadOnly(nonTextProp).Set(native => native.TabIndex = 0)
                : (AutomationTextInput.AutoSuggestBox(text, Changed).AutomationName("Deferred text probe") with { OnSuggestionChosen = Changed }).IsEnabled(!nonTextProp);
            return VStack(4, TextBlock($"tick {tick}"),
                field.AutomationId("DeferredTextProbe").OnMount(probe.Attach).OnUnmountAdd(_ => probe.Detach()));
        }
    }
}
