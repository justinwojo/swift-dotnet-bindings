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
    public static List<BridgeParameter>? AnalyzeInitParameters(MethodDecl constructor)
    {
        var parameters = new List<BridgeParameter>();

        // CSSignature[0] is the return type, skip it
        for (int i = 1; i < constructor.CSSignature.Count; i++)
        {
            var param = constructor.CSSignature[i];
            var bridgeParam = MapParameterType(param);
            if (bridgeParam == null)
                return null; // Unsupported parameter — entire view falls back to template
            parameters.Add(bridgeParam);
        }

        return parameters;
    }

    /// <summary>
    /// Maps a single Swift parameter to its bridge representation.
    /// Returns null if the parameter type is not supported in v1.
    /// </summary>
    private static BridgeParameter? MapParameterType(ArgumentDecl param)
    {
        var typeSpec = param.SwiftTypeSpec;

        // Void closure: () -> () or () -> Void
        if (typeSpec is ClosureTypeSpec closureSpec)
        {
            return MapClosureType(param.Name, closureSpec);
        }

        // Named types: primitives, String
        if (typeSpec is NamedTypeSpec namedSpec)
        {
            return MapNamedType(param.Name, namedSpec);
        }

        // Everything else is unsupported in v1
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

    private static BridgeParameter? MapNamedType(string paramName, NamedTypeSpec namedSpec)
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
            _ => null, // Unsupported type
        };
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
}

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
    string? CSharpConversion = null);
