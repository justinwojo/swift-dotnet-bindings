// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration.ObjC;

/// <summary>
/// ObjC property memory management semantics.
/// Maps to ArgumentSemantic in .NET MAUI bindings.
/// </summary>
public enum ObjCMemorySemantic
{
    None,
    Assign,
    Copy,
    Retain,
    Strong,
    Weak,
    UnsafeUnretained
}

public sealed record ObjCDocParam
{
    public required string Name { get; init; }
    public required string Description { get; init; }
}

public sealed record ObjCParameterDecl
{
    public required string Name { get; init; }
    public required ObjCTypeRef Type { get; init; }
}

public sealed record ObjCMethodDecl
{
    public required string Selector { get; init; }
    public required ObjCTypeRef ReturnType { get; init; }
    public List<ObjCParameterDecl> Parameters { get; init; } = [];
    public bool IsInstanceMethod { get; init; }
    public bool IsOptional { get; init; }
    public bool IsVariadic { get; init; }
    public bool IsDesignatedInitializer { get; init; }
    public bool IsFromCategory { get; init; }
    public string CategoryName { get; init; } = "";
    public List<ObjCAvailability> Availability { get; init; } = [];
    public string? SwiftName { get; init; }
    public bool IsRefinedForSwift { get; init; }
    public string? DocComment { get; init; }
    public List<ObjCDocParam> DocParams { get; init; } = [];
}

public sealed record ObjCPropertyDecl
{
    public required string Name { get; init; }
    public required ObjCTypeRef Type { get; init; }
    public bool IsReadonly { get; init; }
    public bool IsClass { get; init; }
    public bool IsOptional { get; init; }
    public bool IsFromCategory { get; init; }
    public string CategoryName { get; init; } = "";
    public string? GetterSelector { get; init; }
    public string? SetterSelector { get; init; }
    public ObjCMemorySemantic MemorySemantic { get; init; }
    public List<ObjCAvailability> Availability { get; init; } = [];
    public string? SwiftName { get; init; }
    public bool IsRefinedForSwift { get; init; }
    public string? DocComment { get; init; }
}

public sealed record ObjCClassDecl
{
    public required string Name { get; init; }
    public string? SuperclassName { get; init; }
    public List<string> ProtocolNames { get; init; } = [];
    public List<string> GenericTypeParamNames { get; init; } = [];
    public List<ObjCMethodDecl> Methods { get; init; } = [];
    public List<ObjCPropertyDecl> Properties { get; init; } = [];
    public List<ObjCAvailability> Availability { get; init; } = [];
    public string? SwiftName { get; init; }
    public string? DocComment { get; init; }

    /// <summary>
    /// True when the interface carries <c>__attribute__((objc_runtime_name("...")))</c>, so its
    /// runtime class symbol is <c>_OBJC_CLASS_$_&lt;runtimeName&gt;</c> rather than
    /// <c>_OBJC_CLASS_$_&lt;Name&gt;</c>. The clang JSON AST exposes the attribute's presence but
    /// not its string argument, so the native-symbol guard cannot verify the real symbol and must
    /// keep such classes (it only ever drops with positive proof of absence).
    /// </summary>
    public bool HasCustomRuntimeName { get; init; }
}

public sealed record ObjCProtocolDecl
{
    public required string Name { get; init; }
    public List<string> InheritedProtocolNames { get; init; } = [];
    public List<ObjCMethodDecl> Methods { get; init; } = [];
    public List<ObjCPropertyDecl> Properties { get; init; } = [];
    public List<ObjCAvailability> Availability { get; init; } = [];
    public string? DocComment { get; init; }
    /// <summary>
    /// True when this protocol is a delegate or data-source protocol.
    /// Detected by name (*Delegate, *DataSource) or by being the type of a
    /// delegate/dataSource property on a class. Used by the emitter to add [Model].
    /// </summary>
    public bool IsDelegateProtocol { get; init; }
}

public sealed record ObjCEnumCaseDecl
{
    public required string Name { get; init; }
    public long? Value { get; init; }
}

public sealed record ObjCEnumDecl
{
    public required string Name { get; init; }
    public bool IsOptions { get; init; }
    public ObjCTypeRef? UnderlyingType { get; init; }
    public List<ObjCEnumCaseDecl> Cases { get; init; } = [];
    public List<ObjCAvailability> Availability { get; init; } = [];
    public string? SwiftName { get; init; }
    public string? DocComment { get; init; }
}

public sealed record ObjCStructField
{
    public required string Name { get; init; }
    public required ObjCTypeRef Type { get; init; }
}

public sealed record ObjCStructDecl
{
    public required string Name { get; init; }
    public List<ObjCStructField> Fields { get; init; } = [];
    /// <summary>
    /// True when the struct contains bitfields, anonymous unions/structs, or other
    /// layout constructs that cannot be correctly represented in C#.
    /// Emitters should skip these structs and log a diagnostic.
    /// </summary>
    public bool HasUnsafeLayout { get; init; }
    /// <summary>Describes why the layout is unsafe (for diagnostics).</summary>
    public string? UnsafeLayoutReason { get; init; }
}

public sealed record ObjCFunctionDecl
{
    public required string Name { get; init; }
    public required ObjCTypeRef ReturnType { get; init; }
    public List<ObjCParameterDecl> Parameters { get; init; } = [];
    public bool IsVariadic { get; init; }
    public List<ObjCAvailability> Availability { get; init; } = [];
}

public sealed record ObjCConstantDecl
{
    public required string Name { get; init; }
    public required ObjCTypeRef Type { get; init; }
    public bool IsExtern { get; init; }
    public List<ObjCAvailability> Availability { get; init; } = [];
}

public sealed record ObjCTypedefDecl
{
    public required string Name { get; init; }
    public required ObjCTypeRef UnderlyingType { get; init; }
}

public sealed record ObjCCategoryDecl
{
    public required string CategoryName { get; init; }
    public required string ClassName { get; init; }
    public List<string> ProtocolNames { get; init; } = [];
    public List<string> GenericTypeParamNames { get; init; } = [];
    public List<ObjCMethodDecl> Methods { get; init; } = [];
    public List<ObjCPropertyDecl> Properties { get; init; } = [];
    public List<ObjCAvailability> Availability { get; init; } = [];
}
