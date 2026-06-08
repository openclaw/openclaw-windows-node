using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenClaw.Shared.Markdown;
using Xunit;

namespace OpenClaw.Shared.Tests.Markdown;

/// <summary>
/// Guard tests for the <see cref="MdInline"/> equality contract that
/// <c>ChatMarkdownRenderer</c>'s inline cache depends on.
///
/// <para>
/// The renderer short-circuits rebuilding <c>TextBlock.Inlines</c> when the
/// new inline list is value-equal to the previously rendered one (via
/// record-generated <c>Equals</c> + <c>SequenceEqual</c>). That short-circuit
/// is what preserves the user's text selection across pointer events and
/// streaming token updates. It is sound only while every concrete
/// <see cref="MdInline"/> subtype contains exclusively value-comparable
/// members (primitives, <c>string</c>, enums) — record-generated equality
/// compares reference-typed members by reference, which would silently
/// break cache correctness.
/// </para>
///
/// <para>
/// If a new <c>MdInline</c> subtype is introduced (or an existing one gains
/// a member) that violates this invariant, these tests fail and the
/// renderer's equality strategy must be updated before the change ships.
/// </para>
/// </summary>
public class MdInlineEqualityTests
{
    private static readonly HashSet<Type> AllowedMemberTypes = new()
    {
        typeof(string),
        typeof(bool),
        typeof(int),
        typeof(long),
        typeof(double),
        typeof(MdColumnAlignment),
    };

    private static IEnumerable<Type> ConcreteMdInlineTypes()
        => typeof(MdInline).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && typeof(MdInline).IsAssignableFrom(t));

    [Fact]
    public void KnownConcreteSubtypes_AreExactlyTheExpectedSet()
    {
        var actual = ConcreteMdInlineTypes()
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var expected = new[] { nameof(MdInlineLineBreak), nameof(MdInlineText) };

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EveryMdInlineSubtype_OnlyHasValueComparableMembers()
    {
        foreach (var type in ConcreteMdInlineTypes())
        {
            var properties = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.DeclaringType != typeof(MdInline)
                            && p.Name != "EqualityContract");

            foreach (var prop in properties)
            {
                var pt = prop.PropertyType;
                var underlying = Nullable.GetUnderlyingType(pt) ?? pt;

                var ok = AllowedMemberTypes.Contains(underlying)
                         || underlying.IsEnum
                         || underlying.IsPrimitive;

                Assert.True(
                    ok,
                    $"{type.Name}.{prop.Name} has type {pt.FullName}, which is not " +
                    "value-comparable for record equality. ChatMarkdownRenderer's " +
                    "inline cache relies on deep value equality of MdInline lists — " +
                    "adding a reference-typed member silently breaks selection " +
                    "preservation. Update the renderer's equality strategy before " +
                    "extending MdInline this way.");
            }
        }
    }

    [Fact]
    public void SequenceEqual_OnEquivalentFreshLists_ReturnsTrue()
    {
        IReadOnlyList<MdInline> a = new MdInline[]
        {
            new MdInlineText("hello", IsStrong: true),
            new MdInlineLineBreak(IsHard: false),
            new MdInlineText(" world", IsEmphasis: true),
        };

        IReadOnlyList<MdInline> b = new MdInline[]
        {
            new MdInlineText("hello", IsStrong: true),
            new MdInlineLineBreak(IsHard: false),
            new MdInlineText(" world", IsEmphasis: true),
        };

        Assert.False(ReferenceEquals(a, b));
        Assert.True(a.SequenceEqual(b));
    }

    [Fact]
    public void SequenceEqual_OnDifferingContent_ReturnsFalse()
    {
        IReadOnlyList<MdInline> a = new MdInline[]
        {
            new MdInlineText("hello"),
        };

        IReadOnlyList<MdInline> b = new MdInline[]
        {
            new MdInlineText("hello!"),
        };

        Assert.False(a.SequenceEqual(b));
    }
}
