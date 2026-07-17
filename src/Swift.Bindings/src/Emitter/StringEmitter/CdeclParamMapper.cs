// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared utility for mapping Swift parameters to their @_cdecl-compatible C ABI representations.
/// Handles all 16 type categories: primitives, AnyObject, protocol existentials, optional reference types,
/// optional blittable primitives, generic containers, Foundation.Date, Foundation.Data, String, classes,
/// protocol/existential TypeRecords, simple enums, complex enums, non-frozen structs, frozen structs,
/// and fallback.
///
/// Extracted from ConstructorWrapperEmitter to eliminate cross-emitter dependencies — this logic
/// is used by 10+ emitters and is conceptually a shared marshalling utility, not constructor-specific.
/// </summary>
public static class CdeclParamMapper
{
    /// <summary>
    /// Maps a parameter to its @_cdecl-compatible Swift type, reconstruction code,
    /// and call argument expression.
    /// </summary>
    /// <param name="arg">The argument declaration to map.</param>
    /// <param name="label">The parameter label to use in the wrapper.</param>
    /// <param name="env">The method environment providing type database context.</param>
    /// <param name="omitLabels">When true, omit argument labels (used when calling _dbw_init_* which uses _ for all params).</param>
    /// <param name="useUtf8Strings">When true, String params use UTF-8 ptr+len (for subscript/enum case wrappers
    /// where C# already sends UTF-8). When false, uses two Int words matching SwiftString.Buffer layout.</param>
    /// <param name="escapeReservedCollision">When true (default), a <paramref name="label"/> that collides with a
    /// synthetic the caller injects into the same wrapper signature (resultPtr, self_, errorOut, …) is renamed so the
    /// two bindings don't duplicate. Pass <c>false</c> when <paramref name="label"/> is itself one of those synthetics
    /// deliberately routed through this mapper (e.g. the property/subscript setter's <c>newValue</c> value parameter,
    /// which the caller references by its bare name in the wrapper body). Escaping a synthetic's own emission renames
    /// the parameter declaration while the body keeps the bare name → "cannot find 'newValue' in scope".</param>
    internal static (string cdeclParam, string? reconstruction, string callArg) Map(
        ArgumentDecl arg, string label, MethodEnvironment env, bool omitLabels = false, bool useUtf8Strings = false,
        bool escapeReservedCollision = true, IReadOnlySet<string>? reservedSiblings = null)
    {
        var d = Describe(arg, label, env, omitLabels, useUtf8Strings, escapeReservedCollision, reservedSiblings);
        return (d.CdeclParam, d.Reconstruction, d.CallArg);
    }

    /// <summary>
    /// The single per-parameter @_cdecl lowering decision. Classifies <paramref name="arg"/> into
    /// exactly one <see cref="CdeclParamCategory"/> and returns the Swift-side wrapper signature
    /// text, body reconstruction, and call-site expression for that category (plus the cross-file
    /// multi-word name contract for the few categories that split into several C ABI words). The
    /// 3-tuple <see cref="Map"/> shim projects the Swift-text fields for the existing callers.
    /// Parameter semantics are identical to <see cref="Map"/>.
    /// </summary>
    internal static CdeclLoweringDescriptor Describe(
        ArgumentDecl arg, string label, MethodEnvironment env, bool omitLabels = false, bool useUtf8Strings = false,
        bool escapeReservedCollision = true, IReadOnlySet<string>? reservedSiblings = null, bool isInout = false)
    {
        // Common arms carry no inout write-back.
        static CdeclLoweringDescriptor Simple(CdeclParamCategory category, string cdeclParam, string? reconstruction, string callArg)
            => new(category, cdeclParam, reconstruction, callArg, NeedsUnsafe: false, WriteBack: null);

        var swiftTypeSpec = arg.SwiftTypeSpec;

        // Swift keywords (in, for, repeat, extension, …) can't be used as bare identifiers in
        // wrapper bodies; demangled names can carry type-syntax characters; and a user binding can
        // collide with a synthetic the caller injects into the same signature (resultPtr, errorOut,
        // self_, …) or with a sibling user param. All three are handled by the shared core so this
        // path and the @_silgen_name shim emitters can't derive different bindings for one param.
        label = BuildSwiftBindingName(label, reservedSiblings, escapeReservedCollision);

        // Determine the Swift argument label for the init call
        // When calling _dbw_init_* (omitLabels=true), all params use _ (no external label)
        var argLabel = omitLabels ? "" : BuildSwiftCallArgLabel(arg);

        // inout parameters are a distinct lowering axis from the by-value chain below: every inout
        // param crosses @_cdecl as UnsafeMutableRawPointer, the wrapper binds a mutable `var`, passes
        // it `&`-by-reference, and stores the mutated value back through the pointer after the call.
        // Short-circuit here with Category=Inout and a populated WriteBack so the by-value classifier
        // never runs for an inout. (Reconstruction/WriteBack are always non-null for this category;
        // the MapInout shim projects them as non-nullable.) The shared label/argLabel handling above
        // is reused so the inout and by-value paths can't drift on identifier sanitization.
        if (isInout)
        {
            // Bool: stored as Int8 in the @_cdecl ABI, needs explicit conversion both ways.
            var renderedType = ExistentialBypassEmitter.RenderSwiftTypeSpec(swiftTypeSpec);
            if (MarshallingHelpers.IsBoolType(renderedType) || renderedType == "Bool")
            {
                return new CdeclLoweringDescriptor(
                    CdeclParamCategory.Inout,
                    $"_ {label}: UnsafeMutableRawPointer",
                    $"var {label}Val: Bool = {label}.assumingMemoryBound(to: Int8.self).pointee != 0",
                    $"{argLabel}&{label}Val",
                    NeedsUnsafe: false,
                    WriteBack: $"{label}.assumingMemoryBound(to: Int8.self).pointee = {label}Val ? 1 : 0");
            }

            // All other types: UnsafeMutableRawPointer with typed pointer access. Uses
            // assumingMemoryBound(to:).pointee for proper value semantics (not load(as:) which
            // requires BitwiseCopyable). The var binding is mutable so it can be passed as &ref to
            // the original Swift method, then written back through the pointer.
            var inoutSwiftType = RenderModuleQualifiedSwiftTypeWithExistentialAny(swiftTypeSpec, env.TypeDatabase);
            return new CdeclLoweringDescriptor(
                CdeclParamCategory.Inout,
                $"_ {label}: UnsafeMutableRawPointer",
                $"var {label}Val = {label}.assumingMemoryBound(to: {inoutSwiftType}.self).pointee",
                $"{argLabel}&{label}Val",
                NeedsUnsafe: false,
                WriteBack: $"{label}.assumingMemoryBound(to: {inoutSwiftType}.self).pointee = {label}Val");
        }

        // Swift.UnsafeRawBufferPointer / UnsafeMutableRawBufferPointer: 16-byte stdlib structs
        // (base + count) that @_cdecl can't represent. Split into (ptr, len) at the C ABI
        // boundary and reconstruct via (Mutable)RawBufferPointer(start:count:) in the wrapper
        // body. C# side pins a (ReadOnly)Span<byte> via `fixed` and passes (IntPtr)ptr +
        // (nint)length. Empty span pins to a null pointer, so the Swift ptr parameter is
        // optional — both initializers accept an optional start already. The mutable variant
        // uses UnsafeMutableRawPointer? on the Swift side so write-back through the buffer
        // mutates the C# memory directly.
        if (swiftTypeSpec is NamedTypeSpec rawBufSpec
            && (rawBufSpec.Name == "Swift.UnsafeRawBufferPointer"
                || rawBufSpec.Name == "Swift.UnsafeMutableRawBufferPointer"))
        {
            bool isMutable = rawBufSpec.Name == "Swift.UnsafeMutableRawBufferPointer";
            string ptrType = isMutable ? "UnsafeMutableRawPointer?" : "UnsafeRawPointer?";
            string bufferType = isMutable ? "UnsafeMutableRawBufferPointer" : "UnsafeRawBufferPointer";
            return Simple(CdeclParamCategory.RawBufferPointer,
                    $"_ {label}Ptr: {ptrType}, _ {label}Len: Int",
                    $"let {label}Val = {bufferType}(start: {label}Ptr, count: {label}Len)",
                    $"{argLabel}{label}Val");
        }

        // Primitives pass through directly
        if (IsCdeclPrimitive(swiftTypeSpec))
        {
            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(swiftTypeSpec);

            // Bool: Swift @_cdecl receives Int8, needs != 0 conversion
            if (MarshallingHelpers.IsBoolType(swiftType) || swiftType == "Bool")
            {
                return Simple(CdeclParamCategory.Bool,
                        $"_ {label}: Int8",
                        $"let {label}Val = {label} != 0",
                        $"{argLabel}{label}Val");
            }

            return Simple(CdeclParamCategory.Primitive, $"_ {label}: {swiftType}", null, $"{argLabel}{label}");
        }

        // AnyObject: IS a class reference by definition — use Unmanaged<AnyObject> marshalling.
        // Without this, AnyObject falls through to protocol existential path which emits
        // `any AnyObject.self` (not valid Swift metatype syntax).
        if (IsAnyObjectType(swiftTypeSpec))
        {
            return Simple(CdeclParamCategory.AnyObject,
                    $"_ {label}: UnsafeMutableRawPointer",
                    $"let {label}Val: AnyObject = Unmanaged<AnyObject>.fromOpaque({label}).takeUnretainedValue()",
                    $"{argLabel}{label}Val");
        }

        // Optional<Any> (Any?): existential-Any wrapped in Optional, passed by buffer pointer.
        // The "Any" existential is an empty protocol list. C# emits SwiftOptional<ExistentialContainer0>
        // and passes its PayloadBuffer pointer — a 32-byte buffer with Swift's Optional<Any> layout
        // (4-word ExistentialContainer: 3 payload words + 1 metadata pointer; nil is encoded via the
        // null-metadata extra-inhabitant, so no separate tag byte). The Swift wrapper reads it as
        // Optional<Any> directly via load(as:), which works uniformly across the payload types the
        // bare-Any projection currently supports (bool/int/double/string — all value types stored
        // inline in the EC). Must come before IsProtocolExistentialType, which routes Optional<any
        // Protocol> through a different path that needs "any" prefixing.
        if (swiftTypeSpec is NamedTypeSpec optAnySpec && optAnySpec.Name == "Swift.Optional"
            && optAnySpec.GenericParameters.Count == 1
            && optAnySpec.GenericParameters[0] is ProtocolListTypeSpec { Protocols.Count: 0 })
        {
            return Simple(CdeclParamCategory.OptionalAny,
                    $"_ {label}: UnsafeRawPointer",
                    $"let {label}Val: Any? = {label}.load(as: Optional<Any>.self)",
                    $"{argLabel}{label}Val");
        }

        // @objc protocol existentials passed as OPTIONAL ((any P)?): a single 8-byte ObjC object
        // pointer with no witness table and no descriptor — identical wire to AnyObject. The C#
        // side marshals the optional case as a bare, nullable pointer (through the
        // optional-existential projection), so reconstruct via Unmanaged<AnyObject> + `as!` cast
        // rather than the opaque-container load(as:) path below (which would read a buffer Swift
        // never wrote). Must precede the generic ProtocolExistential / OptionalReference arms.
        //
        // The NON-optional case (any P) is deliberately NOT reconstructed as a bare pointer here.
        // The C# side marshals a non-optional @objc existential parameter through the
        // ExistentialContainer1 carrier (allocated on the heap, passed by address) — the same path
        // that auto-wraps a plain C# conformer into a Swift proxy so Swift can dispatch back into
        // it. Reconstructing a bare pointer here would make the wrapper treat the carrier's address
        // as the object itself and crash. Instead it falls through to the generic
        // ProtocolExistential arm below, whose load(as:) reads the carrier's payload word — the
        // representation the C# side actually passes.
        if (ExistentialHandler.IsObjCProtocolExistentialSpec(swiftTypeSpec, env.TypeDatabase, out var objcParamIsOptional)
            && objcParamIsOptional)
        {
            var innerSpec = ((NamedTypeSpec)swiftTypeSpec).GenericParameters[0];
            var protoType = RenderModuleQualifiedSwiftTypeWithExistentialAny(innerSpec, env.TypeDatabase);
            var castType = protoType.StartsWith("any ") ? $"({protoType})" : protoType;
            return Simple(CdeclParamCategory.OptionalReference,
                    $"_ {label}: UnsafeMutableRawPointer?",
                    $"let {label}Val: {castType}? = {label}.map {{ Unmanaged<AnyObject>.fromOpaque($0).takeUnretainedValue() as! {castType} }}",
                    $"{argLabel}{label}Val");
        }

        // Protocol existentials are not C-representable in @_cdecl functions.
        // Marshal as UnsafeRawPointer and reconstruct inside the wrapper body.
        if (IsProtocolExistentialType(swiftTypeSpec, env.TypeDatabase))
        {
            var swiftType = RenderModuleQualifiedSwiftTypeWithExistentialAny(swiftTypeSpec, env.TypeDatabase);
            // Existential types need parenthesization in metatype position: (any Protocol).self
            var loadType = swiftType.StartsWith("any ") ? $"({swiftType})" : swiftType;
            return Simple(CdeclParamCategory.ProtocolExistential,
                    $"_ {label}: UnsafeRawPointer",
                    $"let {label}Val: {loadType} = {label}.load(as: {loadType}.self)",
                    $"{argLabel}{label}Val");
        }

        // Optional<reference type>: nullable pointer ABI.
        // C# passes IntPtr (0 for nil, object pointer for non-nil) via PayloadBuffer<IntPtr>.Buffer.
        // @_cdecl receives UnsafeMutableRawPointer? (nullable pointer maps to void* in C ABI).
        if (IsOptionalWithReferenceInner(swiftTypeSpec, env.TypeDatabase))
        {
            var innerType = ((NamedTypeSpec)swiftTypeSpec).GenericParameters[0];
            var swiftInnerType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(innerType);

            // Check if the inner type is an ObjC-bridged struct (e.g., NSZone, IndexPath).
            // Unmanaged<T> requires T: AnyObject, so ObjC-bridged structs need
            // Unmanaged<AnyObject> + cast. Synthetic ObjCBridged records from Apple framework
            // heuristics have Kind=Class but may represent Swift structs (e.g., NSZone),
            // so ObjCBridged types always use the AnyObject bridge for safety.
            // Also use AnyObject for types without TypeRecords (fallback) since we can't
            // verify they're true classes.
            bool useAnyObjectBridge = true;
            if (innerType is NamedTypeSpec innerNamed &&
                env.TypeDatabase.TryGetTypeRecord(innerNamed, out var innerRecord))
            {
                // True class (not ObjC-bridged) — Unmanaged<ClassName> is safe.
                // ObjC-bridged types use AnyObject because the synthetic TypeRecord
                // may report Kind=Class for types that are actually Swift structs.
                useAnyObjectBridge = innerRecord.Kind != TypeRecordKind.Class ||
                                     MarshallingHelpers.IsObjCBridged(innerRecord);
            }

            string reconstruction;
            if (useAnyObjectBridge)
                reconstruction = $"let {label}Val: {swiftInnerType}? = {label}.map {{ Unmanaged<AnyObject>.fromOpaque($0).takeUnretainedValue() as! {swiftInnerType} }}";
            else
                reconstruction = $"let {label}Val: {swiftInnerType}? = {label}.map {{ Unmanaged<{swiftInnerType}>.fromOpaque($0).takeUnretainedValue() }}";

            return Simple(CdeclParamCategory.OptionalReference,
                    $"_ {label}: UnsafeMutableRawPointer?",
                    reconstruction,
                    $"{argLabel}{label}Val");
        }

        // Optional<BlittablePrimitive>: read value and tag byte separately from UnsafeRawPointer
        // instead of using assumingMemoryBound(to: Optional<T>.self).pointee, which misinterprets
        // the tag byte for Optional<Int32> on some runtimes. The blittable-primitive set ×
        // tag-byte offset table × decode RHS shape is centralised in OptionalMarshalClassifier
        // so the protocol-extension emitter and this regular-method path can't drift apart.
        {
            var decode = OptionalMarshalClassifier.TryGetBlittablePrimitiveOptionalDecode(swiftTypeSpec, label);
            if (decode is not null)
            {
                // When calling _dbw_init_* (omitLabels=true), the dispatch method accepts
                // UnsafeRawPointer and decodes the Optional internally. Pass the pointer through
                // to avoid type mismatch (Optional<Int> vs UnsafeRawPointer).
                if (omitLabels)
                {
                    return Simple(CdeclParamCategory.OptionalBlittablePrimitive, $"_ {label}: UnsafeRawPointer", null, $"{label}");
                }
                var (localType, rhs) = decode.Value;
                var reconstruction = $"let {label}Opt: {localType} = {rhs}";
                return Simple(CdeclParamCategory.OptionalBlittablePrimitive,
                        $"_ {label}: UnsafeRawPointer",
                        reconstruction,
                        $"{argLabel}{label}Opt");
            }
        }

        // Optional<OpaqueType> (complex enums, non-frozen structs): C# wraps these in
        // SwiftOptional<IntPtr> which stores a POINTER to the inner value's VWT buffer.
        // The buffer has Optional<IntPtr> layout (extra-inhabitant nil=0x0), NOT Optional<T>
        // layout (tag-byte encoding where 0=some). Reading as Optional<T> misinterprets
        // the tag byte → nil appears as "some". Read as Optional<UnsafeMutableRawPointer>
        // (pointer-optional) to correctly interpret nil/non-nil, then reconstruct the inner
        // type from the pointer.
        if (swiftTypeSpec is NamedTypeSpec optOpaqueSpec && optOpaqueSpec.Name == "Swift.Optional"
            && optOpaqueSpec.GenericParameters.Count == 1)
        {
            var innerSpec = optOpaqueSpec.GenericParameters[0];
            if (innerSpec is NamedTypeSpec innerOpaqueNamed &&
                env.TypeDatabase.TryGetTypeRecord(innerOpaqueNamed, out var innerOpaqueRecord))
            {
                // Exclude NativeRemapped types (URL, Data, etc.) — they have their own
                // marshalling via ObjC bridging and don't use SwiftOptional<IntPtr>.
                bool isNativeRemapped = innerOpaqueRecord.NativeTypeName != null;
                bool isOpaqueType = !isNativeRemapped &&
                                    ((innerOpaqueRecord.Kind == TypeRecordKind.Enum &&
                                      !innerOpaqueRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum)) ||
                                    (innerOpaqueRecord.Kind == TypeRecordKind.Struct &&
                                      !MarshallingHelpers.IsTypeFrozen(innerOpaqueRecord)));
                if (isOpaqueType)
                {
                    // When calling _dbw_init_* (omitLabels=true), the dispatch method accepts
                    // UnsafeRawPointer for opaque Optional params and decodes internally.
                    // Pass the pointer through to avoid type mismatch.
                    if (omitLabels)
                    {
                        return Simple(CdeclParamCategory.OptionalOpaque, $"_ {label}: UnsafeRawPointer", null, $"{label}");
                    }

                    var innerSwiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(innerSpec);
                    var reconstruction = $"let {label}Val: {innerSwiftType}? = {label}.assumingMemoryBound(to: UnsafeMutableRawPointer?.self).pointee.map {{ $0.assumingMemoryBound(to: {innerSwiftType}.self).pointee }}";
                    return Simple(CdeclParamCategory.OptionalOpaque,
                            $"_ {label}: UnsafeRawPointer",
                            reconstruction,
                            $"{argLabel}{label}Val");
                }
            }
        }

        // Containers with ObjC-bridgeable elements: bridge entire container to ObjC collection.
        // The @_cdecl wrapper receives an ObjC collection pointer (NSArray, NSDictionary, NSSet)
        // and casts it back to the typed Swift collection via _ObjectiveCBridgeable.
        if (IsObjCBridgeableContainer(swiftTypeSpec, env.TypeDatabase))
        {
            var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
            return Simple(CdeclParamCategory.ObjCBridgeableContainer,
                    $"_ {label}: UnsafeMutableRawPointer",
                    $"let {label}Val: {swiftType} = Unmanaged<AnyObject>.fromOpaque({label}).takeUnretainedValue() as! {swiftType}",
                    $"{argLabel}{label}Val");
        }

        // Optional<container with ObjC-bridgeable elements>: nullable ObjC collection pointer.
        if (IsOptionalObjCBridgeableContainer(swiftTypeSpec, env.TypeDatabase))
        {
            var innerSpec = ((NamedTypeSpec)swiftTypeSpec).GenericParameters[0];
            var swiftInnerType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(innerSpec);
            return Simple(CdeclParamCategory.OptionalObjCBridgeableContainer,
                    $"_ {label}: UnsafeMutableRawPointer?",
                    $"let {label}Val: {swiftInnerType}? = {label}.map {{ Unmanaged<AnyObject>.fromOpaque($0).takeUnretainedValue() as! {swiftInnerType} }}",
                    $"{argLabel}{label}Val");
        }

        // Generic container types (Optional<T>, Array<T>, Dictionary<K,V>, etc.)
        // are not C-representable in @_cdecl functions. Marshal as UnsafeRawPointer.
        if (IsGenericContainerType(swiftTypeSpec))
        {
            // When calling _dbw_init_* (omitLabels=true) and the param is a large Optional
            // that _dbw_init_* also widens to UnsafeRawPointer, pass the pointer through directly
            // instead of loading the Optional value (which would cause a type mismatch).
            if (omitLabels && OptionalPointerWrapperEmitter.ShouldWidenParam(arg, env.BoundGenericsHandler))
            {
                return Simple(CdeclParamCategory.GenericContainer,
                        $"_ {label}: UnsafeRawPointer",
                        null,
                        $"{label}");
            }

            // Use assumingMemoryBound(to:).pointee instead of load(as:) — for generic containers
            // like Optional<EnumWithAssociatedValues>, load(as:) can SIGSEGV because the container
            // may not satisfy BitwiseCopyable constraints. assumingMemoryBound(to:).pointee
            // uses typed pointer access with proper value semantics.
            var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
            return Simple(CdeclParamCategory.GenericContainer,
                    $"_ {label}: UnsafeRawPointer",
                    $"let {label}Val = {label}.assumingMemoryBound(to: {swiftType}.self).pointee",
                    $"{argLabel}{label}Val");
        }

        // Foundation.Date: @_cdecl bridges Date ↔ NSDate* (ObjC interop) which is incompatible
        // with the raw double that C# passes. Accept Double and reconstruct Date inside wrapper.
        if (swiftTypeSpec is NamedTypeSpec dateNamed && dateNamed.Name == "Foundation.Date")
        {
            return Simple(CdeclParamCategory.Date,
                    $"_ {label}: Double",
                    $"let {label}Val = Foundation.Date(timeIntervalSinceReferenceDate: {label})",
                    $"{argLabel}{label}Val");
        }

        // Foundation.Data: @_cdecl bridges Data ↔ NSData* (ObjC interop) which is incompatible
        // with the raw Data buffer that C# passes via CallConvCdecl.
        // Accept as two Int words matching the 16-byte struct layout and reconstruct.
        // On ARM64, C# passes Swift.Foundation.Data (16-byte struct) in two consecutive GP registers,
        // exactly matching two Int parameters in the @_cdecl signature.
        // Same pattern as the String ↔ NSString* workaround.
        if (swiftTypeSpec is NamedTypeSpec dataNamed && dataNamed.Name == "Foundation.Data")
        {
            return Simple(CdeclParamCategory.Data,
                    $"_ _dW0_{label}: Int, _ _dW1_{label}: Int",
                    $"let {label}Val = unsafeBitCast((_dW0_{label}, _dW1_{label}), to: Foundation.Data.self)",
                    $"{argLabel}{label}Val");
        }

        // Foundation.LocalizedStringResource (iOS 16+): C# marshals it as a string (StringProjection),
        // so the @_cdecl wrapper receives the same wire shape as Swift.String and reconstructs the
        // String, then builds the resource via its ExpressibleByStringLiteral initializer. Only reached
        // for the carved-out scalar param on the simple concrete wire path (containers/closures/protocol
        // positions are dropped before emission by ClassifyUnsupportedReference).
        if (swiftTypeSpec is NamedTypeSpec lsrNamed && lsrNamed.Name == "Foundation.LocalizedStringResource")
        {
            if (useUtf8Strings)
            {
                return Simple(CdeclParamCategory.String,
                        $"_ {label}Utf8Ptr: UnsafePointer<UInt8>, _ {label}Utf8Len: Int",
                        $"let {label}Val = Foundation.LocalizedStringResource(stringLiteral: String(bytes: UnsafeBufferPointer(start: {label}Utf8Ptr, count: {label}Utf8Len), encoding: .utf8)!)",
                        $"{argLabel}{label}Val");
            }
            return Simple(CdeclParamCategory.String,
                    $"_ _sW0_{label}: Int, _ _sW1_{label}: Int",
                    $"let {label}Val = Foundation.LocalizedStringResource(stringLiteral: unsafeBitCast((_sW0_{label}, _sW1_{label}), to: String.self))",
                    $"{argLabel}{label}Val");
        }

        // String: @_cdecl bridges String ↔ NSString* (ObjC interop) which is incompatible
        // with the raw SwiftString.Buffer that C# passes via CallConvCdecl.
        if (swiftTypeSpec is NamedTypeSpec strNamed && strNamed.Name == "Swift.String")
        {
            if (useUtf8Strings)
            {
                // UTF-8 pointer + length: C# encodes to UTF-8 bytes, pins them, and passes
                // (IntPtr ptr, nint len). NativeAOT-safe — no struct marshalling needed.
                // nint matches Swift's Int (64-bit on ARM64) to avoid truncation.
                // Used by subscript and enum case wrappers where C# already sends UTF-8.
                return Simple(CdeclParamCategory.String,
                        $"_ {label}Utf8Ptr: UnsafePointer<UInt8>, _ {label}Utf8Len: Int",
                        $"let {label}Val = String(bytes: UnsafeBufferPointer(start: {label}Utf8Ptr, count: {label}Utf8Len), encoding: .utf8)!",
                        $"{argLabel}{label}Val");
            }
            else
            {
                // Two Int words matching the 16-byte buffer layout: C# passes SwiftString.Buffer
                // (16-byte struct) in two consecutive GP registers on ARM64.
                // Used by constructor/method wrappers where C# marshals via SwiftString.
                return Simple(CdeclParamCategory.String,
                        $"_ _sW0_{label}: Int, _ _sW1_{label}: Int",
                        $"let {label}Val = unsafeBitCast((_sW0_{label}, _sW1_{label}), to: String.self)",
                        $"{argLabel}{label}Val");
            }
        }

        // Classes: receive as UnsafeMutableRawPointer, reconstruct via Unmanaged
        if (env.TypeDatabase.TryGetTypeRecord(swiftTypeSpec, out var typeRecord))
        {
            // Non-copyable structs (~Copyable): pass as pointer; ownership-specifier decides the load.
            if (typeRecord.Flags.HasFlag(TypeRecordFlags.NonCopyable))
            {
                var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);

                // `consuming` (Owned, +1): ownership transfers into Swift. Move the value out of the
                // C# buffer with `.move()` (which deinitializes the buffer, leaving it uninitialized)
                // and pass it consuming. The function runs the value's deinit exactly once. The C#
                // call site pairs this with SwiftSafeHandle.MarkConsumed() so the now-empty buffer is
                // freed WITHOUT a second value-witness Destroy — without it Swift's consume plus the
                // C# SafeHandle's Destroy double-free (SIGABRT). `.move()` needs a mutable
                // pointer, so the @_cdecl param is UnsafeMutableRawPointer.
                if (arg.Ownership == ParameterOwnership.Owned)
                {
                    return Simple(CdeclParamCategory.NonCopyableConsume,
                            $"_ {label}: UnsafeMutableRawPointer",
                            $"let {label}Val = {label}.assumingMemoryBound(to: {swiftType}.self).move()",
                            $"{argLabel}{label}Val");
                }

                // `borrowing`/default (+0): caller (C#) retains ownership and destroys. Use the inline
                // borrow — `let xVal = ptr...pointee` would copy, which noncopyable types reject.
                // UnsafePointer<T: ~Copyable>.pointee gives a borrow in Swift 6, safe to forward to a
                // borrowing parameter.
                return Simple(CdeclParamCategory.NonCopyableBorrow,
                        $"_ {label}: UnsafeRawPointer",
                        null,  // no reconstruction — inline borrow avoids copy
                        $"{argLabel}{label}.assumingMemoryBound(to: {swiftType}.self).pointee");
            }

            if (typeRecord.Kind == TypeRecordKind.Class ||
                MarshallingHelpers.IsObjCBridged(typeRecord) ||
                MarshallingHelpers.IsObjCRooted(typeRecord))
            {
                var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);

                // Check for NSString typedef structs (e.g., CALayerContentsGravity, CATransitionType).
                // These are ObjC-bridged in the type database but are Swift structs wrapping NSString,
                // not class types. Unmanaged<T> requires T to be a class, so reconstruct via
                // NSString → String → init(rawValue:) instead.
                if (swiftTypeSpec is NamedTypeSpec nsTypedef &&
                    AppleFrameworkRegistry.TryGetNetTypeName(nsTypedef.Name, out var remapped) &&
                    remapped == "Foundation.NSString")
                {
                    return Simple(CdeclParamCategory.ObjCBridgedClassPointer,
                            $"_ {label}: UnsafeMutableRawPointer",
                            $"let {label}Val = {swiftType}(rawValue: Unmanaged<NSString>.fromOpaque({label}).takeUnretainedValue() as String)",
                            $"{argLabel}{label}Val");
                }

                // ObjC-bridged types (e.g., IndexPath bridged to NSIndexPath) may be Swift structs
                // but passed as class pointers across FFI. Use Unmanaged<AnyObject> + cast to handle
                // both true classes and bridged structs safely. Unmanaged<T> requires T: AnyObject,
                // so Unmanaged<IndexPath> fails for bridged structs.
                if (MarshallingHelpers.IsObjCBridged(typeRecord))
                {
                    return Simple(CdeclParamCategory.ObjCBridgedClassPointer,
                            $"_ {label}: UnsafeMutableRawPointer",
                            $"let {label}Val = Unmanaged<AnyObject>.fromOpaque({label}).takeUnretainedValue() as! {swiftType}",
                            $"{argLabel}{label}Val");
                }

                return Simple(CdeclParamCategory.ClassPointer,
                        $"_ {label}: UnsafeMutableRawPointer",
                        $"let {label}Val = Unmanaged<{swiftType}>.fromOpaque({label}).takeUnretainedValue()",
                        $"{argLabel}{label}Val");
            }

            // ObjC-bridgeable value types (URL): Swift auto-bridges to ObjC class pointer
            // via _ObjectiveCBridgeable. Use Unmanaged<AnyObject> + cast, same as ObjCBridged.
            if (MarshallingHelpers.IsObjCBridgeable(typeRecord))
            {
                var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
                return Simple(CdeclParamCategory.ObjCBridgeableValue,
                        $"_ {label}: UnsafeMutableRawPointer",
                        $"let {label}Val = Unmanaged<AnyObject>.fromOpaque({label}).takeUnretainedValue() as! {swiftType}",
                        $"{argLabel}{label}Val");
            }

            // Protocol/Existential TypeRecords: not C-representable, pass as pointer
            if (typeRecord.Kind == TypeRecordKind.Protocol ||
                typeRecord.Kind == TypeRecordKind.Existential)
            {
                var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
                return Simple(CdeclParamCategory.ProtocolTypeRecord,
                        $"_ {label}: UnsafeRawPointer",
                        $"let {label}Val: {swiftType} = {label}.load(as: {swiftType}.self)",
                        $"{argLabel}{label}Val");
            }

            // Simple enums: pass raw value as C-compatible integer, reconstruct safely.
            // unsafeBitCast crashes when enum storage size differs from parameter type
            // (e.g., a 3-case `: Int` enum stored in 1 byte vs 8-byte Int parameter).
            if (typeRecord.Kind == TypeRecordKind.Enum && typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
            {
                var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
                var rawType = GetSwiftRawValueType(typeRecord.RawValueTypeName);

                string conversion;
                if (typeRecord.Flags.HasFlag(TypeRecordFlags.OptionSet))
                {
                    // OptionSet (e.g. an imported ObjC NS_OPTIONS bitmask): init(rawValue:) is
                    // NON-failable and returns a non-optional, so bind it directly. Every raw
                    // bit pattern is a valid OptionSet — there is no invalid-value case to guard —
                    // and a `guard let` / force-unwrap on a non-optional would not compile.
                    conversion = $"let {label}Val = {swiftType}(rawValue: {label})";
                }
                else if (!string.IsNullOrEmpty(typeRecord.RawValueTypeName))
                {
                    // RawRepresentable enum: init(rawValue:) safely maps raw value → case
                    // regardless of in-memory storage size. The synthesized init?(rawValue:)
                    // has the same access level as the type and is always available from
                    // the wrapper module. Guard against invalid raw values from C# (e.g.,
                    // casting an arbitrary integer to the enum type).
                    conversion = $"guard let {label}Val = {swiftType}(rawValue: {label}) else {{ preconditionFailure(\"[SwiftBindings] Invalid raw value \\({label}) for {swiftType}\") }}";
                }
                else
                {
                    // Tag-only enum (no RawRepresentable): C# sends the case index as
                    // a widened integer. Extract the tag from the low bytes via safe
                    // memory load (little-endian: tag is in the first N bytes).
                    conversion = $"var {label}Raw = {label}; let {label}Val = withUnsafeMutablePointer(to: &{label}Raw) {{ UnsafeMutableRawPointer($0).load(as: {swiftType}.self) }}";
                }

                return Simple(CdeclParamCategory.SimpleEnum, $"_ {label}: {rawType}", conversion, $"{argLabel}{label}Val");
            }

            // Complex enums: pass as pointer.
            // Use assumingMemoryBound(to:).pointee instead of load(as:) — for enums with
            // non-BitwiseCopyable fields (e.g., String raw-value enums), load(as:) creates
            // a bitwise copy without proper reference semantics, causing SIGBUS.
            // assumingMemoryBound(to:).pointee uses typed pointer access with proper value semantics.
            if (typeRecord.Kind == TypeRecordKind.Enum)
            {
                var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
                return Simple(CdeclParamCategory.ComplexEnum,
                        $"_ {label}: UnsafeRawPointer",
                        $"let {label}Val = {label}.assumingMemoryBound(to: {swiftType}.self).pointee",
                        $"{argLabel}{label}Val");
            }

            // Non-frozen structs: C# passes SafeHandle (IntPtr), receive as pointer.
            // Use assumingMemoryBound for consistency with enum/container paths.
            if (!MarshallingHelpers.IsTypeFrozen(typeRecord))
            {
                var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
                return Simple(CdeclParamCategory.NonFrozenStruct,
                        $"_ {label}: UnsafeRawPointer",
                        $"let {label}Val = {label}.assumingMemoryBound(to: {swiftType}.self).pointee",
                        $"{argLabel}{label}Val");
            }

            // Frozen structs: system/Apple types pass by-value, custom types via UnsafeRawPointer.
            if (MarshallingHelpers.IsTypeFrozen(typeRecord))
            {
                // System framework frozen structs (CGRect, Date, etc.) are C-representable
                // and safe for @_cdecl by-value passing. Custom frozen structs from third-party
                // libraries trigger "Swift structs cannot be represented in Objective-C".
                // SIMD vectors (simd_floatN, simd_quatf, simd_floatNxN) are excluded: Swift
                // passes them in a single NEON vector register; .NET projects them onto Vector3/
                // Vector4/Quaternion/Matrix4x4 which the CLR passes as HFAs across s0,s1,s2,…
                // Only lane 0 aligns, so by-value loses every lane past the first. Route them
                // through the indirect (UnsafeRawPointer + stackalloc) path so the bytes cross
                // intact.
                if (swiftTypeSpec is NamedTypeSpec frozenNamed && IsSystemFrozenStruct(frozenNamed)
                    && !IsSimdVectorType(frozenNamed))
                {
                    var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(swiftTypeSpec);
                    return Simple(CdeclParamCategory.SystemFrozenStruct, $"_ {label}: {swiftType}", null, $"{argLabel}{label}");
                }

                // Custom frozen structs: pass as UnsafeRawPointer and reconstruct.
                // Use assumingMemoryBound(to:).pointee instead of load(as:) — frozen structs
                // with reference-counted fields (String, Array, Optional) are not BitwiseCopyable.
                var moduleQualifiedType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
                return Simple(CdeclParamCategory.CustomFrozenStruct,
                        $"_ {label}: UnsafeRawPointer",
                        $"let {label}Val = {label}.assumingMemoryBound(to: {moduleQualifiedType}.self).pointee",
                        $"{argLabel}{label}Val");
            }
        }

        // Fallback: pass as UnsafeRawPointer.
        // Use assumingMemoryBound for consistency with all other pointer reconstruction paths.
        var fallbackSwiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
        return Simple(CdeclParamCategory.Fallback,
                $"_ {label}: UnsafeRawPointer",
                $"let {label}Val = {label}.assumingMemoryBound(to: {fallbackSwiftType}.self).pointee",
                $"{argLabel}{label}Val");
    }

    /// <summary>
    /// Renders a TypeSpec as module-qualified Swift, prepending the existential <c>any</c>
    /// keyword when the type is a protocol existential (required by Swift 6;
    /// a bare protocol-with-primary-associated-types name is a Swift 6 error).
    /// <c>Optional&lt;Protocol&gt;</c> becomes <c>Optional&lt;any Protocol&gt;</c> — the
    /// <c>any</c> binds to the inner type, since Swift rejects <c>any Optional&lt;Protocol&gt;</c>.
    /// A <see cref="ProtocolListTypeSpec"/> already carries <c>any</c> from
    /// RenderSwiftTypeSpecCore and is returned unchanged.
    /// </summary>
    internal static string RenderModuleQualifiedSwiftTypeWithExistentialAny(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(typeSpec);
        if (IsProtocolExistentialType(typeSpec, typeDatabase) &&
            typeSpec is NamedTypeSpec namedSpec && !swiftType.StartsWith("any "))
        {
            if (IsOptionalProtocolExistential(namedSpec, typeDatabase))
            {
                var innerType = namedSpec.GenericParameters[0];
                var innerSwiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(innerType);
                // A ProtocolListTypeSpec inner (P & Q, or empty composition → "Any") already
                // carries `any` from RenderSwiftTypeSpecCore; only a bare single-protocol
                // NamedTypeSpec needs it added. Mirror the outer path's StartsWith guard so an
                // optional protocol *composition* doesn't become the invalid `any any P & Q`.
                if (!innerSwiftType.StartsWith("any "))
                {
                    innerSwiftType = $"any {innerSwiftType}";
                }
                swiftType = $"Swift.Optional<({innerSwiftType})>";
            }
            else
            {
                swiftType = $"any {swiftType}";
            }
        }
        return swiftType;
    }

    /// <summary>
    /// Checks whether a type spec represents a protocol existential (any Protocol),
    /// including Optional-wrapped protocol existentials.
    /// Protocol existentials are not C-representable and must be marshalled as UnsafeRawPointer in @_cdecl functions.
    /// </summary>
    internal static bool IsProtocolExistentialType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        // Direct protocol list: any Protocol or any P1 & P2
        if (typeSpec is ProtocolListTypeSpec)
            return true;

        // Single protocol referenced by name: check TypeRecord. Metatypes resolve through
        // MetatypeStrategy to the AnyType record (Kind=Protocol) — exclude them so a bare
        // metatype (AnyClass.Type, T.Type) is not misclassified as a protocol existential
        // and routed through .load(as: any X.Type) rendering, which is invalid Swift.
        if (typeSpec is NamedTypeSpec singleNamed &&
            !WrapperValidation.IsMetatypeType(singleNamed) &&
            typeDatabase.TryGetTypeRecord(singleNamed, out var record) &&
            (record.Kind == TypeRecordKind.Protocol || record.Kind == TypeRecordKind.Existential))
            return true;

        // Optional<Protocol> — delegate to IsOptionalProtocolExistential
        if (typeSpec is NamedTypeSpec namedSpec && IsOptionalProtocolExistential(namedSpec, typeDatabase))
            return true;

        return false;
    }

    /// <summary>
    /// Checks whether a NamedTypeSpec is Optional wrapping a protocol existential.
    /// Used to determine whether "any" should be inserted on the inner type
    /// (Optional&lt;any Protocol&gt;) rather than the outer type.
    /// </summary>
    internal static bool IsOptionalProtocolExistential(NamedTypeSpec namedSpec, ITypeDatabase typeDatabase)
    {
        if (namedSpec.Name != "Swift.Optional" || namedSpec.GenericParameters.Count != 1)
            return false;

        var inner = namedSpec.GenericParameters[0];

        if (inner is ProtocolListTypeSpec)
            return true;

        // Same metatype carve-out as the non-Optional path: the inner Metatype TypeSpec
        // resolves to AnyType (Kind=Protocol) through MetatypeStrategy, but it isn't a
        // protocol existential — emitting "any AnyClass.Type" is invalid Swift.
        if (inner is NamedTypeSpec innerNamed &&
            !WrapperValidation.IsMetatypeType(innerNamed) &&
            typeDatabase.TryGetTypeRecord(innerNamed, out var record) &&
            (record.Kind == TypeRecordKind.Protocol || record.Kind == TypeRecordKind.Existential))
            return true;

        return false;
    }

    /// <summary>
    /// Checks whether a type spec represents AnyObject (the universal class protocol).
    /// AnyObject IS a class reference by definition and should use Unmanaged marshalling,
    /// not existential .load(as:) which produces invalid `any AnyObject.self` syntax.
    /// </summary>
    internal static bool IsAnyObjectType(TypeSpec typeSpec)
    {
        if (typeSpec is ProtocolListTypeSpec protocolList &&
            protocolList.Protocols.Count == 1 &&
            protocolList.Protocols.Keys.First() is NamedTypeSpec protoNamed &&
            (protoNamed.Name == "AnyObject" || protoNamed.Name == "Swift.AnyObject"))
            return true;

        if (typeSpec is NamedTypeSpec named &&
            (named.Name == "AnyObject" || named.Name == "Swift.AnyObject"))
            return true;

        return false;
    }

    /// <summary>
    /// Checks whether a type spec is a container (Array, Dictionary, Set) whose leaf elements
    /// include at least one ObjC-bridgeable type. These containers cross the @_cdecl boundary
    /// as ObjC collection pointers (NSArray, NSDictionary, NSSet) via Swift's _ObjectiveCBridgeable.
    /// </summary>
    internal static bool IsObjCBridgeableContainer(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec is not NamedTypeSpec named || named.GenericParameters.Count == 0)
            return false;

        if (named.Name == "Swift.Array" && named.GenericParameters.Count == 1)
            return HasObjCBridgeableLeafElement(named.GenericParameters[0], typeDatabase);
        if (named.Name == "Swift.Dictionary" && named.GenericParameters.Count == 2)
            return HasObjCBridgeableLeafElement(named.GenericParameters[0], typeDatabase) ||
                   HasObjCBridgeableLeafElement(named.GenericParameters[1], typeDatabase);
        if (named.Name == "Swift.Set" && named.GenericParameters.Count == 1)
            return HasObjCBridgeableLeafElement(named.GenericParameters[0], typeDatabase);
        return false;
    }

    /// <summary>
    /// Checks whether a type spec is Optional wrapping a container with ObjC-bridgeable elements.
    /// E.g., Optional&lt;Array&lt;Foundation.URL&gt;&gt; → nullable ObjC collection pointer at @_cdecl boundary.
    /// </summary>
    internal static bool IsOptionalObjCBridgeableContainer(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec is not NamedTypeSpec optSpec || optSpec.Name != "Swift.Optional" || optSpec.GenericParameters.Count != 1)
            return false;
        return IsObjCBridgeableContainer(optSpec.GenericParameters[0], typeDatabase);
    }

    /// <summary>
    /// Checks whether a type spec is an ObjC-bridgeable type or a container with ObjC-bridgeable elements (recursive).
    /// </summary>
    private static bool HasObjCBridgeableLeafElement(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        // Check nested containers first — Swift.Array/Dictionary/Set are registered in the TypeDB
        // as frozen structs, so the TryGetTypeRecord check below would match them and return
        // IsObjCBridgeable(struct) = false, preventing recursion into their generic parameters.
        if (IsObjCBridgeableContainer(typeSpec, typeDatabase))
            return true;
        if (typeSpec is NamedTypeSpec named && typeDatabase.TryGetTypeRecord(named, out var record))
            return MarshallingHelpers.IsObjCBridgeable(record);
        return false;
    }

    /// <summary>
    /// Checks whether a type spec is a generic container type (Optional, Array, Dictionary,
    /// Set, Result, ClosedRange). These Swift generic types are not C-representable in
    /// @_cdecl functions and must be marshalled as UnsafeRawPointer with .load(as:)
    /// reconstruction in the wrapper body.
    /// </summary>
    internal static bool IsGenericContainerType(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec named || named.GenericParameters.Count == 0)
            return false;

        return named.Name is "Swift.Optional" or "Swift.Array" or "Swift.Dictionary"
            or "Swift.Set" or "Swift.Result" or "Swift.ClosedRange";
    }

    /// <summary>
    /// Returns true for frozen structs from system/Apple frameworks that are C-representable
    /// and safe for by-value @_cdecl passing. Covers:
    /// - Types in AppleFrameworkRegistry.ValueTypes (explicitly registered Apple value types)
    /// - Types from known system C-bridging modules (CoreGraphics, CoreFoundation, Darwin, simd)
    ///   that are not in the Apple framework registry but are always C-representable
    /// Does NOT include arbitrary third-party dependency modules — those may contain custom
    /// Swift structs that trigger "cannot be represented in Objective-C".
    /// </summary>
    internal static bool IsSystemFrozenStruct(NamedTypeSpec typeSpec)
    {
        if (!typeSpec.HasModule())
            return false;
        // Explicitly registered Apple value types (Foundation.Date, ARKit.ARRaycastQuery, etc.)
        if (AppleFrameworkRegistry.IsKnownValueType(typeSpec.Name))
            return true;
        // System C-bridging modules whose frozen structs are always C-representable.
        // These modules expose C structs via Swift overlays — they are inherently @_cdecl-safe.
        var module = SwiftTypeName.FromTypeSpec(typeSpec).Module;
        return module is "CoreGraphics" or "CoreFoundation" or "Darwin" or "simd"
            or "Swift" or "ObjectiveC" or "_Concurrency";
    }

    /// <summary>
    /// Returns true for Swift SIMD vector / matrix types whose register-class on the input
    /// path is incompatible with the C# projection's by-value ABI:
    /// <list type="bullet">
    ///   <item>Direct simd module exports — <c>simd.simd_floatN</c>, <c>simd.simd_quatf</c>,
    ///         <c>simd.simd_floatNxN</c> — Swift passes these in a single NEON vector register
    ///         (q0). .NET projects them onto <c>System.Numerics.Vector{2,3,4}</c>, <c>Quaternion</c>,
    ///         <c>Matrix4x4</c>, which the CLR splits into HFA elements across s0,s1,s2,…
    ///         Only lane 0 lines up; the rest are lost.</item>
    ///   <item>Bound-generic sugar — <c>Swift.SIMD2/3/4&lt;Swift.Float&gt;</c> — appears in
    ///         framework swiftinterfaces (RealityKit, RealityFoundation) before
    ///         <c>BoundGenericSimdAliasStrategy</c> collapses it to <c>simd.simd_floatN</c>.
    ///         <see cref="IsSystemFrozenStruct"/> sees <c>module=="Swift"</c> and returns true,
    ///         routing the param down the broken by-value path. Catch the unresolved alias
    ///         here so gating is correct regardless of which resolution pass has run.</item>
    /// </list>
    /// Callers wedge this in front of by-value branches so SIMD params instead go through the
    /// indirect (UnsafeRawPointer + stackalloc) path, where the full byte payload crosses.
    /// </summary>
    internal static bool IsSimdVectorType(NamedTypeSpec typeSpec)
    {
        if (typeSpec is null)
            return false;

        // Direct simd module: all Clang ext_vector_type exports under module "simd" use the
        // "simd_" prefix (simd_float2/3/4, simd_quatf, simd_float3x3, simd_float4x4).
        if (typeSpec.Name.StartsWith("simd.simd_", StringComparison.Ordinal))
            return true;

        // Bound-generic sugar: Swift.SIMD{2,3,4}<Swift.Float>. Mirror the table in
        // TypeDatabaseExtensions.BoundGenericSimdAliases so the predicate stays in lockstep
        // with the alias resolver — any addition there needs a matching arm here.
        if (typeSpec.GenericParameters.Count == 1 &&
            typeSpec.GenericParameters[0] is NamedTypeSpec elementSpec &&
            elementSpec.Name == "Swift.Float" &&
            typeSpec.Name is "Swift.SIMD2" or "Swift.SIMD3" or "Swift.SIMD4")
            return true;

        return false;
    }

    /// <summary>
    /// Returns true for types that can be passed directly through the C ABI
    /// without pointer wrapping (integers, floats, etc.).
    /// </summary>
    internal static bool IsCdeclPrimitive(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec named)
            return false;

        return named.Name switch
        {
            "Swift.Int" or "Swift.UInt" or "Swift.Int8" or "Swift.UInt8" or
            "Swift.Int16" or "Swift.UInt16" or "Swift.Int32" or "Swift.UInt32" or
            "Swift.Int64" or "Swift.UInt64" or
            "Swift.Float" or "Swift.Double" or "Swift.Bool" or
            "CoreFoundation.CGFloat" => true,
            _ => false
        };
    }

    /// <summary>
    /// Returns true if the Swift type is a blittable primitive whose Optional uses an appended
    /// tag byte (not extra inhabitants). Bool is excluded — Optional&lt;Bool&gt; uses extra
    /// inhabitants (size 1 == Optional size 1), so there is no separate tag byte to read/write.
    /// </summary>
    internal static bool IsBlittablePrimitiveSwiftType(string typeName) => typeName switch
    {
        "Swift.Int" or "Swift.UInt" or "Swift.Int8" or "Swift.UInt8" or
        "Swift.Int16" or "Swift.UInt16" or "Swift.Int32" or "Swift.UInt32" or
        "Swift.Int64" or "Swift.UInt64" or
        "Swift.Float" or "Swift.Double" or
        "CoreFoundation.CGFloat" or "CGFloat" or
        "Int" or "UInt" or "Int8" or "UInt8" or
        "Int16" or "UInt16" or "Int32" or "UInt32" or
        "Int64" or "UInt64" or
        "Float" or "Double" => true,
        _ => false
    };

    /// <summary>
    /// Maps C# enum underlying type names to Swift raw value type names.
    /// </summary>
    /// <summary>
    /// Builds the Swift argument label (<c>"label: "</c>) for a call site, or <c>""</c> for unlabeled
    /// positions. Recovers the raw Swift label via <see cref="BaseDecl.GetSwiftName"/> (which prefers
    /// <see cref="BaseDecl.OriginalSwiftName"/>) and backtick-escapes via <see cref="NameProvider.EscapeSwiftKeyword"/>
    /// so labels that spell Swift keywords (<c>default</c>, <c>in</c>, ...) emit valid Swift, and
    /// subscript index positions marked unlabeled via <see cref="ArgumentDecl.IsUnlabeledSubscriptIndex"/>
    /// emit no label at all. Falls back to the legacy underscore-stripping recovery for ArgumentDecls
    /// that were parsed before the OriginalSwiftName field was populated for that path.
    /// </summary>
    internal static string BuildSwiftCallArgLabel(ArgumentDecl arg)
    {
        if (arg.IsUnlabeledSubscriptIndex)
            return "";

        var name = arg.Name;
        if (SwiftBuilder.IsAutoGeneratedArgName(name) || name == "_" || string.IsNullOrEmpty(name))
            return "";

        // Prefer the parser-captured original Swift name; otherwise fall back to the legacy
        // underscore-stripping recovery for callers whose ArgumentDecl construction predates
        // the OriginalSwiftName population. EscapeSwiftKeyword handles the keyword case in
        // either path, so a `default:` label round-trips as `` `default`: ``.
        var swiftLabel = arg.OriginalSwiftName
            ?? (name.StartsWith("_") ? name.Substring(1) : name);
        return $"{NameProvider.EscapeSwiftKeyword(swiftLabel)}: ";
    }

    internal static string GetSwiftRawValueType(string? rawValueTypeName) => rawValueTypeName switch
    {
        "Swift.Int" or "Int" => "Int",
        "Swift.UInt" or "UInt" => "UInt",
        "Swift.Int8" or "Int8" => "Int8",
        "Swift.UInt8" or "UInt8" => "UInt8",
        "Swift.Int16" or "Int16" => "Int16",
        "Swift.UInt16" or "UInt16" => "UInt16",
        "Swift.Int32" or "Int32" => "Int32",
        "Swift.UInt32" or "UInt32" => "UInt32",
        "Swift.Int64" or "Int64" => "Int64",
        "Swift.UInt64" or "UInt64" => "UInt64",
        "Swift.Bool" or "Bool" => "Bool",
        "Swift.Float" or "Float" => "Float",
        "Swift.Double" or "Double" => "Double",
        "CoreFoundation.CGFloat" or "CGFloat" => "CGFloat",
        "Swift.String" or "String" => "String",
        // No raw value (tag-only enum like `enum Direction { case north }`) and any
        // unrecognized type name map to Int32 — the 32-bit transport the C# side uses
        // (EnumHandler.GetCSharpEnumUnderlyingType(null) == "int"). Emitting pointer-width
        // `Int` here while C# passes a 32-bit `int` is a latent ABI width mismatch that only
        // survives arm64 zero-extension of the argument register; both @_cdecl carriers
        // (CdeclParamMapper.Map and CdeclReturnMapping) must agree with C# on width. The
        // tag-only conversion (`load(as:)` from the low bytes / zero-init + copyMemory) is
        // width-agnostic, so a 4-byte transport reads the same low tag byte a wider one did.
        _ => "Int32"
    };

    /// <summary>
    /// Returns true for Optional&lt;T&gt; where T is a reference-like type (Class, ObjC-bridged, ObjC-rooted).
    /// These use nullable pointer ABI (UnsafeMutableRawPointer?) in @_cdecl wrappers.
    /// Delegates to <see cref="WrapperValidation.IsOptionalWithReferenceInner"/>.
    /// </summary>
    internal static bool IsOptionalWithReferenceInner(TypeSpec typeSpec, ITypeDatabase typeDatabase)
        => WrapperValidation.IsOptionalWithReferenceInner(typeSpec, typeDatabase);

    /// <summary>
    /// Collects the internal binding names that <see cref="Map"/>/<see cref="MapInout"/> will emit for
    /// a wrapper's user parameters, so each per-param escape can dodge its SIBLINGS as well as the
    /// global synthetic set (the user-vs-sibling half of the reserved-name collision class). Mirrors the
    /// keyword-rename + <see cref="SwiftBuilder.SanitizeIdentifier"/> transform <c>Map</c> applies
    /// BEFORE its reserved-collision escape (the escape is the step the sibling set feeds into, so it
    /// is deliberately not replicated here).
    /// <para>
    /// Labels are derived as <c>PrivateName ?? Name</c> — the same source the multi-param emitters
    /// pass to <c>Map</c>. The <c>_</c>→<c>arg{i}</c> index substitution those emitters apply to
    /// unnamed params is intentionally omitted: a reserved-name escape (e.g. <c>tag</c>→<c>__tag</c>)
    /// can never land on <c>arg{i}</c>, so an indexed sibling is never a collision target, and the
    /// resulting set is at worst a harmless superset (over-reserving only ever picks a different,
    /// still-valid escaped name — never a colliding one).
    /// </para>
    /// </summary>
    internal static IReadOnlySet<string> CollectSiblingBindingNames(IEnumerable<ArgumentDecl> args)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var arg in args)
        {
            var label = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            if (string.IsNullOrEmpty(label) || label == "_")
                continue;
            if (NameProvider.IsSwiftKeyword(label))
                label = $"{label}Param";
            names.Add(SwiftBuilder.SanitizeIdentifier(label));
        }
        return names;
    }

    /// <summary>
    /// Returns <paramref name="siblings"/> with the current param's own (already keyword/sanitized)
    /// binding removed, so the per-param reserved-collision escape never escapes a binding against
    /// itself. Returns the set unchanged (no allocation) when it does not contain
    /// <paramref name="label"/>.
    /// </summary>
    internal static IReadOnlySet<string>? ExcludeSelf(IReadOnlySet<string>? siblings, string label)
    {
        if (siblings == null || siblings.Count == 0 || !siblings.Contains(label))
            return siblings;
        var filtered = new HashSet<string>(siblings, StringComparer.Ordinal);
        filtered.Remove(label);
        return filtered;
    }

    /// <summary>
    /// Canonical derivation of the INTERNAL Swift binding name for a user-derived wrapper parameter:
    /// keyword rename, then identifier sanitization, then reserved/sibling collision escape. All
    /// three steps exist for the same reason — a user param name that is not a usable Swift binding —
    /// and they must be applied together and in this order, since each later step assumes the earlier
    /// ones have run (the sibling set is documented as holding post-keyword/sanitize forms).
    /// <para>
    /// This is the single core. Splitting it lets an emitter apply a subset: a
    /// <c>@_silgen_name</c> shim that escaped only the reserved collision emitted a parameter named
    /// with a bare Swift keyword (<c>_ extension: String</c>) and forwarded it unescaped, which
    /// <c>swiftc</c> rejects — while its own call-site LABEL, built by <see cref="BuildSwiftCallArgLabel"/>,
    /// was backtick-escaped and so looked correct. Both the parameter declaration and every value
    /// reference to it must come from here.
    /// </para>
    /// <para>
    /// Renaming is output-safe for the same reason each step already was: the internal binding is
    /// source-local — it is not part of the positional <c>@_cdecl</c> C ABI, and the forwarded Swift
    /// call's external argument label is computed separately from <c>arg.Name</c>/
    /// <c>OriginalSwiftName</c>, never from this binding.
    /// </para>
    /// </summary>
    /// <param name="rawLabel">The user-derived name as it arrives from the parser.</param>
    /// <param name="reservedSiblings">
    /// The other internal binding names emitted into the same wrapper signature. The current label is
    /// stripped internally via <see cref="ExcludeSelf"/>, so callers pass their full set.
    /// </param>
    /// <param name="escapeReservedCollision">
    /// False only when the label IS the synthetic itself and so must not be escaped away from it.
    /// The keyword and sanitization steps still run.
    /// </param>
    internal static string BuildSwiftBindingName(
        string rawLabel,
        IReadOnlySet<string>? reservedSiblings = null,
        bool escapeReservedCollision = true)
    {
        var label = NameProvider.IsSwiftKeyword(rawLabel) ? $"{rawLabel}Param" : rawLabel;
        label = SwiftBuilder.SanitizeIdentifier(label);

        if (escapeReservedCollision)
            label = NameProvider.EscapeReservedSwiftWrapperLabel(label, ExcludeSelf(reservedSiblings, label));

        return label;
    }

    /// <summary>
    /// Maps an inout parameter to its @_cdecl-compatible representation with write-back semantics.
    /// All inout params use UnsafeMutableRawPointer in the @_cdecl signature. The wrapper creates
    /// a mutable local (var), passes it by reference (&amp;), and writes back the modified value.
    /// </summary>
    /// <returns>
    /// A 4-tuple: (cdeclParam, reconstruction, callArg, writeBack) where writeBack is the
    /// statement to store the modified value back through the pointer after the method call.
    /// </returns>
    internal static (string cdeclParam, string reconstruction, string callArg, string writeBack) MapInout(
        ArgumentDecl arg, string label, MethodEnvironment env, bool omitLabels = false,
        IReadOnlySet<string>? reservedSiblings = null)
    {
        // inout is a category of the single Describe producer (Category=Inout); this 4-tuple shim
        // projects the descriptor's Swift-text fields plus the write-back. Reconstruction and
        // WriteBack are always populated for the Inout category, so the null-forgiving projection is
        // safe. MapInout always escapes a reserved/sibling label collision (escapeReservedCollision:
        // true) just as it did inline before the fold.
        var d = Describe(arg, label, env, omitLabels, useUtf8Strings: false,
                         escapeReservedCollision: true, reservedSiblings, isInout: true);
        return (d.CdeclParam, d.Reconstruction!, d.CallArg, d.WriteBack!);
    }
}
