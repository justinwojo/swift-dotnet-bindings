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
    /// C# has a real <c>namespace</c> primitive. Emitting the facade as a
    /// <c>partial class</c> is structurally correct but forces consumers to either fully-qualify every
    /// nested type or reach for <c>using static</c>, which conventionally signals
    /// member access not nested-type access. A faithful translation lifts the
    /// nested types into a real C# namespace under the parent module's namespace
    /// — e.g. <c>namespace Module.OuterFacade</c>.
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
        /// class whether or not it has members, so a member a newer SDK adds
        /// does not move it from a namespace to a type. Stdlib marker conformances (<c>Swift.Copyable</c>,
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
            // semantics) that a C# namespace cannot host. Protocols, actors,
            // etc. are excluded by type.
            if (typeDecl is not StructDecl and not EnumDecl)
                return false;

            // Must be top-level (parent is the module). A nested empty struct
            // inside a real type body would otherwise route through
            // NamespaceFacadeEmitter and produce a `namespace { … }` block
            // inside a class/struct/enum — which is invalid C#. The
            // facade-as-namespace lift only makes sense when the resulting
            // namespace can sit at module scope alongside its module's other
            // top-level types.
            if (typeDecl.ParentDecl is not ModuleDecl)
                return false;

            // Must contain nested types — otherwise emitting as a namespace
            // produces an empty namespace block which is pointless and
            // generally a parser-output anomaly worth investigating, not
            // collapsing to a namespace.
            if (typeDecl.Types.Count == 0)
                return false;

            // No member surface of any kind — properties (instance or static),
            // methods (instance, static, or constructor), operators, or
            // subscripts. Even one static helper means the consumer needs a
            // type-named scope (`Foo.Helper()`), which a namespace cannot
            // provide. The bug doc allows a "pragmatic" sibling-static-class
            // workaround, but this stricter predicate keeps the change purely
            // additive and zero-risk for the namespace-facade case (which has
            // exactly zero static members on its swiftinterface).
            if (typeDecl.Properties.Count > 0)
                return false;
            if (typeDecl.Methods.Count > 0)
                return false;
            if (typeDecl.Operators.Count > 0)
                return false;
            if (typeDecl.Subscripts.Count > 0)
                return false;

            // Generic facades are syntactically possible (`enum Foo<T> { struct
            // Bar { … } }`) but have no real-world precedent and would force a
            // generic namespace, which C# disallows. Fall through to existing
            // emission so the parser-correct (if ugly) class form still surfaces.
            if (typeDecl.GenericParameters.Count > 0)
                return false;

            // For struct: a non-marker protocol conformance would require a
            // witness-table implementation on the type, which a namespace
            // cannot host. The parser auto-attaches Copyable + Escapable to
            // every Swift struct/enum (and Sendable for Sendable-eligible
            // types), so we filter the stdlib marker set before checking.
            if (typeDecl is StructDecl structDecl && HasNonMarkerConformance(structDecl.Conformances))
                return false;

            // A caseless enum is never lifted: it always projects as a static class
            // (EnumHandler.EmitNamespaceEnum), members or not. A newer SDK may give a
            // namespace enum its first static member (RealityFoundation's `Audio`
            // between Xcode 26.3 and 27.2), and a lifted namespace would then become a
            // type: every nested type's CLR name changes (`Audio.X` to `Audio+X`), so a
            // library compiled against one copy of a package cannot run with another
            // copy, and the merge refuses the package. A static class keeps the same
            // shape whichever members an SDK adds.
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
