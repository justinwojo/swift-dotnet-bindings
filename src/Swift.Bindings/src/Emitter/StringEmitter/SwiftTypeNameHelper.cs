// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared helper for rendering Swift type names from TypeSpec instances.
/// Used by EveryProtocolEmitter (for EveryProtocol conformance code) and
/// WitnessDispatchEmitter (for witness table dispatch accessors).
/// </summary>
public static class SwiftTypeNameHelper
{
    /// <summary>
    /// Renders a TypeSpec as a Swift type name suitable for use in generated Swift code.
    /// Handles named types, tuples, closures, protocol compositions, generics, and optionals.
    /// </summary>
    public static string GetSwiftTypeName(TypeSpec? typeSpec)
    {
        if (typeSpec == null) return "Any";

        // Handle ProtocolListTypeSpec (protocol composition types)
        // An empty protocol list represents "Any" in Swift
        if (typeSpec is ProtocolListTypeSpec protocolList)
        {
            if (protocolList.Protocols.Count == 0)
                return "Any";
            if (protocolList.Protocols.Count == 1)
                return $"any {protocolList.Protocols.Keys.First().Name}";
            // Multiple protocols: any P1 & P2 & P3
            var protocolNames = string.Join(" & ", protocolList.Protocols.Keys.Select(p => p.Name));
            return $"any {protocolNames}";
        }

        if (typeSpec is NamedTypeSpec namedType)
        {
            // Check for generic type parameters first (τ_0_0, T, Element, etc.)
            // These can't be resolved to concrete types, so use Any
            if (TypeSpecHelpers.IsGenericTypeParameter(namedType.Name))
                return "Any";

            // Handle metatype patterns like "any Any.Type" or "Any.Type"
            if (namedType.Name == "any Any.Type" || namedType.Name == "Any.Type")
                return "Any.Type";

            // Handle existential types (any Protocol)
            var anyPrefix = namedType.IsAny ? "any " : "";

            // Check if this is a generic type (has generic parameters)
            if (namedType.GenericParameters.Count > 0)
            {
                var typeArgs = string.Join(", ", namedType.GenericParameters.Select(GetSwiftTypeName));

                // Special case for Optional - use ? syntax
                // Note: For optionals, the ? goes after the type name, and any prefix goes on inner type
                if (namedType.Name == "Swift.Optional" && namedType.GenericParameters.Count == 1)
                {
                    var innerType = GetSwiftTypeName(namedType.GenericParameters[0]);
                    // Always `?`, even for a spec marked IsImplicitlyUnwrappedOptional. `!` is legal
                    // ONLY in a declaration position, which GetSwiftTypeNameForDeclaration handles;
                    // this renderer also feeds generic arguments (UnsafeMutablePointer<T>,
                    // CheckedContinuation<T, _>), `.self` metatypes and dedup keys, where `!` is
                    // either rejected outright ("using '!' is not allowed here") or would split one
                    // Swift type across two keys.
                    return $"({innerType})?";
                }

                return $"{anyPrefix}{namedType.Name}<{typeArgs}>";
            }
            return $"{anyPrefix}{namedType.Name}";
        }

        if (typeSpec is TupleTypeSpec tupleType)
        {
            if (tupleType.IsEmptyTuple)
                return "Void";
            var elements = string.Join(", ", tupleType.Elements.Select(e =>
            {
                var name = GetSwiftTypeName(e);
                return !string.IsNullOrEmpty(e.TypeLabel) ? $"{e.TypeLabel}: {name}" : name;
            }));
            return $"({elements})";
        }

        if (typeSpec is ClosureTypeSpec closureType)
        {
            // Build closure type string: (Args) -> Return or (Args) throws -> Return
            string argsString;
            if (closureType.Arguments is TupleTypeSpec argsTuple && argsTuple.IsEmptyTuple)
            {
                // Empty tuple → "()" not "Void" (Swift requires () -> Return, not Void -> Return)
                argsString = "()";
            }
            else
            {
                argsString = GetSwiftTypeName(closureType.Arguments);
                // Ensure args are wrapped in parentheses
                if (closureType.Arguments is not TupleTypeSpec)
                {
                    argsString = $"({argsString})";
                }
            }
            var returnString = GetSwiftTypeName(closureType.ReturnType);
            if (closureType.ReturnType.IsEmptyTuple)
            {
                returnString = "Void";
            }

            var throwsKeyword = closureType.Throws ? " throws" : "";
            var asyncKeyword = closureType.IsAsync ? " async" : "";

            // Build type-level attributes prefix.
            // Include type-level attributes like @MainActor, @Sendable (valid on closure types
            // in any context: property declarations, return types, metatype expressions).
            // Exclude @escaping and @autoclosure — these are calling convention attributes
            // only valid on function parameters, not in property or metatype contexts.
            var attributePrefix = "";
            if (closureType.HasAttributes)
            {
                var typeAttrs = closureType.Attributes
                    .Where(a => a.Name != "escaping" && a.Name != "autoclosure")
                    .Select(a => a.ToString())
                    .ToList();
                if (typeAttrs.Count > 0)
                    attributePrefix = string.Join(" ", typeAttrs) + " ";
            }
            return $"{attributePrefix}{argsString}{asyncKeyword}{throwsKeyword} -> {returnString}";
        }

        return typeSpec.ToString() ?? "Any";
    }

    /// <summary>
    /// Renders a TypeSpec for a Swift *declaration* position — a var/let type annotation, a function
    /// parameter, or a function result. Identical to <see cref="GetSwiftTypeName"/> except that a
    /// type the source spelled `T!` renders back as `T!` rather than `T?`.
    ///
    /// The distinction only exists here because Swift's conformance checker rejects a `T?` witness
    /// for a `T!` requirement ("candidate has non-matching type"), so a synthesized EveryProtocol
    /// stub must reproduce the spelling the protocol used. It is NOT a different type: IUO and
    /// Optional share a layout and project to the same C#.
    ///
    /// Only the TOP level is re-spelled, which is exactly Swift's own rule — `!` is permitted solely
    /// in these three positions, and a nested IUO (a generic argument, a tuple element) is erased to
    /// Optional by the compiler and is a syntax error to write.
    /// </summary>
    public static string GetSwiftTypeNameForDeclaration(TypeSpec? typeSpec)
        => ApplyImplicitlyUnwrappedOptionalSigil(GetSwiftTypeName(typeSpec), typeSpec);

    /// <summary>
    /// Re-spells an already-rendered Swift type as `T!` when <paramref name="typeSpec"/> is a
    /// top-level implicitly unwrapped optional. This is the single rule for the IUO spelling; every
    /// declaration-position renderer routes through it rather than re-deriving the swap, so the
    /// stub renderers and <see cref="GetSwiftTypeNameForDeclaration"/> cannot drift apart.
    ///
    /// Call it ONLY on text destined for a declaration position. <paramref name="rendered"/> must be
    /// the rendering of <paramref name="typeSpec"/> itself — passing an inner type's text re-spells
    /// the wrong level.
    ///
    /// A rendering that is not the `T?` sugar (e.g. the degraded `Swift.Optional&lt;Any&gt;` a stub
    /// emits once a generic placeholder is substituted away) is returned untouched: there is no
    /// legal IUO spelling of the angle-bracket form, and such a stub cannot match its requirement
    /// faithfully regardless.
    /// </summary>
    public static string ApplyImplicitlyUnwrappedOptionalSigil(string rendered, TypeSpec? typeSpec)
    {
        if (typeSpec is NamedTypeSpec { IsImplicitlyUnwrappedOptional: true, Name: "Swift.Optional" } namedType
            && namedType.GenericParameters.Count == 1
            && rendered.EndsWith('?'))
        {
            return string.Concat(rendered.AsSpan(0, rendered.Length - 1), "!");
        }

        return rendered;
    }

    /// <summary>
    /// Gets the Swift type name suitable for use with .self metatype access or assumingMemoryBound(to:).
    /// Wraps existential types (any Protocol) in parentheses since Swift requires
    /// (any Protocol).self instead of any Protocol.self.
    /// Also wraps optional closure types: Optional<(X) -> Y>.self not ((X) -> Y)?.self.
    /// </summary>
    public static string GetSwiftTypeNameForMetatype(TypeSpec? typeSpec)
    {
        var typeName = GetSwiftTypeName(typeSpec);

        // Function types need parenthesization: ((A) -> B).self, not (A) -> B.self
        if (typeSpec is ClosureTypeSpec)
            return $"({typeName})";

        // Optional wrapping a closure: use Optional<ClosureType>.self syntax
        // because ((X) -> Y)?.self can confuse the Swift parser.
        // We emit Optional<(X) -> Y>.self which is unambiguous.
        if (typeSpec is NamedTypeSpec namedType
            && namedType.Name == "Swift.Optional"
            && namedType.GenericParameters.Count == 1
            && namedType.GenericParameters[0] is ClosureTypeSpec)
        {
            var innerTypeName = GetSwiftTypeName(namedType.GenericParameters[0]);
            return $"Optional<{innerTypeName}>";
        }

        // If the type starts with "any ", it needs to be wrapped in parentheses for .self access
        if (typeName.StartsWith("any ") || typeName.StartsWith("(any "))
        {
            // Wrap in parentheses if not already
            if (!typeName.StartsWith("("))
                return $"({typeName})";
        }
        return typeName;
    }
}
