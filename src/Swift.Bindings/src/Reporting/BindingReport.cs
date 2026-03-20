// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Binding generation coverage and skip report.
/// </summary>
public sealed class BindingReport
{
    public required string ModuleName { get; init; }
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public int TotalTypes { get; set; }
    public int EmittedTypes { get; set; }
    public int SkippedTypes { get; set; }

    public int TotalMembers { get; set; }
    public int EmittedMembers { get; set; }
    public int SkippedMembers { get; set; }
    public int SynthesizedMembers { get; set; }

    public List<SkippedItem> SkippedItems { get; } = new();
    public List<WrappedItem> WrappedItems { get; } = new();
    public List<BridgedViewItem> BridgedViews { get; } = new();
    public List<ThemeBridgedItem> ThemeBridgedProperties { get; } = new();
}

/// <summary>
/// Category of declaration being tracked in the report.
/// </summary>
public enum BindingItemKind
{
    Type,
    Method,
    Property,
    Operator,
    Subscript,
}

/// <summary>
/// Reason why an item was skipped.
/// </summary>
public enum SkipReason
{
    UnsupportedType,
    AnyTypeFallback,
    AsyncProperty,
    SwiftUIConstraint,
    CombineFramework,
    GenericProtocolConstraint,
    UnsatisfiedGenericConstraint,
    UnsupportedSignature,
    UnsupportedExistential,
    UnsupportedClosure,
    UnsupportedAsyncStream,
    DuplicateSignature,
    MissingHandler,
    SwiftUIView,
    StaticProtocolMember,
    GenericTypeCallback,
    ActorIsolatedAsyncStream,
    SynthesizedCodable,
    UnderscorePrefixInternal,
    ModuleInternal,
    ExtensionDefault,
    NonBlittableCallConvSwift,
    Unknown,
}

/// <summary>
/// A single skipped type/member entry.
/// </summary>
public sealed class SkippedItem
{
    public required BindingItemKind Kind { get; init; }
    public required string Name { get; init; }
    public string? ContainingType { get; init; }
    public required SkipReason Reason { get; init; }
    public string? Details { get; init; }
    public string? RecommendedWorkaround { get; init; }
}

/// <summary>
/// A SwiftUI View detected for bridge generation.
/// </summary>
public sealed class BridgedViewItem
{
    public required string ViewName { get; init; }
    public required string ModuleName { get; init; }
    public required string InitClassification { get; init; }
    public required string BridgeStatus { get; init; }
}

/// <summary>
/// A theme-bridged property (Color/Font setter and optional getter generated via @_cdecl).
/// </summary>
public sealed class ThemeBridgedItem
{
    public required string ClassName { get; init; }
    public required string PropertyName { get; init; }
    public required string PropertyType { get; init; }
}

/// <summary>
/// A member that was auto-wrapped with a generated Swift wrapper + C# factory.
/// </summary>
public sealed class WrappedItem
{
    public required BindingItemKind Kind { get; init; }
    public required string Name { get; init; }
    public string? MangledName { get; init; }
    public string? ContainingType { get; init; }
    public required string WrapperKind { get; init; }
    public string? Details { get; init; }
}
