// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits int/uint convenience overloads for methods with nint/nuint parameters.
/// C# developers expect Skip(3) and Take(5) with int, not nint.
/// Overloads are pure C# delegation — no Swift wrapper needed.
/// </summary>
internal static class NativeIntOverloadEmitter
{
    private static readonly Dictionary<string, (string NativeType, string ConvenienceType)> NativeIntMap = new()
    {
        ["Swift.Int"] = ("nint", "int"),
        ["Swift.UInt"] = ("nuint", "uint"),
        // Unqualified forms from swiftinterface-parsed protocol extension methods
        ["Int"] = ("nint", "int"),
        ["UInt"] = ("nuint", "uint"),
    };

    /// <summary>
    /// Tries to emit an int/uint convenience overload for a method with nint/nuint params.
    /// </summary>
    public static void TryEmitOverload(CSharpWriter csWriter, MethodEnvironment methodEnv)
    {
        var methodDecl = methodEnv.MethodDecl;

        // Gate: skip constructors, accessors, async, missing symbols
        if (methodDecl.IsConstructor || methodDecl.IsAccessor || methodDecl.IsAsync)
            return;
        if (methodDecl.IsMissingExportedSymbol)
            return;

        // Skip methods with their own generic parameters (beyond the parent type's).
        // E.g., randomInteger<TRNG>(width: Int, generator: inout TRNG) — the overload
        // can't express the TRNG constraint. But skip(count: Int) on Observable<Element>
        // is safe because the int overload inherits the class-level Element naturally.
        var parentGenericCount = (methodDecl.ParentDecl as TypeDecl)?.GenericParameters?.Count ?? 0;
        var methodGenericCount = methodDecl.GenericParameters?.Count ?? 0;
        if (methodGenericCount > parentGenericCount)
            return;

        var csSignature = methodDecl.CSSignature;
        if (csSignature.Count < 2)
            return;

        // Detect nint/nuint params (skip return type at index 0)
        var conversions = new List<(int index, string nativeType, string convType)>();
        for (int i = 1; i < csSignature.Count; i++)
        {
            var arg = csSignature[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            if (arg.SwiftTypeSpec is NamedTypeSpec ns && NativeIntMap.TryGetValue(ns.Name, out var mapping))
                conversions.Add((i, mapping.NativeType, mapping.ConvenienceType));
        }

        if (conversions.Count == 0)
            return;

        // Dedup: check if this overload signature already exists
        if (methodEnv.EmittedProjectedSignatures != null)
        {
            var overloadKey = BuildOverloadKey(methodEnv, conversions);
            if (!methodEnv.EmittedProjectedSignatures.Add(overloadKey))
                return;
        }

        // Determine return type — keep nint/nuint return type as-is to avoid truncation.
        // Only parameters get narrowed (int → nint upcast is safe; nint → int downcast is not).
        var returnTypeSpec = csSignature[0].SwiftTypeSpec;
        bool hasReturn = !returnTypeSpec.IsEmptyTuple;
        string returnType = hasReturn ? ResolveType(returnTypeSpec, methodEnv, isParameter: false) : "void";

        // Build the method name
        var methodName = methodEnv.CSharpMethodName;

        // Build parameter list and call arguments
        var paramParts = new List<string>();
        var callArgs = new List<string>();
        for (int i = 1; i < csSignature.Count; i++)
        {
            var arg = csSignature[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;

            var paramName = NameProvider.GetCSharpParameterName(arg);
            var conv = conversions.Find(c => c.index == i);
            if (conv != default)
            {
                paramParts.Add($"{conv.convType} {paramName}");
                callArgs.Add($"({conv.nativeType}){paramName}");
            }
            else
            {
                var typeName = ResolveType(arg.SwiftTypeSpec, methodEnv, isParameter: true);
                paramParts.Add($"{typeName} {paramName}");
                callArgs.Add(paramName);
            }
        }

        var paramStr = string.Join(", ", paramParts);
        var argsStr = string.Join(", ", callArgs);

        // Determine static modifier
        var isStatic = methodDecl.MethodType == MethodType.Static;
        var staticModifier = isStatic ? "static " : "";

        // Emit the overload
        if (hasReturn)
        {
            csWriter.WriteLine($"public {staticModifier}{returnType} {methodName}({paramStr}) => {methodName}({argsStr});");
        }
        else
        {
            csWriter.WriteLine($"public {staticModifier}void {methodName}({paramStr}) => {methodName}({argsStr});");
        }
    }

    /// <summary>
    /// Tries to emit an int/uint convenience indexer overload for a subscript with nint/nuint params.
    /// </summary>
    public static void TryEmitIndexerOverload(
        CSharpWriter csWriter,
        SubscriptDecl subscriptDecl,
        string returnTypeName,
        List<(string typeName, string paramName, ITypeProjection? projection)> paramInfos,
        HashSet<string>? emittedIndexerKeys = null)
    {
        // Detect nint/nuint params
        var conversions = new List<(int index, string nativeType, string convType)>();
        for (int i = 0; i < paramInfos.Count; i++)
        {
            if (paramInfos[i].typeName == "nint")
                conversions.Add((i, "nint", "int"));
            else if (paramInfos[i].typeName == "nuint")
                conversions.Add((i, "nuint", "uint"));
        }

        if (conversions.Count == 0)
            return;

        // Dedup: build converted signature key and check against already-emitted indexers
        if (emittedIndexerKeys != null)
        {
            var convertedTypes = paramInfos.Select((p, i) =>
            {
                var conv = conversions.Find(c => c.index == i);
                return conv != default ? conv.convType : p.typeName;
            });
            var overloadKey = string.Join(",", convertedTypes);
            if (!emittedIndexerKeys.Add(overloadKey))
                return;
        }

        // Build param list with converted types
        var paramParts = new List<string>();
        var castArgs = new List<string>();
        for (int i = 0; i < paramInfos.Count; i++)
        {
            var (typeName, paramName, _) = paramInfos[i];
            var conv = conversions.Find(c => c.index == i);
            if (conv != default)
            {
                paramParts.Add($"{conv.convType} {paramName}");
                castArgs.Add($"({conv.nativeType}){paramName}");
            }
            else
            {
                paramParts.Add($"{typeName} {paramName}");
                castArgs.Add(paramName);
            }
        }

        var paramStr = string.Join(", ", paramParts);
        var castArgStr = string.Join(", ", castArgs);

        var hasGetter = subscriptDecl.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = subscriptDecl.Accessors.OfType<SetAccessorDecl>().Any();

        if (hasGetter && hasSetter)
        {
            csWriter.WriteLine($"public {returnTypeName} this[{paramStr}]");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine($"get => this[{castArgStr}];");
            csWriter.WriteLine($"set => this[{castArgStr}] = value;");
            csWriter.Indent--;
            csWriter.WriteLine("}");
        }
        else if (hasGetter)
        {
            csWriter.WriteLine($"public {returnTypeName} this[{paramStr}] => this[{castArgStr}];");
        }
        csWriter.WriteLine();
    }

    /// <summary>
    /// Resolves a Swift TypeSpec to a C# type name using the same projection pipeline
    /// as MethodHandler (TypeProjectionFactory → TypeDatabase → ToString fallback),
    /// with generic type parameter substitution.
    /// </summary>
    private static string ResolveType(TypeSpec typeSpec, MethodEnvironment methodEnv, bool isParameter)
    {
        // Handle generic type parameters via GenericTypeMapping
        if (typeSpec is NamedTypeSpec ns && methodEnv.GenericTypeMapping.TryGetValue(ns.ToString(), out var mapping))
            return mapping.TypeParameter;

        var factory = new TypeProjectionFactory();
        var projection = factory.Project(typeSpec, new ProjectionContext
        {
            TypeDatabase = methodEnv.TypeDatabase,
            IsParameter = isParameter
        });
        if (projection != null)
            return projection.PublicType;

        // For named types with generic arguments, resolve base name + each generic arg recursively.
        // Must come BEFORE TryGetTypeRecord which strips generic args.
        if (typeSpec is NamedTypeSpec namedSpec && namedSpec.GenericParameters?.Count > 0)
        {
            var bareSpec = new NamedTypeSpec(namedSpec.Name);
            string baseName;
            if (methodEnv.TypeDatabase.TryGetTypeRecord(bareSpec, out var rec))
                baseName = rec.CSharpTypeName.FullyQualifiedName;
            else
                baseName = namedSpec.Name;
            var genericArgs = namedSpec.GenericParameters.Select(gp => ResolveType(gp, methodEnv, isParameter));
            return $"{baseName}<{string.Join(", ", genericArgs)}>";
        }

        if (methodEnv.TypeDatabase.TryGetTypeRecord(typeSpec, out var record))
            return record.CSharpTypeName.FullyQualifiedName;

        return typeSpec.ToString();
    }

    private static string BuildOverloadKey(MethodEnvironment methodEnv, List<(int index, string nativeType, string convType)> conversions)
    {
        var methodDecl = methodEnv.MethodDecl;
        var methodName = methodEnv.CSharpMethodName;

        var paramTypes = new List<string>();
        for (int i = 1; i < methodDecl.CSSignature.Count; i++)
        {
            var arg = methodDecl.CSSignature[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;

            var conv = conversions.Find(c => c.index == i);
            if (conv != default)
            {
                paramTypes.Add(conv.convType);
            }
            else
            {
                // Mirror GetProjectedCSharpMethodKey: unwrap Optional<Closure> before resolving
                var typeSpecForKey = arg.SwiftTypeSpec;
                if (typeSpecForKey is NamedTypeSpec optSpec &&
                    optSpec.Name == "Swift.Optional" &&
                    optSpec.GenericParameters.Count == 1 &&
                    optSpec.GenericParameters[0] is ClosureTypeSpec)
                {
                    typeSpecForKey = optSpec.GenericParameters[0];
                }
                var paramType = ResolveType(typeSpecForKey, methodEnv, isParameter: true);
                // Normalize nullable reference types (Optional<Class> ≡ Class for overload identity)
                paramType = ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity(paramType, arg.SwiftTypeSpec, methodEnv.TypeDatabase);
                paramTypes.Add(paramType);
            }
        }

        return $"{methodName}({string.Join(",", paramTypes)})";
    }
}
