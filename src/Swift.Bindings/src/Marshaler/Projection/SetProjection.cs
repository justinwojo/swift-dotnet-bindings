// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for Swift.Set&lt;T&gt; ↔ C# IReadOnlySet&lt;T&gt; (return) or IEnumerable&lt;T&gt; (parameter).
/// Composes with an inner element projection for element-wise marshalling.
///
/// Parameter direction: FromEnumerable + PayloadBuffer, with optional element conversion + disposal.
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

    public string PublicType => _isParameter
        ? $"IEnumerable<{_elementProjection.PublicType}>"
        : $"IReadOnlySet<{_elementProjection.PublicType}>";

    public string PInvokeType => "IntPtr";
    public string? PInvokeAttribute => null;

    public string SwiftContainerGenericType => $"SwiftSet<{_elementProjection.SwiftContainerGenericType}>";

    public string ContainerTypeName => $"SwiftSet<{_elementProjection.MarshalFromSwiftType}>";

    public string MarshalFromSwiftType => ContainerTypeName;

    /// <summary>
    /// Builds the container creation statements (element conversion + SwiftSet.FromEnumerable)
    /// without PayloadBuffer extraction. Returns setup statements and the container variable name.
    /// </summary>
    private (List<MarshalStatement> setup, string containerExpr) BuildContainerSetup(string paramName)
    {
        var rawElem = _elementProjection.SwiftContainerGenericType;
        var elemConversion = _elementProjection.GetParameterElementConversion("e");
        var setup = new List<MarshalStatement>();

        if (elemConversion != null && _elementProjection.ElementRequiresDisposal)
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
        else if (elemConversion != null)
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
        // ObjC bridge: convert NSSet handle to typed HashSet (used by OptionalProjection)
        if (UsesObjCContainerBridge)
        {
            var elemPublicType = _elementProjection.PublicType;
            var elemConv = MarshallingHelpers.FormatObjCBridgeCall(elemPublicType, "_nsObj.Handle", nonNull: true);
            return $"((Func<IReadOnlySet<{elemPublicType}>>)(() => {{ " +
                   $"var _nsSet = ObjCRuntime.Runtime.GetNSObject<Foundation.NSSet>({containerVar})!; " +
                   $"var _set = new System.Collections.Generic.HashSet<{elemPublicType}>(); " +
                   $"foreach (var _nsObj in _nsSet) _set.Add({elemConv}); " +
                   $"return _set; }}))()";
        }

        var elemConversion = _elementProjection.GetReturnElementConversion("e");
        if (elemConversion != null)
            return $"{containerVar}.Select(e => {elemConversion}).ToHashSet()";
        // SwiftSet<T> already implements IReadOnlySet<T>, no conversion needed
        return null;
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        // ObjC bridge path: IntPtr is an NSSet handle — extract typed elements
        if (UsesObjCContainerBridge)
            return BuildObjCBridgeReturnPlan(resultName);

        var rawElem = _elementProjection.MarshalFromSwiftType;
        var elemConversion = _elementProjection.GetReturnElementConversion("e");

        // If element conversion is needed (e.g., SwiftString→string), materialize via ToHashSet
        var conversion = elemConversion != null
            ? $".Select(e => {elemConversion}).ToHashSet()"
            : "";

        return strategy switch
        {
            ReturnStrategy.Direct => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwift<SwiftSet<{rawElem}>>(new IntPtr(&{resultName})){conversion}",
                RequiresUnsafe = true
            },
            ReturnStrategy.IndirectResult => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwift<SwiftSet<{rawElem}>>({resultName}){conversion}"
            },
            ReturnStrategy.OutBuffer => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwift<SwiftSet<{rawElem}>>({resultName}){conversion}"
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

        var rawElem = _elementProjection.SwiftContainerGenericType;
        var elemConversion = _elementProjection.GetParameterElementConversion("e");
        if (elemConversion != null)
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
        // Use NSArray.ArrayFromHandle via intermediate (NSSet doesn't have typed extraction)
        return new MarshalPlan
        {
            SetupStatements = new List<MarshalStatement>
            {
                new MarshalStatement.Line(
                    $"var {resultName}NSSet = ObjCRuntime.Runtime.GetNSObject<Foundation.NSSet>({resultName})!;"),
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
