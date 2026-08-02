// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Emits typed KeyPath singleton trampolines rooted directly on the
/// closed <c>AppIntents.AppEntity</c> conformers declared in the module being emitted.
///
/// <para>
/// Motivation. AppIntents' <c>EntityProperty&lt;Value&gt;</c> exposes a family of
/// KeyPath-keyed convenience inits — <c>init&lt;Entity&gt;(identifier:getter: KeyPath&lt;Entity, Value&gt;)
/// where Entity : AppEntity</c> and siblings. The <c>getter:</c> / <c>getSetter:</c>
/// argument must be a real <c>KeyPath&lt;Conformer, Value&gt;</c>, and KeyPath cannot be
/// originated at runtime from C# (see <see cref="KeyPathSingletonEmitter"/> for the
/// full rationale): the <c>keypath</c> SIL descriptor is only emitted at a Swift
/// <c>\Root.prop</c> literal site. This emitter produces those literals as
/// per-conformer <c>@_cdecl</c> trampolines so the typed singletons exist for the
/// consumer to pass into <c>EntityProperty</c> construction.
/// </para>
///
/// <para>
/// Shape comparison. <see cref="KeyPathSingletonEmitter"/> roots its KeyPaths in a
/// PAT-constrained generic parent's <i>nested associated-type bag</i> and is driven by
/// that parent's <c>KeyPath&lt;P.Assoc, *&gt;</c> parameter demand. Here the Root is the
/// <b>conformer itself</b> (<c>KeyPath&lt;MockBook, Int&gt;</c>, not <c>KeyPath&lt;MockBook.SomeBag, Int&gt;</c>),
/// so there is no bag to resolve — we walk the conformer's own storage properties.
/// The driver is module-scope: every closed <c>AppEntity</c> conformer local to the
/// module emits one <c>{ConformerSan}AppEntityKeyPaths</c> container.
/// </para>
///
/// <para>
/// Conformer visibility is intra-module (the
/// <see cref="ConcreteSpecializationEngine"/> indexes only the module being emitted,
/// plus <c>specialization-hints.json</c>). We additionally require the conformer's
/// <see cref="TypeDecl"/> to be present in this module's type tree, because the
/// <c>\Conformer.prop</c> literal and the conformer's C# type must both live in the
/// emitted assembly + its wrapper TU. Cross-module / cross-assembly conformer
/// enumeration is a documented v1 limitation.
/// </para>
///
/// <para>
/// The closed <c>EntityProperty</c> <i>construction</i> surface (factory methods that
/// consume these singletons) is intentionally NOT emitted here — it requires a managed
/// <c>EntityProperty&lt;TValue&gt;</c> type in the consumer's assembly graph, which is a
/// separate prerequisite. These KeyPath singletons are useful standalone and are the
/// bulk of the AppEntity KeyPath surface.
/// </para>
/// </summary>
internal static class AppEntityKeyPathSingletonEmitter
{
    private const string AppEntityProtocolName = "AppIntents.AppEntity";

    /// <summary>
    /// Module-scope entry point. Call after the module's type walk completes, at
    /// namespace scope (alongside the foreign-extension / CSM container classes), so
    /// the emitted <c>{Conformer}AppEntityKeyPaths</c> classes sit beside the
    /// conformer's own generated type.
    /// </summary>
    public static void EmitForModule(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        ModuleDecl moduleDecl,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        ConcreteSpecializationEngine? engine,
        ILogger logger)
    {
        if (engine is null) return;
        // KeyPath singletons are wrapper-trampoline-backed; only meaningful when we
        // are emitting a real wrapper dylib (mirrors KeyPathSingletonEmitter's gate).
        if (!WrapperValidation.IsXCFrameworkMode(typeDatabase)) return;

        var conformers = engine.GetConformers(SwiftTypeName.FromModuleQualifiedName(AppEntityProtocolName));
        if (conformers.Count == 0) return;

        // SwiftQualifiedName → TypeDecl over the module's types. A successful lookup
        // is also our "is this conformer local to the emitted module?" gate.
        var typeDeclByName = KeyPathBagWalker.BuildTypeDeclIndex(moduleDecl);

        foreach (var conformer in conformers)
        {
            if (conformer.SwiftType is null) continue;
            // A conformer TypeSkipPrePass withdrew stays in the type-decl index; naming it would
            // emit a global::<type> reference with no C# declaration (CS0234). Withdraw it here —
            // the same shared oracle the CSM conformer gates use.
            if (ConcreteProtocolSpecializationEmitter.ConformerReferencesWithdrawnType(conformer)) continue;
            if (!typeDeclByName.TryGetValue(conformer.SwiftQualifiedName, out var conformerDecl)) continue;
            if (!IsEligibleConformerType(conformerDecl)) continue;

            // Module-scope dedup. Keyed on the conformer alone (Root = conformer), with
            // an `AppEntity|` namespace so it never collides with the nested-bag
            // `{conformer}|{bag}` container keys in the same registry.
            var containerKey = $"AppEntity|{conformer.SwiftQualifiedName}";
            if (!emissionContext.TryAddKeyPathSingletonContainer(containerKey)) continue;

            EmitContainer(csWriter, swiftWriter, conformer, conformerDecl,
                typeDatabase, emissionContext, logger);
        }
    }

    /// <summary>
    /// Gate a candidate <c>AppEntity</c> conformer for singleton emission. The Root of
    /// <c>KeyPath&lt;Root, V&gt;</c> must be a single closed type whose stored properties we
    /// can reach from the emitted assembly:
    /// <list type="bullet">
    ///   <item>Generic conformers (<c>Foo&lt;T&gt; : AppEntity</c>) have no single closed
    ///   Root — <c>\Foo&lt;T&gt;.prop</c> is not a concrete literal.</item>
    ///   <item>SPI / module-internal conformers can't be referenced from the public
    ///   <c>{Conformer}AppEntityKeyPaths</c> container or the wrapper TU.</item>
    /// </list>
    /// Protocols and enums fall out naturally downstream (no stored properties to walk),
    /// so they are not gated here. Caller is responsible for the local-to-module check
    /// (presence in the module's type-decl index).
    /// </summary>
    internal static bool IsEligibleConformerType(TypeDecl conformerDecl)
    {
        if (conformerDecl.IsGeneric) return false;
        if (conformerDecl.IsSpiProtected || conformerDecl.IsModuleInternal) return false;
        return true;
    }

    private static void EmitContainer(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        ConcreteSpecializationEngine.ConcreteConformer conformer,
        TypeDecl conformerDecl,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        ILogger logger)
    {
        var moduleName = conformerDecl.SwiftTypeName.Module;
        var wrapperLibPath = typeDatabase.AsyncLibraryName ?? "libSwiftBindings";
        var rootSwiftFullName = conformerDecl.SwiftTypeName.ModuleQualifiedName;
        // Same wrapper-source module-name collision concern as the nested-bag singletons: rewrite the
        // Root spelling used inside the `\Root.prop` literal.
        var rootSwiftQualifiedForWrapper = emissionContext.QualifyForWrapperSource(conformerDecl.SwiftTypeName);
        var rootCSharpFullName = KeyPathSingletonEmitter.ResolveCSharpFullName(conformerDecl, typeDatabase);
        if (rootCSharpFullName is null)
        {
            logger.LogDebug(
                "AppEntity KeyPath singletons: conformer {Conformer} has no C# binding — skipping.",
                conformer.SwiftQualifiedName);
            return;
        }

        var projector = new TypeProjectionFactory();
        var emittable = new List<(PropertyDecl Prop, string Symbol, string CSValueType,
            string SwiftValueType, bool IsWritable, IReadOnlyList<AvailabilityAnnotation>? MergedAvailability)>();

        foreach (var prop in conformerDecl.Properties)
        {
            // allowAbstract: false — the conformer is a concrete nominal, not a protocol.
            // allowComputed: true — unlike the nested-bag scenario (where only
            // stored bag fields are KeyPath leaves), a concrete root forms valid KeyPaths
            // for computed properties too: `\Root.getOnly` is a `KeyPath` and
            // `\Root.getSet` is a `WritableKeyPath`. AppEntity conformers commonly expose
            // computed properties (e.g. a derived `var fullName: String`), so admit them.
            if (!KeyPathBagWalker.IsEmittableProperty(prop, allowAbstract: false, allowComputed: true)) continue;

            var projection = projector.Project(prop.SwiftTypeSpec, new ProjectionContext
            {
                TypeDatabase = typeDatabase,
                IsParameter = false,
                CurrentModuleName = moduleName,
            });
            if (projection is null)
            {
                logger.LogDebug(
                    "AppEntity KeyPath singletons: property {Prop} of {Conformer} unprojectable {Type} — skipping.",
                    prop.Name, conformer.SwiftQualifiedName, prop.SwiftTypeSpec);
                continue;
            }

            var csValueType = projection.PublicType;
            var swiftValueType = prop.SwiftTypeSpec.ToString();
            var swiftValueTypeForWrapper = emissionContext.QualifyForWrapperSource(swiftValueType);
            bool isWritable = prop.Accessors.OfType<SetAccessorDecl>().Any();

            var hashInput = $"{moduleName}|{conformer.SwiftQualifiedName}|AppEntity|{prop.Name}|{swiftValueType}";
            var hash = EmitterUtility.DeterministicHash8(hashInput);
            var conformerSan = KeyPathSingletonEmitter.SanitizeSymbol(conformer.SwiftQualifiedName);
            var propSan = KeyPathSingletonEmitter.SanitizeSymbol(prop.Name);
            // SBW_KP_AppEntity_ prefix is dedicated to this emitter — disjoint from
            // SBW_KP_ (nested-bag singletons), SBW_ (method wrappers), and
            // SBW_CSM_ (conformer specialization wrappers).
            var symbol = $"SBW_KP_AppEntity_{moduleName}_{conformerSan}_{propSan}_{hash}";

            // Merge property + ancestor + conformer-record availability so the Swift
            // trampoline's `@available` floor and the C# `[SupportedOSPlatform]` agree
            // with what swiftc type-checks against the device SDK. For a writable path the
            // literal `\Root.prop` references the setter, so when the setter carries a
            // tighter floor (getter iOS 17.0 / setter iOS 17.4) use the parser's
            // setter-specific list — matching PropertyHandler's setter-accessor guard —
            // so the WritableKeyPath isn't exposed under the looser getter floor.
            var memberAvailability =
                isWritable && prop.SetterAvailabilityAnnotations is { Count: > 0 } setterAvailability
                    ? setterAvailability
                    : prop.AvailabilityAnnotations;
            var merged = WrapperEmitterHelpers.MergeAvailability(memberAvailability, prop.ParentDecl);
            if (conformer.AvailabilityAnnotations is { Count: > 0 } conformerAvailability)
            {
                var combined = merged is null
                    ? new List<AvailabilityAnnotation>()
                    : new List<AvailabilityAnnotation>(merged);
                combined.AddRange(conformerAvailability);
                merged = combined;
            }
            // The trampoline names the Value type (`as KeyPath<Root, Value>`); a Value gated to
            // a later OS than the property/conformer must lift the floor or the `@_cdecl` is
            // stripped at wrapper-build, orphaning the C# P/Invoke.
            if (KeyPathBagWalker.CollectValueTypeAvailability(prop.SwiftTypeSpec, typeDatabase)
                is { Count: > 0 } valueAvailability)
            {
                var combined = merged is null
                    ? new List<AvailabilityAnnotation>()
                    : new List<AvailabilityAnnotation>(merged);
                combined.AddRange(valueAvailability);
                merged = combined;
            }

            emittable.Add((prop, symbol, csValueType, swiftValueTypeForWrapper, isWritable, merged));
        }

        if (emittable.Count == 0) return;

        var conformerForName = KeyPathSingletonEmitter.StripModulePrefix(conformer.CSharpType, moduleName);
        var containerCsName = $"{KeyPathSingletonEmitter.SanitizeIdentifier(conformerForName)}AppEntityKeyPaths";

        csWriter.WriteLine();
        csWriter.WriteLine($"// AppEntity KeyPath singletons for {conformer.SwiftQualifiedName}");
        csWriter.WriteLine($"// (KeyPath roots for AppIntents EntityProperty / IntentParameter convenience inits)");
        csWriter.WriteLine($"public static unsafe partial class {containerCsName}");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        var propRenames = NameProvider.ComputePropertyRenames(conformerDecl, typeDatabase);
        foreach (var (prop, symbol, csValueType, _, isWritable, mergedAvailability) in emittable)
        {
            var pascalName = NameProvider.GetFinalMemberName(
                NameProvider.GetPropertyName(prop, conformerDecl.Name), propRenames);
            var keyPathFlavor = isWritable ? "WritableKeyPath" : "KeyPath";
            var fieldType = $"global::Swift.{keyPathFlavor}<{rootCSharpFullName}, {csValueType}>";
            var pinvokeName = $"PInvoke_{symbol}";

            csWriter.WriteLine();
            // [SupportedOSPlatform] on each member; parentAnnotations: null because the
            // container is at namespace scope (NOT nested in the conformer), so dedup
            // against the conformer's floor would wrongly suppress the attribute.
            AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(
                csWriter, mergedAvailability, parentAnnotations: null);
            csWriter.WriteLine($"[global::System.Runtime.InteropServices.DllImport(\"{wrapperLibPath}\", EntryPoint = \"{symbol}\", CallingConvention = global::System.Runtime.InteropServices.CallingConvention.Cdecl)]");
            csWriter.WriteLine($"private static extern IntPtr {pinvokeName}();");
            csWriter.WriteLine();

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

        EmitSwiftTrampolines(swiftWriter, rootSwiftQualifiedForWrapper, rootSwiftFullName, emittable);
    }

    private static void EmitSwiftTrampolines(
        SwiftWriter swiftWriter,
        string rootSwiftQualifiedForWrapper,
        string rootSwiftFullNameForComment,
        IReadOnlyList<(PropertyDecl Prop, string Symbol, string CSValueType,
            string SwiftValueType, bool IsWritable,
            IReadOnlyList<AvailabilityAnnotation>? MergedAvailability)> emittable)
    {
        foreach (var (prop, symbol, _, swiftValueType, isWritable, mergedAvailability) in emittable)
        {
            var keyPathFlavor = isWritable ? "WritableKeyPath" : "KeyPath";
            var swiftPropName = prop.GetSwiftName();

            swiftWriter.WriteLine();
            swiftWriter.WriteLine($"// AppEntity KeyPath singleton trampoline: \\{rootSwiftFullNameForComment}.{swiftPropName}");
            WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, mergedAvailability);
            swiftWriter.WriteLine($"@_cdecl(\"{symbol}\")");
            swiftWriter.WriteLine($"public func {symbol}() -> UnsafeMutableRawPointer {{");
            swiftWriter.Indent++;
            swiftWriter.WriteLine($"let kp: {keyPathFlavor}<{rootSwiftQualifiedForWrapper}, {swiftValueType}> = \\{rootSwiftQualifiedForWrapper}.{swiftPropName}");
            swiftWriter.WriteLine("return Unmanaged.passRetained(kp).toOpaque()");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
        }
    }
}
