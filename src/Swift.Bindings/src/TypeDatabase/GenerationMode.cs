// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// The generation mode for a binding run. This is the explicit, named form of what was
/// previously an implicit sentinel — <c>!string.IsNullOrEmpty(AsyncLibraryName)</c> — consulted
/// inline at dozens of emitter sites. It names the single decision it stands for ("is there a
/// companion wrapper library that carries the <c>@_cdecl</c> thunks?") so every consumer reads
/// one concept instead of re-deriving the sentinel by hand. Route the decision through
/// <see cref="ITypeDatabase.GenerationMode"/> (or <c>WrapperValidation.IsXCFrameworkMode</c>),
/// not through ad-hoc <c>AsyncLibraryName</c> emptiness checks.
/// </summary>
public enum GenerationMode
{
    /// <summary>
    /// Direct mode: no companion wrapper library is configured, so no <c>@_cdecl</c> wrapper
    /// emission occurs (Apple system-framework / direct bindings).
    /// </summary>
    Direct,

    /// <summary>
    /// XCFramework mode: a companion wrapper library (<see cref="ITypeDatabase.AsyncLibraryName"/>)
    /// carries the generated <c>@_cdecl</c> wrappers. This is a prerequisite for all
    /// <c>@_cdecl</c> wrapper emission.
    /// </summary>
    XCFramework,
}
