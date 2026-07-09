// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Sibling emitter to <see cref="ConcreteProtocolSpecializationEmitter"/>
/// that closes the open-V keypath-sort gap CSM can't handle directly.
///
/// <para>
/// CSM can specialize parent generics (e.g. <c>GenericRequest&lt;Album&gt;</c>) but it
/// cannot remove a method-own generic V from a method signature — the resulting C#
/// surface would still carry <c>void Sort&lt;V&gt;(KeyPath&lt;Item.SortBag, V&gt;, bool)</c>,
/// and Swift's <c>@_cdecl</c> wrapper for that method cannot type-erase the KeyPath's
/// Value slot in a way that round-trips back to a typed call. Route C side-steps the
/// limitation by enumerating the conformer's bag once, projecting each property's
/// Value type to a public C# type, and emitting one closed <c>Sort</c> overload per
/// distinct C# overload key. When multiple Swift V types collapse to the same C#
/// overload (e.g. <c>String</c> + <c>Optional&lt;String&gt;</c> → <c>string</c>
/// post-NRT-erasure), the Swift trampoline iterates an <c>as?</c> chain across the
/// member variants to pick the matching <c>KeyPath&lt;ConcreteBag, ConcreteV&gt;</c>
/// at runtime, then falls through to <c>fatalError</c> if none matched.
/// </para>
///
/// <para>
/// Eligibility is governed exclusively by <see cref="RouteCSortShapeEligibility"/>,
/// which is the single-source predicate consulted by three call sites (this emitter,
/// the CSM open-V suppression path, and the CSM eligibility check). The bag walk —
/// "which conformer-bag properties project to real C# types?" — is delegated to
/// <see cref="KeyPathBagWalker"/>, shared with the singleton emitter so the
/// two consumers agree on which leaves admit and which don't.
/// </para>
///
/// <para>
/// Scope. Class generic parents use <c>unsafeBitCast(OpaquePointer(self_), to:)</c>
/// to recover the instance pointer. Struct generic parents (including non-frozen
/// structs marshalled as <c>ClassWithOpaquePayload</c>) bind through
/// <c>self_.assumingMemoryBound(to:).pointee</c>; the mutating case uses
/// <c>var __self</c> + a write-back of <c>pointee = __self</c> after the call so
/// the in-place mutation propagates to the C# SafeHandle's payload memory.
/// Non-KeyPath parameters are limited to
/// <see cref="MethodClosureBridge.ParamAbiCategory.Primitive"/> (Bool, integer
/// numerics, float/double). Anything outside that shape causes the method to be
/// skipped with a debug log.
/// </para>
/// </summary>
internal static class KeyPathBagValueSpecializationEmitter
{
    /// <summary>
    /// Entry point. Same call-site contract as
    /// <see cref="KeyPathSingletonEmitter.EmitKeyPathSingletonsForGenericParent"/> and
    /// <see cref="ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializationsForGenericParent"/>
    /// — invoke after the parent's class body is closed so the emitted extension
    /// classes sit at namespace scope.
    /// </summary>
    public static void EmitRouteCSpecializationsForGenericParent(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        TypeDecl typeDecl,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        ConcreteSpecializationEngine engine,
        ILogger logger)
    {
        if (!typeDecl.IsGeneric) return;
        // Same nested-generic-parent exclusion CSM honours: closed-
        // receiver naming can't reference a nested generic parent without naming its
        // outer's generic args, which Route C has no recipe for.
        if (typeDecl.ParentDecl is TypeDecl) return;
        if (!WrapperValidation.IsXCFrameworkMode(typeDatabase)) return;
        if (typeDecl.ModuleDecl is null) return;

        // Receiver shape: class generic parents use unsafeBitCast(OpaquePointer(...))
        // to recover the instance. Struct generic parents (frozen or non-frozen marshalled
        // as ClassWithOpaquePayload) bind through self_.assumingMemoryBound; the per-overload
        // Swift template branches on (isClass, IsMutating) and adds the pointee write-back
        // for the mutating-struct case. Enums and protocols are not supported as Route C
        // generic parents.
        bool isClass = typeDecl is ClassDecl;
        bool isStruct = typeDecl is StructDecl;
        if (!isClass && !isStruct)
        {
            logger.LogDebug(
                "RouteC: skipping {Type} — only class and struct generic-parent receivers are supported.",
                typeDecl.SwiftTypeName?.ModuleQualifiedName);
            return;
        }
        // Frozen struct receivers project to C# as blittable structs that don't
        // implement ISwiftObject; the C# extension's `((ISwiftObject)self).SwiftHandle`
        // call would throw NotSupportedException at runtime. Only non-frozen structs
        // (ClassWithOpaquePayload) and classes carry an ISwiftObject-backed SwiftHandle.
        if (typeDecl is StructDecl frozenCheck && frozenCheck.IsFrozen)
        {
            logger.LogDebug(
                "RouteC: skipping {Type} — @frozen struct generic parents have no ISwiftObject SwiftHandle.",
                typeDecl.SwiftTypeName?.ModuleQualifiedName);
            return;
        }

        var eligible = new List<(MethodDecl Method, RouteCSortShapeEligibility.RouteCSortShape Shape)>();
        foreach (var m in typeDecl.Methods)
        {
            if (RouteCSortShapeEligibility.IsRouteCSortShapeEligible(m, typeDecl, out var shape) && shape is not null)
                eligible.Add((m, shape));
        }
        if (eligible.Count == 0) return;

        var typeDeclByName = KeyPathBagWalker.BuildTypeDeclIndex(typeDecl.ModuleDecl);
        var moduleName = typeDecl.SwiftTypeName!.Module;
        var wrapperLibPath = typeDatabase.AsyncLibraryName ?? "libSwiftBindings";

        // Group eligible methods by conformer so we open one *RouteCExtensions class
        // per (parent, conformer) and hold every method's per-V overloads inside it.
        var byConformer = new Dictionary<string, (
            ConcreteSpecializationEngine.ConcreteConformer Conformer,
            TypeDecl ConformerDecl,
            List<(MethodDecl, RouteCSortShapeEligibility.RouteCSortShape)> Methods)>(StringComparer.Ordinal);

        foreach (var (method, shape) in eligible)
        {
            var conformers = engine.GetConformers(shape.ProtocolName);
            foreach (var conformer in conformers)
            {
                if (conformer.SwiftType is null) continue;
                var conformerKey = conformer.SwiftQualifiedName;
                if (!typeDeclByName.TryGetValue(conformerKey, out var conformerDecl)) continue;
                if (!byConformer.TryGetValue(conformerKey, out var bucket))
                {
                    bucket = (conformer, conformerDecl, new List<(MethodDecl, RouteCSortShapeEligibility.RouteCSortShape)>());
                    byConformer.Add(conformerKey, bucket);
                }
                bucket.Methods.Add((method, shape));
            }
        }

        foreach (var (_, bucket) in byConformer)
        {
            EmitOneConformerExtensionClass(
                csWriter, swiftWriter, typeDecl, bucket.Conformer, bucket.ConformerDecl,
                bucket.Methods, typeDeclByName, moduleName, wrapperLibPath,
                typeDatabase, emissionContext, logger);
        }
    }

    private static void EmitOneConformerExtensionClass(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        TypeDecl parentTypeDecl,
        ConcreteSpecializationEngine.ConcreteConformer conformer,
        TypeDecl conformerDecl,
        IReadOnlyList<(MethodDecl Method, RouteCSortShapeEligibility.RouteCSortShape Shape)> methods,
        IReadOnlyDictionary<string, TypeDecl> typeDeclByName,
        string moduleName,
        string wrapperLibPath,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        ILogger logger)
    {
        var parentCsName = $"global::{moduleName}.{parentTypeDecl.Name}";
        var conformerCsName = ResolveConformerCSharpFullName(conformer, typeDatabase);
        var receiverCsType = $"{parentCsName}<{conformerCsName}>";
        var parentSwiftQualified = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;
        var conformerSwiftQualified = conformer.SwiftLiteral ?? conformer.SwiftQualifiedName;
        var parentSwiftClosed = $"{parentSwiftQualified}<{conformerSwiftQualified}>";

        var conformerForName = StripModulePrefix(conformer.CSharpType, moduleName);
        var extClassName = $"{SanitizeIdentifier(parentTypeDecl.Name)}{SanitizeIdentifier(conformerForName)}RouteCExtensions";

        // Stage emissions to a list so we don't open an empty wrapper class if every
        // (method × V) shape gets skipped (unprojectable bag, non-Primitive params, …).
        var staged = new List<StagedOverload>();
        var methodsThatEmittedAnything = new HashSet<MethodDecl>();

        foreach (var (method, shape) in methods)
        {
            var walk = KeyPathBagWalker.TryResolveProjectableBagProps(
                conformer, conformerDecl, shape.AssocBagName, parentTypeDecl,
                typeDatabase, typeDeclByName, logger);
            if (walk is null)
            {
                logger.LogDebug(
                    "RouteC: skipping {Parent}.{Method} for conformer {Conformer} — bag {Assoc} not projectable.",
                    parentTypeDecl.Name, method.Name, conformer.SwiftQualifiedName, shape.AssocBagName);
                continue;
            }

            // Group ALL projectable bag properties by their C#-effective overload key.
            // C# erases NRT annotations on reference types — `string` and `string?`
            // collide as duplicate overloads (CS0111). Collapse those siblings into
            // ONE C# overload whose Swift trampoline does an `as?` chain over the
            // member Swift V variants. Value-type Optionals (`Nullable<int>`) stay
            // distinct because their `?` is genuine type structure.
            //
            // Iterate ProjectableProps directly (not DistinctProjectedValueTypes) so
            // two properties whose C# overload keys collide BUT whose Swift V types
            // differ (e.g. one carries `String`, another carries `Optional<String>`)
            // both contribute their Swift V to the trampoline's `as?` chain. A prior
            // pre-dedup by C# PublicType dropped one Swift V silently, causing the
            // surviving overload to fall through to `fatalError` on legitimate calls.
            var groups = new Dictionary<string, List<KeyPathBagWalker.ProjectedBagProperty>>(StringComparer.Ordinal);
            var groupOrder = new List<string>();
            foreach (var v in walk.Value.ProjectableProps)
            {
                var key = NormalizeCsOverloadKey(v.Projection);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<KeyPathBagWalker.ProjectedBagProperty>();
                    groups.Add(key, list);
                    groupOrder.Add(key);
                }
                list.Add(v);
            }

            foreach (var key in groupOrder)
            {
                var variants = groups[key];
                var prepared = TryPrepareOverload(
                    method, shape, parentTypeDecl, conformer, walk.Value.BagDecl!,
                    variants, key, receiverCsType, parentSwiftClosed, moduleName,
                    typeDatabase, logger);
                if (prepared is null) continue;

                // Dedup key + cdecl hash both include the prepared param signature
                // so two Route-C-eligible overloads sharing the same basename and same
                // C# V key but different non-keypath param shapes (e.g. extra Int vs
                // extra Bool flag) don't collide on either the C# overload-suppression
                // dedup or the Swift cdecl symbol.
                var paramSig = BuildParamSignature(prepared.OtherParams);
                var dedupKey = $"{parentSwiftQualified}|{conformer.SwiftQualifiedName}|{method.Name}|{key}|{paramSig}";
                if (!emissionContext.TryAddKeyPathBagValueSpecialization(dedupKey)) continue;

                staged.Add(prepared);
                methodsThatEmittedAnything.Add(method);
            }
        }

        if (staged.Count == 0) return;

        // C# extension class — namespace-scope, sibling to the *CsmExtensions classes.
        csWriter.WriteLine();
        csWriter.WriteLine($"// Route C per-Value Sort specializations for {conformer.SwiftQualifiedName}");
        csWriter.WriteLine($"// (consumer surface: {parentSwiftQualified})");
        csWriter.WriteLine($"public static unsafe partial class {extClassName}");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        foreach (var ov in staged)
            EmitCSharpOverload(csWriter, ov, wrapperLibPath);

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();

        // Swift trampolines — one per staged overload.
        foreach (var ov in staged)
            EmitSwiftTrampoline(swiftWriter, ov);

        // Suppress the parent-body emission of the original (still open-V) method so
        // the C# side surfaces only the Route-C closed-V overloads. MethodHandler
        // consults WasEmitted; setting it here closes the "D's lesson" three-way
        // contract for any method that produced at least one closed-V overload.
        foreach (var m in methodsThatEmittedAnything)
            m.MarkEmitted();
    }

    /// <summary>
    /// Pre-flight a single C# Sort overload: classify the non-keypath params,
    /// build symbol names, derive merged availability. <paramref name="variants"/>
    /// carries every Swift V type that collapses to the same C# overload key
    /// (e.g. <c>Swift.String</c> + <c>Swift.Optional&lt;Swift.String&gt;</c> both
    /// project to C# <c>string</c>); the Swift trampoline does an <c>as?</c> chain
    /// over the member variants. Returns null on any out-of-scope shape so the
    /// caller can skip silently.
    /// </summary>
    private static StagedOverload? TryPrepareOverload(
        MethodDecl method,
        RouteCSortShapeEligibility.RouteCSortShape shape,
        TypeDecl parentTypeDecl,
        ConcreteSpecializationEngine.ConcreteConformer conformer,
        TypeDecl bagDecl,
        IReadOnlyList<KeyPathBagWalker.ProjectedBagProperty> variants,
        string normalizedCsValueType,
        string receiverCsType,
        string parentSwiftClosed,
        string moduleName,
        ITypeDatabase typeDatabase,
        ILogger logger)
    {
        // Classify each non-keypath argument once up front. Only Primitive (Bool /
        // integer / float) is supported. Anything richer (string, payload, struct,
        // closure, …) bails out.
        var otherParams = new List<PreparedParam>();
        for (int i = 1; i < method.CSSignature.Count; i++)
        {
            int paramIdx = i - 1;
            var arg = method.CSSignature[i];
            if (paramIdx == shape.KeyPathParameterIndex) continue;
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;

            var category = MethodClosureBridge.ClassifyParam(arg, typeDatabase);
            if (category != MethodClosureBridge.ParamAbiCategory.Primitive)
            {
                logger.LogDebug(
                    "RouteC: skipping {Method} for {Conformer}/{V} — param {ParamName} (kind={Kind}) is not Primitive.",
                    method.Name, conformer.SwiftQualifiedName, normalizedCsValueType,
                    arg.Name, category);
                return null;
            }

            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec);
            var csType = ResolvePrimitiveCsType(arg.SwiftTypeSpec);
            if (csType is null)
            {
                logger.LogDebug(
                    "RouteC: skipping {Method} — primitive param {Name} has no C# mapping ({Spec}).",
                    method.Name, arg.Name, arg.SwiftTypeSpec);
                return null;
            }
            otherParams.Add(new PreparedParam(arg.Name, swiftType, csType));
        }

        var returnTypeSpec = method.CSSignature.First().SwiftTypeSpec;
        bool isVoidReturn = returnTypeSpec.IsEmptyTuple;
        if (!isVoidReturn)
        {
            // Only void-returning sort methods are supported. A non-void return on a Route-C
            // shape would need either a primitive return ABI or indirect-result handling.
            logger.LogDebug(
                "RouteC: skipping {Method} — non-void return not yet supported.",
                method.Name);
            return null;
        }

        if (method.Throws)
        {
            logger.LogDebug(
                "RouteC: skipping {Method} — throwing methods not yet supported.",
                method.Name);
            return null;
        }

        var bagCsName = ResolveCSharpFullName(bagDecl, typeDatabase);
        if (bagCsName is null)
        {
            logger.LogDebug(
                "RouteC: skipping {Method} — bag {Bag} has no C# binding.",
                method.Name, bagDecl.SwiftTypeName?.ModuleQualifiedName);
            return null;
        }

        var bagSwiftQualified = bagDecl.SwiftTypeName!.ModuleQualifiedName;
        // Sort Swift variants deterministically so the cdecl symbol and emission
        // order don't depend on bag property iteration order.
        var swiftValueVariants = variants
            .Select(v => v.Property.SwiftTypeSpec.ToString())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        var publicMethodName = NameProvider.ToPascalCase(method.Name);
        var conformerSan = SanitizeSymbol(conformer.SwiftQualifiedName);
        // Symbol name uses the FIRST sorted Swift variant for readability + the
        // hash captures the full variant set AND the prepared param signature so
        // name collisions across distinct collapse groups or sibling overloads
        // sharing basename+V but differing in primitive params can't occur.
        var vSan = SanitizeSymbol(swiftValueVariants[0]);
        var paramSig = BuildParamSignature(otherParams);
        var hashInput = $"{moduleName}|{parentTypeDecl.Name}|{conformer.SwiftQualifiedName}|{method.Name}|{string.Join(",", swiftValueVariants)}|{paramSig}";
        var hash = EmitterUtility.DeterministicHash8(hashInput);
        var cdeclSymbol = $"SBW_RouteC_{moduleName}_{SanitizeSymbol(parentTypeDecl.Name)}_{conformerSan}_{publicMethodName}_{vSan}_{hash}";

        // Merge availability from every layer that contributes a deployment-target
        // floor: method, its parent type chain (via MergeAvailability), the bag
        // type, the chosen representative property, and the conformer extension.
        // Missing the bag or property floor would risk emitting a @_cdecl wrapper
        // that compiles under a newer SDK floor than the bag itself supports.
        var mergedAvailability = WrapperEmitterHelpers.MergeAvailability(
            method.AvailabilityAnnotations, method.ParentDecl);
        var availabilityExtras = new List<AvailabilityAnnotation>();
        if (bagDecl.AvailabilityAnnotations is { Count: > 0 } bagAvail)
            availabilityExtras.AddRange(bagAvail);
        var representativeProp = variants[0].Property;
        if (representativeProp.AvailabilityAnnotations is { Count: > 0 } propAvail)
            availabilityExtras.AddRange(propAvail);
        if (conformer.AvailabilityAnnotations is { Count: > 0 } conformerAvail)
            availabilityExtras.AddRange(conformerAvail);
        // The routing trampoline names every variant's Value type (one `as? KeyPath<Bag, V>`
        // branch each); a Value gated above the method/bag/conformer floor would leave the
        // `@_cdecl` under-annotated → stripped → orphaned P/Invoke, so lift the floor — the same
        // value-type merge the singleton + EntityProperty-factory emitters apply. Output-neutral
        // while the bag walk admits only stored properties (Swift forbids `@available` on stored
        // properties, so a stored Value can't out-live its container's floor); load-bearing once a
        // computed bag property is admitted.
        foreach (var variant in variants)
        {
            if (KeyPathBagWalker.CollectValueTypeAvailability(variant.Property.SwiftTypeSpec, typeDatabase)
                is { Count: > 0 } valueAvail)
            {
                availabilityExtras.AddRange(valueAvail);
            }
        }
        if (availabilityExtras.Count > 0)
        {
            var combined = mergedAvailability is null
                ? new List<AvailabilityAnnotation>()
                : new List<AvailabilityAnnotation>(mergedAvailability);
            combined.AddRange(availabilityExtras);
            mergedAvailability = combined;
        }

        return new StagedOverload(
            Method: method,
            Conformer: conformer,
            BagDecl: bagDecl,
            ReceiverCsType: receiverCsType,
            ParentSwiftClosed: parentSwiftClosed,
            BagCsFullName: bagCsName,
            BagSwiftQualified: bagSwiftQualified,
            CsValueType: normalizedCsValueType,
            SwiftValueVariants: swiftValueVariants,
            PublicMethodName: publicMethodName,
            CdeclSymbol: cdeclSymbol,
            OtherParams: otherParams,
            IsClassReceiver: parentTypeDecl is ClassDecl,
            MergedAvailability: mergedAvailability);
    }

    private static void EmitCSharpOverload(CSharpWriter csWriter, StagedOverload ov, string wrapperLibPath)
    {
        // ProtocolDecl bags MUST surface as the `I`-prefixed interface name (e.g.
        // `global::MusicKit.ILibraryAlbumSortProperties`). `BagCsFullName` carries
        // that form via ResolveCSharpFullName; `BagSwiftQualified` carries the Swift
        // module-qualified name used only inside the @_cdecl trampoline.
        var keyPathParamCs = $"global::Swift.KeyPath<{ov.BagCsFullName}, {ov.CsValueType}>";

        // P/Invoke declaration — Cdecl, KeyPath as IntPtr, primitives as their CLR types,
        // self_ last (matches Swift trampoline order). Bool params get [MarshalAs(U1)].
        AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(
            csWriter, ov.MergedAvailability, parentAnnotations: null);
        csWriter.WriteLine($"[System.Runtime.InteropServices.DllImport(\"{wrapperLibPath}\", EntryPoint = \"{ov.CdeclSymbol}\", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]");
        var pinvokeName = $"PInvoke_{ov.CdeclSymbol}";

        var pinvokeParams = new List<string> { "System.IntPtr _by" };
        foreach (var p in ov.OtherParams)
        {
            if (IsBoolCsType(p.CsType))
                pinvokeParams.Add($"[System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.U1)] {p.CsType} _{p.Name}");
            else
                pinvokeParams.Add($"{p.CsType} _{p.Name}");
        }
        pinvokeParams.Add("System.IntPtr self_");
        csWriter.WriteLine($"private static extern void {pinvokeName}({string.Join(", ", pinvokeParams)});");
        csWriter.WriteLine();

        // Extension method surfacing the typed KeyPath overload.
        AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(
            csWriter, ov.MergedAvailability, parentAnnotations: null);
        var publicParams = new List<string> { $"this {ov.ReceiverCsType} self", $"{keyPathParamCs} by" };
        foreach (var p in ov.OtherParams)
            publicParams.Add($"{p.CsType} {p.Name}");

        csWriter.WriteLine($"public static void {ov.PublicMethodName}({string.Join(", ", publicParams)})");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("if (by is null) throw new System.ArgumentNullException(nameof(by));");
        csWriter.WriteLine("if (self is null) throw new System.ArgumentNullException(nameof(self));");
        // The KeyPath bag-value @_cdecl wrapper is availability-gated; on an OS below the merged floor
        // its body dereferences a weak-linked, null gated symbol (uncatchable SIGSEGV). Throw a
        // catchable exception before the P/Invoke.
        AvailabilityAttributeEmitter.EmitRuntimeAvailabilityGuard(csWriter, ov.MergedAvailability, ov.PublicMethodName);
        var callArgs = new List<string> { "by.DangerousGetHandle()" };
        foreach (var p in ov.OtherParams)
            callArgs.Add(p.Name);
        callArgs.Add("((global::Swift.Runtime.ISwiftObject)self).SwiftHandle");
        csWriter.WriteLine($"{pinvokeName}({string.Join(", ", callArgs)});");
        // Keep both the KeyPath SafeHandle and the receiver alive across the call so
        // a finaliser racing the P/Invoke can't free the underlying Swift KP or class
        // instance mid-dispatch.
        csWriter.WriteLine("System.GC.KeepAlive(by);");
        csWriter.WriteLine("System.GC.KeepAlive(self);");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();
    }

    /// <summary>
    /// The raw (pre-escape) `_`-prefixed binding names this trampoline emits for its non-KeyPath
    /// params. Both the param-decl loop (<see cref="EmitSwiftTrampoline"/>) and the call loop
    /// (<see cref="EmitTrampolineCall"/>) feed this set to the reserved-collision escape so each
    /// per-param escape also dodges a sibling binding, and both loops stay in sync.
    /// </summary>
    private static IReadOnlySet<string> CollectTrampolineSiblingBindings(StagedOverload ov)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in ov.OtherParams)
            names.Add($"_{p.Name}");
        return names;
    }

    private static void EmitSwiftTrampoline(SwiftWriter swiftWriter, StagedOverload ov)
    {
        var variantList = string.Join(", ", ov.SwiftValueVariants);
        swiftWriter.WriteLine();
        swiftWriter.WriteLine($"// Route C per-V sort: {ov.ParentSwiftClosed}.{NameProvider.ParserNameToSwift(ov.Method)}<{variantList}>");
        WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, ov.MergedAvailability);
        swiftWriter.WriteLine($"@_cdecl(\"{ov.CdeclSymbol}\")");

        var swiftParams = new List<string> { "_ _by: UnsafeRawPointer" };
        // Sibling bindings (this emitter binds each param to `_{Name}`) so a reserved-name escape
        // (`_by`) also dodges a sibling user binding. EmitTrampolineCall recomputes the
        // identical set, keeping decl and call in sync.
        var siblings = CollectTrampolineSiblingBindings(ov);
        foreach (var p in ov.OtherParams)
        {
            // Escape the final `_`-prefixed binding when it collides with an injected synthetic
            // (`_by`, e.g. user param "by" → "_by") OR a sibling user binding; the external call label
            // is arg.Name, so the rename is source-local. The trampoline call escapes the same form
            // identically.
            var rawBinding = $"_{p.Name}";
            var binding = NameProvider.EscapeReservedSwiftWrapperLabel(
                rawBinding, CdeclParamMapper.ExcludeSelf(siblings, rawBinding));
            swiftParams.Add($"_ {binding}: {SwiftPrimitiveToCdeclType(p.SwiftType)}");
        }
        swiftParams.Add("_ self_: UnsafeMutableRawPointer");

        swiftWriter.WriteLine($"public func {ov.CdeclSymbol}(");
        swiftWriter.WriteLine($"    {string.Join(",\n    ", swiftParams)}");
        swiftWriter.WriteLine(") {");
        swiftWriter.Indent++;
        // Receiver binding mirrors CSM's three-way ConcreteProtocolSpecializationEmitter
        // (cs:791-805). Class: unsafeBitCast through OpaquePointer recovers the class
        // instance directly. Struct + mutating: bind a local `var __self` by reading
        // .pointee through the value witness table (storeBytes-free, ARC-safe for non-
        // BitwiseCopyable structs), then write back after the call so the mutation
        // reaches the C# SafeHandle's payload memory. Struct + non-mutating: same
        // read, but a `let` binding — no write-back.
        string selfWriteBack = string.Empty;
        if (ov.IsClassReceiver)
        {
            swiftWriter.WriteLine($"let __self = unsafeBitCast(OpaquePointer(self_), to: {ov.ParentSwiftClosed}.self)");
        }
        else if (ov.Method.IsMutating)
        {
            swiftWriter.WriteLine($"var __self = self_.assumingMemoryBound(to: {ov.ParentSwiftClosed}.self).pointee");
            selfWriteBack = $"self_.assumingMemoryBound(to: {ov.ParentSwiftClosed}.self).pointee = __self";
        }
        else
        {
            swiftWriter.WriteLine($"let __self = self_.assumingMemoryBound(to: {ov.ParentSwiftClosed}.self).pointee");
        }
        // KeyPath reconstruction: the C# side passed a heap KeyPath produced by
        // the singleton trampoline (or any other +1-retained KP source).
        // Read as AnyKeyPath (the family's common base) then attempt `as?` downcast
        // against each Swift V variant in the collapse group. C# can't differentiate
        // `KeyPath<Bag, String>` from `KeyPath<Bag, String?>` (NRT erasure), so the
        // single C# overload dispatches to whichever Swift V the heap KP carries.
        swiftWriter.WriteLine("let anyKp = Unmanaged<AnyKeyPath>.fromOpaque(_by).takeUnretainedValue()");

        foreach (var swiftV in ov.SwiftValueVariants)
        {
            swiftWriter.WriteLine($"if let typedKp = anyKp as? KeyPath<{ov.BagSwiftQualified}, {swiftV}> {{");
            swiftWriter.Indent++;
            EmitTrampolineCall(swiftWriter, ov, selfWriteBack);
            swiftWriter.WriteLine("return");
            swiftWriter.Indent--;
            swiftWriter.WriteLine("}");
        }
        // No variant matched — surface a fatal so the call-site bug (passing the
        // wrong KeyPath family/V) doesn't silently no-op.
        swiftWriter.WriteLine($"fatalError(\"[SwiftBindings] {ov.CdeclSymbol}: KeyPath value type not in expected variant set [{variantList}]\")");

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    /// <summary>
    /// Emit the in-branch call to the underlying Swift sort method. Assumes the
    /// caller has already bound <c>typedKp</c> (the concrete KeyPath&lt;Bag, V&gt;)
    /// and <c>__self</c> (the receiver). Walks <see cref="MethodDecl.CSSignature"/>
    /// in source order so Swift call-site labels appear in declaration order.
    /// </summary>
    private static void EmitTrampolineCall(SwiftWriter swiftWriter, StagedOverload ov, string selfWriteBack)
    {
        var callArgs = new List<string>();
        // Same sibling set as the param-decl loop (EmitSwiftTrampoline) so the call references the
        // (possibly) escaped binding.
        var siblings = CollectTrampolineSiblingBindings(ov);
        int otherIdx = 0;
        for (int sigIdx = 1; sigIdx < ov.Method.CSSignature.Count; sigIdx++)
        {
            var arg = ov.Method.CSSignature[sigIdx];
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
            var label = !string.IsNullOrEmpty(arg.Name) ? arg.Name + ": " : string.Empty;
            if (IsQualifyingKeyPathArg(arg))
            {
                callArgs.Add($"{label}typedKp");
            }
            else
            {
                var p = ov.OtherParams[otherIdx++];
                // Match the escaped `_`-prefixed binding from the param decl so the call references it.
                var rawBinding = $"_{p.Name}";
                var binding = NameProvider.EscapeReservedSwiftWrapperLabel(
                    rawBinding, CdeclParamMapper.ExcludeSelf(siblings, rawBinding));
                // Swift Bool crosses @_cdecl as Int8; restore Bool at the call site.
                if (SwiftPrimitiveIsBool(p.SwiftType))
                    callArgs.Add($"{label}{binding} != 0");
                else
                    callArgs.Add($"{label}{binding}");
            }
        }

        swiftWriter.WriteLine($"__self.{NameProvider.ParserNameToSwift(ov.Method)}({string.Join(", ", callArgs)})");
        if (!string.IsNullOrEmpty(selfWriteBack))
            swiftWriter.WriteLine(selfWriteBack);
    }

    /// <summary>
    /// True when an argument is the method's qualifying KeyPath slot. The Route C
    /// eligibility predicate already proved at most one such slot exists in the
    /// signature, so a bare KeyPath-family name match is sufficient — no need to
    /// thread the resolved index through every helper.
    /// </summary>
    private static bool IsQualifyingKeyPathArg(ArgumentDecl arg)
    {
        if (arg.SwiftTypeSpec is not NamedTypeSpec named) return false;
        return TypeProjectionFactory.IsKeyPathFamily(named.Name);
    }

    private static bool IsBoolCsType(string csType)
        => string.Equals(csType, "bool", StringComparison.Ordinal)
        || string.Equals(csType, "System.Boolean", StringComparison.Ordinal);

    private static bool SwiftPrimitiveIsBool(string swiftType)
        => string.Equals(swiftType, "Swift.Bool", StringComparison.Ordinal)
        || string.Equals(swiftType, "Bool", StringComparison.Ordinal);

    /// <summary>
    /// Map a Swift primitive @_cdecl param's Swift type spelling to its corresponding
    /// @_cdecl-compatible type. The only special case today is <c>Swift.Bool → Int8</c>;
    /// integers and floats use their direct Swift spelling.
    /// </summary>
    private static string SwiftPrimitiveToCdeclType(string swiftType)
    {
        if (SwiftPrimitiveIsBool(swiftType)) return "Int8";
        return swiftType;
    }

    /// <summary>
    /// Conservative primitive type resolution for the supported primitive set.
    /// Returning null causes the caller to skip the overload.
    /// </summary>
    private static string? ResolvePrimitiveCsType(TypeSpec spec)
    {
        if (spec is not NamedTypeSpec named) return null;
        return named.Name switch
        {
            "Swift.Bool" => "bool",
            "Swift.Int" => "nint",
            "Swift.UInt" => "nuint",
            "Swift.Int8" => "sbyte",
            "Swift.UInt8" => "byte",
            "Swift.Int16" => "short",
            "Swift.UInt16" => "ushort",
            "Swift.Int32" => "int",
            "Swift.UInt32" => "uint",
            "Swift.Int64" => "long",
            "Swift.UInt64" => "ulong",
            "Swift.Float" => "float",
            "Swift.Double" => "double",
            _ => null,
        };
    }

    private static string? ResolveCSharpFullName(TypeDecl bagDecl, ITypeDatabase typeDatabase)
    {
        // ProtocolDecl bags project to C# as `I`-prefixed interfaces — `\Protocol.req`
        // KeyPath literals on the consumer side compile against `KeyPath<IProtocol, V>`
        // and resolve through the witness table at use time. The TypeDatabase record
        // (if any) carries the un-prefixed printedName, so we must rebuild via the
        // central NameProvider path that owns the `I` prefix convention.
        if (bagDecl is ProtocolDecl)
        {
            var moduleName = bagDecl.ModuleDecl?.Name ?? bagDecl.SwiftTypeName?.Module ?? "";
            var ifaceName = NameProvider.GetInterfaceName(bagDecl.Name, moduleName: moduleName);
            return string.IsNullOrEmpty(moduleName)
                ? $"global::{ifaceName}"
                : $"global::{moduleName}.{ifaceName}";
        }

        if (typeDatabase.TryGetTypeRecord(bagDecl.SwiftTypeName, out var record) && record is not null)
            return record.CSharpTypeName.FullyQualifiedName;

        var parts = new List<string>();
        BaseDecl? cursor = bagDecl;
        while (cursor is TypeDecl td)
        {
            parts.Insert(0, td.Name);
            cursor = td.ParentDecl;
        }
        var fallbackModule = bagDecl.ModuleDecl?.Name ?? bagDecl.SwiftTypeName?.Module ?? "";
        parts.Insert(0, fallbackModule);
        return $"global::{string.Join(".", parts)}";
    }

    private static string ResolveConformerCSharpFullName(
        ConcreteSpecializationEngine.ConcreteConformer conformer,
        ITypeDatabase typeDatabase)
    {
        // ConcreteConformer.CSharpType is stored as `Module.Type` (no global:: prefix) and is
        // captured at conformance-index time, BEFORE the nested-type rename pre-pass
        // (NameProvider.PrecomputeNestedTypeRenames) mutates a nested type's C# name for a
        // sibling-member collision (Swift `Codec.Encoding` → C# `Codec.EncodingKind` when
        // `Codec` also has an `Encoding` property). This name is used below as the closed-
        // generic receiver type argument, so a renamed nested conformer would otherwise name a
        // non-existent type. Re-resolve a live nested conformer's post-rename name; flat and
        // hint conformers (never collision-renamed) keep their cached name unchanged.
        var raw = conformer.CSharpType;
        if (conformer.SwiftType != null &&
            conformer.SwiftType.ModuleQualifiedName.Split('.').Length > 2 &&
            typeDatabase.TryGetTypeRecord(conformer.SwiftType, out var record))
        {
            raw = record.CSharpTypeName.FullyQualifiedName;
        }
        // Add global:: to defend against the conformer namespace colliding with a using.
        if (raw.StartsWith("global::", StringComparison.Ordinal)) return raw;
        return $"global::{raw}";
    }

    private static string SanitizeSymbol(string name)
        => name.Replace(".", "_").Replace("<", "_").Replace(">", "")
               .Replace(",", "_").Replace(" ", "").Replace("[", "Arr_").Replace("]", "");

    private static string SanitizeIdentifier(string name)
        => name.Replace(".", "_").Replace("<", "_").Replace(">", "")
               .Replace(",", "_").Replace(" ", "").Replace("[", "Arr_").Replace("]", "");

    private static string StripModulePrefix(string cSharpType, string moduleName)
    {
        var prefix = moduleName + ".";
        return cSharpType.StartsWith(prefix, StringComparison.Ordinal)
            ? cSharpType.Substring(prefix.Length)
            : cSharpType;
    }

    private sealed record PreparedParam(string Name, string SwiftType, string CsType);

    private sealed record StagedOverload(
        MethodDecl Method,
        ConcreteSpecializationEngine.ConcreteConformer Conformer,
        TypeDecl BagDecl,
        string ReceiverCsType,
        string ParentSwiftClosed,
        string BagCsFullName,
        string BagSwiftQualified,
        string CsValueType,
        IReadOnlyList<string> SwiftValueVariants,
        string PublicMethodName,
        string CdeclSymbol,
        List<PreparedParam> OtherParams,
        bool IsClassReceiver,
        IReadOnlyList<AvailabilityAnnotation>? MergedAvailability);

    /// <summary>
    /// Collapse Swift V projections that would produce duplicate C# overloads.
    /// C# erases nullable-reference annotations at the overload-resolution layer,
    /// so <c>Foo</c> and <c>Foo?</c> collide as duplicate signatures (CS0111)
    /// whenever <c>Foo</c> is a reference type. Value-type Optionals
    /// (<c>Nullable&lt;int&gt;</c>) stay distinct because their <c>?</c> is
    /// genuine <see cref="Nullable{T}"/> type structure.
    ///
    /// <para>
    /// The classification uses the projection itself, not the string spelling —
    /// looking at the <see cref="OptionalProjection.InnerProjection"/> tells us
    /// whether the unwrapped type is a C# reference type (collapse <c>?</c>) or a
    /// value type (preserve <c>?</c>). The earlier whitelist-by-name approach only
    /// collapsed <c>string</c>/<c>object</c> and missed custom Swift classes,
    /// KeyPaths, ObjC bridges, collection types, NonFrozenStruct (class-projected),
    /// and async/closure projections — every one of which would yield a CS0111
    /// duplicate overload pair if both <c>Foo</c> and <c>Foo?</c> appeared in a
    /// bag.
    /// </para>
    /// </summary>
    private static string NormalizeCsOverloadKey(ITypeProjection projection)
    {
        if (projection is OptionalProjection opt && IsReferenceTypeProjection(opt.InnerProjection))
            return opt.InnerProjection.PublicType;
        return projection.PublicType;
    }

    /// <summary>
    /// True when the projection produces a C# reference type. C# erases NRT
    /// annotations on reference types so <c>T</c> and <c>T?</c> collide as the
    /// same overload signature. Value-type projections (primitives, frozen
    /// structs, enums, tuples, dates) preserve their <c>?</c> as a genuine
    /// <see cref="Nullable{T}"/>, so they are kept distinct.
    /// </summary>
    private static bool IsReferenceTypeProjection(ITypeProjection projection) =>
        projection is ClassProjection
            or KeyPathProjection
            or StringProjection
            or NonFrozenStructProjection
            or ObjCBridgedProjection
            or ObjCBridgeableProjection
            or ObjCRootedClassProjection
            or ArrayProjection
            or DictionaryProjection
            or SetProjection
            or DataProjection
            or ClosureProjection
            or AsyncProjection
            or ExistentialProjection;

    /// <summary>
    /// Builds a deterministic signature string from the prepared non-keypath
    /// params. Folded into both the C# dedup key and the Swift cdecl hash so
    /// two Route-C overloads sharing basename + V but differing in primitive
    /// param shape don't collide. <c>name@cstype</c> per param, comma-joined.
    /// </summary>
    private static string BuildParamSignature(IReadOnlyList<PreparedParam> otherParams)
    {
        if (otherParams.Count == 0) return string.Empty;
        var parts = new List<string>(otherParams.Count);
        foreach (var p in otherParams)
            parts.Add($"{p.Name}@{p.CsType}");
        return string.Join(",", parts);
    }
}
