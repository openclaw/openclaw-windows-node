using System.ComponentModel;

namespace OpenClawTray.Infrastructure.Core;

/// <summary>
/// A single-value cell that raises <see cref="INotifyPropertyChanged.PropertyChanged"/>
/// when <see cref="Value"/> is assigned a different value. Exists to let
/// developers declare small pieces of global app state (dev flags, light/dark
/// mode, currently-signed-in user, etc.) in one line without hand-writing
/// an INotifyPropertyChanged class.
///
/// Pairs with <c>RenderContext.UseObservable</c>, which subscribes the
/// current component and re-renders on change:
/// <code>
/// public static readonly Observable&lt;bool&gt; DebugUI = new(false);
/// // ...
/// var on = ctx.UseObservable(DebugUI).Value;
/// </code>
///
/// For richer state (multiple related properties, computed properties, XAML
/// binding interop), authoring a plain INPC class is still the recommended
/// path — <see cref="Observable{T}"/> is deliberately minimal and is not a
/// replacement for that.
/// </summary>
public sealed class Observable<T> : INotifyPropertyChanged
{
    private T _value;

    public Observable() : this(default!) { }

    public Observable(T initial) => _value = initial;

    public T Value
    {
        get => _value;
        set
        {
            if (EqualityComparer<T>.Default.Equals(_value, value)) return;
            _value = value;
            PropertyChanged?.Invoke(this, _valueChangedArgs);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => _value?.ToString() ?? string.Empty;

    public static implicit operator T(Observable<T> cell) => cell._value;

    private static readonly PropertyChangedEventArgs _valueChangedArgs = new(nameof(Value));
}
