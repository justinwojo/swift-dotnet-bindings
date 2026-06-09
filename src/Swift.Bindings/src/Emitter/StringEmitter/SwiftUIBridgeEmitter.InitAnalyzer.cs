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
                // (only for View-resolved params; non-View closures fall through to normal bridging)
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
                    var typeParamName = param.SwiftTypeSpec is NamedTypeSpec ns ? ns.Name : "";

                    // Non-View resolved params: substitute the concrete type and bridge normally
                    // (e.g., T: Hashable → String, the C# consumer provides the value)
                    if (genericAnalysis.NonViewResolvedParams?.Contains(typeParamName) == true &&
                        genericAnalysis.ConcreteTypeArgs.TryGetValue(typeParamName, out var resolvedTypeName))
                    {
                        var substitutedParam = SubstituteGenericParam(param, resolvedTypeName);
                        var resolved = MapParameterType(substitutedParam, context);
                        if (resolved == null)
                            return null; // Substituted type not bridgeable → template fallback
                        parameters.Add(resolved);
                        continue;
                    }

                    // View-resolved: synthesize (e.g., EmptyView())
                    synthesizedArgs ??= new List<SynthesizedInitArg>();
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
    /// Only matches View-resolved params; non-View resolved params (e.g., T: Hashable → String)
    /// are not synthesized as closures — they fall through to normal bridge parameter analysis.
    /// </summary>
    private static bool IsViewBuilderClosureParam(ArgumentDecl param, GenericViewAnalysis genericAnalysis)
    {
        if (param.SwiftTypeSpec is not ClosureTypeSpec closureSpec)
            return false;

        // Check if the return type is a generic parameter in ConcreteTypeArgs
        var returnType = closureSpec.ReturnType;
        if (returnType is NamedTypeSpec namedReturn &&
            genericAnalysis.ConcreteTypeArgs.ContainsKey(namedReturn.Name))
        {
            // Non-View resolved params should not be synthesized as ViewBuilder closures —
            // the closure return type is data, not a placeholder view
            if (genericAnalysis.NonViewResolvedParams?.Contains(namedReturn.Name) == true)
                return false;
            return true;
        }

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
    /// Creates a substituted ArgumentDecl with the generic type replaced by a concrete type.
    /// Used for non-View resolved generic params (e.g., T: Hashable → String) so they can
    /// be bridged as normal parameters instead of being synthesized.
    /// </summary>
    private static ArgumentDecl SubstituteGenericParam(ArgumentDecl param, string concreteType)
    {
        var qualifiedName = SwiftUIBridgeEmitter.ConcreteTypeQualifiedNames
            .GetValueOrDefault(concreteType, $"Swift.{concreteType}");
        return new ArgumentDecl
        {
            Name = param.Name,
            PrivateName = param.PrivateName,
            IsInOut = param.IsInOut,
            Ownership = param.Ownership, // faithful clone — keep ownership across type substitution
            IsGeneric = false, // No longer generic after substitution
            SwiftTypeSpec = new NamedTypeSpec(qualifiedName),
            ParentDecl = param.ParentDecl,
            ModuleDecl = param.ModuleDecl,
        };
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

        // Result<T,E> closure: (Result<Success, Failure>) -> Void
        // Decomposed into two callbacks: onSuccess(T) and onError(E)
        if (hasArgs && !hasReturn && closureSpec.ArgumentCount() == 1)
        {
            var singleArg = closureSpec.EachArgument().First();
            if (singleArg is NamedTypeSpec resultSpec &&
                resultSpec.Name == "Swift.Result" &&
                resultSpec.GenericParameters.Count == 2)
            {
                var resultParam = MapResultClosureType(paramName, resultSpec, context);
                if (resultParam != null)
                    return resultParam;
                // Result inner types are not bridge-supported. Do NOT fall through to
                // typed-closure handling: that path maps the Swift.Result arg through the
                // TypeDatabase to the generic Swift.SwiftResult *with its two generic
                // arguments stripped*, emitting an uncompilable Action<Swift.SwiftResult>
                // (CS0305 — SwiftResult<,> requires two type arguments). Treat the whole
                // closure as unsupported so the View degrades to template emission instead.
                return null;
            }
        }

        // Typed closure: max 4 parameters
        if (hasArgs && closureSpec.ArgumentCount() > 4)
            return null;

        // Map each closure argument to a bridge-compatible type (primitives, String, classes, opaque enums/structs)
        var closureArgs = new List<BridgeParameter>();
        int argIndex = 0;
        foreach (var arg in closureSpec.EachArgument())
        {
            if (arg is not NamedTypeSpec namedArg)
                return null;
            var mapped = MapPrimitiveOrString($"arg{argIndex}", namedArg);
            if (mapped == null && context?.TypeDatabase != null)
            {
                mapped = MapDatabaseType($"arg{argIndex}", namedArg, context);
                // Classes cross ABI as Unmanaged pointer; BoundStruct (non-frozen enums/structs)
                // cross ABI as allocated buffer pointer — both are IntPtr-compatible for @convention(c).
                if (mapped != null && mapped.Kind is not BridgeParameterKind.BoundType
                                                  and not BridgeParameterKind.BoundStruct)
                    mapped = null;
            }
            if (mapped == null)
                return null;
            closureArgs.Add(mapped);
            argIndex++;
        }

        // Map return type (primitives, String, and class types)
        BridgeParameter? closureReturn = null;
        if (hasReturn)
        {
            if (closureSpec.ReturnType is not NamedTypeSpec namedReturn)
                return null;
            var mapped = MapPrimitiveOrString("result", namedReturn);
            if (mapped == null && context?.TypeDatabase != null)
            {
                mapped = MapDatabaseType("result", namedReturn, context);
                if (mapped != null && mapped.Kind != BridgeParameterKind.BoundType)
                    mapped = null; // Only classes for closure returns, not enums/structs
            }
            if (mapped == null)
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
        // String-returning closures need a return-length out-parameter
        if (closureReturn?.Kind == BridgeParameterKind.String)
            abiArgTypes.Add("UnsafeMutablePointer<Int>");
        abiArgTypes.Add("UnsafeMutableRawPointer?");
        var abiReturnType = closureReturn?.SwiftAbiType ?? "Void";
        // Class return from closure needs nullable pointer to handle nil returns
        if (closureReturn?.Kind == BridgeParameterKind.BoundType)
            abiReturnType += "?";
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

    /// <summary>
    /// Maps a (Result&lt;Success, Failure&gt;) -> Void closure to a ResultClosure bridge parameter.
    /// Decomposes the Result into two separate callbacks: onSuccess(T) and onError(E).
    /// T and E must individually resolve to bridge-supported types.
    /// </summary>
    private static BridgeParameter? MapResultClosureType(string paramName, NamedTypeSpec resultSpec, BridgeContext? context)
    {
        var successTypeSpec = resultSpec.GenericParameters[0];
        var errorTypeSpec = resultSpec.GenericParameters[1];

        // Map success type — must be a NamedTypeSpec resolvable to Primitive, String, BoundType, or BoundStruct
        BridgeParameter? successParam = null;
        if (successTypeSpec is NamedTypeSpec successNamed)
        {
            successParam = MapPrimitiveOrString("success", successNamed);
            if (successParam == null && context?.TypeDatabase != null)
            {
                successParam = MapDatabaseType("success", successNamed, context);
                if (successParam != null && successParam.Kind is not BridgeParameterKind.BoundType
                                                              and not BridgeParameterKind.BoundStruct)
                    successParam = null;
            }
        }
        if (successParam == null) return null;

        // Map error type — same constraints as success
        BridgeParameter? errorParam = null;
        if (errorTypeSpec is NamedTypeSpec errorNamed)
        {
            errorParam = MapPrimitiveOrString("error", errorNamed);
            if (errorParam == null && context?.TypeDatabase != null)
            {
                errorParam = MapDatabaseType("error", errorNamed, context);
                if (errorParam != null && errorParam.Kind is not BridgeParameterKind.BoundType
                                                            and not BridgeParameterKind.BoundStruct)
                    errorParam = null;
            }
        }
        if (errorParam == null) return null;

        return new BridgeParameter(
            paramName,
            BridgeParameterKind.ResultClosure,
            SwiftAbiType: "ResultClosure", // Not used directly — emitter handles 4-param expansion
            CSharpPInvokeType: "IntPtr",
            HasUserData: true,
            ResultSuccessParam: successParam,
            ResultErrorParam: errorParam);
    }

    private static BridgeParameter? MapNamedType(string paramName, NamedTypeSpec namedSpec, BridgeContext? context)
    {
        // Check for Optional<T> before primitives (Optional is a NamedTypeSpec with generics)
        if (namedSpec.Name == "Swift.Optional" && namedSpec.GenericParameters.Count == 1)
        {
            return MapOptionalType(paramName, namedSpec, context);
        }

        // Binding<T>: unwrap to inner type, bridge as normal value with Binding projection in Wrapper.
        // Only intercept when the inner type IS supported; otherwise fall through to MapDatabaseType
        // so Binding<UnsupportedType> still gets the generic BoundType treatment it had before.
        if (namedSpec.Name is "SwiftUI.Binding" or "SwiftUICore.Binding" && namedSpec.GenericParameters.Count == 1)
        {
            // Return null for unsupported inner types — MapDatabaseType strips generics
            // from Binding, producing broken bare "Binding" in the Swift output.
            return MapBindingType(paramName, namedSpec, context);
        }

        // Array<T>: bridge as pointer + count across ABI.
        // When the element type is unsupported, return null so the entire view falls back
        // to template emission. Falling through to MapDatabaseType produces a broken
        // bare "Array" / "Swift.SwiftArray" (no element type) in the generated bridge.
        if (namedSpec.Name == "Swift.Array" && namedSpec.GenericParameters.Count == 1)
        {
            return MapArrayType(paramName, namedSpec, context);
        }

        // SwiftUI.Image: bridge as String (SF Symbol name), construct Image in wrapper
        if (namedSpec.Name is "SwiftUI.Image" or "SwiftUICore.Image")
        {
            return MapSwiftUIImageType(paramName);
        }

        // Existing primitives and String
        var primitive = MapPrimitiveOrString(paramName, namedSpec);
        if (primitive != null)
            return primitive;

        // TypeDatabase lookup for bound enums
        if (context?.TypeDatabase != null)
        {
            return MapDatabaseType(paramName, namedSpec, context);
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
    private static BridgeParameter? MapDatabaseType(string paramName, NamedTypeSpec namedSpec, BridgeContext? context)
    {
        if (context?.TypeDatabase == null)
            return null;

        var typeDatabase = context.TypeDatabase;
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedSpec.Name);

        if (!typeDatabase.TryGetTypeRecord(swiftTypeName, out var record))
            return null;

        // Guard: nested types whose parent is a suppressed SwiftUI View don't exist
        // in the main C# bindings. Referencing them causes CS0234.
        if (IsNestedTypeUnderSwiftUIView(swiftTypeName, context.ModuleDecl))
            return null;

        if (record.Kind == TypeRecordKind.Enum)
        {
            // Integer raw-representable enums: bridge as BoundEnum (raw value ABI).
            var abiMapping = MapEnumRawValueType(record.RawValueTypeName);
            if (abiMapping != null)
            {
                // Strip module prefix for Swift emission (module is already imported).
                // Use swiftTypeName (parsed on line 422) to preserve nested type dots
                // (e.g., "PaymentSheet.PaymentButton" not just "PaymentButton").
                var swiftSimpleName = swiftTypeName.ModuleQualifiedName.Substring(swiftTypeName.Module.Length + 1);
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

            // Non-raw-value enums (associated values, no RawRepresentable conformance):
            // Bridge as opaque pointer (same ABI as non-frozen structs) when the C# binding
            // is a class with SafeHandle (requiresMemoryManagement).
            if (MarshallingHelpers.RequiresMemoryManagement(record))
            {
                var swiftSimpleName = swiftTypeName.ModuleQualifiedName.Substring(swiftTypeName.Module.Length + 1);
                var csharpName = record.CSharpTypeName.FullyQualifiedName;

                return new BridgeParameter(
                    paramName, BridgeParameterKind.BoundStruct,
                    SwiftAbiType: "UnsafeMutableRawPointer", CSharpPInvokeType: "IntPtr",
                    BridgeTypeName: swiftSimpleName, CSharpTypeName: csharpName,
                    StructProjection: StructProjectionKind.NonFrozen);
            }

            // Enums without raw values and without memory management are unsupported
            return null;
        }

        if (record.Kind == TypeRecordKind.Class)
        {
            // Class parameters cross the ABI as UnsafeMutableRawPointer.
            // C# passes IntPtr via SafeHandle.DangerousGetHandle() (Swift classes)
            // or .Handle (ObjC-bridgeable classes like AVCaptureDevice).
            var swiftSimpleName = swiftTypeName.ModuleQualifiedName.Substring(swiftTypeName.Module.Length + 1);
            // Use fully-qualified C# name for cross-module type safety
            var csharpName = record.CSharpTypeName.FullyQualifiedName;
            var isObjCBridgeable = MarshallingHelpers.IsObjCBridgeable(record);

            return new BridgeParameter(
                paramName,
                BridgeParameterKind.BoundType,
                SwiftAbiType: "UnsafeMutableRawPointer",
                CSharpPInvokeType: "IntPtr",
                BridgeTypeName: swiftSimpleName,
                CSharpTypeName: csharpName,
                IsObjCBridgeable: isObjCBridgeable);
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

            var swiftSimpleName = swiftTypeName.ModuleQualifiedName.Substring(swiftTypeName.Module.Length + 1);
            // ObjC-bridgeable structs (e.g., URL → NSUrl): use the native type name
            var isObjCBridgeable = MarshallingHelpers.IsObjCBridgeable(record);
            var csharpName = record.NativeTypeName?.FullyQualifiedName ?? record.CSharpTypeName.FullyQualifiedName;

            return new BridgeParameter(
                paramName, BridgeParameterKind.BoundStruct,
                SwiftAbiType: "UnsafeMutableRawPointer", CSharpPInvokeType: "IntPtr",
                BridgeTypeName: swiftSimpleName, CSharpTypeName: csharpName,
                StructProjection: projection,
                IsObjCBridgeable: isObjCBridgeable);
        }

        // Other TypeDatabase types not yet supported
        return null;
    }

    /// <summary>
    /// Returns true if the type is nested under a SwiftUI View type.
    /// Such nested types are suppressed from the main C# bindings because their
    /// parent type is skipped during emission.
    /// </summary>
    private static bool IsNestedTypeUnderSwiftUIView(SwiftTypeName typeName, ModuleDecl? moduleDecl)
    {
        if (moduleDecl == null)
            return false;

        // Nested types have 3+ parts: "Module.Parent.Nested"
        var parts = typeName.ModuleQualifiedName.Split('.');
        if (parts.Length < 3)
            return false;

        var parentName = parts[1];
        foreach (var typeDecl in moduleDecl.Types)
        {
            if (typeDecl.Name == parentName && SwiftUIViewDetector.IsSwiftUIView(typeDecl))
                return true;
        }

        return false;
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
    /// Analyzes a View type's methods for self-returning modifiers.
    /// Returns a list of BridgeModifier records for methods that pass all gates.
    /// Overloaded method names (2+ methods with same base name) are skipped entirely.
    /// </summary>
    public static List<BridgeModifier>? AnalyzeModifiers(TypeDecl viewType, string moduleName, BridgeContext? context = null)
    {
        var candidates = new List<BridgeModifier>();

        foreach (var method in viewType.Methods)
        {
            // Must be a self-returning instance method
            if (!MethodEnvironment.IsSelfReturningMethod(method))
                continue;

            // Skip throwing, mutating, and methods with own generic params (beyond parent)
            if (method.Throws || method.IsMutating)
                continue;
            if (method.GenericParameters.Count > (viewType.GenericParameters?.Count ?? 0))
                continue;

            // Count non-return params (CSSignature[0] is return type)
            var paramCount = method.CSSignature.Count - 1;

            if (paramCount == 0)
            {
                // Parameterless bool toggle
                candidates.Add(new BridgeModifier(
                    method.Name,
                    char.ToUpperInvariant(method.Name[0]) + method.Name[1..],
                    Parameter: null,
                    IsParameterless: true));
            }
            else if (paramCount == 1)
            {
                var param = method.CSSignature[1];
                var bridgeParam = MapParameterType(param, context);
                if (bridgeParam == null)
                    continue;

                // Tighten gate: only Primitive, String, BoundEnum this session
                if (bridgeParam.Kind is not BridgeParameterKind.Primitive
                    and not BridgeParameterKind.String
                    and not BridgeParameterKind.BoundEnum)
                    continue;

                candidates.Add(new BridgeModifier(
                    method.Name,
                    char.ToUpperInvariant(method.Name[0]) + method.Name[1..],
                    Parameter: bridgeParam,
                    IsParameterless: false));
            }
            // else: multi-param → skip
        }

        if (candidates.Count == 0)
            return null;

        // Overload dedup: group by MethodName, skip any name with 2+ candidates
        var grouped = candidates.GroupBy(c => c.MethodName).ToList();
        var result = new List<BridgeModifier>();
        foreach (var group in grouped)
        {
            if (group.Count() == 1)
                result.Add(group.First());
            else
                context?.Logger?.LogDebug("SwiftUI bridge: skipping overloaded modifier '{MethodName}' on {ViewName} ({Count} overloads)",
                    group.Key, viewType.Name, group.Count());
        }

        return result.Count > 0 ? result : null;
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

    /// <summary>
    /// Maps Binding&lt;T&gt; by unwrapping the inner type and marking the result as a Binding parameter.
    /// The inner type is bridged normally (same ABI), but the Wrapper passes $state.name (Binding projection).
    /// Supports Binding&lt;Primitive&gt;, Binding&lt;String&gt;, Binding&lt;BoundEnum&gt;, Binding&lt;Optional&lt;T&gt;&gt;,
    /// and Binding&lt;CodableStruct&gt; (non-frozen, non-generic struct conforming to Codable).
    /// </summary>
    private static BridgeParameter? MapBindingType(string paramName, NamedTypeSpec namedSpec, BridgeContext? context)
    {
        var innerTypeSpec = namedSpec.GenericParameters[0];
        if (innerTypeSpec is not NamedTypeSpec innerNamedSpec)
            return null;

        var innerParam = MapNamedType(paramName, innerNamedSpec, context);
        if (innerParam == null)
            return null;

        // Binding<CodableStruct> for non-frozen, non-generic structs conforming to Codable.
        // The Create/Update ABI ships the value as JSON UTF-8 (ptr+len); the Swift bridge
        // decodes via JSONDecoder and stores the real Swift value on @Published state so
        // SwiftUI's $state.<param> projection works unchanged. A per-view Read<Param>Json
        // @_cdecl exposes the current value back to C# for two-way observation. C# reuses
        // the generated EncodeToJson / DecodeFromJson members emitted by CodableJsonEmitter
        // on the same struct binding — gate must mirror CodableJsonEmitter.ShouldEmit so
        // both sides exist.
        if (innerParam.Kind == BridgeParameterKind.BoundStruct
            && innerParam.StructProjection == StructProjectionKind.NonFrozen
            && IsCodableStructForBinding(innerNamedSpec, context))
        {
            return innerParam with { IsBinding = true, IsBindingCodableStruct = true };
        }

        // Binding<Primitive>, Binding<String>, Binding<BoundEnum>, and Binding<Optional<T>>
        // where T is any supported type. The State stores the inner value; $state.x creates
        // the Binding projection automatically. OptionalWrapped works because the update
        // pipeline already handles all Optional inner type variants.
        // Binding<BoundType> and non-Codable Binding<BoundStruct> (non-optional) need more
        // complex two-way lifetime management — deferred.
        if (innerParam.Kind is not BridgeParameterKind.Primitive
            and not BridgeParameterKind.String
            and not BridgeParameterKind.BoundEnum
            and not BridgeParameterKind.OptionalWrapped)
            return null;

        // Reject Binding<SwiftUI.Image> — Image maps as Kind=String with IsSwiftUIImage=true,
        // but Binding projection ($state.name) is incompatible with Image(systemName:) reconstruction.
        if (innerParam.IsSwiftUIImage)
            return null;

        return innerParam with { IsBinding = true };
    }

    /// <summary>
    /// Returns true when the named struct type satisfies the same Codable-emission gate as
    /// <c>CodableJsonEmitter.ShouldEmit</c>: non-generic, non-frozen, projected as a class
    /// (ClassWithOpaquePayload), and conforms to both Encodable and Decodable. When true,
    /// the generated C# binding for the struct carries <c>EncodeToJson()</c> and
    /// <c>DecodeFromJson(byte[])</c> members which the Binding bridge reuses for C# round-trip.
    /// </summary>
    private static bool IsCodableStructForBinding(NamedTypeSpec namedSpec, BridgeContext? context)
    {
        if (context?.ModuleDecl is null)
            return false;

        // CodableJsonEmitter.Emit hard-skips EncodeToJson/DecodeFromJson emission when the
        // wrapper library name is empty (xcframework-less mode). The bridge would then emit
        // C# call sites referencing those nonexistent members; mirror the gate here.
        if (string.IsNullOrEmpty(context.TypeDatabase?.AsyncLibraryName))
            return false;

        var qualifiedName = namedSpec.Name;
        var lastDot = qualifiedName.LastIndexOf('.');
        if (lastDot < 0)
            return false;

        var moduleName = qualifiedName.Substring(0, lastDot);
        var simpleName = qualifiedName.Substring(lastDot + 1);

        // Binding<CodableStruct> requires the inner struct's binding to exist in the same
        // assembly so the bridge's C# call site can reach EncodeToJson/DecodeFromJson without
        // cross-assembly visibility games. Cross-module Codable structs would still satisfy
        // CodableJsonEmitter.ShouldEmit in their home assembly, but the SwiftUI bridge is
        // emitted in this module — defer cross-module routing until a real use case lands.
        if (!string.Equals(moduleName, context.ModuleDecl.Name, StringComparison.Ordinal))
            return false;

        foreach (var typeDecl in context.ModuleDecl.Types)
        {
            if (typeDecl is not StructDecl structDecl) continue;
            if (!string.Equals(structDecl.Name, simpleName, StringComparison.Ordinal)) continue;

            // Mirror CodableJsonEmitter.ShouldEmit: skip generic / frozen / module-internal.
            // Frozen structs lack the _payloadSize + NewFromPayloadCore factory that the
            // C# DecodeFromJson relies on; module-internal types aren't projected at all.
            if (structDecl.IsGeneric) return false;
            if (structDecl.IsFrozen) return false;
            if (structDecl.IsModuleInternal) return false;

            return CodableJsonEmitter.ConformsToCodable(structDecl);
        }

        return false;
    }

    /// <summary>
    /// Maps Array&lt;T&gt; where T is a bridgeable element type (Primitive, BoundEnum).
    /// Crosses the ABI as a pointer to packed element values + count.
    /// </summary>
    private static BridgeParameter? MapArrayType(string paramName, NamedTypeSpec namedSpec, BridgeContext? context)
    {
        var innerTypeSpec = namedSpec.GenericParameters[0];
        if (innerTypeSpec is not NamedTypeSpec innerNamedSpec)
            return null;

        // Map the element type
        var elementParam = MapNamedType($"{paramName}_elem", innerNamedSpec, context);
        if (elementParam == null)
            return null;

        // Support arrays of Primitives and BoundEnum (integer raw-value) for now
        if (elementParam.Kind is not BridgeParameterKind.Primitive
            and not BridgeParameterKind.BoundEnum)
            return null;

        return new BridgeParameter(
            paramName, BridgeParameterKind.BridgeArray,
            SwiftAbiType: $"UnsafePointer<{elementParam.SwiftAbiType}>?",
            CSharpPInvokeType: "IntPtr",
            HasLength: true,
            InnerParameter: elementParam);
    }

    /// <summary>
    /// Maps SwiftUI.Image as a String parameter (SF Symbol name).
    /// The Swift wrapper constructs Image(systemName:) from the string value.
    /// </summary>
    private static BridgeParameter MapSwiftUIImageType(string paramName)
    {
        return new BridgeParameter(
            paramName, BridgeParameterKind.String,
            SwiftAbiType: "UnsafePointer<UInt8>?",
            CSharpPInvokeType: "IntPtr",
            HasLength: true,
            IsSwiftUIImage: true);
    }
}

/// <summary>
/// A detected self-returning modifier method on a View.
/// Maps to a Set* @_cdecl function and a C# method on the Session class.
/// </summary>
public record BridgeModifier(
    string MethodName,          // Swift name: "playing", "animationSpeed"
    string PascalName,          // C# name: "Playing", "AnimationSpeed"
    BridgeParameter? Parameter, // null for parameterless, single BridgeParameter otherwise
    bool IsParameterless);      // true = bool toggle, false = single non-optional param

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
    /// <summary>Array&lt;T&gt; where T is a bridgeable type (Primitive, BoundEnum). Crosses ABI as pointer + count.</summary>
    BridgeArray,
    /// <summary>Closure taking Result&lt;Success, Failure&gt; and returning Void.
    /// Decomposed at the ABI into two separate callbacks (onSuccess + onError).</summary>
    ResultClosure,
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
    StructProjectionKind? StructProjection = null,
    bool IsObjCBridgeable = false,
    /// <summary>True when the original Swift type is Binding&lt;T&gt;. The inner T is bridged normally,
    /// but the Wrapper passes $state.name (Binding projection) instead of state.name.</summary>
    bool IsBinding = false,
    /// <summary>True when the original Swift type is SwiftUI.Image. Bridges as String (SF Symbol name),
    /// but the Wrapper constructs Image(systemName:) from the stored string.</summary>
    bool IsSwiftUIImage = false,
    /// <summary>Mapped success type for ResultClosure (e.g., BoundType for ScanResult).</summary>
    BridgeParameter? ResultSuccessParam = null,
    /// <summary>Mapped error type for ResultClosure (e.g., BoundType for ScanError).</summary>
    BridgeParameter? ResultErrorParam = null,
    /// <summary>True when the original Swift type is Binding&lt;T&gt; AND the inner T is a non-frozen
    /// Codable struct. The Create/Update ABI carries the value as JSON UTF-8 bytes (ptr+len),
    /// the Swift state stores the decoded value, and a per-view Read&lt;Param&gt;Json @_cdecl
    /// exposes the current state back to C# for two-way observation.</summary>
    bool IsBindingCodableStruct = false)
{
    /// <summary>
    /// Returns true for parameter kinds that support Update* methods (two-way state binding).
    /// Closures and arrays are excluded — closures because of GCHandle lifecycle,
    /// arrays because full-array replacement is complex and deferred.
    /// </summary>
    public bool IsUpdatable => Kind is not BridgeParameterKind.VoidClosure
                                    and not BridgeParameterKind.TypedClosure
                                    and not BridgeParameterKind.ResultClosure
                                    and not BridgeParameterKind.BridgeArray;
}
