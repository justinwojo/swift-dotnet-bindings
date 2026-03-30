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
    internal static (string cdeclParam, string? reconstruction, string callArg) Map(
        ArgumentDecl arg, string label, MethodEnvironment env, bool omitLabels = false, bool useUtf8Strings = false)
    {
        var swiftTypeSpec = arg.SwiftTypeSpec;

        // Swift keywords (in, for, repeat, etc.) can't be used as bare identifiers
        // in @_cdecl wrapper bodies. Rename to avoid conflicts — the call argument
        // label comes from arg.Name, so it's unaffected by this rename.
        if (NameProvider.IsSwiftKeyword(label))
            label = $"{label}Param";

        // Strip type-syntax characters (<>[]()) that could appear in demangled parameter names
        label = SwiftBuilder.SanitizeIdentifier(label);

        // Determine the Swift argument label for the init call
        // When calling _dbw_init_* (omitLabels=true), all params use _ (no external label)
        var argLabel = omitLabels ? "" : arg.Name switch
        {
            var n when n.StartsWith("arg") => "",
            "_" => "",  // Unlabeled parameter (Name set to "_") — no argument label
            var n when n.StartsWith("_") => $"{n.Substring(1)}: ",
            var n when string.IsNullOrEmpty(n) => "",
            var n => $"{n}: "
        };

        // Primitives pass through directly
        if (IsCdeclPrimitive(swiftTypeSpec))
        {
            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(swiftTypeSpec);

            // Bool: Swift @_cdecl receives Int8, needs != 0 conversion
            if (MarshallingHelpers.IsBoolType(swiftType) || swiftType == "Bool")
            {
                return ($"_ {label}: Int8",
                        $"let {label}Val = {label} != 0",
                        $"{argLabel}{label}Val");
            }

            return ($"_ {label}: {swiftType}", null, $"{argLabel}{label}");
        }

        // AnyObject: IS a class reference by definition — use Unmanaged<AnyObject> marshalling.
        // Without this, AnyObject falls through to protocol existential path which emits
        // `any AnyObject.self` (not valid Swift metatype syntax).
        if (IsAnyObjectType(swiftTypeSpec))
        {
            return ($"_ {label}: UnsafeMutableRawPointer",
                    $"let {label}Val: AnyObject = Unmanaged<AnyObject>.fromOpaque({label}).takeUnretainedValue()",
                    $"{argLabel}{label}Val");
        }

        // Protocol existentials are not C-representable in @_cdecl functions.
        // Marshal as UnsafeRawPointer and reconstruct inside the wrapper body.
        if (IsProtocolExistentialType(swiftTypeSpec, env.TypeDatabase))
        {
            var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
            // NamedTypeSpec protocol existentials need explicit "any" prefix for Swift 6.
            // ProtocolListTypeSpec already includes "any" from RenderSwiftTypeSpecCore.
            // Optional<Protocol> needs "any" on the INNER type: Optional<any Protocol>,
            // NOT on the outer type (Swift rejects "any Optional<Protocol>").
            if (swiftTypeSpec is NamedTypeSpec namedSpec && !swiftType.StartsWith("any "))
            {
                if (IsOptionalProtocolExistential(namedSpec, env.TypeDatabase))
                {
                    // Optional<Protocol> → Optional<any Protocol>
                    var innerType = namedSpec.GenericParameters[0];
                    var innerSwiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(innerType);
                    swiftType = $"Swift.Optional<(any {innerSwiftType})>";
                }
                else
                {
                    swiftType = $"any {swiftType}";
                }
            }
            // Existential types need parenthesization in metatype position: (any Protocol).self
            var loadType = swiftType.StartsWith("any ") ? $"({swiftType})" : swiftType;
            return ($"_ {label}: UnsafeRawPointer",
                    $"let {label}Val: {loadType} = {label}.load(as: {loadType}.self)",
                    $"{argLabel}{label}Val");
        }

        // Optional<reference type>: nullable pointer ABI.
        // C# passes IntPtr (0 for nil, object pointer for non-nil) via PayloadBuffer<IntPtr>.Buffer.
        // @_cdecl receives UnsafeMutableRawPointer? (nullable pointer maps to void* in C ABI).
        if (IsOptionalWithReferenceInner(swiftTypeSpec, env.TypeDatabase))
        {
            var innerType = ((NamedTypeSpec)swiftTypeSpec).GenericParameters[0];
            var swiftInnerType = ExistentialBypassEmitter.RenderSwiftTypeSpec(innerType);

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

            return ($"_ {label}: UnsafeMutableRawPointer?",
                    reconstruction,
                    $"{argLabel}{label}Val");
        }

        // Optional<BlittablePrimitive>: read value and tag byte separately from UnsafeRawPointer
        // instead of using assumingMemoryBound(to: Optional<T>.self).pointee, which misinterprets
        // the tag byte for Optional<Int32> on some runtimes.
        if (swiftTypeSpec is NamedTypeSpec optSpec && optSpec.Name == "Swift.Optional"
            && optSpec.GenericParameters.Count == 1)
        {
            var innerSpec = optSpec.GenericParameters[0];
            if (innerSpec is NamedTypeSpec innerNamed && IsBlittablePrimitiveSwiftType(innerNamed.Name))
            {
                // When calling _dbw_init_* (omitLabels=true), the dispatch method accepts
                // UnsafeRawPointer and decodes the Optional internally. Pass the pointer through
                // to avoid type mismatch (Optional<Int> vs UnsafeRawPointer).
                if (omitLabels)
                {
                    return ($"_ {label}: UnsafeRawPointer", null, $"{label}");
                }
                var rawType = GetSwiftRawValueType(innerNamed.Name);
                // Compute the tag byte offset = size of the inner type (centralized in OptionalMarshalClassifier)
                var tagOffset = OptionalMarshalClassifier.GetSwiftTagByteOffsetString(innerNamed.Name) ?? "8";
                // Read payload and tag separately, reconstruct Optional
                var reconstruction = $"let {label}Opt: {rawType}? = {label}.advanced(by: {tagOffset}).load(as: UInt8.self) == 0 ? {label}.load(as: {rawType}.self) : nil";
                return ($"_ {label}: UnsafeRawPointer",
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
                        return ($"_ {label}: UnsafeRawPointer", null, $"{label}");
                    }

                    var innerSwiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(innerSpec);
                    var reconstruction = $"let {label}Val: {innerSwiftType}? = {label}.assumingMemoryBound(to: UnsafeMutableRawPointer?.self).pointee.map {{ $0.assumingMemoryBound(to: {innerSwiftType}.self).pointee }}";
                    return ($"_ {label}: UnsafeRawPointer",
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
            return ($"_ {label}: UnsafeMutableRawPointer",
                    $"let {label}Val: {swiftType} = Unmanaged<AnyObject>.fromOpaque({label}).takeUnretainedValue() as! {swiftType}",
                    $"{argLabel}{label}Val");
        }

        // Optional<container with ObjC-bridgeable elements>: nullable ObjC collection pointer.
        if (IsOptionalObjCBridgeableContainer(swiftTypeSpec, env.TypeDatabase))
        {
            var innerSpec = ((NamedTypeSpec)swiftTypeSpec).GenericParameters[0];
            var swiftInnerType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(innerSpec);
            return ($"_ {label}: UnsafeMutableRawPointer?",
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
                return ($"_ {label}: UnsafeRawPointer",
                        null,
                        $"{label}");
            }

            // Use assumingMemoryBound(to:).pointee instead of load(as:) — for generic containers
            // like Optional<EnumWithAssociatedValues>, load(as:) can SIGSEGV because the container
            // may not satisfy BitwiseCopyable constraints. assumingMemoryBound(to:).pointee
            // uses typed pointer access with proper value semantics.
            var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
            return ($"_ {label}: UnsafeRawPointer",
                    $"let {label}Val = {label}.assumingMemoryBound(to: {swiftType}.self).pointee",
                    $"{argLabel}{label}Val");
        }

        // Foundation.Date: @_cdecl bridges Date ↔ NSDate* (ObjC interop) which is incompatible
        // with the raw double that C# passes. Accept Double and reconstruct Date inside wrapper.
        if (swiftTypeSpec is NamedTypeSpec dateNamed && dateNamed.Name == "Foundation.Date")
        {
            return ($"_ {label}: Double",
                    $"let {label}Val = Foundation.Date(timeIntervalSinceReferenceDate: {label})",
                    $"{argLabel}{label}Val");
        }

        // Foundation.Data: @_cdecl bridges Data ↔ NSData* (ObjC interop) which is incompatible
        // with the raw Data buffer that C# passes via CallConvCdecl.
        // Accept as two Int words matching the 16-byte struct layout and reconstruct.
        // On ARM64, C# passes Swift.Data (16-byte struct) in two consecutive GP registers,
        // exactly matching two Int parameters in the @_cdecl signature.
        // Same pattern as the String ↔ NSString* workaround.
        if (swiftTypeSpec is NamedTypeSpec dataNamed && dataNamed.Name == "Foundation.Data")
        {
            return ($"_ _dW0_{label}: Int, _ _dW1_{label}: Int",
                    $"let {label}Val = unsafeBitCast((_dW0_{label}, _dW1_{label}), to: Foundation.Data.self)",
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
                return ($"_ {label}Utf8Ptr: UnsafePointer<UInt8>, _ {label}Utf8Len: Int",
                        $"let {label}Val = String(bytes: UnsafeBufferPointer(start: {label}Utf8Ptr, count: {label}Utf8Len), encoding: .utf8)!",
                        $"{argLabel}{label}Val");
            }
            else
            {
                // Two Int words matching the 16-byte buffer layout: C# passes SwiftString.Buffer
                // (16-byte struct) in two consecutive GP registers on ARM64.
                // Used by constructor/method wrappers where C# marshals via SwiftString.
                return ($"_ _sW0_{label}: Int, _ _sW1_{label}: Int",
                        $"let {label}Val = unsafeBitCast((_sW0_{label}, _sW1_{label}), to: String.self)",
                        $"{argLabel}{label}Val");
            }
        }

        // Classes: receive as UnsafeMutableRawPointer, reconstruct via Unmanaged
        if (env.TypeDatabase.TryGetTypeRecord(swiftTypeSpec, out var typeRecord))
        {
            // Non-copyable structs (~Copyable): pass as pointer, use inline borrow without copy.
            // let xVal = ptr.assumingMemoryBound(to: T.self).pointee creates a copy which
            // noncopyable types reject. Instead, pass the inline borrow expression as the call arg.
            // UnsafePointer<T: ~Copyable>.pointee gives a borrow in Swift 6 — safe for passing
            // to functions that take borrowing parameters.
            if (typeRecord.Flags.HasFlag(TypeRecordFlags.NonCopyable))
            {
                var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
                return ($"_ {label}: UnsafeRawPointer",
                        null,  // no reconstruction — inline borrow avoids copy
                        $"{argLabel}{label}.assumingMemoryBound(to: {swiftType}.self).pointee");
            }

            if (typeRecord.Kind == TypeRecordKind.Class ||
                MarshallingHelpers.IsObjCBridged(typeRecord) ||
                MarshallingHelpers.IsObjCRooted(typeRecord))
            {
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(swiftTypeSpec);

                // Check for NSString typedef structs (e.g., CALayerContentsGravity, CATransitionType).
                // These are ObjC-bridged in the type database but are Swift structs wrapping NSString,
                // not class types. Unmanaged<T> requires T to be a class, so reconstruct via
                // NSString → String → init(rawValue:) instead.
                if (swiftTypeSpec is NamedTypeSpec nsTypedef &&
                    AppleFrameworkRegistry.TryGetNetTypeName(nsTypedef.Name, out var remapped) &&
                    remapped == "Foundation.NSString")
                {
                    return ($"_ {label}: UnsafeMutableRawPointer",
                            $"let {label}Val = {swiftType}(rawValue: Unmanaged<NSString>.fromOpaque({label}).takeUnretainedValue() as String)",
                            $"{argLabel}{label}Val");
                }

                // ObjC-bridged types (e.g., IndexPath bridged to NSIndexPath) may be Swift structs
                // but passed as class pointers across FFI. Use Unmanaged<AnyObject> + cast to handle
                // both true classes and bridged structs safely. Unmanaged<T> requires T: AnyObject,
                // so Unmanaged<IndexPath> fails for bridged structs.
                if (MarshallingHelpers.IsObjCBridged(typeRecord))
                {
                    return ($"_ {label}: UnsafeMutableRawPointer",
                            $"let {label}Val = Unmanaged<AnyObject>.fromOpaque({label}).takeUnretainedValue() as! {swiftType}",
                            $"{argLabel}{label}Val");
                }

                return ($"_ {label}: UnsafeMutableRawPointer",
                        $"let {label}Val = Unmanaged<{swiftType}>.fromOpaque({label}).takeUnretainedValue()",
                        $"{argLabel}{label}Val");
            }

            // ObjC-bridgeable value types (URL): Swift auto-bridges to ObjC class pointer
            // via _ObjectiveCBridgeable. Use Unmanaged<AnyObject> + cast, same as ObjCBridged.
            if (MarshallingHelpers.IsObjCBridgeable(typeRecord))
            {
                var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(swiftTypeSpec);
                return ($"_ {label}: UnsafeMutableRawPointer",
                        $"let {label}Val = Unmanaged<AnyObject>.fromOpaque({label}).takeUnretainedValue() as! {swiftType}",
                        $"{argLabel}{label}Val");
            }

            // Protocol/Existential TypeRecords: not C-representable, pass as pointer
            if (typeRecord.Kind == TypeRecordKind.Protocol ||
                typeRecord.Kind == TypeRecordKind.Existential)
            {
                var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
                return ($"_ {label}: UnsafeRawPointer",
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
                if (!string.IsNullOrEmpty(typeRecord.RawValueTypeName))
                {
                    // RawRepresentable enum: init(rawValue:) safely maps raw value → case
                    // regardless of in-memory storage size. The synthesized init?(rawValue:)
                    // has the same access level as the type and is always available from
                    // the wrapper module. Guard against invalid raw values from C# (e.g.,
                    // casting an arbitrary integer to the enum type).
                    conversion = $"guard let {label}Val = {swiftType}(rawValue: {label}) else {{ preconditionFailure(\"Invalid raw value \\({label}) for {swiftType}\") }}";
                }
                else
                {
                    // Tag-only enum (no RawRepresentable): C# sends the case index as
                    // a widened integer. Extract the tag from the low bytes via safe
                    // memory load (little-endian: tag is in the first N bytes).
                    conversion = $"var {label}Raw = {label}; let {label}Val = withUnsafeMutablePointer(to: &{label}Raw) {{ UnsafeMutableRawPointer($0).load(as: {swiftType}.self) }}";
                }

                return ($"_ {label}: {rawType}", conversion, $"{argLabel}{label}Val");
            }

            // Complex enums: pass as pointer.
            // Use assumingMemoryBound(to:).pointee instead of load(as:) — for enums with
            // non-BitwiseCopyable fields (e.g., String raw-value enums), load(as:) creates
            // a bitwise copy without proper reference semantics, causing SIGBUS.
            // assumingMemoryBound(to:).pointee uses typed pointer access with proper value semantics.
            if (typeRecord.Kind == TypeRecordKind.Enum)
            {
                var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
                return ($"_ {label}: UnsafeRawPointer",
                        $"let {label}Val = {label}.assumingMemoryBound(to: {swiftType}.self).pointee",
                        $"{argLabel}{label}Val");
            }

            // Non-frozen structs: C# passes SafeHandle (IntPtr), receive as pointer.
            // Use assumingMemoryBound for consistency with enum/container paths.
            if (!MarshallingHelpers.IsTypeFrozen(typeRecord))
            {
                var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
                return ($"_ {label}: UnsafeRawPointer",
                        $"let {label}Val = {label}.assumingMemoryBound(to: {swiftType}.self).pointee",
                        $"{argLabel}{label}Val");
            }

            // Frozen structs: system/Apple types pass by-value, custom types via UnsafeRawPointer.
            if (MarshallingHelpers.IsTypeFrozen(typeRecord))
            {
                // System framework frozen structs (CGRect, Date, etc.) are C-representable
                // and safe for @_cdecl by-value passing. Custom frozen structs from third-party
                // libraries trigger "Swift structs cannot be represented in Objective-C".
                if (swiftTypeSpec is NamedTypeSpec frozenNamed && IsSystemFrozenStruct(frozenNamed))
                {
                    var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(swiftTypeSpec);
                    return ($"_ {label}: {swiftType}", null, $"{argLabel}{label}");
                }

                // Custom frozen structs: pass as UnsafeRawPointer and reconstruct.
                // Use assumingMemoryBound(to:).pointee instead of load(as:) — frozen structs
                // with reference-counted fields (String, Array, Optional) are not BitwiseCopyable.
                var moduleQualifiedType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
                return ($"_ {label}: UnsafeRawPointer",
                        $"let {label}Val = {label}.assumingMemoryBound(to: {moduleQualifiedType}.self).pointee",
                        $"{argLabel}{label}Val");
            }
        }

        // Fallback: pass as UnsafeRawPointer.
        // Use assumingMemoryBound for consistency with all other pointer reconstruction paths.
        var fallbackSwiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);
        return ($"_ {label}: UnsafeRawPointer",
                $"let {label}Val = {label}.assumingMemoryBound(to: {fallbackSwiftType}.self).pointee",
                $"{argLabel}{label}Val");
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

        // Single protocol referenced by name: check TypeRecord
        if (typeSpec is NamedTypeSpec singleNamed &&
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

        if (inner is NamedTypeSpec innerNamed &&
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
    /// Checks whether a type spec is a generic container type (Optional, Array, Dictionary, Set, Result).
    /// These Swift generic types are not C-representable in @_cdecl functions and must be
    /// marshalled as UnsafeRawPointer with .load(as:) reconstruction in the wrapper body.
    /// </summary>
    internal static bool IsGenericContainerType(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec named || named.GenericParameters.Count == 0)
            return false;

        return named.Name is "Swift.Optional" or "Swift.Array" or "Swift.Dictionary"
            or "Swift.Set" or "Swift.Result";
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
        _ => "Int" // fallback
    };

    /// <summary>
    /// Returns true for Optional&lt;T&gt; where T is a reference-like type (Class, ObjC-bridged, ObjC-rooted).
    /// These use nullable pointer ABI (UnsafeMutableRawPointer?) in @_cdecl wrappers.
    /// Delegates to <see cref="WrapperValidation.IsOptionalWithReferenceInner"/>.
    /// </summary>
    internal static bool IsOptionalWithReferenceInner(TypeSpec typeSpec, ITypeDatabase typeDatabase)
        => WrapperValidation.IsOptionalWithReferenceInner(typeSpec, typeDatabase);

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
        ArgumentDecl arg, string label, MethodEnvironment env, bool omitLabels = false)
    {
        var swiftTypeSpec = arg.SwiftTypeSpec;

        // Swift keywords and identifier sanitization (same as Map)
        if (NameProvider.IsSwiftKeyword(label))
            label = $"{label}Param";
        label = SwiftBuilder.SanitizeIdentifier(label);

        // Argument label (same as Map)
        var argLabel = omitLabels ? "" : arg.Name switch
        {
            var n when n.StartsWith("arg") => "",
            "_" => "",
            var n when n.StartsWith("_") => $"{n.Substring(1)}: ",
            var n when string.IsNullOrEmpty(n) => "",
            var n => $"{n}: "
        };

        var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(swiftTypeSpec);

        // Protocol existentials need "any" prefix in Swift 6.
        // Optional<Protocol> needs "any" on the INNER type: Optional<any Protocol>,
        // NOT on the outer type (Swift rejects "any Optional<Protocol>").
        if (IsProtocolExistentialType(swiftTypeSpec, env.TypeDatabase))
        {
            if (swiftTypeSpec is NamedTypeSpec namedSpec && !swiftType.StartsWith("any "))
            {
                if (IsOptionalProtocolExistential(namedSpec, env.TypeDatabase))
                {
                    var innerType = namedSpec.GenericParameters[0];
                    var innerSwiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(innerType);
                    swiftType = $"Swift.Optional<(any {innerSwiftType})>";
                }
                else
                {
                    swiftType = $"any {swiftType}";
                }
            }
        }

        // Bool: stored as Int8 in @_cdecl ABI, needs conversion
        var renderedType = ExistentialBypassEmitter.RenderSwiftTypeSpec(swiftTypeSpec);
        if (MarshallingHelpers.IsBoolType(renderedType) || renderedType == "Bool")
        {
            return ($"_ {label}: UnsafeMutableRawPointer",
                    $"var {label}Val: Bool = {label}.assumingMemoryBound(to: Int8.self).pointee != 0",
                    $"{argLabel}&{label}Val",
                    $"{label}.assumingMemoryBound(to: Int8.self).pointee = {label}Val ? 1 : 0");
        }

        // All other types: UnsafeMutableRawPointer with typed pointer access.
        // Uses assumingMemoryBound(to:).pointee for proper value semantics (not load(as:)
        // which requires BitwiseCopyable). The var binding is mutable so it can be passed
        // as &ref to the original Swift method, then written back through the pointer.
        return ($"_ {label}: UnsafeMutableRawPointer",
                $"var {label}Val = {label}.assumingMemoryBound(to: {swiftType}.self).pointee",
                $"{argLabel}&{label}Val",
                $"{label}.assumingMemoryBound(to: {swiftType}.self).pointee = {label}Val");
    }
}
