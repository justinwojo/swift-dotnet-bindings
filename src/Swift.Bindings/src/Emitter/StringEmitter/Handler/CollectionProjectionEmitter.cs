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
/// <c>subscript(Int) -&gt; Element</c>. This is the shape Apple's
/// <c>WeatherKit.Forecast&lt;Element&gt;</c> exposes — opaque private storage with
/// only the Collection protocol requirements visible. The projection delegates
/// <c>Count</c> to <c>EndIndex - StartIndex</c>, <c>this[int]</c> to a freshly-emitted
/// <c>@_cdecl</c> subscript wrapper (generic static dispatch, mirroring the
/// Session 2 property-wrapper pattern), and <c>GetEnumerator()</c> iterates via
/// the projected indexer.</para>
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
        /// <summary>Delegate through <c>StartIndex</c>/<c>EndIndex</c>/<c>subscript</c> witnesses.</summary>
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
    /// <para>For the Collection-witness backing path the planner must apply the
    /// same gates as <see cref="EmitWitnessBackedMembers"/> so that a "yes" plan
    /// is always followed by actual member emission — adding
    /// <c>IReadOnlyList&lt;T&gt;</c> to the class header while the witness-backed
    /// body silently bails on an indeterminate PWT would leave CS0535 holes
    /// (Apple surfaces with
    /// <c>Sendable &amp; Decodable &amp; Encodable</c>-heavy Element
    /// constraints are the canary).</para>
    /// </summary>
    public static string? TryPlanInterface(
        StructDecl structDecl,
        ITypeDatabase typeDatabase,
        PInvokeHelperContext? pinvokeHelperContext = null)
    {
        var backing = TryFindBacking(structDecl, typeDatabase);
        if (backing is null)
            return null;

        // Array-backed path delegates to a concrete C# property — no PWT plumbing,
        // so no shape guards apply. Emit the interface unconditionally.
        if (backing.Kind == BackingKind.ArrayProperty)
            return $"global::System.Collections.Generic.IReadOnlyList<{backing.ElementCsName}>";

        // Collection-witness path needs a PWT-resolvable dispatch shape AND the
        // emission context that EmitMembers receives later. Mirror every early
        // return in EmitWitnessBackedMembers / EmitWitnessIndexerBody so the two
        // stay in lockstep.
        if (pinvokeHelperContext is null)
            return null;
        if (pinvokeHelperContext.HasIndeterminatePwtShape)
            return null;
        if (pinvokeHelperContext.ExceedsRegisterArgumentThreshold)
            return null;
        foreach (var entry in pinvokeHelperContext.PwtEntries)
        {
            if (!entry.IsResolvable || string.IsNullOrEmpty(entry.ResolvableInterfaceName))
                return null;
        }

        return $"global::System.Collections.Generic.IReadOnlyList<{backing.ElementCsName}>";
    }

    /// <summary>
    /// Emits the projection member bodies. Call only after <see cref="TryPlanInterface"/>
    /// has returned non-null for this struct. For Collection-witness backings the caller
    /// must also pass <paramref name="swiftWriter"/>, <paramref name="moduleCtx"/>, and
    /// <paramref name="pinvokeHelperContext"/> so the projection can emit its own
    /// <c>@_cdecl</c> subscript wrapper + P/Invoke declaration. For array backings
    /// those parameters are ignored.
    /// </summary>
    public static void EmitMembers(
        CSharpWriter csWriter,
        StructDecl structDecl,
        ITypeDatabase typeDatabase,
        IReadOnlyDictionary<string, string>? propertyRenames,
        ILogger logger,
        SwiftWriter? swiftWriter = null,
        ModuleEmissionContext? moduleCtx = null,
        PInvokeHelperContext? pinvokeHelperContext = null)
    {
        var backing = TryFindBacking(structDecl, typeDatabase);
        if (backing is null)
            return;

        if (backing.Kind == BackingKind.ArrayProperty)
        {
            EmitArrayBackedMembers(csWriter, structDecl, backing, propertyRenames, logger);
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
            structDecl, backing, typeDatabase, logger);
    }

    private static void EmitArrayBackedMembers(
        CSharpWriter csWriter,
        StructDecl structDecl,
        BackingInfo backing,
        IReadOnlyDictionary<string, string>? propertyRenames,
        ILogger logger)
    {
        var prop = backing.ArrayProperty!;
        var elementCsName = backing.ElementCsName;
        var backingCsName = NameProvider.GetFinalMemberName(
            NameProvider.GetPropertyName(prop.Name, structDecl.Name), propertyRenames);

        csWriter.WriteLine();
        csWriter.WriteLine("/// <summary>Number of elements — projection of Swift <c>Collection.count</c>.</summary>");
        csWriter.WriteLine($"public int Count => {backingCsName}.Count;");
        csWriter.WriteLine();
        csWriter.WriteLine("/// <summary>Element access — projection of Swift <c>subscript(_:)</c>.</summary>");
        csWriter.WriteLine($"public {elementCsName} this[int index] => {backingCsName}[index];");
        csWriter.WriteLine();
        csWriter.WriteLine("/// <summary>Iterates the collection in index order.</summary>");
        csWriter.WriteLine($"public global::System.Collections.Generic.IEnumerator<{elementCsName}> GetEnumerator() => {backingCsName}.GetEnumerator();");
        csWriter.WriteLine("global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();");
        csWriter.WriteLine();

        logger.LogInformation(
            "Emitted Collection projection (array-backed) on '{TypeName}' backed by property '{Backing}'.",
            structDecl.Name, prop.Name);
    }

    private static void EmitWitnessBackedMembers(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        ModuleEmissionContext moduleCtx,
        PInvokeHelperContext pinvokeHelperContext,
        StructDecl structDecl,
        BackingInfo backing,
        ITypeDatabase typeDatabase,
        ILogger logger)
    {
        // Guard against generator states that would produce wrong Swift metadata-accessor
        // shape. NonFrozenStructHandler's TypeMetadataAccessorSkipGate normally filters
        // these types out before we reach emission, but re-check defensively.
        if (pinvokeHelperContext.HasIndeterminatePwtShape ||
            pinvokeHelperContext.ExceedsRegisterArgumentThreshold)
        {
            logger.LogInformation(
                "Skipping Collection-witness projection on '{TypeName}' — indeterminate PWT / over-threshold metadata accessor.",
                structDecl.Name);
            return;
        }

        var elementCsName = backing.ElementCsName;

        // Symbol naming — deterministic hash on the full type identity so a rebuild with
        // no source change produces the same symbol. The moduleQualifiedName includes
        // the module, so two modules with same short type name get distinct symbols.
        var moduleQualifiedName = structDecl.SwiftTypeName.ModuleQualifiedName;
        var symbolSource = $"{moduleQualifiedName}:CollProj:subscript";
        var hash = EmitterUtility.DeterministicHash8(symbolSource);
        var cdeclSymbolName = $"SBW_CollProj_subscript_{hash}";
        var pinvokeMethodName = $"PInvoke_collSubscript_{hash}";

        // Emit the Swift @_cdecl wrapper + the C# P/Invoke declaration in the existing
        // {TypeName}_PInvoke helper class. This runs exactly once per struct because
        // EmitMembers runs once per struct emission.
        EmitSubscriptSwiftWrapper(
            swiftWriter, moduleCtx, structDecl, typeDatabase, pinvokeHelperContext,
            backing.ElementSubscript!, hash, cdeclSymbolName);

        var moduleName = structDecl.SwiftTypeName.Module;
        var libraryPath = typeDatabase.AsyncLibraryName ?? typeDatabase.GetLibraryPath(moduleName);

        // Build the C# P/Invoke parameter list — matches the @_cdecl signature emitted
        // below (resultPtr, position, metadata..., pwt..., self_).
        var pinvokeParams = new List<string> { "IntPtr resultPtr", "nint position" };
        for (int i = 0; i < structDecl.GenericParameters.Count; i++)
            pinvokeParams.Add($"IntPtr _metadata{i}");
        for (int i = 0; i < pinvokeHelperContext.PwtEntries.Count; i++)
            pinvokeParams.Add($"IntPtr _pwt{i}");
        pinvokeParams.Add("IntPtr _self");

        pinvokeHelperContext.AddDeclaration(new PInvokeDeclaration
        {
            LibraryPath = libraryPath,
            EntryPoint = cdeclSymbolName,
            MethodName = pinvokeMethodName,
            ReturnType = "void",
            ParametersString = string.Join(", ", pinvokeParams),
            CallingConvention = PInvokeCallingConvention.Cdecl,
        });

        // C# body — Count via StartIndex/EndIndex (emitted as int properties by the
        // PropertyWrapperEmitter Collection-family path), subscript via our new P/Invoke,
        // enumerator iterates via the projected indexer.
        csWriter.WriteLine();
        csWriter.WriteLine("/// <summary>Number of elements — projection of Swift <c>Collection.count</c> computed as <c>endIndex - startIndex</c>.</summary>");
        csWriter.WriteLine("public int Count => checked(EndIndex - StartIndex);");
        csWriter.WriteLine();

        EmitWitnessIndexerBody(
            csWriter, structDecl, elementCsName,
            pinvokeHelperContext, pinvokeMethodName);

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
            "Emitted Collection projection (witness-backed) on '{TypeName}' via subscript symbol '{Symbol}'.",
            structDecl.Name, cdeclSymbolName);
    }

    /// <summary>
    /// Emits the Swift-side <c>@_cdecl</c> subscript wrapper using generic static dispatch,
    /// mirroring <see cref="PropertyWrapperEmitter"/>'s getter wrapper shape but specialized
    /// for <c>subscript(Int) -&gt; Element</c>.
    /// </summary>
    private static void EmitSubscriptSwiftWrapper(
        SwiftWriter swiftWriter,
        ModuleEmissionContext moduleCtx,
        StructDecl structDecl,
        ITypeDatabase typeDatabase,
        PInvokeHelperContext pinvokeHelperContext,
        SubscriptDecl subscriptDecl,
        string hash,
        string cdeclSymbolName)
    {
        var protocolName = $"_SBW_GSS_{hash}";
        var dispatchMethodName = $"_sbw_coll_sub_{hash}";
        var moduleQualifiedName = structDecl.SwiftTypeName.ModuleQualifiedName;

        // Element as it appears in the sugared Swift source — maps τ_0_0 → Element
        // for the parent's generic parameter.
        var abiToSugaredName = WrapperValidation.GetAbiToSugaredNameMap(structDecl);
        var elementSwiftType = WrapperValidation.RenderSwiftTypeSpecWithSugaredNames(
            subscriptDecl.ReturnTypeSpec, abiToSugaredName);

        // Emit the protocol + conformance extension that encapsulates the subscript body.
        // We do a value-type read (Self is a struct) and write the result into resultPtr via
        // initializeMemory — the ARC-safe path for non-trivial element types.
        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            private protocol {{protocolName}} {
                static func {{dispatchMethodName}}(resultPtr: UnsafeMutableRawPointer, position: Int, selfPtr: UnsafeRawPointer)
            }
            """);

        swiftWriter.WriteLines($$"""
            extension {{moduleQualifiedName}}: {{protocolName}} {
                static func {{dispatchMethodName}}(resultPtr: UnsafeMutableRawPointer, position: Int, selfPtr: UnsafeRawPointer) {
                    let obj = selfPtr.assumingMemoryBound(to: Self.self).pointee
                    let result: {{elementSwiftType}} = obj[position]
                    resultPtr.initializeMemory(as: {{elementSwiftType}}.self, repeating: result, count: 1)
                }
            }
            """);

        // Metadata accessor helper — dedupes across this type's property/method/subscript
        // wrappers so only one copy exists per (type, pwtCount) pair.
        int pwtCount = pinvokeHelperContext.PwtEntries.Count;
        var metaHelperName = MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(
            swiftWriter, structDecl, moduleCtx, pwtCount: pwtCount);

        // Build @_cdecl param list — mirror CdeclSignatureContract for methods:
        // [ResultPtr] [Arguments] [Metadata] [PWT] [Self]
        var cdeclParams = new List<string>
        {
            "_ resultPtr: UnsafeMutableRawPointer",
            "_ position: Int",
        };
        for (int i = 0; i < structDecl.GenericParameters.Count; i++)
            cdeclParams.Add($"_ _metadata{i}: UnsafeRawPointer");
        for (int i = 0; i < pwtCount; i++)
            cdeclParams.Add($"_ _pwt{i}: UnsafeRawPointer");
        cdeclParams.Add("_ self_: UnsafeRawPointer");

        var swiftFuncName = $"_sbw_coll_subscript_{hash}";
        var metaArgs = string.Join(", ",
            Enumerable.Range(0, structDecl.GenericParameters.Count).Select(i => $"_metadata{i}")
                .Concat(Enumerable.Range(0, pwtCount).Select(i => $"_pwt{i}")));

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Collection subscript @_cdecl wrapper for {{moduleQualifiedName}}.subscript(_:) -> {{elementSwiftType}}
            // Routes through generic static dispatch so CallConvCdecl callers can read Element
            // witnesses without tripping the Mono CallConvSwift pathology on 2+ metadata args.
            """);

        WrapperEmitterHelpers.EmitCdeclAnnotation(
            swiftWriter, cdeclSymbolName, needsMainActor: false,
            availabilityAnnotations: WrapperEmitterHelpers.MergeAvailability(null, structDecl));

        swiftWriter.WriteLine($"public func {swiftFuncName}({string.Join(", ", cdeclParams)}) {{");
        swiftWriter.Indent++;
        swiftWriter.WriteLine($"let parentMeta = {metaHelperName}({metaArgs})");
        swiftWriter.WriteLine($"let metatype = unsafeBitCast(parentMeta, to: Any.Type.self) as! any {protocolName}.Type");
        swiftWriter.WriteLine($"metatype.{dispatchMethodName}(resultPtr: resultPtr, position: position, selfPtr: self_)");
        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    private static void EmitWitnessIndexerBody(
        CSharpWriter csWriter,
        StructDecl structDecl,
        string elementCsName,
        PInvokeHelperContext pinvokeHelperContext,
        string pinvokeMethodName)
    {
        // Mirror PropertyHandler's element-returning getter: stackalloc an Element-sized
        // buffer, hand it to the @_cdecl wrapper, then MarshalFromSwift to build the
        // C# value. Single-generic-param structs only (TryFindBacking enforces that).
        var tparam = structDecl.GenericParameters[0];
        var csGenericName = NameProvider.GetCSharpGenericParameterName(tparam, 0);

        // Resolvable PWTs — PInvokeHelperContext.PwtEntries is already filtered to PAT/Self-free
        // protocols and sorted in the order Swift's metadata accessor expects.
        var pwtLocals = new List<string>();
        var pinvokeArgs = new List<string>
        {
            "(IntPtr)__resultPtr",
            "(nint)index",
            $"{csGenericName}Metadata.Handle",
        };

        for (int i = 0; i < pinvokeHelperContext.PwtEntries.Count; i++)
        {
            var entry = pinvokeHelperContext.PwtEntries[i];
            if (!entry.IsResolvable || string.IsNullOrEmpty(entry.ResolvableInterfaceName))
            {
                // Defensive — HasIndeterminatePwtShape should have tripped already.
                return;
            }
            var localName = $"__pwt{i}";
            pwtLocals.Add(
                $"var {localName} = ProtocolWitnessTable.GetOrThrow<{entry.GenericParamCsName}, {entry.ResolvableInterfaceName}>();");
            pinvokeArgs.Add($"{localName}.Handle");
        }
        pinvokeArgs.Add("_payload.DangerousGetHandle()");

        csWriter.WriteLine("/// <summary>Element access — projection of Swift <c>subscript(_:)</c> via Collection witness.</summary>");
        csWriter.WriteLine($"public {elementCsName} this[int index]");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("get");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine($"var {csGenericName}Metadata = TypeMetadata.GetTypeMetadataOrThrow<{csGenericName}>();");
        foreach (var local in pwtLocals)
            csWriter.WriteLine(local);
        csWriter.WriteLine("var __success = false;");
        csWriter.WriteLine("_payload.DangerousAddRef(ref __success);");
        csWriter.WriteLine("try");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine($"var __size = checked((int){csGenericName}Metadata.Size);");
        csWriter.WriteLine("unsafe");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine("global::System.Span<byte> __buf = __size <= 256");
        csWriter.Indent++;
        csWriter.WriteLine("? stackalloc byte[__size]");
        csWriter.WriteLine(": new byte[__size];");
        csWriter.Indent--;
        csWriter.WriteLine("fixed (byte* __resultPtr = __buf)");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        csWriter.WriteLine($"{pinvokeHelperContext.HelperClassName}.{pinvokeMethodName}({string.Join(", ", pinvokeArgs)});");
        csWriter.WriteLine($"return SwiftMarshal.MarshalFromSwift<{elementCsName}>(new IntPtr(__resultPtr));");
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
        // Require public `startIndex: Int` and `endIndex: Int` on the raw ABI — these are the
        // properties PropertyWrapperEmitter's Collection-family relaxation emits as C# `int`
        // getters (StartIndex / EndIndex). We reference those names directly in Count.
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
        foreach (var c in structDecl.Conformances)
        {
            if (s_collectionProtocols.Contains(c.Protocol.ModuleQualifiedName))
                return true;
            if (c.Protocol.Name is "Collection" or "RandomAccessCollection"
                or "BidirectionalCollection" or "Sequence")
                return true;
        }
        return false;
    }
}
