// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration.ObjC;

public enum ObjCNullability
{
    Unspecified,
    Nullable,
    Nonnull
}

public sealed record ObjCTypeRef
{
    public required string Name { get; init; }
    public bool IsPointer { get; init; }
    public bool IsBlock { get; init; }
    public ObjCNullability Nullability { get; init; }
    public List<string> ProtocolQualifications { get; init; } = [];
    public ObjCTypeRef? PointeeType { get; init; }
    public List<ObjCTypeRef> BlockParams { get; init; } = [];
    public ObjCTypeRef? BlockReturnType { get; init; }
    public List<ObjCTypeRef> GenericArgs { get; init; } = [];
    public int? FixedArraySize { get; init; }
    public bool IsFunctionPointer { get; init; }
    public bool IsAnonymousRecord { get; init; }

    /// <summary>
    /// True when <see cref="Name"/> denotes a C record (a <c>struct</c> or <c>union</c>), whether it
    /// was spelled with its tag or through a typedef. A pointer to a record is an address whatever
    /// the record holds — opaque handle, self-referential node, system type such as <c>FILE</c> — so
    /// the pointer, not the record, is what crosses the boundary.
    /// </summary>
    public bool IsRecord { get; init; }

    /// <summary>
    /// True when the pointee is <c>const</c>-qualified (<c>const T *</c> / <c>T const *</c>). This is
    /// the read-only marker C carries on a pointer parameter, and it is what separates an input buffer
    /// from a caller-allocated output slot: a <c>const T *</c> parameter can never be written through,
    /// so it must never be projected as a C# <c>out</c> (whose call-site semantics zero the caller's
    /// storage before the callee runs). A const-qualified *pointer* with a mutable pointee
    /// (<c>T *const</c>) says nothing about writability and is deliberately not flagged here.
    /// </summary>
    public bool IsConst { get; init; }

    public string RawQualType { get; init; } = "";
}
