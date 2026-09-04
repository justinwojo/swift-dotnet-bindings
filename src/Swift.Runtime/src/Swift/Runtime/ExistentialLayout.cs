// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.ComponentModel;

namespace Swift.Runtime;

/// <summary>
/// Load-time tripwire for the existential-container layout a generated protocol proxy builds.
/// </summary>
/// <remarks>
/// <para>
/// A Swift protocol existential (<c>any P</c>) has two shapes, and the generated proxy must fill
/// the wire words for the shape Swift actually expects:
/// </para>
/// <list type="bullet">
/// <item><description><b>Opaque</b> — five words: three inline payload words, the payload's type
/// metadata, then the protocol witness table. The proxy writes the witness table into the last
/// word and the helper class's metadata into the metadata word.</description></item>
/// <item><description><b>Class-bound</b> (<c>: AnyObject</c>, or rooted at an Objective-C
/// protocol) — two words: the class reference then the witness table. A pure <c>@objc</c>
/// protocol narrows further to a single bare object pointer, because dispatch runs through the
/// Objective-C selector table and there is no Swift witness table at all.</description></item>
/// </list>
/// <para>
/// The generator picks the shape at emission time from parsed ABI facts. When those facts
/// mis-classify a class-bound protocol as opaque, the proxy writes the witness table into word 4
/// while Swift reads it from word 1 — Swift then dispatches through a null witness table and
/// segfaults inside the framework on the first callback, with no managed frame and no diagnostic.
/// The Swift wrapper knows the truth (<c>MemoryLayout&lt;any P&gt;.size</c>), so each proxy asks
/// it once, lazily, when it first resolves its witness table, and compares the answer against the
/// size its own layout choice implies. A mismatch throws here — a loud, named, actionable failure
/// at the boundary instead of a silent memory-safety violation deep in Swift.
/// </para>
/// <para>
/// The comparison is deliberately about which <em>arm</em> was chosen, not the exact word count:
/// the class-bound arm accepts both the 2-word Swift shape and the 1-word <c>@objc</c> shape,
/// because the proxy writes the class reference into word 0 and the witness table into word 1 in
/// both cases, and a reader that consumes only word 0 reads exactly what it needs. Confusing the
/// opaque arm with either class-bound shape (in either direction) is the failure this rejects.
/// </para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ExistentialLayout
{
    /// <summary>
    /// Size of an opaque protocol existential: three inline payload words + type metadata +
    /// witness table.
    /// </summary>
    public static readonly int OpaqueSize = 5 * IntPtr.Size;

    /// <summary>
    /// Size of a class-bound Swift protocol existential: class reference + witness table.
    /// </summary>
    public static readonly int ClassBoundSize = 2 * IntPtr.Size;

    /// <summary>
    /// Size of a pure <c>@objc</c> protocol existential: a bare Objective-C object pointer, with
    /// no Swift witness-table word (dispatch runs through the selector table). Accepted wherever
    /// <see cref="ClassBoundSize"/> is expected — see the remarks on <see cref="ExistentialLayout"/>.
    /// </summary>
    public static readonly int ObjCSize = IntPtr.Size;

    /// <summary>
    /// Verifies that the existential size the Swift wrapper reports for <paramref name="protocolName"/>
    /// agrees with the container layout the generator emitted for it.
    /// </summary>
    /// <param name="protocolName">The Swift protocol name, for the diagnostic.</param>
    /// <param name="expectedSize">
    /// The size implied by the generator's container-layout choice: <see cref="OpaqueSize"/> for
    /// the opaque arm, <see cref="ClassBoundSize"/> for the class-bound arm.
    /// </param>
    /// <param name="reportedSize">
    /// <c>MemoryLayout&lt;any P&gt;.size</c> as reported by the Swift wrapper accessor.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The two disagree on which existential shape the protocol has. The binding was generated
    /// from ABI facts that mis-classify this protocol, and using it would hand Swift a container
    /// whose witness-table word is in the wrong place.
    /// </exception>
    public static void Verify(string protocolName, int expectedSize, int reportedSize)
    {
        if (reportedSize == expectedSize)
            return;

        // The class-bound arm covers both the 2-word Swift shape and the 1-word @objc shape; the
        // proxy fills word 0 (class reference) and word 1 (witness table) either way, so a reader
        // that consumes only word 0 is served correctly.
        if (expectedSize == ClassBoundSize && reportedSize == ObjCSize)
            return;

        throw new InvalidOperationException(
            $"Existential layout mismatch for the Swift protocol '{protocolName}': this binding builds a "
            + $"{DescribeLayout(expectedSize)} existential container ({expectedSize} bytes), but the Swift library "
            + $"reports MemoryLayout<any {protocolName}>.size == {reportedSize} bytes "
            + $"({DescribeLayout(reportedSize)}). Handing Swift this container would place the protocol witness "
            + "table in the wrong word, so the first callback into it would read a null witness table and crash "
            + "inside the framework. Regenerate this binding with a newer SwiftBindings SDK; if it is already "
            + "current, the protocol's class-boundedness is being mis-parsed and the binding cannot be used safely.");
    }

    /// <summary>
    /// Builds the exception for a wrapper that resolved the protocol's witness table but exports no
    /// existential-size accessor — a wrapper built by an older SDK than the C# binding beside it.
    /// The check is fail-closed: a missing accessor is reported, never treated as agreement.
    /// </summary>
    /// <param name="protocolName">The Swift protocol name, for the diagnostic.</param>
    /// <param name="inner">The <see cref="EntryPointNotFoundException"/> the accessor call raised.</param>
    /// <returns>The exception to throw at the call site.</returns>
    public static InvalidOperationException MissingSizeAccessor(string protocolName, Exception inner) =>
        new InvalidOperationException(
            $"The Swift wrapper for the protocol '{protocolName}' exports no existential-size accessor, so the "
            + "existential-container layout this binding builds cannot be verified against the layout Swift "
            + "expects. The wrapper library is older than the generated C# beside it. Rebuild the Swift wrapper "
            + "and the binding together with the same SwiftBindings SDK.",
            inner);

    private static string DescribeLayout(int size)
    {
        if (size == OpaqueSize)
            return "5-word opaque";
        if (size == ClassBoundSize)
            return "2-word class-bound";
        if (size == ObjCSize)
            return "1-word @objc class-bound";
        return $"{size}-byte unrecognized";
    }
}
