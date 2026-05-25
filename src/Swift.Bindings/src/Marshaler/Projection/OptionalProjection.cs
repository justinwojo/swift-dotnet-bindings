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

    /// <summary>
    /// Creates an optional projection.
    /// </summary>
    /// <param name="innerProjection">The projection for the wrapped type.</param>
    /// <param name="isExistentialInner">Whether the inner type is an existential (uses discriminant check instead of ToNullable).</param>
    /// <param name="useDangerousGetHandle">When true, uses DangerousGetHandle() instead of PayloadBuffer for large Optional params passed to Swift wrappers.</param>
    public OptionalProjection(ITypeProjection innerProjection, bool isExistentialInner = false, bool useDangerousGetHandle = false)
    {
        _innerProjection = innerProjection;
        _isExistentialInner = isExistentialInner;
        _useDangerousGetHandle = useDangerousGetHandle;
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
    /// use the full SwiftOptional type name with P/Invoke-level inner type.
    /// Nullable-pointer-ABI inner types (classes, ObjC-bridged, ObjC-rooted) use
    /// bare IntPtr because Swift's Optional&lt;ClassRef&gt; is nil-pointer-optimized:
    /// the container element is an 8-byte pointer (0 = nil), not a SwiftOptional wrapper.
    /// </summary>
    public string SwiftContainerGenericType =>
        _innerProjection is ClassProjection or ObjCBridgedProjection or ObjCBridgeableProjection or ObjCRootedClassProjection
            ? "IntPtr"
            : $"SwiftOptional<{_innerProjection.SwiftContainerGenericType}>";

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
        if (_innerProjection is ObjCBridgedProjection or ObjCBridgeableProjection or ObjCRootedClassProjection)
            return $"({elementVar} is {{ }} {patVar} ? {patVar}.Handle : IntPtr.Zero)";

        // Tagged Optional path: build a SwiftOptional<inner> wrapper per element.
        // When the inner's SwiftContainerGenericType matches its PublicType (e.g. NonFrozenStruct),
        // SwiftOptional<TStruct>.NewSome takes the typed wrapper directly so ISwiftObject.MarshalToSwift
        // copies the struct's payload bytes by value. Applying the per-element conversion here
        // (e.g. .Payload.DangerousGetHandle()) would yield SwiftOptional<TStruct>.NewSome(IntPtr),
        // which is a type mismatch.
        var optType = $"SwiftOptional<{_innerProjection.SwiftContainerGenericType}>";
        var innerConv = _innerProjection.GetParameterElementConversion(patVar);
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
            or ObjCBridgedProjection
            or ObjCBridgeableProjection
            or ObjCRootedClassProjection);

    /// <summary>
    /// The SwiftOptional type parameter — uses SwiftContainerGenericType which returns the correct
    /// C# type for use as a generic parameter in Swift containers (enum name for enums,
    /// SwiftArray&lt;T&gt; for arrays, etc.)
    /// </summary>
    private string OptionalTypeParam => _innerProjection.SwiftContainerGenericType;

    public MarshalPlan GetParameterPlan(string paramName)
    {
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

        var optTypeParam = OptionalTypeParam;
        var innerParamConv = _innerProjection.GetParameterElementConversion($"{paramName}Value");
        var containerPlan = _innerProjection.GetContainerCreationPlan($"{paramName}Value");
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
        if (_innerProjection is ObjCBridgedProjection or ObjCBridgeableProjection or ObjCRootedClassProjection)
        {
            var innerPublicType = _innerProjection.PublicType;
            var bridgeCall = MarshallingHelpers.FormatObjCBridgeCall(innerPublicType, resultName);
            var indirectBridgeCall = MarshallingHelpers.FormatObjCBridgeCall(innerPublicType, $"*(IntPtr*){resultName}");
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
            // Existential inner — discriminant check + proxy construction
            var elemConversion = _innerProjection.GetReturnElementConversion("swiftResult.Some");
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
                    PInvokeExpression =
                        $"({resultName} == IntPtr.Zero ? (Swift.Foundation.AnyError?)null : " +
                        $"new Swift.Foundation.AnyError(new Swift.Runtime.ExistentialContainer1 {{ Payload0 = {resultName} }}))"
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
    /// Element-level conversion for when this Optional appears as a dictionary/array value.
    /// Converts SwiftOptional&lt;T&gt; → T? via .ToNullable() with optional inner element conversion.
    /// </summary>
    public string? GetReturnElementConversion(string elementVar)
    {
        // Derive a unique inner variable name from elementVar to avoid CS0128 when
        // multiple optional elements appear in the same scope (e.g., tuple elements).
        var safeVar = elementVar.Replace(".", "_").Replace("[", "").Replace("]", "");
        var innerVar = $"_optVal_{safeVar}";
        var innerConv = _innerProjection.GetReturnElementConversion(innerVar);
        if (innerConv != null)
        {
            // Inner needs conversion: e.g., SwiftOptional<SwiftString> → string?
            // ToNullable() gives SwiftString?, then convert the inner value.
            return $"({elementVar}.ToNullable() is {{ }} {innerVar} ? ({_innerProjection.PublicType}?){innerConv} : default)";
        }
        // Simple inner: SwiftOptional<T> → T?
        return $"{elementVar}.ToNullable()";
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
