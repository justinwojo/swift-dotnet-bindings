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
    public string? ProtocolQualification { get; init; }
    public ObjCTypeRef? PointeeType { get; init; }
    public List<ObjCTypeRef> BlockParams { get; init; } = [];
    public ObjCTypeRef? BlockReturnType { get; init; }
    public List<ObjCTypeRef> GenericArgs { get; init; } = [];
    public int? FixedArraySize { get; init; }
    public bool IsFunctionPointer { get; init; }
    public bool IsAnonymousRecord { get; init; }
    public string RawQualType { get; init; } = "";
}
