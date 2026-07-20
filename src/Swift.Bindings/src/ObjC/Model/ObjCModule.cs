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
    /// ObjC class and protocol names declared in Apple SDK headers (not framework-local, not
    /// third-party), mapped to the .NET namespace that owns each — derived from the authoritative
    /// <c>&lt;Framework&gt;.framework</c> provenance in the resolved header path (see
    /// <see cref="AppleFrameworkRegistry.TryResolveFrameworkNamespaceFromHeaderPath"/>). The keys
    /// drive ApiDefinitionEmitter's type-resolvability gate (which passthrough types are available
    /// from Apple framework <c>using</c> directives); the values drive the <c>using</c> set itself,
    /// so a referenced Apple framework's namespace is emitted whether or not it is in the curated
    /// baseline. An entry maps to the empty string when its type has no derivable framework namespace
    /// (e.g. a runtime header under <c>/usr/include</c>) — still resolvable, but contributes no
    /// <c>using</c>. Null in <c>-fmodules</c> mode, where SDK types are never expanded into the AST.
    /// </summary>
    public IReadOnlyDictionary<string, string>? AppleSdkTypeNamespaces { get; init; }

    /// <summary>
    /// Apple SDK ENUM name → owning .NET namespace. A usings-only provenance channel, SEPARATE
    /// from <see cref="AppleSdkTypeNamespaces"/> (whose keys drive ApiDefinition resolvability):
    /// enum names must NOT affect resolvability, only supply the owning <c>using</c> for a struct
    /// field / function surface that references an Apple SDK enum (e.g. MTLPixelFormat → Metal).
    /// Null when none.
    /// </summary>
    public IReadOnlyDictionary<string, string>? AppleSdkEnumNamespaces { get; init; }

    public int TotalDeclarations =>
        Classes.Count + Protocols.Count + Enums.Count + Structs.Count +
        Functions.Count + Constants.Count + Typedefs.Count;
}
