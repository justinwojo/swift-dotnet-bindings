// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Emits consumer-side factory methods that construct a
/// <i>framework-dependency</i>'s generic reference type via its method-own-generic,
/// KeyPath-keyed initializer, closing the method generic to a concrete local
/// conformer.
///
/// <para>
/// Motivation. AppIntents' <c>EntityProperty&lt;Value&gt;</c> (and its minimal
/// stand-in <c>MiniEntityProperty&lt;Value&gt;</c> in BindingTests) only offers
/// KeyPath-keyed inits of the shape
/// <c>init&lt;Entity&gt;(identifier: …, getter: KeyPath&lt;Entity, Value&gt;) where Entity : AppEntity</c>.
/// Those inits <b>tombstone</b> in the dependency's own binding: C# has no generic
/// constructors with method-own type parameters, and the generic <c>Entity</c> can't
/// satisfy the C# <c>ISwiftObject</c> constraint the binding would impose. This emitter
/// rescues them from the <i>consumer</i> side: for each local conformer of the init's
/// generic constraint (e.g. <c>MockBook : AppEntity</c>) it closes <c>Entity</c> to that
/// concrete type, emitting a static factory in the consumer assembly that builds
/// <c>{Dep}.MiniEntityProperty&lt;Value&gt;</c> through a Swift <c>@_cdecl</c> trampoline
/// and adopts the returned <c>+1</c> ARC handle via
/// <see cref="SwiftMarshal.MarshalFromSwiftObject{T}"/> — no new public runtime API.
/// </para>
///
/// <para>
/// Shape-driven, not AppIntents-hardcoded. The recognizer matches the structural
/// shape — a dependency generic <i>class</i> whose constructor has a method-own generic
/// <c>G</c> bound to a protocol <c>P</c>, taking a <c>KeyPath&lt;G, V&gt;</c> /
/// <c>WritableKeyPath&lt;G, V&gt;</c> whose value <c>V</c> is the class's own sole generic
/// parameter — and pairs it with any local conformer of <c>P</c>. AppIntents is one
/// instance; nothing here is string-matched to it.
/// </para>
///
/// <para>
/// The factory keys the KeyPath as a <i>parameter</i> (not one factory per property),
/// so a single overload serves every property of the conformer that shares a value type
/// and KeyPath flavor. The concrete value types come from the conformer's own
/// <see cref="KeyPathSingletonEmitter">KeyPath singletons</see> — the only KeyPaths a C#
/// caller can pass, since a KeyPath cannot be originated at runtime from C#. This emitter
/// therefore composes with <see cref="AppEntityKeyPathSingletonEmitter"/>: the singletons
/// are the inputs, these factories are the sinks.
/// </para>
/// </summary>
internal static class ConformerKeyPathInitFactoryEmitter
{
    /// <summary>
    /// A recognized dependency KeyPath-init shape: one constructor of a dependency
    /// generic class whose method generic <see cref="ConstraintProtocol"/>-bound parameter
    /// roots a <c>KeyPath&lt;G, V&gt;</c> whose <c>V</c> is the class's sole generic param.
    /// </summary>
    private sealed record InitShape(
        ClassDecl DepClass,
        string DepClassCSharpBaseName,
        string DepClassSwiftQualified,
        string DepClassSwiftQualifiedForWrapper,
        SwiftTypeName ConstraintProtocol,
        bool KeyPathIsWritable,
        string KeyPathArgLabel,
        IReadOnlyList<ScalarParam> Scalars);

    /// <summary>A constructor scalar parameter the factory marshals verbatim. v1: String only.</summary>
    private sealed record ScalarParam(string Label);

    /// <summary>
    /// Module-scope entry point. Call at namespace scope after the module's type walk,
    /// alongside <see cref="AppEntityKeyPathSingletonEmitter.EmitForModule"/> (whose
    /// singletons these factories consume).
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
        // Factories are wrapper-trampoline-backed; only meaningful when emitting a real
        // wrapper dylib (mirrors the KeyPath singleton emitters' gate).
        if (!WrapperValidation.IsXCFrameworkMode(typeDatabase)) return;

        var depModules = typeDatabase.GetDependencyModuleDecls();
        if (depModules.Count == 0) return;

        // Recognize candidate init shapes across all dependency generic classes.
        var shapes = new List<InitShape>();
        foreach (var dep in depModules)
        {
            foreach (var depDecl in KeyPathBagWalker.BuildTypeDeclIndex(dep).Values)
                CollectInitShapes(depDecl, emissionContext, typeDatabase, logger, shapes);
        }
        logger.LogDebug("KeyPath-init factory: recognized {Count} init shapes across {Deps} dependency module(s).",
            shapes.Count, depModules.Count);
        if (shapes.Count == 0) return;

        // Resolve local conformers once (the index doubles as the "local to this module?" gate).
        var typeDeclByName = KeyPathBagWalker.BuildTypeDeclIndex(moduleDecl);

        foreach (var shape in shapes)
        {
            var conformers = engine.GetConformers(
                SwiftTypeName.FromModuleQualifiedName(shape.ConstraintProtocol.ModuleQualifiedName));
            foreach (var conformer in conformers)
            {
                if (conformer.SwiftType is null) continue;
                if (!typeDeclByName.TryGetValue(conformer.SwiftQualifiedName, out var conformerDecl)) continue;
                if (!AppEntityKeyPathSingletonEmitter.IsEligibleConformerType(conformerDecl)) continue;

                EmitFactoriesForConformer(csWriter, swiftWriter, shape, conformer, conformerDecl,
                    typeDatabase, emissionContext, logger);
            }
        }
    }

    /// <summary>
    /// The pure, TypeDatabase-free result of recognizing one rescuable KeyPath-init shape.
    /// Exposed (with <see cref="TryRecognizeInitShape"/>) so unit tests can pin the recognizer
    /// without standing up a full emission pipeline.
    /// </summary>
    internal readonly record struct RecognizedInit(
        SwiftTypeName ConstraintProtocol,
        bool KeyPathIsWritable,
        string KeyPathArgLabel,
        IReadOnlyList<string> ScalarLabels)
    {
        internal string ConstraintProtocolQualifiedName => ConstraintProtocol.ModuleQualifiedName;
    }

    /// <summary>
    /// Walk a dependency class's constructors for the rescuable KeyPath-init shape and
    /// append any matches to <paramref name="shapes"/>. Only generic reference types
    /// (<see cref="ClassDecl"/>) qualify — the construction path adopts a <c>+1</c> ARC
    /// reference, which is class-only.
    /// </summary>
    private static void CollectInitShapes(
        TypeDecl depDecl,
        ModuleEmissionContext emissionContext,
        ITypeDatabase typeDatabase,
        ILogger logger,
        List<InitShape> shapes)
    {
        if (depDecl is not ClassDecl classDecl) return;
        // v1: the KeyPath value must be the class's sole generic parameter, so the factory
        // closes exactly one type argument (`MiniEntityProperty<V>`).
        if (classDecl.GenericParameters.Count != 1) return;

        var depBaseName = KeyPathSingletonEmitter.ResolveCSharpFullName(classDecl, typeDatabase);
        if (depBaseName is null) return;
        var depSwiftQualified = classDecl.SwiftTypeName.ModuleQualifiedName;
        var depSwiftForWrapper = emissionContext.QualifyForWrapperSource(classDecl.SwiftTypeName);

        foreach (var ctor in classDecl.Methods)
        {
            if (!TryRecognizeInitShape(classDecl, ctor, out var recognized)) continue;

            logger.LogDebug(
                "KeyPath-init factory: recognized {Dep}.init<G: {P}>({Label}: {Flavor}) — eligible for consumer factories.",
                depSwiftQualified, recognized.ConstraintProtocolQualifiedName,
                recognized.KeyPathArgLabel, recognized.KeyPathIsWritable ? "WritableKeyPath" : "KeyPath");

            shapes.Add(new InitShape(
                classDecl, depBaseName, depSwiftQualified, depSwiftForWrapper,
                recognized.ConstraintProtocol, recognized.KeyPathIsWritable, recognized.KeyPathArgLabel,
                recognized.ScalarLabels.Select(label => new ScalarParam(label)).ToList()));
        }
    }

    /// <summary>
    /// The shape recognizer: does this constructor of <paramref name="classDecl"/> match
    /// <c>init&lt;G: P&gt;(scalars…, label: KeyPath&lt;G, V&gt;)</c> where <c>V</c> is the
    /// class's sole generic parameter? Pure over the model — no TypeDatabase, no I/O — so it
    /// is the unit-test seam for the recognition logic.
    /// </summary>
    internal static bool TryRecognizeInitShape(ClassDecl classDecl, MethodDecl ctor, out RecognizedInit result)
    {
        result = default;

        // v1: exactly one class generic (closed to `{DepClass}<V>`).
        if (classDecl.GenericParameters.Count != 1) return false;
        if (!ctor.IsConstructor) return false;
        var classGeneric = classDecl.GenericParameters[0];

        // `ctor.GenericParameters` carries the enclosing class's generic(s) (depth-0) followed by
        // the constructor's own (depth-1). Isolate the method-own generics by excluding the class
        // generics. The rescuable shape has exactly one (`init<Entity>`): a non-generic init can't
        // carry the `where Entity : P` constraint, and a second method generic (`init<Entity, Other>`)
        // would be uninferable from the rescued KeyPath + scalar arguments, so the generated Swift
        // call wouldn't compile and its `@_cdecl` would be silently stripped.
        var classGenericNames = new HashSet<string>(classDecl.GenericParameters.Select(g => g.TypeName));
        var methodOwnGenerics = ctor.GenericParameters.Where(g => !classGenericNames.Contains(g.TypeName)).ToList();
        if (methodOwnGenerics.Count != 1) return false;

        string? keyPathLabel = null;
        bool keyPathIsWritable = false;
        SwiftTypeName? constraintProtocol = null;
        var scalarLabels = new List<string>();

        // CSSignature[0] is the return type; real parameters start at [1].
        for (int i = 1; i < ctor.CSSignature.Count; i++)
        {
            var arg = ctor.CSSignature[i];
            if (arg.SwiftTypeSpec is NamedTypeSpec nts
                && TypeProjectionFactory.IsKeyPathFamily(nts.Name)
                && nts.GenericParameters.Count == 2)
            {
                // Only one rescuable KeyPath parameter per init (v1).
                if (keyPathLabel is not null) return false;

                // ReferenceWritableKeyPath requires a reference-type (class) root. The AppEntity
                // conformers this factory targets are value types whose emitted singletons are
                // only KeyPath / WritableKeyPath; a RWKP init parameter can't bind those, and the
                // emission path collapses every writable shape to WritableKeyPath, so recognizing
                // RWKP would produce a trampoline the wrapper build strips. Don't recognize it.
                if (nts.Name == "Swift.ReferenceWritableKeyPath") return false;

                var root = nts.GenericParameters[0] as NamedTypeSpec;
                var value = nts.GenericParameters[1] as NamedTypeSpec;
                if (root is null || value is null) return false;

                // Root must be the constructor's own method generic (not the class generic),
                // constrained by exactly one protocol and nothing else. A richer constraint set
                // (`Entity : P & Q`, a concrete/superclass bound, or an associated-type requirement)
                // can't be satisfied by enumerating conformers of a single protocol — those
                // conformers might not satisfy the others, producing trampolines that fail to
                // type-check and get stripped.
                var methodGeneric = methodOwnGenerics[0];
                if (methodGeneric.TypeName != root.Name && methodGeneric.SugaredTypeName != root.Name)
                    return false;
                if (methodGeneric.GenericConformances.Count != 1) return false;
                if (methodGeneric.AssosiatedTypeConformances.Count != 0) return false;

                var protocolConformance = methodGeneric.GenericConformances[0];
                if (protocolConformance.Kind != ConformanceKind.Protocol) return false;
                constraintProtocol = protocolConformance.ConformanceTarget;

                // Value must be the class's sole generic parameter, so the factory closes
                // `{DepClass}<V>` to the conformer's property value type.
                if (value.Name != classGeneric.TypeName && value.Name != classGeneric.SugaredTypeName)
                    return false;

                keyPathLabel = arg.Name;
                // RWKP is rejected above, so a writable shape here is exactly WritableKeyPath.
                keyPathIsWritable = nts.Name == "Swift.WritableKeyPath";
            }
            else if (arg.SwiftTypeSpec.ToString() == "Swift.String")
            {
                scalarLabels.Add(arg.Name);
            }
            else
            {
                // Any other parameter type (e.g. LocalizedStringResource, IntentParameter)
                // isn't marshallable by this v1 factory — skip the whole init shape.
                return false;
            }
        }

        if (keyPathLabel is null || constraintProtocol is null) return false;

        result = new RecognizedInit(constraintProtocol, keyPathIsWritable, keyPathLabel, scalarLabels);
        return true;
    }

    /// <summary>
    /// Emit the factory overloads + Swift trampolines for one (shape, conformer) pairing.
    /// One overload is emitted per distinct value type among the conformer's KeyPath-able
    /// properties (writable-only when the init takes a <c>WritableKeyPath</c>).
    /// </summary>
    private static void EmitFactoriesForConformer(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        InitShape shape,
        ConcreteSpecializationEngine.ConcreteConformer conformer,
        TypeDecl conformerDecl,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        ILogger logger)
    {
        var moduleName = conformerDecl.SwiftTypeName.Module;
        var conformerCSharpFull = KeyPathSingletonEmitter.ResolveCSharpFullName(conformerDecl, typeDatabase);
        if (conformerCSharpFull is null) return;
        var conformerSwiftForWrapper = emissionContext.QualifyForWrapperSource(conformerDecl.SwiftTypeName);

        // The trampoline names both the dependency class (`MiniEntityProperty`) and the
        // concrete conformer (`MockBook`); when either is gated (e.g. AppIntents types are
        // iOS 16+), an unannotated `@_cdecl` fails to type-check against the device SDK and
        // is silently stripped from the wrapper — leaving the C# P/Invoke with no symbol at
        // runtime. Merge both floors so the Swift `@available` and the C# `[SupportedOSPlatform]`
        // agree, mirroring AppEntityKeyPathSingletonEmitter.
        var mergedAvailability = MergeFactoryAvailability(shape.DepClass, conformer);

        // Distinct value types among KeyPath-able properties of the conformer. The set of
        // values a caller can actually pass equals the conformer's KeyPath singletons; we
        // derive it from the same emittable-property gate the singleton emitter uses, so
        // the two stay aligned without coupling to the singleton emitter's internal state.
        var projector = new TypeProjectionFactory();
        var seenValueTypes = new HashSet<string>(StringComparer.Ordinal);
        var values = new List<(string CSValueType, string CSMarshalValueType, string SwiftValueTypeForWrapper,
            IReadOnlyList<AvailabilityAnnotation>? Availability)>();
        foreach (var prop in conformerDecl.Properties)
        {
            if (!KeyPathBagWalker.IsEmittableProperty(prop, allowAbstract: false, allowComputed: true)) continue;
            // A WritableKeyPath init needs a settable path; get-only properties have none.
            bool isWritableProp = prop.Accessors.OfType<SetAccessorDecl>().Any();
            if (shape.KeyPathIsWritable && !isWritableProp) continue;

            var projection = projector.Project(prop.SwiftTypeSpec, new ProjectionContext
            {
                TypeDatabase = typeDatabase,
                IsParameter = false,
                CurrentModuleName = moduleName,
            });
            if (projection is null) continue;

            if (!seenValueTypes.Add(projection.PublicType)) continue;
            var swiftValueTypeForWrapper = emissionContext.QualifyForWrapperSource(prop.SwiftTypeSpec.ToString());
            // The KeyPath parameter is typed in idiomatic C# (`string`/`nint`) so it binds the
            // emitted singleton, but the dependency's `MiniEntityProperty<Value>` generic argument
            // must use the *marshal* type — `SwiftString` (an ISwiftObject), not `string` — so the
            // dep binding's `TypeMetadata.GetTypeMetadataOrThrow<Value>()` resolves at runtime. For
            // blittable values the two coincide (`nint`); for classes both are the qualified name.
            //
            // The trampoline closes `MiniEntityProperty<Value>` over this Value type and names it
            // in the body, so a Value gated to a later OS than the dep class / conformer must lift
            // the per-method floor or the `@_cdecl` is stripped at wrapper-build, orphaning the
            // C# P/Invoke. Availability is therefore per-Value, not one floor for the container.
            var valueAvailability = MergeWithValueType(mergedAvailability, prop.SwiftTypeSpec, typeDatabase);
            values.Add((projection.PublicType, projection.MarshalFromSwiftType, swiftValueTypeForWrapper, valueAvailability));
        }
        if (values.Count == 0) return;

        var conformerForName = KeyPathSingletonEmitter.StripModulePrefix(conformer.CSharpType, moduleName);
        var conformerIdent = KeyPathSingletonEmitter.SanitizeIdentifier(conformerForName);
        var depIdent = KeyPathSingletonEmitter.SanitizeIdentifier(shape.DepClass.Name);
        var containerCsName = $"{conformerIdent}{depIdent}Factory";
        var keyPathFlavor = shape.KeyPathIsWritable ? "WritableKeyPath" : "KeyPath";
        var methodName = $"CreateFrom{Capitalize(shape.KeyPathArgLabel)}";
        var depGlobalBase = EnsureGlobalPrefix(shape.DepClassCSharpBaseName);

        // Collect the emittable (V, symbol) set first so we can skip an empty container.
        var emit = new List<(string CSValueType, string CSMarshalValueType, string SwiftValueTypeForWrapper,
            IReadOnlyList<AvailabilityAnnotation>? Availability, string Symbol)>();
        foreach (var (csValueType, csMarshalValueType, swiftValueTypeForWrapper, valueAvailability) in values)
        {
            var dedupKey =
                $"{conformer.SwiftQualifiedName}|{shape.DepClassSwiftQualified}|{shape.KeyPathArgLabel}|{csValueType}";
            if (!emissionContext.TryAddKeyPathInitFactory(dedupKey)) continue;

            var hashInput =
                $"{moduleName}|{conformer.SwiftQualifiedName}|{shape.DepClassSwiftQualified}|{shape.KeyPathArgLabel}|{swiftValueTypeForWrapper}";
            var hash = EmitterUtility.DeterministicHash8(hashInput);
            var conformerSan = KeyPathSingletonEmitter.SanitizeSymbol(conformer.SwiftQualifiedName);
            var depSan = KeyPathSingletonEmitter.SanitizeSymbol(shape.DepClassSwiftQualified);
            var labelSan = KeyPathSingletonEmitter.SanitizeSymbol(shape.KeyPathArgLabel);
            // SBW_EPF_ (EntityProperty factory) is dedicated to this emitter — disjoint from
            // SBW_KP_ / SBW_KP_AppEntity_ (KeyPath singletons), SBW_ (methods), SBW_CSM_.
            var symbol = $"SBW_EPF_{moduleName}_{conformerSan}_{depSan}_{labelSan}_{hash}";
            emit.Add((csValueType, csMarshalValueType, swiftValueTypeForWrapper, valueAvailability, symbol));
        }
        if (emit.Count == 0) return;

        csWriter.WriteLine();
        csWriter.WriteLine($"// KeyPath-init factories: {shape.DepClassSwiftQualified} for {conformer.SwiftQualifiedName} (via {shape.KeyPathArgLabel}:)");
        csWriter.WriteLine($"public static unsafe partial class {containerCsName}");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        foreach (var (csValueType, csMarshalValueType, _, availability, symbol) in emit)
        {
            var pinvokeName = $"PInvoke_{symbol}";
            // Return generic arg uses the marshal type (`SwiftString`, not `string`); the KeyPath
            // parameter uses the idiomatic public type so it binds the emitted singleton.
            var returnType = $"{depGlobalBase}<{csMarshalValueType}>";
            var keyPathParamType = $"global::Swift.{keyPathFlavor}<{conformerCSharpFull}, {csValueType}>";

            // P/Invoke: scalars as (w0, w1) word pairs, the KeyPath as a pinned IntPtr,
            // returning the +1 ARC object pointer.
            var pinvokeParams = new List<string>();
            foreach (var scalar in shape.Scalars)
            {
                pinvokeParams.Add($"nint {scalar.Label}_w0");
                pinvokeParams.Add($"nint {scalar.Label}_w1");
            }
            pinvokeParams.Add($"IntPtr {shape.KeyPathArgLabel}Buffer");

            csWriter.WriteLine();
            // parentAnnotations: null — the container sits at namespace scope (not nested in
            // the conformer), so there is no parent floor to dedup against.
            AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(
                csWriter, availability, parentAnnotations: null);
            csWriter.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
            csWriter.WriteLine($"[global::System.Runtime.InteropServices.LibraryImport(\"SwiftBindings\", EntryPoint = \"{symbol}\")]");
            csWriter.WriteLine($"private static partial IntPtr {pinvokeName}({PInvokeEmitHelper.DeduplicateCSharpParamNames(string.Join(", ", pinvokeParams))});");
            csWriter.WriteLine();

            // Public factory.
            var factoryParams = new List<string>();
            foreach (var scalar in shape.Scalars)
                factoryParams.Add($"string {scalar.Label}");
            factoryParams.Add($"{keyPathParamType} {shape.KeyPathArgLabel}");

            AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(
                csWriter, availability, parentAnnotations: null);
            csWriter.WriteLine($"public static {returnType} {methodName}({string.Join(", ", factoryParams)})");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // The KeyPath-init trampoline this P/Invokes is availability-gated (see EmitSwiftTrampolines).
            // On an OS below the merged dep-class+conformer floor its body dereferences a weak-linked,
            // null gated symbol (uncatchable SIGSEGV). Throw a catchable exception before marshalling.
            AvailabilityAttributeEmitter.EmitRuntimeAvailabilityGuard(csWriter, availability, methodName);

            var callArgs = new List<string>();
            foreach (var scalar in shape.Scalars)
            {
                csWriter.WriteLine($"using var {scalar.Label}Swift = new SwiftString({scalar.Label});");
                csWriter.WriteLine($"using var {scalar.Label}Disposable = {scalar.Label}Swift.PayloadBuffer;");
                csWriter.WriteLine($"var {scalar.Label}Buf = {scalar.Label}Disposable.Buffer;");
                csWriter.WriteLine($"nint {scalar.Label}_w0 = Unsafe.As<SwiftString.Buffer, nint>(ref {scalar.Label}Buf);");
                csWriter.WriteLine($"nint {scalar.Label}_w1 = Unsafe.Add(ref Unsafe.As<SwiftString.Buffer, nint>(ref {scalar.Label}Buf), 1);");
                callArgs.Add($"{scalar.Label}_w0");
                callArgs.Add($"{scalar.Label}_w1");
            }
            csWriter.WriteLine($"using SafeHandlePin {shape.KeyPathArgLabel}Pin = new SafeHandlePin({shape.KeyPathArgLabel}.Payload);");
            csWriter.WriteLine($"IntPtr {shape.KeyPathArgLabel}Buffer = {shape.KeyPathArgLabel}Pin.Handle;");
            callArgs.Add($"{shape.KeyPathArgLabel}Buffer");

            csWriter.WriteLine($"IntPtr resultPtr = {pinvokeName}({string.Join(", ", callArgs)});");
            csWriter.WriteLine($"return SwiftMarshal.MarshalFromSwiftObject<{returnType}>(resultPtr);");

            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();

        EmitSwiftTrampolines(swiftWriter, shape, conformerSwiftForWrapper, keyPathFlavor, emit);
    }

    private static void EmitSwiftTrampolines(
        SwiftWriter swiftWriter,
        InitShape shape,
        string conformerSwiftForWrapper,
        string keyPathFlavor,
        IReadOnlyList<(string CSValueType, string CSMarshalValueType, string SwiftValueTypeForWrapper,
            IReadOnlyList<AvailabilityAnnotation>? Availability, string Symbol)> emit)
    {
        foreach (var (_, _, swiftValueTypeForWrapper, availability, symbol) in emit)
        {
            // Trampoline params: scalars as (w0, w1) Int pairs, then the KeyPath pointer.
            var trampolineParams = new List<string>();
            foreach (var scalar in shape.Scalars)
            {
                trampolineParams.Add($"_ _sW0_{scalar.Label}: Int");
                trampolineParams.Add($"_ _sW1_{scalar.Label}: Int");
            }
            trampolineParams.Add($"_ {shape.KeyPathArgLabel}: UnsafeMutableRawPointer");

            swiftWriter.WriteLine();
            swiftWriter.WriteLine($"// KeyPath-init factory trampoline: {shape.DepClassSwiftQualified}({shape.KeyPathArgLabel}: \\{conformerSwiftForWrapper}.*) as {keyPathFlavor}<{conformerSwiftForWrapper}, {swiftValueTypeForWrapper}>");
            WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);
            swiftWriter.WriteLine($"@_cdecl(\"{symbol}\")");
            swiftWriter.WriteLine($"public func {symbol}({string.Join(", ", trampolineParams)}) -> UnsafeMutableRawPointer {{");
            swiftWriter.Indent++;

            var ctorArgs = new List<string>();
            foreach (var scalar in shape.Scalars)
            {
                swiftWriter.WriteLine($"let {scalar.Label}Val = unsafeBitCast((_sW0_{scalar.Label}, _sW1_{scalar.Label}), to: String.self)");
                ctorArgs.Add($"{scalar.Label}: {scalar.Label}Val");
            }
            swiftWriter.WriteLine($"let {shape.KeyPathArgLabel}Val = Unmanaged<{keyPathFlavor}<{conformerSwiftForWrapper}, {swiftValueTypeForWrapper}>>.fromOpaque({shape.KeyPathArgLabel}).takeUnretainedValue()");
            ctorArgs.Add($"{shape.KeyPathArgLabel}: {shape.KeyPathArgLabel}Val");

            swiftWriter.WriteLine($"let obj = {shape.DepClassSwiftQualifiedForWrapper}<{swiftValueTypeForWrapper}>({string.Join(", ", ctorArgs)})");
            swiftWriter.WriteLine("return Unmanaged.passRetained(obj).toOpaque()");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
        }
    }

    /// <summary>
    /// Combine the availability floors of the dependency class and the concrete conformer —
    /// both are named in the trampoline body, so the <c>@_cdecl</c> must be guarded by the
    /// stricter of the two on every platform. <see cref="WrapperEmitterHelpers.EmitSwiftAvailability"/>
    /// and <see cref="AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations"/>
    /// dedup to one entry per platform (max version), so simply concatenating is correct.
    /// </summary>
    private static IReadOnlyList<AvailabilityAnnotation>? MergeFactoryAvailability(
        ClassDecl depClass,
        ConcreteSpecializationEngine.ConcreteConformer conformer)
    {
        var depMerged = WrapperEmitterHelpers.MergeAvailability(depClass.AvailabilityAnnotations, depClass.ParentDecl);
        var combined = depMerged is null
            ? new List<AvailabilityAnnotation>()
            : new List<AvailabilityAnnotation>(depMerged);
        if (conformer.AvailabilityAnnotations is { Count: > 0 } conformerAvailability)
            combined.AddRange(conformerAvailability);
        return combined.Count > 0 ? combined : null;
    }

    /// <summary>
    /// Layer the per-Value floor (the Value type named in this trampoline's closed
    /// <c>MiniEntityProperty&lt;Value&gt;</c>) onto the container's dep+conformer base. Concatenation
    /// is correct because the downstream emitters take the max version per platform.
    /// </summary>
    private static IReadOnlyList<AvailabilityAnnotation>? MergeWithValueType(
        IReadOnlyList<AvailabilityAnnotation>? baseAvailability,
        TypeSpec valueSpec,
        ITypeDatabase typeDatabase)
    {
        if (KeyPathBagWalker.CollectValueTypeAvailability(valueSpec, typeDatabase) is not { Count: > 0 } valueAvailability)
            return baseAvailability;
        var combined = baseAvailability is null
            ? new List<AvailabilityAnnotation>()
            : new List<AvailabilityAnnotation>(baseAvailability);
        combined.AddRange(valueAvailability);
        return combined;
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    private static string EnsureGlobalPrefix(string cSharpFullName) =>
        cSharpFullName.StartsWith("global::", StringComparison.Ordinal) ? cSharpFullName : $"global::{cSharpFullName}";
}
