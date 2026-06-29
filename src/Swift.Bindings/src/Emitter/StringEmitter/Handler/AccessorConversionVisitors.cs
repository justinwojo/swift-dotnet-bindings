// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared visitor for getter conversion dispatch in property and subscript accessors.
/// Replaces duplicated switches in PropertyHandler and SubscriptHandler.
/// </summary>
internal class AccessorGetterConversionVisitor : IProjectionVisitor<(string? conversion, bool requiresDisposal)>
{
    private readonly string _resultExpr;
    public AccessorGetterConversionVisitor(string resultExpr) => _resultExpr = resultExpr;

    public (string?, bool) Visit(StringProjection p) => ($"{_resultExpr}.ToString()", true);
    public (string?, bool) Visit(DataProjection p) => ($"{_resultExpr}.ToByteArray()", false);
    public (string?, bool) Visit(NativeRemappedProjection p) => ($"{_resultExpr}.{p.ToConversionMethod}()", p.RequiresDisposal);
    public (string?, bool) Visit(DateProjection p) => ($"{DateProjection.SwiftEpoch}.AddSeconds({_resultExpr})", false);
    public (string?, bool) Visit(OptionalProjection p) => OptionalAccessorGetterConversion(p, _resultExpr);
    public (string?, bool) Visit(ArrayProjection p) => ArrayGetterConversion(p, _resultExpr);
    public (string?, bool) Visit(DictionaryProjection p) => DictGetterConversion(p, _resultExpr);
    public (string?, bool) Visit(SetProjection p) => SetGetterConversion(p, _resultExpr);

    // Passthrough — no conversion needed
    public (string?, bool) Visit(BlittableProjection p) => (null, false);
    public (string?, bool) Visit(BoolProjection p) => (null, false);
    public (string?, bool) Visit(SimpleEnumProjection p) => (null, false);
    public (string?, bool) Visit(ClassProjection p) => (null, false);
    public (string?, bool) Visit(NonFrozenStructProjection p) => (null, false);
    public (string?, bool) Visit(FrozenWithMemoryProjection p) => (null, false);
    public (string?, bool) Visit(ExistentialProjection p) => (null, false);
    public (string?, bool) Visit(ClosureProjection p) => (null, false);
    public (string?, bool) Visit(AsyncProjection p) => (null, false);
    public (string?, bool) Visit(ObjCBridgedProjection p) => (null, false);
    public (string?, bool) Visit(ObjCBridgeableProjection p) => (null, false);
    public (string?, bool) Visit(ObjCRootedClassProjection p) => (null, false);
    public (string?, bool) Visit(TupleProjection p) => (null, false);
    public (string?, bool) Visit(ResultProjection p) => (null, false);
    public (string?, bool) Visit(KeyPathProjection p) => (null, false);

    // --- Shared getter helpers ---

    internal static (string?, bool) ArrayGetterConversion(ArrayProjection arr, string resultExpr)
    {
        // ObjC bridge: resultExpr is IntPtr (NSArray handle) — convert to typed list
        if (arr.UsesObjCContainerBridge)
        {
            var conv = arr.GetReturnContainerConversion(resultExpr);
            return (conv, false);
        }
        var elemConv = arr.ElementProjection.GetReturnElementConversion("e");
        if (elemConv != null)
            return ($"{resultExpr}.AsProjected(e => {elemConv})", false);
        return (null, false);
    }

    internal static (string?, bool) DictGetterConversion(DictionaryProjection dict, string resultExpr)
    {
        // ObjC bridge: resultExpr is IntPtr (NSDictionary handle) — convert to typed dictionary
        if (dict.UsesObjCContainerBridge)
        {
            var conv = dict.GetReturnContainerConversion(resultExpr);
            return (conv, false);
        }

        var keyConv = dict.KeyProjection.GetReturnElementConversion("k");
        var valConv = dict.ValueProjection.GetReturnElementConversion("v");
        if (keyConv == null && valConv == null)
            return (null, false);

        // Route through BuildAsProjected (the single owner of the AsProjected shape) instead of inlining
        // it here, so the invariant value-slot cast in CastValueSelectorBody is applied. A nested-container
        // value — e.g. the inner dictionary of [String: [String: Any]] — projects to a CONCRETE
        // Dictionary<...>, which the INVARIANT outer IReadOnlyDictionary value slot rejects (CS0266) unless
        // cast to its declared interface type. The receiver dict-setter path already delegates the same way.
        return ($"{resultExpr}{dict.BuildAsProjected(keyConv, valConv)}", false);
    }

    internal static (string?, bool) SetGetterConversion(SetProjection set, string resultExpr)
    {
        // ObjC bridge: resultExpr is IntPtr (NSSet handle) — convert to typed HashSet
        if (set.UsesObjCContainerBridge)
        {
            var conv = set.GetReturnContainerConversion(resultExpr);
            return (conv, false);
        }
        var elemConv = set.ElementProjection.GetReturnElementConversion("e");
        if (elemConv != null)
            return ($"{resultExpr}.Select(e => {elemConv}).ToHashSet()", true);
        return (null, false);
    }

    internal static (string?, bool) OptionalAccessorGetterConversion(OptionalProjection opt, string resultExpr)
    {
        return opt.InnerProjection.Accept(new OptionalAccessorGetterVisitor(resultExpr));
    }

    internal static (string?, bool) OptionalContainerGetterConversion(
        ITypeProjection innerContainer, string resultExpr)
    {
        // ObjC bridge containers use nullable pointer ABI — resultExpr is IntPtr (0 for nil).
        if (innerContainer.UsesObjCContainerBridge)
        {
            var idiomaticType = innerContainer.PublicType;
            var conv = innerContainer.GetReturnContainerConversion(resultExpr);
            return ($"({resultExpr} == IntPtr.Zero ? ({idiomaticType}?)null : {conv})", false);
        }

        var innerHasConversion = innerContainer switch
        {
            ArrayProjection arr => arr.ElementProjection.GetReturnElementConversion("e") != null,
            DictionaryProjection dict => dict.KeyProjection.GetReturnElementConversion("k") != null
                || dict.ValueProjection.GetReturnElementConversion("v") != null,
            SetProjection set => set.ElementProjection.GetReturnElementConversion("e") != null,
            _ => false
        };
        var idiomaticTypeStd = innerContainer.PublicType;
        var someExpr = innerHasConversion
            ? innerContainer.GetReturnContainerConversion($"{resultExpr}.Some") ?? $"{resultExpr}.Some"
            : $"{resultExpr}.Some";
        return ($"({resultExpr}.Case == Swift.SwiftOptionalCases.None ? ({idiomaticTypeStd}?)null : {someExpr})", true);
    }
}

/// <summary>
/// Visitor for the inner projection of Optional in getter context.
/// Dispatches on the inner type to determine how to unwrap Optional&lt;T&gt; for the accessor return.
/// </summary>
internal class OptionalAccessorGetterVisitor : IProjectionVisitor<(string? conversion, bool requiresDisposal)>
{
    private readonly string _resultExpr;
    public OptionalAccessorGetterVisitor(string resultExpr) => _resultExpr = resultExpr;

    public (string?, bool) Visit(StringProjection p) =>
        ($"((SwiftString?){_resultExpr})?.ToString()", true);
    public (string?, bool) Visit(DataProjection p) =>
        ($"((Swift.Foundation.Data?){_resultExpr})?.ToByteArray()", true);
    public (string?, bool) Visit(NativeRemappedProjection p) =>
        ($"(({p.SwiftWrapperType}?){_resultExpr})?.{p.ToConversionMethod}()", true);
    public (string?, bool) Visit(DateProjection p) =>
        ($"((double?){_resultExpr}) is {{}} {_resultExpr}DateVal ? (System.DateTimeOffset?){DateProjection.SwiftEpoch}.AddSeconds({_resultExpr}DateVal) : null", false);
    public (string?, bool) Visit(ClosureProjection p) => (null, false);
    // ObjC types: Swift @_cdecl wrapper returns passRetained (+1).
    // ownsReference=true transfers +1 ownership to the wrapper without extra DangerousRetain.
    public (string?, bool) Visit(ObjCBridgedProjection p) =>
        ($"({_resultExpr} == IntPtr.Zero ? null : {MarshallingHelpers.FormatObjCBridgeCall(p.PublicType, _resultExpr, ownsReference: true)})", false);
    public (string?, bool) Visit(ObjCBridgeableProjection p) =>
        ($"({_resultExpr} == IntPtr.Zero ? null : {MarshallingHelpers.FormatObjCBridgeCall(p.PublicType, _resultExpr, ownsReference: true)})", false);
    public (string?, bool) Visit(ArrayProjection p) =>
        AccessorGetterConversionVisitor.OptionalContainerGetterConversion(p, _resultExpr);
    public (string?, bool) Visit(DictionaryProjection p) =>
        AccessorGetterConversionVisitor.OptionalContainerGetterConversion(p, _resultExpr);
    public (string?, bool) Visit(SetProjection p) =>
        AccessorGetterConversionVisitor.OptionalContainerGetterConversion(p, _resultExpr);

    // Existential/non-frozen struct: accessor already returns the projected type — no conversion needed
    public (string?, bool) Visit(ExistentialProjection p) => (null, false);
    public (string?, bool) Visit(NonFrozenStructProjection p) => (null, false);

    // Reference class types: accessor returns IntPtr (nullable pointer ABI), convert to T?
    // Swift @_cdecl wrapper returns passRetained (+1). ClassProjection: SwiftClassHandle takes ownership.
    // ObjCRooted: ownsReference=true transfers +1 to wrapper without extra DangerousRetain.
    public (string?, bool) Visit(ClassProjection p) =>
        ($"({_resultExpr} == IntPtr.Zero ? null : ({p.PublicType})SwiftMarshal.MarshalFromSwiftObject<{p.MarshalFromSwiftType}>({_resultExpr}))", false);
    // KeyPath family: same shape as ClassProjection. Swift @_cdecl wrapper returns
    // passRetained on the Some path; the SafeHandle-derived wrapper adopts the retain
    // via NewFromPayload. Use the public typed wrapper (Swift.KeyPath<TRoot,TValue> etc.).
    public (string?, bool) Visit(KeyPathProjection p) =>
        ($"({_resultExpr} == IntPtr.Zero ? null : ({p.PublicType})SwiftMarshal.MarshalFromSwiftObject<{p.MarshalFromSwiftType}>({_resultExpr}))", false);
    public (string?, bool) Visit(ObjCRootedClassProjection p) =>
        ($"({_resultExpr} == IntPtr.Zero ? null : {MarshallingHelpers.FormatObjCBridgeCall(p.PublicType, _resultExpr, ownsReference: true)})", false);

    // Default: cast to nullable public type.
    // Generic param: accessor already returns TValue? directly via decomposed buffer path
    // (UsesCdeclPropertyWrapper + IsDecomposed), so no SwiftOptional wrap is needed.
    public (string?, bool) Visit(BlittableProjection p) => p.IsGenericParameter ? (null, false) : DefaultCast(p);
    public (string?, bool) Visit(BoolProjection p) => DefaultCast(p);
    public (string?, bool) Visit(SimpleEnumProjection p) => DefaultCast(p);
    public (string?, bool) Visit(FrozenWithMemoryProjection p) => DefaultCast(p);
    public (string?, bool) Visit(AsyncProjection p) => DefaultCast(p);
    public (string?, bool) Visit(OptionalProjection p) => DefaultCast(p);
    public (string?, bool) Visit(TupleProjection p) => DefaultCast(p);
    public (string?, bool) Visit(ResultProjection p) => DefaultCast(p);

    // Use explicit HasValue/Some check instead of implicit operator cast.
    // The implicit operator T?(SwiftOptional<T>) is broken for value types:
    // T is unconstrained, so T? in IL is T (not Nullable<T>). default(T) returns 0/false
    // instead of null, causing None to appear as Some(0).
    // Note: `default` resolves to null for concrete Nullable<T> (int?, bool?, etc.) but
    // to default(T) for unconstrained generic T? — the generic case is a known limitation.
    private (string?, bool) DefaultCast(ITypeProjection inner) =>
        ($"({_resultExpr}?.HasValue == true ? ({inner.PublicType}?){_resultExpr}.Some : default)", true);
}

/// <summary>
/// Shared visitor for setter conversion dispatch in property and subscript accessors.
/// Replaces duplicated switches in PropertyHandler and SubscriptHandler.
/// </summary>
internal class AccessorSetterConversionVisitor : IProjectionVisitor<(string? conversion, bool requiresDisposal)>
{
    private readonly string _valueExpr;
    public AccessorSetterConversionVisitor(string valueExpr) => _valueExpr = valueExpr;

    public (string?, bool) Visit(StringProjection p) => ($"new SwiftString({_valueExpr})", true);
    public (string?, bool) Visit(DataProjection p) => ($"Swift.Foundation.Data.FromByteArray({_valueExpr})", false);
    public (string?, bool) Visit(NativeRemappedProjection p) => (
        p.FromFactoryMethod != null
            ? $"{p.SwiftWrapperType}.{p.FromFactoryMethod}({_valueExpr})"
            : $"new {p.SwiftWrapperType}({_valueExpr})",
        p.RequiresDisposal);
    public (string?, bool) Visit(DateProjection p) => ($"({_valueExpr} - {DateProjection.SwiftEpoch}).TotalSeconds", false);
    public (string?, bool) Visit(ArrayProjection p) => ArraySetterConversion(p, _valueExpr);
    public (string?, bool) Visit(DictionaryProjection p) => DictSetterConversion(p, _valueExpr);
    public (string?, bool) Visit(SetProjection p) => SetSetterConversion(p, _valueExpr);
    public (string?, bool) Visit(OptionalProjection p) => OptionalSetterConversion(p, _valueExpr);

    // Passthrough — no conversion needed
    public (string?, bool) Visit(BlittableProjection p) => (null, false);
    public (string?, bool) Visit(BoolProjection p) => (null, false);
    public (string?, bool) Visit(SimpleEnumProjection p) => (null, false);
    public (string?, bool) Visit(ClassProjection p) => (null, false);
    public (string?, bool) Visit(NonFrozenStructProjection p) => (null, false);
    public (string?, bool) Visit(FrozenWithMemoryProjection p) => (null, false);
    public (string?, bool) Visit(ExistentialProjection p) => (null, false);
    public (string?, bool) Visit(ClosureProjection p) => (null, false);
    public (string?, bool) Visit(AsyncProjection p) => (null, false);
    public (string?, bool) Visit(ObjCBridgedProjection p) => (null, false);
    public (string?, bool) Visit(ObjCBridgeableProjection p) => (null, false);
    public (string?, bool) Visit(ObjCRootedClassProjection p) => (null, false);
    public (string?, bool) Visit(TupleProjection p) => (null, false);
    public (string?, bool) Visit(ResultProjection p) => (null, false);
    public (string?, bool) Visit(KeyPathProjection p) => (null, false);

    // --- Shared setter helpers ---

    internal static (string?, bool) ArraySetterConversion(ArrayProjection arr, string valueExpr)
    {
        // ObjC bridge: create NSArray and dispose after use. PropertyHandler extracts .Handle
        // via the ObjC container bridge path when requiresDisposal=true.
        // For nested containers (e.g., [[URL]]), recursively convert inner elements.
        // Leaf ObjCBridgeable elements (NSUrl) are already NSObject — no inner conversion.
        // Note: inner wrappers created in Select() rely on GC — single-expression accessor
        // context has no statement boundary for using/try-finally.
        if (arr.UsesObjCContainerBridge)
        {
            if (arr.ElementProjection is ArrayProjection or DictionaryProjection or SetProjection
                && arr.ElementProjection.UsesObjCContainerBridge)
            {
                var innerConv = arr.ElementProjection.GetParameterElementConversion("e");
                if (innerConv != null)
                    return ($"Foundation.NSArray.FromNSObjects({valueExpr}.Select(e => (Foundation.NSObject){innerConv}).ToArray())", true);
            }
            return ($"Foundation.NSArray.FromNSObjects({valueExpr}.ToArray())", true);
        }

        // Existential elements ride the owned (+1) carrier (stride-correct type + owned mint): the
        // SwiftArray store + its value-witness destroy balance an independent retain rather than
        // over-releasing the proxy's sole +1. No-op for non-existential elements; mirrors the forward
        // ArrayProjection param path. The exclusion list (Class/KeyPath/NonFrozenStruct/ObjCRooted) still
        // returns null so the container holds typed wrappers directly (struct-by-value / nil-ptr ABI).
        var rawElem = ExistentialElementCarrier.CarrierType(arr.ElementProjection, arr.ElementProjection.MarshalFromSwiftType);
        var elemConv = arr.ElementProjection is ClassProjection or KeyPathProjection or NonFrozenStructProjection or ObjCRootedClassProjection
            ? null
            : ExistentialElementCarrier.ParamConversion(arr.ElementProjection, "e");
        if (elemConv != null)
            return ($"SwiftArray<{rawElem}>.FromEnumerable({valueExpr}.Select(e => {elemConv}))", true);
        return ($"SwiftArray<{rawElem}>.FromEnumerable({valueExpr})", true);
    }

    internal static (string?, bool) DictSetterConversion(DictionaryProjection dict, string valueExpr)
    {
        // ObjC bridge: create NSDictionary and dispose after use. PropertyHandler extracts .Handle.
        if (dict.UsesObjCContainerBridge)
        {
            var keyToNS = DictionaryProjection.ToNSObject(dict.KeyProjection, "kvp.Key");
            var valToNS = DictionaryProjection.ToNSObject(dict.ValueProjection, "kvp.Value");
            return ($"Foundation.NSDictionary.FromObjectsAndKeys({valueExpr}.Select(kvp => {valToNS}).ToArray(), {valueExpr}.Select(kvp => {keyToNS}).ToArray())", true);
        }

        // Keys can never be existential (`any P` is not Hashable), so only the VALUE rides the owned
        // carrier; no-op for non-existential values. Mirrors the forward DictionaryProjection param path.
        var rawK = dict.KeyProjection.MarshalFromSwiftType;
        var rawV = ExistentialElementCarrier.CarrierType(dict.ValueProjection, dict.ValueProjection.MarshalFromSwiftType);
        var keyConv = dict.KeyProjection is ClassProjection or KeyPathProjection or NonFrozenStructProjection or ObjCRootedClassProjection
            ? null
            : dict.KeyProjection.GetParameterElementConversion("kvp.Key");
        var valConv = dict.ValueProjection is ClassProjection or KeyPathProjection or NonFrozenStructProjection or ObjCRootedClassProjection
            ? null
            : ExistentialElementCarrier.ParamConversion(dict.ValueProjection, "kvp.Value");
        if (keyConv != null || valConv != null)
        {
            var keyExpr = keyConv ?? "kvp.Key";
            var valExpr = valConv ?? "kvp.Value";
            return ($"SwiftDictionary<{rawK}, {rawV}>.FromDictionary({valueExpr}.Select(kvp => new KeyValuePair<{rawK}, {rawV}>({keyExpr}, {valExpr})))", true);
        }
        return ($"SwiftDictionary<{rawK}, {rawV}>.FromDictionary({valueExpr})", true);
    }

    internal static (string?, bool) SetSetterConversion(SetProjection set, string valueExpr)
    {
        // ObjC bridge: create NSSet and dispose after use. PropertyHandler extracts .Handle.
        // For nested containers (e.g., Set<[URL]>), recursively convert inner elements.
        if (set.UsesObjCContainerBridge)
        {
            if (set.ElementProjection is ArrayProjection or DictionaryProjection or SetProjection
                && set.ElementProjection.UsesObjCContainerBridge)
            {
                var innerConv = set.ElementProjection.GetParameterElementConversion("e");
                if (innerConv != null)
                    return ($"new Foundation.NSSet({valueExpr}.Select(e => (Foundation.NSObject){innerConv}).ToArray())", true);
            }
            return ($"new Foundation.NSSet({valueExpr}.ToArray())", true);
        }

        // Existential elements ride the owned (+1) carrier; no-op for non-existential. Mirrors the
        // forward SetProjection param path (and the exclusion list keeps typed-wrapper-by-value).
        var rawElem = ExistentialElementCarrier.CarrierType(set.ElementProjection, set.ElementProjection.MarshalFromSwiftType);
        var elemConv = set.ElementProjection is ClassProjection or KeyPathProjection or NonFrozenStructProjection or ObjCRootedClassProjection
            ? null
            : ExistentialElementCarrier.ParamConversion(set.ElementProjection, "e");
        if (elemConv != null)
            return ($"SwiftSet<{rawElem}>.FromEnumerable({valueExpr}.Select(e => {elemConv}))", true);
        return ($"SwiftSet<{rawElem}>.FromEnumerable({valueExpr})", true);
    }

    internal static (string?, bool) OptionalSetterConversion(OptionalProjection opt, string valueExpr)
    {
        var inner = opt.InnerProjection;

        // Closure inner — passthrough, accessor methods handle their own marshalling
        if (inner is ClosureProjection)
            return (null, false);

        // Existential inner — passthrough. Optional existentials use nullable interface ABI.
        if (inner is ExistentialProjection)
            return (null, false);

        var optType = inner.MarshalFromSwiftType;

        // ObjC bridge containers use nullable pointer ABI — no SwiftOptional wrapper needed.
        // Setter conversions now return the collection object (requiresDisposal=true), so
        // extract .Handle in the ternary for the IntPtr P/Invoke.
        // Note: the optional path is inline single-expression (can't use 'using'), so these
        // inner collections rely on GC/finalizer. This matches the pre-disposal behavior.
        if (inner.UsesObjCContainerBridge)
        {
            if (inner is ArrayProjection arrBridge)
            {
                var (arrConv, _) = ArraySetterConversion(arrBridge, $"{valueExpr}Val");
                return ($"({valueExpr} is {{}} {valueExpr}Val ? {arrConv}.Handle : IntPtr.Zero)", false);
            }
            if (inner is DictionaryProjection dictBridge)
            {
                var (dictConv, _) = DictSetterConversion(dictBridge, $"{valueExpr}Val");
                return ($"({valueExpr} is {{}} {valueExpr}Val ? {dictConv}.Handle : IntPtr.Zero)", false);
            }
            if (inner is SetProjection setBridge)
            {
                var (setConv, _) = SetSetterConversion(setBridge, $"{valueExpr}Val");
                return ($"({valueExpr} is {{}} {valueExpr}Val ? {setConv}.Handle : IntPtr.Zero)", false);
            }
        }

        // Container inner (Array, Dictionary, Set) — wrap with full container creation
        if (inner is ArrayProjection arr)
        {
            var (arrConv, _) = ArraySetterConversion(arr, $"{valueExpr}Val");
            return ($"({valueExpr} is {{}} {valueExpr}Val ? SwiftOptional<{optType}>.NewSome({arrConv}) : SwiftOptional<{optType}>.NewNone())", true);
        }
        if (inner is DictionaryProjection dict)
        {
            var (dictConv, _) = DictSetterConversion(dict, $"{valueExpr}Val");
            return ($"({valueExpr} is {{}} {valueExpr}Val ? SwiftOptional<{optType}>.NewSome({dictConv}) : SwiftOptional<{optType}>.NewNone())", true);
        }
        if (inner is SetProjection set)
        {
            var (setConv, _) = SetSetterConversion(set, $"{valueExpr}Val");
            return ($"({valueExpr} is {{}} {valueExpr}Val ? SwiftOptional<{optType}>.NewSome({setConv}) : SwiftOptional<{optType}>.NewNone())", true);
        }

        // Class/NonFrozenStruct/KeyPath inner — pass the public type as-is. The KeyPath
        // wrappers implement ISwiftObject.MarshalToSwift so SwiftOptional<KeyPath<R,V>>
        // serialises the +retained pointer into the optional payload buffer correctly.
        if (inner is ClassProjection or KeyPathProjection or NonFrozenStructProjection)
            return ($"({valueExpr} is {{}} {valueExpr}Val ? SwiftOptional<{optType}>.NewSome({valueExpr}Val) : SwiftOptional<{optType}>.NewNone())", true);

        // ObjC bridged/bridgeable inner — nullable pointer ABI, no SwiftOptional wrapper needed
        if (inner is ObjCBridgedProjection or ObjCBridgeableProjection)
            return ($"({valueExpr} is {{}} {valueExpr}Val ? {valueExpr}Val.Handle : IntPtr.Zero)", false);

        // ObjC-rooted inner — pass as-is
        if (inner is ObjCRootedClassProjection)
            return ($"({valueExpr} is {{}} {valueExpr}Val ? SwiftOptional<{optType}>.NewSome({valueExpr}Val) : SwiftOptional<{optType}>.NewNone())", true);

        // Element conversion (String, NativeRemapped, etc.)
        var innerConv = inner.GetParameterElementConversion($"{valueExpr}Val");
        if (innerConv != null)
            return ($"({valueExpr} is {{}} {valueExpr}Val ? SwiftOptional<{optType}>.NewSome({innerConv}) : SwiftOptional<{optType}>.NewNone())", true);

        // Simple inner type (blittable, enum)
        return ($"({valueExpr} is {{}} {valueExpr}Val ? SwiftOptional<{optType}>.NewSome({valueExpr}Val) : SwiftOptional<{optType}>.NewNone())", true);
    }
}
