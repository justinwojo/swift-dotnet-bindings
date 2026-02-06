// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Init parameter analysis for SwiftUI bridge generation.
/// Maps Swift parameter types to C ABI types for bridge code generation.
/// </summary>
public static partial class SwiftUIBridgeEmitter
{
    /// <summary>
    /// Analyzes all init parameters and returns bridge parameter mappings.
    /// Returns null if any parameter is unsupported (entire View falls back to template).
    /// </summary>
    public static List<BridgeParameter>? AnalyzeInitParameters(MethodDecl constructor, BridgeContext? context = null)
    {
        var parameters = new List<BridgeParameter>();

        // CSSignature[0] is the return type, skip it
        for (int i = 1; i < constructor.CSSignature.Count; i++)
        {
            var param = constructor.CSSignature[i];
            var bridgeParam = MapParameterType(param, context);
            if (bridgeParam == null)
                return null; // Unsupported parameter — entire view falls back to template
            parameters.Add(bridgeParam);
        }

        return parameters;
    }

    /// <summary>
    /// Maps a single Swift parameter to its bridge representation.
    /// Returns null if the parameter type is not supported.
    /// </summary>
    private static BridgeParameter? MapParameterType(ArgumentDecl param, BridgeContext? context)
    {
        var typeSpec = param.SwiftTypeSpec;

        // Void closure: () -> () or () -> Void
        if (typeSpec is ClosureTypeSpec closureSpec)
        {
            return MapClosureType(param.Name, closureSpec);
        }

        // Named types: primitives, String, Optional<T>, enums (via TypeDatabase)
        if (typeSpec is NamedTypeSpec namedSpec)
        {
            return MapNamedType(param.Name, namedSpec, context);
        }

        // Everything else is unsupported
        return null;
    }

    private static BridgeParameter? MapClosureType(string paramName, ClosureTypeSpec closureSpec)
    {
        // v1: Only () -> Void closures supported
        if (closureSpec.IsAsync || closureSpec.Throws)
            return null;

        var hasArgs = closureSpec.HasArguments();
        var hasReturn = closureSpec.HasReturn();

        if (!hasArgs && !hasReturn)
        {
            // () -> Void — maps to callback function pointer + userData
            return new BridgeParameter(
                paramName,
                BridgeParameterKind.VoidClosure,
                SwiftAbiType: "(@convention(c) (UnsafeMutableRawPointer?) -> Void)?",
                CSharpPInvokeType: "IntPtr",
                HasUserData: true);
        }

        // Closures with parameters or return types are unsupported in v1
        return null;
    }

    private static BridgeParameter? MapNamedType(string paramName, NamedTypeSpec namedSpec, BridgeContext? context)
    {
        // Check for Optional<T> before primitives (Optional is a NamedTypeSpec with generics)
        if (namedSpec.Name == "Swift.Optional" && namedSpec.GenericParameters.Count == 1)
        {
            return MapOptionalType(paramName, namedSpec, context);
        }

        // Existing primitives and String
        var primitive = MapPrimitiveOrString(paramName, namedSpec);
        if (primitive != null)
            return primitive;

        // TypeDatabase lookup for bound enums
        if (context?.TypeDatabase != null)
        {
            return MapDatabaseType(paramName, namedSpec, context.TypeDatabase);
        }

        // Unsupported type
        return null;
    }

    private static BridgeParameter? MapPrimitiveOrString(string paramName, NamedTypeSpec namedSpec)
    {
        var fullName = namedSpec.ToString();

        return fullName switch
        {
            "Swift.Int" => new BridgeParameter(paramName, BridgeParameterKind.Primitive,
                "Int", "nint"),
            "Swift.Int32" => new BridgeParameter(paramName, BridgeParameterKind.Primitive,
                "Int32", "int"),
            "Swift.Int64" => new BridgeParameter(paramName, BridgeParameterKind.Primitive,
                "Int64", "long"),
            "Swift.Bool" => new BridgeParameter(paramName, BridgeParameterKind.Primitive,
                "Int32", "int", SwiftConversion: "!= 0", CSharpConversion: "? 1 : 0"),
            "Swift.Double" => new BridgeParameter(paramName, BridgeParameterKind.Primitive,
                "Double", "double"),
            "Swift.Float" => new BridgeParameter(paramName, BridgeParameterKind.Primitive,
                "Float", "float"),
            "Swift.String" => new BridgeParameter(paramName, BridgeParameterKind.String,
                "UnsafePointer<UInt8>?", "IntPtr",
                HasLength: true),
            _ => null,
        };
    }

    /// <summary>
    /// Looks up a type in the TypeDatabase. Currently handles enums (BoundEnum).
    /// </summary>
    private static BridgeParameter? MapDatabaseType(string paramName, NamedTypeSpec namedSpec, ITypeDatabase typeDatabase)
    {
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedSpec.Name);

        if (!typeDatabase.TryGetTypeRecord(swiftTypeName, out var record))
            return null;

        if (record.Kind == TypeRecordKind.Enum)
        {
            // Only support integer raw-representable enums.
            // Non-RawRepresentable enums and String raw-value enums fall back to template.
            var abiMapping = MapEnumRawValueType(record.RawValueTypeName);
            if (abiMapping == null)
                return null;

            // Strip module prefix for Swift emission (module is already imported)
            var dotIndex = namedSpec.Name.IndexOf('.');
            var swiftSimpleName = dotIndex >= 0 ? namedSpec.Name.Substring(dotIndex + 1) : namedSpec.Name;
            var csharpName = record.CSharpTypeName.Name;

            return new BridgeParameter(
                paramName,
                BridgeParameterKind.BoundEnum,
                SwiftAbiType: abiMapping.Value.SwiftType,
                CSharpPInvokeType: abiMapping.Value.CSharpType,
                BridgeTypeName: swiftSimpleName,
                CSharpTypeName: csharpName);
        }

        if (record.Kind == TypeRecordKind.Class)
        {
            // Class parameters cross the ABI as UnsafeMutableRawPointer.
            // C# passes IntPtr via SafeHandle.DangerousGetHandle().
            var dotIndex = namedSpec.Name.IndexOf('.');
            var swiftSimpleName = dotIndex >= 0 ? namedSpec.Name.Substring(dotIndex + 1) : namedSpec.Name;
            var csharpName = record.CSharpTypeName.Name;

            return new BridgeParameter(
                paramName,
                BridgeParameterKind.BoundType,
                SwiftAbiType: "UnsafeMutableRawPointer",
                CSharpPInvokeType: "IntPtr",
                BridgeTypeName: swiftSimpleName,
                CSharpTypeName: csharpName);
        }

        // Other TypeDatabase types (structs) not yet supported — deferred to v2.1
        return null;
    }

    /// <summary>
    /// Maps a Swift enum raw value type name to its ABI types.
    /// Returns null for non-integer or unsupported raw value types.
    /// </summary>
    private static (string SwiftType, string CSharpType)? MapEnumRawValueType(string? rawValueTypeName)
    {
        return rawValueTypeName switch
        {
            "Int" => ("Int", "nint"),
            "Int8" => ("Int8", "sbyte"),
            "Int16" => ("Int16", "short"),
            "Int32" => ("Int32", "int"),
            "Int64" => ("Int64", "long"),
            "UInt" => ("UInt", "nuint"),
            "UInt8" => ("UInt8", "byte"),
            "UInt16" => ("UInt16", "ushort"),
            "UInt32" => ("UInt32", "uint"),
            "UInt64" => ("UInt64", "ulong"),
            _ => null, // String, non-RawRepresentable, or unknown → template fallback
        };
    }

    /// <summary>
    /// Maps Optional&lt;T&gt; where T is a Primitive or BoundEnum.
    /// Uses a hasValue flag + raw value pair across the ABI.
    /// </summary>
    private static BridgeParameter? MapOptionalType(string paramName, NamedTypeSpec namedSpec, BridgeContext? context)
    {
        var innerTypeSpec = namedSpec.GenericParameters[0];

        // Inner type must be a NamedTypeSpec (not closure, tuple, etc.)
        if (innerTypeSpec is not NamedTypeSpec innerNamedSpec)
            return null;

        // Recursively map the inner type
        var innerParam = MapNamedType(paramName, innerNamedSpec, context);
        if (innerParam == null)
            return null;

        // Optional<BoundType> for reference types — nullable pointer, no hasValue flag needed
        if (innerParam.Kind == BridgeParameterKind.BoundType)
        {
            return new BridgeParameter(
                paramName,
                BridgeParameterKind.OptionalWrapped,
                SwiftAbiType: "UnsafeMutableRawPointer?",   // nullable pointer
                CSharpPInvokeType: "IntPtr",                // IntPtr.Zero = nil
                InnerParameter: innerParam);
        }

        // Optional<Primitive> and Optional<BoundEnum> use hasValue flag + raw value
        if (innerParam.Kind != BridgeParameterKind.Primitive && innerParam.Kind != BridgeParameterKind.BoundEnum)
            return null;

        // Optional<String> not supported in Phase 1A (reference type semantics differ)
        // This is already blocked by the check above since String has its own Kind

        return new BridgeParameter(
            paramName,
            BridgeParameterKind.OptionalWrapped,
            SwiftAbiType: "Int32",          // hasValue flag ABI type
            CSharpPInvokeType: "int",       // hasValue flag P/Invoke type
            InnerParameter: innerParam);
    }
}

/// <summary>
/// Kind of bridge parameter.
/// </summary>
public enum BridgeParameterKind
{
    Primitive,
    String,
    VoidClosure,
    BoundEnum,
    BoundType,
    OptionalWrapped,
}

/// <summary>
/// Context for bridge parameter analysis. Holds shared services needed by the analyzer.
/// </summary>
public record BridgeContext(ITypeDatabase? TypeDatabase = null);

/// <summary>
/// Mapping of a Swift init parameter to its C ABI representation for bridge code.
/// </summary>
public record BridgeParameter(
    string Name,
    BridgeParameterKind Kind,
    string SwiftAbiType,
    string CSharpPInvokeType,
    bool HasUserData = false,
    bool HasLength = false,
    string? SwiftConversion = null,
    string? CSharpConversion = null,
    string? BridgeTypeName = null,
    string? CSharpTypeName = null,
    BridgeParameter? InnerParameter = null);
