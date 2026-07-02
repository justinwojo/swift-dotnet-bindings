// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for Swift.Array&lt;T&gt; ↔ C# IReadOnlyList&lt;T&gt; (return) or IEnumerable&lt;T&gt; (parameter).
/// Composes with an inner element projection for element-wise marshalling.
///
/// Parameter direction: FromEnumerable + PayloadBuffer, with optional element conversion + disposal.
/// Return direction: MarshalFromSwift + AsProjected with element conversion lambda.
/// </summary>
public class ArrayProjection : ITypeProjection
{
    private readonly ITypeProjection _elementProjection;
    private readonly bool _isParameter;

    public ArrayProjection(ITypeProjection elementProjection, bool isParameter)
    {
        _elementProjection = elementProjection;
        _isParameter = isParameter;
    }

    /// <summary>The inner element projection for composition.</summary>
    public ITypeProjection ElementProjection => _elementProjection;

    /// <summary>
    /// True when element projection uses ObjC container bridge — the entire array
    /// crosses the @_cdecl boundary as an NSArray pointer instead of SwiftArray&lt;T&gt;.
    /// </summary>
    public bool UsesObjCContainerBridge => _elementProjection.UsesObjCContainerBridge;

    public string PublicType => _isParameter
        ? $"IEnumerable<{_elementProjection.PublicType}>"
        : $"IReadOnlyList<{_elementProjection.PublicType}>";

    public string PInvokeType => "IntPtr";
    public string? PInvokeAttribute => null;

    public string SwiftContainerGenericType => $"SwiftArray<{ParamElementCarrierType}>";

    /// <summary>
    /// The SwiftArray element type for the PARAMETER/WRITE (FromEnumerable) direction. For a
    /// class-bound single-protocol existential element this is the 16-byte
    /// <c>ClassExistentialContainer1</c> — matching the Swift array's actual element stride and the
    /// read-direction <see cref="ContainerTypeName"/> (which already uses ArrayElementCarrierType).
    /// For every other element it is the legacy <c>SwiftContainerGenericType</c>: class / non-frozen
    /// struct elements keep their typed-wrapper or IntPtr param carrier, which ArrayElementCarrierType
    /// would wrongly replace with the read-direction PublicType. For an existential element
    /// ArrayElementCarrierType equals its own SwiftContainerGenericType in the opaque case, so this is
    /// a no-op everywhere except the class-bound existential bug. Composition is automatic: an outer
    /// container reading this property routes <c>[[any P]]</c> / <c>[any P]?</c> through the same
    /// carrier in the parameter direction.
    /// </summary>
    private string ParamElementCarrierType =>
        _elementProjection is ExistentialProjection existElem
            ? existElem.ArrayElementCarrierType
            : _elementProjection.SwiftContainerGenericType;

    /// <summary>
    /// Per-element conversion for the PARAMETER/WRITE direction. Narrows a class-bound existential
    /// element's proxy-produced <c>ExistentialContainer1</c> down to the 16-byte
    /// <c>ClassExistentialContainer1</c> carrier (<see cref="ParamElementCarrierType"/>); a no-op
    /// passthrough for every other element. Pairs with <see cref="ParamElementCarrierType"/> so the
    /// SwiftArray element type and the expression filling it always agree on stride.
    /// </summary>
    private string? ParamElementConversion(string elementVar) =>
        _elementProjection is ExistentialProjection existElem
            ? existElem.GetArrayElementCarrierConversion(elementVar)
            : _elementProjection.GetParameterElementConversion(elementVar);

    // Return/read direction: the SwiftArray<T> element type must match the Swift array's actual
    // element stride. ArrayElementCarrierType is MarshalFromSwiftType for every projection except a
    // class-bound existential element, whose 16-byte ClassExistentialContainer1 carrier replaces the
    // 40-byte opaque ExistentialContainer1 (which would over-read and crash on the first index).
    public string ContainerTypeName => $"SwiftArray<{_elementProjection.ArrayElementCarrierType}>";

    /// <summary>
    /// For MarshalFromSwift in return direction, use MarshalFromSwiftType of inner elements
    /// (same as ContainerTypeName). This ensures OptionalProjection wrapping an ArrayProjection
    /// gets the public type names (e.g., SwiftArray&lt;PaymentSdkPaymentMethod&gt;) not P/Invoke types.
    /// </summary>
    public string MarshalFromSwiftType => ContainerTypeName;

    /// <summary>
    /// Builds the container creation statements (element conversion + SwiftArray.FromEnumerable)
    /// without PayloadBuffer extraction. Returns setup statements and the container variable name.
    /// </summary>
    private (List<MarshalStatement> setup, string containerExpr) BuildContainerSetup(string paramName)
    {
        var rawElem = ParamElementCarrierType;
        var elemConversion = ParamElementConversion("e");
        // When SwiftContainerGenericType matches the C# public type, the SwiftArray<T>
        // container holds typed wrapper instances directly (e.g. SwiftArray<NonFrozenStruct>).
        // FromEnumerable then dispatches to ISwiftObject.MarshalToSwift per element, which
        // copies the struct's payload bytes by value via VWT into each contiguous slot —
        // matching the @_cdecl wrapper's `assumingMemoryBound(to: Array<TStruct>.self).pointee`
        // expectation. Applying the per-element conversion (e.g. e.Payload.DangerousGetHandle())
        // would silently downgrade the storage to 1-word IntPtr slots, causing an
        // ABI mismatch where the Swift side expects the full struct layout.
        var skipPerElementConversion = elemConversion != null
            && rawElem == _elementProjection.PublicType;
        var needsConversion = elemConversion != null && !skipPerElementConversion;
        var setup = new List<MarshalStatement>();

        if (needsConversion && _elementProjection.ElementRequiresDisposal)
        {
            // Materialize to list for disposal: .ToList() + try/finally + SwiftInner intermediate
            setup.Add(new MarshalStatement.Line(
                $"var {paramName}Converted = {paramName}.Select(e => {elemConversion}).ToList();"));
            setup.Add(new MarshalStatement.Line(
                $"SwiftArray<{rawElem}> {paramName}SwiftInner;"));

            var tryBody = new List<MarshalStatement>
            {
                new MarshalStatement.Line(
                    $"{paramName}SwiftInner = SwiftArray<{rawElem}>.FromEnumerable({paramName}Converted);")
            };
            var finallyBody = new List<MarshalStatement>
            {
                new MarshalStatement.Line(
                    $"foreach (var _item in {paramName}Converted) _item.Dispose();")
            };
            setup.Add(new MarshalStatement.Block("try", tryBody));
            setup.Add(new MarshalStatement.Block("finally", finallyBody));

            return (setup, $"{paramName}SwiftInner");
        }
        else if (needsConversion)
        {
            // Conversion needed but no disposal — lazy Select without materialization
            setup.Add(new MarshalStatement.Line(
                $"var {paramName}Containers = {paramName}.Select(e => {elemConversion});"));
            setup.Add(new MarshalStatement.Line(
                $"var {paramName}SwiftDirect = SwiftArray<{rawElem}>.FromEnumerable({paramName}Containers);"));
            return (setup, $"{paramName}SwiftDirect");
        }
        else
        {
            setup.Add(new MarshalStatement.Line(
                $"var {paramName}SwiftDirect = SwiftArray<{rawElem}>.FromEnumerable({paramName});"));
            return (setup, $"{paramName}SwiftDirect");
        }
    }

    public MarshalPlan GetParameterPlan(string paramName)
    {
        // ObjC bridge path: create NSArray from elements and pass ObjC handle
        if (UsesObjCContainerBridge)
        {
            return BuildObjCBridgeParameterPlan(paramName);
        }

        var (setup, containerExpr) = BuildContainerSetup(paramName);

        // Wrap in Using for ownership + PayloadBuffer extraction
        setup.Add(new MarshalStatement.Using(
            SwiftContainerGenericType, $"{paramName}Swift", containerExpr));
        setup.Add(new MarshalStatement.Using(
            "PayloadBuffer<IntPtr>", $"{paramName}Disposable", $"{paramName}Swift.PayloadBuffer"));
        setup.Add(new MarshalStatement.Line(
            $"IntPtr {paramName}Buffer = {paramName}Disposable.Buffer;"));

        return new MarshalPlan
        {
            SetupStatements = setup,
            PInvokeExpression = $"{paramName}Buffer"
        };
    }

    public MarshalPlan? GetContainerCreationPlan(string paramName)
    {
        // ObjC bridge: not used (no SwiftArray creation needed)
        if (UsesObjCContainerBridge)
            return null;

        var (setup, containerExpr) = BuildContainerSetup(paramName);
        return new MarshalPlan
        {
            SetupStatements = setup,
            PInvokeExpression = containerExpr
        };
    }

    public string? GetReturnContainerConversion(string containerVar)
    {
        // ObjC bridge: convert NSArray to typed list (used by OptionalProjection).
        // releaseHandle: true balances the +1 retain emitted by the Swift @_cdecl
        // wrapper (Unmanaged.passRetained(_unwrapped as AnyObject).toOpaque()).
        // Without it, ArrayFromHandleFunc leaves the +1 dangling and each call
        // leaks one NSArray plus its contained NSObject elements. The single-arg
        // ArrayFromHandle<T>(IntPtr) and the (IntPtr, Converter) overloads do NOT
        // accept ownership transfer; ArrayFromHandleFunc<T>(IntPtr, Func, bool) is
        // the only public Microsoft.iOS API that releases the input handle.
        if (UsesObjCContainerBridge)
        {
            var objcElemType = GetObjCElementType();
            return $"Foundation.NSArray.ArrayFromHandleFunc<{objcElemType}>({containerVar}, h => ObjCRuntime.Runtime.GetNSObject<{objcElemType}>(h)!, true)";
        }

        var elemConversion = OwnedReturnElementConversion("e");
        var selector = elemConversion != null
            ? $"e => {elemConversion}"
            : "e => e";
        return $"{containerVar}.AsProjected({selector})";
    }

    /// <summary>
    /// Element conversion for the OWNED-return directions only
    /// (<see cref="GetReturnContainerConversion"/> / <see cref="GetReturnPlan"/>). Swift hands
    /// each existential element to us at +1: the array subscript getter <c>InitializeWithCopy</c>s
    /// the element into a temp slot that the SwiftArray indexer then raw-frees, leaving the
    /// element's ARC retain live. The adopting proxy must release that retain on Dispose or the
    /// +1 leaks (the storage keeps its own independent +1, so adoption never double-frees).
    /// Existential elements therefore use the owning <c>new Proxy(e, ownsContainer: true)</c> form;
    /// every other element — and the shared <see cref="GetReturnElementConversion"/>, which is also
    /// reused for borrowed receiver-callback parameter reads — keeps the non-owning +0 form.
    /// Mirrors OptionalProjection.GetReturnPlan's existential-inner branch. Nested existential
    /// collections (e.g. <c>[[any P]]</c>) recurse through <see cref="GetOwnedReturnElementConversion"/>
    /// so the owning adoption threads down to the existential leaf inside the inner container.
    /// </summary>
    private string? OwnedReturnElementConversion(string elementVar)
        => _elementProjection.GetOwnedReturnElementConversion(elementVar);

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        // ObjC bridge path: IntPtr is an NSArray handle — extract typed elements
        if (UsesObjCContainerBridge)
        {
            return BuildObjCBridgeReturnPlan(resultName, strategy);
        }

        // Use ArrayElementCarrierType for return — classes/non-frozen structs need the real type name
        // (not IntPtr) for MarshalFromSwift to construct instances via ISwiftObject.NewFromPayload;
        // class-bound existential elements need the 16-byte ClassExistentialContainer1 stride
        // (ArrayElementCarrierType defaults to MarshalFromSwiftType for every other projection).
        var rawElem = _elementProjection.ArrayElementCarrierType;
        // Owned-return direction — existential elements are adopted at +1 (see OwnedReturnElementConversion).
        var elemConversion = OwnedReturnElementConversion("e");

        var asProjected = elemConversion != null
            ? $".AsProjected(e => {elemConversion})"
            : $".AsProjected(e => e)";

        return strategy switch
        {
            // Direct (by-value register) return: `resultName` is a stack temporary the caller OWNS
            // (the Swift Array value carrying +1 on its CoW storage). SwiftArray's from-handle ctor
            // runs VWT InitializeWithCopy (a fresh +1 for the SafeHandle), so the owned temporary
            // must be value-witness-destroyed afterwards or its +1 leaks the entire storage — use the
            // consuming marshal, which copies then destroys the source slot.
            ReturnStrategy.Direct => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwiftObjectConsuming<SwiftArray<{rawElem}>>(&{resultName}){asProjected}",
                RequiresUnsafe = true
            },
            ReturnStrategy.IndirectResult => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwiftObject<SwiftArray<{rawElem}>>({resultName}){asProjected}"
            },
            ReturnStrategy.OutBuffer => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwiftObject<SwiftArray<{rawElem}>>({resultName}){asProjected}"
            },
            ReturnStrategy.AsyncCallback => MarshalPlan.PassThrough(resultName),
            _ => MarshalPlan.PassThrough(resultName)
        };
    }

    public string? GetParameterElementConversion(string elementVar)
    {
        // ObjC bridge: convert IEnumerable<T> → NSArray (as NSObject for parent container).
        // For nested containers (e.g., [[URL]] inside [[[URL]]]), recursively convert inner
        // elements. For leaf ObjCBridgeable (NSUrl), elements ARE NSObject — no inner conversion.
        //
        // Disposal limitation: inner Foundation wrappers created inside Select() are single-
        // expression and have no statement boundary for using/try-finally. They rely on
        // GC/finalizer. The outer NSArray retains them via ObjC ARC, so they survive the
        // P/Invoke call; they just aren't deterministically released. This is acceptable for
        // the accessor/element-conversion context. The top-level parameter plan
        // (BuildObjCBridgeParameterPlan) uses `using var` for the outermost collection.
        if (UsesObjCContainerBridge)
        {
            // Only recurse for container-typed inner elements, not leaf ObjCBridgeable
            if (_elementProjection is ArrayProjection or DictionaryProjection or SetProjection
                && _elementProjection.UsesObjCContainerBridge)
            {
                var innerConv = _elementProjection.GetParameterElementConversion("e");
                if (innerConv != null)
                    return $"Foundation.NSArray.FromNSObjects({elementVar}.Select(e => (Foundation.NSObject){innerConv}).ToArray())";
            }
            return $"Foundation.NSArray.FromNSObjects({elementVar}.ToArray())";
        }

        var rawElem = ParamElementCarrierType;
        var elemConversion = ParamElementConversion("e");
        // Same skip-conversion rule as BuildContainerSetup — when SwiftContainerGenericType
        // matches the C# public type, FromEnumerable wants the typed wrapper directly.
        if (elemConversion != null && rawElem != _elementProjection.PublicType)
            return $"SwiftArray<{rawElem}>.FromEnumerable({elementVar}.Select(e => {elemConversion}))";
        return $"SwiftArray<{rawElem}>.FromEnumerable({elementVar})";
    }

    public string? GetReturnElementConversion(string elementVar)
    {
        // ObjC bridge: convert NSArray (received as NSObject from parent) → IReadOnlyList<T>
        if (UsesObjCContainerBridge)
        {
            var objcElemType = GetObjCElementType();
            return $"Foundation.NSArray.ArrayFromHandle<{objcElemType}>({elementVar}.Handle)";
        }

        var elemConversion = _elementProjection.GetReturnElementConversion("e");
        var selector = elemConversion != null ? $"e => {elemConversion}" : "e => e";
        return $"{elementVar}.AsProjected({selector})";
    }

    /// <summary>
    /// Owned-return element conversion for when this Array is itself an element of an OWNED outer
    /// container (e.g. the inner array of <c>[[any P]]</c>). Mirrors <see cref="GetReturnElementConversion"/>
    /// but threads the OWNED inner selector (<see cref="OwnedReturnElementConversion"/>) so an
    /// existential leaf nested under this array adopts its moved +1 instead of leaking it. ObjC-bridged
    /// elements carry no existential move-out +1 to adopt, so they keep the non-owning form.
    /// </summary>
    public string? GetOwnedReturnElementConversion(string elementVar)
    {
        if (UsesObjCContainerBridge)
            return GetReturnElementConversion(elementVar);

        var elemConversion = OwnedReturnElementConversion("e");
        var selector = elemConversion != null ? $"e => {elemConversion}" : "e => e";
        return $"{elementVar}.AsProjected({selector})";
    }

    public bool ElementRequiresDisposal => !UsesObjCContainerBridge;

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);

    // --- ObjC bridge helpers ---

    /// <summary>
    /// Gets the ObjC/.NET type for elements when extracted from an NSArray.
    /// For ObjCBridgeable elements (NSUrl): returns the element's public type.
    /// For nested ObjC bridge containers: returns the ObjC collection type (NSArray, etc.).
    /// </summary>
    private string GetObjCElementType()
    {
        if (_elementProjection is ObjCBridgeableProjection)
            return _elementProjection.PublicType;
        if (_elementProjection is ArrayProjection { UsesObjCContainerBridge: true })
            return "Foundation.NSArray";
        if (_elementProjection is DictionaryProjection { UsesObjCContainerBridge: true })
            return "Foundation.NSDictionary";
        if (_elementProjection is SetProjection { UsesObjCContainerBridge: true })
            return "Foundation.NSSet";
        return _elementProjection.PublicType;
    }

    /// <summary>
    /// ObjC bridge parameter plan: create NSArray from C# elements and pass the ObjC handle.
    /// For nested containers (e.g., [[URL]]), inner elements must be recursively converted
    /// to their ObjC collection counterparts before wrapping in the outer NSArray.
    /// </summary>
    /// <summary>
    /// REVERSE-dispatch receiver conversion: a C#-implemented conformer returns this whole
    /// container, which must cross back to the Swift EveryProtocol thunk as an NSArray pointer.
    /// Builds the NSArray from the C# elements (recursively bridging nested ObjC containers) and
    /// transfers a +1 ARC retain via <c>Arc.UnknownObjectRetain</c> (dispatches to objc_retain).
    /// The transfer retain keeps the collection — and the elements it retains — alive after the
    /// receiver frame returns, independent of the freshly-allocated managed wrapper's finalization;
    /// the Swift thunk balances it with <c>takeRetainedValue()</c>. This is the reverse of the
    /// forward accessor's <c>Unmanaged.passRetained</c>(+1) / C# <c>owns: true</c> adoption.
    /// </summary>
    public string GetReverseReceiverObjCBridgeConversion(string varName)
    {
        var isNestedContainer = _elementProjection is ArrayProjection or DictionaryProjection or SetProjection
            && _elementProjection.UsesObjCContainerBridge;
        string arrayExpr;
        if (isNestedContainer)
        {
            var innerConv = _elementProjection.GetParameterElementConversion("e");
            arrayExpr = innerConv != null
                ? $"{varName}.Select(e => (Foundation.NSObject){innerConv}).ToArray()"
                : $"{varName}.ToArray()";
        }
        else
        {
            arrayExpr = $"{varName}.ToArray()";
        }
        return $"global::Swift.Runtime.Arc.UnknownObjectRetain(Foundation.NSArray.FromNSObjects({arrayExpr}).Handle)";
    }

    private MarshalPlan BuildObjCBridgeParameterPlan(string paramName)
    {
        // For nested containers (e.g., [[URL]]), inner elements need recursive conversion
        // to their ObjC collection counterparts before wrapping in the outer NSArray.
        // Inner wrappers must be materialized for disposal after the P/Invoke call.
        var isNestedContainer = _elementProjection is ArrayProjection or DictionaryProjection or SetProjection
            && _elementProjection.UsesObjCContainerBridge;

        if (isNestedContainer)
        {
            var innerConv = _elementProjection.GetParameterElementConversion("e");
            if (innerConv != null)
            {
                // Inner wrappers are retained by the outer NSArray — disposing the outer releases
                // its retain on the inners. No separate inner disposal needed (ObjC ARC handles it).
                var setup = new List<MarshalStatement>
                {
                    new MarshalStatement.Line(
                        $"using var {paramName}NSArray = Foundation.NSArray.FromNSObjects({paramName}.Select(e => (Foundation.NSObject){innerConv}).ToArray());"),
                    new MarshalStatement.Line(
                        $"IntPtr {paramName}Buffer = {paramName}NSArray.Handle;")
                };
                return new MarshalPlan
                {
                    SetupStatements = setup,
                    PInvokeExpression = $"{paramName}Buffer"
                };
            }
        }

        var setup2 = new List<MarshalStatement>
        {
            new MarshalStatement.Line(
                $"using var {paramName}NSArray = Foundation.NSArray.FromNSObjects({paramName}.ToArray());"),
            new MarshalStatement.Line(
                $"IntPtr {paramName}Buffer = {paramName}NSArray.Handle;")
        };
        return new MarshalPlan
        {
            SetupStatements = setup2,
            PInvokeExpression = $"{paramName}Buffer"
        };
    }

    /// <summary>
    /// ObjC bridge return plan: receive NSArray handle, extract typed elements.
    /// releaseHandle: true on ArrayFromHandleFunc balances the +1 retain emitted
    /// by the Swift @_cdecl wrapper. Inner-element NSArray reads (via
    /// GetReturnElementConversion) stay non-releasing because nested elements
    /// are borrowed references owned by the outer NSArray.
    ///
    /// Note: Microsoft.iOS exposes no `ArrayFromHandle&lt;T&gt;(IntPtr, bool owns)`
    /// overload; the closest ownership-transferring API is
    /// <c>ArrayFromHandleFunc&lt;T&gt;(IntPtr, Func&lt;NativeHandle, T&gt;, bool releaseHandle)</c>.
    /// Using <c>ObjCRuntime.Runtime.GetNSObject&lt;T&gt;</c> as the factory
    /// matches what the single-arg <c>ArrayFromHandle</c> does internally,
    /// so this swap only adds the +1-retain release; per-element marshaling
    /// is unchanged.
    /// </summary>
    private MarshalPlan BuildObjCBridgeReturnPlan(string resultName, ReturnStrategy strategy)
    {
        var objcElemType = GetObjCElementType();
        var arrayFromHandle = $"Foundation.NSArray.ArrayFromHandleFunc<{objcElemType}>({resultName}, h => ObjCRuntime.Runtime.GetNSObject<{objcElemType}>(h)!, true)";

        // For nested containers, apply inner element conversion
        if (_elementProjection is ArrayProjection or DictionaryProjection or SetProjection
            && _elementProjection.UsesObjCContainerBridge)
        {
            var innerConv = _elementProjection.GetReturnElementConversion("e");
            if (innerConv != null)
                arrayFromHandle = $"Foundation.NSArray.ArrayFromHandleFunc<{objcElemType}>({resultName}, h => ObjCRuntime.Runtime.GetNSObject<{objcElemType}>(h)!, true).Select(e => {innerConv}).ToList()";
        }

        // ObjC bridge returns as ClassPointer (direct IntPtr), not IndirectResult
        return new MarshalPlan
        {
            PInvokeExpression = arrayFromHandle
        };
    }
}
