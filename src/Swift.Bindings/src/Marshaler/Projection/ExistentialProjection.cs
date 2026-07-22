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
    private readonly bool _proxyIsSuppressed;
    private readonly bool _isObjCExistential;

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
    /// <param name="proxyIsSuppressed">
    /// True when <paramref name="proxyClassName"/> names a proxy whose EveryProtocol conformance was NOT
    /// emitted. The CONSUME arms (parameter/element wrap fallbacks) then drop the
    /// <c>static __v =&gt; new {Proxy}(__v)</c> lambda and emit the no-fallback overload (the member stays,
    /// only Swift-vended conformers round-trip); the PRODUCE arms (return constructions) throw
    /// <see cref="SuppressedProxyReferenceException"/> so the member-emit boundary stubs the whole member.
    /// This is the emit-time replacement for the retired CoGater proxy-reference post-pass on the
    /// projection path. Always false unless <paramref name="proxyClassName"/> is non-null.
    /// </param>
    /// <param name="isObjCExistential">
    /// True when the single protocol is declared <c>@objc</c>. Such an existential's ABI is a single
    /// 8-byte ObjC object pointer (no witness table, no <c>…Mp</c> descriptor), so the wire
    /// representation is a bare <c>IntPtr</c> (nil = <c>IntPtr.Zero</c>, unknown-object ARC) and the
    /// public surface stays the proxy interface — never an <c>ExistentialContainerN</c> carrier.
    /// Mutually exclusive in practice with <paramref name="isClassBoundArity1"/> (the @objc predicate
    /// is keyed on the <c>ObjCProtocol</c> flag, which routes off the class-bound-container path).
    /// </param>
    public ExistentialProjection(string containerType, string publicType, string? proxyClassName, bool isBareAny = false, bool isClassBoundArity1 = false, bool proxyIsSuppressed = false, bool isObjCExistential = false)
    {
        _containerType = containerType;
        _publicType = publicType;
        _proxyClassName = proxyClassName;
        _isBareAny = isBareAny;
        _isClassBoundArity1 = isClassBoundArity1;
        _proxyIsSuppressed = proxyIsSuppressed;
        _isObjCExistential = isObjCExistential;
    }

    public string PublicType => _publicType;
    public string PInvokeType => _isObjCExistential ? "IntPtr" : _containerType;
    public string? PInvokeAttribute => null;

    /// <summary>
    /// True when the single protocol is declared <c>@objc</c> — its existential marshals as a bare
    /// 8-byte object pointer (<c>IntPtr</c>), not an <c>ExistentialContainerN</c> carrier. Consumed by
    /// <see cref="OptionalProjection"/> to route <c>(any P)?</c> through the nullable-pointer ABI.
    /// </summary>
    public bool IsObjCExistential => _isObjCExistential;

    /// <summary>
    /// True when this existential leaf names a proxy whose EveryProtocol conformance was NOT emitted
    /// (see the <c>proxyIsSuppressed</c> constructor parameter). A CONSUME site that drops its
    /// <c>static __v =&gt; new {Proxy}(__v)</c> wrap fallback because of this is a degraded member — read
    /// this together with <see cref="SuppressedProxyName"/> to record the decline. Read-only projection
    /// of the private suppression state so a collection handler that owns the decl can walk the container
    /// sub-projections and classify a per-element consume-degrade (the leaf itself has no owning decl).
    /// </summary>
    public bool ConsumeProxyIsSuppressed => _proxyIsSuppressed;

    /// <summary>
    /// The suppressed proxy class name to name in a degraded-member report row, or <c>null</c> when this
    /// leaf's CONSUME arm never had a per-element wrap fallback to drop. Non-null ONLY for a suppressed
    /// single-protocol <see cref="ExistentialContainer1"/> proxy — the exact shape whose CONSUME arms emit
    /// the <c>static __v =&gt; new {Proxy}(__v)</c> fallback (see the EC1 gate on the container/element
    /// conversions). A live proxy, a well-known/<c>object</c> leaf, an existential union, and an
    /// EC2+/composition leaf (which marshals via <c>((ISwiftExistentialConvertible&lt;…&gt;)x).GetExistentialContainer()</c>
    /// with NO wrap fallback either way) each yield <c>null</c>, so a suppressed composition is not
    /// mis-recorded as a consume-degrade. Kept in lockstep with <see cref="SuppressedProxyTypeSpecWalk"/>'s
    /// EC1 gate so the projection walk and the TypeSpec walk report the same set.
    /// </summary>
    public string? SuppressedProxyName =>
        _proxyIsSuppressed && _containerType == "Swift.Runtime.ExistentialContainer1" ? _proxyClassName : null;

    /// <summary>
    /// C#→Swift parameter extraction for an <c>@objc</c> existential: read the underlying ObjC object
    /// pointer (the proxy's <c>SwiftHandle</c> = <c>_swiftContainer.Payload0</c>). The argument is rooted
    /// on the caller's stack across the synchronous call, so the +0 borrow needs no extra keepalive
    /// (same as <see cref="ClassProjection"/>). C# conformers that are not Swift-vended proxies are the
    /// unsupported reverse direction and fail closed here: an <c>@objc</c> protocol existential is a bare
    /// ObjC object pointer, and a plain managed type is not an ObjC object that responds to the protocol's
    /// selectors, so we cannot synthesize one. The <c>as … ?? throw</c> guard raises a self-describing
    /// <see cref="System.NotSupportedException"/> instead of a bare <c>InvalidCastException</c>. The
    /// <c>as</c> form (rather than a pattern variable) keeps the expression collision-free when a method
    /// has more than one <c>@objc</c> existential parameter.
    /// </summary>
    internal string GetObjCParameterExpression(string paramName) =>
        $"(({paramName} as Swift.Runtime.ISwiftObject) ?? throw new global::System.NotSupportedException(" +
        $"\"Cannot marshal a C# implementation of {_publicType} to Swift: an @objc protocol existential is a bare ObjC object pointer, so only a value vended by the Swift library round-trips. The reverse direction (a managed type conforming to an @objc protocol) is not supported.\"" +
        $")).SwiftHandle";

    /// <summary>
    /// True when a <em>plain managed</em> conformer flowing INTO an <c>@objc</c> existential parameter
    /// can be auto-wrapped into the generated EveryProtocol proxy — i.e. a proxy class is emitted for
    /// the protocol. When false (no proxy, or the proxy's EveryProtocol conformance was suppressed) only
    /// a Swift-vended conformer round-trips and the reverse direction stays fail-closed via
    /// <see cref="GetObjCParameterExpression"/>. Consumed by <see cref="OptionalProjection"/> / the
    /// wrapper emitter to decide whether to auto-wrap or keep the fail-closed guard.
    /// </summary>
    internal bool CanAutoWrapObjCConformer => _isObjCExistential && _proxyClassName != null && !_proxyIsSuppressed;

    /// <summary>
    /// C#→Swift statements assigning <paramref name="bufferVar"/> (an already-declared <c>IntPtr</c>) the
    /// bare <c>@objc</c> object pointer for the non-null value <paramref name="valueExpr"/>, and binding the
    /// object whose liveness must span Swift's borrow into <paramref name="keepAliveVar"/> (declared
    /// <c>object?</c> by the caller) so the marshalling site can <c>GC.KeepAlive</c> it past the native
    /// call. A Swift-vended conformer (already an <see cref="Swift.Runtime.ISwiftObject"/>) contributes the
    /// bare pointer read out of its live handle at +0 and is itself pinned into <paramref name="keepAliveVar"/>:
    /// once the raw pointer is extracted the wrapper is otherwise unreferenced, so without the pin the JIT
    /// could drop it and a concurrent GC could finalize it (releasing the object) mid-borrow. A plain managed
    /// conformer is auto-wrapped into the EveryProtocol proxy; the proxy's sole object pointer
    /// (<c>ExistentialContainer1.Payload0</c>, which the C#-impl proxy ctor sets to the EveryProtocol
    /// construction +1) is the wire value, and the freshly-built proxy is bound into
    /// <paramref name="keepAliveVar"/> — it is registered only weakly, so a GC between the wrap and the
    /// borrow could otherwise finalize it and release R0 mid-call (UAF). Either way the single post-call
    /// <c>GC.KeepAlive</c> covers the arm that ran. Only valid when <see cref="CanAutoWrapObjCConformer"/> is true.
    /// </summary>
    internal IReadOnlyList<MarshalStatement> GetObjCAutoWrapBufferStatements(string valueExpr, string bufferVar, string keepAliveVar)
    {
        string swiftObjTmp = $"{bufferVar}SwiftObj";
        string containerTmp = $"{bufferVar}Container";
        return new List<MarshalStatement>
        {
            new MarshalStatement.Block(
                $"if ({valueExpr} is Swift.Runtime.ISwiftObject {swiftObjTmp})",
                new List<MarshalStatement>
                {
                    new MarshalStatement.Line($"{bufferVar} = {swiftObjTmp}.SwiftHandle;"),
                    // Pin the Swift-vended wrapper too: the wire value is the bare object pointer read
                    // out of it, and after this the wrapper is otherwise unreferenced, so the JIT could
                    // drop it and let a concurrent GC finalize it (releasing the object) while Swift
                    // borrows the pointer. The post-call GC.KeepAlive({keepAlive}) then covers BOTH arms.
                    new MarshalStatement.Line($"{keepAliveVar} = {swiftObjTmp};"),
                }),
            new MarshalStatement.Block(
                "else",
                new List<MarshalStatement>
                {
                    new MarshalStatement.Line(
                        $"var {containerTmp} = Swift.Runtime.ExistentialContainerFactory.GetOrCreate<{_publicType}>({valueExpr}, static __v => new {_proxyClassName}(__v), out _, out {keepAliveVar});"),
                    new MarshalStatement.Line($"{bufferVar} = {containerTmp}.Payload0;"),
                }),
        };
    }

    /// <summary>
    /// Swift→C# return construction for an <c>@objc</c> existential: adopt the +1-owned object pointer
    /// returned by value into the proxy via <c>Payload0</c> (<c>ownsContainer: true</c> → released via
    /// unknown-object ARC on Dispose/finalize). The proxy's class-bound layout reads/releases Payload0
    /// only and never runs a value-witness destroy, so the otherwise-zero container is safe.
    /// </summary>
    /// <remarks>
    /// PRODUCE chokepoint for EVERY <c>@objc</c>-existential forward return: the scalar arm
    /// (<see cref="GetReturnPlan"/>'s early <c>@objc</c> branch) and the optional arm
    /// (<see cref="OptionalProjection"/>'s nullable <c>@objc</c> ternary, Direct + sret) both build their
    /// return expression HERE — neither routes through <see cref="GetReturnPlan"/>'s non-<c>@objc</c>
    /// suppressed-proxy throw. So the same fail-closed guard must live at this shared site, or a member
    /// returning <c>(any P)?</c> / <c>any P</c> for an <c>@objc</c> <c>P</c> whose EveryProtocol
    /// conformance was suppressed ships a <c>new {P}Proxy(…)</c> for a class that was never emitted (a
    /// dangling CS0246). Throwing rolls the partial body back at the member-emit boundary and re-stubs the
    /// whole member, exactly as the non-<c>@objc</c> PRODUCE path does in <see cref="GetReturnPlan"/>.
    /// </remarks>
    internal string GetObjCReturnExpression(string ptrExpr)
    {
        if (_proxyClassName != null && _proxyIsSuppressed)
            throw new SuppressedProxyReferenceException(_proxyClassName);

        return $"new {_proxyClassName}(new Swift.Runtime.ExistentialContainer1 {{ Payload0 = {ptrExpr} }}, ownsContainer: true)";
    }

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
        if (_isObjCExistential)
        {
            return new MarshalPlan { PInvokeExpression = GetObjCParameterExpression(paramName) };
        }

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
                ? _proxyIsSuppressed
                    // CONSUME: suppressed proxy → no wrap fallback; only Swift-vended conformers round-trip.
                    ? $"ExistentialContainerFactory.GetOrCreate<{_publicType}>({paramName})"
                    : $"ExistentialContainerFactory.GetOrCreate<{_publicType}>({paramName}, static __v => new {_proxyClassName}(__v))"
                : $"((ISwiftExistentialConvertible<{_containerType}>){paramName}).GetExistentialContainer()";
        }

        return new MarshalPlan
        {
            PInvokeExpression = expr
        };
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        if (_isObjCExistential)
        {
            // Swift returns the @objc existential as a +1-owned object pointer by value.
            return new MarshalPlan { PInvokeExpression = GetObjCReturnExpression(resultName) };
        }

        string expression;
        if (_isBareAny)
        {
            expression = $"ExistentialContainer0.Unbox({resultName})";
        }
        else
        {
            // PRODUCE: a suppressed proxy cannot back a `new {Proxy}(…)` return construction — throw so
            // the member-emit boundary rolls back and stubs the whole member (matching the retired CoGater body rewrite).
            if (_proxyClassName != null && _proxyIsSuppressed)
                throw new SuppressedProxyReferenceException(_proxyClassName);
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
                ? _proxyIsSuppressed
                    // CONSUME: suppressed proxy → no wrap fallback (see GetParameterPlan).
                    ? $"ExistentialContainerFactory.GetOrCreate<{_publicType}>({elementVar})"
                    : $"ExistentialContainerFactory.GetOrCreate<{_publicType}>({elementVar}, static __v => new {_proxyClassName}(__v))"
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
            ? _proxyIsSuppressed
                // CONSUME: suppressed proxy → no wrap fallback (see GetParameterPlan).
                ? $"ExistentialContainerFactory.GetOrCreate<{_publicType}>({elementVar}, out _, out var {keepAliveVar})"
                : $"ExistentialContainerFactory.GetOrCreate<{_publicType}>({elementVar}, static __v => new {_proxyClassName}(__v), out _, out var {keepAliveVar})"
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
            // An @objc existential produced to Swift is a single +1-owned bare object pointer, NOT a
            // 40-byte EC1 — so gate this BEFORE the EC1 arm (an @objc projection still carries
            // _containerType == ExistentialContainer1). Mint the owned class carrier and hand Swift its
            // bare word0 (ClassRef): CreateOwnedClassCarrier's Arc.UnknownObjectRetain balances the
            // __owned return whether word0 is an ObjC or native Swift class, and the receiver wraps this
            // in the SwiftOptional<IntPtr>/IntPtr carrier that takes the bare pointer. This is the
            // Swift-return (owned) inverse of GetObjCReturnExpression's Payload0 read.
            : _isObjCExistential && _proxyClassName != null
                ? _proxyIsSuppressed
                    // CONSUME: suppressed proxy → no wrap fallback (see GetParameterPlan).
                    ? $"Swift.Runtime.ExistentialContainerFactory.CreateOwnedClassCarrier<{_publicType}>({elementVar}).ClassRef"
                    : $"Swift.Runtime.ExistentialContainerFactory.CreateOwnedClassCarrier<{_publicType}>({elementVar}, static __v => new {_proxyClassName}(__v)).ClassRef"
                : _proxyClassName != null && _containerType == "Swift.Runtime.ExistentialContainer1"
                    ? _proxyIsSuppressed
                        // CONSUME: suppressed proxy → no wrap fallback (see GetParameterPlan).
                        ? $"Swift.Runtime.ExistentialContainerFactory.CreateOwnedExistential1<{_publicType}>({elementVar})"
                        : $"Swift.Runtime.ExistentialContainerFactory.CreateOwnedExistential1<{_publicType}>({elementVar}, static __v => new {_proxyClassName}(__v))"
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
            // CONSUME: suppressed proxy → no wrap fallback (see GetParameterPlan).
            return _proxyIsSuppressed
                ? $"Swift.Runtime.ExistentialContainerFactory.CreateOwnedClassCarrier<{_publicType}>({elementVar})"
                : $"Swift.Runtime.ExistentialContainerFactory.CreateOwnedClassCarrier<{_publicType}>({elementVar}, static __v => new {_proxyClassName}(__v))";
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
            // CONSUME: suppressed proxy → no wrap fallback (see GetParameterPlan).
            return _proxyIsSuppressed
                ? $"Swift.Runtime.ExistentialContainerFactory.CreateOwnedExistential1<{_publicType}>({elementVar})"
                : $"Swift.Runtime.ExistentialContainerFactory.CreateOwnedExistential1<{_publicType}>({elementVar}, static __v => new {_proxyClassName}(__v))";
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
    public string? GetReturnElementConversion(string elementVar)
    {
        if (_isBareAny)
            return $"ExistentialContainer0.Unbox({elementVar})";
        // An @objc existential received from Swift is a single bare object pointer (+0 borrowed on the
        // scalar receiver path — Swift keeps ownership), NOT a 40-byte EC1. When the proxy is emitted,
        // wrap the pointer into its class-bound container ctor with ownsContainer:false so the borrow is
        // not adopted (adopting would run an unknown-object release on storage Swift still owns → UAF).
        // The ownsContainer:true owned counterpart is GetObjCReturnExpression's wrapper-return form.
        // A SUPPRESSED @objc proxy has no emitted class to construct, so it must NOT be gated ahead of
        // the suppressed-proxy throw below — it falls through to that fail-closed skip exactly like the
        // non-@objc suppressed path (constructing new {Proxy}(…) would dangle on a type that was elided).
        if (_isObjCExistential && _proxyClassName != null && !_proxyIsSuppressed)
            return $"({_publicType})new {_proxyClassName}(new Swift.Runtime.ExistentialContainer1 {{ Payload0 = {elementVar} }}, ownsContainer: false)";
        // PRODUCE: a suppressed proxy cannot back a `new {Proxy}(…)` element construction (see GetReturnPlan).
        if (_proxyClassName != null && _proxyIsSuppressed)
            throw new SuppressedProxyReferenceException(_proxyClassName);
        return _proxyClassName != null
            // Cast to interface type for invariant container compatibility (IReadOnlyDictionary<K,V>
            // is invariant in V, so Func<EC, Proxy> won't match Func<EC, IProtocol>).
            ? $"({_publicType})new {_proxyClassName}({elementVar})"
            : _publicType == "object"
                ? $"(object){elementVar}"
                : $"new {_publicType}({elementVar})";
    }

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
    public string? GetOwnedReturnElementConversion(string elementVar)
    {
        // PRODUCE: a suppressed proxy cannot back a `new {Proxy}(…)` element construction (see GetReturnPlan).
        if (!_isBareAny && _proxyClassName != null && _proxyIsSuppressed)
            throw new SuppressedProxyReferenceException(_proxyClassName);
        return !_isBareAny && _proxyClassName != null
            ? $"({_publicType})new {_proxyClassName}({elementVar}{OwnsContainerArg})"
            : GetReturnElementConversion(elementVar);
    }

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
