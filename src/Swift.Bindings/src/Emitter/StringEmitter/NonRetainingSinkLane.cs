// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Answers who roots the existential carrier built for a property setter's value — a question
/// about the SINK the value is stored into, not about the wire shape the value travels in.
///
/// <para>Swift <c>weak</c>/<c>unowned</c> storage takes no reference on the conformer box behind an
/// existential. When the value is a plain C# implementation the binding auto-wraps into the
/// generated proxy, the proxy's construction <c>+1</c> is the box's only reference, and it is held
/// only by that freshly built proxy: the next collection finalizes the proxy, releases the
/// <c>+1</c>, the box deinitializes, and the Swift slot zeroes (<c>weak</c>) or dangles
/// (<c>unowned</c>) while the consumer still holds their implementation and expects callbacks.
/// Routing those setters through the consumer-owned lane keys the carrier's memo on the
/// implementation and makes the proxy hold the implementation strongly, so the carrier lives for
/// exactly as long as the implementation does — and going away with it is what lets the Swift slot
/// clear the way the declaration promises.</para>
///
/// <para>Every arm that auto-wraps an implementation for a setter asks this one question, so the
/// answer cannot drift between the shapes: the decomposed-Optional carrier, the bare
/// <c>@objc</c> object pointer, the wrapper-library existential argument and the direct-dispatch
/// existential argument are four widths of the same store. Only the auto-wrap arm needs the lane —
/// a value that already IS a carrier (a Swift-vended proxy, an already-marshalled container) is
/// held by the consumer directly, and a boxable value conformer owns its own fresh <c>+1</c>.</para>
/// </summary>
internal static class NonRetainingSinkLane
{
    /// <summary>
    /// The proxy-constructor argument that puts the generated proxy in its consumer-owned mode
    /// (holding the implementation strongly), appended to the auto-wrap fallback's
    /// <c>new {Protocol}Proxy(__v)</c>. Empty on the default lane.
    /// </summary>
    internal static string ProxyOwnershipArgument(bool consumerOwnsCarrier)
        => consumerOwnsCarrier ? ", global::Swift.Runtime.ProxyImplOwnership.ConsumerOwned" : string.Empty;

    /// <summary>
    /// The <c>ExistentialContainerFactory</c> entry point for the chosen lane. The consumer-owned
    /// entry point only exists for the auto-wrap fallback, so a site without a proxy class stays on
    /// the default one: with nothing to wrap there is no carrier for the lane to re-root.
    /// </summary>
    internal static string FactoryMethodName(bool consumerOwnsCarrier, bool hasProxyClass)
        => consumerOwnsCarrier && hasProxyClass ? "GetOrCreateConsumerOwned" : "GetOrCreate";

    /// <summary>
    /// True when <paramref name="argumentDecl"/> is the value of <paramref name="methodDecl"/>'s
    /// setter AND that setter writes non-retaining storage. Keyed on the accessor's recorded sink
    /// ownership so a marshalling arm never has to reach back to the owning property, and scoped to
    /// the value argument so a subscript's indices — ordinary borrowed arguments sitting beside it —
    /// keep the default lane.
    /// </summary>
    internal static bool ConsumerOwnsCarrier(MethodDecl methodDecl, ArgumentDecl argumentDecl)
        => methodDecl.SinkReferenceOwnership != SwiftReferenceOwnership.Strong
           && CalleeArgumentOwnership.IsSetterNewValue(methodDecl, argumentDecl);

    /// <summary>
    /// The same question asked from the property side, for an arm that writes the setter body
    /// itself and therefore already holds the declaration. It reads the identical fact the accessor
    /// overload reads — the accessor's value is propagated from this one — so the two cannot
    /// disagree about a given property.
    /// </summary>
    internal static bool ConsumerOwnsCarrier(PropertyDecl propertyDecl)
        => propertyDecl.ReferenceOwnership != SwiftReferenceOwnership.Strong;
}
