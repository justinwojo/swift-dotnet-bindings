// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for Swift.Set&lt;T&gt; ↔ C# IReadOnlySet&lt;T&gt; (both directions).
/// Composes with an inner element projection for element-wise marshalling.
///
/// Parameter direction: FromEnumerable + PayloadBuffer, with optional element conversion + disposal.
///   The public parameter type is IReadOnlySet&lt;T&gt; (not IEnumerable&lt;T&gt;) so the C# type
///   system communicates the Swift `Set&lt;T&gt;` uniqueness invariant to consumers — passing
///   a `List` or `T[]` would silently dedupe inside the Swift wrapper, hiding bugs at the
///   API boundary. Callers wanting a quick conversion can use `.ToHashSet()` on any
///   IEnumerable. Using IReadOnlySet communicates the Swift Set uniqueness invariant; passing
///   a List or array would silently dedupe inside the Swift wrapper.
/// Return direction: MarshalFromSwift + ToHashSet with element conversion lambda.
/// </summary>
public class SetProjection : ITypeProjection
{
    private readonly ITypeProjection _elementProjection;
    private readonly bool _isParameter;

    public SetProjection(ITypeProjection elementProjection, bool isParameter)
    {
        _elementProjection = elementProjection;
        _isParameter = isParameter;
    }

    public ITypeProjection ElementProjection => _elementProjection;

    /// <summary>
    /// True when element projection uses ObjC container bridge — the entire set
    /// crosses the @_cdecl boundary as an NSSet pointer instead of SwiftSet&lt;T&gt;.
    /// </summary>
    public bool UsesObjCContainerBridge => _elementProjection.UsesObjCContainerBridge;

    // Both directions surface as IReadOnlySet<T>. Parameter-side keeps the same
    // FromEnumerable plumbing (IReadOnlySet<T> implements IEnumerable<T>) — the
    // change is type-system fidelity at the public API surface only.
    public string PublicType => $"IReadOnlySet<{_elementProjection.PublicType}>";

    public string PInvokeType => "IntPtr";
    public string? PInvokeAttribute => null;

    // PARAMETER direction: existential elements ride the owned (+1) carrier type so the
    // FromEnumerable store + the set's value-witness destroy balance against an independent
    // retain rather than over-releasing the proxy's sole construction +1. Mirrors ArrayProjection.
    public string SwiftContainerGenericType => $"SwiftSet<{ExistentialElementCarrier.CarrierType(_elementProjection, _elementProjection.SwiftContainerGenericType)}>";

    // READ direction: same carrier element type so the slot stride agrees. Mirrors ArrayProjection.
    public string ContainerTypeName => $"SwiftSet<{_elementProjection.ArrayElementCarrierType}>";

    public string MarshalFromSwiftType => ContainerTypeName;

    /// <summary>
    /// Builds the container creation statements (element conversion + SwiftSet.FromEnumerable)
    /// without PayloadBuffer extraction. Returns setup statements and the container variable name.
    /// </summary>
    private (List<MarshalStatement> setup, string containerExpr) BuildContainerSetup(string paramName)
    {
        var rawElem = ExistentialElementCarrier.CarrierType(_elementProjection, _elementProjection.SwiftContainerGenericType);
        var elemConversion = ExistentialElementCarrier.ParamConversion(_elementProjection, "e");
        // When SwiftContainerGenericType matches the C# public type, the SwiftSet<T>
        // container holds typed wrapper instances directly (e.g. SwiftSet<NonFrozenStruct>).
        // FromEnumerable then dispatches to ISwiftObject.MarshalToSwift per element, which
        // copies the struct's payload bytes by value via VWT into each contiguous slot.
        // Applying the per-element conversion (e.g. e.Payload.DangerousGetHandle()) would
        // silently downgrade the storage to 1-word IntPtr slots, causing an ABI mismatch
        // where the Swift side expects the full struct layout. Mirrors ArrayProjection.
        var skipPerElementConversion = elemConversion != null
            && rawElem == _elementProjection.PublicType;
        var needsConversion = elemConversion != null && !skipPerElementConversion;
        var setup = new List<MarshalStatement>();

        if (needsConversion && _elementProjection.ElementRequiresDisposal)
        {
            setup.Add(new MarshalStatement.Line(
                $"var {paramName}Converted = {paramName}.Select(e => {elemConversion}).ToList();"));
            setup.Add(new MarshalStatement.Line(
                $"SwiftSet<{rawElem}> {paramName}SwiftInner;"));

            var tryBody = new List<MarshalStatement>
            {
                new MarshalStatement.Line(
                    $"{paramName}SwiftInner = SwiftSet<{rawElem}>.FromEnumerable({paramName}Converted);")
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
            setup.Add(new MarshalStatement.Line(
                $"var {paramName}Containers = {paramName}.Select(e => {elemConversion});"));
            setup.Add(new MarshalStatement.Line(
                $"var {paramName}SwiftDirect = SwiftSet<{rawElem}>.FromEnumerable({paramName}Containers);"));
            return (setup, $"{paramName}SwiftDirect");
        }
        else
        {
            setup.Add(new MarshalStatement.Line(
                $"var {paramName}SwiftDirect = SwiftSet<{rawElem}>.FromEnumerable({paramName});"));
            return (setup, $"{paramName}SwiftDirect");
        }
    }

    public MarshalPlan GetParameterPlan(string paramName)
    {
        // ObjC bridge path: create NSSet from elements and pass ObjC handle
        if (UsesObjCContainerBridge)
            return BuildObjCBridgeParameterPlan(paramName);

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
        // ObjC bridge: convert NSSet handle to typed HashSet (used by OptionalProjection).
        // owns: true balances the +1 retain emitted by the Swift @_cdecl wrapper
        // (Unmanaged.passRetained(_unwrapped as AnyObject).toOpaque()). Without
        // owns: true, the NSSet (and any retained inner elements) leak per call.
        if (UsesObjCContainerBridge)
        {
            var elemPublicType = _elementProjection.PublicType;
            var elemConv = MarshallingHelpers.FormatObjCBridgeCall(elemPublicType, "_nsObj.Handle", nonNull: true);
            return $"((Func<IReadOnlySet<{elemPublicType}>>)(() => {{ " +
                   $"var _nsSet = ObjCRuntime.Runtime.GetINativeObject<Foundation.NSSet>({containerVar}, true)!; " +
                   $"var _set = new System.Collections.Generic.HashSet<{elemPublicType}>(); " +
                   $"foreach (var _nsObj in _nsSet) _set.Add({elemConv}); " +
                   $"return _set; }}))()";
        }

        var elemConversion = OwnedReturnElementConversion("e");
        if (elemConversion != null)
            return $"{containerVar}.Select(e => {elemConversion}).ToHashSet()";
        // SwiftSet<T> already implements IReadOnlySet<T>, no conversion needed
        return null;
    }

    /// <summary>
    /// Element conversion for the OWNED-return directions only. SwiftSet's iterator moves
    /// each element out of the slot at +1 (MarshalMovedValueFromSlot), so the adopting proxy must
    /// release that retain on Dispose or it leaks; the source set keeps its own independent +1, so
    /// adoption never double-frees. Existential elements use the owning form; every other element —
    /// and the shared non-owning <see cref="GetReturnElementConversion"/> reused for borrowed reads —
    /// stays +0. Nested container elements recurse through <see cref="GetOwnedReturnElementConversion"/>
    /// so an existential leaf inside an inner container still adopts its moved +1. Mirrors
    /// ArrayProjection.OwnedReturnElementConversion.
    /// </summary>
    private string? OwnedReturnElementConversion(string elementVar)
        => _elementProjection.GetOwnedReturnElementConversion(elementVar);

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        // ObjC bridge path: IntPtr is an NSSet handle — extract typed elements
        if (UsesObjCContainerBridge)
            return BuildObjCBridgeReturnPlan(resultName);

        var rawElem = _elementProjection.MarshalFromSwiftType;
        // Owned-return direction — existential elements are adopted at +1 (see
        // OwnedReturnElementConversion). Mirrors GetReturnContainerConversion and
        // ArrayProjection/DictionaryProjection.GetReturnPlan; the shared non-owning
        // GetReturnElementConversion stays reserved for borrowed receiver reads.
        var elemConversion = OwnedReturnElementConversion("e");

        // If element conversion is needed (e.g., SwiftString→string), materialize via ToHashSet
        var conversion = elemConversion != null
            ? $".Select(e => {elemConversion}).ToHashSet()"
            : "";

        return strategy switch
        {
            // Direct (by-value register) return: the owned Swift Set temporary carries +1 on its CoW
            // storage. SwiftSet's from-handle ctor runs VWT InitializeWithCopy (a fresh +1 for the
            // SafeHandle), so the source slot must be value-witness-destroyed or that +1 leaks the
            // storage — use the consuming marshal (copy then destroy the source).
            ReturnStrategy.Direct => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwiftObjectConsuming<SwiftSet<{rawElem}>>(&{resultName}){conversion}",
                RequiresUnsafe = true
            },
            ReturnStrategy.IndirectResult => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwiftObject<SwiftSet<{rawElem}>>({resultName}){conversion}"
            },
            ReturnStrategy.OutBuffer => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwiftObject<SwiftSet<{rawElem}>>({resultName}){conversion}"
            },
            ReturnStrategy.AsyncCallback => MarshalPlan.PassThrough(resultName),
            _ => MarshalPlan.PassThrough(resultName)
        };
    }

    public string? GetParameterElementConversion(string elementVar)
    {
        // ObjC bridge: convert IEnumerable<T> → NSSet. Recursively convert nested container elements.
        // For leaf ObjCBridgeable (NSUrl), elements ARE NSObject — no inner conversion needed.
        if (UsesObjCContainerBridge)
        {
            if (_elementProjection is ArrayProjection or DictionaryProjection or SetProjection
                && _elementProjection.UsesObjCContainerBridge)
            {
                var innerConv = _elementProjection.GetParameterElementConversion("e");
                if (innerConv != null)
                    return $"new Foundation.NSSet({elementVar}.Select(e => (Foundation.NSObject){innerConv}).ToArray())";
            }
            return $"new Foundation.NSSet({elementVar}.ToArray())";
        }

        var rawElem = ExistentialElementCarrier.CarrierType(_elementProjection, _elementProjection.SwiftContainerGenericType);
        var elemConversion = ExistentialElementCarrier.ParamConversion(_elementProjection, "e");
        // Same skip-conversion rule as BuildContainerSetup — when SwiftContainerGenericType
        // matches the C# public type, FromEnumerable wants the typed wrapper directly.
        if (elemConversion != null && rawElem != _elementProjection.PublicType)
            return $"SwiftSet<{rawElem}>.FromEnumerable({elementVar}.Select(e => {elemConversion}))";
        return $"SwiftSet<{rawElem}>.FromEnumerable({elementVar})";
    }

    public string? GetReturnElementConversion(string elementVar)
    {
        if (UsesObjCContainerBridge)
        {
            var elemPublicType = _elementProjection.PublicType;
            var elemConv = MarshallingHelpers.FormatObjCBridgeCall(elemPublicType, "_nsObj.Handle", nonNull: true);
            return $"((Func<IReadOnlySet<{elemPublicType}>>)(() => {{ " +
                   $"var _nsSet = ObjCRuntime.Runtime.GetNSObject<Foundation.NSSet>({elementVar})!; " +
                   $"var _set = new System.Collections.Generic.HashSet<{elemPublicType}>(); " +
                   $"foreach (var _nsObj in _nsSet) _set.Add({elemConv}); " +
                   $"return _set; }}))()";
        }

        var elemConversion = _elementProjection.GetReturnElementConversion("e");
        if (elemConversion != null)
            return $"{elementVar}.Select(e => {elemConversion}).ToHashSet()";
        return null;
    }

    /// <summary>
    /// Owned-return element conversion for when this Set is itself an element of an OWNED outer
    /// container. Mirrors <see cref="GetReturnElementConversion"/> but threads the OWNED inner
    /// selector (<see cref="OwnedReturnElementConversion"/>) so an existential leaf nested under this
    /// set adopts its moved +1 instead of leaking it. ObjC-bridged elements keep the non-owning form.
    /// </summary>
    public string? GetOwnedReturnElementConversion(string elementVar)
    {
        if (UsesObjCContainerBridge)
            return GetReturnElementConversion(elementVar);

        var elemConversion = OwnedReturnElementConversion("e");
        if (elemConversion != null)
            return $"{elementVar}.Select(e => {elemConversion}).ToHashSet()";
        return null;
    }

    public bool ElementRequiresDisposal => !UsesObjCContainerBridge;

    // --- ObjC bridge helpers ---

    private MarshalPlan BuildObjCBridgeParameterPlan(string paramName)
    {
        // For nested containers (e.g., Set<[URL]>), inner elements need recursive conversion
        // to their ObjC collection counterparts before wrapping in the outer NSSet.
        var isNestedContainer = _elementProjection is ArrayProjection or DictionaryProjection or SetProjection
            && _elementProjection.UsesObjCContainerBridge;
        string arrayExpr;
        if (isNestedContainer)
        {
            var innerConv = _elementProjection.GetParameterElementConversion("e");
            arrayExpr = innerConv != null
                ? $"{paramName}.Select(e => (Foundation.NSObject){innerConv}).ToArray()"
                : $"{paramName}.ToArray()";
        }
        else
        {
            arrayExpr = $"{paramName}.ToArray()";
        }

        var setup = new List<MarshalStatement>
        {
            new MarshalStatement.Line(
                $"using var {paramName}NSSet = new Foundation.NSSet({arrayExpr});"),
            new MarshalStatement.Line(
                $"IntPtr {paramName}Buffer = {paramName}NSSet.Handle;")
        };
        return new MarshalPlan
        {
            SetupStatements = setup,
            PInvokeExpression = $"{paramName}Buffer"
        };
    }

    private MarshalPlan BuildObjCBridgeReturnPlan(string resultName)
    {
        var elemPublicType = _elementProjection.PublicType;
        // NSSet received as ObjC pointer → HashSet<T>
        // Use NSArray.ArrayFromHandle via intermediate (NSSet doesn't have typed extraction).
        // owns: true balances the +1 retain emitted by the Swift @_cdecl wrapper.
        return new MarshalPlan
        {
            SetupStatements = new List<MarshalStatement>
            {
                new MarshalStatement.Line(
                    $"var {resultName}NSSet = ObjCRuntime.Runtime.GetINativeObject<Foundation.NSSet>({resultName}, true)!;"),
                new MarshalStatement.Line(
                    $"var {resultName}Set = new System.Collections.Generic.HashSet<{elemPublicType}>();"),
                new MarshalStatement.Line(
                    $"foreach (var _nsObj in {resultName}NSSet) {resultName}Set.Add({MarshallingHelpers.FormatObjCBridgeCall(elemPublicType, "_nsObj.Handle", nonNull: true)});")
            },
            PInvokeExpression = $"{resultName}Set"
        };
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
