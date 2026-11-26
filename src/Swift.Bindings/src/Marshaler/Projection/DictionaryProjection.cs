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
public class DictionaryProjection : ITypeProjection, IObjCContainerBridgeOwnerSource
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

    // Param/write value slot uses ParamValueCarrierType; return/read uses the value's
    // ArrayElementCarrierType. Both collapse to the legacy carrier for every non-existential value
    // and for opaque/composition existentials — the class-bound single-protocol value is the only
    // one that changes (16-byte ClassExistentialContainer1 instead of the 40-byte opaque container).
    public string SwiftContainerGenericType => $"SwiftDictionary<{_keyProjection.SwiftContainerGenericType}, {ParamValueCarrierType}>";

    public string ContainerTypeName => $"SwiftDictionary<{_keyProjection.MarshalFromSwiftType}, {_valueProjection.ArrayElementCarrierType}>";

    /// <summary>
    /// For MarshalFromSwift in return direction, use MarshalFromSwiftType of inner key/value
    /// (same as ContainerTypeName). This ensures OptionalProjection wrapping a DictionaryProjection
    /// gets the public type names not P/Invoke types.
    /// </summary>
    public string MarshalFromSwiftType => ContainerTypeName;

    /// <summary>
    /// The SwiftDictionary VALUE-slot carrier for the PARAMETER/WRITE (FromDictionary) direction. For a
    /// class-bound single-protocol existential value this is the 16-byte
    /// <c>ClassExistentialContainer1</c> — matching the Swift dictionary's actual value stride
    /// (<c>MemoryLayout&lt;any ClassP&gt;.stride == 16</c>, vs 40 for the opaque
    /// <c>ExistentialContainer1</c>; the value layout is a property of the type, not the container, so
    /// it is identical to the array-element case). Mirrors
    /// <see cref="ArrayProjection.ParamElementCarrierType"/>. Dictionary KEYS are never class-bound
    /// existentials — <c>any P</c> is not <c>Hashable</c>, so <c>[any P: V]</c> is ill-formed — so only
    /// the value needs the carrier; the key stays on its <c>SwiftContainerGenericType</c>. A no-op for
    /// every non-existential value and for opaque/composition existentials (whose
    /// <c>ArrayElementCarrierType</c> equals their own <c>SwiftContainerGenericType</c>).
    /// </summary>
    private string ParamValueCarrierType =>
        _valueProjection is ExistentialProjection existVal
            ? existVal.ArrayElementCarrierType
            : _valueProjection.SwiftContainerGenericType;

    /// <summary>
    /// Per-value conversion for the PARAMETER/WRITE direction. Narrows a class-bound existential
    /// value's proxy-produced <c>ExistentialContainer1</c> down to the 16-byte
    /// <c>ClassExistentialContainer1</c> carrier (<see cref="ParamValueCarrierType"/>) via the owned
    /// <c>CreateOwnedClassCarrier</c>; a no-op passthrough for every other value. Pairs with
    /// <see cref="ParamValueCarrierType"/> so the SwiftDictionary value slot type and the expression
    /// filling it always agree on stride. Mirrors <c>ArrayProjection.ParamElementConversion</c>.
    /// </summary>
    private string? ParamValueConversion(string valueVar) =>
        _valueProjection is ExistentialProjection existVal
            ? existVal.GetArrayElementCarrierConversion(valueVar)
            : _valueProjection.GetParameterElementConversion(valueVar);

    /// <summary>
    /// Builds the container creation statements (key/value conversion + SwiftDictionary.FromDictionary)
    /// without PayloadBuffer extraction.
    /// </summary>
    private (List<MarshalStatement> setup, string containerExpr) BuildContainerSetup(string paramName)
    {
        var rawK = _keyProjection.SwiftContainerGenericType;
        // Class-bound existential value → 16-byte ClassExistentialContainer1 carrier + owned
        // narrowing (ParamValueCarrierType/ParamValueConversion); a no-op for every other value.
        var rawV = ParamValueCarrierType;
        var keyConv = _keyProjection.GetParameterElementConversion("kvp.Key");
        var valConv = ParamValueConversion("kvp.Value");
        // When SwiftContainerGenericType matches the C# public type for a key or value
        // projection (e.g. SwiftDictionary<K, NonFrozenStruct>), the per-slot storage holds
        // the typed wrapper directly and FromDictionary dispatches to ISwiftObject.MarshalToSwift.
        // Applying the per-element conversion (e.g. e.Payload.DangerousGetHandle()) would
        // silently downgrade that slot to a 1-word IntPtr, causing an ABI mismatch where the
        // Swift side expects the full struct layout. Mirrors ArrayProjection / SetProjection.
        var skipKeyConv = keyConv != null && rawK == _keyProjection.PublicType;
        var skipValConv = valConv != null && rawV == _valueProjection.PublicType;
        var effectiveKeyConv = skipKeyConv ? null : keyConv;
        var effectiveValConv = skipValConv ? null : valConv;
        var needsConversion = effectiveKeyConv != null || effectiveValConv != null;
        var setup = new List<MarshalStatement>();

        if (needsConversion)
        {
            var keyExpr = effectiveKeyConv ?? "kvp.Key";
            var valExpr = effectiveValConv ?? "kvp.Value";

            // The pairs stream into the Swift dictionary as they are converted, as an array's
            // elements do, so a conversion that mints (an owned existential value) and a later
            // pair whose conversion throws leave the earlier mints with the abandoned dictionary,
            // which releases them, rather than in a list nothing owns.
            setup.Add(new MarshalStatement.Line(
                $"var {paramName}Pairs = {CollectionElementSelect.Pairs(paramName, $"kvp => new KeyValuePair<{rawK}, {rawV}>({keyExpr}, {valExpr})", _keyProjection, _valueProjection)};"));

            // Disposal applies only to projections whose conversion was actually emitted.
            // When the conversion is skipped (typed-wrapper passthrough), the original
            // wrapper instances aren't owned by this call site and must not be disposed.
            var finallyBody = new List<MarshalStatement>();
            if (_keyProjection.ElementRequiresDisposal && effectiveKeyConv != null)
            {
                finallyBody.Add(new MarshalStatement.Line(
                    $"foreach (var _item in {paramName}Converted) _item.Key.Dispose();"));
            }
            if (_valueProjection.ElementRequiresDisposal && effectiveValConv != null)
            {
                finallyBody.Add(new MarshalStatement.Line(
                    $"foreach (var _item in {paramName}Converted) _item.Value.Dispose();"));
            }

            if (finallyBody.Count > 0)
            {
                // Temporaries to dispose are collected as the dictionary takes each pair.
                setup.Add(new MarshalStatement.Line(
                    $"var {paramName}Converted = new global::System.Collections.Generic.List<KeyValuePair<{rawK}, {rawV}>>();"));
                setup.Add(new MarshalStatement.Line(
                    $"SwiftDictionary<{rawK}, {rawV}> {paramName}SwiftInner;"));
                var tryBody = new List<MarshalStatement>
                {
                    new MarshalStatement.Line(
                        $"{paramName}SwiftInner = SwiftDictionary<{rawK}, {rawV}>.FromDictionary(global::System.Linq.Enumerable.Select({paramName}Pairs, {paramName}Pair => {{ {paramName}Converted.Add({paramName}Pair); return {paramName}Pair; }}));")
                };
                setup.Add(new MarshalStatement.Block("try", tryBody));
                setup.Add(new MarshalStatement.Block("finally", finallyBody));
            }
            else
            {
                setup.Add(new MarshalStatement.Line(
                    $"var {paramName}SwiftInner = SwiftDictionary<{rawK}, {rawV}>.FromDictionary({paramName}Pairs);"));
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
        // ObjC bridge: convert NSDictionary handle to typed dictionary (used by OptionalProjection)
        if (UsesObjCContainerBridge)
            return BuildObjCBridgeReturnExpression(containerVar);

        var keyConv = OwnedReturnKeyConversion("k");
        var valConv = OwnedReturnValueConversion("v");
        return $"{containerVar}{BuildAsProjected(keyConv, valConv)}";
    }

    /// <summary>
    /// Key/value conversions for the OWNED-return directions only. SwiftDictionary's
    /// indexer get, Keys/Values, and entry enumerator all move each key and value out of their
    /// slot at +1 (MarshalMovedValueFromSlot), so an adopting proxy must release that retain on
    /// Dispose or it leaks; the source dictionary keeps its own independent +1, so adoption never
    /// double-frees. Existential keys/values use the owning form; everything else — and the shared
    /// non-owning <see cref="GetReturnElementConversion"/> reused for borrowed reads — stays +0.
    /// Nested container keys/values recurse through <see cref="GetOwnedReturnElementConversion"/> so
    /// an existential leaf inside an inner container still adopts its moved +1. Mirrors
    /// ArrayProjection.OwnedReturnElementConversion.
    /// </summary>
    private string? OwnedReturnKeyConversion(string keyVar)
        => _keyProjection.GetOwnedReturnElementConversion(keyVar);

    private string? OwnedReturnValueConversion(string valVar)
        => _valueProjection.GetOwnedReturnElementConversion(valVar);

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        // ObjC bridge path: IntPtr is an NSDictionary handle — extract typed key-value pairs
        if (UsesObjCContainerBridge)
            return BuildObjCBridgeReturnPlan(resultName, strategy);

        // Use MarshalFromSwiftType for return — classes/non-frozen structs need the real type name.
        // The VALUE uses ArrayElementCarrierType so a class-bound existential value reads at its
        // 16-byte ClassExistentialContainer1 stride (matches ContainerTypeName/MarshalFromSwiftType);
        // ArrayElementCarrierType == MarshalFromSwiftType for every other value.
        var rawK = _keyProjection.MarshalFromSwiftType;
        var rawV = _valueProjection.ArrayElementCarrierType;
        // Owned-return direction — existential keys/values are adopted at +1 (see OwnedReturn*Conversion).
        var keyConv = OwnedReturnKeyConversion("k");
        var valConv = OwnedReturnValueConversion("v");

        var asProjected = BuildAsProjected(keyConv, valConv);

        return strategy switch
        {
            // Direct (by-value register) return: the owned Swift Dictionary temporary carries +1 on
            // its CoW storage. SwiftDictionary's from-handle ctor runs VWT InitializeWithCopy (a fresh
            // +1 for the SafeHandle), so the source slot must be value-witness-destroyed or that +1
            // leaks the storage — use the consuming marshal (copy then destroy the source).
            ReturnStrategy.Direct => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwiftObjectConsuming<SwiftDictionary<{rawK}, {rawV}>>(&{resultName}){asProjected}",
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
    /// When the value projection yields a CONCRETE collection (a nested Dictionary's <c>ToDictionary</c> or a
    /// Set's <c>ToHashSet</c>), the value selector is cast to the value's <see cref="ITypeProjection.PublicType"/>
    /// so <c>AsProjected</c> infers the declared <c>IReadOnlyDictionary&lt;K, IReadOnly…&gt;</c> rather than
    /// <c>IReadOnlyDictionary&lt;K, Dictionary&lt;…&gt;&gt;</c> — the outer value slot is INVARIANT, so the concrete
    /// inner type triggers CS0266 without it. This is the top-level counterpart to the per-value cast in
    /// <see cref="GetReturnElementConversion"/> (which handles the same hazard one level down). Other value
    /// kinds need no cast: existentials self-cast to their interface, Arrays project through covariant
    /// <c>IReadOnlyList</c>, and scalars already carry their declared type.
    /// <para>
    /// <c>internal</c> so the EveryProtocol receiver setter path
    /// (<c>ProtocolProxyEmitter.GetReceiverDictSetterConversion</c>) reuses the SAME invariant-slot cast:
    /// a settable <c>[String: [String: any P]] { get set }</c> property assigns the converted value into
    /// the impl's <c>IReadOnlyDictionary&lt;…, IReadOnlyDictionary&lt;…&gt;&gt;</c> setter param, whose value slot is
    /// invariant — so the bare concrete-donor selector body would be CS0266 there too.
    /// </para>
    /// </summary>
    internal string BuildAsProjected(string? keyConv, string? valConv)
    {
        var valBody = CastValueSelectorBody(valConv);
        if (keyConv != null)
        {
            var reverseKeyConv = _keyProjection.GetParameterElementConversion("k") ?? "k";
            var valSelector = valBody != null ? $"v => {valBody}" : "v => v";
            return $".AsProjected(k => {keyConv}, k => {reverseKeyConv}, {valSelector})";
        }
        if (valBody != null)
        {
            return $".AsProjected(v => {valBody})";
        }
        return ".AsProjected(v => v)";
    }

    /// <summary>
    /// Wraps a value-selector body in a cast to the value's public interface type when (and only when) the
    /// value projection is a CONTAINER (Array/Dictionary/Set) — see <see cref="BuildAsProjected"/>. The outer
    /// dictionary value slot is INVARIANT, so the value must surface its EXACT declared public type: a nested
    /// Dictionary/Set element conversion yields a concrete <c>Dictionary</c>/<c>HashSet</c>, and a nested Array
    /// yields <c>IReadOnlyList&lt;concreteInner&gt;</c> when its own elements are concrete collections (e.g.
    /// <c>[String: [[String: Any]]]</c> → <c>IReadOnlyList&lt;Dictionary&lt;…&gt;&gt;</c>). Both differ from the
    /// declared <c>IReadOnlyList&lt;IReadOnlyDictionary&lt;…&gt;&gt;</c> / <c>IReadOnly…</c> value type, so without
    /// the cast <c>AsProjected</c> infers a value type the invariant outer dictionary rejects with CS0266. The
    /// cast is always legal (covariant <c>IReadOnlyList&lt;out T&gt;</c> reference conversion, or identity).
    /// Existential values self-cast to their interface and scalars already carry their declared type, so neither
    /// needs this — restricting to containers avoids a redundant double cast.
    /// </summary>
    private string? CastValueSelectorBody(string? valConv)
    {
        if (valConv == null)
            return null;
        return _valueProjection is ArrayProjection or DictionaryProjection or SetProjection
            ? $"({_valueProjection.PublicType}){valConv}"
            : valConv;
    }

    /// <summary>
    /// Per-element conversion for the PARAMETER/WRITE direction when this Dictionary is itself an
    /// element of an OUTER container (e.g. the inner dict of <c>[[String: any P]]</c>). Mirrors
    /// <see cref="ArrayProjection.GetParameterElementConversion"/>: a single-expression
    /// <c>FromDictionary(... .Select(kvp => new KeyValuePair&lt;rawK, rawV&gt;(keyConv, valConv)))</c>
    /// that recursively narrows existential / nested-container keys and values to their wire carriers
    /// so an outer Array/Dictionary/Set can build <c>SwiftArray&lt;SwiftDictionary&lt;…&gt;&gt;</c> with each
    /// inner dictionary already converted. Without this override DictionaryProjection inherited the
    /// <see cref="ITypeProjection.GetParameterElementConversion"/> null default, so a dict nested under
    /// an array passed unconverted C# dictionaries straight into
    /// <c>SwiftArray&lt;SwiftDictionary&lt;…&gt;&gt;.FromEnumerable</c> (CS1503; the symmetric
    /// return-direction recursion was already closed). The intermediate inner
    /// SwiftDictionary is disposed by the OUTER container's materialize+dispose pass
    /// (<see cref="ElementRequiresDisposal"/>); nesting deeper than two levels shares the array
    /// sibling's single-expression no-dispose limitation.
    /// </summary>
    public string? GetParameterElementConversion(string elementVar)
    {
        // ObjC bridge: nested dictionary → NSDictionary (received as NSObject by the outer
        // container, cf. ToNSObject). Mirror BuildObjCBridgeParameterPlan as a single expression.
        if (UsesObjCContainerBridge)
        {
            if (CrossesByHandles)
                return AdoptNSDictionary(CreateFromHandles(elementVar));
            return OnePassNSDictionary(elementVar);
        }

        var rawK = _keyProjection.SwiftContainerGenericType;
        var rawV = ParamValueCarrierType;
        var keyConv = _keyProjection.GetParameterElementConversion("kvp.Key");
        var valConv = ParamValueConversion("kvp.Value");
        // Same typed-wrapper skip rule as BuildContainerSetup: when the wire carrier already equals
        // the C# public type, FromDictionary wants the wrapper directly (applying a conversion would
        // downgrade the slot's stride). Mirrors ArrayProjection.GetParameterElementConversion.
        var skipKeyConv = keyConv != null && rawK == _keyProjection.PublicType;
        var skipValConv = valConv != null && rawV == _valueProjection.PublicType;
        var effectiveKeyConv = skipKeyConv ? null : keyConv;
        var effectiveValConv = skipValConv ? null : valConv;
        if (effectiveKeyConv != null || effectiveValConv != null)
        {
            var keyExpr = effectiveKeyConv ?? "kvp.Key";
            var valExpr = effectiveValConv ?? "kvp.Value";
            return $"SwiftDictionary<{rawK}, {rawV}>.FromDictionary({(effectiveValConv != null ? CollectionElementSelect.ConsumedPairs(elementVar, $"kvp => new KeyValuePair<{rawK}, {rawV}>({keyExpr}, {valExpr})", _keyProjection, _valueProjection) : CollectionElementSelect.Pairs(elementVar, $"kvp => new KeyValuePair<{rawK}, {rawV}>({keyExpr}, {valExpr})", _keyProjection, _valueProjection))})";
        }
        return $"SwiftDictionary<{rawK}, {rawV}>.FromDictionary({elementVar})";
    }

    /// <summary>
    /// Element-level conversion for when this Dictionary appears inside a container (e.g., Array&lt;Dictionary&gt;).
    /// Converts SwiftDictionary&lt;K,V&gt; → a CONCRETE <c>Dictionary&lt;PublicK,PublicV&gt;</c> via .ToDictionary()
    /// with per-key/value public-type casts (each leaf still surfaces its declared interface).
    /// The whole expression is deliberately NOT cast to <see cref="PublicType"/> (<c>IReadOnlyDictionary</c>):
    /// a concrete <c>Dictionary&lt;K,V&gt;</c> is the universal donor — assignable to BOTH the read-only
    /// <c>IReadOnlyDictionary</c> (covariant/forward-return consumers) AND the mutable <c>IDictionary</c>
    /// (reverse-dispatch receiver params whose impl method takes <c>IEnumerable&lt;IDictionary&gt;</c>). Casting
    /// to <c>IReadOnlyDictionary</c> here destroys that dual assignability and breaks the receiver path with
    /// CS1503 (an <c>IReadOnlyDictionary</c> is not an <c>IDictionary</c>, and array covariance can't bridge it).
    /// The INVARIANT-slot cast is therefore applied by the consumer instead, via one of two mechanisms: the
    /// per-value <c>({valPubType})</c> cast at the <c>ToDictionary</c> line below — which fires when an OUTER
    /// dict's <c>GetReturnElementConversion</c> wraps this dict as its value (the nested-dict-in-dict ToDictionary
    /// recursion) — and <see cref="CastValueSelectorBody"/> (called from <see cref="BuildAsProjected"/>) for the
    /// <c>AsProjected</c> selector paths (top-level return container conversion and the receiver dict setter).
    /// (The Array sibling needs no per-element cast — <c>IReadOnlyList&lt;out T&gt;</c> is covariant.)
    /// </summary>
    public string? GetReturnElementConversion(string elementVar)
    {
        // An element of an Objective-C collection (`[[String: URL]]`): the NSDictionary itself.
        if (UsesObjCContainerBridge)
            return BorrowedObjCBridgeRead(elementVar);
        var keyConv = _keyProjection.GetReturnElementConversion("kvp.Key");
        var valConv = _valueProjection.GetReturnElementConversion("kvp.Value");
        var keyExpr = keyConv ?? "kvp.Key";
        var valueExpr = valConv ?? "kvp.Value";
        var keyPubType = _keyProjection.PublicType;
        var valPubType = _valueProjection.PublicType;
        return Materialize(elementVar, $"kvp => ({keyPubType}){keyExpr}", $"kvp => ({valPubType}){valueExpr}", _keyProjection);
    }

    /// <summary>
    /// Owned-return element conversion for when this Dictionary is itself an element of an OWNED
    /// outer container. Mirrors <see cref="GetReturnElementConversion"/> but threads the OWNED
    /// key/value conversions (<see cref="OwnedReturnKeyConversion"/>/<see cref="OwnedReturnValueConversion"/>)
    /// so an existential leaf nested under this dictionary adopts its moved +1 instead of leaking it.
    /// Yields the same CONCRETE universal-donor <c>Dictionary</c> as <see cref="GetReturnElementConversion"/>
    /// (no top-level <c>IReadOnlyDictionary</c> cast); the invariant-slot cast is the consumer's job.
    /// </summary>
    public string? GetOwnedReturnElementConversion(string elementVar)
    {
        if (UsesObjCContainerBridge)
            return BorrowedObjCBridgeRead(elementVar);
        var keyConv = OwnedReturnKeyConversion("kvp.Key");
        var valConv = OwnedReturnValueConversion("kvp.Value");
        var keyExpr = keyConv ?? "kvp.Key";
        var valueExpr = valConv ?? "kvp.Value";
        var keyPubType = _keyProjection.PublicType;
        var valPubType = _valueProjection.PublicType;
        return Materialize(elementVar, $"kvp => ({keyPubType}){keyExpr}", $"kvp => ({valPubType}){valueExpr}", _keyProjection);
    }

    /// <summary>
    /// True when the key type is an optional (<c>[Node?: V]</c>, <c>[Int?: V]</c>): <c>nil</c> is then a
    /// key like any other, which a .NET <c>Dictionary</c> refuses.
    /// </summary>
    public bool HasOptionalKey => _keyProjection is OptionalProjection;

    /// <summary>
    /// The concrete dictionary a Swift dictionary's entries are read into, from <paramref name="source"/>
    /// through the two selectors: a <c>Dictionary</c>, or for an optional key type a
    /// <c>Swift.NilKeyDictionary</c>, which holds the <c>nil</c> key as Swift's does. Both implement
    /// <c>IDictionary</c> and <c>IReadOnlyDictionary</c>. A key that Swift alone can compare
    /// (<see cref="SwiftKeyComparer"/>) gets a comparer that compares it as Swift does.
    /// </summary>
    internal static string Materialize(string source, string keySelector, string valueSelector, ITypeProjection keyProjection)
        => Materialize(source, keySelector, valueSelector, keyProjection is OptionalProjection, SwiftKeyComparer(keyProjection));

    /// <summary>
    /// <see cref="Materialize(string, string, string, ITypeProjection)"/> for a caller that knows only
    /// whether the key type is optional: its keys are compared by their own equality.
    /// </summary>
    internal static string Materialize(string source, string keySelector, string valueSelector, bool optionalKey, string? comparer = null)
    {
        var comparerArgument = comparer is null ? "" : $", {comparer}";
        return optionalKey
            ? $"global::Swift.NilKeyDictionary.From({source}, {keySelector}, {valueSelector}{comparerArgument})"
            : $"global::System.Linq.Enumerable.ToDictionary({source}, {keySelector}, {valueSelector}{comparerArgument})";
    }

    /// <summary>
    /// A comparer expression for a managed collection of keys (or set elements) of
    /// <paramref name="keyProjection"/> that compares and hashes each as Swift does, through the
    /// <c>Hashable</c> conformance of the Swift type it crosses as, or <c>null</c> when the key's own
    /// managed equality is Swift's. A Swift class key, optional or not, needs one: Swift hashes it by
    /// the conformance its collection was built with, which the class's wrapper need not share (a
    /// conformance conditional on the class's generic arguments, or declared outside its module,
    /// leaves the wrapper comparing by identity), so a fresh, equal object would miss the entry
    /// Swift holds under it. A copy that compares as Swift does finds it, as a lookup on the Swift
    /// dictionary itself does.
    /// </summary>
    internal static string? SwiftKeyComparer(ITypeProjection keyProjection)
    {
        if (StringKeyComparer(keyProjection) is { } stringComparer)
            return stringComparer;
        if (CollectionKeyComparer(keyProjection) is { } collectionComparer)
            return collectionComparer;
        var swiftClassKey = keyProjection switch
        {
            ClassProjection or ObjCRootedClassProjection => keyProjection.SwiftContainerGenericType == keyProjection.PublicType,
            OptionalProjection { ObjectCarrier: not null, InnerProjection: ClassProjection or ObjCRootedClassProjection } => true,
            _ => false,
        };
        if (!swiftClassKey)
            return null;
        var carrier = keyProjection.SwiftContainerGenericType;
        // A class slot's carrier is the wrapper itself; an optional's is its object carrier, made
        // from a non-null key (the comparer never converts a null one).
        var toCarrier = carrier == keyProjection.PublicType
            ? "comparerKey"
            : keyProjection.GetParameterElementConversion("comparerKey") ?? "comparerKey";
        return $"global::Swift.Runtime.SwiftKeyComparer.Create<{keyProjection.PublicType}, {carrier}>(comparerKey => {toCarrier})";
    }

    /// <summary>
    /// The comparer of a managed collection whose keys or set elements are themselves Swift arrays
    /// or sets (<c>Set&lt;[URL]&gt;</c>, <c>[Set&lt;Int&gt;: V]</c>), or <c>null</c> for any other key.
    /// Such a key reads as a list or a set, which compares by reference, where Swift's <c>Hashable</c>
    /// compares an array by its elements in order and a set by its elements in any order; the
    /// comparer does the same, comparing the elements as Swift does in turn.
    /// </summary>
    internal static string? CollectionKeyComparer(ITypeProjection keyProjection)
    {
        var (kind, element) = keyProjection switch
        {
            ArrayProjection array => ("Sequences", array.ElementProjection),
            SetProjection set => ("Sets", set.ElementProjection),
            _ => (null, null),
        };
        if (kind is null || element is null)
            return null;
        return $"global::Swift.Runtime.SwiftKeyComparer.{kind}<{element.PublicType}>({SwiftKeyComparer(element) ?? "null"})";
    }

    /// <summary>
    /// The comparer of a managed set's elements: a Swift string or a nested array or set compares
    /// as Swift does (<see cref="StringKeyComparer"/>, <see cref="CollectionKeyComparer"/>);
    /// <c>null</c> when the element's managed equality is Swift's.
    /// </summary>
    internal static string? SetElementComparer(ITypeProjection elementProjection)
        => StringKeyComparer(elementProjection) ?? CollectionKeyComparer(elementProjection);

    /// <summary>
    /// The comparer of a managed collection of Swift <c>String</c> keys or set elements, optional or
    /// not: Swift compares strings by canonical equivalence, where .NET's string equality is ordinal
    /// (and normalizing in .NET is a no-op in an app built with invariant globalization), so the
    /// collection compares through Swift. <c>null</c> for any other key.
    /// </summary>
    internal static string? StringKeyComparer(ITypeProjection keyProjection)
        => keyProjection is StringProjection or OptionalProjection { InnerProjection: StringProjection }
            ? "global::Swift.Runtime.SwiftKeyComparer.StringKeys"
            : null;

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
        // An optional key or element (`[NSUUID?: URL]`) holds nil like any other value. Swift spells
        // nil inside an Objective-C collection as NSNull, and reads NSNull back as nil.
        if (projection is OptionalProjection optional)
        {
            var some = NonNilName(elementVar);
            return $"({elementVar} is {{ }} {some} ? (Foundation.NSObject){ToNSObject(optional.InnerProjection, some)} : Foundation.NSNull.Null)";
        }
        // An Apple typed-enum element projects as a C# enum, not an NSObject — convert it to the
        // NSString constant it bridges as before it can go into the dictionary.
        if (projection.TypedEnumAdapter is { } toAdapter)
            return toAdapter.ToCarrier(elementVar);
        if (projection is ObjCBridgeableProjection)
            return elementVar; // Already NSObject (NSUrl IS NSObject)
        if (projection is StringProjection)
            return $"new Foundation.NSString({elementVar})";
        if (projection is DataProjection)
            return $"Foundation.NSData.FromArray({elementVar})";
        // A nested container is the Objective-C collection Swift bridges it to, whether its own
        // elements bridge (`[String: [URL]]`) or not (`[URL: [Int]]`, whose `[Int]` Swift sends as an
        // NSArray of NSNumbers once the URL key bridges the dictionary around it).
        if (projection is ArrayProjection or SetProjection or DictionaryProjection)
        {
            var innerConv = projection.UsesObjCContainerBridge
                ? projection.GetParameterElementConversion(elementVar)
                : NativeContainerToNSObject(projection, elementVar);
            if (innerConv != null)
                return $"(Foundation.NSObject){innerConv}";
        }
        // Primitive numerics/bool aren't NSObjects — box via Foundation.NSNumber to mirror
        // the FromNSObject unbox path (NIntValue/BoolValue/...). Without this, an
        // [Int: URL] or [Bool: URL] *parameter* bridge emits "(Foundation.NSObject)kvp.Key"
        // which is an invalid primitive-to-NSObject cast.
        if (projection is BlittableProjection or BoolProjection)
        {
            var box = NSNumberBoxExpression(projection.PublicType, elementVar);
            if (box != null)
                return box;
        }
        return $"(Foundation.NSObject){elementVar}";
    }

    /// <summary>
    /// One expression that builds a new <c>NSDictionary</c> from the .NET dictionary
    /// <paramref name="sourceExpr"/>, bridging each key and value through <see cref="ToNSObject"/>.
    /// One pass over the dictionary makes each key's pair with its value: a dictionary need not
    /// enumerate twice in the same order, so separate key and value passes could pair a key with
    /// another key's value.
    /// </summary>
    internal string OnePassNSDictionary(string sourceExpr)
    {
        var kvp = LambdaName("kvp", sourceExpr);
        return "((global::System.Func<Foundation.NSObject[][], Foundation.NSDictionary>)(static __pairs => Foundation.NSDictionary.FromObjectsAndKeys(" +
               "global::System.Linq.Enumerable.ToArray(global::System.Linq.Enumerable.Select(__pairs, __pair => __pair[1])), " +
               "global::System.Linq.Enumerable.ToArray(global::System.Linq.Enumerable.Select(__pairs, __pair => __pair[0])))))(" +
               $"global::System.Linq.Enumerable.ToArray(global::System.Linq.Enumerable.Select({sourceExpr}, {kvp} => new Foundation.NSObject[] {{ (Foundation.NSObject){ToNSObject(_keyProjection, $"{kvp}.Key")}, (Foundation.NSObject){ToNSObject(_valueProjection, $"{kvp}.Value")} }})))";
    }

    /// <summary>
    /// The Objective-C collection a container whose own elements do not bridge is, nested in one
    /// that does: an NSArray, NSSet or NSDictionary whose elements are each converted as an element of
    /// an Objective-C collection (<see cref="ToNSObject"/>). Lambda parameters are named after
    /// <paramref name="elementVar"/>, so a nested one never redeclares its parent's.
    /// </summary>
    private static string NativeContainerToNSObject(ITypeProjection projection, string elementVar)
    {
        var e = LambdaName("e", elementVar);
        switch (projection)
        {
            case ArrayProjection array when IsSwiftObject(array.ElementProjection):
                // Each element is the Swift object itself, which has no NSObject peer.
                return $"ObjCRuntime.Runtime.GetINativeObject<Foundation.NSArray>(global::Swift.Runtime.ObjCArrayHandles.Create({elementVar}, {e} => {SwiftObjectHandle(array.ElementProjection, e)}), true)!";
            case ArrayProjection array:
                return $"Foundation.NSArray.FromNSObjects(global::System.Linq.Enumerable.ToArray(global::System.Linq.Enumerable.Select({elementVar}, {e} => (Foundation.NSObject){ToNSObject(array.ElementProjection, e)})))";
            case SetProjection set:
                return ObjCContainerBridgeOwner.NewSet($"global::System.Linq.Enumerable.ToArray(global::System.Linq.Enumerable.Select({elementVar}, {e} => (Foundation.NSObject){ToNSObject(set.ElementProjection, e)}))");
            case DictionaryProjection dictionary:
                if (dictionary.HoldsSwiftObjects)
                    return AdoptNSDictionary(dictionary.CreateFromHandles(elementVar));
                return dictionary.OnePassNSDictionary(elementVar);
            default:
                throw new ArgumentException($"Not a container projection: {projection.GetType().Name}", nameof(projection));
        }
    }

    /// <summary>
    /// A lambda parameter name for the elements of <paramref name="sourceExpr"/>, unique to that
    /// source: a lambda nested in it reads a longer source, so it gets a longer name.
    /// </summary>
    internal static string LambdaName(string stem, string sourceExpr)
        => $"_{stem}" + new string(sourceExpr.Where(char.IsLetterOrDigit).ToArray());

    /// <summary>A pattern variable for the non-nil value of <paramref name="elementVar"/>.</summary>
    private static string NonNilName(string elementVar)
        => "nonNil" + new string(elementVar.Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// The concrete dictionary type an Objective-C bridged dictionary is read into: one that can
    /// hold the nil key of an optional key type (<see cref="Materialize(string, string, string, ITypeProjection)"/>).
    /// </summary>
    private string ObjCBridgeReadDictionaryType
        => HasOptionalKey
            ? $"global::Swift.NilKeyDictionary<{_keyProjection.PublicType}, {_valueProjection.PublicType}>"
            : $"System.Collections.Generic.Dictionary<{_keyProjection.PublicType}, {_valueProjection.PublicType}>";

    /// <summary>
    /// The keys of <paramref name="nsDictionary"/> to read entries by. The platform binding hands
    /// back an NSNull key (Swift's nil key) as <see langword="null"/>, which neither converts nor
    /// looks its value up, so an optional key's NSNull is put back: it reads as nil, and finds its
    /// value, as the NSNull singleton is equal to itself.
    /// </summary>
    private string ObjCBridgeReadKeys(string nsDictionary)
        => HasOptionalKey
            ? $"global::System.Linq.Enumerable.Select({nsDictionary}.Keys, _key => (Foundation.NSObject?)_key ?? Foundation.NSNull.Null)"
            : $"{nsDictionary}.Keys";

    /// <summary>
    /// A new, empty <see cref="ObjCBridgeReadDictionaryType"/> sized for <paramref name="nsDictionary"/>,
    /// comparing its keys as Swift does where the key's managed equality is not Swift's
    /// (<see cref="SwiftKeyComparer(ITypeProjection)"/>), as a natively read copy does.
    /// </summary>
    private string NewObjCBridgeReadDictionary(string nsDictionary)
    {
        var comparer = SwiftKeyComparer(_keyProjection);
        var arguments = HasOptionalKey
            ? comparer ?? ""
            : comparer is null ? $"(int){nsDictionary}.Count" : $"(int){nsDictionary}.Count, {comparer}";
        return $"new {ObjCBridgeReadDictionaryType}({arguments})";
    }

    /// <summary>
    /// Converts an NSObject from NSDictionary to the C# typed element.
    /// Numeric keys/values are stored as boxed NSNumber instances and require explicit
    /// unboxing through the matching NSNumber accessor (e.g. <c>NIntValue</c> for
    /// <c>Swift.Int</c>) rather than a plain cast — <c>(nint)NSObject</c> is invalid.
    /// </summary>
    internal static string FromNSObject(ITypeProjection projection, string nsObjectVar)
    {
        // NSNull is Swift's nil inside an Objective-C collection (see ToNSObject).
        if (projection is OptionalProjection optional)
            return $"({nsObjectVar} is Foundation.NSNull ? ({optional.PublicType})null : ({optional.PublicType}){FromNSObject(optional.InnerProjection, nsObjectVar)})";
        // An Apple typed-enum element travels as its NSString carrier; read the carrier, then
        // convert back to the projected C# enum through the platform binding's {Enum}Extensions.
        if (projection.TypedEnumAdapter is { } fromAdapter)
            return fromAdapter.FromCarrier(
                MarshallingHelpers.FormatObjCBridgeCall(fromAdapter.CarrierType, $"{nsObjectVar}.Handle", nonNull: true));
        if (projection is ObjCBridgeableProjection bridgeable)
            return MarshallingHelpers.FormatObjCBridgeCall(bridgeable.PublicType, $"{nsObjectVar}.Handle", nonNull: true);
        if (projection is StringProjection)
            // This object is the NSString carrier of a non-optional Swift.String key/value.
            // NSObject.ToString() is nullable in newer Apple bindings, but this carrier cannot
            // represent nil; Optional<String> does not use this whole-container bridge.
            return $"{nsObjectVar}.ToString()!";
        if (projection is DataProjection)
            return $"((Foundation.NSData){nsObjectVar}).ToArray()";
        // A nested container is the Objective-C collection Swift bridged it to, borrowed from this
        // one: read its elements without adopting it. That holds whether its own elements bridge
        // (`[String: [URL]]`) or not (`[URL: [Int]]`: Swift bridges the `[Int]` with the dictionary
        // around it, as an NSArray of NSNumbers).
        if (projection is ArrayProjection array)
            return array.UsesObjCContainerBridge
                ? array.GetReturnElementConversion(nsObjectVar)!
                : array.ReadNativeElementsOfNSArray(nsObjectVar);
        if (projection is SetProjection set)
            return set.ReadObjCSet($"ObjCRuntime.Runtime.GetNSObject<Foundation.NSSet>({nsObjectVar}.Handle)!");
        if (projection is DictionaryProjection dictionary)
            return dictionary.BorrowedObjCBridgeRead(nsObjectVar);
        // BoolProjection is its own class (not BlittableProjection), but Swift.Bool also
        // bridges to NSNumber inside an NSDictionary, so it needs the same unbox path.
        if (projection is BlittableProjection or BoolProjection)
        {
            var unbox = NSNumberUnboxExpression(projection.PublicType, nsObjectVar);
            if (unbox != null)
                return unbox;
        }
        return $"({projection.PublicType}){nsObjectVar}";
    }

    /// <summary>
    /// For numeric/boolean primitive types stored as NSNumber inside an NSDictionary,
    /// emit the matching NSNumber accessor. Returns null for non-NSNumber primitives,
    /// in which case callers fall back to the default NSObject cast.
    /// </summary>
    private static string? NSNumberUnboxExpression(string publicType, string nsObjectVar)
    {
        var nsNumber = $"((Foundation.NSNumber){nsObjectVar})";
        return publicType switch
        {
            "nint" => $"{nsNumber}.NIntValue",
            "nuint" => $"{nsNumber}.NUIntValue",
            "long" => $"{nsNumber}.Int64Value",
            "ulong" => $"{nsNumber}.UInt64Value",
            "int" => $"{nsNumber}.Int32Value",
            "uint" => $"{nsNumber}.UInt32Value",
            "short" => $"{nsNumber}.Int16Value",
            "ushort" => $"{nsNumber}.UInt16Value",
            "byte" => $"{nsNumber}.ByteValue",
            "sbyte" => $"{nsNumber}.SByteValue",
            "float" => $"{nsNumber}.FloatValue",
            "double" => $"{nsNumber}.DoubleValue",
            "bool" => $"{nsNumber}.BoolValue",
            _ => null
        };
    }

    /// <summary>
    /// Inverse of <see cref="NSNumberUnboxExpression"/>: box a C# primitive into a
    /// Foundation.NSNumber for NSDictionary construction. Foundation.NSNumber IS an NSObject,
    /// so the result is directly assignable as a key/value. Returns null for primitives
    /// without an NSNumber factory; callers fall back to the default NSObject cast.
    /// </summary>
    private static string? NSNumberBoxExpression(string publicType, string elementVar)
    {
        return publicType switch
        {
            "nint" => $"Foundation.NSNumber.FromNInt({elementVar})",
            "nuint" => $"Foundation.NSNumber.FromNUInt({elementVar})",
            "long" => $"Foundation.NSNumber.FromInt64({elementVar})",
            "ulong" => $"Foundation.NSNumber.FromUInt64({elementVar})",
            "int" => $"Foundation.NSNumber.FromInt32({elementVar})",
            "uint" => $"Foundation.NSNumber.FromUInt32({elementVar})",
            "short" => $"Foundation.NSNumber.FromInt16({elementVar})",
            "ushort" => $"Foundation.NSNumber.FromUInt16({elementVar})",
            "byte" => $"Foundation.NSNumber.FromByte({elementVar})",
            "sbyte" => $"Foundation.NSNumber.FromSByte({elementVar})",
            "float" => $"Foundation.NSNumber.FromFloat({elementVar})",
            "double" => $"Foundation.NSNumber.FromDouble({elementVar})",
            "bool" => $"Foundation.NSNumber.FromBoolean({elementVar})",
            _ => null
        };
    }

    /// <summary>
    /// REVERSE-dispatch receiver conversion: a C#-implemented conformer returns this whole
    /// dictionary, which must cross back to the Swift EveryProtocol thunk as an NSDictionary pointer.
    /// Reuses <see cref="GetParameterElementConversion"/>'s single-expression
    /// <c>NSDictionary.FromObjectsAndKeys(...)</c> builder (recursively bridging nested ObjC
    /// containers via <see cref="ToNSObject"/>) and transfers a +1 ARC retain via
    /// <c>Arc.UnknownObjectRetain</c> (dispatches to objc_retain). The transfer retain keeps the
    /// dictionary — and the keys/values it retains — alive after the receiver frame returns,
    /// after the new managed wrapper, held until the retain is taken, is released; the Swift thunk balances
    /// it with <c>takeRetainedValue()</c>. This is the reverse of the forward accessor's
    /// <c>Unmanaged.passRetained</c>(+1) / C# <c>owns: true</c> adoption.
    public string GetReverseReceiverObjCBridgeConversion(string varName)
    {
        // A dictionary built from key handles is already the +1 Swift takes.
        if (CrossesByHandles)
            return CreateFromHandles(varName);
        return ObjCContainerBridgeOwner.RetainedHandle(OnePassNSDictionary(varName));
    }

    public ObjCContainerBridgeOwner BuildObjCBridgeOwner(string paramName, bool sourceIsNullable)
    {
        if (CrossesByHandles)
        {
            return new ObjCContainerBridgeOwner(
                new List<MarshalStatement>
                {
                    ObjCContainerBridgeOwner.Declare(
                        "Foundation.NSDictionary", $"{paramName}NSDict",
                        AdoptNSDictionary(CreateFromHandles(paramName)),
                        paramName, sourceIsNullable)
                },
                $"{paramName}NSDict");
        }
        var keyToNS = ToNSObject(_keyProjection, "kvp.Key");
        var valToNS = ToNSObject(_valueProjection, "kvp.Value");
        var ownerName = $"{paramName}NSDict";

        // The pairs are materialized once so the key and value arrays come from the same pass
        // over the dictionary; an IReadOnlyDictionary makes no promise about two enumerations
        // agreeing on order.
        if (sourceIsNullable)
        {
            return new ObjCContainerBridgeOwner(
                new List<MarshalStatement>
                {
                    new MarshalStatement.Line($"var {paramName}Pairs = {paramName} is null ? null : global::System.Linq.Enumerable.ToArray({paramName});"),
                    ObjCContainerBridgeOwner.Declare(
                        "Foundation.NSDictionary", ownerName,
                        $"Foundation.NSDictionary.FromObjectsAndKeys(global::System.Linq.Enumerable.ToArray(global::System.Linq.Enumerable.Select({paramName}Pairs, kvp => {valToNS})), global::System.Linq.Enumerable.ToArray(global::System.Linq.Enumerable.Select({paramName}Pairs, kvp => {keyToNS})))",
                        $"{paramName}Pairs", sourceIsNullable: true)
                },
                ownerName);
        }

        return new ObjCContainerBridgeOwner(
            new List<MarshalStatement>
            {
                new MarshalStatement.Line(
                    $"var {paramName}Pairs = global::System.Linq.Enumerable.ToArray({paramName});"),
                new MarshalStatement.Line(
                    $"var {paramName}Keys = global::System.Linq.Enumerable.ToArray(global::System.Linq.Enumerable.Select({paramName}Pairs, kvp => {keyToNS}));"),
                new MarshalStatement.Line(
                    $"var {paramName}Values = global::System.Linq.Enumerable.ToArray(global::System.Linq.Enumerable.Select({paramName}Pairs, kvp => {valToNS}));"),
                ObjCContainerBridgeOwner.Declare(
                    "Foundation.NSDictionary", ownerName,
                    $"Foundation.NSDictionary.FromObjectsAndKeys({paramName}Values, {paramName}Keys)",
                    paramName, sourceIsNullable: false)
            },
            ownerName);
    }

    /// <summary>
    /// ObjC bridge parameter plan: create NSDictionary from C# key-value pairs.
    /// </summary>
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
            // The bridged NSDictionary handle is borrowed: the `using` wrapper releases it after
            // the call, so a consuming callee needs a +1 handed over first.
            OwnedHandOverHandle = $"{paramName}Buffer"
        };
    }

    /// <summary>
    /// ObjC bridge return plan: receive NSDictionary handle, extract typed key-value pairs.
    /// owns: true balances the +1 retain emitted by the Swift @_cdecl wrapper
    /// (Unmanaged.passRetained(_unwrapped as AnyObject).toOpaque()). Without
    /// owns: true, the NSDictionary (and retained inner key/value NSObjects) leak per call.
    /// </summary>
    private MarshalPlan BuildObjCBridgeReturnPlan(string resultName, ReturnStrategy strategy)
    {
        if (CrossesByHandles)
        {
            return new MarshalPlan
            {
                SetupStatements = new List<MarshalStatement>
                {
                    new MarshalStatement.Line($"var {resultName}Dict = {GetAdoptedObjCBridgeReadExpression(resultName)};")
                },
                PInvokeExpression = $"{resultName}Dict"
            };
        }
        // Named by its depth, as the reader of a bridged dictionary nested in its values names its own.
        var depth = ObjCDictionaryNestingDepth(this);
        var nsKey = depth == 1 ? "_nsKey" : $"_nsKey{depth}";
        return new MarshalPlan
        {
            SetupStatements = new List<MarshalStatement>
            {
                new MarshalStatement.Line(
                    $"var {resultName}NSDict = ObjCRuntime.Runtime.GetINativeObject<Foundation.NSDictionary>({resultName}, true)!;"),
                new MarshalStatement.Line(
                    $"var {resultName}Dict = {NewObjCBridgeReadDictionary($"{resultName}NSDict")};"),
                new MarshalStatement.Line(
                    $"foreach (var {nsKey} in {ObjCBridgeReadKeys($"{resultName}NSDict")}) {resultName}Dict[{FromNSObject(_keyProjection, nsKey)}] = {FromNSObject(_valueProjection, $"{resultName}NSDict.ObjectForKey({nsKey})!")};")
            },
            PInvokeExpression = $"{resultName}Dict"
        };
    }

    /// <summary>
    /// Builds the ObjC bridge return expression for use by OptionalProjection.
    /// The containerVar parameter is the IntPtr handle to the NSDictionary.
    /// owns: true balances the +1 retain emitted by the Swift @_cdecl wrapper.
    /// </summary>
    private string BuildObjCBridgeReturnExpression(string containerVar)
        => BuildObjCBridgeReadExpression(
            $"ObjCRuntime.Runtime.GetINativeObject<Foundation.NSDictionary>({containerVar}, true)!",
            $"IReadOnlyDictionary<{_keyProjection.PublicType}, {_valueProjection.PublicType}>");

    /// <summary>
    /// Reads the +1 NSDictionary handle <paramref name="containerVar"/> into a new managed
    /// dictionary of its concrete type, adopting the handle as <see cref="BuildObjCBridgeReturnExpression"/>
    /// does.
    /// </summary>
    private string GetAdoptedObjCBridgeReadExpression(string containerVar)
        => BuildObjCBridgeReadExpression(
            $"ObjCRuntime.Runtime.GetINativeObject<Foundation.NSDictionary>({containerVar}, true)!",
            ObjCBridgeReadDictionaryType);

    /// <summary>
    /// Reads the borrowed NSDictionary handle <paramref name="handleVar"/> into a new managed
    /// dictionary of its concrete type. A reverse-dispatch receiver reads a bridged dictionary Swift
    /// lends its conformer this way: the concrete type satisfies both a parameter's
    /// <c>IDictionary</c> and a property setter's <c>IReadOnlyDictionary</c>.
    /// </summary>
    public string GetBorrowedObjCBridgeReadExpression(string handleVar)
        => BuildObjCBridgeReadExpression(
            $"ObjCRuntime.Runtime.GetNSObject<Foundation.NSDictionary>({handleVar})!",
            ObjCBridgeReadDictionaryType);

    /// <summary>
    /// The entries of the NSDictionary <paramref name="nsDictionaryExpr"/> read into a new
    /// <see cref="ObjCBridgeReadDictionaryType"/>, typed as <paramref name="resultType"/>. Each
    /// nesting level names its locals by its own depth, so a bridged dictionary inside another one
    /// does not redeclare its parent's.
    /// </summary>
    private string BuildObjCBridgeReadExpression(string nsDictionaryExpr, string resultType)
    {
        var depth = ObjCDictionaryNestingDepth(this);
        var nsDict = depth == 1 ? "_nsDict" : $"_nsDict{depth}";
        var nsKey = depth == 1 ? "_nsKey" : $"_nsKey{depth}";
        var dict = depth == 1 ? "_dict" : $"_dict{depth}";
        if (HoldsSwiftObjects)
        {
            // A key or value is a Swift object: read the entries by handle (CrossesByHandles).
            var entry = depth == 1 ? "_entry" : $"_entry{depth}";
            var key = KeyIsSwiftObject
                ? SwiftObjectFromHandle(_keyProjection, $"{entry}.Key")
                : FromNSObject(_keyProjection, $"ObjCRuntime.Runtime.GetNSObject({entry}.Key)!");
            var value = ValueIsSwiftObject
                ? SwiftObjectFromHandle(_valueProjection, $"{entry}.Value")
                : FromNSObject(_valueProjection, $"ObjCRuntime.Runtime.GetNSObject({entry}.Value)!");
            return $"((Func<{resultType}>)(() => {{ " +
                   $"var {nsDict} = {nsDictionaryExpr}; " +
                   $"var {dict} = {NewObjCBridgeReadDictionary(nsDict)}; " +
                   $"foreach (var {entry} in global::Swift.Runtime.ObjCDictionaryHandles.Entries({nsDict}.Handle)) {{ " +
                   $"{dict}[{key}] = {value}; }} " +
                   $"GC.KeepAlive({nsDict}); " +
                   $"return {dict}; }}))()";
        }
        var keyConv = FromNSObject(_keyProjection, nsKey);
        var valConv = FromNSObject(_valueProjection, $"{nsDict}.ObjectForKey({nsKey})!");
        // Inline dictionary construction using LINQ. The dictionary holds the collections its
        // values read by handle, so it stays alive until the last is read.
        return $"((Func<{resultType}>)(() => {{ " +
               $"var {nsDict} = {nsDictionaryExpr}; " +
               $"var {dict} = {NewObjCBridgeReadDictionary(nsDict)}; " +
               $"foreach (var {nsKey} in {ObjCBridgeReadKeys(nsDict)}) {dict}[{keyConv}] = {valConv}; " +
               $"GC.KeepAlive({nsDict}); " +
               $"return {dict}; }}))()";
    }

    /// <summary>
    /// Reads the NSDictionary <paramref name="nsObjectExpr"/>, an element or value of an enclosing
    /// Objective-C collection that keeps it alive, into a new managed dictionary without adopting it.
    /// </summary>
    private string BorrowedObjCBridgeRead(string nsObjectExpr)
        => BuildObjCBridgeReadExpression(
            $"ObjCRuntime.Runtime.GetNSObject<Foundation.NSDictionary>({nsObjectExpr}.Handle)!",
            ObjCBridgeReadDictionaryType);

    /// <summary>
    /// Whether this dictionary crosses by the handles of its keys and values: its key or value is a
    /// Swift class, pure Swift or <c>NSObject</c>-rooted, optional or not. Built, it is made from the
    /// Swift objects' own handles (<see cref="CreateFromHandles"/>): Foundation copies the keys of a
    /// dictionary it builds, a Swift class is not <c>NSCopying</c>, and a pure Swift object is no
    /// <c>NSObject</c> at all; the dictionary retains them instead, as Swift's own bridge does. Read,
    /// its entries are read by handle and each Swift object through the class's own marshalling,
    /// which finds an <c>NSObject</c>-rooted object's managed peer and wraps a pure Swift one.
    /// </summary>
    private bool CrossesByHandles
        => UsesObjCContainerBridge && HoldsSwiftObjects;

    /// <summary>
    /// Whether the key or the value is a Swift class (<see cref="IsSwiftObject"/>). Inside an
    /// Objective-C dictionary each such key or value is the object itself, so the dictionary is
    /// read and built by handles (<see cref="CrossesByHandles"/>), including when the dictionary is
    /// native (<c>[Node: Int]</c>) and only crosses as an NSDictionary because an enclosing
    /// collection bridges.
    /// </summary>
    private bool HoldsSwiftObjects => KeyIsSwiftObject || ValueIsSwiftObject;

    private bool KeyIsSwiftObject => IsSwiftObject(_keyProjection);

    private bool ValueIsSwiftObject => IsSwiftObject(_valueProjection);

    /// <summary>
    /// Whether <paramref name="projection"/> is a Swift class, pure Swift or <c>NSObject</c>-rooted,
    /// optional or not: inside an Objective-C collection each is the object itself.
    /// </summary>
    internal static bool IsSwiftObject(ITypeProjection projection)
        => Unwrapped(projection) is ClassProjection or ObjCRootedClassProjection;

    /// <summary>
    /// The handle of the Swift object <paramref name="elementVar"/> (<see cref="IsSwiftObject"/>) as
    /// an Objective-C collection holds it: an <c>NSNull</c> for nil.
    /// </summary>
    internal static string SwiftObjectHandle(ITypeProjection projection, string elementVar)
    {
        var handle = Unwrapped(projection).GetParameterElementConversion(elementVar)!;
        return projection is OptionalProjection
            ? $"({elementVar} is null ? Foundation.NSNull.Null.Handle : {handle})"
            : handle;
    }

    private static ITypeProjection Unwrapped(ITypeProjection projection)
        => projection is OptionalProjection optional ? optional.InnerProjection : projection;

    /// <summary>
    /// A new NSDictionary handle at +1 holding the entries of <paramref name="sourceExpr"/>: each Swift
    /// object key or value (<see cref="IsSwiftObject"/>) is the object itself (an <c>NSNull</c> for
    /// nil), and any other key or value is the object it bridges to.
    /// </summary>
    private string CreateFromHandles(string sourceExpr)
    {
        // Lambda parameters named by depth: a dictionary nested in the values builds inside these.
        var depth = ObjCDictionaryNestingDepth(this);
        string Name(string stem) => depth == 1 ? stem : $"{stem}{depth}";
        // Each conversion is its own lambda, so the object and handle lambdas share a parameter name.
        var (k, v, o) = (Name("k"), Name("v"), Name("o"));
        var (keyObject, keyHandle) = KeyIsSwiftObject
            ? (k, SwiftObjectHandle(_keyProjection, k))
            : ($"(Foundation.NSObject){ToNSObject(_keyProjection, k)}", $"{k}.Handle");
        var (valueObject, valueHandle) = ValueIsSwiftObject
            ? (v, SwiftObjectHandle(_valueProjection, o))
            : ($"(Foundation.NSObject){ToNSObject(_valueProjection, v)}", $"{o}.Handle");
        return $"global::Swift.Runtime.ObjCDictionaryHandles.Create({sourceExpr}, {k} => {keyObject}, {k} => {keyHandle}, {v} => {valueObject}, {o} => {valueHandle})";
    }

    /// <summary>
    /// A new NSDictionary, adopted at +1, holding <paramref name="sourceExpr"/>'s entries by the
    /// handles of its Swift objects when this dictionary crosses by them (<see cref="CrossesByHandles"/>);
    /// <c>null</c> otherwise.
    /// </summary>
    internal string? HandlesDictionary(string sourceExpr)
        => CrossesByHandles ? AdoptNSDictionary(CreateFromHandles(sourceExpr)) : null;

    /// <summary>The NSDictionary at +1 <paramref name="handleExpr"/>, adopted by a managed wrapper.</summary>
    private static string AdoptNSDictionary(string handleExpr)
        => $"ObjCRuntime.Runtime.GetINativeObject<Foundation.NSDictionary>({handleExpr}, true)!";

    /// <summary>
    /// The Swift object (<see cref="IsSwiftObject"/>) whose handle <paramref name="handleExpr"/> an
    /// Objective-C collection holds, borrowed from it: a new reference to the object, or nil for an
    /// <c>NSNull</c>.
    /// </summary>
    internal static string SwiftObjectFromHandle(ITypeProjection projection, string handleExpr)
    {
        var inner = Unwrapped(projection);
        var read = $"SwiftMarshal.MarshalBorrowedClassFromSwift<{inner.PublicType}>({handleExpr})";
        return projection is OptionalProjection optional
            ? $"({handleExpr} == Foundation.NSNull.Null.Handle ? ({optional.PublicType})null : {read})"
            : read;
    }

    /// <summary>How many bridged dictionaries enclose this one's entries, itself included.</summary>
    private static int ObjCDictionaryNestingDepth(ITypeProjection projection) => projection switch
    {
        DictionaryProjection dictionary => 1 + Math.Max(
            ObjCDictionaryNestingDepth(dictionary.KeyProjection),
            ObjCDictionaryNestingDepth(dictionary.ValueProjection)),
        ArrayProjection array => ObjCDictionaryNestingDepth(array.ElementProjection),
        SetProjection set => ObjCDictionaryNestingDepth(set.ElementProjection),
        OptionalProjection optional => ObjCDictionaryNestingDepth(optional.InnerProjection),
        _ => 0
    };
}
