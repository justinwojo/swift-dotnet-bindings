// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// The element conversion of a Swift collection a binding builds from a C# sequence
/// (<c>FromEnumerable</c>, <c>FromDictionary</c>). The builder copies each converted element into Swift
/// storage as it reads it, so the conversion runs lazily, one element ahead of nothing:
/// <list type="bullet">
/// <item>An element that converts to an Objective-C object's bare pointer (<see cref="ObjCBridgedProjection"/>)
/// holds no managed reference, so a sequence that creates each object could let it be finalized before
/// Swift copies the pointer. The conversion keeps each source element reachable until then
/// (<c>SwiftCollectionSource.Borrowing</c>).</item>
/// <item>An element that converts to a Swift collection built only to be copied in (the inner
/// collection of a nested one), or to the optional of one, is disposed once copied
/// (<c>SwiftCollectionSource.Consuming</c>, or <c>ConsumingValues</c> for a dictionary's values), so it
/// does not hold its references until its finalizer runs.</item>
/// <item>Anything else is a plain <c>Select</c>.</item>
/// </list>
/// </summary>
internal static class CollectionElementSelect
{
    private const string Source = "global::Swift.Runtime.SwiftCollectionSource";

    /// <summary>
    /// The converted sequence of an array's or set's elements.
    /// </summary>
    /// <param name="source">The C# expression of the source sequence.</param>
    /// <param name="selector">The conversion lambda, e.g. <c>e => …</c>.</param>
    /// <param name="element">The projection that converts each element.</param>
    public static string Elements(string source, string selector, ITypeProjection element)
    {
        if (BuildsDisposableTemporary(element))
            return $"{Source}.Consuming({source}, {selector})";
        return Pairs(source, selector, element);
    }

    /// <summary>
    /// The converted sequence of a dictionary's pairs whose values are Swift collections built only
    /// to be copied in (<c>[String: [NSUUID]]</c>, <c>[String: [Int]?]</c>): each value is disposed once
    /// the dictionary copied it (<c>SwiftCollectionSource.ConsumingValues</c>), and every source pair
    /// stays reachable until then. A pair whose value builds nothing disposable converts as
    /// <see cref="Pairs"/> does.
    /// </summary>
    /// <param name="source">The C# expression of the source sequence.</param>
    /// <param name="selector">The conversion lambda.</param>
    /// <param name="key">The key's projection.</param>
    /// <param name="value">The value's projection.</param>
    public static string ConsumedPairs(string source, string selector, ITypeProjection key, ITypeProjection value)
        => BuildsDisposableTemporary(value)
            ? $"{Source}.ConsumingValues({source}, {selector})"
            : Pairs(source, selector, key, value);

    /// <summary>
    /// The converted sequence of a dictionary's pairs (or of any element whose conversion builds
    /// nothing disposable).
    /// </summary>
    /// <param name="source">The C# expression of the source sequence.</param>
    /// <param name="selector">The conversion lambda.</param>
    /// <param name="slots">The projections whose conversions the lambda applies (a key and a value).</param>
    public static string Pairs(string source, string selector, params ITypeProjection[] slots) =>
        slots.Any(slot => slot is ObjCBridgedProjection or OptionalProjection { ObjectCarrier: not null })
            ? $"{Source}.Borrowing({source}, {selector})"
            : $"global::System.Linq.Enumerable.Select({source}, {selector})";

    /// <summary>
    /// Whether the conversion of <paramref name="slot"/> builds a Swift value C# owns only until Swift
    /// copies it: a Swift collection (not an Objective-C container bridge), or the tagged optional
    /// (<c>SwiftOptional&lt;T&gt;</c>) of one.
    /// </summary>
    internal static bool BuildsDisposableTemporary(ITypeProjection slot) => slot switch
    {
        ArrayProjection or SetProjection or DictionaryProjection => !slot.UsesObjCContainerBridge,
        OptionalProjection { ObjectCarrier: null, IsExistentialInner: false } optional
            => optional.InnerProjection is ArrayProjection or SetProjection or DictionaryProjection
               && !optional.InnerProjection.UsesObjCContainerBridge,
        _ => false,
    };
}
