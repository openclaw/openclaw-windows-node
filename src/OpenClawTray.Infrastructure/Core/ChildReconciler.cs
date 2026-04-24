using Microsoft.UI.Xaml;
using WinUI = Microsoft.UI.Xaml.Controls;

namespace OpenClawTray.Infrastructure.Core;

// AI-HINT: React-like child list diffing. Two strategies:
//   Unkeyed: O(n) positional match by index — simple but can't detect reorders.
//   Keyed:   4-phase algorithm — (1) common prefix, (2) common suffix,
//            (3) pure insert/remove middle, (4) LIS-based minimal moves.
//   LIS finds the longest subsequence of old children that are already in order
//   in the new list; only children NOT in the LIS need to be moved.
//   UnmountAndPool returns controls to ElementPool for reuse.

/// <summary>
/// Keyed child reconciliation using Longest Increasing Subsequence (LIS).
///
/// Strategies:
///   1. Unkeyed children: positional reconciliation (match by index)
///   2. Keyed children: prefix/suffix stripping + LIS for minimal moves
/// </summary>
internal static class ChildReconciler
{
    /// <summary>
    /// Reconciles old and new child element arrays against a Panel's Children collection.
    /// </summary>
    internal static void Reconcile(
        Element[] oldChildren,
        Element[] newChildren,
        IChildCollection children,
        Reconciler reconciler,
        Action requestRerender)
    {
        // Filter out nulls and EmptyElements
        var oldFiltered = Filter(oldChildren);
        var newFiltered = Filter(newChildren);

        bool hasKeys = HasAnyKeys(oldFiltered) || HasAnyKeys(newFiltered);

        if (hasKeys)
            ReconcileKeyed(oldFiltered, newFiltered, children, reconciler, requestRerender);
        else
            ReconcilePositional(oldFiltered, newFiltered, children, reconciler, requestRerender);
    }

    /// <summary>
    /// Positional reconciliation: match children by index.
    /// O(max(old, new)) — no reorder detection, but simple and fast.
    /// </summary>
    private static void ReconcilePositional(
        Element[] oldChildren,
        Element[] newChildren,
        IChildCollection children,
        Reconciler reconciler,
        Action requestRerender)
    {
        int childCount = children.Count; // cache to avoid repeated COM calls
        int common = Math.Min(oldChildren.Length, newChildren.Length);

        // Update common children in place
        for (int i = 0; i < common; i++)
        {
            if (i >= childCount) break;

            // Early skip: if the element is structurally identical (and has no
            // theme bindings that need re-evaluation), we can avoid the expensive
            // children.Get(i) COM call entirely. This saves ~2 COM roundtrips per
            // unchanged child (IVector.GetAt + all the property diffing inside Update).
            var oldEl = oldChildren[i];
            var newEl = newChildren[i];
            if (Element.CanSkipUpdate(oldEl, newEl))
            {
                reconciler.DebugElementsSkipped++;
                continue;
            }

            if (reconciler.CanUpdate(oldEl, newEl))
            {
                var existingControl = children.Get(i);
                var replacement = reconciler.UpdateChild(oldEl, newEl, existingControl, requestRerender);
                if (replacement is not null)
                {
                    // Child type changed at runtime — replace in place
                    reconciler.UnmountChild(existingControl);
                    children.Replace(i, replacement);
                }
            }
            else
            {
                // Type mismatch — mount new, replace old with exit transition support
                var newControl = reconciler.Mount(newEl, requestRerender);
                if (newControl is not null)
                    reconciler.ReplaceChildWithExitTransition(children, i, newControl);
                else
                {
                    reconciler.UnmountChild(children.Get(i));
                }
            }
        }

        // Remove excess old children (from end to start to keep indices stable).
        // Use live children.Count — NOT the cached childCount — because
        // ReplaceChildWithExitTransition in the common loop may have re-inserted
        // elements for exit animation, increasing the actual count.
        for (int i = children.Count - 1; i >= common; i--)
        {
            reconciler.RemoveChildWithExitTransition(children, i);
        }

        // Insert new children beyond old count
        for (int i = common; i < newChildren.Length; i++)
        {
            var ctrl = reconciler.Mount(newChildren[i], requestRerender);
            if (ctrl is not null)
                children.Insert(children.Count, ctrl);
        }
    }

    /// <summary>
    /// Keyed reconciliation using prefix/suffix stripping + LIS.
    /// Minimizes DOM operations for reordered lists.
    /// </summary>
    private static void ReconcileKeyed(
        Element[] oldChildren,
        Element[] newChildren,
        IChildCollection children,
        Reconciler reconciler,
        Action requestRerender)
    {
        int oldLen = oldChildren.Length;
        int newLen = newChildren.Length;

        // Phase 1: Common prefix
        int prefixLen = 0;
        while (prefixLen < oldLen && prefixLen < newLen &&
               KeyMatch(oldChildren[prefixLen], newChildren[prefixLen]) &&
               reconciler.CanUpdate(oldChildren[prefixLen], newChildren[prefixLen]))
        {
            // Update in place
            if (prefixLen < children.Count)
            {
                var replacement = reconciler.UpdateChild(oldChildren[prefixLen], newChildren[prefixLen], children.Get(prefixLen), requestRerender);
                if (replacement is not null)
                {
                    reconciler.UnmountChild(children.Get(prefixLen));
                    children.Replace(prefixLen, replacement);
                }
            }
            prefixLen++;
        }

        // Phase 2: Common suffix
        int suffixLen = 0;
        while (suffixLen < (oldLen - prefixLen) && suffixLen < (newLen - prefixLen) &&
               KeyMatch(oldChildren[oldLen - 1 - suffixLen], newChildren[newLen - 1 - suffixLen]) &&
               reconciler.CanUpdate(oldChildren[oldLen - 1 - suffixLen], newChildren[newLen - 1 - suffixLen]))
        {
            // Update in place (from end)
            int oldIdx = oldLen - 1 - suffixLen;
            int panelIdx = children.Count - 1 - suffixLen;
            if (panelIdx >= 0 && panelIdx < children.Count)
            {
                var replacement = reconciler.UpdateChild(oldChildren[oldIdx], newChildren[newLen - 1 - suffixLen], children.Get(panelIdx), requestRerender);
                if (replacement is not null)
                {
                    reconciler.UnmountChild(children.Get(panelIdx));
                    children.Replace(panelIdx, replacement);
                }
            }
            suffixLen++;
        }

        // Phase 3: Middle section
        int oldStart = prefixLen;
        int oldEnd = oldLen - suffixLen;
        int newStart = prefixLen;
        int newEnd = newLen - suffixLen;

        int oldMidLen = oldEnd - oldStart;
        int newMidLen = newEnd - newStart;

        if (oldMidLen == 0 && newMidLen == 0)
            return; // Prefix + suffix covered everything

        if (oldMidLen == 0)
        {
            // Only insertions
            for (int i = 0; i < newMidLen; i++)
            {
                var ctrl = reconciler.Mount(newChildren[newStart + i], requestRerender);
                if (ctrl is not null)
                    children.Insert(prefixLen + i, ctrl);
            }
            return;
        }

        if (newMidLen == 0)
        {
            // Only removals (from end to start)
            for (int i = oldMidLen - 1; i >= 0; i--)
            {
                int panelIdx = prefixLen + i;
                if (panelIdx < children.Count)
                {
                    reconciler.RemoveChildWithExitTransition(children, panelIdx);
                }
            }
            return;
        }

        // Middle section requires key mapping + LIS
        ReconcileKeyedMiddle(oldChildren, newChildren, oldStart, oldMidLen, newStart, newMidLen,
            prefixLen, suffixLen, children, reconciler, requestRerender);
    }

    /// <summary>
    /// Keyed middle section reconciliation using LIS for minimal moves.
    /// </summary>
    private static void ReconcileKeyedMiddle(
        Element[] oldChildren, Element[] newChildren,
        int oldStart, int oldMidLen, int newStart, int newMidLen,
        int prefixLen, int suffixLen,
        IChildCollection children,
        Reconciler reconciler,
        Action requestRerender)
    {
        // Build old key → index map
        var oldKeyMap = new Dictionary<string, int>(oldMidLen);
        for (int i = 0; i < oldMidLen; i++)
        {
            var key = GetKey(oldChildren[oldStart + i], oldStart + i);
            oldKeyMap[key] = i;
        }

        // Map new keys to old indices
        var newToOld = new int[newMidLen];
        var matched = new bool[oldMidLen];
        for (int i = 0; i < newMidLen; i++)
        {
            var key = GetKey(newChildren[newStart + i], newStart + i);
            if (oldKeyMap.TryGetValue(key, out int oldIdx) &&
                reconciler.CanUpdate(oldChildren[oldStart + oldIdx], newChildren[newStart + i]))
            {
                newToOld[i] = oldIdx;
                matched[oldIdx] = true;
            }
            else
            {
                newToOld[i] = -1;
            }
        }

        // Compute LIS on newToOld
        var lisIndices = ComputeLIS(newToOld);
        var inLIS = new HashSet<int>(lisIndices);

        // Step 1: Remove unmatched old items (reverse order for stable indices)
        for (int i = oldMidLen - 1; i >= 0; i--)
        {
            if (!matched[i])
            {
                int panelIdx = prefixLen + i;
                if (panelIdx < children.Count)
                {
                    reconciler.RemoveChildWithExitTransition(children, panelIdx);
                }
            }
        }

        // Build key→panel-index lookup for O(1) FindItemByOldIndex (CR-011).
        var keyToIndex = new Dictionary<string, int>();
        int searchEnd = children.Count - suffixLen;
        for (int i = prefixLen; i < searchEnd && i < children.Count; i++)
        {
            var child = children.Get(i);
            if (child is FrameworkElement fe && fe.Tag is Element tagElement)
            {
                var key = GetKey(tagElement, i);
                keyToIndex.TryAdd(key, i);
            }
        }

        // Step 2: Process new items - insert new, move existing not in LIS
        for (int i = 0; i < newMidLen; i++)
        {
            int targetPanelIdx = prefixLen + i;

            if (newToOld[i] == -1)
            {
                var ctrl = reconciler.Mount(newChildren[newStart + i], requestRerender);
                if (ctrl is not null)
                    children.Insert(targetPanelIdx, ctrl);
            }
            else if (inLIS.Contains(i))
            {
                if (targetPanelIdx < children.Count)
                {
                    var replacement = reconciler.UpdateChild(
                        oldChildren[oldStart + newToOld[i]],
                        newChildren[newStart + i],
                        children.Get(targetPanelIdx),
                        requestRerender);
                    if (replacement is not null)
                    {
                        reconciler.UnmountChild(children.Get(targetPanelIdx));
                        children.Replace(targetPanelIdx, replacement);
                    }
                }
            }
            else
            {
                int oldRelIdx = newToOld[i];
                int oldAbsIdx = oldStart + oldRelIdx;
                var lookupKey = oldAbsIdx < oldChildren.Length ? GetKey(oldChildren[oldAbsIdx], oldAbsIdx) : null;
                int currentPos = lookupKey != null && keyToIndex.TryGetValue(lookupKey, out var pos) ? pos : -1;
                if (currentPos >= 0 && currentPos != targetPanelIdx)
                {
                    children.Move(currentPos, targetPanelIdx);
                    // Update lookup: moved element is now at targetPanelIdx.
                    if (lookupKey != null) keyToIndex[lookupKey] = targetPanelIdx;
                }
                if (targetPanelIdx < children.Count)
                {
                    var replacement = reconciler.UpdateChild(
                        oldChildren[oldStart + oldRelIdx],
                        newChildren[newStart + i],
                        children.Get(targetPanelIdx),
                        requestRerender);
                    if (replacement is not null)
                    {
                        reconciler.UnmountChild(children.Get(targetPanelIdx));
                        children.Replace(targetPanelIdx, replacement);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Find the current position of an item that was originally at a given old index.
    /// Uses the reconciler's element map to match elements.
    /// </summary>
    private static int FindItemByOldIndex(
        IChildCollection children,
        Element[] oldElements,
        int oldIndex,
        int searchStart,
        int searchEnd,
        Reconciler reconciler)
    {
        for (int i = searchStart; i < searchEnd && i < children.Count; i++)
        {
            var child = children.Get(i);
            if (child is FrameworkElement fe && fe.Tag is Element tagElement)
            {
                if (oldIndex < oldElements.Length &&
                    GetKey(tagElement, -1) == GetKey(oldElements[oldIndex], oldIndex))
                    return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Compute Longest Increasing Subsequence.
    /// Returns indices into the input array that form the LIS.
    /// Skips entries with value -1 (unmapped items).
    /// O(n log n) using patience sorting.
    /// </summary>
    internal static HashSet<int> ComputeLIS(int[] arr)
    {
        if (arr.Length == 0) return new HashSet<int>();

        var tails = new List<int>();     // Smallest tail values
        var tailIndices = new List<int>(); // Indices in arr corresponding to tails
        var predecessors = new int[arr.Length];
        Array.Fill(predecessors, -1);

        for (int i = 0; i < arr.Length; i++)
        {
            if (arr[i] == -1) continue; // Skip unmapped

            int val = arr[i];

            // Binary search for insertion position
            int lo = 0, hi = tails.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (tails[mid] < val) lo = mid + 1;
                else hi = mid;
            }

            if (lo == tails.Count)
            {
                tails.Add(val);
                tailIndices.Add(i);
            }
            else
            {
                tails[lo] = val;
                tailIndices[lo] = i;
            }

            if (lo > 0)
                predecessors[i] = tailIndices[lo - 1];
        }

        // Backtrack to find actual LIS indices
        var result = new HashSet<int>();
        if (tailIndices.Count == 0) return result;

        int idx = tailIndices[^1];
        while (idx != -1)
        {
            result.Add(idx);
            idx = predecessors[idx];
        }

        return result;
    }

    private static Element[] Filter(Element[] elements)
    {
        // Fast path: if no filtering needed, return as-is
        bool needsFilter = false;
        for (int i = 0; i < elements.Length; i++)
        {
            if (elements[i] is null or EmptyElement) { needsFilter = true; break; }
        }
        if (!needsFilter) return elements;

        var result = new List<Element>(elements.Length);
        for (int i = 0; i < elements.Length; i++)
        {
            if (elements[i] is not null and not EmptyElement)
                result.Add(elements[i]);
        }
        return result.ToArray();
    }

    private static bool HasAnyKeys(Element[] elements)
    {
        for (int i = 0; i < elements.Length; i++)
        {
            if (elements[i].Key is not null) return true;
        }
        return false;
    }

    private static bool KeyMatch(Element a, Element b)
    {
        // Both must have the same key (or both null) AND same type
        if (a.GetType() != b.GetType()) return false;
        return a.Key == b.Key;
    }

    private static string GetKey(Element element, int positionalIndex)
    {
        // Use explicit key if available, otherwise fall back to type+position
        if (element.Key is not null) return element.Key;
        return $"__pos_{positionalIndex}_{element.GetType().Name}";
    }
}
