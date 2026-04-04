// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for Swift.Dictionary&lt;K,V&gt; ↔ C# IReadOnlyDictionary (return) or IDictionary (parameter).
/// Composes with inner key and value projections for element-wise marshalling.
///
/// Parameter direction: FromDictionary + PayloadBuffer, with optional key/value conversion + disposal.
/// Return direction: MarshalFromSwift + AsProjected with key/value conversion lambdas.
/// </summary>
public class DictionaryProjection : ITypeProjection
{
    private readonly ITypeProjection _keyProjection;
    private readonly ITypeProjection _valueProjection;
    private readonly bool _isParameter;

    public DictionaryProjection(ITypeProjection keyProjection, ITypeProjection valueProjection, bool isParameter)
    {
        _keyProjection = keyProjection;
        _valueProjection = valueProjection;
        _isParameter = isParameter;
    }

    /// <summary>The inner key projection.</summary>
    public ITypeProjection KeyProjection => _keyProjection;

    /// <summary>The inner value projection.</summary>
    public ITypeProjection ValueProjection => _valueProjection;

    /// <summary>
    /// True when key or value projection uses ObjC container bridge — the entire dictionary
    /// crosses the @_cdecl boundary as an NSDictionary pointer instead of SwiftDictionary&lt;K,V&gt;.
    /// </summary>
    public bool UsesObjCContainerBridge =>
        _keyProjection.UsesObjCContainerBridge || _valueProjection.UsesObjCContainerBridge;

    public string PublicType => _isParameter
        ? $"IDictionary<{_keyProjection.PublicType}, {_valueProjection.PublicType}>"
        : $"IReadOnlyDictionary<{_keyProjection.PublicType}, {_valueProjection.PublicType}>";

    public string PInvokeType => "IntPtr";
    public string? PInvokeAttribute => null;

    public string SwiftContainerGenericType => $"SwiftDictionary<{_keyProjection.SwiftContainerGenericType}, {_valueProjection.SwiftContainerGenericType}>";

    public string ContainerTypeName => $"SwiftDictionary<{_keyProjection.MarshalFromSwiftType}, {_valueProjection.MarshalFromSwiftType}>";

    /// <summary>
    /// For MarshalFromSwift in return direction, use MarshalFromSwiftType of inner key/value
    /// (same as ContainerTypeName). This ensures OptionalProjection wrapping a DictionaryProjection
    /// gets the public type names not P/Invoke types.
    /// </summary>
    public string MarshalFromSwiftType => ContainerTypeName;

    /// <summary>
    /// Builds the container creation statements (key/value conversion + SwiftDictionary.FromDictionary)
    /// without PayloadBuffer extraction.
    /// </summary>
    private (List<MarshalStatement> setup, string containerExpr) BuildContainerSetup(string paramName)
    {
        var rawK = _keyProjection.SwiftContainerGenericType;
        var rawV = _valueProjection.SwiftContainerGenericType;
        var keyConv = _keyProjection.GetParameterElementConversion("kvp.Key");
        var valConv = _valueProjection.GetParameterElementConversion("kvp.Value");
        var needsConversion = keyConv != null || valConv != null;
        var setup = new List<MarshalStatement>();

        if (needsConversion)
        {
            var keyExpr = keyConv ?? "kvp.Key";
            var valExpr = valConv ?? "kvp.Value";

            setup.Add(new MarshalStatement.Line(
                $"var {paramName}Converted = {paramName}.Select(kvp => new KeyValuePair<{rawK}, {rawV}>({keyExpr}, {valExpr})).ToList();"));
            setup.Add(new MarshalStatement.Line(
                $"SwiftDictionary<{rawK}, {rawV}> {paramName}SwiftInner;"));

            var tryBody = new List<MarshalStatement>
            {
                new MarshalStatement.Line(
                    $"{paramName}SwiftInner = SwiftDictionary<{rawK}, {rawV}>.FromDictionary({paramName}Converted);")
            };

            var finallyBody = new List<MarshalStatement>();
            if (_keyProjection.ElementRequiresDisposal)
            {
                finallyBody.Add(new MarshalStatement.Line(
                    $"foreach (var _item in {paramName}Converted) _item.Key.Dispose();"));
            }
            if (_valueProjection.ElementRequiresDisposal)
            {
                finallyBody.Add(new MarshalStatement.Line(
                    $"foreach (var _item in {paramName}Converted) _item.Value.Dispose();"));
            }

            if (finallyBody.Count > 0)
            {
                setup.Add(new MarshalStatement.Block("try", tryBody));
                setup.Add(new MarshalStatement.Block("finally", finallyBody));
            }
            else
            {
                setup.AddRange(tryBody);
            }

            return (setup, $"{paramName}SwiftInner");
        }
        else
        {
            setup.Add(new MarshalStatement.Line(
                $"var {paramName}SwiftDirect = SwiftDictionary<{rawK}, {rawV}>.FromDictionary({paramName});"));
            return (setup, $"{paramName}SwiftDirect");
        }
    }

    public MarshalPlan GetParameterPlan(string paramName)
    {
        // ObjC bridge path: create NSDictionary from elements and pass ObjC handle
        if (UsesObjCContainerBridge)
            return BuildObjCBridgeParameterPlan(paramName);

        var (setup, containerExpr) = BuildContainerSetup(paramName);

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
        // ObjC bridge: convert NSDictionary handle to typed dictionary (used by OptionalProjection)
        if (UsesObjCContainerBridge)
            return BuildObjCBridgeReturnExpression(containerVar);

        var keyConv = _keyProjection.GetReturnElementConversion("k");
        var valConv = _valueProjection.GetReturnElementConversion("v");
        return $"{containerVar}{BuildAsProjected(keyConv, valConv)}";
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        // ObjC bridge path: IntPtr is an NSDictionary handle — extract typed key-value pairs
        if (UsesObjCContainerBridge)
            return BuildObjCBridgeReturnPlan(resultName, strategy);

        // Use MarshalFromSwiftType for return — classes/non-frozen structs need the real type name
        var rawK = _keyProjection.MarshalFromSwiftType;
        var rawV = _valueProjection.MarshalFromSwiftType;
        var keyConv = _keyProjection.GetReturnElementConversion("k");
        var valConv = _valueProjection.GetReturnElementConversion("v");

        var asProjected = BuildAsProjected(keyConv, valConv);

        return strategy switch
        {
            ReturnStrategy.Direct => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwiftObject<SwiftDictionary<{rawK}, {rawV}>>(new IntPtr(&{resultName})){asProjected}",
                RequiresUnsafe = true
            },
            ReturnStrategy.IndirectResult => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwiftObject<SwiftDictionary<{rawK}, {rawV}>>({resultName}){asProjected}"
            },
            ReturnStrategy.OutBuffer => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwiftObject<SwiftDictionary<{rawK}, {rawV}>>({resultName}){asProjected}"
            },
            ReturnStrategy.AsyncCallback => MarshalPlan.PassThrough(resultName),
            _ => MarshalPlan.PassThrough(resultName)
        };
    }

    /// <summary>
    /// Builds the AsProjected call matching SwiftDictionary runtime API overloads:
    /// - Value-only: AsProjected(Func&lt;TValue,TResult&gt; valueSelector)
    /// - Key+value: AsProjected(Func&lt;TKey,TResultKey&gt; keySelector, Func&lt;TResultKey,TKey&gt; reverseKeySelector, Func&lt;TValue,TResultValue&gt; valueSelector)
    /// </summary>
    private string BuildAsProjected(string? keyConv, string? valConv)
    {
        if (keyConv != null)
        {
            var reverseKeyConv = _keyProjection.GetParameterElementConversion("k") ?? "k";
            var valSelector = valConv != null ? $"v => {valConv}" : "v => v";
            return $".AsProjected(k => {keyConv}, k => {reverseKeyConv}, {valSelector})";
        }
        if (valConv != null)
        {
            return $".AsProjected(v => {valConv})";
        }
        return ".AsProjected(v => v)";
    }

    /// <summary>
    /// Element-level conversion for when this Dictionary appears inside a container (e.g., Array&lt;Dictionary&gt;).
    /// Converts SwiftDictionary&lt;K,V&gt; → IDictionary&lt;PublicK,PublicV&gt; via .ToDictionary() with
    /// key/value conversion lambdas, using explicit public type casts for invariant Dictionary covariance.
    /// </summary>
    public string? GetReturnElementConversion(string elementVar)
    {
        var keyConv = _keyProjection.GetReturnElementConversion("kvp.Key");
        var valConv = _valueProjection.GetReturnElementConversion("kvp.Value");
        var keyExpr = keyConv ?? "kvp.Key";
        var valueExpr = valConv ?? "kvp.Value";
        var keyPubType = _keyProjection.PublicType;
        var valPubType = _valueProjection.PublicType;
        return $"{elementVar}.ToDictionary(kvp => ({keyPubType}){keyExpr}, kvp => ({valPubType}){valueExpr})";
    }

    public bool ElementRequiresDisposal => !UsesObjCContainerBridge;

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);

    // --- ObjC bridge helpers ---

    /// <summary>
    /// Converts a C# element to an NSObject for NSDictionary construction.
    /// For nested containers (e.g., [String: [URL]]), recursively converts inner collections
    /// to their ObjC counterparts before casting to NSObject.
    /// </summary>
    internal static string ToNSObject(ITypeProjection projection, string elementVar)
    {
        if (projection is ObjCBridgeableProjection)
            return elementVar; // Already NSObject (NSUrl IS NSObject)
        if (projection is StringProjection)
            return $"new Foundation.NSString({elementVar})";
        if (projection is DataProjection)
            return $"Foundation.NSData.FromArray({elementVar})";
        // Nested containers: convert inner collection to ObjC counterpart first
        if ((projection is ArrayProjection or SetProjection or DictionaryProjection)
            && projection.UsesObjCContainerBridge)
        {
            var innerConv = projection.GetParameterElementConversion(elementVar);
            if (innerConv != null)
                return $"(Foundation.NSObject){innerConv}";
        }
        return $"(Foundation.NSObject){elementVar}";
    }

    /// <summary>
    /// Converts an NSObject from NSDictionary to the C# typed element.
    /// </summary>
    private static string FromNSObject(ITypeProjection projection, string nsObjectVar)
    {
        if (projection is ObjCBridgeableProjection bridgeable)
            return MarshallingHelpers.FormatObjCBridgeCall(bridgeable.PublicType, $"{nsObjectVar}.Handle", nonNull: true);
        if (projection is StringProjection)
            return $"{nsObjectVar}.ToString()";
        if (projection is DataProjection)
            return $"((Foundation.NSData){nsObjectVar}).ToArray()";
        return $"({projection.PublicType}){nsObjectVar}";
    }

    /// <summary>
    /// ObjC bridge parameter plan: create NSDictionary from C# key-value pairs.
    /// </summary>
    private MarshalPlan BuildObjCBridgeParameterPlan(string paramName)
    {
        var keyToNS = ToNSObject(_keyProjection, "kvp.Key");
        var valToNS = ToNSObject(_valueProjection, "kvp.Value");
        var setup = new List<MarshalStatement>
        {
            new MarshalStatement.Line(
                $"var {paramName}Pairs = {paramName}.ToArray();"),
            new MarshalStatement.Line(
                $"var {paramName}Keys = {paramName}Pairs.Select(kvp => {keyToNS}).ToArray();"),
            new MarshalStatement.Line(
                $"var {paramName}Values = {paramName}Pairs.Select(kvp => {valToNS}).ToArray();"),
            new MarshalStatement.Line(
                $"using var {paramName}NSDict = Foundation.NSDictionary.FromObjectsAndKeys({paramName}Values, {paramName}Keys);"),
            new MarshalStatement.Line(
                $"IntPtr {paramName}Buffer = {paramName}NSDict.Handle;")
        };
        return new MarshalPlan
        {
            SetupStatements = setup,
            PInvokeExpression = $"{paramName}Buffer"
        };
    }

    /// <summary>
    /// ObjC bridge return plan: receive NSDictionary handle, extract typed key-value pairs.
    /// </summary>
    private MarshalPlan BuildObjCBridgeReturnPlan(string resultName, ReturnStrategy strategy)
    {
        return new MarshalPlan
        {
            SetupStatements = new List<MarshalStatement>
            {
                new MarshalStatement.Line(
                    $"var {resultName}NSDict = ObjCRuntime.Runtime.GetNSObject<Foundation.NSDictionary>({resultName})!;"),
                new MarshalStatement.Line(
                    $"var {resultName}Dict = new System.Collections.Generic.Dictionary<{_keyProjection.PublicType}, {_valueProjection.PublicType}>((int){resultName}NSDict.Count);"),
                new MarshalStatement.Line(
                    $"foreach (var _nsKey in {resultName}NSDict.Keys) {resultName}Dict[{FromNSObject(_keyProjection, "_nsKey")}] = {FromNSObject(_valueProjection, $"{resultName}NSDict.ObjectForKey(_nsKey)!")};")
            },
            PInvokeExpression = $"{resultName}Dict"
        };
    }

    /// <summary>
    /// Builds the ObjC bridge return expression for use by OptionalProjection.
    /// The containerVar parameter is the IntPtr handle to the NSDictionary.
    /// </summary>
    private string BuildObjCBridgeReturnExpression(string containerVar)
    {
        var keyConv = FromNSObject(_keyProjection, "_nsKey");
        var valConv = FromNSObject(_valueProjection, $"_nsDict.ObjectForKey(_nsKey)!");
        // Inline dictionary construction using LINQ
        return $"((Func<IReadOnlyDictionary<{_keyProjection.PublicType}, {_valueProjection.PublicType}>>)(() => {{ " +
               $"var _nsDict = ObjCRuntime.Runtime.GetNSObject<Foundation.NSDictionary>({containerVar})!; " +
               $"var _dict = new System.Collections.Generic.Dictionary<{_keyProjection.PublicType}, {_valueProjection.PublicType}>((int)_nsDict.Count); " +
               $"foreach (var _nsKey in _nsDict.Keys) _dict[{keyConv}] = {valConv}; " +
               $"return _dict; }}))()";
    }
}
