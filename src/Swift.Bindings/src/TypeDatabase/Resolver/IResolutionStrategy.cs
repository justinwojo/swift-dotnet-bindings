// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// A single type-resolution strategy plugged into <see cref="TypeResolver"/>.
/// Each strategy owns one of the legacy 9-stage <c>TryGetTypeRecord</c> branches
/// that previously lived inline in <see cref="TypeDatabaseExtensions"/>.
/// </summary>
/// <remarks>
/// Strategies are dispatched in the order registered with the resolver.
/// The first strategy whose <see cref="TryResolve"/> returns true wins;
/// later strategies are not consulted. This mirrors the short-circuit
/// behaviour of the legacy stage chain.
/// </remarks>
public interface IResolutionStrategy
{
    /// <summary>
    /// Stable name used in provenance and diagnostics.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Attempts to resolve the supplied <paramref name="typeSpec"/>.
    /// Returns true with a populated <paramref name="result"/> when this
    /// strategy claims the type; false to defer to subsequent strategies.
    /// </summary>
    bool TryResolve(
        TypeSpec typeSpec,
        ResolutionContext context,
        [NotNullWhen(true)] out TypeResolutionResult? result);
}
