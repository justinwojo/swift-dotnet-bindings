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
    /// How a reverse-dispatch receiver must read one borrowed Swift value slot. The Swift conformance
    /// passes every receiver argument by address: it copies the value into its own local and
    /// deinitializes that local once the receiver returns, so the read has to suit the ABI carrier the
    /// parameter projects to and must leave the source slot untouched.
    /// </summary>
    internal enum ReceiverSlotReadKind
    {
        /// <summary>
        /// The proxy-local <c>MarshalFromSwift&lt;T&gt;</c> (a plain <c>Unsafe.Read&lt;T&gt;</c>) is sound:
        /// the carrier is a blittable value whose C# layout matches Swift's, so a byte-for-byte read
        /// carries no managed reference and no ARC obligation.
        /// </summary>
        RawRead,

        /// <summary>
        /// <c>SwiftMarshal.MarshalFromSwiftObject&lt;T&gt;</c>: the carrier is a reference-backed
        /// <c>ISwiftObject</c> container wrapper whose <c>NewFromPayload</c> takes its own owned copy of
        /// the payload, leaving the borrowed slot intact.
        /// </summary>
        ObjectMarshal,

        /// <summary>
        /// <c>SwiftMarshal.MarshalCopiedValueFromSlot&lt;T&gt;</c>: either the carrier is a managed
        /// wrapper class whose payload has to be value-witness-copied out of the borrowed slot before
        /// the slot dies, or it is a C# enum whose Swift discriminator is narrower than the C# backing
        /// type. A raw read of the first reinterprets Swift value bytes as a managed object reference;
        /// a raw read of the second over-reads past the discriminator into the neighbouring value.
        /// </summary>
        CopiedValue,
    }

    /// <summary>
    /// Decides, per projection kind, how a receiver parameter's borrowed ABI slot must be read. The
    /// top-level projection KIND is the reliable discriminator, and the visitor is compile-time
    /// exhaustive so a new <c>ITypeProjection</c> forces an explicit ownership decision here.
    /// </summary>
    internal sealed class ReceiverSlotReadKindVisitor : IProjectionVisitor<ReceiverSlotReadKind>
    {
        // Reference-backed ISwiftObject containers: NewFromPayload copies the storage reference out
        // under its own retain, so the borrowed slot's own reference stays balanced.
        public ReceiverSlotReadKind Visit(ArrayProjection p) => ReceiverSlotReadKind.ObjectMarshal;
        public ReceiverSlotReadKind Visit(DictionaryProjection p) => ReceiverSlotReadKind.ObjectMarshal;
        public ReceiverSlotReadKind Visit(SetProjection p) => ReceiverSlotReadKind.ObjectMarshal;
        // SwiftString: the method-parameter and property-setter sites intercept strings upstream and
        // emit this very read, so keeping the residual sites (subscript index/value) on the same call
        // gives every receiver site one string read shape.
        public ReceiverSlotReadKind Visit(StringProjection p) => ReceiverSlotReadKind.ObjectMarshal;

        // Blittable values: the C# layout matches Swift's, with no managed reference and no ARC.
        public ReceiverSlotReadKind Visit(BlittableProjection p) => ReceiverSlotReadKind.RawRead;
        public ReceiverSlotReadKind Visit(BoolProjection p) => ReceiverSlotReadKind.RawRead;
        // Swift Date crosses as a bare Double (seconds relative to the reference date).
        public ReceiverSlotReadKind Visit(DateProjection p) => ReceiverSlotReadKind.RawRead;
        // Existential containers are fixed-layout blittable structs (or a bare pointer for an ObjC
        // existential) whose words the receiver takes as-is — the one carrier that must NOT be copied
        // through its own type metadata.
        public ReceiverSlotReadKind Visit(ExistentialProjection p) => ReceiverSlotReadKind.RawRead;
        // Closure parameters are expanded into (function pointer, context) machine words and are
        // intercepted upstream; any residual read is of a blittable closure-data struct.
        public ReceiverSlotReadKind Visit(ClosureProjection p) => ReceiverSlotReadKind.RawRead;
        // Async is a return-side shape; it never produces a receiver parameter slot.
        public ReceiverSlotReadKind Visit(AsyncProjection p) => ReceiverSlotReadKind.RawRead;
        // ObjC-bridged/bridgeable values cross as one ObjC pointer word and are bridged after the read.
        public ReceiverSlotReadKind Visit(ObjCBridgedProjection p) => ReceiverSlotReadKind.RawRead;
        public ReceiverSlotReadKind Visit(ObjCBridgeableProjection p) => ReceiverSlotReadKind.RawRead;

        // A C# `enum : int` whose Swift discriminator occupies fewer bytes. The runtime read consults
        // the enum's Swift metadata and reads exactly the Swift width instead of pulling four bytes out
        // of a one-byte slot and taking three bytes of the neighbouring value with it.
        public ReceiverSlotReadKind Visit(SimpleEnumProjection p) => ReceiverSlotReadKind.CopiedValue;

        // Managed wrapper classes. Reading these bitwise reinterprets Swift's first payload word as a
        // managed object reference. Plain classes are additionally intercepted upstream by the borrowed
        // class copy-out; the arms here keep every remaining receiver site honest.
        public ReceiverSlotReadKind Visit(ClassProjection p) => ReceiverSlotReadKind.CopiedValue;
        public ReceiverSlotReadKind Visit(ObjCRootedClassProjection p) => ReceiverSlotReadKind.CopiedValue;
        // Non-frozen structs AND associated-value enums both project here, as adopting wrappers. The
        // adopting handle is why the copy-out cannot construct straight over the slot: the wrapper
        // would take ownership of, and later free, Swift's own storage.
        public ReceiverSlotReadKind Visit(NonFrozenStructProjection p) => ReceiverSlotReadKind.CopiedValue;
        // Frozen structs carrying reference fields, projected as a copying wrapper class.
        public ReceiverSlotReadKind Visit(FrozenWithMemoryProjection p) => ReceiverSlotReadKind.CopiedValue;
        // Foundation value wrappers project to their Swift-side wrapper type, which is a managed class
        // whenever the underlying Swift value is non-frozen.
        public ReceiverSlotReadKind Visit(DataProjection p) => ReceiverSlotReadKind.CopiedValue;
        public ReceiverSlotReadKind Visit(NativeRemappedProjection p) => ReceiverSlotReadKind.CopiedValue;
        // The ABI carrier is always the SwiftOptional wrapper class, even where the inner value is
        // nil-pointer-optimized, so the tagged carrier is never bitwise-readable. Optional-of-class and
        // optional-of-ObjC-bridgeable-value are intercepted upstream by their own coupled reads.
        public ReceiverSlotReadKind Visit(OptionalProjection p) => ReceiverSlotReadKind.CopiedValue;
        // SwiftResult is a copying ISwiftObject wrapper class.
        public ReceiverSlotReadKind Visit(ResultProjection p) => ReceiverSlotReadKind.CopiedValue;
        // A key path is a managed handle-derived class, so a bitwise read produces a bogus reference.
        // The runtime copy-out has no key-path arm, which surfaces as a diagnosable throw rather than
        // the silent corruption a raw read produces.
        public ReceiverSlotReadKind Visit(KeyPathProjection p) => ReceiverSlotReadKind.CopiedValue;

        // A tuple is only bitwise-readable when every element is. One wrapper-class element (a tuple
        // carrying a String or a non-frozen struct) makes the whole read a managed reinterpretation, so
        // defer to the runtime tuple walk, which copies each element out of the borrowed slot.
        public ReceiverSlotReadKind Visit(TupleProjection p) =>
            p.ElementProjections.All(e => e.Accept(new ReceiverSlotReadKindVisitor()) == ReceiverSlotReadKind.RawRead)
                ? ReceiverSlotReadKind.RawRead
                : ReceiverSlotReadKind.CopiedValue;
    }
}
