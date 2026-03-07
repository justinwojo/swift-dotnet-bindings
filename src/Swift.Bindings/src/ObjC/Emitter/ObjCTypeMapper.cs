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
        if (typeRef.Name == "id" && typeRef.ProtocolQualification != null)
        {
            // Multi-protocol: id<Proto1, Proto2> — use the first bindable protocol.
            // Filter out NSObject (implicit in ObjC) and NSFastEnumeration (no .NET binding).
            var protocols = typeRef.ProtocolQualification.Split(',')
                .Select(p => p.Trim())
                .Where(p => p != "NSObject" && p != "NSFastEnumeration")
                .ToList();
            if (protocols.Count == 0)
                return "NSObject";
            return $"I{MapProtocolName(protocols[0])}";
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
                    ProtocolQualification = resolved.ProtocolQualification,
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

        // 11. ObjC-to-.NET naming convention fallback
        // .NET MAUI lowercases URL→Url and HTTP→Http in NS-prefixed type names.
        // Apply anywhere in the name (NSCachedURLResponse → NSCachedUrlResponse).
        var name = typeRef.Name;
        if (name.StartsWith("NS", StringComparison.Ordinal) &&
            (name.Contains("URL", StringComparison.Ordinal) || name.Contains("HTTP", StringComparison.Ordinal)))
        {
            return name
                .Replace("HTTP", "Http", StringComparison.Ordinal)
                .Replace("URL", "Url", StringComparison.Ordinal);
        }

        // 12. Passthrough / fallback
        return name;
    }

    /// <summary>
    /// Maps an ObjC class name to its .NET MAUI binding name.
    /// Like MapProtocolName but for class names used in BaseType attributes.
    /// Applies URL→Url and HTTP→Http convention without pointer type semantics
    /// (NSString stays NSString, not string).
    /// </summary>
    public static string MapClassName(string name)
    {
        // Check explicit pointer mappings for non-string types
        // (NSString → string is wrong for BaseType, keep NSString)
        if (PointerTypeMappings.TryGetValue(name, out var mapped) && mapped != "string" && mapped != "bool")
            return mapped;

        // Apply URL/HTTP convention
        if (name.StartsWith("NS", StringComparison.Ordinal) &&
            (name.Contains("URL", StringComparison.Ordinal) || name.Contains("HTTP", StringComparison.Ordinal)))
        {
            return name
                .Replace("HTTP", "Http", StringComparison.Ordinal)
                .Replace("URL", "Url", StringComparison.Ordinal);
        }

        return name;
    }

    /// <summary>
    /// Maps an ObjC protocol name to its .NET MAUI binding convention name.
    /// E.g., NSURLSessionTaskDelegate → NSUrlSessionTaskDelegate.
    /// </summary>
    public static string MapProtocolName(string name)
    {
        if (name.StartsWith("NS", StringComparison.Ordinal) &&
            (name.Contains("URL", StringComparison.Ordinal) || name.Contains("HTTP", StringComparison.Ordinal)))
        {
            return name
                .Replace("HTTP", "Http", StringComparison.Ordinal)
                .Replace("URL", "Url", StringComparison.Ordinal);
        }
        return name;
    }

    public static Dictionary<string, ObjCTypeRef> BuildBlockTypedefMap(ObjCModule module) =>
        module.Typedefs
            .Where(t => t.UnderlyingType.IsBlock)
            .ToDictionary(t => t.Name, t => t.UnderlyingType);

    public static Dictionary<string, ObjCTypeRef> BuildResolvedTypedefMap(ObjCModule module)
    {
        var raw = new Dictionary<string, ObjCTypeRef>();
        var structNames = new HashSet<string>(module.Structs.Select(s => s.Name));

        foreach (var t in module.Typedefs)
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

    public static bool IsNullableAttribute(ObjCTypeRef typeRef) =>
        typeRef.Nullability == ObjCNullability.Nullable;

    public static bool IsNSErrorOutParameter(ObjCTypeRef typeRef) =>
        typeRef.Name == "NSError"
        && typeRef.IsPointer
        && typeRef.PointeeType is { Name: "NSError", IsPointer: true };

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
}
