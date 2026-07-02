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

            setup.Add(new MarshalStatement.Line(
                $"var {paramName}Converted = {paramName}.Select(kvp => new KeyValuePair<{rawK}, {rawV}>({keyExpr}, {valExpr})).ToList();"));
            setup.Add(new MarshalStatement.Line(
                $"SwiftDictionary<{rawK}, {rawV}> {paramName}SwiftInner;"));

            var tryBody = new List<MarshalStatement>
            {
                new MarshalStatement.Line(
                    $"{paramName}SwiftInner = SwiftDictionary<{rawK}, {rawV}>.FromDictionary({paramName}Converted);")
            };

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
            var keyToNS = ToNSObject(_keyProjection, "kvp.Key");
            var valToNS = ToNSObject(_valueProjection, "kvp.Value");
            return $"Foundation.NSDictionary.FromObjectsAndKeys({elementVar}.Select(kvp => {valToNS}).ToArray(), {elementVar}.Select(kvp => {keyToNS}).ToArray())";
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
            return $"SwiftDictionary<{rawK}, {rawV}>.FromDictionary({elementVar}.Select(kvp => new KeyValuePair<{rawK}, {rawV}>({keyExpr}, {valExpr})))";
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
        var keyConv = _keyProjection.GetReturnElementConversion("kvp.Key");
        var valConv = _valueProjection.GetReturnElementConversion("kvp.Value");
        var keyExpr = keyConv ?? "kvp.Key";
        var valueExpr = valConv ?? "kvp.Value";
        var keyPubType = _keyProjection.PublicType;
        var valPubType = _valueProjection.PublicType;
        return $"{elementVar}.ToDictionary(kvp => ({keyPubType}){keyExpr}, kvp => ({valPubType}){valueExpr})";
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
        var keyConv = OwnedReturnKeyConversion("kvp.Key");
        var valConv = OwnedReturnValueConversion("kvp.Value");
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
    /// Converts an NSObject from NSDictionary to the C# typed element.
    /// Numeric keys/values are stored as boxed NSNumber instances and require explicit
    /// unboxing through the matching NSNumber accessor (e.g. <c>NIntValue</c> for
    /// <c>Swift.Int</c>) rather than a plain cast — <c>(nint)NSObject</c> is invalid.
    /// </summary>
    private static string FromNSObject(ITypeProjection projection, string nsObjectVar)
    {
        if (projection is ObjCBridgeableProjection bridgeable)
            return MarshallingHelpers.FormatObjCBridgeCall(bridgeable.PublicType, $"{nsObjectVar}.Handle", nonNull: true);
        if (projection is StringProjection)
            return $"{nsObjectVar}.ToString()";
        if (projection is DataProjection)
            return $"((Foundation.NSData){nsObjectVar}).ToArray()";
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
    /// independent of the freshly-allocated managed wrapper's finalization; the Swift thunk balances
    /// it with <c>takeRetainedValue()</c>. This is the reverse of the forward accessor's
    /// <c>Unmanaged.passRetained</c>(+1) / C# <c>owns: true</c> adoption.
    public string GetReverseReceiverObjCBridgeConversion(string varName)
    {
        var keyToNS = ToNSObject(_keyProjection, "kvp.Key");
        var valToNS = ToNSObject(_valueProjection, "kvp.Value");
        var nsDict = $"Foundation.NSDictionary.FromObjectsAndKeys({varName}.Select(kvp => {valToNS}).ToArray(), {varName}.Select(kvp => {keyToNS}).ToArray())";
        return $"global::Swift.Runtime.Arc.UnknownObjectRetain({nsDict}.Handle)";
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
    /// owns: true balances the +1 retain emitted by the Swift @_cdecl wrapper
    /// (Unmanaged.passRetained(_unwrapped as AnyObject).toOpaque()). Without
    /// owns: true, the NSDictionary (and retained inner key/value NSObjects) leak per call.
    /// </summary>
    private MarshalPlan BuildObjCBridgeReturnPlan(string resultName, ReturnStrategy strategy)
    {
        return new MarshalPlan
        {
            SetupStatements = new List<MarshalStatement>
            {
                new MarshalStatement.Line(
                    $"var {resultName}NSDict = ObjCRuntime.Runtime.GetINativeObject<Foundation.NSDictionary>({resultName}, true)!;"),
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
    /// owns: true balances the +1 retain emitted by the Swift @_cdecl wrapper.
    /// </summary>
    private string BuildObjCBridgeReturnExpression(string containerVar)
    {
        var keyConv = FromNSObject(_keyProjection, "_nsKey");
        var valConv = FromNSObject(_valueProjection, $"_nsDict.ObjectForKey(_nsKey)!");
        // Inline dictionary construction using LINQ
        return $"((Func<IReadOnlyDictionary<{_keyProjection.PublicType}, {_valueProjection.PublicType}>>)(() => {{ " +
               $"var _nsDict = ObjCRuntime.Runtime.GetINativeObject<Foundation.NSDictionary>({containerVar}, true)!; " +
               $"var _dict = new System.Collections.Generic.Dictionary<{_keyProjection.PublicType}, {_valueProjection.PublicType}>((int)_nsDict.Count); " +
               $"foreach (var _nsKey in _nsDict.Keys) _dict[{keyConv}] = {valConv}; " +
               $"return _dict; }}))()";
    }
}
