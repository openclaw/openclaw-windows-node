namespace OpenClawTray.Presentation;

internal static class WorkspaceItemsSync
{
    /// <summary>
    /// Places <paramref name="desired"/> at items[start..start+desired.Count) in order,
    /// moving only out-of-place objects so reused WinUI containers (and the selected
    /// NavigationViewItem) keep their identity. Callers remove stale items first.
    /// </summary>
    public static void Arrange(IList<object> items, int start, IReadOnlyList<object> desired)
    {
        for (var i = 0; i < desired.Count; i++)
        {
            var index = start + i;
            if (index < items.Count && ReferenceEquals(items[index], desired[i]))
                continue;
            items.Remove(desired[i]);
            items.Insert(index, desired[i]);
        }
    }
}
