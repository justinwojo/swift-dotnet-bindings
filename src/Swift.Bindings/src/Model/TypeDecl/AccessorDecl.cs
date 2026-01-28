// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Base class for property accessor declarations in Swift.
/// </summary>
public abstract record AccessorDecl
{
    /// <summary>
    /// Gets or sets the method that implements this accessor.
    /// </summary>
    public required MethodDecl Method { get; init; }
}

/// <summary>
/// Represents a getter accessor declaration for a Swift property.
/// </summary>
public record GetAccessorDecl : AccessorDecl { }

/// <summary>
/// Represents a setter accessor declaration for a Swift property.
/// </summary>
public record SetAccessorDecl : AccessorDecl { }

