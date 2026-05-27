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
    private readonly bool _isBareAny;

    /// <summary>
    /// Creates an existential projection.
    /// </summary>
    /// <param name="containerType">The runtime container type (e.g., "ExistentialContainer1").</param>
    /// <param name="publicType">The public C# type (e.g., "IImageProcessing", "AnyError", "object").</param>
    /// <param name="proxyClassName">The proxy class name for known protocols, or null for well-known/object.</param>
    /// <param name="isBareAny">True if this represents bare 'Any' (0 protocols), enabling Box/Unbox marshalling.</param>
    public ExistentialProjection(string containerType, string publicType, string? proxyClassName, bool isBareAny = false)
    {
        _containerType = containerType;
        _publicType = publicType;
        _proxyClassName = proxyClassName;
        _isBareAny = isBareAny;
    }

    public string PublicType => _publicType;
    public string PInvokeType => _containerType;
    public string? PInvokeAttribute => null;

    // The owned-return ctor argument, emitted for both single-protocol (EC1) and composition
    // (EC2+) proxies that expose the ownership-aware ctor. The proxy adopts the +1 and releases
    // the container's one conforming value via the existential's own metadata on Dispose/finalize.
    private string OwnsContainerArg =>
        ExistentialHandler.IsOwnedExistentialContainerType(_containerType) ? ", ownsContainer: true" : string.Empty;

    public MarshalPlan GetParameterPlan(string paramName)
    {
        string expr;
        if (_isBareAny)
        {
            expr = $"ExistentialContainer0.Box({paramName})";
        }
        else
        {
            // GetOrCreate only works for single-protocol existentials (EC1) with proxy classes.
            // - EC0 (bare Any): a value type, can't satisfy the class constraint (well-known AnyError
            //   is a reference type and takes the "no proxy" direct-convertible path below)
            // - EC2+ (compositions): GetOrCreate returns EC1 but P/Invoke expects EC2+
            // - No proxy (well-known/object): always implement ISwiftExistentialConvertible directly
            //
            // When a proxy class is known, pass a wrap fallback so plain C# implementations of
            // the interface are automatically wrapped in the proxy (users don't have to construct
            // the hidden {Protocol}Proxy manually).
            expr = _proxyClassName != null && _containerType == "Swift.Runtime.ExistentialContainer1"
                ? $"ExistentialContainerFactory.GetOrCreate<{_publicType}>({paramName}, static __v => new {_proxyClassName}(__v))"
                : $"((ISwiftExistentialConvertible<{_containerType}>){paramName}).GetExistentialContainer()";
        }

        return new MarshalPlan
        {
            PInvokeExpression = expr
        };
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        string expression;
        if (_isBareAny)
        {
            expression = $"ExistentialContainer0.Unbox({resultName})";
        }
        else
        {
            expression = _proxyClassName != null
                // Owned return: Swift transfers the existential at +1, so the proxy adopts
                // the container and releases it on Dispose/finalize (ownsContainer: true).
                // Only single-protocol (EC1) proxies expose the ownership-aware ctor;
                // multi-protocol composition proxies (EC2+, emitted by ModuleHandler with an
                // empty Dispose) are a separate release mechanism and keep their 1-arg ctor.
                ? $"new {_proxyClassName}({resultName}{OwnsContainerArg})"
                : _publicType == "object"
                    ? resultName
                    // Well-known no-proxy existential (Swift.Foundation.AnyError): an owned +1
                    // transfer, so the wrapper adopts the boxed error and releases it on
                    // Dispose/finalize. The helper is a no-op for any other well-known type.
                    : $"new {_publicType}({resultName}{ExistentialHandler.WellKnownOwnedTransferArg(_publicType)})";
        }

        return new MarshalPlan
        {
            PInvokeExpression = expression
        };
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public string? GetParameterElementConversion(string elementVar) =>
        _isBareAny
            ? $"ExistentialContainer0.Box({elementVar})"
            : _proxyClassName != null && _containerType == "Swift.Runtime.ExistentialContainer1"
                ? $"ExistentialContainerFactory.GetOrCreate<{_publicType}>({elementVar}, static __v => new {_proxyClassName}(__v))"
                : $"((ISwiftExistentialConvertible<{_containerType}>){elementVar}).GetExistentialContainer()";

    // Non-owning by design: this element conversion is reused by BOTH owned collection-element
    // returns AND borrowed Swift->C# receiver-callback parameter wraps
    // (GetReceiverExistentialSetterConversion). A receiver parameter is +0 guaranteed — Swift
    // retains ownership and MarshalFromSwift bitwise-reads the container without a retain — so
    // adopting it would run a value-witness Destroy on storage Swift still owns (over-release /
    // UAF). Owned scalar returns balance their +1 through GetReturnPlan; owned OPTIONAL existential
    // returns use GetOwnedReturnElementConversion below. The owned collection-element +1 stays a
    // pre-existing deferred wire-carrier gap pending per-collection copy-then-destroy verification.
    public string? GetReturnElementConversion(string elementVar) =>
        _isBareAny
            ? $"ExistentialContainer0.Unbox({elementVar})"
            : _proxyClassName != null
                // Cast to interface type for invariant container compatibility (IReadOnlyDictionary<K,V>
                // is invariant in V, so Func<EC, Proxy> won't match Func<EC, IProtocol>).
                ? $"({_publicType})new {_proxyClassName}({elementVar})"
                : _publicType == "object"
                    ? $"(object){elementVar}"
                    : $"new {_publicType}({elementVar})";

    /// <summary>
    /// Owned-return variant of <see cref="GetReturnElementConversion"/>: the proxy ADOPTS a
    /// Swift-returned existential at +1 (read out of an sret/out buffer that is then raw-freed,
    /// so the only surviving retain lives in the proxy) and releases it on Dispose/finalize.
    /// Used only by owned OPTIONAL existential returns (<c>OptionalProjection</c>); the borrowed
    /// receiver-callback path keeps the non-owning <see cref="GetReturnElementConversion"/>.
    /// Falls back to the non-owning form for bare-<c>any</c>/no-proxy and non-EC1 containers
    /// (<see cref="OwnsContainerArg"/> is empty for those), which have no single-proxy +1 to adopt.
    /// </summary>
    public string? GetOwnedReturnElementConversion(string elementVar) =>
        !_isBareAny && _proxyClassName != null
            ? $"({_publicType})new {_proxyClassName}({elementVar}{OwnsContainerArg})"
            : GetReturnElementConversion(elementVar);

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
