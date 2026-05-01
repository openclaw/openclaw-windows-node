using System;
using System.Collections.Generic;
using System.Linq;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;

namespace OpenClawTray.Onboarding.Widgets;

/// <summary>
/// Dynamic wizard step renderer that adapts UI based on <see cref="WizardStepType"/>.
/// Used by the onboarding wizard to render steps received from the gateway RPC protocol.
/// </summary>
public sealed class WizardStepView : Component<WizardStepProps>
{
    public override Element Render()
    {
        var body = Props.Type switch
        {
            WizardStepType.Note => RenderNote(),
            WizardStepType.Text => RenderText(),
            WizardStepType.Confirm => RenderConfirm(),
            WizardStepType.Select => RenderSelect(),
            WizardStepType.MultiSelect => RenderMultiSelect(),
            WizardStepType.Progress => RenderProgress(),
            WizardStepType.Action => RenderAction(),
            _ => RenderNote(),
        };

        return body
            .HAlign(HorizontalAlignment.Center)
            .MaxWidth(460);
    }

    private Element Header() =>
        VStack(8,
            TextBlock(Props.Title)
                .FontSize(20)
                .FontWeight(new global::Windows.UI.Text.FontWeight(700))
                .HAlign(HorizontalAlignment.Center),
            TextBlock(Props.Message)
                .FontSize(14)
                .Opacity(0.7)
                .HAlign(HorizontalAlignment.Center)
                .TextWrapping()
        );

    private Element RenderNote() =>
        VStack(16, Header());

    private Element RenderText()
    {
        var (value, setValue) = UseState(Props.InitialValue ?? "");

        Element input = Props.Sensitive
            ? PasswordBox(value, v => setValue(v), placeholderText: Props.Placeholder)
            : TextField(value, v => setValue(v), placeholder: Props.Placeholder, header: null);

        return VStack(16,
            Header(),
            Border(
                VStack(12, input).Padding(16)
            ).CornerRadius(8).Background("#FFFFFF"),
            Button("Submit", () => Props.OnSubmit?.Invoke(value))
                .HAlign(HorizontalAlignment.Center)
                .Disabled(string.IsNullOrWhiteSpace(value))
        );
    }

    private Element RenderConfirm()
    {
        return VStack(16,
            Header(),
            HStack(12,
                Button("Yes", () => Props.OnSubmit?.Invoke("Yes")),
                Button("No", () => Props.OnSubmit?.Invoke("No"))
            ).HAlign(HorizontalAlignment.Center)
        );
    }

    private Element RenderSelect()
    {
        var options = Props.Options ?? [];
        var initialIndex = Props.InitialValue != null
            ? Array.IndexOf(options, Props.InitialValue)
            : -1;
        var (selected, setSelected) = UseState(initialIndex);

        return VStack(16,
            Header(),
            Border(
                VStack(4,
                    options.Select((opt, i) =>
                        RadioButton(opt, selected == i, _ => setSelected(i), groupName: Props.Id)
                    ).ToArray()
                ).Padding(16)
            ).CornerRadius(8).Background("#FFFFFF"),
            Button("Submit", () =>
            {
                if (selected >= 0 && selected < options.Length)
                    Props.OnSubmit?.Invoke(options[selected]);
            })
            .HAlign(HorizontalAlignment.Center)
            .Disabled(selected < 0)
        );
    }

    private Element RenderMultiSelect()
    {
        var options = Props.Options ?? [];
        var (selections, setSelections) = UseState(new HashSet<int>());

        var toggles = options.Select((opt, i) =>
        {
            var isChecked = selections.Contains(i);
            return HStack(8,
                CheckBox(isChecked, _ =>
                {
                    var next = new HashSet<int>(selections);
                    if (isChecked) next.Remove(i); else next.Add(i);
                    setSelections(next);
                }),
                TextBlock(opt).FontSize(13)
                    .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center)
            );
        }).ToArray();

        return VStack(16,
            Header(),
            Border(
                VStack(6, toggles).Padding(16)
            ).CornerRadius(8).Background("#FFFFFF"),
            Button("Submit", () =>
            {
                var chosen = selections
                    .Where(i => i >= 0 && i < options.Length)
                    .OrderBy(i => i)
                    .Select(i => options[i]);
                Props.OnSubmit?.Invoke(string.Join(",", chosen));
            })
            .HAlign(HorizontalAlignment.Center)
            .Disabled(selections.Count == 0)
        );
    }

    private Element RenderProgress()
    {
        return VStack(16,
            Header(),
            TextBlock("⏳ Processing…")
                .FontSize(14)
                .Opacity(0.6)
                .HAlign(HorizontalAlignment.Center)
        );
    }

    private Element RenderAction()
    {
        return VStack(16,
            Header(),
            Button(Props.InitialValue ?? "Run", () => Props.OnSubmit?.Invoke("action"))
                .HAlign(HorizontalAlignment.Center)
        );
    }
}
