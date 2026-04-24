using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace OpenClawTray.Infrastructure.Controls;

/// <summary>
/// Provides add/remove/reorder operations for array/list properties.
/// Works with IList for runtime polymorphism over List&lt;T&gt; and T[].
/// </summary>
internal static class ArrayOperations
{
    /// <summary>
    /// Adds an item to the end of the list. For arrays, returns a new array.
    /// </summary>
    [RequiresDynamicCode("Array.CreateInstance requires dynamic code for array creation at runtime.")]
    public static object Add(object collection, object item, Type elementType)
    {
        if (collection is IList list && !collection.GetType().IsArray)
        {
            list.Add(item);
            return collection;
        }

        // Array — create a new array with the item appended
        if (collection is Array array)
        {
            var newArray = Array.CreateInstance(elementType, array.Length + 1);
            Array.Copy(array, newArray, array.Length);
            newArray.SetValue(item, array.Length);
            return newArray;
        }

        throw new InvalidOperationException($"Cannot add to {collection.GetType().Name}");
    }

    /// <summary>
    /// Removes an item at the given index. For arrays, returns a new array.
    /// </summary>
    [RequiresDynamicCode("Array.CreateInstance requires dynamic code for array creation at runtime.")]
    public static object RemoveAt(object collection, int index, Type elementType)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be non-negative.");

        if (collection is IList list && !collection.GetType().IsArray)
        {
            if (index >= list.Count)
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must be less than the collection size ({list.Count}).");
            list.RemoveAt(index);
            return collection;
        }

        if (collection is Array array)
        {
            if (index >= array.Length)
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must be less than the array length ({array.Length}).");
            var newArray = Array.CreateInstance(elementType, array.Length - 1);
            if (index > 0)
                Array.Copy(array, 0, newArray, 0, index);
            if (index < array.Length - 1)
                Array.Copy(array, index + 1, newArray, index, array.Length - index - 1);
            return newArray;
        }

        throw new InvalidOperationException($"Cannot remove from {collection.GetType().Name}");
    }

    /// <summary>
    /// Moves an item up (swap with previous).
    /// </summary>
    public static object MoveUp(object collection, int index, Type elementType)
    {
        if (index <= 0) return collection;

        if (collection is IList list && !collection.GetType().IsArray)
        {
            var item = list[index];
            list.RemoveAt(index);
            list.Insert(index - 1, item);
            return collection;
        }

        if (collection is Array array)
        {
            var newArray = (Array)array.Clone();
            var temp = newArray.GetValue(index);
            newArray.SetValue(newArray.GetValue(index - 1), index);
            newArray.SetValue(temp, index - 1);
            return newArray;
        }

        return collection;
    }

    /// <summary>
    /// Moves an item down (swap with next).
    /// </summary>
    public static object MoveDown(object collection, int index, Type elementType)
    {
        if (collection is IList list && !collection.GetType().IsArray)
        {
            if (index >= list.Count - 1) return collection;
            var item = list[index];
            list.RemoveAt(index);
            list.Insert(index + 1, item);
            return collection;
        }

        if (collection is Array array)
        {
            if (index >= array.Length - 1) return collection;
            var newArray = (Array)array.Clone();
            var temp = newArray.GetValue(index);
            newArray.SetValue(newArray.GetValue(index + 1), index);
            newArray.SetValue(temp, index + 1);
            return newArray;
        }

        return collection;
    }

    public static int GetCount(object collection)
    {
        if (collection is ICollection c) return c.Count;
        if (collection is Array a) return a.Length;
        return 0;
    }

    public static object? GetItem(object collection, int index)
    {
        if (collection is IList list) return list[index];
        if (collection is Array array) return array.GetValue(index);
        return null;
    }

    public static Type? GetElementType(Type collectionType)
    {
        if (collectionType.IsArray)
            return collectionType.GetElementType();

        if (collectionType.IsGenericType)
        {
            var genDef = collectionType.GetGenericTypeDefinition();
            if (genDef == typeof(List<>) || genDef == typeof(IList<>))
                return collectionType.GetGenericArguments()[0];
        }

        return null;
    }
}
