// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration.ObjC;

public static class ObjCTypeMapper
{
    static readonly Dictionary<string, string> PointerTypeMappings = new()
    {
        ["NSString"] = "string",
        ["NSArray"] = "NSArray",
        ["NSDictionary"] = "NSDictionary",
        ["NSData"] = "NSData",
        ["NSURL"] = "NSUrl",
        ["NSNumber"] = "NSNumber",
        ["NSError"] = "NSError",
        ["NSSet"] = "NSSet",
        ["NSDate"] = "NSDate",
        ["NSObject"] = "NSObject",
        ["CGImageRef"] = "CGImage",
        ["NSURLSession"] = "NSUrlSession",
        ["NSURLSessionTask"] = "NSUrlSessionTask",
        ["NSURLSessionDataTask"] = "NSUrlSessionDataTask",
        ["NSURLSessionDownloadTask"] = "NSUrlSessionDownloadTask",
        ["NSURLSessionUploadTask"] = "NSUrlSessionUploadTask",
        ["NSURLSessionStreamTask"] = "NSUrlSessionStreamTask",
        ["NSURLSessionConfiguration"] = "NSUrlSessionConfiguration",
        ["NSURLSessionTaskMetrics"] = "NSUrlSessionTaskMetrics",
        ["NSURLSessionTaskTransactionMetrics"] = "NSUrlSessionTaskTransactionMetrics",
        ["NSURLSessionWebSocketTask"] = "NSUrlSessionWebSocketTask",
        ["NSURLCredential"] = "NSUrlCredential",
        ["NSURLCredentialStorage"] = "NSUrlCredentialStorage",
        ["NSURLAuthenticationChallenge"] = "NSUrlAuthenticationChallenge",
        ["NSURLProtectionSpace"] = "NSUrlProtectionSpace",
        ["NSURLCache"] = "NSUrlCache",
        ["NSUUID"] = "NSUuid",
        ["NSTimeZone"] = "NSTimeZone",
        ["NSURLRequest"] = "NSUrlRequest",
        ["NSURLResponse"] = "NSUrlResponse",
        ["NSURLConnection"] = "NSUrlConnection",
        ["NSHTTPURLResponse"] = "NSHttpUrlResponse",
        ["NSHTTPCookie"] = "NSHttpCookie",
        ["NSHTTPCookieStorage"] = "NSHttpCookieStorage",
        ["NSCachedURLResponse"] = "NSCachedUrlResponse",
        ["NSMutableURLRequest"] = "NSMutableUrlRequest",
        ["BOOL"] = "bool",
    };

    // CoreFoundation Ref typedefs and opaque types that appear without '*' in clang AST.
    static readonly Dictionary<string, string> CoreFoundationRefMappings = new()
    {
        ["CGImageRef"] = "CGImage",
        ["CGColorRef"] = "CGColor",
        ["CGPathRef"] = "CGPath",
        ["CGContextRef"] = "CGContext",
        ["dispatch_queue_t"] = "DispatchQueue",
        ["dispatch_data_t"] = "DispatchData",
        ["dispatch_block_t"] = "Action",
        ["CFUUIDRef"] = "IntPtr",
        ["CFTypeRef"] = "IntPtr",
        ["CFArrayRef"] = "IntPtr",
        ["CFDataRef"] = "IntPtr",
        ["CFStringRef"] = "IntPtr",
        ["CFErrorRef"] = "IntPtr",
        ["CFIndex"] = "nint",
        ["CGColorSpaceRef"] = "CGColorSpace",
        ["CGLayerRef"] = "IntPtr",
        ["CVPixelBufferRef"] = "IntPtr",
        ["CVImageBufferRef"] = "IntPtr",
        ["IOSurfaceRef"] = "IntPtr",
        ["CGImageSourceRef"] = "IntPtr",
        ["CFAllocatorRef"] = "IntPtr",
        ["CFDictionaryRef"] = "IntPtr",
        ["dispatch_queue_attr_t"] = "IntPtr",
        ["dispatch_semaphore_t"] = "IntPtr",
        ["dispatch_group_t"] = "IntPtr",
        ["dispatch_source_t"] = "IntPtr",
        ["os_log_t"] = "IntPtr",
        ["os_log_type_t"] = "byte",
        ["os_unfair_lock_t"] = "IntPtr",
        ["os_unfair_lock"] = "IntPtr",
        ["SecKeyRef"] = "IntPtr",
        ["SecCertificateRef"] = "IntPtr",
        ["SecIdentityRef"] = "IntPtr",
        ["SecTrustRef"] = "IntPtr",
        ["SecPolicyRef"] = "IntPtr",
        ["AudioComponentInstance"] = "IntPtr",
        ["AudioUnit"] = "IntPtr",
        ["CMSampleBufferRef"] = "CMSampleBuffer",
    };

    static readonly Dictionary<string, string> PrimitiveTypeMappings = new()
    {
        ["BOOL"] = "bool",
        ["NSInteger"] = "nint",
        ["NSUInteger"] = "nuint",
        ["CGFloat"] = "nfloat",
        ["NSTimeInterval"] = "double",
        ["void"] = "void",
        ["int"] = "int",
        ["float"] = "float",
        ["double"] = "double",
        ["long"] = "long",
        ["unsigned int"] = "uint",
        ["unsigned long"] = "ulong",
        ["unsigned short"] = "ushort",
        ["unsigned char"] = "byte",
        ["short"] = "short",
        ["char"] = "byte",
        ["signed char"] = "sbyte",
        ["unsigned char"] = "byte",
        ["int8_t"] = "sbyte",
        ["long long"] = "long",
        ["unsigned long long"] = "ulong",
        ["uint8_t"] = "byte",
        ["UInt8"] = "byte",
        ["int32_t"] = "int",
        ["int64_t"] = "long",
        ["uint32_t"] = "uint",
        ["uint16_t"] = "ushort",
        ["int16_t"] = "short",
        ["uint64_t"] = "ulong",
        ["size_t"] = "nuint",
        ["va_list"] = "IntPtr",
        ["CFAbsoluteTime"] = "double",
        ["Float64"] = "double",
        ["Float32"] = "float",
        ["ABRecordID"] = "int",
        ["ABPropertyID"] = "int",
        ["CFComparisonResult"] = "nint",
        ["ABPropertyType"] = "int",
        ["ABPersonImageFormat"] = "int",
        ["ABRecordRef"] = "IntPtr",
        ["CLLocationDegrees"] = "double",
        ["CLLocationDistance"] = "double",
        ["CLLocationDirection"] = "double",
        ["CLLocationSpeed"] = "double",
        ["CLLocationAccuracy"] = "double",
        ["UIBackgroundTaskIdentifier"] = "nint",
        ["uint_least8_t"] = "byte",
        ["int_least8_t"] = "sbyte",
        ["uint_least16_t"] = "ushort",
        ["int_least16_t"] = "short",
        ["uint_least32_t"] = "uint",
        ["int_least32_t"] = "int",
        ["uint_least64_t"] = "ulong",
        ["int_least64_t"] = "long",
        ["uint_fast8_t"] = "byte",
        ["int_fast8_t"] = "sbyte",
        ["uint_fast16_t"] = "ushort",
        ["int_fast16_t"] = "short",
        ["uint_fast32_t"] = "uint",
        ["int_fast32_t"] = "int",
        ["uint_fast64_t"] = "ulong",
        ["int_fast64_t"] = "long",
        ["intptr_t"] = "nint",
        ["uintptr_t"] = "nuint",
        ["ptrdiff_t"] = "nint",
        ["ssize_t"] = "nint",
    };

    public static string MapType(ObjCTypeRef typeRef, string? declaringClassName = null, HashSet<string>? genericTypeParams = null, Dictionary<string, ObjCTypeRef>? typedefMap = null, Dictionary<string, ObjCTypeRef>? blockTypedefMap = null)
    {
        // 0a. C function pointers and anonymous records → IntPtr
        if (typeRef.IsFunctionPointer || typeRef.IsAnonymousRecord)
            return "IntPtr";

        // 0b. Fixed-size C array (e.g., uint8_t [4] → "byte[4]", NSString *[4] → "string[4]")
        // Must be checked before primitive mapping, which would discard the array size.
        if (typeRef.FixedArraySize is > 0)
        {
            var elementType = MapType(new ObjCTypeRef { Name = typeRef.Name, IsPointer = typeRef.IsPointer }, declaringClassName, genericTypeParams, typedefMap);
            return $"{elementType}[{typeRef.FixedArraySize}]";
        }

        // 1. Block types
        if (typeRef.IsBlock)
            return MapBlockType(typeRef, genericTypeParams, typedefMap);

        // 2. instancetype
        if (typeRef.Name == "instancetype")
            return declaringClassName ?? "NSObject";

        // 3. Protocol-qualified id (id<Proto> or id<Proto1, Proto2>)
        if (typeRef.Name == "id" && typeRef.ProtocolQualifications.Count > 0)
        {
            // Multi-protocol: id<Proto1, Proto2> — use the first bindable protocol.
            // Filter out NSObject (implicit in ObjC) and NSFastEnumeration (no .NET binding).
            var protocols = typeRef.ProtocolQualifications
                .Where(p => p != "NSObject" && p != "NSFastEnumeration")
                .ToList();
            if (protocols.Count == 0)
                return "NSObject";
            return $"I{MapProtocolName(protocols[0])}";
        }

        // 3b. Typed generic collections: NSArray<T> → T[], NSDictionary<K,V> → NSDictionary<K,V>
        if (typeRef.GenericArgs.Count > 0)
        {
            var mappedGeneric = MapGenericCollectionType(typeRef, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap);
            if (mappedGeneric != null)
                return mappedGeneric;
        }

        // 4. Known pointer types
        if (typeRef.IsPointer && PointerTypeMappings.TryGetValue(typeRef.Name, out var mapped))
            return mapped;

        // 5. Special non-pointer types
        if (typeRef.Name == "id")
            return "NSObject";
        if (typeRef.Name == "SEL")
            return "Selector";
        if (typeRef.Name == "Class")
            return "Class";

        // 6. Primitive types (void* → IntPtr, not "void")
        if (typeRef.Name == "void" && typeRef.IsPointer)
            return "IntPtr";
        if (PrimitiveTypeMappings.TryGetValue(typeRef.Name, out var primitive))
            return primitive;

        // 7. ObjC lightweight generic type parameters → NSObject
        // Only recognize params declared by the owning class via ObjCTypeParamDecl in the AST.
        // No hardcoded fallback set — avoids cross-type collisions where a generic param name
        // in one class matches a real type name used elsewhere.
        if (genericTypeParams != null && genericTypeParams.Contains(typeRef.Name))
            return "NSObject";

        // 8. CoreFoundation Ref types (typedefs for CF pointers, e.g., CGImageRef → CGImage)
        if (CoreFoundationRefMappings.TryGetValue(typeRef.Name, out var cfMapped))
            return cfMapped;

        // 9. Typedef alias resolution (pre-resolved, single-hop lookup)
        if (typedefMap != null && typedefMap.TryGetValue(typeRef.Name, out var resolved))
        {
            // Preserve pointer from usage when the typedef itself is non-pointer.
            // e.g., typedef NSString BRAlias; usage: BRAlias * → should resolve to NSString *
            if (typeRef.IsPointer && !resolved.IsPointer)
            {
                var withPointer = new ObjCTypeRef
                {
                    Name = resolved.Name,
                    IsPointer = true,
                    Nullability = typeRef.Nullability,
                    ProtocolQualifications = [.. resolved.ProtocolQualifications],
                    GenericArgs = [.. resolved.GenericArgs],
                    BlockReturnType = resolved.BlockReturnType,
                    IsBlock = resolved.IsBlock,
                };
                withPointer.BlockParams.AddRange(resolved.BlockParams);
                return MapType(withPointer, declaringClassName, genericTypeParams, typedefMap: null, blockTypedefMap: blockTypedefMap);
            }
            return MapType(resolved, declaringClassName, genericTypeParams, typedefMap: null, blockTypedefMap: blockTypedefMap);
        }

        // 10. Block typedef name resolution (e.g., RLMNotificationBlock → Action<string, RLMRealm>)
        if (blockTypedefMap != null && blockTypedefMap.TryGetValue(typeRef.Name, out var blockResolved))
            return MapBlockType(blockResolved, genericTypeParams, typedefMap);

        // 11. ObjC-to-.NET naming convention fallback (NS-prefix only)
        return ApplyDotNetAcronymConvention(typeRef.Name);
    }

    /// <summary>
    /// Maps an ObjC class name to its .NET MAUI binding name.
    /// Like MapProtocolName but for class names used in BaseType attributes.
    /// Applies acronym conventions without pointer type semantics
    /// (NSString stays NSString, not string).
    /// </summary>
    public static string MapClassName(string name)
    {
        // Check explicit pointer mappings for non-string types
        // (NSString → string is wrong for BaseType, keep NSString)
        if (PointerTypeMappings.TryGetValue(name, out var mapped) && mapped != "string" && mapped != "bool")
            return mapped;

        return ApplyDotNetAcronymConvention(name);
    }

    /// <summary>
    /// Maps an ObjC protocol name to its .NET MAUI binding convention name.
    /// E.g., NSURLSessionTaskDelegate → NSUrlSessionTaskDelegate, NSXPCListenerDelegate → NSXpcListenerDelegate.
    /// </summary>
    public static string MapProtocolName(string name) => ApplyDotNetAcronymConvention(name);

    /// <summary>
    /// Acronyms that Microsoft.iOS/MAUI bgen lowercases in the body of NS-prefixed
    /// managed type names (per .NET naming guidelines: acronym keeps first letter
    /// uppercase, rest lowercase when 3+ chars long). Ordered LONGER FIRST so that
    /// substring overlaps (HTTPS contains HTTP) are handled correctly via Replace.
    /// </summary>
    private static readonly (string ObjC, string Dotnet)[] AcronymConventions = new[]
    {
        ("HTTPS", "Https"),
        ("HTTP",  "Http"),
        ("JSON",  "Json"),
        ("HTML",  "Html"),
        ("URL",   "Url"),
        ("XPC",   "Xpc"),
    };

    /// <summary>
    /// Applies .NET naming convention to ObjC type names: NSXPC* → NSXpc*, NSURL* → NSUrl*,
    /// etc. Only triggers on NS-prefixed names — other framework prefixes (CB, CG, AV, ...)
    /// keep their original casing because Microsoft.iOS does not apply the convention there.
    /// </summary>
    internal static string ApplyDotNetAcronymConvention(string name)
    {
        if (!name.StartsWith("NS", StringComparison.Ordinal))
            return name;
        var result = name;
        foreach (var (objc, dn) in AcronymConventions)
        {
            if (result.Contains(objc, StringComparison.Ordinal))
                result = result.Replace(objc, dn, StringComparison.Ordinal);
        }
        return result;
    }

    /// <summary>
    /// Reverses <see cref="ApplyDotNetAcronymConvention"/> for SDK-name lookups: takes a
    /// .NET-cased managed name and recovers the original ObjC acronym casing so the name
    /// can be looked up in <c>appleSdkTypeNames</c> (which stores raw clang AST names).
    /// </summary>
    internal static string ReverseDotNetAcronymConvention(string mappedName)
    {
        var result = mappedName;
        foreach (var (objc, dn) in AcronymConventions)
        {
            if (result.Contains(dn, StringComparison.Ordinal))
                result = result.Replace(dn, objc, StringComparison.Ordinal);
        }
        return result;
    }

    /// <summary>
    /// Returns a formatted generic type hint for a type with GenericArgs, or null if none.
    /// E.g., NSArray&lt;NSString *&gt; → "Element type: string"
    ///       NSDictionary&lt;NSString *, NSNumber *&gt; → "Key type: string, Value type: NSNumber"
    /// </summary>
    public static string? FormatGenericTypeHint(ObjCTypeRef typeRef, string? declaringClassName = null, HashSet<string>? genericTypeParams = null, Dictionary<string, ObjCTypeRef>? typedefMap = null, Dictionary<string, ObjCTypeRef>? blockTypedefMap = null)
    {
        if (typeRef.GenericArgs.Count == 0)
            return null;

        // For generic type parameters (e.g., T in NSArray<T>), preserve the original name
        // instead of mapping to NSObject, since the hint is for human readability.
        var mappedArgs = typeRef.GenericArgs
            .Select(a =>
            {
                var mapped = genericTypeParams != null && genericTypeParams.Contains(a.Name)
                    ? a.Name
                    : MapType(a, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap);
                if (a.Nullability == ObjCNullability.Nullable)
                    mapped += " (nullable)";
                else if (a.Nullability == ObjCNullability.Nonnull)
                    mapped += " (nonnull)";
                return mapped;
            })
            .ToList();

        return typeRef.Name switch
        {
            "NSArray" or "NSMutableArray" or "NSSet" or "NSMutableSet" or "NSOrderedSet" or "NSMutableOrderedSet"
                when mappedArgs.Count == 1 => $"Element type: {mappedArgs[0]}",
            "NSDictionary" or "NSMutableDictionary"
                when mappedArgs.Count == 2 => $"Key type: {mappedArgs[0]}, Value type: {mappedArgs[1]}",
            _ => $"Generic args: {string.Join(", ", mappedArgs)}"
        };
    }

    public static Dictionary<string, ObjCTypeRef> BuildBlockTypedefMap(ObjCModule module) =>
        module.Typedefs
            .Where(t => t.UnderlyingType.IsBlock)
            .ToDictionary(t => t.Name, t => t.UnderlyingType);

    public static Dictionary<string, ObjCTypeRef> BuildResolvedTypedefMap(ObjCModule module)
    {
        // Use ResolutionTypedefs (all headers) when available for broader typedef resolution.
        // This resolves external C types (e.g., nanopb pb_type_t → uint_least8_t → byte)
        // that are defined in included headers outside the framework's own Headers directory.
        var sourceTypedefs = module.ResolutionTypedefs ?? module.Typedefs;

        var raw = new Dictionary<string, ObjCTypeRef>();
        var structNames = new HashSet<string>(module.Structs.Select(s => s.Name));

        foreach (var t in sourceTypedefs)
        {
            // Skip block typedefs (emitted as delegates) and struct typedefs (emitted as structs)
            if (t.UnderlyingType.IsBlock || structNames.Contains(t.Name))
                continue;
            raw[t.Name] = t.UnderlyingType;
        }

        // Resolve chains: A → B → NSString* becomes A → NSString*
        var resolved = new Dictionary<string, ObjCTypeRef>();
        foreach (var (name, typeRef) in raw)
        {
            var current = typeRef;
            var visited = new HashSet<string> { name };
            while (raw.TryGetValue(current.Name, out var next) && visited.Add(current.Name))
                current = next;
            resolved[name] = current;
        }
        return resolved;
    }

    /// <summary>
    /// Returns the set of all C# type names that MapType can produce via its built-in mappings
    /// (primitives, pointer types, CoreFoundation refs). Used by emitters to detect unresolvable
    /// passthrough types that would cause compile errors.
    /// </summary>
    public static HashSet<string> BuildKnownMappedTypes()
    {
        var known = new HashSet<string>();
        foreach (var v in PrimitiveTypeMappings.Values) known.Add(v);
        foreach (var v in PointerTypeMappings.Values) known.Add(v);
        foreach (var v in CoreFoundationRefMappings.Values) known.Add(v);
        // Types that MapType returns directly (not via dictionaries)
        known.Add("NSObject");
        known.Add("Selector");
        known.Add("Class");
        known.Add("NativeHandle");
        // Apple framework value types (CGPoint, CGRect, NSRange, etc.) — these are
        // recognized everywhere else in the type mapper as well-known Apple value
        // types but reach IsApiDefinitionTypeResolvable as raw passthrough names.
        // Without this, CoreGraphics/CoreLocation/etc. types get rejected under the
        // -fmodules fallback because their prefixes aren't registered as ObjC class
        // prefixes (those frameworks expose only C structs, not ObjC classes).
        foreach (var v in KnownAppleValueTypes) known.Add(v);
        return known;
    }

    /// <summary>
    /// Checks whether a mapped type name will be resolvable in StructsAndEnums.cs.
    /// Uses CamelCase heuristic: ObjC/Apple types start uppercase, C-internal types are snake_case.
    /// </summary>
    public static bool IsTypeResolvable(string mappedType, HashSet<string> knownTypes)
    {
        if (IsKnownMappedOrPatternType(mappedType, knownTypes)) return true;
        // ObjC/Apple framework types use CamelCase (e.g., CGBitmapInfo, UIColor, NSCoder).
        // C-internal types use snake_case (e.g., pb_wire_type_t, pb_size_t).
        // Accept uppercase-starting types — they're available via Apple framework using directives.
        if (mappedType.Length > 0 && char.IsUpper(mappedType[0])) return true;
        return false;
    }

    /// <summary>
    /// Checks whether a mapped type name will be resolvable in ApiDefinition.cs.
    /// Source-aware: uses Apple SDK type names collected during parsing to distinguish
    /// Apple framework types (available via .NET iOS bindings) from third-party types
    /// (not available). When <paramref name="appleSdkTypeNames"/> is null (e.g.
    /// <c>-fmodules</c> mode where SDK types are loaded from precompiled module files
    /// and never expanded into the AST), falls back to the known Apple framework ObjC
    /// class prefix list registered in <see cref="AppleFrameworkRegistry"/>. The bare
    /// "any uppercase letter" rule is too permissive there: under -fmodules a
    /// cross-framework type referenced from a sibling xcframework (e.g.
    /// <c>FIROptions</c> in FirebaseCore, used by a method declared in FirebaseCoreExtension)
    /// would otherwise pass the check and produce CS0246 at compile time.
    /// </summary>
    public static bool IsApiDefinitionTypeResolvable(string mappedType, HashSet<string> knownTypes, HashSet<string>? appleSdkTypeNames)
    {
        if (IsKnownMappedOrPatternType(mappedType, knownTypes)) return true;
        // Check Apple SDK types: classes and protocols declared in Apple SDK headers
        if (appleSdkTypeNames != null && appleSdkTypeNames.Count > 0)
        {
            if (ContainsAppleSdkType(appleSdkTypeNames, mappedType)) return true;
            // Protocol interfaces have I prefix (e.g., ICTTelephonyNetworkInfoDelegate → CTTelephonyNetworkInfoDelegate)
            if (mappedType.Length > 1 && mappedType[0] == 'I' && char.IsUpper(mappedType[1])
                && ContainsAppleSdkType(appleSdkTypeNames, mappedType[1..])) return true;
            return false;
        }
        // -fmodules fallback: only accept names whose head matches a known Apple
        // framework ObjC class prefix.
        if (mappedType.Length == 0 || !char.IsUpper(mappedType[0])) return false;
        if (AppleFrameworkRegistry.TypeNameStartsWithKnownObjCPrefix(mappedType)) return true;
        // Protocol interface form: I-prefix on an Apple class prefix
        // (e.g., INSObjectProtocol -> NSObjectProtocol).
        if (mappedType.Length > 1 && mappedType[0] == 'I' && char.IsUpper(mappedType[1])
            && AppleFrameworkRegistry.TypeNameStartsWithKnownObjCPrefix(mappedType[1..]))
            return true;
        return false;
    }

    /// <summary>
    /// Checks if a mapped C# type name exists in the Apple SDK type names set.
    /// The SDK set stores raw ObjC names (e.g., NSURLSessionDelegate, NSXPCConnection) but the
    /// mapped name uses .NET acronym convention (NSUrlSessionDelegate, NSXpcConnection), so we
    /// also try the reverse-renamed form.
    /// </summary>
    private static bool ContainsAppleSdkType(HashSet<string> appleSdkTypeNames, string mappedName)
    {
        if (appleSdkTypeNames.Contains(mappedName)) return true;
        var objcName = ReverseDotNetAcronymConvention(mappedName);
        if (!string.Equals(objcName, mappedName, StringComparison.Ordinal) && appleSdkTypeNames.Contains(objcName)) return true;
        return false;
    }

    private static bool IsKnownMappedOrPatternType(string mappedType, HashSet<string> knownTypes)
    {
        if (knownTypes.Contains(mappedType)) return true;
        // Action/Func delegate types (from block mappings)
        if (mappedType.StartsWith("Action", StringComparison.Ordinal) ||
            mappedType.StartsWith("Func<", StringComparison.Ordinal)) return true;
        // Array types (from fixed-size C arrays and typed NSArray<T>, e.g., "byte[]", "NSUrl[]")
        if (mappedType.EndsWith("]", StringComparison.Ordinal)) return true;
        // Typed generic collections: NSDictionary<K,V>, NSSet<T>
        if (mappedType.StartsWith("NSDictionary<", StringComparison.Ordinal) ||
            mappedType.StartsWith("NSSet<", StringComparison.Ordinal)) return true;
        return false;
    }

    public static bool IsNullableAttribute(ObjCTypeRef typeRef) =>
        typeRef.Nullability == ObjCNullability.Nullable;

    public static bool IsNSErrorOutParameter(ObjCTypeRef typeRef) =>
        typeRef.Name == "NSError"
        && typeRef.IsPointer
        && typeRef.PointeeType is { Name: "NSError", IsPointer: true };

    /// <summary>
    /// Detects whether a parameter type is a pointer to a value type (primitive, struct, enum),
    /// which should be emitted as an <c>out</c> parameter in C# bindings.
    /// Examples: <c>BOOL *</c> → <c>out bool</c>, <c>CGPoint *</c> → <c>out CGPoint</c>.
    /// ObjC object pointers (e.g., <c>NSObject *</c>) return false — they are references, not out-params.
    /// Double pointers (e.g., <c>NSError **</c>) also return false — they have their own handling.
    /// </summary>
    public static bool IsValueTypePointerParameter(ObjCTypeRef typeRef) =>
        IsValueTypePointerParameter(typeRef, typedefMap: null, enumNames: null);

    /// <summary>
    /// Overload that resolves through typedefs and recognizes enum types as value types.
    /// <paramref name="typedefMap"/> resolves typedef'd names (e.g., MyErrorCode → NSInteger).
    /// <paramref name="enumNames"/> is the set of enum type names defined in the module.
    /// </summary>
    public static bool IsValueTypePointerParameter(ObjCTypeRef typeRef, Dictionary<string, ObjCTypeRef>? typedefMap, HashSet<string>? enumNames)
    {
        // Must be a pointer type
        if (!typeRef.IsPointer) return false;
        // Double pointers are not value-type out params (e.g., NSError **)
        if (typeRef.PointeeType != null) return false;
        // Blocks, function pointers, anonymous records are not value type pointers
        if (typeRef.IsBlock || typeRef.IsFunctionPointer || typeRef.IsAnonymousRecord) return false;
        // id<Protocol> pointers are object types
        if (typeRef.ProtocolQualifications.Count > 0) return false;
        // Generic containers (NSArray<T> *) are object types
        if (typeRef.GenericArgs.Count > 0) return false;

        var name = typeRef.Name;

        // Resolve through typedefs: e.g., typedef NSInteger MyErrorCode; MyErrorCode * → out nint
        if (typedefMap != null && typedefMap.TryGetValue(name, out var resolved))
        {
            var underlying = resolved;
            var visited = new HashSet<string> { name };
            while (typedefMap.TryGetValue(underlying.Name, out var deeper) && visited.Add(underlying.Name))
                underlying = deeper;
            name = underlying.Name;
        }

        // void * → IntPtr, not an out param
        if (name == "void") return false;
        // id, Class, SEL — object/meta types, not value type pointers
        if (name is "id" or "Class" or "SEL" or "instancetype") return false;
        // CoreFoundation Ref types (dispatch_queue_t, CGImageRef, etc.) — opaque pointers
        if (CoreFoundationRefMappings.ContainsKey(name)) return false;

        // If it maps to a primitive, it's a value type pointer (e.g., BOOL *, int *, CGFloat *)
        if (PrimitiveTypeMappings.ContainsKey(name)) return true;

        // ObjC object types: known pointer type mappings (NSString *, NSObject *, etc.)
        if (PointerTypeMappings.ContainsKey(name)) return false;

        // Struct types from Apple frameworks (CG*, CL*, MK*, CM*, etc.)
        if (IsKnownAppleValueType(name)) return true;

        // Enum types are value types — pointer to enum is an out-param
        // Check both the resolved name (for typedef aliases) and the original name
        if (enumNames != null && (enumNames.Contains(name) || enumNames.Contains(typeRef.Name))) return true;

        return false;
    }

    /// <summary>
    /// Maps a value-type pointer parameter to its C# <c>out</c> type.
    /// E.g., <c>_Bool *</c> → <c>bool</c>, <c>CGPoint *</c> → <c>CGPoint</c>.
    /// Call only after <see cref="IsValueTypePointerParameter"/> returns true.
    /// </summary>
    public static string MapValueTypePointerParameterType(ObjCTypeRef typeRef, Dictionary<string, ObjCTypeRef>? typedefMap = null)
    {
        // Map the pointee (non-pointer version of the type)
        var pointee = new ObjCTypeRef { Name = typeRef.Name };
        return MapType(pointee, typedefMap: typedefMap);
    }

    // Apple framework struct/value types commonly used as pointer parameters.
    // These are C structs bridged to .NET value types.
    private static readonly HashSet<string> KnownAppleValueTypes =
    [
        "CGPoint", "CGSize", "CGRect", "CGVector", "CGAffineTransform",
        "UIEdgeInsets", "NSDirectionalEdgeInsets",
        "UIOffset", "UIFloatRange",
        "CLLocationCoordinate2D",
        "MKCoordinateSpan", "MKCoordinateRegion", "MKMapPoint", "MKMapSize", "MKMapRect",
        "CMTime", "CMTimeRange", "CMTimeMapping", "CMVideoDimensions",
        "CATransform3D",
        "NSRange",
        "SCNVector3", "SCNVector4", "SCNMatrix4",
        "simd_float2", "simd_float3", "simd_float4",
        "simd_float4x4", "simd_float3x3",
        "MTLOrigin", "MTLSize", "MTLRegion",
        "AVAudio3DPoint", "AVAudio3DVector", "AVAudio3DAngularOrientation",
    ];

    private static bool IsKnownAppleValueType(string name) =>
        KnownAppleValueTypes.Contains(name);

    static string MapBlockType(ObjCTypeRef typeRef, HashSet<string>? genericTypeParams = null, Dictionary<string, ObjCTypeRef>? typedefMap = null)
    {
        var returnType = typeRef.BlockReturnType != null
            ? MapType(typeRef.BlockReturnType, genericTypeParams: genericTypeParams, typedefMap: typedefMap)
            : "void";

        var paramTypes = typeRef.BlockParams.Select(p => MapType(p, genericTypeParams: genericTypeParams, typedefMap: typedefMap)).ToList();

        if (paramTypes.Count > 16)
            return "NSObject";

        if (returnType == "void")
        {
            return paramTypes.Count == 0
                ? "Action"
                : $"Action<{string.Join(", ", paramTypes)}>";
        }

        var allTypes = paramTypes.Append(returnType);
        return $"Func<{string.Join(", ", allTypes)}>";
    }

    /// <summary>
    /// Maps ObjC generic collection types to their C# equivalents:
    /// - NSArray&lt;T&gt; / NSMutableArray&lt;T&gt; → T[] (when T is a concrete type, not a generic param)
    /// - NSDictionary&lt;K,V&gt; / NSMutableDictionary&lt;K,V&gt; → NSDictionary&lt;K,V&gt; (preserves generic args)
    /// - NSSet&lt;T&gt; / NSMutableSet&lt;T&gt; / NSOrderedSet&lt;T&gt; → NSSet&lt;T&gt; (preserves generic args)
    /// Returns null if the type doesn't qualify for generic mapping (e.g., generic param element type).
    /// </summary>
    private static string? MapGenericCollectionType(ObjCTypeRef typeRef, string? declaringClassName, HashSet<string>? genericTypeParams, Dictionary<string, ObjCTypeRef>? typedefMap, Dictionary<string, ObjCTypeRef>? blockTypedefMap)
    {
        // NSArray<T> / NSMutableArray<T> with a single concrete element type → T[]
        if (typeRef.Name is "NSArray" or "NSMutableArray" && typeRef.GenericArgs.Count == 1)
        {
            var elemArg = typeRef.GenericArgs[0];
            // If element is a generic type parameter (e.g., ObjectType from class decl), fall through to NSObject[]
            // which isn't useful — return null to let normal mapping handle it as plain NSArray.
            if (genericTypeParams != null && genericTypeParams.Contains(elemArg.Name))
                return null;
            var mappedElem = MapType(elemArg, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap);
            // Closures (Action/Func<>) and nested arrays (T[][]) don't implement INativeObject,
            // which is required by bgen's NSArray.FromNSObjects<T>() / CFArray.ArrayFromHandle<T>().
            // Fall back to untyped NSArray for these element types.
            if (mappedElem == "Action" ||
                mappedElem.StartsWith("Action<", StringComparison.Ordinal) ||
                mappedElem.StartsWith("Func<", StringComparison.Ordinal) ||
                mappedElem.EndsWith("[]", StringComparison.Ordinal))
                return null;
            return $"{mappedElem}[]";
        }

        // NSDictionary<K,V> / NSMutableDictionary<K,V> → NSDictionary<K,V>
        // Note: NSDictionary<TKey, TValue> requires INativeObject — can't use mapped types like string.
        // Only emit typed generics when both args are known NS/ObjC object types.
        if (typeRef.Name is "NSDictionary" or "NSMutableDictionary" && typeRef.GenericArgs.Count == 2)
        {
            var keyArg = typeRef.GenericArgs[0];
            var valArg = typeRef.GenericArgs[1];
            if (genericTypeParams != null &&
                (genericTypeParams.Contains(keyArg.Name) || genericTypeParams.Contains(valArg.Name)))
                return null;
            var mappedKey = MapNativeObjectGenericArg(keyArg, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap);
            var mappedVal = MapNativeObjectGenericArg(valArg, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap);
            if (mappedKey == null || mappedVal == null)
                return null; // Fall back to plain NSDictionary
            return $"NSDictionary<{mappedKey}, {mappedVal}>";
        }

        // NSSet<T> / NSMutableSet<T> / NSOrderedSet<T> / NSMutableOrderedSet<T> → NSSet<T>
        // Same INativeObject constraint as NSDictionary.
        if (typeRef.Name is "NSSet" or "NSMutableSet" or "NSOrderedSet" or "NSMutableOrderedSet"
            && typeRef.GenericArgs.Count == 1)
        {
            var elemArg = typeRef.GenericArgs[0];
            if (genericTypeParams != null && genericTypeParams.Contains(elemArg.Name))
                return null;
            var mappedElem = MapNativeObjectGenericArg(elemArg, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap);
            if (mappedElem == null)
                return null; // Fall back to plain NSSet
            return $"NSSet<{mappedElem}>";
        }

        return null;
    }

    /// <summary>
    /// Maps an ObjC generic arg for NSDictionary/NSSet, ensuring the result implements INativeObject.
    /// Returns null if the type can't be used as a generic arg (e.g., typedefs to NSString, primitives).
    /// Unlike <see cref="MapType"/>, this does NOT convert NSString→string or other INativeObject-incompatible mappings.
    /// </summary>
    private static string? MapNativeObjectGenericArg(ObjCTypeRef typeRef, string? declaringClassName, HashSet<string>? genericTypeParams, Dictionary<string, ObjCTypeRef>? typedefMap, Dictionary<string, ObjCTypeRef>? blockTypedefMap)
    {
        var name = typeRef.Name;

        // Resolve typedefs — if it's a typedef for NSString (e.g., SDWebImageContextOption),
        // use NSString instead of the typedef name (which won't exist as a C# type).
        if (typedefMap != null && typedefMap.TryGetValue(name, out var resolved))
        {
            // Follow the typedef chain to the underlying type
            var underlying = resolved;
            var visited = new HashSet<string> { name };
            while (typedefMap.TryGetValue(underlying.Name, out var deeper) && visited.Add(underlying.Name))
                underlying = deeper;
            name = underlying.Name;
        }

        // Known NS object types that implement INativeObject
        if (PointerTypeMappings.TryGetValue(name, out var mapped))
        {
            // string doesn't implement INativeObject — keep NSString
            if (mapped == "string")
                return "NSString";
            return mapped;
        }

        // If it's "id" (any object), use NSObject
        if (name == "id")
            return "NSObject";

        // Primitive types (int, bool, etc.) can't be used as NSDictionary generic args
        if (PrimitiveTypeMappings.ContainsKey(name))
            return null;

        // Unknown pointer types: module-local ObjC classes (e.g., GIDClaim) are emitted as
        // partial interfaces by bgen, which don't implement INativeObject at compile time.
        // Only use known Apple SDK types as generic args; for everything else, fall back to
        // the untyped container.
        return null;
    }
}
