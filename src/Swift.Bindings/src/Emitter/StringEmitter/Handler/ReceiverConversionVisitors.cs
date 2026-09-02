// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

// Exhaustive IProjectionVisitor<T> replacements for the projection-dispatch switches in
// ProtocolProxyEmitter.Receivers.cs. Each visitor implements one Visit overload per concrete
// projection kind, so adding a new ITypeProjection forces a compile-time decision here instead
// of silently falling through a `_ => null` / `_ => false` arm. The visitors are nested in the
// ProtocolProxyEmitter partial so they can reach the emitter's private receiver-conversion
// helpers (GetReceiverArrayGetterConversion, …) through the captured owner reference. Mirrors
// AccessorConversionVisitors.cs.
public partial class ProtocolProxyEmitter
{
    /// <summary>
    /// Whole-value getter dispatch for a Swift callback receiver: converts the C# idiomatic value
    /// in <c>varName</c> to its Swift ABI carrier. Returns the same expression each former switch
    /// arm produced; unhandled kinds return null (passthrough).
    /// </summary>
    internal sealed class ReceiverGetterConversionVisitor : IProjectionVisitor<string?>
    {
        private readonly string _varName;
        private readonly ProtocolProxyEmitter _owner;
        public ReceiverGetterConversionVisitor(string varName, ProtocolProxyEmitter owner)
        {
            _varName = varName;
            _owner = owner;
        }

        public string? Visit(StringProjection p) => $"new SwiftString({_varName})";
        public string? Visit(DataProjection p) => $"Swift.Foundation.Data.FromByteArray({_varName})";
        public string? Visit(DateProjection p) => $"({_varName} - {DateProjection.SwiftEpoch}).TotalSeconds";
        public string? Visit(NativeRemappedProjection nrp) => nrp.FromFactoryMethod != null
            ? $"{nrp.SwiftWrapperType}.{nrp.FromFactoryMethod}({_varName})"
            : $"new {nrp.SwiftWrapperType}({_varName})";
        // Reverse-dispatch scalar ObjC return: a C#-implemented conformer returns a single ObjC object
        // (URL/NSURLSession/an NSObject subclass), which crosses back to the Swift EveryProtocol thunk
        // as an ObjC pointer. The C# wrapper here is frequently freshly allocated by the conformer and
        // has no guaranteed lifetime once the receiver frame returns, so transfer a +1 ARC retain
        // (Arc.UnknownObjectRetain → the isa-dispatching swift_unknownObjectRetain) to keep the object
        // alive across the boundary; the Swift thunk balances it (takeRetainedValue for the
        // ObjCBridgeable value arm, move() for the ObjCBridged/ObjCRooted raw-buffer arm). Symmetric
        // with the whole-container bridge (see SetProjection.GetReverseReceiverObjCBridgeConversion).
        public string? Visit(ObjCBridgedProjection p) => $"global::Swift.Runtime.Arc.UnknownObjectRetain({_varName}.Handle)";
        public string? Visit(ObjCBridgeableProjection p) => $"global::Swift.Runtime.Arc.UnknownObjectRetain({p.BridgeWriteExpression(_varName)}.Handle)";
        public string? Visit(ObjCRootedClassProjection p) => $"global::Swift.Runtime.Arc.UnknownObjectRetain({_varName}.Handle)";
        public string? Visit(ArrayProjection arr) => _owner.GetReceiverArrayGetterConversion(arr, _varName);
        public string? Visit(DictionaryProjection dict) => _owner.GetReceiverDictGetterConversion(dict, _varName);
        public string? Visit(SetProjection set) => _owner.GetReceiverSetGetterConversion(set, _varName);
        public string? Visit(OptionalProjection opt) => _owner.GetReceiverOptionalGetterConversion(opt, _varName);

        // No whole-value getter conversion — passthrough.
        public string? Visit(BlittableProjection p) => null;
        public string? Visit(BoolProjection p) => null;
        public string? Visit(SimpleEnumProjection p) => null;
        public string? Visit(ClassProjection p) => null;
        public string? Visit(NonFrozenStructProjection p) => null;
        public string? Visit(FrozenWithMemoryProjection p) => null;
        public string? Visit(ExistentialProjection p) => null;
        public string? Visit(ClosureProjection p) => null;
        public string? Visit(AsyncProjection p) => null;
        public string? Visit(TupleProjection p) => null;
        public string? Visit(ResultProjection p) => null;
        public string? Visit(KeyPathProjection p) => null;
    }

    /// <summary>
    /// Whole-value setter dispatch for a Swift callback receiver: converts the Swift ABI value in
    /// <c>varName</c> back to its C# idiomatic form for interface assignment. Mirror of
    /// <see cref="ReceiverGetterConversionVisitor"/> in the setter direction.
    /// </summary>
    internal sealed class ReceiverSetterConversionVisitor : IProjectionVisitor<string?>
    {
        private readonly string _varName;
        private readonly ProtocolProxyEmitter _owner;
        public ReceiverSetterConversionVisitor(string varName, ProtocolProxyEmitter owner)
        {
            _varName = varName;
            _owner = owner;
        }

        public string? Visit(StringProjection p) => $"{_varName}.ToString()";
        public string? Visit(DataProjection p) => $"{_varName}.ToByteArray()";
        public string? Visit(DateProjection p) => $"{DateProjection.SwiftEpoch}.AddSeconds({_varName})";
        public string? Visit(NativeRemappedProjection nrp) => $"{_varName}.{nrp.ToConversionMethod}()";
        public string? Visit(ObjCBridgedProjection objc) => MarshallingHelpers.FormatObjCBridgeCall(objc.PublicType, _varName, nonNull: true);
        public string? Visit(ObjCBridgeableProjection objc) => objc.BridgeReadExpression(_varName, nonNull: true);
        public string? Visit(ArrayProjection arr) => _owner.GetReceiverArraySetterConversion(arr, _varName);
        public string? Visit(DictionaryProjection dict) => _owner.GetReceiverDictSetterConversion(dict, _varName);
        public string? Visit(SetProjection set) => _owner.GetReceiverSetSetterConversion(set, _varName);
        public string? Visit(OptionalProjection opt) => _owner.GetReceiverOptionalSetterConversion(opt, _varName);

        // No whole-value setter conversion — passthrough.
        public string? Visit(BlittableProjection p) => null;
        public string? Visit(BoolProjection p) => null;
        public string? Visit(SimpleEnumProjection p) => null;
        public string? Visit(ClassProjection p) => null;
        public string? Visit(NonFrozenStructProjection p) => null;
        public string? Visit(FrozenWithMemoryProjection p) => null;
        public string? Visit(ExistentialProjection p) => null;
        public string? Visit(ClosureProjection p) => null;
        public string? Visit(AsyncProjection p) => null;
        public string? Visit(ObjCRootedClassProjection p) => null;
        // Per-element lift for tuples whose elements have distinct ABI vs public forms — e.g.
        // (Date, Date) arrives as ValueTuple<double, double> but the interface method takes
        // (DateTimeOffset, DateTimeOffset). GetReturnElementConversion composes each element's
        // Swift→C# conversion and returns null when no element needs one, so pure-blittable
        // tuples keep the passthrough shape.
        public string? Visit(TupleProjection p) => p.GetReturnElementConversion(_varName);
        public string? Visit(ResultProjection p) => null;
        public string? Visit(KeyPathProjection p) => null;
    }

    /// <summary>
    /// Reference-backed copy-out dispatch for a Swift callback receiver: materializes a borrowed C#
    /// class (or optional class) instance from the ABI slot expression. Only class-like kinds (and
    /// optionals wrapping them) produce an expression; everything else returns null.
    /// </summary>
    internal sealed class ReceiverClassCopyOutVisitor : IProjectionVisitor<string?>
    {
        private const string Marshal = "global::Swift.Runtime.InteropServices.SwiftMarshal";
        private readonly string _slotExpr;
        public ReceiverClassCopyOutVisitor(string slotExpr) => _slotExpr = slotExpr;

        public string? Visit(ClassProjection cls) => $"{Marshal}.MarshalBorrowedClassFromSlot<{cls.PublicType}>({_slotExpr})";
        public string? Visit(ObjCRootedClassProjection objc) => $"{Marshal}.MarshalBorrowedClassFromSlot<{objc.PublicType}>({_slotExpr})";
        public string? Visit(OptionalProjection opt) => opt.InnerProjection switch
        {
            ClassProjection innerCls => $"{Marshal}.MarshalBorrowedOptionalClassFromSlot<{innerCls.PublicType}>({_slotExpr})",
            ObjCRootedClassProjection innerObjc => $"{Marshal}.MarshalBorrowedOptionalClassFromSlot<{innerObjc.PublicType}>({_slotExpr})",
            _ => null
        };

        // Not a reference-backed copy-out kind.
        public string? Visit(StringProjection p) => null;
        public string? Visit(BlittableProjection p) => null;
        public string? Visit(BoolProjection p) => null;
        public string? Visit(SimpleEnumProjection p) => null;
        public string? Visit(NonFrozenStructProjection p) => null;
        public string? Visit(FrozenWithMemoryProjection p) => null;
        public string? Visit(ArrayProjection p) => null;
        public string? Visit(DictionaryProjection p) => null;
        public string? Visit(SetProjection p) => null;
        public string? Visit(DataProjection p) => null;
        public string? Visit(ExistentialProjection p) => null;
        public string? Visit(ClosureProjection p) => null;
        public string? Visit(AsyncProjection p) => null;
        public string? Visit(ObjCBridgedProjection p) => null;
        public string? Visit(ObjCBridgeableProjection p) => null;
        public string? Visit(NativeRemappedProjection p) => null;
        public string? Visit(TupleProjection p) => null;
        public string? Visit(DateProjection p) => null;
        public string? Visit(ResultProjection p) => null;
        public string? Visit(KeyPathProjection p) => null;
    }

    /// <summary>
    /// True when a receiver parameter's ABI carrier is a reference-backed collection wrapper
    /// (SwiftArray/SwiftDictionary/SwiftSet) that must be materialized through NewFromPayload rather
    /// than Unsafe.Read. The top-level projection KIND is the reliable discriminator.
    /// </summary>
    internal sealed class ReceiverParamNeedsObjectMarshalVisitor : IProjectionVisitor<bool>
    {
        public bool Visit(ArrayProjection p) => true;
        public bool Visit(DictionaryProjection p) => true;
        public bool Visit(SetProjection p) => true;

        public bool Visit(StringProjection p) => false;
        public bool Visit(BlittableProjection p) => false;
        public bool Visit(BoolProjection p) => false;
        public bool Visit(SimpleEnumProjection p) => false;
        public bool Visit(ClassProjection p) => false;
        public bool Visit(NonFrozenStructProjection p) => false;
        public bool Visit(FrozenWithMemoryProjection p) => false;
        public bool Visit(DataProjection p) => false;
        public bool Visit(OptionalProjection p) => false;
        public bool Visit(ExistentialProjection p) => false;
        public bool Visit(ClosureProjection p) => false;
        public bool Visit(AsyncProjection p) => false;
        public bool Visit(ObjCBridgedProjection p) => false;
        public bool Visit(ObjCBridgeableProjection p) => false;
        public bool Visit(ObjCRootedClassProjection p) => false;
        public bool Visit(NativeRemappedProjection p) => false;
        public bool Visit(TupleProjection p) => false;
        public bool Visit(DateProjection p) => false;
        public bool Visit(ResultProjection p) => false;
        public bool Visit(KeyPathProjection p) => false;
    }
}
