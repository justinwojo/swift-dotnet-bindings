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
    private readonly bool _isClassBoundArity1;

    /// <summary>
    /// Creates an existential projection.
    /// </summary>
    /// <param name="containerType">The runtime container type (e.g., "ExistentialContainer1").</param>
    /// <param name="publicType">The public C# type (e.g., "IImageProcessing", "AnyError", "object").</param>
    /// <param name="proxyClassName">The proxy class name for known protocols, or null for well-known/object.</param>
    /// <param name="isBareAny">True if this represents bare 'Any' (0 protocols), enabling Box/Unbox marshalling.</param>
    /// <param name="isClassBoundArity1">
    /// True when the existential is a single class-bound (superclass-/AnyObject-constrained) protocol.
    /// Such existentials carry the 16-byte <c>ClassExistentialContainer1</c> stride when read out of a
    /// Swift array; see <see cref="ArrayElementCarrierType"/>. The single-value and parameter paths keep
    /// <paramref name="containerType"/> (the opaque <c>ExistentialContainer1</c> the proxy implements).
    /// </param>
    public ExistentialProjection(string containerType, string publicType, string? proxyClassName, bool isBareAny = false, bool isClassBoundArity1 = false)
    {
        _containerType = containerType;
        _publicType = publicType;
        _proxyClassName = proxyClassName;
        _isBareAny = isBareAny;
        _isClassBoundArity1 = isClassBoundArity1;
    }

    public string PublicType => _publicType;
    public string PInvokeType => _containerType;
    public string? PInvokeAttribute => null;

    // Class-bound existentials are read out of Swift arrays at a 16-byte [classRef][witnessTable]
    // stride. The opaque ExistentialContainer1 carrier (40 bytes) over-reads and crashes on the first
    // index, so the SwiftArray<T> element type must be the 16-byte ClassExistentialContainer1. The
    // single-value and parameter paths stay on _containerType (the interface the proxy implements);
    // the array wrap lambda new {Proxy}(e) bridges via the implicit ClassExistentialContainer1 →
    // ExistentialContainer1 conversion.
    // ExistentialProjection does not override MarshalFromSwiftType, so it resolves to _containerType
    // (MarshalFromSwiftType → SwiftContainerGenericType → PInvokeType → _containerType); use that
    // directly here since the interface default member isn't in scope by name.
    public string ArrayElementCarrierType =>
        _isClassBoundArity1 && _proxyClassName != null
            ? "Swift.Runtime.ClassExistentialContainer1"
            : _containerType;

    /// <summary>
    /// True when this is a single class-bound (superclass-/AnyObject-constrained) existential with a
    /// proxy, i.e. one whose array READ carrier (<see cref="ArrayElementCarrierType"/>) is the 16-byte
    /// <c>ClassExistentialContainer1</c> rather than the 40-byte opaque <c>ExistentialContainer1</c>.
    /// Accessor return-type selection uses this to route only class-bound existential arrays through the
    /// projection carrier, leaving every other array accessor on the legacy translation unchanged.
    /// </summary>
    public bool IsClassBoundArity1 => _isClassBoundArity1 && _proxyClassName != null;

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
                // Both single-protocol (EC1) and multi-protocol composition (EC2+, emitted by
                // ModuleHandler) proxies expose this ownership-aware ctor and run a real
                // Dispose + finalizer; OwnsContainerArg is gated on the container TYPE, not
                // the protocol count.
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

    /// <summary>
    /// keepAlive-capturing variant of <see cref="GetParameterElementConversion"/> for the +0 BORROWED
    /// closure-ARGUMENT direction (design change 4 / mechanism 3). A C# lambda that wraps a Swift
    /// function pointer passes an <c>any P</c> argument by value into the native call, where Swift
    /// borrows it (+0) for the call's duration. The EC1 aliases the auto-wrapped proxy's sole
    /// construction +1 (R0); under Design B2's weak proxy registration nothing strong roots that proxy
    /// while Swift runs, so a GC between the wrap and Swift's borrow could finalize the proxy → release
    /// R0 → UAF mid-call. This emits the keepAlive <c>GetOrCreate</c> overload, binding the proxy into
    /// <paramref name="keepAliveVar"/> so the marshalling site can emit <c>GC.KeepAlive({keepAliveVar})</c>
    /// AFTER the native call returns (by which point Swift has finished borrowing). There is no container
    /// temp to own a +1 here (the arg is borrowed, not stored), so an owned mint would leak — keepAlive
    /// is the correct fence (contrast <see cref="GetArrayElementCarrierConversion"/>'s owned-element
    /// mint, whose collection carrier DOES own a +1). Returns <c>null</c> for bare <c>Any</c> (a fresh
    /// owned EC0 box, no proxy), EC2+ composition, and no-proxy well-known existentials — none of which
    /// have a <c>GetOrCreate</c> keepAlive overload. (EC2+ composition still BORROW-aliases its proxy's
    /// R0, but via <see cref="GetParameterElementConversion"/>'s <c>GetExistentialContainer()</c> form,
    /// not <c>GetOrCreate</c>.) The sole caller is <c>ClosureProjection</c>'s lambda-builder, which is
    /// dead code in live closure emission — closures are diverted to the string-emitter
    /// <c>ClosureEmitter</c> before any projection is built — so the live EC2+ closure-arg keepAlive is
    /// emitted by <c>ClosureEmitter.GetSwiftInvokeArgExpression</c>, not through this projection path.
    /// </summary>
    public string? GetKeepAliveParameterElementConversion(string elementVar, string keepAliveVar) =>
        !_isBareAny && _proxyClassName != null && _containerType == "Swift.Runtime.ExistentialContainer1"
            ? $"ExistentialContainerFactory.GetOrCreate<{_publicType}>({elementVar}, static __v => new {_proxyClassName}(__v), out _, out var {keepAliveVar})"
            : null;

    /// <summary>
    /// Owned (+1) C#→Swift counterpart to <see cref="GetParameterElementConversion"/> (which borrows).
    /// A reverse-dispatch getter/method that RETURNS <c>any P</c> hands Swift a +1-owned existential:
    /// the C# thunk writes the container into a buffer (<c>MarshalToSwiftBuffer</c>, a byte-copy that
    /// does NOT retain) and Swift loads + owns it after the thunk returns. Borrowing the proxy's
    /// construction +1 (R0) via <see cref="GetParameterElementConversion"/> would (a) under B2's weak
    /// proxy registration let a GC release R0 before Swift loads, and (b) over-release once the proxy
    /// finalizes against the +1 Swift now owns. So mint an independent +1 via
    /// <see cref="Swift.Runtime.ExistentialContainerFactory.CreateOwnedExistential1"/> — the scalar EC1
    /// sibling of <see cref="GetArrayElementCarrierConversion"/>'s owned element mint, which the
    /// array/dictionary getter-return arms already use. Bare-<c>Any</c> (EC0) boxes a fresh owned +1
    /// already. EC2+ composition (<c>any P &amp; Q…</c>) mints through the always-mint
    /// <see cref="Swift.Runtime.ExistentialContainerFactory.CreateOwnedCompositionExistential{TProtocol,TContainer}"/>:
    /// the only conformer is a Swift-vended proxy whose <c>GetExistentialContainer()</c> borrows, so the
    /// raw bytes would alias the proxy's sole +1 (no <c>BoxAsExistential2</c> donate arm exists). The
    /// borrowed <c>GetExistentialContainer()</c> form remains only for no-proxy well-known/object
    /// existentials, which carry their own self-owning release.
    ///
    /// NOT to be confused with the owned Swift→C# <see cref="GetOwnedReturnElementConversion"/>: that
    /// ADOPTS a Swift-returned container INTO a C# proxy (<c>new Proxy(container, ownsContainer:true)</c>),
    /// the inverse transform. "Parameter" here = the C#→Swift hand-off direction (the getter-return
    /// projection is built with <c>IsParameter = true</c>); "Return" there = the Swift→C# read direction.
    /// </summary>
    public string? GetOwnedParameterElementConversion(string elementVar) =>
        _isBareAny
            ? $"ExistentialContainer0.Box({elementVar})"
            : _proxyClassName != null && _containerType == "Swift.Runtime.ExistentialContainer1"
                ? $"Swift.Runtime.ExistentialContainerFactory.CreateOwnedExistential1<{_publicType}>({elementVar}, static __v => new {_proxyClassName}(__v))"
                : _proxyClassName != null && ExistentialHandler.IsOwnedExistentialContainerType(_containerType)
                    ? $"Swift.Runtime.ExistentialContainerFactory.CreateOwnedCompositionExistential<{_publicType}, {_containerType}>({elementVar})"
                    : $"((ISwiftExistentialConvertible<{_containerType}>){elementVar}).GetExistentialContainer()";

    /// <summary>
    /// Per-element conversion for the PARAMETER/WRITE direction of a <c>[any P]</c> array (and the
    /// receiver-getter write path) — the symmetric counterpart to <see cref="ArrayElementCarrierType"/>,
    /// which fixed only the READ stride. For a class-bound single-protocol existential the Swift array
    /// strides over the 16-byte <c>ClassExistentialContainer1</c> (the inverse of
    /// <c>ClassExistentialContainer1.ReadHeapCell</c> on the read side), so this routes the element
    /// through <see cref="Swift.Runtime.ExistentialContainerFactory.CreateOwnedClassCarrier"/>: the
    /// Swift array write is <c>__owned</c> (consuming) and its class-existential value-witness table
    /// releases word0 on destroy, so the carrier must OWN exactly one +1 — minted for the borrowed
    /// proxy/auto-wrap path, donated for the boxable conformer path (whose <c>Create</c> already
    /// produced a fresh +1 that would otherwise leak). For every other existential (opaque arity-1,
    /// composition, bare Any) <see cref="ArrayElementCarrierType"/> is the opaque container, so this
    /// returns the unchanged per-element conversion — a no-op outside the class-bound case.
    /// </summary>
    public string? GetArrayElementCarrierConversion(string elementVar)
    {
        if (IsClassBoundArity1)
        {
            // Class-bound [any P] element: hand the Swift array a 16-byte carrier that owns exactly
            // one +1 on its class ref. CreateOwnedClassCarrier consults GetOrCreate's ownership
            // signal — mint for borrowed (proxy/auto-wrap), donate for boxable — so the consuming
            // __owned append and the VWT destroy balance for BOTH layouts. The bare
            // FromExistentialContainer1 narrowing used previously over-released the proxy (it
            // aliased the proxy's only +1) and leaked the boxable conformer's +1.
            return $"Swift.Runtime.ExistentialContainerFactory.CreateOwnedClassCarrier<{_publicType}>({elementVar}, static __v => new {_proxyClassName}(__v))";
        }

        // Opaque single-protocol existential with a proxy: the 40-byte EC1 carrier write is ALSO
        // __owned (the array/dict existential value-witness table destroys each element on teardown),
        // so the carrier must own its +1 — minted for the borrowed proxy/auto-wrap path, donated for
        // the boxable conformer path — by CreateOwnedExistential1 (the opaque sibling of
        // CreateOwnedClassCarrier). The bare GetParameterElementConversion below aliased the proxy's
        // only +1, which the __owned consume plus the carrier's value-witness destroy over-released
        // (opaque sibling: owned-element over-release). Mirrors the EC1 condition in GetParameterElementConversion.
        if (_proxyClassName != null && _containerType == "Swift.Runtime.ExistentialContainer1")
        {
            return $"Swift.Runtime.ExistentialContainerFactory.CreateOwnedExistential1<{_publicType}>({elementVar}, static __v => new {_proxyClassName}(__v))";
        }

        return GetParameterElementConversion(elementVar);
    }

    // Non-owning by design: this element conversion is reused by borrowed reads — non-owned forward
    // returns AND the +0 borrowed SCALAR receiver-callback arms (the standalone-existential and
    // Optional<existential> arms of GetReceiverExistentialSetterConversion). A scalar receiver
    // parameter is +0 guaranteed — Swift retains ownership and MarshalFromSwift bitwise-reads the
    // container without a retain — so adopting it would run a value-witness Destroy on storage Swift
    // still owns (over-release / UAF). The receiver-callback COLLECTION arms (array/dict element
    // reads) do NOT use this form: they materialize via a +1 move-out (subscript getter
    // InitializeWithCopy / entry-enumerator MarshalMovedValueFromSlot), so they route the existential
    // leaf through GetOwnedReturnElementConversion (ownsContainer: true) below to adopt+release that
    // moved-out +1 — see ProtocolProxyEmitter.Receivers.GetReceiver*Conversion, gated by
    // ClassBoundExistentialCollectionLeakProbeTests. Owned scalar returns balance their +1 through
    // GetReturnPlan; owned OPTIONAL existential and owned EC1 collection-element returns use
    // GetOwnedReturnElementConversion below. Non-EC1 collection leaves (opaque / composition /
    // bare-any) fall back to this non-owning form there (OwnsContainerArg empty) — the per-collection
    // copy-then-destroy case still pending verification (see GetArrayElementCarrierConversion).
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
    /// Falls back to the non-owning form for bare-<c>any</c>/no-proxy containers, which have no
    /// single-proxy +1 to adopt. For a proxy-backed container <see cref="OwnsContainerArg"/> is
    /// gated on the container TYPE (EC1 through EC8, via
    /// <see cref="ExistentialHandler.IsOwnedExistentialContainerType"/>), not on protocol count —
    /// so composition (EC2+) proxies adopt the +1 here exactly like single-protocol (EC1) ones.
    /// </summary>
    public string? GetOwnedReturnElementConversion(string elementVar) =>
        !_isBareAny && _proxyClassName != null
            ? $"({_publicType})new {_proxyClassName}({elementVar}{OwnsContainerArg})"
            : GetReturnElementConversion(elementVar);

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
