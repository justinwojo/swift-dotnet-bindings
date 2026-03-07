// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration.ObjC;

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
}

public sealed record ObjCProtocolDecl
{
    public required string Name { get; init; }
    public List<string> InheritedProtocolNames { get; init; } = [];
    public List<ObjCMethodDecl> Methods { get; init; } = [];
    public List<ObjCPropertyDecl> Properties { get; init; } = [];
    public List<ObjCAvailability> Availability { get; init; } = [];
    public string? DocComment { get; init; }
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
}

public sealed record ObjCFunctionDecl
{
    public required string Name { get; init; }
    public required ObjCTypeRef ReturnType { get; init; }
    public List<ObjCParameterDecl> Parameters { get; init; } = [];
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
