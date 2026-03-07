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
    /// <summary>
    /// All typedefs from the translation unit (including system headers).
    /// Used for typedef resolution in MapType only — never emitted directly.
    /// Falls back to Typedefs if not populated.
    /// </summary>
    public List<ObjCTypedefDecl>? ResolutionTypedefs { get; init; }
    public List<ObjCCategoryDecl> Categories { get; init; } = [];
    /// <summary>
    /// ObjC class and protocol names declared in Apple SDK headers (not framework-local,
    /// not third-party). Used by ApiDefinitionEmitter to determine which passthrough types
    /// are available from Apple framework using directives at compile time.
    /// </summary>
    public HashSet<string>? AppleSdkTypeNames { get; init; }

    public int TotalDeclarations =>
        Classes.Count + Protocols.Count + Enums.Count + Structs.Count +
        Functions.Count + Constants.Count + Typedefs.Count;
}
