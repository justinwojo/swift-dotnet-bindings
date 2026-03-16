// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for protocol existential types (any Protocol).
/// Three-tier resolution:
///   1. Well-known protocols (Swift.Error) → named type (AnyError)
///   2. Known protocols with proxy → IProtocol interface
///   3. Unknown protocols → object
///
/// Parameter direction: extract ExistentialContainer via ISwiftExistentialConvertible.
/// Return direction: wrap container in proxy class or well-known type.
/// </summary>
public class ExistentialProjection : ITypeProjection
{
    private readonly string _containerType;
    private readonly string _publicType;
    private readonly string? _proxyClassName;

    /// <summary>
    /// Creates an existential projection.
    /// </summary>
    /// <param name="containerType">The runtime container type (e.g., "ExistentialContainer1").</param>
    /// <param name="publicType">The public C# type (e.g., "IImageProcessing", "AnyError", "object").</param>
    /// <param name="proxyClassName">The proxy class name for known protocols, or null for well-known/object.</param>
    public ExistentialProjection(string containerType, string publicType, string? proxyClassName)
    {
        _containerType = containerType;
        _publicType = publicType;
        _proxyClassName = proxyClassName;
    }

    public string PublicType => _publicType;
    public string PInvokeType => _containerType;
    public string? PInvokeAttribute => null;

    public MarshalPlan GetParameterPlan(string paramName)
    {
        // GetOrCreate only works for single-protocol existentials (EC1) with proxy classes.
        // - EC0 (Any/AnyError): AnyError is a value type, can't satisfy class constraint
        // - EC2+ (compositions): GetOrCreate returns EC1 but P/Invoke expects EC2+
        // - No proxy (well-known/object): always implement ISwiftExistentialConvertible directly
        var expr = _proxyClassName != null && _containerType == "Swift.Runtime.ExistentialContainer1"
            ? $"ExistentialContainerFactory.GetOrCreate<{_publicType}>({paramName})"
            : $"((ISwiftExistentialConvertible<{_containerType}>){paramName}).GetExistentialContainer()";

        return new MarshalPlan
        {
            PInvokeExpression = expr
        };
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        var expression = _proxyClassName != null
            ? $"new {_proxyClassName}({resultName})"
            : _publicType == "object"
                ? resultName
                : $"new {_publicType}({resultName})";

        return new MarshalPlan
        {
            PInvokeExpression = expression
        };
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public string? GetParameterElementConversion(string elementVar) =>
        _proxyClassName != null && _containerType == "Swift.Runtime.ExistentialContainer1"
            ? $"ExistentialContainerFactory.GetOrCreate<{_publicType}>({elementVar})"
            : $"((ISwiftExistentialConvertible<{_containerType}>){elementVar}).GetExistentialContainer()";

    public string? GetReturnElementConversion(string elementVar) =>
        _proxyClassName != null
            // Cast to interface type for invariant container compatibility (IReadOnlyDictionary<K,V>
            // is invariant in V, so Func<EC, Proxy> won't match Func<EC, IProtocol>).
            ? $"({_publicType})new {_proxyClassName}({elementVar})"
            : _publicType == "object"
                ? $"(object){elementVar}"
                : $"new {_publicType}({elementVar})";

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
