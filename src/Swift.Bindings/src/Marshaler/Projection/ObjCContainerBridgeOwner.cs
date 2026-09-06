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
