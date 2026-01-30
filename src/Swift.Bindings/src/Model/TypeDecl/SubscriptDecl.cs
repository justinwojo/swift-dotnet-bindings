// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Represents a subscript declaration.
/// Subscripts enable access using bracket syntax like array[index] or dictionary[key].
/// </summary>
public record SubscriptDecl : BaseDecl
{
    /// <summary>
    /// The return type of the subscript (the element type).
    /// </summary>
    public required TypeSpec ReturnTypeSpec { get; set; }

    /// <summary>
    /// The index parameters for the subscript.
    /// Swift supports multiple indices: subscript(row: Int, col: Int) -> T
    /// </summary>
    public required IReadOnlyList<ArgumentDecl> IndexParameters { get; init; }

    /// <summary>
    /// Indicates if the subscript is static.
    /// </summary>
    public required bool IsStatic { get; set; }

    /// <summary>
    /// The accessors available for this subscript (Get, Set, or both).
    /// </summary>
    public required IReadOnlyList<AccessorDecl> Accessors { get; init; }

    /// <summary>
    /// The mangled name of the subscript declaration.
    /// </summary>
    public required string MangledName { get; set; }

    /// <summary>
    /// Whether this subscript has a getter.
    /// </summary>
    public bool HasGetter => Accessors.Any(a => a is GetAccessorDecl);

    /// <summary>
    /// Whether this subscript has a setter.
    /// </summary>
    public bool HasSetter => Accessors.Any(a => a is SetAccessorDecl);
}
