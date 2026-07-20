// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using BindingsGeneration;

namespace BindingsGeneration
{
    /// <summary>
    /// Collects, from a single module's parsed declarations, the set of OTHER module names it
    /// references through the exact foreign-type-record lookups the layout/hierarchy finalizer
    /// (<see cref="ModuleProcessor"/>) performs during finalization. The result is the finalize-order
    /// edge set for <see cref="DependencyFinalizationPlanner"/>.
    /// </summary>
    /// <remarks>
    /// This is a deliberately STRICT SUBSET of every conceivable cross-module reference: an omitted
    /// edge only forgoes a possible reordering fix (safe), whereas a spurious edge could reorder an
    /// already-valid supplied order and break the byte-identity invariant (the worst outcome).
    /// Byte identity is load-bearing here specifically because finalize order is EMISSION-order
    /// relevant, not merely a record-availability question: the KeyPath-init factory emitter, for
    /// one, collects init shapes across dependencies in finalize order and emits them in that order
    /// (<c>ConformerKeyPathInitFactoryEmitter</c>), so reversing two dependencies' finalize order
    /// reverses generated declarations. An over-approximated edge that reorders a valid input is
    /// therefore a genuine output change, not a content-neutral one. Only these reference shapes
    /// cause the finalizer to read a FOREIGN type's record with NO data-dependent short-circuit
    /// that could skip the lookup, so only these are emitted as edges:
    /// <list type="number">
    ///   <item>a <b>frozen</b> struct's stored-property type — <c>CacluateFlags</c> looks up the
    ///     property record to propagate Frozen/RequiresMemoryManagement/float/bool flags, and it
    ///     processes EVERY non-static named property (no early return past the first), so each is a
    ///     real edge. A non-frozen struct returns before any property lookup, so its property types
    ///     are NOT an edge; only a directly-named property type is looked up (the finalizer skips
    ///     tuple- and existential-typed properties), and of the generic containers it unwraps
    ///     EXACTLY ONE Optional level to look up the immediate inner record — it does not recurse
    ///     into a nested Optional/container payload, and Array/Dictionary/Set/pointer yield a fixed
    ///     layout without an element lookup;</item>
    ///   <item>a class's <b>direct, non-ObjC, non-generic superclass that does NOT resolve within
    ///     this module</b> — <c>ResolveClassHierarchy</c> looks up only <c>DirectSuperclassName</c>
    ///     (never the transitive ancestor chain), skips a generic-instantiated parent name (one
    ///     containing <c>&lt;</c>), and skips an ObjC-rooted parent (<c>HasObjCSuperclass</c>) before
    ///     any record lookup. Crucially, the finalizer reaches the FOREIGN
    ///     <c>TryGetTypeRecord(parent)</c> only AFTER two same-module resolution passes miss: it
    ///     resolves the parent against this module's own class set (<c>classesByName</c>, keyed by
    ///     module-qualified name) both by the <c>DirectSuperclassName</c> string and by the class
    ///     USR (<c>TryResolveSuperclassByUsr</c> → <c>TryParseSwiftClassUsr</c>), setting
    ///     <c>ResolvedSuperclass</c> and short-circuiting the foreign lookup. A module can legally
    ///     RETAIN a foreign class (an ownership re-export where this module contributes extension
    ///     members), so a superclass whose name looks cross-module can still resolve same-module —
    ///     including the umbrella case where the name is <c>RealityKit.Entity</c> but the USR keys
    ///     the parent under <c>RealityFoundation.Entity</c> which this module retains. An edge is
    ///     emitted ONLY when neither same-module pass resolves, i.e. only when the finalizer will
    ///     actually read a foreign record — mirrored precisely because this scanner's own recursion
    ///     visits exactly the class set the finalizer builds <c>classesByName</c> from, and it reuses
    ///     the finalizer's own <c>ModuleProcessor.TryParseSwiftClassUsr</c>. A class's own
    ///     stored-property layout is not order-sensitive (reference type), so class property types
    ///     are NOT an edge. One deliberate UNDER-approximation remains here: after a same-module
    ///     resolve, the finalizer's <c>IsObjCRooted</c> fixed-point can STILL do a foreign
    ///     <c>TryGetTypeRecord(DirectSuperclassName)</c> when the resolved parent is not itself
    ///     ObjC-rooted — a genuine order-sensitive read that this suppression forgoes. Emitting an
    ///     edge for it would demand replicating the whole fixed-point (fragile), and forgoing it is
    ///     safe: it only reproduces the historical sequential loop's behavior for that supplied order
    ///     (no reorder invented, so no byte change), at most declining to FIX an ObjC-rooted
    ///     mislabel. Reopen trigger: a real library where a class extends a retained foreign parent
    ///     that is ObjC-rooted only transitively and loses its IsObjCRooted flag under a
    ///     dependent-first supplied order.</item>
    /// </list>
    /// Everything else the parser can express is intentionally NOT walked, because the finalizer
    /// either consumes no foreign record for it — enum associated-value payloads (only a boolean is
    /// read), all nominal conformances (name-only, no record lookup), tuple elements, non-Optional
    /// generic arguments, pointer element types — OR consumes it only behind a data-dependent
    /// short-circuit that cannot be predicted here without over-approximating. The latter is why a
    /// <b>protocol's inherited protocol</b> is NOT an edge even though the finalizer sometimes looks
    /// its record up: the <c>ProtocolIsClassBoundTransitive</c> / <c>ProtocolInheritsCodable</c>
    /// walks short-circuit (on <c>IsClassBound</c>, an earlier <c>AnyObject</c>, or an earlier
    /// Codable-family name) and can return BEFORE reaching a later inherited foreign protocol,
    /// skipping its record — so an unconditional inherited-protocol edge would be a SUPERSET of the
    /// finalizer's actual lookups. Modeling the short-circuit precisely would require replicating
    /// both recursive walks' internal ordering (fragile coupling that could itself drift into
    /// over-approximation), so the whole shape is dropped: this only forgoes a reordering fix that
    /// the historical sequential loop never provided either (no regression), and preserves strict
    /// subset. Reopen trigger: a real library that supplies a dependency whose protocol inherits a
    /// LATER-supplied dependency's protocol and loses a ClassBound/Codable flag as a result.
    /// </remarks>
    internal static class DependencyReferenceScanner
    {
        /// <summary>
        /// Returns the module names referenced by <paramref name="moduleDecl"/> through the
        /// finalizer's order-dependent foreign-record lookups, excluding the module's own name.
        /// Names are the bare module component (e.g. <c>Foundation</c>).
        /// </summary>
        public static IReadOnlyCollection<string> ReferencedModules(ModuleDecl moduleDecl)
        {
            // Mirror the finalizer's same-module class set exactly: ResolveClassHierarchy builds
            // classesByName from the module's TypeDecls (top-level AND nested), keyed by
            // SwiftTypeName.ModuleQualifiedName, and resolves a superclass against it before ever
            // reading a foreign record. The parser populates that TypeDecls set from precisely the
            // classes reachable by the same Types-recursion this scanner walks, so collecting every
            // ClassDecl's module-qualified name here reproduces classesByName's key set — letting the
            // superclass arm suppress an edge whenever the finalizer would resolve same-module.
            var localClassNames = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var type in moduleDecl.Types)
                CollectClassNames(type, localClassNames);

            var result = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var type in moduleDecl.Types)
                ScanType(type, moduleDecl.Name, localClassNames, result);
            result.Remove(moduleDecl.Name);
            return result;
        }

        private static void CollectClassNames(TypeDecl type, HashSet<string> acc)
        {
            if (type is ClassDecl)
                acc.Add(type.SwiftTypeName.ModuleQualifiedName);
            foreach (var nested in type.Types)
                CollectClassNames(nested, acc);
        }

        private static void ScanType(
            TypeDecl type, string ownModule, HashSet<string> localClassNames, HashSet<string> acc)
        {
            switch (type)
            {
                case StructDecl s when s.IsFrozen:
                    // Only a frozen struct's stored-property foreign records are consumed
                    // (ModuleProcessor.CacluateFlags); a non-frozen struct returns before the
                    // property loop, so its property types are not an order edge.
                    ScanFrozenStructProperties(s.Properties, ownModule, acc);
                    break;
                case ClassDecl c:
                    // A class's own field layout is not order-sensitive; only the DIRECT
                    // superclass record is consumed (ResolveClassHierarchy looks up
                    // DirectSuperclassName, i.e. SuperclassNames[0], never the transitive
                    // ancestors), and only when the name is not a generic instantiation — the
                    // finalizer skips a parent name containing '<' (parity with RegisterClassType).
                    // An ObjC-rooted parent (HasObjCSuperclass, USR "c:") is ALSO not an edge: the
                    // finalizer's cross-module link and its IsObjCRooted fixed-point both short-
                    // circuit on HasObjCSuperclass BEFORE any TryGetTypeRecord, and an ObjC parent
                    // is never a Swift Class record anyway — so a Swift class deriving from an ObjC
                    // class exposed by another supplied (mixed) dependency consumes no foreign Swift
                    // superclass record and imposes no finalize-order constraint.
                    //
                    // Finally — and this is the load-bearing subset guard — the finalizer reaches the
                    // foreign TryGetTypeRecord ONLY after two same-module resolution passes miss:
                    // classesByName.TryGetValue(DirectSuperclassName) and TryResolveSuperclassByUsr
                    // (USR → canonical module-qualified name, looked up in the SAME local class set).
                    // A module can RETAIN a foreign class it extends, so a cross-module-LOOKING parent
                    // name (or an umbrella name like RealityKit.Entity whose USR keys the retained
                    // parent under RealityFoundation.Entity) can resolve same-module and consume NO
                    // foreign record. Emitting an edge in that case would be a spurious over-approx
                    // that could reorder a valid supplied order and change emitted bytes. So suppress
                    // the edge whenever either same-module pass resolves, reusing the finalizer's own
                    // TryParseSwiftClassUsr to stay in lockstep.
                    var directSuper = c.DirectSuperclassName;
                    if (directSuper != null && !directSuper.Contains('<') && !c.HasObjCSuperclass
                        && !ResolvesSameModule(directSuper, c.SuperclassUsr, localClassNames))
                        AddModuleOf(directSuper, ownModule, acc);
                    break;
                // ProtocolDecl contributes no order edge. The finalizer DOES look up a cross-module
                // inherited protocol's record during ClassBound/Codable flag propagation, but only
                // behind a data-dependent short-circuit (ProtocolIsClassBoundTransitive /
                // ProtocolInheritsCodable return on IsClassBound, an earlier AnyObject, or an earlier
                // Codable-family name and may never reach a later inherited foreign protocol). An
                // unconditional inherited-protocol edge would therefore be a SUPERSET of the actual
                // lookups, and — because finalize order is emission-order relevant (see the type
                // remarks) — a spurious reorder can change generated bytes. Predicting the
                // short-circuit would require replicating both recursive walks (fragile), so the edge
                // is dropped: strict subset preserved, forgoing only a fix the historical sequential
                // loop never provided (no regression). See the reopen trigger in the type remarks.
                //
                // EnumDecl likewise contributes no order edge: CalculateEnumFlags reads only a
                // boolean (HasAssociatedValueCases), never a payload record.
            }

            // Nested types are finalized in the same module pass, so their edges bind this module.
            foreach (var nested in type.Types)
                ScanType(nested, ownModule, localClassNames, acc);
        }

        /// <summary>
        /// True when the finalizer would resolve <paramref name="directSuper"/> against this module's
        /// own class set — by name or by USR — and therefore NOT read a foreign superclass record.
        /// Mirrors <c>ModuleProcessor.ResolveClassHierarchy</c>'s two same-module passes exactly:
        /// <c>classesByName.TryGetValue(DirectSuperclassName)</c> then
        /// <c>TryResolveSuperclassByUsr</c> (reused here so the two cannot drift).
        /// </summary>
        private static bool ResolvesSameModule(
            string directSuper, string? superclassUsr, HashSet<string> localClassNames)
        {
            if (localClassNames.Contains(directSuper))
                return true;
            return superclassUsr is string usr
                && ModuleProcessor.TryParseSwiftClassUsr(usr, out var canonical)
                && localClassNames.Contains(canonical);
        }

        private static void ScanFrozenStructProperties(
            IEnumerable<PropertyDecl> properties, string ownModule, HashSet<string> acc)
        {
            foreach (var property in properties)
            {
                if (property.IsStatic)
                    continue;
                ScanFrozenPropertySpec(property.SwiftTypeSpec, ownModule, acc);
            }
        }

        private static void ScanFrozenPropertySpec(TypeSpec? spec, string ownModule, HashSet<string> acc)
        {
            // The finalizer only looks up a NamedTypeSpec property type; tuple- and
            // existential-typed properties are skipped (they are not a NamedTypeSpec, or are IsAny).
            if (spec is not NamedTypeSpec named || named.IsAny)
                return;

            // The outer nominal's module is looked up directly (CacluateFlags). For a foreign
            // generic type (Foreign.Box<...>) that is the foreign module; for a Swift container
            // wrapper (Swift.Array/Swift.Optional) that is Swift, which is never a finalizable dep.
            AddModuleOf(named.Name, ownModule, acc);

            // The finalizer unwraps EXACTLY ONE Optional level: it looks up the immediate payload's
            // record (ClassifyFieldType, single TryGetTypeRecord on the inner) to classify the
            // field, but it never recurses into a nested Optional/container payload. So only the
            // immediate inner NAMED type's module is an edge — NO recursion. (A doubly-Optional
            // `Foreign.Value??` has an immediate inner of Swift.Optional, not Foreign, so the
            // finalizer never looks Foreign up; every other container/pointer inner is likewise
            // not looked up.)
            if (named.Name == "Swift.Optional"
                && named.GenericParameters.Count == 1
                && named.GenericParameters[0] is NamedTypeSpec inner
                && !inner.IsAny)
            {
                AddModuleOf(inner.Name, ownModule, acc);
            }
        }

        private static void AddModuleOf(string? qualifiedName, string ownModule, HashSet<string> acc)
        {
            if (string.IsNullOrEmpty(qualifiedName))
                return;
            int dot = qualifiedName.IndexOf('.');
            if (dot <= 0)
                return;
            var module = qualifiedName.Substring(0, dot);
            if (module != ownModule)
                acc.Add(module);
        }
    }
}
