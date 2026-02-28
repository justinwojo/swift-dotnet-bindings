// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

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
    /// Backward-compatible overload — wraps the generic-aware version.
    /// </summary>
    public static List<BridgeParameter>? AnalyzeInitParameters(MethodDecl constructor, BridgeContext? context = null)
    {
        return AnalyzeInitParameters(constructor, context, null, out _);
    }

    /// <summary>
    /// Analyzes init parameters with optional generic view support.
    /// When genericAnalysis is provided, ViewBuilder closure params and generic type params
    /// matching ConcreteTypeArgs are synthesized (skipped from bridge params, added to synthesizedArgs).
    /// </summary>
    public static List<BridgeParameter>? AnalyzeInitParameters(
        MethodDecl constructor, BridgeContext? context,
        GenericViewAnalysis? genericAnalysis,
        out List<SynthesizedInitArg>? synthesizedArgs)
    {
        synthesizedArgs = null;
        var parameters = new List<BridgeParameter>();

        // CSSignature[0] is the return type, skip it
        for (int i = 1; i < constructor.CSSignature.Count; i++)
        {
            var param = constructor.CSSignature[i];

            // Generic view support: check for synthesizable parameters
            if (genericAnalysis != null)
            {
                // ViewBuilder closure whose return type matches a ConcreteTypeArgs key
                if (IsViewBuilderClosureParam(param, genericAnalysis))
                {
                    synthesizedArgs ??= new List<SynthesizedInitArg>();
                    var closureReturnName = ((NamedTypeSpec)((ClosureTypeSpec)param.SwiftTypeSpec).ReturnType).Name;
                    var concreteType = genericAnalysis.ConcreteTypeArgs.GetValueOrDefault(closureReturnName, "EmptyView");
                    synthesizedArgs.Add(new SynthesizedInitArg(param.Name, $"{{ {concreteType}() }}"));
                    continue; // Skip from bridgeParams
                }

                // Direct generic type parameter matching a ConcreteTypeArgs key
                if (IsGenericTypeParam(param, genericAnalysis))
                {
                    synthesizedArgs ??= new List<SynthesizedInitArg>();
                    var typeParamName = param.SwiftTypeSpec is NamedTypeSpec ns ? ns.Name : "";
                    var concreteType = genericAnalysis.ConcreteTypeArgs.GetValueOrDefault(typeParamName, "EmptyView");
                    synthesizedArgs.Add(new SynthesizedInitArg(param.Name, $"{concreteType}()"));
                    continue; // Skip from bridgeParams
                }
            }

            var bridgeParam = MapParameterType(param, context);
            if (bridgeParam == null)
                return null; // Unsupported parameter — entire view falls back to template
            parameters.Add(bridgeParam);
        }

        return parameters;
    }

    /// <summary>
    /// Checks if a parameter is a ViewBuilder closure that returns a generic placeholder type.
    /// e.g., @ViewBuilder placeholder: () -> Placeholder where Placeholder is in ConcreteTypeArgs.
    /// </summary>
    private static bool IsViewBuilderClosureParam(ArgumentDecl param, GenericViewAnalysis genericAnalysis)
    {
        if (param.SwiftTypeSpec is not ClosureTypeSpec closureSpec)
            return false;

        // Check if the return type is a generic parameter in ConcreteTypeArgs
        var returnType = closureSpec.ReturnType;
        if (returnType is NamedTypeSpec namedReturn &&
            genericAnalysis.ConcreteTypeArgs.ContainsKey(namedReturn.Name))
            return true;

        return false;
    }

    /// <summary>
    /// Checks if a parameter is a generic type parameter matching a ConcreteTypeArgs key.
    /// </summary>
    private static bool IsGenericTypeParam(ArgumentDecl param, GenericViewAnalysis genericAnalysis)
    {
        if (!param.IsGeneric)
            return false;

        if (param.SwiftTypeSpec is NamedTypeSpec namedSpec &&
            genericAnalysis.ConcreteTypeArgs.ContainsKey(namedSpec.Name))
            return true;

        return false;
    }

    /// <summary>
    /// Maps a single Swift parameter to its bridge representation.
    /// Returns null if the parameter type is not supported.
    /// Internal visibility allows the async inference algorithm to reuse leaf checks.
    /// </summary>
    internal static BridgeParameter? MapParameterType(ArgumentDecl param, BridgeContext? context)
    {
        var typeSpec = param.SwiftTypeSpec;

        // Void closure: () -> () or () -> Void
        if (typeSpec is ClosureTypeSpec closureSpec)
        {
            return MapClosureType(param.Name, closureSpec, context);
        }

        // Named types: primitives, String, Optional<T>, enums (via TypeDatabase)
        if (typeSpec is NamedTypeSpec namedSpec)
        {
            return MapNamedType(param.Name, namedSpec, context);
        }

        // Everything else is unsupported
        return null;
    }

    private static BridgeParameter? MapClosureType(string paramName, ClosureTypeSpec closureSpec, BridgeContext? context = null)
    {
        // Async and throwing closures are unsupported
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

        // Typed closure: max 4 parameters
        if (hasArgs && closureSpec.ArgumentCount() > 4)
            return null;

        // Map each closure argument to a bridge-compatible type (primitives, String, classes)
        var closureArgs = new List<BridgeParameter>();
        int argIndex = 0;
        foreach (var arg in closureSpec.EachArgument())
        {
            if (arg is not NamedTypeSpec namedArg)
                return null;
            var mapped = MapPrimitiveOrString($"arg{argIndex}", namedArg);
            if (mapped == null && context?.TypeDatabase != null)
            {
                mapped = MapDatabaseType($"arg{argIndex}", namedArg, context.TypeDatabase);
                if (mapped != null && mapped.Kind != BridgeParameterKind.BoundType)
                    mapped = null; // Only classes via TypeDB in closures, not enums/structs
            }
            if (mapped == null)
                return null;
            closureArgs.Add(mapped);
            argIndex++;
        }

        // Map return type (primitives only — String/class returns deferred to 1B)
        BridgeParameter? closureReturn = null;
        if (hasReturn)
        {
            if (closureSpec.ReturnType is not NamedTypeSpec namedReturn)
                return null;
            var mapped = MapPrimitiveOrString("result", namedReturn);
            if (mapped == null || mapped.Kind == BridgeParameterKind.String)
                return null;
            closureReturn = mapped;
        }

        // Build @convention(c) signature: (ArgAbi1, ArgAbi2, ..., UnsafeMutableRawPointer?) -> ReturnAbi
        // String args produce TWO ABI parameters (ptr + len)
        var abiArgTypes = new List<string>();
        foreach (var a in closureArgs)
        {
            abiArgTypes.Add(a.SwiftAbiType);
            if (a.Kind == BridgeParameterKind.String)
                abiArgTypes.Add("Int"); // length companion
        }
        abiArgTypes.Add("UnsafeMutableRawPointer?");
        var abiReturnType = closureReturn?.SwiftAbiType ?? "Void";
        var swiftAbiType = $"(@convention(c) ({string.Join(", ", abiArgTypes)}) -> {abiReturnType})?";

        return new BridgeParameter(
            paramName,
            BridgeParameterKind.TypedClosure,
            SwiftAbiType: swiftAbiType,
            CSharpPInvokeType: "IntPtr",
            HasUserData: true,
            ClosureArguments: closureArgs,
            ClosureReturn: closureReturn);
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

    internal static BridgeParameter? MapPrimitiveOrString(string paramName, NamedTypeSpec namedSpec)
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
            // Use fully-qualified C# name for cross-module type safety
            var csharpName = record.CSharpTypeName.FullyQualifiedName;

            return new BridgeParameter(
                paramName,
                BridgeParameterKind.BoundEnum,
                SwiftAbiType: abiMapping.Value.SwiftType,
                CSharpPInvokeType: abiMapping.Value.CSharpType,
                BridgeTypeName: swiftSimpleName,
                CSharpTypeName: csharpName,
                IsSimpleEnum: record.Flags.HasFlag(TypeRecordFlags.SimpleEnum));
        }

        if (record.Kind == TypeRecordKind.Class)
        {
            // Class parameters cross the ABI as UnsafeMutableRawPointer.
            // C# passes IntPtr via SafeHandle.DangerousGetHandle().
            var dotIndex = namedSpec.Name.IndexOf('.');
            var swiftSimpleName = dotIndex >= 0 ? namedSpec.Name.Substring(dotIndex + 1) : namedSpec.Name;
            // Use fully-qualified C# name for cross-module type safety
            var csharpName = record.CSharpTypeName.FullyQualifiedName;

            return new BridgeParameter(
                paramName,
                BridgeParameterKind.BoundType,
                SwiftAbiType: "UnsafeMutableRawPointer",
                CSharpPInvokeType: "IntPtr",
                BridgeTypeName: swiftSimpleName,
                CSharpTypeName: csharpName);
        }

        if (record.Kind == TypeRecordKind.Struct)
        {
            var projection = MarshallingHelpers.IsTypeFrozen(record)
                ? (MarshallingHelpers.RequiresMemoryManagement(record)
                    ? StructProjectionKind.FrozenWithMemory
                    : StructProjectionKind.FrozenBlittable)
                : StructProjectionKind.NonFrozen;

            // Frozen blittable structs are C# value types (no SafeHandle) — pinning deferred
            if (projection == StructProjectionKind.FrozenBlittable)
                return null;

            var dotIndex = namedSpec.Name.IndexOf('.');
            var swiftSimpleName = dotIndex >= 0 ? namedSpec.Name.Substring(dotIndex + 1) : namedSpec.Name;
            var csharpName = record.CSharpTypeName.FullyQualifiedName;

            return new BridgeParameter(
                paramName, BridgeParameterKind.BoundStruct,
                SwiftAbiType: "UnsafeMutableRawPointer", CSharpPInvokeType: "IntPtr",
                BridgeTypeName: swiftSimpleName, CSharpTypeName: csharpName,
                StructProjection: projection);
        }

        // Other TypeDatabase types not yet supported
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

        // Optional<Closure> — closures are already nullable in the bridge ABI
        if (innerTypeSpec is ClosureTypeSpec innerClosureSpec)
        {
            return MapClosureType(paramName, innerClosureSpec, context);
        }

        // Inner type must be a NamedTypeSpec (not tuple, etc.)
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

        // Optional<BoundStruct> for struct types — nullable pointer, same as BoundType
        if (innerParam.Kind == BridgeParameterKind.BoundStruct)
        {
            return new BridgeParameter(
                paramName,
                BridgeParameterKind.OptionalWrapped,
                SwiftAbiType: "UnsafeMutableRawPointer?",
                CSharpPInvokeType: "IntPtr",
                InnerParameter: innerParam);
        }

        // Optional<String> — same ABI as String (ptr+len), with ptr==nil meaning nil
        if (innerParam.Kind == BridgeParameterKind.String)
        {
            return new BridgeParameter(
                paramName,
                BridgeParameterKind.OptionalWrapped,
                SwiftAbiType: "UnsafePointer<UInt8>?",
                CSharpPInvokeType: "IntPtr",
                HasLength: true,
                InnerParameter: innerParam);
        }

        // Optional<Primitive> and Optional<BoundEnum> use hasValue flag + raw value
        if (innerParam.Kind != BridgeParameterKind.Primitive && innerParam.Kind != BridgeParameterKind.BoundEnum)
            return null;

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
    TypedClosure,
    BoundEnum,
    BoundType,
    BoundStruct,
    OptionalWrapped,
}

/// <summary>
/// Projection strategy for struct bridge parameters.
/// </summary>
public enum StructProjectionKind
{
    /// <summary>Non-frozen struct — C# class with SafeHandle (opaque payload).</summary>
    NonFrozen,
    /// <summary>Frozen struct with no reference-counted fields — C# value type (no SafeHandle).</summary>
    FrozenBlittable,
    /// <summary>Frozen struct with reference-counted fields — C# class with SafeHandle.</summary>
    FrozenWithMemory,
}

/// <summary>
/// Context for bridge parameter analysis. Holds shared services needed by the analyzer.
/// </summary>
public record BridgeContext(ITypeDatabase? TypeDatabase = null, ModuleDecl? ModuleDecl = null, BridgeHintsFile? Hints = null, ILogger? Logger = null);

/// <summary>
/// A synthesized init argument that the bridge emitter injects into the Swift init call.
/// Used for generic view placeholder parameters (e.g., @ViewBuilder closures → { EmptyView() }).
/// </summary>
public record SynthesizedInitArg(string ParamName, string SwiftExpression);

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
    BridgeParameter? InnerParameter = null,
    List<BridgeParameter>? ClosureArguments = null,
    BridgeParameter? ClosureReturn = null,
    bool IsSimpleEnum = false,
    StructProjectionKind? StructProjection = null);
