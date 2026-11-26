// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// The managed wrapper that owns a bridged ObjC collection (NSArray / NSDictionary / NSSet) for
/// the duration of a call. <see cref="Setup"/> ends in a <see cref="MarshalStatement.Using"/>
/// declaring <see cref="Name"/>, so the wrapper — and the handle read off it — live until the
/// emitting method returns, never shorter. Any plan that copies the handle out of this wrapper
/// must keep the statements at method scope: a handle copied out of a narrower block is a bare
/// pointer to an object the block's disposal has already released.
/// </summary>
public sealed record ObjCContainerBridgeOwner(IReadOnlyList<MarshalStatement> Setup, string Name)
{
    /// <summary>
    /// Builds the owning declaration from the expression that constructs the collection out of a
    /// non-null source. For a nullable source the construction is guarded on the source itself, so
    /// the wrapper is null (and its disposal a no-op) exactly when the value is absent.
    /// </summary>
    public static MarshalStatement.Using Declare(
        string wrapperType, string name, string construct, string sourceExpression, bool sourceIsNullable)
        => sourceIsNullable
            ? new MarshalStatement.Using($"{wrapperType}?", name, $"{sourceExpression} is null ? null : {construct}")
            : new MarshalStatement.Using(wrapperType, name, construct);

    /// <summary>
    /// An expression that builds the collection <paramref name="construct"/> makes and hands Swift
    /// its own +1 reference to it: the new wrapper is held by a local until the retain has been
    /// taken, then released. Reading the handle straight off the new wrapper
    /// (<c>Retain(construct.Handle)</c>) leaves the wrapper unreachable before the retain, so a
    /// collection the GC finalizes in between is freed before Swift retains it.
    /// </summary>
    public static string RetainedHandle(string construct)
        => "((global::System.Func<global::System.IntPtr>)(() => { " +
           $"using var __collection = {construct}; " +
           "return global::Swift.Runtime.Arc.UnknownObjectRetain(__collection.Handle); }))()";

    /// <summary>
    /// A new <c>NSSet</c> of the Objective-C objects in <paramref name="elementArrayExpr"/> (an
    /// <c>NSObject[]</c>). <c>NSSet(NSObject[])</c> stages them in an <c>NSArray</c> it never
    /// disposes, which keeps every element retained until a finalizer runs; this builds the set from
    /// a staging array it disposes once the set holds the elements.
    /// </summary>
    public static string NewSet(string elementArrayExpr)
        => "((global::System.Func<Foundation.NSObject[], Foundation.NSSet>)(static __elements => { " +
           "using var __staged = Foundation.NSArray.FromNSObjects(__elements); " +
           "return new Foundation.NSSet(__staged); }))(" + elementArrayExpr + ")";

    /// <summary>
    /// An expression that hands Swift its own +1 reference to the Objective-C object
    /// <paramref name="objectExpr"/> evaluates to, holding the object's wrapper until the retain has
    /// been taken. A conformer's result is often a wrapper nothing else references: reading its
    /// handle inline (<c>Retain(result.Handle)</c>) leaves the wrapper unreachable before the
    /// retain, and a precise GC that finalizes it in between releases the object first.
    /// </summary>
    public static string RetainedObjectHandle(string objectExpr)
        => "((global::System.Func<ObjCRuntime.INativeObject, global::System.IntPtr>)(static __object => { " +
           "var __handle = global::Swift.Runtime.Arc.UnknownObjectRetain(__object.Handle); " +
           "global::System.GC.KeepAlive(__object); return __handle; }))(" + objectExpr + ")";
}

/// <summary>
/// A container projection whose parameters cross as a bridged ObjC collection handle. Both the
/// bare and the optional parameter plans read their handle off the same owner, so the two cannot
/// disagree about how the collection is built or how long its wrapper lives.
/// </summary>
public interface IObjCContainerBridgeOwnerSource
{
    /// <summary>
    /// The owning wrapper for the collection built from <paramref name="paramName"/>. With
    /// <paramref name="sourceIsNullable"/> the parameter is an optional whose absent case must yield
    /// a null wrapper rather than an empty collection.
    /// </summary>
    ObjCContainerBridgeOwner BuildObjCBridgeOwner(string paramName, bool sourceIsNullable);
}
