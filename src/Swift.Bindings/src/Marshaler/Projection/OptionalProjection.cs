// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for Swift.Optional&lt;T&gt; ↔ C# T?.
/// Composes with an inner projection for the wrapped type.
///
/// Parameter direction: null-check branching → SwiftOptional.NewSome/NewNone + PayloadBuffer.
/// Return direction: MarshalFromSwift + ToNullable (or discriminant check for existentials/containers).
/// </summary>
public class OptionalProjection : ITypeProjection
{
    private readonly ITypeProjection _innerProjection;
    private readonly bool _isExistentialInner;
    private readonly bool _useDangerousGetHandle;
    private readonly string? _carrierTypeName;

    /// <summary>
    /// Creates an optional projection.
    /// </summary>
    /// <param name="innerProjection">The projection for the wrapped type.</param>
    /// <param name="isExistentialInner">Whether the inner type is an existential (uses discriminant check instead of ToNullable).</param>
    /// <param name="useDangerousGetHandle">When true, uses DangerousGetHandle() instead of PayloadBuffer for large Optional params passed to Swift wrappers.</param>
    /// <param name="carrierTypeName">
    /// When set, the unmanaged carrier struct this Optional travels in on a direct CallConvSwift
    /// P/Invoke, replacing the default single-word <c>PayloadBuffer&lt;IntPtr&gt;</c> load. Only
    /// meaningful for Optionals wider than one machine word with no Swift-side wrapper; mutually
    /// exclusive with <paramref name="useDangerousGetHandle"/>, which addresses the same problem
    /// by passing a pointer to Swift code that dereferences it.
    /// </param>
    public OptionalProjection(
        ITypeProjection innerProjection,
        bool isExistentialInner = false,
        bool useDangerousGetHandle = false,
        string? carrierTypeName = null)
    {
        _innerProjection = innerProjection;
        _isExistentialInner = isExistentialInner;
        _useDangerousGetHandle = useDangerousGetHandle;
        _carrierTypeName = carrierTypeName;
    }

    /// <summary>The inner projection for the wrapped type.</summary>
    public ITypeProjection InnerProjection => _innerProjection;

    /// <summary>Whether the inner type is an existential.</summary>
    public bool IsExistentialInner => _isExistentialInner;

    public string PublicType => $"{_innerProjection.PublicType}?";
    public string PInvokeType => "IntPtr";
    public string? PInvokeAttribute => null;

    /// <summary>
    /// The container type name for this Optional — used in MarshalFromSwift calls (return direction).
    /// Uses MarshalFromSwiftType for the inner type so that non-frozen structs/classes use their
    /// public type name (e.g., SwiftOptional&lt;AssetType&gt;) instead of IntPtr.
    /// </summary>
    public string ContainerTypeName => $"SwiftOptional<{_innerProjection.MarshalFromSwiftType}>";

    /// <summary>
    /// For MarshalFromSwift deserialization, use the inner type's MarshalFromSwiftType.
    /// This ensures classes/non-frozen structs use their public name (e.g., SwiftOptional&lt;URLRequest&gt;)
    /// rather than IntPtr (which SwiftContainerGenericType would produce).
    /// </summary>
    public string MarshalFromSwiftType => ContainerTypeName;

    /// <summary>
    /// When this Optional appears as a generic parameter inside another container,
    /// use the full SwiftOptional type name with the inner's metadata-bearing wrapper type.
    /// Nullable-pointer-ABI inner types (classes, ObjC-bridged, ObjC-rooted) use
    /// bare IntPtr because Swift's Optional&lt;ClassRef&gt; is nil-pointer-optimized:
    /// the container element is an 8-byte pointer (0 = nil), not a SwiftOptional wrapper.
    /// The inner generic is MarshalFromSwiftType (the wrapper type), matching ContainerTypeName:
    /// the SwiftOptional element carries the inner value by metadata. MarshalFromSwiftType and
    /// SwiftContainerGenericType coincide for every inner that reaches this branch EXCEPT
    /// FrozenWithMemoryProjection, whose SwiftContainerGenericType is the by-value `.Buffer` struct —
    /// nonexistent for a handle-backed wrapper such as SwiftClosedRange&lt;T&gt;.
    /// </summary>
    public string SwiftContainerGenericType =>
        _innerProjection is ClassProjection or KeyPathProjection or ObjCBridgedProjection or ObjCBridgeableProjection or ObjCRootedClassProjection
            ? "IntPtr"
            : $"SwiftOptional<{_innerProjection.MarshalFromSwiftType}>";

    /// <summary>
    /// Per-element conversion for when Optional is used as an array/dictionary element.
    /// Needed because OptionalProjection has a different public type (T?) than the element's
    /// P/Invoke representation, and ArrayProjection uses this in a .Select() conversion lambda.
    /// Two layouts:
    ///  - Nil-pointer optimized (class refs, ObjC types): bare IntPtr, 0 = nil → 8 bytes.
    ///  - Tagged Optional (everything else): SwiftOptional&lt;inner&gt;.NewSome/NewNone → 9 bytes.
    /// </summary>
    public string? GetParameterElementConversion(string elementVar)
    {
        // Derive a unique pattern variable name from elementVar so that two optional element
        // conversions in the same C# expression (e.g., dictionary with optional key and
        // optional value) don't both declare `__v`, which would trigger CS0128.
        var safeVar = elementVar.Replace(".", "_").Replace("[", "").Replace("]", "");
        var patVar = $"__v_{safeVar}";

        if (_innerProjection is ClassProjection)
            return $"({elementVar} is {{ }} {patVar} ? {patVar}.Payload.DangerousGetHandle() : IntPtr.Zero)";
        // KeyPath wrappers ARE the SafeHandle (no .Payload hop); nil-pointer-optimized like classes.
        if (_innerProjection is KeyPathProjection)
            return $"({elementVar} is {{ }} {patVar} ? {patVar}.DangerousGetHandle() : IntPtr.Zero)";
        if (_innerProjection is ObjCBridgedProjection or ObjCBridgeableProjection or ObjCRootedClassProjection)
            return $"({elementVar} is {{ }} {patVar} ? {patVar}.Handle : IntPtr.Zero)";

        // Tagged Optional path: build a SwiftOptional<inner> wrapper per element.
        // When the inner's SwiftContainerGenericType matches its PublicType (e.g. NonFrozenStruct),
        // SwiftOptional<TStruct>.NewSome takes the typed wrapper directly so ISwiftObject.MarshalToSwift
        // copies the struct's payload bytes by value. Applying the per-element conversion here
        // (e.g. .Payload.DangerousGetHandle()) would yield SwiftOptional<TStruct>.NewSome(IntPtr),
        // which is a type mismatch.
        //
        // The generic is the inner's MarshalFromSwiftType (the metadata-bearing wrapper type), which
        // matches the SwiftArray<SwiftOptional<...>> element generic emitted by SwiftContainerGenericType
        // above. The two coincide for every inner reaching here EXCEPT FrozenWithMemoryProjection, whose
        // SwiftContainerGenericType is the by-value `.Buffer` struct — nonexistent for a handle-backed
        // wrapper such as SwiftClosedRange<T>.
        // SwiftOptional has owned element semantics — its value-witness destroy runs on the .Some
        // payload at teardown. An existential inner must therefore ride the owned (+1) carrier
        // (matching carrier type so the slot stride agrees), not the bare borrowed leaf which would
        // over-release the proxy's sole +1. No-op for every non-existential inner. Mirrors Array/Set.
        var optType = $"SwiftOptional<{ExistentialElementCarrier.CarrierType(_innerProjection, _innerProjection.MarshalFromSwiftType)}>";
        var innerConv = ExistentialElementCarrier.ParamConversion(_innerProjection, patVar);
        var skipInnerConv = innerConv != null
            && _innerProjection.SwiftContainerGenericType == _innerProjection.PublicType;
        var someArg = (skipInnerConv ? null : innerConv) ?? patVar;
        return $"({elementVar} is {{ }} {patVar} ? {optType}.NewSome({someArg}) : {optType}.NewNone())";
    }

    /// <summary>
    /// The tagged-optional element conversion path allocates SwiftOptional&lt;T&gt; wrappers per
    /// element (via NewSome/NewNone). Those wrappers own native buffers and must be disposed
    /// in the container's finally block. The nil-pointer-optimized paths (classes, ObjC types)
    /// don't allocate anything, so no disposal is needed.
    /// </summary>
    public bool ElementRequiresDisposal =>
        _innerProjection is not (ClassProjection
            or KeyPathProjection
            or ObjCBridgedProjection
            or ObjCBridgeableProjection
            or ObjCRootedClassProjection);

    /// <summary>
    /// The SwiftOptional type parameter — uses SwiftContainerGenericType which returns the correct
    /// C# type for use as a generic parameter in Swift containers (enum name for enums,
    /// SwiftArray&lt;T&gt; for arrays, etc.)
    /// </summary>
    // Existential inner rides the owned carrier element type (stride-correct); every other inner
    // keeps its SwiftContainerGenericType (preserves FrozenWithMemory's by-value `.Buffer` generic).
    private string OptionalTypeParam => ExistentialElementCarrier.CarrierType(_innerProjection, _innerProjection.SwiftContainerGenericType);

    public MarshalPlan GetParameterPlan(string paramName)
    {
        // @objc protocol existential inner: a single nullable ObjC object pointer (nil = IntPtr.Zero),
        // identical wire to a class reference. The @_cdecl wrapper signature is
        // `(_ p: UnsafeMutableRawPointer?, ...)`, so the object handle (or IntPtr.Zero) is passed
        // directly — no 40-byte opaque container, no SwiftOptional wrapper. The non-nil handle
        // extraction (and the fail-closed guard for an unsupported managed-conformer value) is
        // delegated to the inner ExistentialProjection so the two @objc-existential parameter paths
        // cannot drift.
        if (_innerProjection is ExistentialProjection { IsObjCExistential: true } objcInner)
        {
            return new MarshalPlan
            {
                SetupStatements = new List<MarshalStatement>
                {
                    new MarshalStatement.Line(
                        $"IntPtr {paramName}Buffer = {paramName} is {{ }} {paramName}Val ? {objcInner.GetObjCParameterExpression($"{paramName}Val")} : IntPtr.Zero;")
                },
                PInvokeExpression = $"{paramName}Buffer"
            };
        }

        // ObjC bridged, ObjC-bridgeable, and ObjC-rooted types use nullable pointer ABI — no SwiftOptional wrapper needed.
        if (_innerProjection is ObjCBridgedProjection or ObjCBridgeableProjection or ObjCRootedClassProjection)
        {
            return new MarshalPlan
            {
                SetupStatements = new List<MarshalStatement>
                {
                    new MarshalStatement.Line(
                        $"IntPtr {paramName}Buffer = {paramName} is {{ }} {paramName}Val ? {paramName}Val.Handle : IntPtr.Zero;")
                },
                PInvokeExpression = $"{paramName}Buffer"
            };
        }

        // ObjC bridge containers (Array/Dict/Set with ObjC-bridgeable elements) use nullable pointer ABI.
        // The container is bridged to an ObjC collection (NSArray/NSDictionary/NSSet) on the Swift side.
        if (_innerProjection.UsesObjCContainerBridge)
        {
            var innerPlan = _innerProjection.GetParameterPlan($"{paramName}Val");
            var bridgeSetup = new List<MarshalStatement>();
            var someBody = new List<MarshalStatement>();
            someBody.AddRange(innerPlan.SetupStatements);
            someBody.Add(new MarshalStatement.Line(
                $"{paramName}Buffer = {innerPlan.PInvokeExpression};"));

            bridgeSetup.Add(new MarshalStatement.Line($"IntPtr {paramName}Buffer = IntPtr.Zero;"));
            bridgeSetup.Add(new MarshalStatement.Block(
                $"if ({paramName} is {{ }} {paramName}Val)", someBody));

            return new MarshalPlan
            {
                SetupStatements = bridgeSetup,
                PInvokeExpression = $"{paramName}Buffer"
            };
        }

        // Swift class types use nullable pointer ABI as DIRECT params: the @_cdecl
        // wrapper signature is `(_ p: UnsafeMutableRawPointer?, ...)` so the nullable
        // pointer is read directly from the parameter slot (no extra indirection).
        // C# passes either the class payload pointer or IntPtr.Zero verbatim.
        if (_innerProjection is ClassProjection)
        {
            return new MarshalPlan
            {
                SetupStatements = new List<MarshalStatement>
                {
                    new MarshalStatement.Line(
                        $"IntPtr {paramName}Buffer = {paramName} is {{ }} {paramName}Val ? {paramName}Val.Payload.DangerousGetHandle() : IntPtr.Zero;")
                },
                PInvokeExpression = $"{paramName}Buffer"
            };
        }

        // KeyPath wrappers ARE the SafeHandle (no .Payload). Otherwise identical to the
        // ClassProjection nullable-pointer ABI path above.
        if (_innerProjection is KeyPathProjection)
        {
            return new MarshalPlan
            {
                SetupStatements = new List<MarshalStatement>
                {
                    new MarshalStatement.Line(
                        $"IntPtr {paramName}Buffer = {paramName} is {{ }} {paramName}Val ? {paramName}Val.DangerousGetHandle() : IntPtr.Zero;")
                },
                PInvokeExpression = $"{paramName}Buffer"
            };
        }

        // Non-frozen struct DIRECT-param Optional: the @_cdecl wrapper signature is
        // `(_ p: UnsafeRawPointer, ...)` and the wrapper reads
        // `p.assumingMemoryBound(to: UnsafeMutableRawPointer?.self).pointee`. That is,
        // `p` is a pointer to an 8-byte buffer containing `Optional<UnsafeMutableRawPointer>`
        // (the resilient ABI of `Optional<NonFrozenStruct>` — a single pointer slot,
        // 0 = nil, otherwise a pointer to the struct's payload bytes).
        //
        // So C# must:
        //   1. compute the inner pointer-or-null (DangerousGetHandle / IntPtr.Zero),
        //   2. write it into a stack-local IntPtr,
        //   3. pass &stackLocal as the buffer pointer.
        //
        // Passing the inner pointer directly (the ClassProjection shape) would make
        // Swift read the first 8 bytes of the *struct's payload* as the pointer-or-null,
        // which is garbage. Using SwiftOptional<TStruct> (the fall-through path with
        // SwiftContainerGenericType = TStruct) packs sizeof(T)+1 bytes of struct-by-value
        // payload — also wrong shape.
        //
        // NOTE: this is the *direct-param* shape only. When NonFrozenStruct is the inner
        // type of a *container element* (e.g. `[T?]`), the per-element ABI IS struct-by-value
        // (sizeof(T)+1 tag bytes); that path is handled by GetParameterElementConversion
        // above, which builds SwiftOptional&lt;TStruct&gt;.NewSome(value).
        if (_innerProjection is NonFrozenStructProjection)
        {
            return new MarshalPlan
            {
                SetupStatements = new List<MarshalStatement>
                {
                    new MarshalStatement.Line(
                        $"IntPtr {paramName}Pointee = {paramName} is {{ }} {paramName}Val ? {paramName}Val.Payload.DangerousGetHandle() : IntPtr.Zero;"),
                    new MarshalStatement.Line(
                        $"IntPtr {paramName}Buffer = (IntPtr)(&{paramName}Pointee);")
                },
                PInvokeExpression = $"{paramName}Buffer",
                RequiresUnsafe = true
            };
        }

        // Existential inner rides the owned (+1) carrier so the SwiftOptional store + VWT destroy
        // balance an independent retain rather than over-releasing the proxy's sole +1; no-op otherwise.
        var innerParamConv = ExistentialElementCarrier.ParamConversion(_innerProjection, $"{paramName}Value");
        var containerPlan = _innerProjection.GetContainerCreationPlan($"{paramName}Value");

        // The SwiftOptional<T> generic parameter. The container and element-conversion branches
        // below feed NewSome a P/Invoke-shaped value (a built SwiftArray/SwiftDictionary, or an
        // element conversion), so they need SwiftContainerGenericType. The passthrough branch
        // (and the simple-inner inline path) hand NewSome the wrapper value {paramName}Value
        // directly, which is typed as the inner's public type — so the generic must be the inner's
        // MarshalFromSwiftType (the metadata-bearing wrapper type), matching the return direction's
        // returnTypeParam below. These coincide for every projection EXCEPT
        // FrozenWithMemoryProjection, whose SwiftContainerGenericType is the by-value `.Buffer`
        // struct: correct for a genuine ClassWithBufferStruct, but nonexistent for a handle-backed
        // wrapper such as SwiftClosedRange<T>, where SwiftOptional<SwiftClosedRange<T>> round-trips
        // the value through Swift metadata instead.
        var optTypeParam = (containerPlan == null && innerParamConv == null)
            ? _innerProjection.MarshalFromSwiftType
            : OptionalTypeParam;

        var needsComplexInner = innerParamConv != null || containerPlan != null ||
            _innerProjection.PublicType != _innerProjection.PInvokeType;
        var setup = new List<MarshalStatement>();

        if (needsComplexInner)
        {
            // Complex inner type — multi-statement branching
            setup.Add(new MarshalStatement.Line(
                $"SwiftOptional<{optTypeParam}> {paramName}SwiftInner;"));

            var someBody = new List<MarshalStatement>();
            if (containerPlan != null)
            {
                // Inner is a container (Array, Dictionary) — use container creation plan
                // which creates the SwiftArray/SwiftDictionary without PayloadBuffer extraction
                someBody.AddRange(containerPlan.SetupStatements);
                someBody.Add(new MarshalStatement.Line(
                    $"{paramName}SwiftInner = SwiftOptional<{optTypeParam}>.NewSome({containerPlan.PInvokeExpression});"));
            }
            else if (innerParamConv != null)
            {
                // Inner has element conversion (string, enum, etc.) — use it directly
                someBody.Add(new MarshalStatement.Line(
                    $"{paramName}SwiftInner = SwiftOptional<{optTypeParam}>.NewSome({innerParamConv});"));
            }
            else
            {
                // Inner has different public/pinvoke types but no conversion/container — passthrough
                someBody.Add(new MarshalStatement.Line(
                    $"{paramName}SwiftInner = SwiftOptional<{optTypeParam}>.NewSome({paramName}Value);"));
            }
            setup.Add(new MarshalStatement.Block(
                $"if ({paramName} is {{ }} {paramName}Value)", someBody));

            var noneBody = new List<MarshalStatement>
            {
                new MarshalStatement.Line(
                    $"{paramName}SwiftInner = SwiftOptional<{optTypeParam}>.NewNone();")
            };
            setup.Add(new MarshalStatement.Block("else", noneBody));

            setup.Add(new MarshalStatement.Using(
                $"SwiftOptional<{optTypeParam}>", $"{paramName}Swift", $"{paramName}SwiftInner"));
        }
        else
        {
            // Simple inner type — inline ternary
            setup.Add(new MarshalStatement.Using(
                $"SwiftOptional<{optTypeParam}>", $"{paramName}Swift",
                $"{paramName} is {{ }} {paramName}Value ? SwiftOptional<{optTypeParam}>.NewSome({paramName}Value) : SwiftOptional<{optTypeParam}>.NewNone()"));
        }

        if (_useDangerousGetHandle)
        {
            // Large Optional passed to Swift wrapper — pass pointer to full Optional buffer
            setup.Add(new MarshalStatement.Line(
                $"IntPtr {paramName}Buffer = {paramName}Swift.Payload.DangerousGetHandle();"));
        }
        else if (_carrierTypeName is { } carrier)
        {
            // Wider than one word on the direct path: load the whole Optional into its carrier so
            // every register Swift reads is supplied. GetCarrierBuffer throws on a size mismatch
            // rather than silently transferring a prefix of the value.
            setup.Add(new MarshalStatement.Using(
                $"PayloadBuffer<{carrier}>", $"{paramName}Disposable",
                $"{paramName}Swift.GetCarrierBuffer<{carrier}>()"));
            setup.Add(new MarshalStatement.Line(
                $"{carrier} {paramName}Buffer = {paramName}Disposable.Buffer;"));
        }
        else
        {
            setup.Add(new MarshalStatement.Using(
                "PayloadBuffer<IntPtr>", $"{paramName}Disposable", $"{paramName}Swift.PayloadBuffer"));
            setup.Add(new MarshalStatement.Line(
                $"IntPtr {paramName}Buffer = {paramName}Disposable.Buffer;"));
        }

        return new MarshalPlan
        {
            SetupStatements = setup,
            PInvokeExpression = $"{paramName}Buffer"
        };
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        // Use MarshalFromSwiftType for return MarshalFromSwift calls — for classes/non-frozen structs,
        // this is the actual type name (not IntPtr), which MarshalFromSwift needs to construct instances.
        var returnTypeParam = _innerProjection.MarshalFromSwiftType;

        // ObjC bridged, ObjC-bridgeable, and ObjC-rooted types use nullable pointer ABI (nil = IntPtr.Zero, Some = ObjC pointer).
        // Bypass SwiftOptional entirely — the IntPtr result IS the payload.
        // Owned return: the Swift @_cdecl wrapper hands back the Some pointer at +1
        // (`Unmanaged.passRetained($0 as AnyObject).toOpaque()`). ownsReference:true adopts that +1
        // (GetINativeObject owns:true) so the wrapper releases exactly once on Dispose/finalize —
        // a bare GetNSObject (owns:false) would add a SECOND retain that nothing balances, leaking
        // one object per call. This matches the non-optional sibling (WrapperEmitter.Return.cs:
        // GetNSObject + DangerousRelease, net +0) and the accessor getter path
        // (OptionalAccessorGetterVisitor: same ownsReference:true).
        if (_innerProjection is ObjCBridgedProjection or ObjCBridgeableProjection or ObjCRootedClassProjection)
        {
            var innerPublicType = _innerProjection.PublicType;
            var bridgeCall = MarshallingHelpers.FormatObjCBridgeCall(innerPublicType, resultName, ownsReference: true);
            var indirectBridgeCall = MarshallingHelpers.FormatObjCBridgeCall(innerPublicType, $"*(IntPtr*){resultName}", ownsReference: true);
            return strategy switch
            {
                ReturnStrategy.Direct => new MarshalPlan
                {
                    PInvokeExpression = $"({resultName} == IntPtr.Zero ? null : {bridgeCall})"
                },
                ReturnStrategy.IndirectResult or ReturnStrategy.OutBuffer => new MarshalPlan
                {
                    PInvokeExpression = $"(*(IntPtr*){resultName} == IntPtr.Zero ? null : {indirectBridgeCall})",
                    RequiresUnsafe = true
                },
                _ => MarshalPlan.PassThrough(resultName)
            };
        }

        // ObjC bridge containers: nullable pointer ABI. nil = IntPtr.Zero, Some = ObjC collection handle.
        if (_innerProjection.UsesObjCContainerBridge)
        {
            var bridgeContainerConv = _innerProjection.GetReturnContainerConversion(resultName);
            var innerPublicType = _innerProjection.PublicType;
            var convExpr = bridgeContainerConv ?? $"({innerPublicType}){resultName}";
            return strategy switch
            {
                ReturnStrategy.Direct => new MarshalPlan
                {
                    PInvokeExpression = $"({resultName} == IntPtr.Zero ? ({innerPublicType}?)null : {convExpr})"
                },
                ReturnStrategy.IndirectResult or ReturnStrategy.OutBuffer => new MarshalPlan
                {
                    PInvokeExpression = $"(*(IntPtr*){resultName} == IntPtr.Zero ? ({innerPublicType}?)null : " +
                        $"{(_innerProjection.GetReturnContainerConversion($"*(IntPtr*){resultName}") ?? $"({innerPublicType})*(IntPtr*){resultName}")})",
                    RequiresUnsafe = true
                },
                _ => MarshalPlan.PassThrough(resultName)
            };
        }

        if (_isExistentialInner)
        {
            // @objc protocol existential inner: the optional is a single nullable ObjC object pointer
            // (nil = IntPtr.Zero), returned BY VALUE — not the 40-byte opaque container via sret.
            // The @_cdecl wrapper returns `UnsafeMutableRawPointer?` (the +1-owned object or nil), so
            // the IntPtr result IS the payload: nil → null, otherwise construct the proxy over Payload0
            // and adopt the +1 (ownsContainer: true), mirroring the non-optional @objc scalar return.
            if (_innerProjection is ExistentialProjection { IsObjCExistential: true } objcInner)
            {
                return strategy switch
                {
                    ReturnStrategy.Direct => new MarshalPlan
                    {
                        PInvokeExpression =
                            $"({resultName} == IntPtr.Zero ? null : {objcInner.GetObjCReturnExpression(resultName)})"
                    },
                    ReturnStrategy.IndirectResult or ReturnStrategy.OutBuffer => new MarshalPlan
                    {
                        PInvokeExpression =
                            $"(*(IntPtr*){resultName} == IntPtr.Zero ? null : {objcInner.GetObjCReturnExpression($"*(IntPtr*){resultName}")})",
                        RequiresUnsafe = true
                    },
                    _ => MarshalPlan.PassThrough(resultName)
                };
            }

            // Existential inner — discriminant check + proxy construction.
            // Owned return: Swift transfers the inner existential at +1 via the sret/out buffer
            // (raw-freed after the read), so the adopting proxy must release it on Dispose/finalize
            // or the payload's +1 leaks. Request the owned element conversion (ownsContainer: true)
            // explicitly — the shared GetReturnElementConversion stays non-owning because it is also
            // reused for borrowed Swift->C# receiver-callback parameter wraps.
            var elemConversion = _innerProjection is ExistentialProjection existInner
                ? existInner.GetOwnedReturnElementConversion("swiftResult.Some")
                : _innerProjection.GetReturnElementConversion("swiftResult.Some");
            var convExpr = elemConversion ?? "swiftResult.Some";

            // Optional<any Error> direct-IntPtr return: `any Error` is class-bound (single
            // boxed pointer, MemoryLayout = 8) so Swift returns Optional<(any Error)> directly
            // in x0 with nil = IntPtr.Zero — no sret. Construct AnyError over Payload0 only;
            // sbw_anyErrorGetDescription loads `(any Error).self` (8 bytes) from the container,
            // so the remaining EC1 slots are unread and may stay zero.
            //
            // Gated to AnyError specifically — every other `any P` is a 5-word existential
            // container (40 bytes) returned via sret, which falls through to the indirect-result
            // bypass below.
            if (strategy == ReturnStrategy.Direct &&
                _innerProjection.PublicType == "Swift.Foundation.AnyError")
            {
                return new MarshalPlan
                {
                    // Owned return: Swift returns the boxed error in x0 at +1, so the AnyError adopts
                    // it and releases it on Dispose/finalize (ownsContainer: true) or the box leaks.
                    PInvokeExpression =
                        $"({resultName} == IntPtr.Zero ? (Swift.Foundation.AnyError?)null : " +
                        $"new Swift.Foundation.AnyError(new Swift.Runtime.ExistentialContainer1 {{ Payload0 = {resultName} }}, ownsContainer: true))"
                };
            }

            // Indirect-result existential bypass: SwiftOptional<ExistentialContainerN>.Case
            // resolves the discriminant via VWT.GetEnumTag, which is known-broken on Mono iOS
            // Simulator for Optional<any P> (the inner SwiftOptional.cs blittable/simple-enum
            // bypasses don't cover existentials). Read the existential's metadata pointer
            // directly: ExistentialContainerN places `_metadata` at offset 3 × IntPtr.Size on
            // every variant (0 protocols .. 8 protocols). Swift encodes None as
            // `metadata = nullptr` via the metadata pointer's extra-inhabitant slot, so a
            // pointer comparison against IntPtr.Zero is the canonical None check. Some =
            // dereference the full container and feed it to the projection's element
            // conversion (e.g. `new AnyError(...)` or proxy-class construction).
            if (strategy is ReturnStrategy.IndirectResult or ReturnStrategy.OutBuffer)
            {
                // A class-bound single-protocol Optional<any P> is a compact 2-word
                // [classRef][witnessTable] heap cell (16 bytes), not the 5-word opaque container
                // (40 bytes). Its None is the null classRef extra-inhabitant at offset 0, NOT the
                // opaque container's _metadata slot at offset 3 × IntPtr.Size. The sret buffer is
                // zero-filled and Swift writes only the 2 words, so the opaque offset-24 check
                // always reads zero and would mis-report a present value as None; the 5-word read
                // would also pull uninitialized bytes into the unused container fields. Read the
                // cell at its true width via ReadHeapCell (widened to ExistentialContainer1 for the
                // proxy's owned ctor) and key None off offset 0. Ownership is unchanged: the +1
                // transfers through the bitwise read and the proxy adopts it (ownsContainer: true),
                // mirroring the non-optional class-bound sret return.
                if (_innerProjection is ExistentialProjection { IsClassBoundArity1: true })
                {
                    var classBoundRead = $"Swift.Runtime.ClassExistentialContainer1.ReadHeapCell({resultName})";
                    var classBoundConv = elemConversion?.Replace("swiftResult.Some", classBoundRead)
                                      ?? classBoundRead;
                    return new MarshalPlan
                    {
                        PInvokeExpression =
                            $"(*(IntPtr*)(byte*){resultName} == IntPtr.Zero ? null : {classBoundConv})",
                        RequiresUnsafe = true
                    };
                }

                var directInnerExpr = $"*({returnTypeParam}*){resultName}";
                var directConv = elemConversion?.Replace("swiftResult.Some", directInnerExpr)
                              ?? directInnerExpr;
                return new MarshalPlan
                {
                    PInvokeExpression =
                        $"(*(IntPtr*)((byte*){resultName} + (3 * IntPtr.Size)) == IntPtr.Zero ? null : {directConv})",
                    RequiresUnsafe = true
                };
            }

            return BuildDiscriminantReturnPlan(resultName, strategy, returnTypeParam, convExpr);
        }

        // Container inner (Array, Dictionary) — discriminant check + container conversion
        var containerConv = _innerProjection.GetReturnContainerConversion("swiftResult.Some");
        if (containerConv != null)
        {
            return BuildDiscriminantReturnPlan(resultName, strategy, returnTypeParam, containerConv);
        }

        // Blittable primitive inner types: read the discriminator byte directly from the buffer
        // instead of going through SwiftOptional<T> and VWT GetEnumTag. This avoids a known issue
        // where VWT GetEnumTag returns incorrect values for Optional<Int32> on some runtimes.
        // Layout: [sizeof(T) bytes payload][1 byte discriminator: 0=Some, 1=None]
        var blittableSize = GetBlittablePrimitiveSize(_innerProjection);
        if (blittableSize != null && strategy is ReturnStrategy.IndirectResult or ReturnStrategy.OutBuffer)
        {
            var innerType = _innerProjection.PublicType;
            var innerRetConvBlittable = _innerProjection.GetReturnElementConversion("_optVal");
            // Direct byte read: check discriminator at offset sizeof(T)
            var tagExpr = $"((byte*){resultName})[{blittableSize.Value}]";
            var valueExpr = $"*({innerType}*){resultName}";
            if (innerRetConvBlittable != null)
            {
                return new MarshalPlan
                {
                    SetupStatements = new List<MarshalStatement>
                    {
                        new MarshalStatement.Line(
                            $"var _optVal = {tagExpr} == 0 ? ({innerType}?){valueExpr} : default;")
                    },
                    PInvokeExpression = $"_optVal is {{ }} rawVal ? {innerRetConvBlittable} : default",
                    RequiresUnsafe = true
                };
            }
            return new MarshalPlan
            {
                PInvokeExpression = $"({tagExpr} == 0 ? ({innerType}?){valueExpr} : default)",
                RequiresUnsafe = true
            };
        }

        // Frozen blittable struct inner types (CGPoint, CGSize, CGRect, etc.): read the tag byte
        // at TypeMetadata.Size offset instead of going through SwiftOptional<T>.ToNullable() which
        // relies on VWT GetEnumTag — known to return incorrect values for frozen structs on Mono.
        // Uses TypeMetadata.Size (the runtime's source of truth for Swift type size) rather than
        // Unsafe.SizeOf<T>() which includes C# trailing padding and can disagree for padded structs.
        // Excludes known primitives (which use the compile-time fast path above).
        // Layout: [TypeMetadata.Size bytes payload][1 byte discriminator: 0=Some, 1=None]
        if (_innerProjection is BlittableProjection blitProj && blittableSize == null &&
            !IsKnownPrimitiveTypeName(_innerProjection.PublicType) &&
            !blitProj.IsGenericParameter &&
            strategy is ReturnStrategy.IndirectResult or ReturnStrategy.OutBuffer)
        {
            var innerType = _innerProjection.PublicType;
            var tagExpr = $"((byte*){resultName})[(int)TypeMetadata.GetTypeMetadataOrThrow<{innerType}>().Size]";
            var valueExpr = $"Unsafe.ReadUnaligned<{innerType}>(ref *(byte*){resultName})";
            return new MarshalPlan
            {
                PInvokeExpression = $"({tagExpr} == 0 ? ({innerType}?){valueExpr} : default)",
                RequiresUnsafe = true
            };
        }

        // Non-existential, non-container — HasValue/Some check.
        // NOTE: .ToNullable() is broken for value types in unconstrained generic context
        // (T? is T in IL, not Nullable<T>, so default returns zero-value instead of null).
        // Use explicit HasValue/Some check in generated concrete code, where default(T?) IS null.
        var marshalFromSwift = $"SwiftMarshal.MarshalFromSwiftObject<SwiftOptional<{returnTypeParam}>>";
        // Direct (by-value register) return: the owned SwiftOptional temporary carries +1 on any
        // non-POD payload (class ref, frozen-with-ref struct, container). Its from-handle ctor runs
        // VWT InitializeWithCopy for non-POD payloads (a fresh +1 for the wrapper), so the source slot
        // must be value-witness-destroyed afterwards or that +1 leaks. The consuming marshal copies
        // then destroys the source; for POD payloads the witness Destroy is a trivial no-op, so it is
        // safe to use uniformly here.
        var marshalFromSwiftConsuming = $"SwiftMarshal.MarshalFromSwiftObjectConsuming<SwiftOptional<{returnTypeParam}>>";
        var innerRetConv = _innerProjection.GetReturnElementConversion("rawVal");

        // Tuple inner carrying class and/or self-owning ISwiftObject elements: ownership-aware
        // extraction. The generic innerRetConv path below would access _swiftOpt.Some twice
        // (".Some" re-extracts each access → a fresh +1 leaked per element per access). Instead
        // bind .Some ONCE; the carrier's class-aware tuple metadata (TupleProjection.MarshalFromSwiftType)
        // extracts each element as its self-owning wrapper, so class elements pass through (+1 to the
        // caller) and consumed ISwiftObject elements (e.g. SwiftString) are disposed after the public
        // tuple is built. The whole body lives in setup with an empty PInvokeExpression (the
        // ClassProjection pattern) so disposal can run after the public tuple is built.
        if (_innerProjection is TupleProjection ownedTuple && ownedTuple.RequiresOwnedCarrierExtraction)
        {
            var (elemSetup, tupleExpr) = ownedTuple.GetOwnedCarrierReturnConversion("_optTuple");

            List<MarshalStatement> BuildBody(string carrierCtor)
            {
                var body = new List<MarshalStatement>
                {
                    new MarshalStatement.Using($"SwiftOptional<{returnTypeParam}>", "_swiftOpt", carrierCtor),
                    new MarshalStatement.Line("if (!_swiftOpt.HasValue) return null;"),
                    new MarshalStatement.Line("var _optTuple = _swiftOpt.Some;")
                };
                body.AddRange(elemSetup);
                body.Add(new MarshalStatement.Line($"return {tupleExpr};"));
                return body;
            }

            return strategy switch
            {
                ReturnStrategy.Direct => new MarshalPlan
                {
                    SetupStatements = BuildBody($"{marshalFromSwiftConsuming}(&{resultName})"),
                    PInvokeExpression = "",
                    RequiresUnsafe = true
                },
                ReturnStrategy.IndirectResult or ReturnStrategy.OutBuffer or ReturnStrategy.AsyncCallback => new MarshalPlan
                {
                    SetupStatements = BuildBody($"{marshalFromSwift}({resultName})"),
                    PInvokeExpression = ""
                },
                _ => MarshalPlan.PassThrough(resultName)
            };
        }

        if (innerRetConv != null)
        {
            // Element conversion needed: MarshalFromSwift → HasValue check → conditional convert.
            // Use null instead of default to avoid value-type zero-value bug (same as no-conversion path).
            var convExpr = innerRetConv.Replace("rawVal", "_swiftOpt.Some");
            return strategy switch
            {
                ReturnStrategy.Direct => new MarshalPlan
                {
                    SetupStatements = new List<MarshalStatement>
                    {
                        new MarshalStatement.Line(
                            $"using var _swiftOpt = {marshalFromSwiftConsuming}(&{resultName});")
                    },
                    PInvokeExpression = $"_swiftOpt.HasValue ? {convExpr} : null",
                    RequiresUnsafe = true
                },
                ReturnStrategy.IndirectResult or ReturnStrategy.OutBuffer => new MarshalPlan
                {
                    SetupStatements = new List<MarshalStatement>
                    {
                        new MarshalStatement.Line(
                            $"using var _swiftOpt = {marshalFromSwift}({resultName});")
                    },
                    PInvokeExpression = $"_swiftOpt.HasValue ? {convExpr} : null"
                },
                ReturnStrategy.AsyncCallback => new MarshalPlan
                {
                    SetupStatements = new List<MarshalStatement>
                    {
                        new MarshalStatement.Line(
                            $"using var _swiftOpt = {marshalFromSwift}({resultName});")
                    },
                    PInvokeExpression = $"_swiftOpt.HasValue ? {convExpr} : null"
                },
                _ => MarshalPlan.PassThrough(resultName)
            };
        }

        // No element conversion — explicit HasValue/Some check.
        // Cast Some to nullable type so the ternary expression has type T?, not T.
        // Use `default` (inferred from the LHS as default(T?) = null) so the expression
        // also compiles when T is an unconstrained generic parameter — `null` literal
        // is rejected (CS0403) when T could be a non-nullable value type.
        var optInnerType = _innerProjection.PublicType;
        var nullableExpr = $"_swiftOpt.HasValue ? ({optInnerType}?)_swiftOpt.Some : default";
        return strategy switch
        {
            ReturnStrategy.Direct => new MarshalPlan
            {
                SetupStatements = new List<MarshalStatement>
                {
                    new MarshalStatement.Line(
                        $"using var _swiftOpt = {marshalFromSwiftConsuming}(&{resultName});")
                },
                PInvokeExpression = nullableExpr,
                RequiresUnsafe = true
            },
            ReturnStrategy.IndirectResult or ReturnStrategy.OutBuffer => new MarshalPlan
            {
                SetupStatements = new List<MarshalStatement>
                {
                    new MarshalStatement.Line(
                        $"using var _swiftOpt = {marshalFromSwift}({resultName});")
                },
                PInvokeExpression = nullableExpr
            },
            ReturnStrategy.AsyncCallback => new MarshalPlan
            {
                SetupStatements = new List<MarshalStatement>
                {
                    new MarshalStatement.Line(
                        $"using var _swiftOpt = {marshalFromSwift}({resultName});")
                },
                PInvokeExpression = nullableExpr
            },
            _ => MarshalPlan.PassThrough(resultName)
        };
    }

    /// <summary>
    /// Returns the size in bytes for blittable primitive C# types, or null if not a known blittable primitive.
    /// Used for direct discriminator byte reading in Optional return/parameter marshalling.
    /// </summary>
    private static int? GetBlittablePrimitiveSize(ITypeProjection inner)
    {
        return GetBlittablePrimitiveSizePublic(inner);
    }

    /// <summary>
    /// Public accessor for blittable primitive size check.
    /// Used by WrapperEmitter.Return.cs for accessor return fast path.
    /// </summary>
    public static int? GetBlittablePrimitiveSizePublic(ITypeProjection inner)
    {
        if (inner is not BlittableProjection) return null;
        return inner.PublicType switch
        {
            "bool" or "byte" or "sbyte" => 1,
            "short" or "ushort" => 2,
            "int" or "uint" or "float" => 4,
            "long" or "ulong" or "double" => 8,
            "nint" or "nuint" => 8, // 8 bytes on arm64
            _ => null
        };
    }

    /// <summary>
    /// Returns true if the type name is a known C# primitive type (keyword or BCL name).
    /// Used to exclude primitives from the frozen struct Unsafe.SizeOf fast path.
    /// </summary>
    public static bool IsKnownPrimitiveTypeNamePublic(string typeName) => IsKnownPrimitiveTypeName(typeName);

    private static bool IsKnownPrimitiveTypeName(string typeName) => typeName is
        "bool" or "byte" or "sbyte" or "short" or "ushort" or
        "int" or "uint" or "float" or "long" or "ulong" or "double" or
        "nint" or "nuint" or
        "Boolean" or "Byte" or "SByte" or "Int16" or "UInt16" or
        "Int32" or "UInt32" or "Single" or "Int64" or "UInt64" or "Double" or
        "IntPtr" or "UIntPtr";

    /// <summary>
    /// Builds a return plan using discriminant check (Case == None ? null : conversion).
    /// Used for both existential and container inners.
    /// </summary>
    private static MarshalPlan BuildDiscriminantReturnPlan(
        string resultName, ReturnStrategy strategy, string optTypeParam, string convExpr)
    {
        var marshalFromSwift = $"SwiftMarshal.MarshalFromSwiftObject<SwiftOptional<{optTypeParam}>>";
        // Direct (by-value register) return: the owned SwiftOptional temporary copies its payload
        // (existential box or container carrier) via VWT InitializeWithCopy, taking a +1 that the
        // source slot still owns. Value-witness-destroy the source via the consuming marshal or that
        // +1 leaks per call. POD payloads' witness Destroy is a no-op, so this is uniformly safe.
        var marshalFromSwiftConsuming = $"SwiftMarshal.MarshalFromSwiftObjectConsuming<SwiftOptional<{optTypeParam}>>";
        return strategy switch
        {
            ReturnStrategy.Direct => new MarshalPlan
            {
                SetupStatements = new List<MarshalStatement>
                {
                    new MarshalStatement.Line(
                        $"var swiftResult = {marshalFromSwiftConsuming}(&{resultName});")
                },
                PInvokeExpression = $"swiftResult.Case == SwiftOptionalCases.None ? null : {convExpr}",
                RequiresUnsafe = true
            },
            ReturnStrategy.IndirectResult or ReturnStrategy.OutBuffer => new MarshalPlan
            {
                SetupStatements = new List<MarshalStatement>
                {
                    new MarshalStatement.Line(
                        $"var swiftResult = {marshalFromSwift}({resultName});")
                },
                PInvokeExpression = $"swiftResult.Case == SwiftOptionalCases.None ? null : {convExpr}"
            },
            ReturnStrategy.AsyncCallback => MarshalPlan.PassThrough(resultName),
            _ => MarshalPlan.PassThrough(resultName)
        };
    }

    /// <summary>
    /// Element-level conversion for when this Optional appears as a dictionary/array/tuple element.
    /// Converts SwiftOptional&lt;T&gt; → a concrete <c>PublicType?</c> by gating on <c>HasValue</c>
    /// (NOT <c>.ToNullable()</c>, which collapses a value-type inner's None to <c>Some(default)</c>),
    /// applying the inner element conversion to the unwrapped <c>.Some</c> in the HasValue branch only.
    /// </summary>
    public string? GetReturnElementConversion(string elementVar)
    {
        // Both arms below gate on HasValue and build a CONCRETE Nullable<PublicType> rather than
        // routing a genuine None through `ToNullable()`. Inside SwiftOptional<T>, ToNullable()
        // returns the *unconstrained* type parameter's `T?`, which for a value-type inner collapses
        // to `T` in IL (there is no Nullable<T> to carry a None), so a None surfaces as default(T)
        // (0/false, or for a wrapped value type its zero value) and then widens back to Some(default)
        // at the call site. At THIS emission site PublicType is the CONCRETE element type, so
        // `PublicType?` is a real Nullable<T> and the None arm is a true null. `.Some` is read only
        // when HasValue — a single payload read, ARC-identical to ToNullable()'s for a reference
        // inner (each leaf inner conversion references its argument exactly once).
        var innerPublic = _innerProjection.PublicType;
        var innerConv = _innerProjection.GetReturnElementConversion($"{elementVar}.Some");
        if (innerConv != null)
        {
            // Inner needs conversion: e.g. SwiftOptional<Date> → DateTimeOffset?, SwiftOptional<SwiftString>
            // → string?. Apply the inner conversion to the unwrapped `.Some` in the HasValue branch only;
            // the None branch is a real `default(PublicType?)` null. Using `ToNullable() is { } v` here
            // silently mis-reports a value-type inner's None as Some(default) — `is { }` always matches a
            // non-nullable value type — which is the collapse the explicit form above avoids.
            return $"({elementVar}.HasValue ? ({innerPublic}?)({innerConv}) : default({innerPublic}?))";
        }
        // Simple inner: SwiftOptional<T> → T?.
        return $"({elementVar}.HasValue ? ({innerPublic}?){elementVar}.Some : default({innerPublic}?))";
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
