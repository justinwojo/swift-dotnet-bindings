// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Represents a Swift type name.
/// </summary>
public record SwiftTypeName
{
    /// <summary>
    /// The module name.
    /// </summary>
    public string Module { get; }

    /// <summary>
    /// The type name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The module-qualified type name. Includes all parent types.
    /// </summary>
    public string ModuleQualifiedName { get; }

    /// <inheritdoc />
    public override string ToString() => ModuleQualifiedName;

    private SwiftTypeName(string module, string name, string moduleQualifiedName)
    {
        Module = module;
        Name = name;
        ModuleQualifiedName = moduleQualifiedName;
    }

    /// <summary>
    /// Creates a new SwiftTypeName from a module-qualified name.
    /// </summary>
    /// <param name="moduleQualifiedName">The module-qualified name.</param>
    /// <returns>The SwiftTypeName.</returns>
    public static SwiftTypeName FromModuleQualifiedName(string moduleQualifiedName)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleQualifiedName, nameof(moduleQualifiedName));

        if (moduleQualifiedName.Contains('<'))
        {
            throw new ArgumentException("Cannot create a SwiftTypeName from a generic type.");
        }

        var parts = moduleQualifiedName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new ArgumentException($"Invalid module-qualified name: {moduleQualifiedName}");
        }

        return new SwiftTypeName(parts.First(), parts.Last(), moduleQualifiedName);
    }

    /// <summary>
    /// Non-throwing counterpart to <see cref="FromModuleQualifiedName"/> for names that come
    /// from untrusted input — printed ABI type names, generic-signature constraint operands,
    /// and anything else derived from the library under binding rather than from generator
    /// code. Returns false instead of throwing so the caller can degrade to a reasoned skip.
    ///
    /// Beyond the syntactic checks the throwing factory makes, this also rejects a name whose
    /// root segment is an unsubstituted generic parameter. Those are NOT module-qualified
    /// names even though they are shaped like one: splitting <c>τ_0_0.Bridge.T</c> on '.'
    /// yields three segments, so the throwing factory happily reports module <c>τ_0_0</c> and
    /// name <c>T</c> — a module that does not exist. Everything downstream then works from a
    /// fabricated identity: the type is looked up and missed, and its spelling is rendered
    /// into Swift source and sanitized into <c>@_cdecl</c> symbol names. A placeholder is a
    /// stand-in for a type that substitution never supplied, so the only correct answer is
    /// "this is not a nameable type" — not a guess at which module it lives in.
    /// </summary>
    /// <param name="moduleQualifiedName">The candidate module-qualified name.</param>
    /// <param name="swiftTypeName">The parsed name, or null when the input is not one.</param>
    /// <returns>True if the input is a well-formed module-qualified name.</returns>
    public static bool TryFromModuleQualifiedName(
        string? moduleQualifiedName,
        [NotNullWhen(true)] out SwiftTypeName? swiftTypeName)
    {
        swiftTypeName = null;

        if (string.IsNullOrEmpty(moduleQualifiedName) || moduleQualifiedName.Contains('<'))
            return false;

        var parts = moduleQualifiedName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return false;

        // Only the ROOT segment is checked. It sits in module position, and a generic parameter
        // there means the whole path hangs off a stand-in rather than a real module. Later
        // segments must NOT be checked: they sit in type position, where a one-letter name is a
        // legitimate type (`MyModule.T`), and the placeholder test treats bare T/U/V/E/K/R/S as
        // parameters — so testing them would reject real bindable types.
        if (TypeSpecHelpers.IsGenericTypeParameter(parts[0]))
            return false;

        swiftTypeName = new SwiftTypeName(parts.First(), parts.Last(), moduleQualifiedName);
        return true;
    }

    /// <summary>
    /// Creates a new SwiftTypeName from a NamedTypeSpec.
    /// Traverses the InnerType chain for nested types (e.g., ImagePipeline.ImageRequest.UserInfoKey).
    /// </summary>
    /// <param name="namedTypeSpec">The NamedTypeSpec.</param>
    /// <returns>The SwiftTypeName.</returns>
    public static SwiftTypeName FromTypeSpec(NamedTypeSpec namedTypeSpec)
    {
        ArgumentNullException.ThrowIfNull(namedTypeSpec, nameof(namedTypeSpec));

        // Build full name including nested types via InnerType chain
        var fullName = namedTypeSpec.Name;
        var innerType = namedTypeSpec.InnerType;
        while (innerType != null)
        {
            fullName += "." + innerType.Name;
            innerType = innerType.InnerType;
        }

        return FromModuleQualifiedName(fullName);
    }

    /// <summary>
    /// Swift type name for void.
    /// </summary>
    public static readonly SwiftTypeName VoidType = new SwiftTypeName(string.Empty, "()", "()");

    /// <summary>
    /// Swift type name for Any.
    /// </summary>
    public static readonly SwiftTypeName AnyType = new SwiftTypeName(string.Empty, "Any", "Any");
}
