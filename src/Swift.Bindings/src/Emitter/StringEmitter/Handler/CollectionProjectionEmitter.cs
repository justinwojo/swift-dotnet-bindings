// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Projects generic Swift structs that conform to Collection/RandomAccessCollection as
/// C# <c>IReadOnlyList&lt;TElement&gt;</c>. Two backing shapes are supported:
///
/// <para><b>Array backing.</b> The struct exposes a single public property of type
/// <c>Swift.Array&lt;Element&gt;</c> (the "Collection-with-metadata" pattern used by
/// our BindingTests <c>IndexedSeries&lt;Element&gt;</c> and <c>MusicItemBag&lt;Item&gt;</c>
/// fixtures, and by the MusicKit-shape Apple surfaces). <c>Count</c>, <c>this[int]</c>,
/// and <c>GetEnumerator()</c> delegate directly to that already-projected array.</para>
///
/// <para><b>Collection-witness backing.</b> The struct has NO public array-typed
/// backing property but does publish <c>startIndex: Int</c>, <c>endIndex: Int</c>, and
/// <c>subscript(Int) -&gt; Element</c> on its ABI. This is the shape Apple's
/// <c>WeatherKit.Forecast&lt;Element&gt;</c> exposes — opaque private storage with
/// only the Collection protocol requirements visible.
/// The projection emits two <c>@_cdecl</c> wrappers (<c>count</c> and <c>subscript</c>)
/// on top of a private Swift protocol whose conformance extension performs the
/// <c>Self</c>-typed value load and witness dispatch. Parent type metadata is passed
/// **directly from C#** (via <c>SwiftObjectHelper&lt;Type&gt;.GetTypeMetadata()</c>),
/// which means: no dlsym'd metadata-accessor helper, no per-Element PWT plumbing on
/// the Swift side, and the gate on the register-argument threshold / resolvable-PWT
/// shape drops away. The pattern works regardless of whether the parent's metadata
/// accessor ABI is thin or buffer mode, and regardless of whether Element's
/// protocol constraints (<c>Decodable</c>, <c>Encodable</c>, <c>Equatable</c>, …)
/// have static C# interfaces or runtime-only descriptor lookup.</para>
///
/// Emitted surface: <c>Count</c>, <c>this[int index]</c>, <c>GetEnumerator()</c>,
/// and the non-generic <c>IEnumerable.GetEnumerator()</c>.
/// </summary>
internal static class CollectionProjectionEmitter
{
    private static readonly HashSet<string> s_collectionProtocols = new()
    {
        "Swift.Collection",
        "Swift.RandomAccessCollection",
        "Swift.BidirectionalCollection",
        "Swift.Sequence",
    };

    /// <summary>
    /// Discriminates how <see cref="EmitMembers"/> builds its projection bodies.
    /// </summary>
    private enum BackingKind
    {
        /// <summary>Delegate through a public <c>[Element]</c> property (original shape).</summary>
        ArrayProperty,
        /// <summary>Dispatch through freshly-emitted <c>count</c>/<c>subscript</c> witness wrappers.</summary>
        CollectionWitness,
    }

    private sealed record BackingInfo(
        BackingKind Kind,
        string ElementCsName,
        PropertyDecl? ArrayProperty,
        SubscriptDecl? ElementSubscript);

    /// <summary>
    /// Decides whether the projection will fire on this struct. Returns the
    /// C# interface name to add to the class's interface list (e.g.
    /// <c>IReadOnlyList&lt;TElement&gt;</c>) or <c>null</c> when the projection
    /// does not apply. Must be called before the class header is written so the
    /// interface can be inserted.
    ///
    /// <para>The witness-backed path uses parent-metadata-direct-pass (fetched on
    /// the C# side via <c>SwiftObjectHelper&lt;Type&gt;.GetTypeMetadata()</c>), so
    /// unlike the pre-2026-04-23 design it is independent of the parent type's
    /// metadata-accessor ABI variant and its per-Element PWT resolvability.</para>
    /// </summary>
    public static string? TryPlanInterface(
        StructDecl structDecl,
        ITypeDatabase typeDatabase)
    {
        var backing = TryFindBacking(structDecl, typeDatabase);
        if (backing is null)
            return null;

        return $"global::System.Collections.Generic.IReadOnlyList<{backing.ElementCsName}>";
    }

    /// <summary>
    /// Emits the projection member bodies. Call only after <see cref="TryPlanInterface"/>
    /// has returned non-null for this struct. For Collection-witness backings the caller
    /// must pass <paramref name="swiftWriter"/>, <paramref name="moduleCtx"/>, and the
    /// C# <paramref name="typeNameWithGenerics"/> (e.g. <c>"Forecast&lt;TElement&gt;"</c>)
    /// so the projection can emit its own <c>@_cdecl</c> wrappers + P/Invoke declarations
    /// and reference the parent type via <c>SwiftObjectHelper&lt;…&gt;.GetTypeMetadata()</c>.
    /// For array backings those parameters are unused and may be null.
    /// </summary>
    public static void EmitMembers(
        CSharpWriter csWriter,
        StructDecl structDecl,
        string typeNameWithGenerics,
        ITypeDatabase typeDatabase,
        IReadOnlyDictionary<string, string>? propertyRenames,
        ILogger logger,
        SwiftWriter? swiftWriter = null,
        ModuleEmissionContext? moduleCtx = null,
        PInvokeHelperContext? pinvokeHelperContext = null,
        IReadOnlySet<string>? alreadyEmittedMembers = null)
    {
        var backing = TryFindBacking(structDecl, typeDatabase);
        if (backing is null)
            return;

        if (backing.Kind == BackingKind.ArrayProperty)
        {
            EmitArrayBackedMembers(csWriter, structDecl, backing, propertyRenames, logger, alreadyEmittedMembers);
            return;
        }

        // Collection-witness backing — requires Swift wrapper + P/Invoke plumbing.
        if (swiftWriter is null || moduleCtx is null || pinvokeHelperContext is null)
        {
            logger.LogInformation(
                "Skipping Collection-witness projection on '{TypeName}' — Swift/emission context unavailable.",
                structDecl.Name);
            return;
        }

        EmitWitnessBackedMembers(
            csWriter, swiftWriter, moduleCtx, pinvokeHelperContext,
            structDecl, typeNameWithGenerics, backing, typeDatabase, logger,
            alreadyEmittedMembers);
    }

    private static void EmitArrayBackedMembers(
        CSharpWriter csWriter,
        StructDecl structDecl,
        BackingInfo backing,
        IReadOnlyDictionary<string, string>? propertyRenames,
        ILogger logger,
        IReadOnlySet<string>? alreadyEmittedMembers)
    {
        var prop = backing.ArrayProperty!;
        var elementCsName = backing.ElementCsName;
        var backingCsName = NameProvider.GetFinalMemberName(
            NameProvider.GetPropertyName(prop, structDecl.Name), propertyRenames);

        // Skip Count when the property handler already emitted it (e.g., Swift `count: Int`
        // surfaces as a regular C# property before this projection runs — RealityFoundation
        // MeshBuffer<TElement> hits this). Without the gate the duplicate produces CS0102.
        bool emitCount = alreadyEmittedMembers is null || !alreadyEmittedMembers.Contains("Count");
        if (emitCount)
        {
            csWriter.WriteLine();
            csWriter.WriteLine("/// <summary>Number of elements — projection of Swift <c>Collection.count</c>.</summary>");
            csWriter.WriteLine($"public int Count => {backingCsName}.Count;");
        }
        csWriter.WriteLine();
        csWriter.WriteLine("/// <summary>Element access — projection of Swift <c>subscript(_:)</c>.</summary>");
        csWriter.WriteLine($"public {elementCsName} this[int index] => {backingCsName}[index];");
        csWriter.WriteLine();
        csWriter.WriteLine("/// <summary>Iterates the collection in index order.</summary>");
        csWriter.WriteLine($"public global::System.Collections.Generic.IEnumerator<{elementCsName}> GetEnumerator() => {backingCsName}.GetEnumerator();");
        csWriter.WriteLine("global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();");
        csWriter.WriteLine();

        logger.LogInformation(
            "Emitted Collection projection (array-backed) on '{TypeName}' backed by property '{Backing}' (Count emitted: {EmitCount}).",
            structDecl.Name, prop.Name, emitCount);
    }

    private static void EmitWitnessBackedMembers(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        ModuleEmissionContext moduleCtx,
        PInvokeHelperContext pinvokeHelperContext,
        StructDecl structDecl,
        string typeNameWithGenerics,
        BackingInfo backing,
        ITypeDatabase typeDatabase,
        ILogger logger,
        IReadOnlySet<string>? alreadyEmittedMembers)
    {
        bool emitCount = alreadyEmittedMembers is null || !alreadyEmittedMembers.Contains("Count");
        var elementCsName = backing.ElementCsName;

        // Symbol naming — deterministic hash on the full type identity so a rebuild with
        // no source change produces the same symbol. The moduleQualifiedName includes
        // the module, so two modules with same short type name get distinct symbols.
        var moduleQualifiedName = structDecl.SwiftTypeName.ModuleQualifiedName;
        var symbolSource = $"{moduleQualifiedName}:CollProj:witness";
        var hash = EmitterUtility.DeterministicHash8(symbolSource);
        var subscriptSymbol = $"SBW_CollProj_subscript_{hash}";
        var countSymbol = $"SBW_CollProj_count_{hash}";
        var pinvokeSubscriptName = $"PInvoke_collSubscript_{hash}";
        var pinvokeCountName = $"PInvoke_collCount_{hash}";

        EmitCollectionSwiftWrappers(
            swiftWriter, structDecl, backing.ElementSubscript!, hash,
            subscriptSymbol, countSymbol);

        var moduleName = structDecl.SwiftTypeName.Module;
        var libraryPath = typeDatabase.AsyncLibraryName ?? typeDatabase.GetLibraryPath(moduleName);

        // C# P/Invoke declarations — the Swift wrappers take only parent-metadata +
        // self (plus resultPtr/position for subscript), no Element metadata and no
        // PWT args. That is what lets this path fire on Apple-shape types whose
        // Element carries constraints (Decodable/Encodable/Equatable/…) that the
        // static-interface projection can't materialize at the C# layer.
        pinvokeHelperContext.AddDeclaration(new PInvokeDeclaration
        {
            LibraryPath = libraryPath,
            EntryPoint = subscriptSymbol,
            MethodName = pinvokeSubscriptName,
            ReturnType = "void",
            ParametersString = "IntPtr resultPtr, nint position, IntPtr parentMeta, IntPtr self_",
            CallingConvention = PInvokeCallingConvention.Cdecl,
        });
        pinvokeHelperContext.AddDeclaration(new PInvokeDeclaration
        {
            LibraryPath = libraryPath,
            EntryPoint = countSymbol,
            MethodName = pinvokeCountName,
            ReturnType = "nint",
            ParametersString = "IntPtr parentMeta, IntPtr self_",
            CallingConvention = PInvokeCallingConvention.Cdecl,
        });

        // C# body — Count dispatches to the count wrapper, subscript to the subscript
        // wrapper, enumerator iterates through the projected indexer. Count is gated by
        // `alreadyEmittedMembers` so a Swift `count: Int` property already emitted by
        // PropertyHandler doesn't collide (CS0102 — see MeshBuffer<TElement> on RealityFoundation).
        if (emitCount)
        {
            csWriter.WriteLine();
            csWriter.WriteLine("/// <summary>Number of elements — projection of Swift <c>Collection.count</c>.</summary>");
            csWriter.WriteLine("public int Count");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("get");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine($"var __parentMeta = SwiftObjectHelper<{typeNameWithGenerics}>.GetTypeMetadata();");
            csWriter.WriteLine("var __success = false;");
            csWriter.WriteLine("_payload.DangerousAddRef(ref __success);");
            csWriter.WriteLine("try");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine($"var __count = {pinvokeHelperContext.HelperClassName}.{pinvokeCountName}(__parentMeta.Handle, _payload.DangerousGetHandle());");
            csWriter.WriteLine("return checked((int)__count);");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine("finally");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("if (__success)");
            csWriter.Indent++;
            csWriter.WriteLine("_payload.DangerousRelease();");
            csWriter.Indent--;
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();
        }

        EmitWitnessIndexerBody(
            csWriter, structDecl, typeNameWithGenerics, elementCsName,
            pinvokeHelperContext, pinvokeSubscriptName);

        csWriter.WriteLine();
        csWriter.WriteLine("/// <summary>Iterates the collection in index order via the projected indexer.</summary>");
        csWriter.WriteLine($"public global::System.Collections.Generic.IEnumerator<{elementCsName}> GetEnumerator()");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("int __count = Count;");
        csWriter.WriteLine("for (int __i = 0; __i < __count; __i++)");
        csWriter.Indent++;
        csWriter.WriteLine("yield return this[__i];");
        csWriter.Indent--;
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine("global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();");
        csWriter.WriteLine();

        logger.LogInformation(
            "Emitted Collection projection (witness-backed, parent-meta-direct) on '{TypeName}' via subscript symbol '{Symbol}'.",
            structDecl.Name, subscriptSymbol);
    }

    /// <summary>
    /// Emits the Swift-side plumbing for the witness-backed Collection projection:
    /// a private protocol carrying static <c>_sbw_coll_count</c> and
    /// <c>_sbw_coll_sub</c> requirements, an extension conforming the target struct
    /// to that protocol (where the <c>Self</c>-typed load / <c>count</c> and
    /// <c>subscript</c> witness calls actually live), and the pair of
    /// <c>@_cdecl</c> wrappers C# calls into.
    ///
    /// <para>The wrappers take parent type metadata as an <c>UnsafeRawPointer</c>
    /// passed from C# — no Element metadata, no PWT args, no dlsym'd metadata
    /// accessor helper. The metatype is reconstructed via
    /// <c>unsafeBitCast(parentMeta, to: Any.Type.self) as! any Protocol.Type</c>,
    /// which dispatches through Swift's existential-metatype mechanism rather than
    /// the generic-specialization ABI — sidestepping the register-argument threshold
    /// and the resolvable-interface gate that the earlier design inherited from
    /// <see cref="MetatypeHelperEmitter"/>.</para>
    /// </summary>
    private static void EmitCollectionSwiftWrappers(
        SwiftWriter swiftWriter,
        StructDecl structDecl,
        SubscriptDecl subscriptDecl,
        string hash,
        string subscriptSymbol,
        string countSymbol)
    {
        var protocolName = $"_SBW_Coll_{hash}";
        var subDispatchName = $"_sbw_coll_sub_{hash}";
        var countDispatchName = $"_sbw_coll_count_{hash}";
        var moduleQualifiedName = structDecl.SwiftTypeName.ModuleQualifiedName;

        // Element as it appears in the sugared Swift source — maps τ_0_0 → Element
        // for the parent's generic parameter.
        var abiToSugaredName = WrapperValidation.GetAbiToSugaredNameMap(structDecl);
        var elementSwiftType = WrapperValidation.RenderSwiftTypeSpecWithSugaredNames(
            subscriptDecl.ReturnTypeSpec, abiToSugaredName);

        // Merge availability early: the conformance extension targets a type that may
        // carry platform-version floors (e.g., WeatherKit.HourlyWeatherStatistics is
        // iOS 18+). Without matching @available on the extension itself, swiftc rejects
        // the extension body ("type is only available in iOS 18.0 or newer").
        //
        // Include the subscript decl's own availability annotations — the witness-backed
        // projection dispatches through `Self[position]` (the exact decl in hand), so a
        // stricter subscript-level floor must flow onto the conformance extension and
        // both @_cdecl wrappers. Parent-chain annotations are still merged via
        // MergeAvailability's ancestor walk.
        var availability = WrapperEmitterHelpers.MergeAvailability(
            subscriptDecl.AvailabilityAnnotations, structDecl);

        // The dispatch protocol + conformance extension carry no @_cdecl symbol; the anchor pins
        // both symbol-less blocks to the subscript that owns them so a wrapper-compile failure inside
        // either attributes to it rather than the coarse module scope, and the post-processor strips
        // the anchor with the block it names.
        var originAnchor = OriginAnchorEmitter.LineForWrapper(subscriptDecl);

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            {{originAnchor}}
            private protocol {{protocolName}} {
                static func {{countDispatchName}}(selfPtr: UnsafeRawPointer) -> Int
                static func {{subDispatchName}}(resultPtr: UnsafeMutableRawPointer, position: Int, selfPtr: UnsafeRawPointer)
            }
            """);

        // Conformance extension — Self is the generic struct with its parameter bound
        // by the caller's metadata, so obj.count / obj[position] dispatch through the
        // normal Swift protocol-witness path. The subscript result write uses
        // initializeMemory to preserve ARC semantics for non-trivial element types
        // (classes, nested structs with reference fields). Loading obj by value
        // copies Self — for non-move-only structs the compiler handles the reference
        // fields' retain, so this is safe.
        OriginAnchorEmitter.Write(swiftWriter, FragmentOwners.ForDeclWrapper(subscriptDecl).Artifact);
        WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);
        swiftWriter.WriteLines($$"""
            extension {{moduleQualifiedName}}: {{protocolName}} {
                static func {{countDispatchName}}(selfPtr: UnsafeRawPointer) -> Int {
                    let obj = selfPtr.assumingMemoryBound(to: Self.self).pointee
                    return obj.count
                }
                static func {{subDispatchName}}(resultPtr: UnsafeMutableRawPointer, position: Int, selfPtr: UnsafeRawPointer) {
                    let obj = selfPtr.assumingMemoryBound(to: Self.self).pointee
                    let result: {{elementSwiftType}} = obj[position]
                    resultPtr.initializeMemory(as: {{elementSwiftType}}.self, repeating: result, count: 1)
                }
            }
            """);

        // Count wrapper.
        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Collection count @_cdecl wrapper for {{moduleQualifiedName}} — returns Self.count.
            // Parent type metadata is supplied by the C# caller so we avoid the dlsym'd
            // metadata-accessor helper (which requires thin-mode ABI and resolvable PWTs).
            """);
        WrapperEmitterHelpers.EmitCdeclAnnotation(
            swiftWriter, countSymbol, needsMainActor: false,
            availabilityAnnotations: availability);
        swiftWriter.WriteLine($"public func _sbw_coll_count_cdecl_{hash}(_ parentMetaPtr: UnsafeRawPointer, _ self_: UnsafeRawPointer) -> Int {{");
        swiftWriter.Indent++;
        swiftWriter.WriteLine($"let metatype = unsafeBitCast(parentMetaPtr, to: Any.Type.self) as! any {protocolName}.Type");
        swiftWriter.WriteLine($"return metatype.{countDispatchName}(selfPtr: self_)");
        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");

        // Subscript wrapper.
        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Collection subscript @_cdecl wrapper for {{moduleQualifiedName}}.subscript(_:) -> {{elementSwiftType}}.
            // Parent type metadata is supplied by the C# caller (see count wrapper above).
            """);
        WrapperEmitterHelpers.EmitCdeclAnnotation(
            swiftWriter, subscriptSymbol, needsMainActor: false,
            availabilityAnnotations: availability);
        swiftWriter.WriteLine($"public func _sbw_coll_subscript_cdecl_{hash}(_ resultPtr: UnsafeMutableRawPointer, _ position: Int, _ parentMetaPtr: UnsafeRawPointer, _ self_: UnsafeRawPointer) {{");
        swiftWriter.Indent++;
        swiftWriter.WriteLine($"let metatype = unsafeBitCast(parentMetaPtr, to: Any.Type.self) as! any {protocolName}.Type");
        swiftWriter.WriteLine($"metatype.{subDispatchName}(resultPtr: resultPtr, position: position, selfPtr: self_)");
        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    private static void EmitWitnessIndexerBody(
        CSharpWriter csWriter,
        StructDecl structDecl,
        string typeNameWithGenerics,
        string elementCsName,
        PInvokeHelperContext pinvokeHelperContext,
        string pinvokeMethodName)
    {
        // NativeMemory.Alloc the buffer (not stackalloc) because some Element
        // types adopt the handle in their NewFromPayload constructor (non-frozen
        // struct pattern — stores the provided pointer directly in the Payload
        // SafeHandle). A stack buffer would die when the indexer returns, leaving
        // the new TElement with a dangling pointer. NativeMemory.Alloc lets the
        // adopting TElement take ownership. If TElement uses copy semantics
        // instead (frozen-struct-as-class: allocates its own buffer and copies
        // via VWT.InitializeWithCopy), we detect that by comparing the returned
        // object's Payload handle against our allocation and free it ourselves
        // to avoid a leak. Single-generic-param structs only (TryFindBacking
        // enforces that).
        var tparam = structDecl.GenericParameters[0];
        var csGenericName = NameProvider.GetCSharpGenericParameterName(tparam, 0);

        csWriter.WriteLine("/// <summary>Element access — projection of Swift <c>subscript(_:)</c> via Collection witness.</summary>");
        csWriter.WriteLine($"public {elementCsName} this[int index]");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("get");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine($"var {csGenericName}Metadata = TypeMetadata.GetTypeMetadataOrThrow<{csGenericName}>();");
        csWriter.WriteLine($"var __parentMeta = SwiftObjectHelper<{typeNameWithGenerics}>.GetTypeMetadata();");
        csWriter.WriteLine("var __success = false;");
        csWriter.WriteLine("_payload.DangerousAddRef(ref __success);");
        csWriter.WriteLine("try");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine($"var __size = checked((nuint){csGenericName}Metadata.Size);");
        csWriter.WriteLine("unsafe");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("void* __cdeclBuf = NativeMemory.Alloc(__size);");
        csWriter.WriteLine("try");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine($"{pinvokeHelperContext.HelperClassName}.{pinvokeMethodName}((IntPtr)__cdeclBuf, (nint)index, __parentMeta.Handle, _payload.DangerousGetHandle());");
        csWriter.WriteLine($"var __element = SwiftMarshal.MarshalFromSwift<{elementCsName}>(new IntPtr(__cdeclBuf));");
        csWriter.WriteLine("if (__element is ISwiftObject __so && __so.SwiftHandle == (IntPtr)__cdeclBuf)");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("__cdeclBuf = null;");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine("return __element;");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine("finally");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("if (__cdeclBuf != null) NativeMemory.Free(__cdeclBuf);");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine("finally");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("if (__success)");
        csWriter.Indent++;
        csWriter.WriteLine("_payload.DangerousRelease();");
        csWriter.Indent--;
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.Indent--;
        csWriter.WriteLine("}");
    }

    private static BackingInfo? TryFindBacking(StructDecl structDecl, ITypeDatabase typeDatabase)
    {
        if (structDecl.GenericParameters.Count != 1)
            return null;
        if (!HasCollectionConformance(structDecl))
            return null;

        var param = structDecl.GenericParameters[0];
        var unsugaredName = param.TypeName;
        var sugaredName = param.SugaredTypeName;
        if (string.IsNullOrEmpty(unsugaredName) || string.IsNullOrEmpty(sugaredName))
            return null;

        var csParamName = NameProvider.GetCSharpGenericParameterName(param, 0);

        // Prefer the array-backing projection — cheaper, delegates to a real C# property.
        var arrayBacking = TryFindArrayBacking(structDecl, typeDatabase, unsugaredName, sugaredName);
        if (arrayBacking is not null)
            return new BackingInfo(BackingKind.ArrayProperty, csParamName, arrayBacking, null);

        // Fallback — Apple's WeatherKit `Forecast<Element>` shape: private backing, but
        // `startIndex`/`endIndex`/`subscript(Int) -> Element` are public on the struct's ABI.
        // Dispatch through the Collection witnesses directly.
        var witnessSubscript = TryFindWitnessBacking(structDecl, unsugaredName, sugaredName);
        if (witnessSubscript is not null)
            return new BackingInfo(BackingKind.CollectionWitness, csParamName, null, witnessSubscript);

        return null;
    }

    private static PropertyDecl? TryFindArrayBacking(
        StructDecl structDecl,
        ITypeDatabase typeDatabase,
        string unsugaredElementName,
        string sugaredElementName)
    {
        PropertyDecl? match = null;
        foreach (var p in structDecl.Properties)
        {
            // Keep backing-property filters in sync with MemberEmissionValidator.CanEmitProperty:
            // if the property won't be emitted, the projection body has nothing to delegate to.
            if (p.IsStatic || p.IsModuleInternal || p.IsSpiProtected)
                continue;
            var skipReason = MemberEmissionValidator.CanEmitProperty(p, typeDatabase, out _, out _);
            if (skipReason != null)
                continue;
            if (p.SwiftTypeSpec is not NamedTypeSpec named)
                continue;
            if (named.Name != "Swift.Array" || named.GenericParameters.Count != 1)
                continue;
            if (named.GenericParameters[0] is not NamedTypeSpec elem)
                continue;
            if (elem.Name != unsugaredElementName && elem.Name != sugaredElementName)
                continue;
            if (match is not null)
                return null;
            match = p;
        }
        return match;
    }

    private static SubscriptDecl? TryFindWitnessBacking(
        StructDecl structDecl,
        string unsugaredElementName,
        string sugaredElementName)
    {
        // Require public `startIndex: Int` and `endIndex: Int` on the raw ABI — these are
        // the shape guarantees that let us safely dispatch through `Self.count` /
        // `Self[position]` defaults inside the @_cdecl wrappers. We do NOT require these
        // to be emittable as C# properties (PropertyHandler's `HasUnsupportedProtocolConstraints`
        // gate skips them when Element carries Self-requirement conformances like Decodable,
        // which is exactly the Apple-shape case this path exists for). We only need the
        // ABI-level presence as a sanity check that Collection conformance is formed.
        if (!HasPublicIntProperty(structDecl, "startIndex") ||
            !HasPublicIntProperty(structDecl, "endIndex"))
            return null;

        // Find a public non-static subscript whose sole parameter is `Swift.Int` and whose
        // return type is the struct's generic parameter. Match by unsugared (τ_0_0) or
        // sugared (source) name — Apple's ABI and our source-compiled ABI differ.
        foreach (var sub in structDecl.Subscripts)
        {
            if (sub.IsStatic)
                continue;
            if (sub.IndexParameters.Count != 1)
                continue;
            if (sub.IndexParameters[0].SwiftTypeSpec is not NamedTypeSpec indexSpec)
                continue;
            if (indexSpec.Name != "Swift.Int")
                continue;
            var returnSpec = sub.ReturnTypeSpec;
            if (returnSpec is not NamedTypeSpec retNamed)
                continue;
            if (retNamed.Name != unsugaredElementName && retNamed.Name != sugaredElementName)
                continue;
            // Require a getter — write-only subscripts can't power IReadOnlyList projection.
            if (!sub.HasGetter)
                continue;
            return sub;
        }
        return null;
    }

    private static bool HasPublicIntProperty(StructDecl structDecl, string swiftPropertyName)
    {
        foreach (var p in structDecl.Properties)
        {
            if (p.IsStatic || p.IsModuleInternal || p.IsSpiProtected)
                continue;
            if (p.Name != swiftPropertyName)
                continue;
            if (p.SwiftTypeSpec is not NamedTypeSpec named)
                continue;
            // Accept the bare `Swift.Int` spec; associated-type aliases like `Self.Index`
            // currently collapse to the underlying `Swift.Int` on generic-parent resolution.
            if (named.Name == "Swift.Int")
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true when the struct conforms to Swift.Collection, Sequence,
    /// BidirectionalCollection, or RandomAccessCollection. Shared with
    /// <see cref="GenericDispatchEmitter.CanEmitStaticDispatch"/> and
    /// <c>PropertyWrapperEmitter.CanEmitGenericClassPropertyWrapper</c> to relax the
    /// generic-parent-param gates for Collection-family conformers.
    /// </summary>
    internal static bool HasCollectionConformance(StructDecl structDecl)
    {
        // Match only the module-qualified Swift.* protocols. An earlier defensive
        // unqualified-Name fallback ("Collection", "Sequence", …) false-positived on
        // any third-party protocol with the same simple name (e.g. user-defined
        // `Other.Collection`). SwiftTypeName.FromModuleQualifiedName always populates
        // a module component, so the unqualified path was dead anyway for parsed ABI
        // data — only constructed conformances with a non-Swift module would have hit it.
        foreach (var c in structDecl.Conformances)
        {
            if (s_collectionProtocols.Contains(c.Protocol.ModuleQualifiedName))
                return true;
        }
        return false;
    }
}
