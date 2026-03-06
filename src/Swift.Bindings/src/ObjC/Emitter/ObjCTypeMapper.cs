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
        ["uint64_t"] = "ulong",
        ["va_list"] = "IntPtr",
    };

    public static string MapType(ObjCTypeRef typeRef, string? declaringClassName = null, HashSet<string>? genericTypeParams = null)
    {
        // 1. Block types
        if (typeRef.IsBlock)
            return MapBlockType(typeRef, genericTypeParams);

        // 2. instancetype
        if (typeRef.Name == "instancetype")
            return declaringClassName ?? "NSObject";

        // 3. Protocol-qualified id (id<Proto>)
        if (typeRef.Name == "id" && typeRef.ProtocolQualification != null)
            return $"I{typeRef.ProtocolQualification}";

        // 4. Known pointer types
        if (typeRef.IsPointer)
        {
            if (PointerTypeMappings.TryGetValue(typeRef.Name, out var mapped))
                return mapped;
            return typeRef.Name;
        }

        // 5. Special non-pointer types
        if (typeRef.Name == "id")
            return "NSObject";
        if (typeRef.Name == "SEL")
            return "Selector";
        if (typeRef.Name == "Class")
            return "Class";

        // 6. Primitive types
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

        // 9. Passthrough / fallback
        return typeRef.Name;
    }

    public static bool IsNullableAttribute(ObjCTypeRef typeRef) =>
        typeRef.Nullability == ObjCNullability.Nullable;

    public static bool IsNSErrorOutParameter(ObjCTypeRef typeRef) =>
        typeRef.Name == "NSError"
        && typeRef.IsPointer
        && typeRef.PointeeType is { Name: "NSError", IsPointer: true };

    static string MapBlockType(ObjCTypeRef typeRef, HashSet<string>? genericTypeParams = null)
    {
        var returnType = typeRef.BlockReturnType != null
            ? MapType(typeRef.BlockReturnType, genericTypeParams: genericTypeParams)
            : "void";

        var paramTypes = typeRef.BlockParams.Select(p => MapType(p, genericTypeParams: genericTypeParams)).ToList();

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
