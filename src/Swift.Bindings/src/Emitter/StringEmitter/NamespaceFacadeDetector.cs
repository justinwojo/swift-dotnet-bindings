// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Detects Swift "namespace facade" types — public <c>struct</c>
    /// declarations whose only purpose is to scope a family of nested types.
    /// (A caseless <c>enum</c> used the same way is not lifted; see
    /// <see cref="IsNamespaceFacade(TypeDecl)"/>.)
    /// In Swift these look like:
    /// <code>
    /// public struct OuterFacade {           // outer namespace facade
    ///   public struct InnerResult { … }
    ///   public enum InnerStatus { … }
    ///   // …no inits, no stored properties, no instance/static members.
    /// }
    /// </code>
    /// Such a struct is emitted as a C# static class that holds only its nested
    /// types (<see cref="NamespaceFacadeEmitter"/>), not as an instance struct
    /// class with a payload no Swift code can produce. It used to be lifted to a C#
    /// namespace, which put the nested types at <c>Module.OuterFacade.InnerResult</c>
    /// until a newer SDK gave the struct a member and moved them to
    /// <c>Module.OuterFacade+InnerResult</c>; a class keeps the second form in
    /// every copy.
    ///
    /// The canonical case is an outer struct containing many nested types.
    /// </summary>
    public static class NamespaceFacadeDetector
    {
        /// <summary>
        /// Returns <c>true</c> when <paramref name="typeDecl"/> matches the
        /// strict namespace-facade shape: a non-generic <c>StructDecl</c> with zero properties, zero methods (instance,
        /// static, or constructor), zero operators, zero subscripts, zero
        /// non-marker protocol conformances, and at least one nested type.
        /// A caseless <c>EnumDecl</c> never qualifies: it projects as a static
        /// class through its own handler whether or not it has members. Stdlib marker conformances (<c>Swift.Copyable</c>,
        /// <c>Swift.Escapable</c>, <c>Swift.Sendable</c>, <c>Swift.SendableMetatype</c>,
        /// <c>Swift.BitwiseCopyable</c>) are filtered out before the count
        /// check — every Swift struct/enum has Copyable + Escapable implicitly,
        /// and these markers carry no runtime witness table, so they don't
        /// force the type onto the runtime-identity emission path. Any
        /// real (non-marker) protocol conformance, or any non-zero count
        /// for the other member surfaces, falls through to the existing
        /// class-emission path so types with runtime semantics keep their
        /// current shape.
        /// </summary>
        /// <param name="typeDecl">The type declaration to evaluate.</param>
        /// <returns><c>true</c> if the type qualifies as a namespace facade.</returns>
        public static bool IsNamespaceFacade(TypeDecl typeDecl)
        {
            // Only struct/enum can host the facade idiom — class hierarchies
            // carry runtime identity (vtables, init-and-deinit, reference
            // semantics) that a static class cannot host. Protocols, actors,
            // etc. are excluded by type.
            if (typeDecl is not StructDecl and not EnumDecl)
                return false;

            // Must be top-level (parent is the module). The case-only collision
            // pass ranks top-level facades only, and a nested member-less struct
            // keeps the ordinary struct emission it has always had.
            if (typeDecl.ParentDecl is not ModuleDecl)
                return false;

            // Must contain nested types — a member-less struct with nothing
            // nested in it is an ordinary (empty) struct, which a generic
            // argument or a parameter can still name; a static class could not.
            if (typeDecl.Types.Count == 0)
                return false;

            // No member surface of any kind — properties (instance or static),
            // methods (instance, static, or constructor), operators, or
            // subscripts. Even one static helper means the consumer needs a
            // type-named scope (`Foo.Helper()`) that the ordinary struct
            // emission provides, members and all.
            if (typeDecl.Properties.Count > 0)
                return false;
            if (typeDecl.Methods.Count > 0)
                return false;
            if (typeDecl.Operators.Count > 0)
                return false;
            if (typeDecl.Subscripts.Count > 0)
                return false;

            // Generic facades are syntactically possible (`struct Foo<T> { struct
            // Bar { … } }`) but have no real-world precedent. Fall through to
            // the ordinary generic struct emission.
            if (typeDecl.GenericParameters.Count > 0)
                return false;

            // For struct: a non-marker protocol conformance would require a
            // witness-table implementation on the type, which a static class
            // cannot host. The parser auto-attaches Copyable + Escapable to
            // every Swift struct/enum (and Sendable for Sendable-eligible
            // types), so we filter the stdlib marker set before checking.
            if (typeDecl is StructDecl structDecl && HasNonMarkerConformance(structDecl.Conformances))
                return false;

            // A caseless enum is not a facade: it always projects as a static class
            // through its own handler (EnumHandler.EmitNamespaceEnum), members or not,
            // so a static member a newer SDK gives it (RealityFoundation's `Audio`
            // between Xcode 26.3 and 27.2) changes no nested type's CLR name.
            if (typeDecl is EnumDecl)
                return false;

            return true;
        }

        /// <summary>
        /// Returns <c>true</c> if any conformance in <paramref name="conformances"/>
        /// targets a non-marker protocol (i.e. anything other than the stdlib
        /// auto-attached markers). Marker protocols carry no runtime witness
        /// table and are added implicitly by the Swift compiler to every
        /// struct/enum that satisfies their layout requirements, so they
        /// cannot be used to distinguish a real protocol-bearing type from
        /// a bare namespace facade.
        /// </summary>
        private static bool HasNonMarkerConformance(IReadOnlyList<TypeConformance> conformances)
        {
            foreach (var conformance in conformances)
            {
                if (!IsStdlibMarkerProtocol(conformance.Protocol))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Stdlib marker protocols carry no runtime witness table — the Swift
        /// compiler auto-attaches them to every struct/enum that satisfies the
        /// layout requirement (Copyable + Escapable on every value type by
        /// default). Module-qualified to avoid misidentifying a same-name
        /// app/framework protocol as a marker. Mirrors the parallel definition
        /// in <c>GenericTypeEmitter.IsStdlibMarkerProtocol</c>,
        /// <c>PInvokeHelperEmitter.IsStdlibMarkerProtocol</c>, and
        /// <c>ExistentialHandler.IsMarkerProtocol</c>.
        /// </summary>
        private static bool IsStdlibMarkerProtocol(SwiftTypeName protocolTypeName) =>
            protocolTypeName.Module == "Swift" &&
            protocolTypeName.Name is "Sendable" or "Escapable" or "Copyable"
                                  or "SendableMetatype" or "BitwiseCopyable";
    }
}
