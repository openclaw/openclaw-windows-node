namespace OpenClawTray.Infrastructure.Controls.Validation;

/// <summary>
/// Collects, queries, and manages validation messages for a form or section of a component tree.
/// Thread-safe: all mutation and query methods are synchronized.
/// </summary>
public sealed class ValidationContext
{
    private readonly object _lock = new();
    private readonly Dictionary<string, List<ValidationMessage>> _messages = new();
    private readonly Dictionary<string, List<ValidationMessage>> _externalMessages = new();
    private readonly HashSet<string> _registeredFields = new();
    private readonly HashSet<string> _touchedFields = new();
    private readonly Dictionary<string, object?> _initialValues = new();
    private readonly Dictionary<string, object?> _currentValues = new();

    private int _version;

    /// <summary>
    /// Monotonically increasing version number, bumped on every mutation.
    /// Useful for change detection in hooks/memos.
    /// </summary>
    public int Version
    {
        get { lock (_lock) return _version; }
    }

    // ════════════════════════════════════════════════════════════════
    //  Field registration
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Registers a field with the context. Called automatically by .Validate()
    /// or manually for fields that need dirty/touched tracking.
    /// </summary>
    public void RegisterField(string field)
    {
        lock (_lock)
        {
            _registeredFields.Add(field);
        }
    }

    /// <summary>
    /// Returns all registered field names.
    /// </summary>
    public IReadOnlySet<string> RegisteredFields
    {
        get { lock (_lock) return new HashSet<string>(_registeredFields); }
    }

    // ════════════════════════════════════════════════════════════════
    //  Message collection
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Adds a validation message for the specified field.
    /// </summary>
    public void Add(string field, string text, Severity severity = Severity.Error)
    {
        Add(new ValidationMessage(field, text, severity));
    }

    /// <summary>
    /// Adds a validation message.
    /// </summary>
    public void Add(ValidationMessage message)
    {
        lock (_lock)
        {
            if (!_messages.TryGetValue(message.Field, out var list))
            {
                list = new List<ValidationMessage>();
                _messages[message.Field] = list;
            }
            list.Add(message);
            _version++;
        }
    }

    /// <summary>
    /// Adds an externally-sourced validation message (e.g., server-side error).
    /// External messages persist until explicitly cleared or the field value changes.
    /// </summary>
    public void AddExternal(string field, string text, Severity severity = Severity.Error)
    {
        AddExternal(new ValidationMessage(field, text, severity));
    }

    /// <summary>
    /// Adds an externally-sourced validation message.
    /// </summary>
    public void AddExternal(ValidationMessage message)
    {
        lock (_lock)
        {
            if (!_externalMessages.TryGetValue(message.Field, out var list))
            {
                list = new List<ValidationMessage>();
                _externalMessages[message.Field] = list;
            }
            list.Add(message);
            _version++;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Message clearing
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Clears all validation messages (both internal and external) for the specified field.
    /// </summary>
    public void Clear(string field)
    {
        lock (_lock)
        {
            var changed = false;
            if (_messages.Remove(field)) changed = true;
            if (_externalMessages.Remove(field)) changed = true;
            if (changed) _version++;
        }
    }

    /// <summary>
    /// Clears only internal (validator-produced) messages for the specified field.
    /// External messages are preserved.
    /// </summary>
    internal void ClearInternal(string field)
    {
        lock (_lock)
        {
            if (_messages.Remove(field))
                _version++;
        }
    }

    /// <summary>
    /// Clears only external messages for the specified field.
    /// </summary>
    public void ClearExternal(string field)
    {
        lock (_lock)
        {
            if (_externalMessages.Remove(field))
                _version++;
        }
    }

    /// <summary>
    /// Clears all messages (internal and external) for all fields.
    /// </summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            _messages.Clear();
            _externalMessages.Clear();
            _version++;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Query methods
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns all messages (internal + external) for the specified field.
    /// </summary>
    public IReadOnlyList<ValidationMessage> GetMessages(string field)
    {
        lock (_lock)
        {
            var result = new List<ValidationMessage>();
            if (_messages.TryGetValue(field, out var internal_))
                result.AddRange(internal_);
            if (_externalMessages.TryGetValue(field, out var external))
                result.AddRange(external);
            return result;
        }
    }

    /// <summary>
    /// Returns all messages (internal + external) across all fields.
    /// </summary>
    public IReadOnlyList<ValidationMessage> GetAllMessages()
    {
        lock (_lock)
        {
            var result = new List<ValidationMessage>();
            foreach (var list in _messages.Values)
                result.AddRange(list);
            foreach (var list in _externalMessages.Values)
                result.AddRange(list);
            return result;
        }
    }

    /// <summary>
    /// Returns true if the specified field has any Error-severity messages.
    /// </summary>
    public bool HasError(string field)
    {
        lock (_lock)
        {
            return HasSeverity(field, Severity.Error);
        }
    }

    /// <summary>
    /// Returns true if the specified field has any messages of any severity.
    /// </summary>
    public bool HasMessages(string field)
    {
        lock (_lock)
        {
            if (_messages.TryGetValue(field, out var internal_) && internal_.Count > 0)
                return true;
            if (_externalMessages.TryGetValue(field, out var external) && external.Count > 0)
                return true;
            return false;
        }
    }

    /// <summary>
    /// Returns the highest severity among all messages for the specified field, or null if none.
    /// </summary>
    public Severity? HighestSeverity(string field)
    {
        lock (_lock)
        {
            Severity? highest = null;
            CheckSeverity(field, _messages, ref highest);
            CheckSeverity(field, _externalMessages, ref highest);
            return highest;
        }
    }

    /// <summary>
    /// Returns true when there are no Error-severity messages across all fields.
    /// </summary>
    public bool IsValid()
    {
        lock (_lock)
        {
            foreach (var list in _messages.Values)
                foreach (var msg in list)
                    if (msg.Severity == Severity.Error)
                        return false;
            foreach (var list in _externalMessages.Values)
                foreach (var msg in list)
                    if (msg.Severity == Severity.Error)
                        return false;
            return true;
        }
    }

    /// <summary>
    /// Returns the names of all fields that have at least one Error-severity message.
    /// </summary>
    public IReadOnlyList<string> InvalidFields
    {
        get
        {
            lock (_lock)
            {
                var fields = new HashSet<string>();
                foreach (var (@field, list) in _messages)
                    foreach (var msg in list)
                        if (msg.Severity == Severity.Error)
                        { fields.Add(@field); break; }
                foreach (var (@field, list) in _externalMessages)
                    foreach (var msg in list)
                        if (msg.Severity == Severity.Error)
                        { fields.Add(@field); break; }
                return fields.ToList();
            }
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Touched state
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns true if the field has been focused and then blurred.
    /// </summary>
    public bool IsTouched(string field)
    {
        lock (_lock) return _touchedFields.Contains(field);
    }

    /// <summary>
    /// Marks a field as touched (e.g., after focus+blur).
    /// </summary>
    public void MarkTouched(string field)
    {
        lock (_lock)
        {
            if (_touchedFields.Add(field))
                _version++;
        }
    }

    /// <summary>
    /// Marks all registered fields as touched. Typically called on form submit.
    /// </summary>
    public void MarkAllTouched()
    {
        lock (_lock)
        {
            foreach (var field in _registeredFields)
                _touchedFields.Add(field);
            _version++;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Dirty state
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Stores the initial value for a field. Called at field registration time.
    /// </summary>
    public void SetInitialValue(string field, object? value)
    {
        lock (_lock)
        {
            _initialValues[field] = value;
            _currentValues[field] = value;
        }
    }

    /// <summary>
    /// Notifies the context of a field value change. Clears external messages for this field.
    /// </summary>
    public void NotifyValueChanged(string field, object? value)
    {
        lock (_lock)
        {
            _currentValues[field] = value;
            // External messages clear on field value change
            if (_externalMessages.Remove(field))
                _version++;
        }
    }

    /// <summary>
    /// Returns true if the field's current value differs from its initial value.
    /// </summary>
    public bool IsDirty(string field)
    {
        lock (_lock)
        {
            if (!_initialValues.TryGetValue(field, out var initial))
                return false;
            if (!_currentValues.TryGetValue(field, out var current))
                return false;
            return !Equals(initial, current);
        }
    }

    /// <summary>
    /// Returns true if any registered field is dirty.
    /// </summary>
    public bool IsDirty()
    {
        lock (_lock)
        {
            foreach (var field in _registeredFields)
            {
                if (_initialValues.TryGetValue(field, out var initial)
                    && _currentValues.TryGetValue(field, out var current)
                    && !Equals(initial, current))
                    return true;
            }
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Reset
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resets a field to its initial value, clears touched state and all messages.
    /// Returns the initial value so the caller can update the UI.
    /// </summary>
    public object? Reset(string field)
    {
        lock (_lock)
        {
            _touchedFields.Remove(field);
            _messages.Remove(field);
            _externalMessages.Remove(field);
            _initialValues.TryGetValue(field, out var initial);
            _currentValues[field] = initial;
            _version++;
            return initial;
        }
    }

    /// <summary>
    /// Resets all fields to their initial values, clears all touched states and messages.
    /// Returns a dictionary of field → initial value.
    /// </summary>
    public IReadOnlyDictionary<string, object?> ResetAll()
    {
        lock (_lock)
        {
            _touchedFields.Clear();
            _messages.Clear();
            _externalMessages.Clear();
            var result = new Dictionary<string, object?>();
            foreach (var (field, initial) in _initialValues)
            {
                _currentValues[field] = initial;
                result[field] = initial;
            }
            _version++;
            return result;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════════

    private bool HasSeverity(string field, Severity severity)
    {
        if (_messages.TryGetValue(field, out var internal_))
            foreach (var msg in internal_)
                if (msg.Severity == severity)
                    return true;
        if (_externalMessages.TryGetValue(field, out var external))
            foreach (var msg in external)
                if (msg.Severity == severity)
                    return true;
        return false;
    }

    private static void CheckSeverity(string field,
        Dictionary<string, List<ValidationMessage>> store,
        ref Severity? highest)
    {
        if (!store.TryGetValue(field, out var list)) return;
        foreach (var msg in list)
        {
            if (highest is null || msg.Severity > highest.Value)
                highest = msg.Severity;
            if (highest == Severity.Error)
                return; // can't go higher
        }
    }
}
