// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// <see cref="SwiftSet{Element}.AsProjected"/>: a set read as another element type keeps Swift's
/// membership. The projected element here is a class with reference equality, the shape of a wrapper
/// whose managed equality is not the conformance Swift hashes by, so a managed copy of the set would
/// find none of the elements Swift holds.
/// </summary>
public class SwiftSetProjectionTests
{
    // Reference equality only: two Labels of one value are different objects to .NET.
    private sealed class Label(nint value)
    {
        public nint Value { get; } = value;
    }

    private static SwiftSet<SwiftIntMock> Set(params nint[] values)
    {
        var set = new SwiftSet<SwiftIntMock>();
        foreach (var value in values)
            set.Add(new SwiftIntMock(value));
        return set;
    }

    private static IReadOnlySet<Label> View(SwiftSet<SwiftIntMock> set)
        => set.AsProjected(e => new Label(e.Value), l => new SwiftIntMock(l.Value));

    [Fact]
    public void Contains_FindsAnElementSwiftHoldsByItsOwnHashable()
    {
        var view = View(Set(1, 2, 3));

        Assert.True(view.Contains(new Label(2)));
        Assert.False(view.Contains(new Label(4)));
        // The managed copy a binding used to return misses it.
        Assert.DoesNotContain(new Label(2), view.ToHashSet());
    }

    [Fact]
    public void CountAndEnumeration_ReadEveryElementThroughTheSelector()
    {
        var view = View(Set(5, 6));

        Assert.Equal(2, view.Count);
        Assert.Equal(new nint[] { 5, 6 }, view.Select(l => l.Value).OrderBy(v => v));
    }

    [Fact]
    public void SetComparisons_MapEachItemBackAndAskSwift()
    {
        var view = View(Set(1, 2));

        Assert.True(view.SetEquals(new[] { new Label(2), new Label(1) }));
        Assert.False(view.SetEquals(new[] { new Label(1) }));
        Assert.True(view.IsSubsetOf(new[] { new Label(1), new Label(2), new Label(3) }));
        Assert.True(view.IsProperSubsetOf(new[] { new Label(1), new Label(2), new Label(3) }));
        Assert.False(view.IsProperSubsetOf(new[] { new Label(1), new Label(2) }));
        Assert.True(view.IsSupersetOf(new[] { new Label(1) }));
        Assert.True(view.IsProperSupersetOf(new[] { new Label(1) }));
        Assert.True(view.Overlaps(new[] { new Label(2), new Label(9) }));
        Assert.False(view.Overlaps(new[] { new Label(9) }));
    }

    [Fact]
    public void View_IsLiveAndDisposingItDisposesTheSet()
    {
        var set = Set(1);
        var view = View(set);
        set.Add(new SwiftIntMock(7));
        Assert.True(view.Contains(new Label(7)));

        ((IDisposable)view).Dispose();
        Assert.Throws<ObjectDisposedException>(() => set.Count);
    }

    [Fact]
    public void AsProjected_RefusesADisposedSetAndNullSelectors()
    {
        var set = Set(1);
        Assert.Throws<ArgumentNullException>(() => set.AsProjected<Label>(null!, l => new SwiftIntMock(l.Value)));
        Assert.Throws<ArgumentNullException>(() => set.AsProjected<Label>(e => new Label(e.Value), null!));
        set.Dispose();
        Assert.Throws<ObjectDisposedException>(() => View(set));
    }
}
