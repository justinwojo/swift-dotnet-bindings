// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// The access level a Swift <c>import</c> is declared at (SE-0409 access-controlled imports).
/// <see cref="Plain"/> is the default (no explicit modifier), which is effectively public.
/// </summary>
public enum ImportAccess
{
    /// <summary>No explicit access modifier — a plain <c>import X</c>, effectively public.</summary>
    Plain,

    /// <summary><c>public import X</c> (or <c>open</c>, folded here).</summary>
    Public,

    /// <summary><c>package import X</c>.</summary>
    Package,

    /// <summary><c>internal import X</c>.</summary>
    Internal,

    /// <summary><c>fileprivate import X</c>.</summary>
    FilePrivate,

    /// <summary><c>private import X</c>.</summary>
    Private,
}

/// <summary>
/// One <c>import</c> declaration extracted from a <c>.swiftinterface</c>, carrying the imported
/// module name plus the provenance the closure preflight needs to attribute a missing-module error:
/// which interface file declared it, at which line, at what visibility, and whether it re-exports.
/// </summary>
/// <remarks>
/// This is the structured form behind <see cref="AppleFrameworkImportDetector.ExtractImportEdges"/>.
/// The legacy <see cref="AppleFrameworkImportDetector.ExtractImports"/> (bare module-name list) is a
/// compatibility projection over it. Unlike the bare list, an edge is never silently discarded — the
/// closure preflight turns an unresolved edge into a named obligation with this attribution.
/// </remarks>
public sealed record ImportEdge
{
    /// <summary>The leading imported module name (e.g. <c>RealityFoundation</c>). For a submember
    /// import like <c>import struct Foundation.URL</c> this is the leading module (<c>Foundation</c>).</summary>
    public required string ModuleName { get; init; }

    /// <summary>The access modifier the import was declared at (<see cref="ImportAccess.Plain"/> when none).</summary>
    public required ImportAccess Access { get; init; }

    /// <summary><c>true</c> when declared <c>@_exported import</c> — the module's public API is re-exported
    /// through the importing module, so a consumer (and our generated wrapper) transitively needs it.</summary>
    public bool IsExported { get; init; }

    /// <summary><c>true</c> when declared <c>@_implementationOnly import</c> — the module is a private
    /// implementation detail that does NOT propagate to consumers and is NOT re-emitted into the wrapper.</summary>
    public bool IsImplementationOnly { get; init; }

    /// <summary>Absolute path to the <c>.swiftinterface</c> the import was read from.</summary>
    public required string InterfacePath { get; init; }

    /// <summary>1-based line number of the import statement within <see cref="InterfacePath"/>.</summary>
    public required int Line { get; init; }

    /// <summary>
    /// <c>true</c> when this import does NOT contribute to the generated wrapper's compile-time module
    /// closure — an <c>@_implementationOnly</c> import or one declared at a non-public access level
    /// (<c>package</c>/<c>internal</c>/<c>fileprivate</c>/<c>private</c>). These are deliberately not
    /// re-emitted into the wrapper (see <see cref="AppleFrameworkImportDetector.ExtractNonPublicImports"/>),
    /// so a missing one can never break the wrapper compile and MUST NOT become a preflight obligation.
    /// A plain or <c>public</c> or <c>@_exported</c> import IS a compile obligation.
    /// </summary>
    public bool IsNonPublic =>
        IsImplementationOnly
        || Access is ImportAccess.Package or ImportAccess.Internal or ImportAccess.FilePrivate or ImportAccess.Private;
}
