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
/// Return direction: MarshalFromSwift + ToHashSet with element conversion lambda, or a live view of
/// the Swift set for object-reference optional elements (see ReadSet).
/// </summary>
public class SetProjection : ITypeProjection, IObjCContainerBridgeOwnerSource
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
                $"var {paramName}Converted = global::System.Linq.Enumerable.ToList(global::System.Linq.Enumerable.Select({paramName}, e => {elemConversion}));"));
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
                $"var {paramName}Containers = {CollectionElementSelect.Elements(paramName, "e => " + elemConversion, _elementProjection)};"));
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
            PInvokeExpression = $"{paramName}Buffer",
            // The container's element storage is a refcounted object; a consuming callee releases
            // it, so the borrowed count read out of the transient wrapper has to be topped up.
            OwnedValueArgument = new(SwiftContainerGenericType, $"{paramName}Swift.Payload")
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
            return ReadObjCSet($"ObjCRuntime.Runtime.GetINativeObject<Foundation.NSSet>({containerVar}, true)!");

        var elemConversion = OwnedReturnElementConversion("e");
        if (elemConversion != null)
            return ReadSet(containerVar, elemConversion);
        // SwiftSet<T> already implements IReadOnlySet<T>, no conversion needed
        return null;
    }

    /// <summary>
    /// The public set a Swift set reads as, its elements converted by <paramref name="elemConversion"/>.
    /// The source is a <see cref="Swift.SwiftSet{Element}"/> wrapper, which owns its copy of the Swift
    /// storage whether it was returned, read out of a container or handed to a receiver, so a view of
    /// it outlives nothing it depends on. A set of object-reference optionals (<c>Set&lt;C?&gt;</c>, each
    /// slot the optional type's carrier) reads as a live view whose lookups go back to the Swift set:
    /// Swift hashes the element by the conformance it was built with, which a wrapper's managed
    /// equality need not share (a conformance declared outside the class's module, or one
    /// conditional on its generic arguments, leaves the wrapper comparing by identity), so a managed
    /// copy would miss an element Swift holds. Every other set is copied into a
    /// <see cref="System.Collections.Generic.HashSet{T}"/>, its element types' managed equality
    /// being the one Swift uses, except a set of strings, which compares through Swift.
    /// </summary>
    internal string ReadSet(string sourceExpr, string elemConversion)
    {
        if (ViewReverseConversion() is { } reverse)
            return $"{sourceExpr}.AsProjected<{_elementProjection.PublicType}>(e => {elemConversion}, p => {reverse})";
        // A set of Swift strings, arrays or sets compares its elements as Swift does
        // (DictionaryProjection.SetElementComparer).
        var comparer = DictionaryProjection.SetElementComparer(_elementProjection) is { } elementComparer ? $", {elementComparer}" : "";
        return $"global::System.Linq.Enumerable.ToHashSet(global::System.Linq.Enumerable.Select({sourceExpr}, e => {elemConversion}){comparer})";
    }

    /// <summary>
    /// True when <see cref="ReadSet"/> reads the set as a live view, which keeps the source set: a
    /// caller that disposes its source after converting must not dispose it then.
    /// </summary>
    internal bool ReadsAsView => ViewReverseConversion() != null;

    private string? ViewReverseConversion()
        => _elementProjection is OptionalProjection { ObjectCarrier: not null } optional
            ? optional.GetParameterElementConversion("p")
            : null;

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
        string Converted(string marshalExpr) => elemConversion != null
            ? ReadSet(marshalExpr, elemConversion)
            : marshalExpr;

        return strategy switch
        {
            // Direct (by-value register) return: the owned Swift Set temporary carries +1 on its CoW
            // storage. SwiftSet's from-handle ctor runs VWT InitializeWithCopy (a fresh +1 for the
            // SafeHandle), so the source slot must be value-witness-destroyed or that +1 leaks the
            // storage — use the consuming marshal (copy then destroy the source).
            ReturnStrategy.Direct => new MarshalPlan
            {
                PInvokeExpression = Converted($"SwiftMarshal.MarshalFromSwiftObjectConsuming<SwiftSet<{rawElem}>>(&{resultName})"),
                RequiresUnsafe = true
            },
            ReturnStrategy.IndirectResult => new MarshalPlan
            {
                PInvokeExpression = Converted($"SwiftMarshal.MarshalFromSwiftObject<SwiftSet<{rawElem}>>({resultName})")
            },
            ReturnStrategy.OutBuffer => new MarshalPlan
            {
                PInvokeExpression = Converted($"SwiftMarshal.MarshalFromSwiftObject<SwiftSet<{rawElem}>>({resultName})")
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
                    return ObjCContainerBridgeOwner.NewSet($"global::System.Linq.Enumerable.ToArray(global::System.Linq.Enumerable.Select({elementVar}, e => (Foundation.NSObject){innerConv}))");
            }
            return ObjCContainerBridgeOwner.NewSet(ObjCLeafElementArrayExpr(elementVar));
        }

        var rawElem = ExistentialElementCarrier.CarrierType(_elementProjection, _elementProjection.SwiftContainerGenericType);
        var elemConversion = ExistentialElementCarrier.ParamConversion(_elementProjection, "e");
        // Same skip-conversion rule as BuildContainerSetup — when SwiftContainerGenericType
        // matches the C# public type, FromEnumerable wants the typed wrapper directly.
        if (elemConversion != null && rawElem != _elementProjection.PublicType)
            return $"SwiftSet<{rawElem}>.FromEnumerable({CollectionElementSelect.Elements(elementVar, "e => " + elemConversion, _elementProjection)})";
        return $"SwiftSet<{rawElem}>.FromEnumerable({elementVar})";
    }

    public string? GetReturnElementConversion(string elementVar)
    {
        if (UsesObjCContainerBridge)
            return ReadObjCSet($"ObjCRuntime.Runtime.GetNSObject<Foundation.NSSet>({elementVar})!");

        var elemConversion = _elementProjection.GetReturnElementConversion("e");
        if (elemConversion != null)
            return ReadSet(elementVar, elemConversion);
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
            return ReadSet(elementVar, elemConversion);
        return null;
    }

    public bool ElementRequiresDisposal => !UsesObjCContainerBridge;

    // --- ObjC bridge helpers ---

    /// <summary>
    /// The ObjC-object array expression an <c>NSSet</c> construction is built from. For a leaf
    /// element that already IS an NSObject this is just <c>{var}.ToArray()</c>; for an Apple
    /// typed-enum element (a C# enum whose ObjC carrier is an NSString constant) it converts each
    /// value to its backing constant first.
    /// </summary>
    /// <remarks>
    /// Internal rather than private for the same reason as the array twin: the accessor SETTER path
    /// (<c>AccessorConversionVisitors.SetSetterConversion</c>) constructs its own <c>NSSet</c> and has to
    /// read the leaf expression from here instead of keeping a second copy.
    /// </remarks>
    internal string ObjCLeafElementArrayExpr(string varName)
        => _elementProjection.TypedEnumAdapter is { } adapter
            ? $"global::System.Linq.Enumerable.ToArray(global::System.Linq.Enumerable.Select({varName}, e => {adapter.ToCarrier("e")}))"
            : $"global::System.Linq.Enumerable.ToArray({varName})";

    /// <summary>
    /// The elements of the NSSet <paramref name="nsSetExpr"/> read into a new managed set. Each
    /// element is read as an element of an Objective-C collection (<see cref="DictionaryProjection.FromNSObject"/>):
    /// a URL or an Apple typed-enum carrier as itself, and a nested collection (<c>Set&lt;[URL]&gt;</c>,
    /// an NSSet of NSArrays) as the collection it bridged to, not as a leaf. A set of Swift strings,
    /// arrays or sets compares its elements as Swift does (<see cref="DictionaryProjection.SetElementComparer"/>).
    /// Each nesting level names its locals by its own depth, so a set read inside another one does
    /// not redeclare its parent's.
    /// </summary>
    internal string ReadObjCSet(string nsSetExpr)
    {
        var elemPublicType = _elementProjection.PublicType;
        var depth = ObjCSetNestingDepth(this);
        string Name(string stem) => depth == 1 ? stem : $"{stem}{depth}";
        var (nsSet, set, nsObj) = (Name("_nsSet"), Name("_set"), Name("_nsObj"));
        var comparer = DictionaryProjection.SetElementComparer(_elementProjection) ?? "";
        return $"((Func<IReadOnlySet<{elemPublicType}>>)(() => {{ " +
               $"var {nsSet} = {nsSetExpr}; " +
               $"var {set} = new System.Collections.Generic.HashSet<{elemPublicType}>({comparer}); " +
               $"foreach (var {nsObj} in {nsSet}) {set}.Add({DictionaryProjection.FromNSObject(_elementProjection, nsObj)}); " +
               $"return {set}; }}))()";
    }

    /// <summary>How many sets enclose this one's elements, itself included.</summary>
    private static int ObjCSetNestingDepth(ITypeProjection projection) => projection switch
    {
        SetProjection set => 1 + ObjCSetNestingDepth(set.ElementProjection),
        ArrayProjection array => ObjCSetNestingDepth(array.ElementProjection),
        DictionaryProjection dictionary => Math.Max(
            ObjCSetNestingDepth(dictionary.KeyProjection),
            ObjCSetNestingDepth(dictionary.ValueProjection)),
        OptionalProjection optional => ObjCSetNestingDepth(optional.InnerProjection),
        _ => 0
    };

    public ObjCContainerBridgeOwner BuildObjCBridgeOwner(string paramName, bool sourceIsNullable)
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
                ? $"global::System.Linq.Enumerable.ToArray(global::System.Linq.Enumerable.Select({paramName}, e => (Foundation.NSObject){innerConv}))"
                : $"global::System.Linq.Enumerable.ToArray({paramName})";
        }
        else
        {
            arrayExpr = ObjCLeafElementArrayExpr(paramName);
        }

        var ownerName = $"{paramName}NSSet";
        return new ObjCContainerBridgeOwner(
            new List<MarshalStatement>
            {
                ObjCContainerBridgeOwner.Declare(
                    "Foundation.NSSet", ownerName,
                    ObjCContainerBridgeOwner.NewSet(arrayExpr), paramName, sourceIsNullable)
            },
            ownerName);
    }

    private MarshalPlan BuildObjCBridgeParameterPlan(string paramName)
    {
        var owner = BuildObjCBridgeOwner(paramName, sourceIsNullable: false);
        var setup = new List<MarshalStatement>(owner.Setup)
        {
            new MarshalStatement.Line($"IntPtr {paramName}Buffer = {owner.Name}.Handle;")
        };
        return new MarshalPlan
        {
            SetupStatements = setup,
            PInvokeExpression = $"{paramName}Buffer",
            // The bridged NSSet handle is borrowed: the `using` wrapper releases it after the
            // call, so a consuming callee needs a +1 handed over first.
            OwnedHandOverHandle = $"{paramName}Buffer"
        };
    }

    /// <summary>
    /// REVERSE-dispatch receiver conversion: a C#-implemented conformer returns this whole
    /// container, which must cross back to the Swift EveryProtocol thunk as an NSSet pointer.
    /// Builds the NSSet from the C# elements (recursively bridging nested ObjC containers) and
    /// transfers a +1 ARC retain via <c>Arc.UnknownObjectRetain</c> (dispatches to objc_retain).
    /// The transfer retain keeps the collection — and the elements it retains — alive after the
    /// receiver frame returns, after the new managed wrapper, held until the retain is taken, is released;
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
                ? $"global::System.Linq.Enumerable.ToArray(global::System.Linq.Enumerable.Select({varName}, e => (Foundation.NSObject){innerConv}))"
                : $"global::System.Linq.Enumerable.ToArray({varName})";
        }
        else
        {
            arrayExpr = ObjCLeafElementArrayExpr(varName);
        }
        return ObjCContainerBridgeOwner.RetainedHandle(ObjCContainerBridgeOwner.NewSet(arrayExpr));
    }

    private MarshalPlan BuildObjCBridgeReturnPlan(string resultName)
    {
        // NSSet received as ObjC pointer → HashSet<T>.
        // owns: true balances the +1 retain emitted by the Swift @_cdecl wrapper.
        return new MarshalPlan
        {
            SetupStatements = new List<MarshalStatement>
            {
                new MarshalStatement.Line(
                    $"var {resultName}Set = {ReadObjCSet($"ObjCRuntime.Runtime.GetINativeObject<Foundation.NSSet>({resultName}, true)!")};")
            },
            PInvokeExpression = $"{resultName}Set"
        };
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
