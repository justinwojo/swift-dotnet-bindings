// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Emits typed KeyPath singleton trampolines for closed conformers of
/// PAT-constrained generic parent types.
///
/// <para>
/// Background. KeyPath cannot be originated at runtime from C#: the Swift runtime
/// exposes only <c>swift_getKeyPath</c>, which consumes a per-descriptor pattern
/// baked into a TU by the <c>keypath</c> SIL instruction. The compiler emits that
/// descriptor only at <c>\Root.prop</c> literal sites. Consequence: an IN-path
/// KeyPath argument must come from a generator-emitted Swift trampoline that
/// contains the literal. This emitter walks the closed conformers of each
/// PAT-constrained generic parent whose API surface includes
/// <c>KeyPath&lt;P.AssocType, V&gt;</c> parameters and emits, per stored property of
/// the conformer's nested associated-type bag:
/// </para>
/// <list type="bullet">
///   <item><description>One Swift <c>@_cdecl</c> trampoline returning the +1 retained
///   KeyPath via <c>Unmanaged.passRetained(\Root.prop).toOpaque()</c>.</description></item>
///   <item><description>One C# <c>public static</c> property surfacing a lazily-
///   initialized typed wrapper (<c>WritableKeyPath</c> for <c>var</c>; <c>KeyPath</c>
///   for <c>let</c>). Initialization is via <c>Lazy&lt;T&gt;</c>, matching the existing
///   enum-case singleton pattern.</description></item>
/// </list>
///
/// <para>
/// Container class shape: <c>{ConformerSan}{BagName}KeyPaths</c>. One container per
/// (closed conformer × nested-bag) pair, written at namespace scope alongside the
/// CSM <c>*CsmExtensions</c> classes.
/// </para>
///
/// <para>
/// Equality of two KeyPath instances is governed by <c>AnyKeyPath.==</c> (value-
/// equality on path content), never pointer identity — value equality, not pointer identity.
/// </para>
///
/// <para>
/// Open associated-type-rooted KeyPath parameters (Root is still
/// <c>P.AssocType</c>) are explicitly out of scope: the CSM path that substitutes
/// those parameters per conformer is not yet implemented. This emitter only
/// produces singletons keyed on the closed conformer's nested bag; the consumer
/// methods that take those singletons are emitted independently (today: via
/// concrete-rooted Swift methods in the fixture).
/// </para>
/// </summary>
internal static class KeyPathSingletonEmitter
{
    /// <summary>
    /// Entry point. Mirrors the call-site contract of
    /// <see cref="ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializationsForGenericParent"/>
    /// — call after the parent type's class body is closed, so the emitted
    /// container classes sit at namespace scope.
    /// </summary>
    public static void EmitKeyPathSingletonsForGenericParent(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        TypeDecl typeDecl,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        ConcreteSpecializationEngine engine,
        ILogger logger)
    {
        if (!typeDecl.IsGeneric) return;
        // Nested generic parents skip the closed-conformer path for the same reason
        // EmitConcreteSpecializationsForGenericParent skips them (CS1109 / closed-
        // receiver naming). Singletons follow that boundary.
        if (typeDecl.ParentDecl is TypeDecl) return;
        if (!WrapperValidation.IsXCFrameworkMode(typeDatabase)) return;
        if (typeDecl.ModuleDecl is null) return;

        var moduleDecl = typeDecl.ModuleDecl;
        var demand = CollectBagDemand(typeDecl, logger);
        if (demand.Count == 0) return;

        // Build a one-shot index: SwiftQualifiedName → TypeDecl. Conformer lookup
        // needs to walk nested types of the module; doing it once amortises the
        // O(N) walk across all (conformer, bag) pairs in this generic parent.
        var typeDeclByName = KeyPathBagWalker.BuildTypeDeclIndex(moduleDecl);

        foreach (var (genericParamName, protocolName, assocName) in demand)
        {
            var conformers = engine.GetConformers(protocolName);
            if (conformers.Count == 0) continue;

            foreach (var conformer in conformers)
            {
                if (conformer.SwiftType is null) continue;
                // A conformer TypeSkipPrePass withdrew stays in the type-decl index, so the
                // TryGetValue below still resolves it; naming it would emit a global::<type>
                // reference with no C# declaration (CS0234) and fail the whole binding closed.
                // Withdraw it here — the same shared oracle the CSM conformer gates use.
                if (ConcreteProtocolSpecializationEmitter.ConformerReferencesWithdrawnType(conformer)) continue;
                var conformerKey = conformer.SwiftQualifiedName;
                if (!typeDeclByName.TryGetValue(conformerKey, out var conformerDecl)) continue;

                // Single source of truth for bag resolution + per-property emittability
                // + value-type projection. Returns null if the bag can't be resolved,
                // fails IsEmittableBag (generic / SPI / internal / custom-actor /
                // class-bound protocol / Self-requirement protocol), or projects zero
                // properties. Walker logs the per-property unprojectable case; the
                // singleton emitter shares the same data path Route C consumes.
                var walk = KeyPathBagWalker.TryResolveProjectableBagProps(
                    conformer, conformerDecl, assocName, typeDecl,
                    typeDatabase, typeDeclByName, logger);
                if (walk is null)
                {
                    logger.LogDebug(
                        "KeyPath singletons: no projectable bag for conformer {Conformer} assoc {Assoc} (hints: {Hints}).",
                        conformerKey, assocName,
                        conformer.AssociatedTypes is { } m ? string.Join(",", m.Keys) : "<null>");
                    continue;
                }
                var bagDecl = walk.Value.BagDecl;

                // Module-level dedup. Two generic parents in the same module that both
                // demand KeyPath<Item.LibraryFilter, *> (or any same-shape pair) must
                // share one container, otherwise the second pass emits a duplicate C#
                // partial class member set (CS0102) and a duplicate set of
                // `SBW_KP_…` Swift @_cdecl symbols (linker error). The
                // ModuleEmissionContext registry is the single source of truth across
                // every TypeDecl handled in this module.
                var containerKey = $"{conformer.SwiftQualifiedName}|{bagDecl.SwiftTypeName!.ModuleQualifiedName}";
                if (!emissionContext.TryAddKeyPathSingletonContainer(containerKey)) continue;

                EmitContainer(csWriter, swiftWriter, typeDecl, conformer, conformerDecl,
                    walk.Value, typeDatabase, emissionContext, logger);
            }
        }
    }

    /// <summary>
    /// Demand entry: a generic-param name on the parent type, the protocol it's
    /// constrained to, and the associated-type name a method's KeyPath parameter
    /// rooted into. (<c>τ_0_0</c>, <c>SwiftBindingsTestLib.Session4_Filterable</c>,
    /// <c>LibraryFilter</c>).
    /// </summary>
    private readonly record struct BagDemand(string GenericParamName, SwiftTypeName Protocol, string AssocName);

    /// <summary>
    /// Walks <paramref name="typeDecl"/>'s methods/properties looking for
    /// <c>KeyPath&lt;τ_X_Y.AssocName, ...&gt;</c> parameters or returns. Each unique
    /// (gp, protocol, assocName) triple produces one demand entry. Demand drives
    /// container emission — no methods, no singletons.
    /// </summary>
    private static IReadOnlyList<BagDemand> CollectBagDemand(TypeDecl typeDecl, ILogger logger)
    {
        var demand = new HashSet<BagDemand>();

        // Pre-resolve {paramName → protocol} for each parent generic param. We index
        // by BOTH the canonical TypeName (`τ_0_0`) and the SugaredTypeName (`Item`)
        // because AssociatedTypeReferenceSpec.BaseType can carry either form
        // depending on which parser path produced the TypeSpec — the ABI JSON
        // populates the canonical form, but the swiftinterface fallback can keep
        // the source-level name. Indexing both forms makes the demand walk robust
        // to that variance.
        var protoByParam = new Dictionary<string, SwiftTypeName>(StringComparer.Ordinal);
        foreach (var gp in typeDecl.GenericParameters)
        {
            foreach (var conf in gp.GenericConformances)
            {
                if (conf.Kind != ConformanceKind.Protocol) continue;
                if (!string.IsNullOrEmpty(gp.TypeName))
                    protoByParam[gp.TypeName] = conf.ConformanceTarget;
                if (!string.IsNullOrEmpty(gp.SugaredTypeName))
                    protoByParam[gp.SugaredTypeName] = conf.ConformanceTarget;
                break;
            }
        }
        if (protoByParam.Count == 0) return Array.Empty<BagDemand>();

        // Methods: scan parameters and return type for KeyPath<param.assoc, *>.
        foreach (var method in typeDecl.Methods)
        {
            foreach (var arg in method.CSSignature)
                ScanTypeSpec(arg.SwiftTypeSpec, protoByParam, demand);
        }
        // Properties: scan their type specs the same way.
        foreach (var prop in typeDecl.Properties)
            ScanTypeSpec(prop.SwiftTypeSpec, protoByParam, demand);
        // Subscripts: scan their accessors' return / param types.
        foreach (var sub in typeDecl.Subscripts)
        {
            if (sub.Accessors is null) continue;
            foreach (var accessor in sub.Accessors)
                foreach (var arg in accessor.Method.CSSignature)
                    ScanTypeSpec(arg.SwiftTypeSpec, protoByParam, demand);
        }

        return demand.ToList();
    }

    private static void ScanTypeSpec(
        TypeSpec? spec,
        IReadOnlyDictionary<string, SwiftTypeName> protoByParam,
        HashSet<BagDemand> demand)
    {
        if (spec is null) return;

        if (spec is NamedTypeSpec named)
        {
            if (TypeProjectionFactory.IsKeyPathFamily(named.Name) && named.GenericParameters.Count >= 1)
            {
                // Inspect the Root generic argument for a τ_X_Y.AssocName shape. The
                // ABI parser produces two equivalent encodings of this pattern:
                //   (1) AssociatedTypeReferenceSpec with BaseType="τ_0_0",
                //       AssociatedTypeName="LibraryFilter" — seen when the parser
                //       takes the DependentMember branch in CreateTypeSpec.
                //   (2) NamedTypeSpec with Name="τ_0_0.LibraryFilter" (single dotted
                //       string) — seen for generic-parent methods with associated-
                //       type-rooted KeyPath params today. Recognise both.
                var root = named.GenericParameters[0];
                string? rootBase = null;
                string? rootAssoc = null;
                if (root is AssociatedTypeReferenceSpec atRef
                    && !string.IsNullOrEmpty(atRef.AssociatedTypeName))
                {
                    rootBase = atRef.BaseType;
                    rootAssoc = atRef.AssociatedTypeName;
                }
                else if (root is NamedTypeSpec rootNamed && root.GenericParameters.Count == 0)
                {
                    var dotIdx = rootNamed.Name.IndexOf('.');
                    if (dotIdx > 0 && dotIdx < rootNamed.Name.Length - 1)
                    {
                        rootBase = rootNamed.Name.Substring(0, dotIdx);
                        rootAssoc = rootNamed.Name.Substring(dotIdx + 1);
                    }
                }

                if (rootBase is not null
                    && rootAssoc is not null
                    && protoByParam.TryGetValue(rootBase, out var protocolName))
                {
                    demand.Add(new BagDemand(rootBase, protocolName, rootAssoc));
                }
            }
            // Recurse: KeyPath may itself appear as a generic argument of another
            // container (Optional<KeyPath<...>>, Array<KeyPath<...>>, etc.). Walk
            // all generic args so demand isn't lost behind a wrapping container.
            foreach (var arg in named.GenericParameters)
                ScanTypeSpec(arg, protoByParam, demand);
        }
        else if (spec is TupleTypeSpec tuple)
        {
            foreach (var elt in tuple.Elements)
                ScanTypeSpec(elt, protoByParam, demand);
        }
        else if (spec is ClosureTypeSpec closure)
        {
            ScanTypeSpec(closure.Arguments, protoByParam, demand);
            ScanTypeSpec(closure.ReturnType, protoByParam, demand);
        }
        // AssociatedTypeReferenceSpec at top level (without an outer KeyPath wrapper)
        // is not a demand site. ProtocolListTypeSpec has no nested type specs we care
        // about.
    }

    private static void EmitContainer(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        TypeDecl parentTypeDecl,
        ConcreteSpecializationEngine.ConcreteConformer conformer,
        TypeDecl conformerDecl,
        KeyPathBagWalker.BagWalkResult walk,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        ILogger logger)
    {
        var bagDecl = walk.BagDecl;
        var moduleName = parentTypeDecl.SwiftTypeName.Module;
        var wrapperLibPath = typeDatabase.AsyncLibraryName ?? "libSwiftBindings";
        var bagSwiftFullName = bagDecl.SwiftTypeName.ModuleQualifiedName;
        // Use QualifyForWrapperSource for the Swift-source spelling so modules with a
        // type sharing the module name (the well-known wrapper-source collision case
        // captured in ModuleEmissionContext.SetCollisionContext) don't mis-resolve the
        // bag's qualified name inside the `\Root.prop` literal.
        var bagSwiftQualifiedForWrapper = emissionContext.QualifyForWrapperSource(bagDecl.SwiftTypeName);
        var bagCSharpFullName = ResolveCSharpFullName(bagDecl, typeDatabase);
        if (bagCSharpFullName is null)
        {
            logger.LogDebug(
                "KeyPath singletons: bag {Bag} has no C# binding — skipping container for {Conformer}.",
                bagSwiftFullName, conformer.SwiftQualifiedName);
            return;
        }

        // Each ProjectedBagProperty already carries the value-type projection +
        // writability — both ran inside the walker. Layer per-property symbol
        // generation and merged availability on top.
        var emittable = new List<(PropertyDecl Prop, string PInvokeSymbol, string CSValueType,
            string SwiftValueType, bool IsWritable, IReadOnlyList<AvailabilityAnnotation>? MergedAvailability)>();
        foreach (var (prop, projection, isWritable) in walk.ProjectableProps)
        {
            var csValueType = projection.PublicType;
            var swiftValueType = prop.SwiftTypeSpec.ToString();
            // Same wrapper-source collision concern as the bag's Root spelling: a
            // value type referenced as `CollidingModule.Payload` inside the
            // `KeyPath<Root, Value>` annotation will mis-resolve when the wrapper
            // module name collides with a type name. Rewrite via the same helper.
            // Keep the raw spelling in the hash input so the symbol stays stable
            // across compilations (qualification is purely a source-text concern).
            var swiftValueTypeForWrapper = emissionContext.QualifyForWrapperSource(swiftValueType);

            var hashInput = $"{moduleName}|{conformer.SwiftQualifiedName}|{bagSwiftFullName}|{prop.Name}|{swiftValueType}";
            var hash = EmitterUtility.DeterministicHash8(hashInput);
            var conformerSan = SanitizeSymbol(conformer.SwiftQualifiedName);
            var bagSan = SanitizeSymbol(bagDecl.Name);
            var propSan = SanitizeSymbol(prop.Name);
            var symbol = $"SBW_KP_{moduleName}_{conformerSan}_{bagSan}_{propSan}_{hash}";

            // Merge property + bag-chain + conformer-record availability once. Both
            // the Swift trampoline (`@available(...) @_cdecl`) and the C# surface
            // (`[SupportedOSPlatform]` on the Lazy field + accessor) need the same
            // floor so the generated bindings agree with what swiftc actually
            // type-checks against the device SDK.
            var merged = WrapperEmitterHelpers.MergeAvailability(
                prop.AvailabilityAnnotations, prop.ParentDecl);
            if (conformer.AvailabilityAnnotations is { Count: > 0 } conformerAvailability)
            {
                var combined = merged is null
                    ? new List<AvailabilityAnnotation>()
                    : new List<AvailabilityAnnotation>(merged);
                combined.AddRange(conformerAvailability);
                merged = combined;
            }

            // The trampoline names the Value type in `KeyPath<Bag, Value>`. A Value gated above the
            // property/bag/conformer floor would leave the `@_cdecl` under-annotated → stripped at
            // wrapper-build → orphaned C# P/Invoke, so lift the floor here — the same merge the
            // AppEntity and EntityProperty-factory KeyPath emitters apply. Output-neutral while the
            // bag walk admits only stored properties: Swift forbids `@available` on stored properties,
            // so a stored Value can never out-live its container's floor (which `merged` already
            // carries). This guard becomes load-bearing the moment a computed bag property is admitted.
            if (KeyPathBagWalker.CollectValueTypeAvailability(prop.SwiftTypeSpec, typeDatabase)
                is { Count: > 0 } valueAvailability)
            {
                var combined = merged is null
                    ? new List<AvailabilityAnnotation>()
                    : new List<AvailabilityAnnotation>(merged);
                combined.AddRange(valueAvailability);
                merged = combined;
            }

            // Symbol-collision avoidance: the SBW_KP_ prefix is dedicated to this
            // emitter and does not overlap with SBW_ (method wrappers) or SBW_CSM_
            // (conformer specialisation wrappers). The hash input includes the
            // property's full Swift value type so two properties with the same
            // PascalCase name but different value-type spellings still get distinct
            // symbols.
            emittable.Add((prop, symbol, csValueType, swiftValueTypeForWrapper, isWritable, merged));
        }

        if (emittable.Count == 0) return;

        // Drop the conformer's module-namespace prefix when it equals the parent's
        // module (the singleton container lives in that same namespace, so the
        // prefix is redundant noise). For cross-module conformers we keep it for
        // disambiguation. Mirrors the design doc shape: `MockBookLibraryFilterKeyPaths`,
        // not `SwiftBindingsTestLib_MockBookLibraryFilterKeyPaths`.
        var conformerForName = StripModulePrefix(conformer.CSharpType, moduleName);
        var containerCsName = $"{SanitizeIdentifier(conformerForName)}{SanitizeIdentifier(bagDecl.Name)}KeyPaths";

        // C# container class — top-level, sibling to CSM extension classes.
        csWriter.WriteLine();
        csWriter.WriteLine($"// KeyPath singletons for {conformer.SwiftQualifiedName}.{bagDecl.Name}");
        csWriter.WriteLine($"// (consumer surface: {parentTypeDecl.SwiftTypeName.ModuleQualifiedName})");
        csWriter.WriteLine($"public static unsafe partial class {containerCsName}");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        var propRenames = NameProvider.ComputePropertyRenames(bagDecl, typeDatabase);
        foreach (var (prop, symbol, csValueType, swiftValueType, isWritable, mergedAvailability) in emittable)
        {
            var pascalName = NameProvider.GetFinalMemberName(
                NameProvider.GetPropertyName(prop.Name, bagDecl.Name), propRenames);
            var keyPathFlavor = isWritable ? "WritableKeyPath" : "KeyPath";
            var fieldType = $"global::Swift.{keyPathFlavor}<{bagCSharpFullName}, {csValueType}>";

            var pinvokeName = $"PInvoke_{symbol}";
            csWriter.WriteLine();
            // [SupportedOSPlatform] before the P/Invoke so CA1416 narrows the
            // P/Invoke surface to the same OS floor as the Swift trampoline.
            // NOTE: the singleton container is emitted at namespace scope, NOT
            // nested inside `parentTypeDecl`, so we must NOT dedupe against the
            // parent's annotations — if the bag/property/conformer floor happens
            // to equal the generic parent's floor, dedup would suppress the
            // attribute entirely and leave the top-level P/Invoke unguarded.
            // Pass null for the parent-annotation argument to disable dedup.
            AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(
                csWriter, mergedAvailability, parentAnnotations: null);
            csWriter.WriteLine($"[global::System.Runtime.InteropServices.DllImport(\"{wrapperLibPath}\", EntryPoint = \"{symbol}\", CallingConvention = global::System.Runtime.InteropServices.CallingConvention.Cdecl)]");
            csWriter.WriteLine($"private static extern IntPtr {pinvokeName}();");
            csWriter.WriteLine();

            // Lazy<T> matches the existing enum-case singleton pattern
            // (EnumHandler.RawRepresentable.cs). Lazy.Value is thread-safe per .NET
            // contract (LazyThreadSafetyMode.ExecutionAndPublication by default) and
            // pushes the first-touch Swift-runtime call off the static cctor —
            // important under NativeAOT, where eager static init can race the
            // SwiftFrameworkResolver init sequence.
            AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(
                csWriter, mergedAvailability, parentAnnotations: null);
            csWriter.WriteLine($"private static readonly Lazy<{fieldType}> _lazy_{pascalName} = new(() =>");
            csWriter.WriteLine($"    new {fieldType}({pinvokeName}()));");
            AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(
                csWriter, mergedAvailability, parentAnnotations: null);
            csWriter.WriteLine($"public static {fieldType} {pascalName}");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("get");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            // The KeyPath trampoline this P/Invokes is availability-gated (see EmitSwiftTrampolines).
            // On an OS below the merged floor, forcing _lazy_…Value runs the trampoline, whose body
            // dereferences a weak-linked, null gated symbol (uncatchable SIGSEGV). Throw a catchable
            // exception at the property boundary so the Lazy initializer never runs there.
            AvailabilityAttributeEmitter.EmitRuntimeAvailabilityGuard(
                csWriter, mergedAvailability, $"{containerCsName}.{pascalName}");
            csWriter.WriteLine($"return _lazy_{pascalName}.Value;");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();

        // Swift trampolines — one per emittable property. Routing through
        // swiftWriter directly matches the CSM pattern (CSM bypasses
        // WrapperEmitter.EmitMethod because there is no original MethodDecl).
        EmitSwiftTrampolines(swiftWriter, conformer, conformerDecl, bagDecl,
            bagSwiftQualifiedForWrapper, bagSwiftFullName, emittable);
    }

    private static void EmitSwiftTrampolines(
        SwiftWriter swiftWriter,
        ConcreteSpecializationEngine.ConcreteConformer conformer,
        TypeDecl conformerDecl,
        TypeDecl bagDecl,
        string bagSwiftQualifiedForWrapper,
        string bagSwiftFullNameForComment,
        IReadOnlyList<(PropertyDecl Prop, string PInvokeSymbol, string CSValueType,
            string SwiftValueType, bool IsWritable,
            IReadOnlyList<AvailabilityAnnotation>? MergedAvailability)> emittable)
    {
        foreach (var (prop, symbol, _, swiftValueType, isWritable, mergedAvailability) in emittable)
        {
            var keyPathFlavor = isWritable ? "WritableKeyPath" : "KeyPath";
            var swiftPropName = prop.GetSwiftName();

            swiftWriter.WriteLine();
            swiftWriter.WriteLine($"// KeyPath singleton trampoline: \\{bagSwiftFullNameForComment}.{swiftPropName}");
            // @available emitted from the same merged-availability list the C# side
            // consumes (property + bag/conformer ancestors + conformer-record hint).
            // Without these the wrapper's `\Root.prop` reference can fail to compile
            // on device SDKs whose deployment target is older than the wrapped
            // property — same failure mode CSM mitigates with the identical merge.
            WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, mergedAvailability);
            swiftWriter.WriteLine($"@_cdecl(\"{symbol}\")");
            swiftWriter.WriteLine($"public func {symbol}() -> UnsafeMutableRawPointer {{");
            swiftWriter.Indent++;
            // Type-annotate the literal so the Swift compiler picks the correct
            // flavour for a `var` (WritableKeyPath) without us depending on type
            // inference; the upcast to KeyPath in the read-only case is explicit.
            // The literal itself emits the `keypath` SIL instruction; without it,
            // there is no way for C# to obtain a KeyPath at runtime.
            swiftWriter.WriteLine($"let kp: {keyPathFlavor}<{bagSwiftQualifiedForWrapper}, {swiftValueType}> = \\{bagSwiftQualifiedForWrapper}.{swiftPropName}");
            swiftWriter.WriteLine("return Unmanaged.passRetained(kp).toOpaque()");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
        }
    }

    internal static string? ResolveCSharpFullName(TypeDecl bagDecl, ITypeDatabase typeDatabase)
    {
        // Prefer the TypeRecord's canonical C# name (handles renames, nested-type
        // collisions, and module-namespace mapping). Fall back to building the
        // fully-qualified name from the parsed TypeDecl chain.
        if (typeDatabase.TryGetTypeRecord(bagDecl.SwiftTypeName, out var record) && record is not null)
            return record.CSharpTypeName.FullyQualifiedName;

        // Fallback chain. Module.Outer.Inner.Bag → global::Module.Outer.Inner.Bag.
        var parts = new List<string>();
        BaseDecl? cursor = bagDecl;
        while (cursor is TypeDecl td)
        {
            parts.Insert(0, td.Name);
            cursor = td.ParentDecl;
        }
        var moduleName = bagDecl.ModuleDecl?.Name ?? bagDecl.SwiftTypeName.Module;
        parts.Insert(0, moduleName);
        return $"global::{string.Join(".", parts)}";
    }

    /// <summary>
    /// Sanitise a Swift qualified name (e.g. <c>Module.Type.Nested</c>) into a flat,
    /// symbol-safe token. Mirrors <c>ConcreteProtocolSpecializationEmitter.SanitizeTypeName</c>
    /// but keeps a flat character set safe for cdecl symbol names — no angle brackets,
    /// no commas, no whitespace.
    /// </summary>
    internal static string SanitizeSymbol(string name)
        => name.Replace(".", "_").Replace("<", "_").Replace(">", "")
               .Replace(",", "_").Replace(" ", "").Replace("[", "Arr_").Replace("]", "");

    /// <summary>
    /// Strip a leading <c>{module}.</c> from a C# fully-qualified type name if it
    /// matches the parent module. Used for the singleton container class name —
    /// the container lives in the parent module's namespace, so the prefix is
    /// redundant. Conformers from other modules keep their module qualifier (with
    /// the dot sanitised by <see cref="SanitizeIdentifier"/>) so the container
    /// names remain unique across cross-module conformers.
    /// </summary>
    internal static string StripModulePrefix(string cSharpType, string moduleName)
    {
        var prefix = moduleName + ".";
        return cSharpType.StartsWith(prefix, StringComparison.Ordinal)
            ? cSharpType.Substring(prefix.Length)
            : cSharpType;
    }

    internal static string SanitizeIdentifier(string name)
    {
        // Used for C# class-name segments only. Drop characters that can't appear
        // in a C# identifier — same approach as SanitizeTypeName in CSM. Avoids
        // any cross-contamination with the symbol-name sanitiser above so the
        // identifier and the cdecl symbol share a parsing contract.
        return name.Replace(".", "_").Replace("<", "_").Replace(">", "")
                   .Replace(",", "_").Replace(" ", "").Replace("[", "Arr_").Replace("]", "");
    }
}
