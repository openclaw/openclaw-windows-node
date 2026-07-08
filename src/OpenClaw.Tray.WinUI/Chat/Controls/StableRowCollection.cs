using System.Collections.ObjectModel;

namespace OpenClawTray.Chat.Controls;

internal static class StableRowCollection
{
    public static void Sync<T>(
        ObservableCollection<T> current,
        IReadOnlyList<T> desired,
        Func<T, string> keySelector)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(keySelector);

        var desiredKeys = desired.Select(keySelector).ToHashSet(StringComparer.Ordinal);
        for (var index = current.Count - 1; index >= 0; index--)
        {
            if (!desiredKeys.Contains(keySelector(current[index])))
                current.RemoveAt(index);
        }

        for (var desiredIndex = 0; desiredIndex < desired.Count; desiredIndex++)
        {
            var desiredItem = desired[desiredIndex];
            var desiredKey = keySelector(desiredItem);
            if (desiredIndex < current.Count &&
                string.Equals(keySelector(current[desiredIndex]), desiredKey, StringComparison.Ordinal))
            {
                continue;
            }

            var existingIndex = IndexOfKey(current, desiredKey, desiredIndex + 1, keySelector);
            if (existingIndex >= 0)
                current.Move(existingIndex, desiredIndex);
            else
                current.Insert(desiredIndex, desiredItem);
        }
    }

    private static int IndexOfKey<T>(
        ObservableCollection<T> current,
        string key,
        int startIndex,
        Func<T, string> keySelector)
    {
        for (var index = Math.Max(0, startIndex); index < current.Count; index++)
        {
            if (string.Equals(keySelector(current[index]), key, StringComparison.Ordinal))
                return index;
        }

        return -1;
    }
}
