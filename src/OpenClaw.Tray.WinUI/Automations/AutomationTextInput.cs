// Binds Automations text controls to the root-owned draft before deferred change events.
// Usage: AutomationTextInput.TextBox(form.Name, value => updateName(value)).AutomationId("Name").
using System;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClawTray.Automations;

/// <summary>
/// Uses native text-property notifications rather than preview.17's deferred, counter-suppressed
/// callbacks. This is a control binding, not a second editor draft: the caller still owns all values.
/// </summary>
internal static class AutomationTextInput
{
    private static readonly DependencyProperty BindingProperty = DependencyProperty.RegisterAttached(
        "Binding", typeof(object), typeof(AutomationTextInput), new PropertyMetadata(null));

    /// <summary>Creates a stock TextBox whose native edits immediately reach its current owner.</summary>
    public static TextBoxElement TextBox(string value, Action<string> changed,
        string? placeholder = null, string? header = null) =>
        new TextBoxElement(PlaceholderText: placeholder) { Header = header }
            .Set(native => Apply(native, Microsoft.UI.Xaml.Controls.TextBox.TextProperty, value, changed))
            .OnUnmount(Release);

    /// <summary>Creates a stock suggestion input; query and suggestion actions retain native semantics.</summary>
    public static AutoSuggestBoxElement AutoSuggestBox(string value, Action<string> changed) =>
        new AutoSuggestBoxElement(OnQuerySubmitted: changed, OnSuggestionChosen: changed)
            .Set(native => Apply(native, Microsoft.UI.Xaml.Controls.AutoSuggestBox.TextProperty, value, changed))
            .OnUnmount(Release);

    private static void Apply(FrameworkElement control, DependencyProperty property, string value, Action<string> changed)
    {
        // Store on the native dependency object so a different WinRT wrapper sees the same binding.
        if (control.GetValue(BindingProperty) is not TextBinding binding)
        {
            binding = new TextBinding(control, property);
            control.SetValue(BindingProperty, binding);
        }
        binding.Update(value, changed);
    }

    private static void Release(FrameworkElement control)
    {
        if (control.GetValue(BindingProperty) is TextBinding binding)
            binding.Dispose();
        control.ClearValue(BindingProperty);
    }

    private sealed class TextBinding : IDisposable
    {
        private readonly FrameworkElement _control;
        private readonly DependencyProperty _property;
        private Action<string>? _changed;
        private string? _value;
        private long? _token;
        private bool _writing;

        public TextBinding(FrameworkElement control, DependencyProperty property)
        {
            _control = control;
            _property = property;
            // ContentDialog reconciliation can reparent a still-live input. Its
            // property binding belongs to the Reactor element, not visual loading.
            _token = control.RegisterPropertyChangedCallback(property, Changed);
        }

        public void Update(string value, Action<string> changed)
        {
            _changed = changed;
            // AutoSuggestBox's template edit can precede its outer Text change. An unrelated
            // render of the same owner value must not restore that value over the pending edit.
            if (string.Equals(_value, value, StringComparison.Ordinal)) return;
            _value = value;
            _writing = true;
            try
            {
                if (!string.Equals((string)_control.GetValue(_property), value, StringComparison.Ordinal))
                    _control.SetValue(_property, value);
            }
            finally { _writing = false; }
        }

        private void Changed(DependencyObject sender, DependencyProperty property)
        {
            if (_writing) return;
            var value = (string)sender.GetValue(property);
            if (string.Equals(_value, value, StringComparison.Ordinal)) return;
            _value = value;
            _changed?.Invoke(value);
        }

        public void Dispose()
        {
            if (_token is { } token)
                _control.UnregisterPropertyChangedCallback(_property, token);
            _token = null;
            _changed = null;
        }
    }
}
