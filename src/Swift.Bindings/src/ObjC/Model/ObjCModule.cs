// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration.ObjC;

public sealed record ObjCModule
{
    public required string ModuleName { get; init; }
    public string? FrameworkPath { get; init; }
    public List<ObjCClassDecl> Classes { get; init; } = [];
    public List<ObjCProtocolDecl> Protocols { get; init; } = [];
    public List<ObjCEnumDecl> Enums { get; init; } = [];
    public List<ObjCStructDecl> Structs { get; init; } = [];
    public List<ObjCFunctionDecl> Functions { get; init; } = [];
    public List<ObjCConstantDecl> Constants { get; init; } = [];
    public List<ObjCTypedefDecl> Typedefs { get; init; } = [];
    public List<ObjCCategoryDecl> Categories { get; init; } = [];

    public int TotalDeclarations =>
        Classes.Count + Protocols.Count + Enums.Count + Structs.Count +
        Functions.Count + Constants.Count + Typedefs.Count;
}
