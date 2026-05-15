// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Emits protocol extension methods by:
/// 1. Mapping protocol extension methods (from swiftinterface) to conforming types (from ABI)
/// 2. Creating synthetic MethodDecl entries on each conforming type
/// 3. Generating @_silgen_name Swift wrappers that specialize Self to the concrete type
///
/// Protocol extension methods use static dispatch and do NOT appear in ABI JSON.
/// The C# side is handled by the existing MethodHandler → PInvokeEmitter pipeline
/// via UsesWrapperLibrary + UsesFreeFunctionWrapper flags.
/// </summary>
public static class ProtocolExtensionEmitter
{
    /// <summary>
    /// Scans the module's conforming types, matches them to parsed protocol extension methods,
    /// and injects synthetic MethodDecl entries with corresponding Swift wrapper code.
    /// </summary>
    public static void InjectExtensionMethods(
        ModuleDecl moduleDecl,
        Dictionary<string, List<ProtocolExtensionMethodDecl>> protocolExtensionMethods,
        ITypeDatabase typeDatabase,
        ILogger logger,
        ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        if (protocolExtensionMethods.Count == 0)
            return;

        // Build protocol name → list of conforming TypeDecl map (ClassDecl + non-frozen StructDecl)
        var conformanceMap = BuildConformanceMap(moduleDecl);

        foreach (var (protocolQualifiedName, extensionMethods) in protocolExtensionMethods)
        {
            // Extract unqualified protocol name for conformance lookup
            var dotIdx = protocolQualifiedName.LastIndexOf('.');
            var unqualifiedProtocolName = dotIdx >= 0
                ? protocolQualifiedName.Substring(dotIdx + 1)
                : protocolQualifiedName;

            if (!conformanceMap.TryGetValue(unqualifiedProtocolName, out var conformingTypes))
                continue;

            foreach (var extMethod in extensionMethods)
            {
                // Skip constrained extensions for now — we can't validate constraints
                // against conforming types without full type system resolution
                if (extMethod.WhereConstraints.Count > 0)
                    continue;

                // Skip properties (defer to later session)
                if (extMethod.IsProperty)
                    continue;

                // Skip static methods (defer to later session)
                if (extMethod.IsStatic)
                    continue;

                // Skip async methods (wrapper semantics not implemented)
                if (IsAsyncSignature(extMethod.RawSignature))
                    continue;

                // Skip typed throws (e.g., "throws(ParseError)") — deferred
                if (IsTypedThrowsSignature(extMethod.RawSignature))
                    continue;

                foreach (var conformingType in conformingTypes)
                {
                    TryInjectMethod(moduleDecl, conformingType, extMethod, typeDatabase, logger, ctx);
                }
            }
        }

        if (ctx.ProtocolExtInjectedCount > 0)
        {
            logger.LogInformation("Injected {Count} protocol extension methods across conforming types", ctx.ProtocolExtInjectedCount);
        }
    }

    /// <summary>
    /// Emits accumulated Swift wrapper functions to the SwiftWriter.
    /// Called from ModuleHandler.Emit() after all types have been processed.
    /// </summary>
    public static void EmitSwiftWrappers(SwiftWriter swiftWriter, ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        if (ctx.ProtocolExtSwiftWrapperLines.Count == 0)
            return;

        // Emit `_sbWrapClosureContext` and its dlsym-cached factory before the
        // buffered wrapper bodies are flushed — they reference the helper by name.
        // Idempotent across paths (NCB / MCB may have already emitted it earlier).
        if (ctx.ProtocolExtUsesClosureContextHelper)
        {
            ClosureContextHelperEmitter.EmitIfNeeded(swiftWriter, ctx);
        }

        swiftWriter.WriteLine();
        swiftWriter.WriteLine("// --- Protocol extension method wrappers ---");
        foreach (var line in ctx.ProtocolExtSwiftWrapperLines)
        {
            swiftWriter.WriteLine(line);
        }
    }

    /// <summary>
    /// Builds a map from unqualified protocol name → list of conforming TypeDecls.
    /// Includes ClassDecl types and non-frozen StructDecl types.
    /// </summary>
    private static Dictionary<string, List<TypeDecl>> BuildConformanceMap(ModuleDecl moduleDecl)
    {
        var map = new Dictionary<string, List<TypeDecl>>();
        CollectConformances(moduleDecl.Types, map);
        return map;
    }

    /// <summary>
    /// Recursively collects conformances from types and their nested types.
    /// Includes ClassDecl and non-frozen StructDecl types.
    /// </summary>
    private static void CollectConformances(IEnumerable<TypeDecl> types, Dictionary<string, List<TypeDecl>> map)
    {
        foreach (var type in types)
        {
            List<TypeConformance>? conformances = null;

            if (type is ClassDecl classDecl)
            {
                conformances = classDecl.Conformances;
            }
            else if (type is StructDecl structDecl && !structDecl.IsFrozen)
            {
                conformances = structDecl.Conformances;
            }

            if (conformances != null)
            {
                foreach (var conformance in conformances)
                {
                    var protocolName = conformance.Protocol.Name;
                    if (!map.ContainsKey(protocolName))
                        map[protocolName] = new List<TypeDecl>();
                    if (!map[protocolName].Contains(type))
                        map[protocolName].Add(type);
                }
            }

            // Recurse into nested types
            if (type.Types.Any())
            {
                CollectConformances(type.Types, map);
            }
        }
    }

    /// <summary>
    /// Attempts to inject a single protocol extension method onto a conforming type (class or non-frozen struct).
    /// Applies conservative gates and generates the Swift wrapper + synthetic MethodDecl.
    /// </summary>
    private static void TryInjectMethod(
        ModuleDecl moduleDecl,
        TypeDecl conformingType,
        ProtocolExtensionMethodDecl extMethod,
        ITypeDatabase typeDatabase,
        ILogger logger,
        ModuleEmissionContext ctx)
    {
        var typeName = conformingType.SwiftTypeName.ModuleQualifiedName;
        var flatTypeName = FlattenTypeName(conformingType.SwiftTypeName);

        // Parse the raw signature to extract parameter types and return type
        var parseResult = ParseExtensionSignature(extMethod, typeDatabase, logger);
        if (parseResult == null)
            return;

        var (parameters, returnTypeSpec, returnTypeName) = parseResult.Value;

        // EC-8: Gate: conforming type must not be a protocol in the TypeDatabase.
        // Protocol metatypes (e.g., CryptoSwift.Updatable.self) are invalid in Swift wrapper
        // contexts like assumingMemoryBound(to:) and Unmanaged<T>.fromOpaque().
        if (typeDatabase.TryGetTypeRecord(conformingType.SwiftTypeName, out var conformingTypeRecord) &&
            conformingTypeRecord.Kind == TypeRecordKind.Protocol)
        {
            logger.LogDebug("Skipping extension method {Type}.{Method}: conforming type is a protocol (metatype invalid)",
                typeName, extMethod.MethodName);
            return;
        }

        // Gate: all parameter types must be resolvable and cdecl-compatible
        foreach (var (_, paramTypeSpec, _) in parameters)
        {
            if (!IsCdeclCompatibleType(paramTypeSpec, typeDatabase))
            {
                logger.LogDebug("Skipping extension method {Type}.{Method}: parameter type not cdecl-compatible",
                    typeName, extMethod.MethodName);
                return;
            }
        }

        // EC-17: Gate: parameter and return types must not contain raw generic type parameters
        // or AssociatedTypeReferenceSpec instances (e.g., τ_0_0, τ_0_0.Element).
        // Protocol extension wrappers that reference these produce Swift compilation errors
        // because the generic context is lost outside the protocol extension.
        foreach (var (_, paramTypeSpec, _) in parameters)
        {
            if (WrapperValidation.ContainsRawGenericTypeParam(paramTypeSpec) ||
                ContainsAssociatedTypeReference(paramTypeSpec))
            {
                logger.LogDebug("Skipping extension method {Type}.{Method}: unresolved generic/associated type in parameter",
                    typeName, extMethod.MethodName);
                return;
            }
        }
        if (!extMethod.ReturnsSelf && returnTypeSpec != null && !returnTypeSpec.IsEmptyTuple &&
            (WrapperValidation.ContainsRawGenericTypeParam(returnTypeSpec) ||
             ContainsAssociatedTypeReference(returnTypeSpec)))
        {
            logger.LogDebug("Skipping extension method {Type}.{Method}: unresolved generic/associated type in return",
                typeName, extMethod.MethodName);
            return;
        }

        // Gate: return type must be Self, Void, a primitive, a class type, or a supported existential
        if (!extMethod.ReturnsSelf && returnTypeSpec != null && !returnTypeSpec.IsEmptyTuple)
        {
            if (!IsPrimitiveReturn(returnTypeSpec) &&
                !IsClassType(returnTypeSpec, typeDatabase) &&
                !IsSupportedExistentialReturn(returnTypeSpec, typeDatabase))
            {
                logger.LogDebug("Skipping extension method {Type}.{Method}: return type not class/Self/Void/primitive/existential",
                    typeName, extMethod.MethodName);
                return;
            }
        }

        // Compute @_cdecl eligibility BEFORE BuildSymbolName so the prefix encodes the calling
        // convention: SBW_ ↔ @_cdecl (Cdecl), SBSW_ ↔ @_silgen_name (Swift CC).
        // @_cdecl is illegal on generic functions, so generic conforming types and method-level
        // generics force the @_silgen_name path. Throwing methods are kept on @_silgen_name to
        // avoid changing the working Cdecl+throws bridging for protocol-ext wrappers in this pass.
        // Existential and Foundation.Data params/returns are also not C-representable: the Swift
        // wrapper would emit `any Protocol` / `Foundation.Data` in the @_cdecl signature and
        // swiftc refuses ("type is not representable in C"). Those methods stay on @_silgen_name
        // where the wrapper-side renders existentials by value through the Swift CC ABI.
        bool isThrowsEarly = IsThrowingSignature(extMethod.RawSignature);
        bool hasClosureEarly = parameters.Any(p => p.typeSpec is ClosureTypeSpec);
        var methodLevelGenericsEarly = hasClosureEarly
            ? ExtractMethodLevelGenerics(extMethod.RawSignature, extMethod.MethodName)
            : new List<string>();
        var existentialHandlerEarly = new ExistentialHandler(typeDatabase);
        bool hasNonCRepresentableParam = parameters.Any(p =>
            ContainsNonCRepresentable(p.typeSpec, existentialHandlerEarly));
        bool hasNonCRepresentableReturn = returnTypeSpec != null &&
            !returnTypeSpec.IsEmptyTuple &&
            ContainsNonCRepresentable(returnTypeSpec, existentialHandlerEarly);
        bool useCdecl = !conformingType.IsGeneric &&
                        methodLevelGenericsEarly.Count == 0 &&
                        !isThrowsEarly &&
                        !hasNonCRepresentableParam &&
                        !hasNonCRepresentableReturn;

        // Build symbol name with overload disambiguation
        var symbolName = BuildSymbolName(flatTypeName, extMethod.MethodName, parameters, useCdecl);

        // Early skip if any wrapper kind already claimed this symbol (e.g., a parent class
        // conformance already emitted it, or MethodWrapperEmitter beat us to it). Read-only
        // here — we only *claim* the symbol after every other gate passes, otherwise a
        // failed gate would permanently reserve the name and block a legitimate later
        // emitter from registering it.
        if (ctx.IsWrapperSymbolRegistered(symbolName))
            return;

        // Check for duplicate methods using a Swift-overload-aware key that pairs the
        // method name with each parameter's printed Swift type name. Labels-only keys
        // (e.g. "step(_:)") collapse legitimate Swift overloads that share labels but
        // differ on parameter type (`step(_:Bool)` vs `step(_:Int32)`), so the second
        // overload would silently drop here before reaching the structural-identity
        // claim. Including the Swift type names keeps the fast-path collision check
        // while letting genuine overloads through; the projected C# signature collision
        // gate immediately below still catches cases where two Swift overloads project
        // onto the same C# signature.
        var existingMethodKeys = new HashSet<string>(
            conformingType.Methods.Select(m => BuildOverloadAwareMethodKey(m)));
        var extensionKey = BuildOverloadAwareExtensionKey(extMethod.MethodName, parameters);
        if (existingMethodKeys.Contains(extensionKey))
        {
            logger.LogDebug("Skipping extension method {Type}.{Method}: collision with ABI method (key: {Key})",
                typeName, extMethod.MethodName, extensionKey);
            return;
        }

        // Check projected C# signature collision — Swift overloads with different labels
        // (e.g., verify(_:expectedData:) vs verify(_:for:)) may produce identical C# signatures.
        {
            bool hasReturnValue = returnTypeSpec != null && !returnTypeSpec.IsEmptyTuple;
            var csMethodName = NameProvider.GetPublicMethodName(extMethod.MethodName, isAsync: false,
                hasReturnValue: hasReturnValue, parameterCount: parameters.Count);
            var projParamTypes = new List<string>();
            foreach (var (_, paramTypeSpec, _) in parameters)
            {
                var factory = new TypeProjectionFactory();
                var projection = factory.Project(paramTypeSpec, new ProjectionContext
                {
                    TypeDatabase = typeDatabase,
                    IsParameter = true
                });
                projParamTypes.Add(projection?.PublicType ?? paramTypeSpec.ToString());
            }
            var projectedKey = $"{csMethodName}({string.Join(",", projParamTypes)})";

            // Compute projected keys for existing methods
            var existingProjectedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in conformingType.Methods)
            {
                if (m.IsAccessor || m.IsConstructor) continue;
                try
                {
                    existingProjectedKeys.Add(BaseHandler.GetProjectedCSharpMethodKey(m, typeDatabase));
                }
                catch { /* skip unresolvable methods */ }
            }
            if (existingProjectedKeys.Contains(projectedKey))
            {
                logger.LogDebug("Skipping extension method {Type}.{Method}: projected C# signature collision ({Key})",
                    typeName, extMethod.MethodName, projectedKey);
                return;
            }
        }

        // --- All gates passed: emit Swift wrapper and synthetic MethodDecl ---

        // Determine if method throws (untyped "throws" only — rethrows treated as non-throwing)
        bool isThrows = IsThrowingSignature(extMethod.RawSignature);

        // Detect closure parameters
        int closureCount = 0;
        ClosureTypeSpec? closureTypeSpec = null;
        int closureParamIndex = -1;
        for (int i = 0; i < parameters.Count; i++)
        {
            if (parameters[i].typeSpec is ClosureTypeSpec cts)
            {
                closureCount++;
                closureTypeSpec = cts;
                closureParamIndex = i;
            }
        }

        // Only one closure param supported initially
        if (closureCount > 1)
        {
            logger.LogDebug("Skipping extension method {Type}.{Method}: multiple closure params not supported",
                typeName, extMethod.MethodName);
            return;
        }

        if (closureCount == 1)
        {
            // Only closure-only methods are supported (no additional non-closure params).
            // The C# bridge public method signature only emits the closure delegate param;
            // non-closure params would produce undeclared variable references in the P/Invoke call.
            if (parameters.Count > 1)
            {
                logger.LogDebug("Skipping extension method {Type}.{Method}: closure + additional params not supported",
                    typeName, extMethod.MethodName);
                return;
            }

            // Skip methods with where constraints on method-level generics (e.g., flatMap<Source> where Source : ObservableConvertibleType)
            // These have associated types (Source.Element) we can't represent in C#
            if (HasMethodLevelWhereClause(extMethod.RawSignature))
            {
                logger.LogDebug("Skipping extension method {Type}.{Method}: method-level where constraints not supported",
                    typeName, extMethod.MethodName);
                return;
            }

            // Detect method-level generic params (e.g., <Result> in map<Result>)
            var methodLevelGenerics = ExtractMethodLevelGenerics(extMethod.RawSignature, extMethod.MethodName);

            // Reject methods with inline generic constraints (e.g., <T: Protocol>)
            // ExtractMethodLevelGenerics would parse "T: Protocol" as a generic name,
            // producing invalid Swift/C# code.
            if (methodLevelGenerics.Any(g => g.Contains(':')))
            {
                logger.LogDebug("Skipping extension method {Type}.{Method}: inline generic constraints not supported",
                    typeName, extMethod.MethodName);
                return;
            }

            // Lift the wrapper's @available floor to also satisfy the protocol-extension's
            // own @available — protocol-extension methods inherited from a protocol introduced
            // in a newer SDK (e.g., iOS 18 EntityCollection.insert(_:beforeIndex:) on an iOS 13
            // ChildCollection) need that floor or wrapper compile fails.
            var protocolAvailability = LookupProtocolAvailability(moduleDecl, extMethod.ProtocolQualifiedName);

            // Claim the wrapper symbol via structural identity so a later
            // MethodHandler -> MethodWrapperEmitter pass on the synthetic MethodDecl
            // collapses into the same identity and skips a redundant @_cdecl emission
            // even if its rendered symbol string would have diverged.
            var sourceKey = BuildSourceKey(extMethod);
            if (!ctx.TryClaimWrapperSymbol(typeName, extMethod.MethodName, sourceKey, symbolName))
                return;

            // Closure-bearing method: emit Swift wrapper with closure bridging
            EmitClosureSwiftWrapper(conformingType, extMethod, parameters, returnTypeSpec,
                symbolName, closureTypeSpec!, closureParamIndex, methodLevelGenerics, isThrows, useCdecl,
                typeDatabase, ctx, protocolAvailability);

            // Build synthetic MethodDecl preserving ClosureTypeSpec
            var syntheticMethod = BuildClosureSyntheticMethodDecl(
                moduleDecl, conformingType, extMethod, parameters, returnTypeSpec, returnTypeName,
                symbolName, closureTypeSpec!, methodLevelGenerics, isThrows, useCdecl);
            syntheticMethod.WrapperSourceKey = sourceKey;

            conformingType.Methods.Add(syntheticMethod);
            ctx.ProtocolExtInjectedCount++;
        }
        else
        {
            // Lift the wrapper's @available floor to also satisfy the protocol-extension's
            // own @available — see closure path comment above.
            var protocolAvailability = LookupProtocolAvailability(moduleDecl, extMethod.ProtocolQualifiedName);

            // Claim the wrapper symbol via structural identity — see closure path
            // comment above for the rationale.
            var sourceKey = BuildSourceKey(extMethod);
            if (!ctx.TryClaimWrapperSymbol(typeName, extMethod.MethodName, sourceKey, symbolName))
                return;

            // Non-closure method: existing path
            EmitSwiftWrapper(conformingType, extMethod, parameters, returnTypeSpec, symbolName, isThrows, useCdecl,
                typeDatabase, ctx, protocolAvailability);

            var syntheticMethod = BuildSyntheticMethodDecl(
                moduleDecl, conformingType, extMethod, parameters, returnTypeSpec, returnTypeName, symbolName, isThrows, useCdecl);
            syntheticMethod.WrapperSourceKey = sourceKey;

            conformingType.Methods.Add(syntheticMethod);
            ctx.ProtocolExtInjectedCount++;
        }
    }

    /// <summary>
    /// Parses the raw swiftinterface signature into structured parameter and return type info.
    /// Returns null if parsing fails or types can't be resolved.
    /// </summary>
    private static (List<(string label, TypeSpec typeSpec, string swiftType)> parameters,
                     TypeSpec? returnTypeSpec, string returnTypeName)?
        ParseExtensionSignature(
            ProtocolExtensionMethodDecl extMethod,
            ITypeDatabase typeDatabase,
            ILogger logger)
    {
        var line = extMethod.RawSignature;

        // Extract parameter section
        var funcIdx = line.IndexOf($"func {extMethod.MethodName}", StringComparison.Ordinal);
        if (funcIdx < 0)
            return null;

        var parenStart = line.IndexOf('(', funcIdx);
        if (parenStart < 0)
            return null;

        // Find matching close paren
        int depth = 0;
        int parenEnd = parenStart;
        for (int i = parenStart; i < line.Length; i++)
        {
            if (line[i] == '(') depth++;
            if (line[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    parenEnd = i;
                    break;
                }
            }
        }

        var paramStr = line.Substring(parenStart + 1, parenEnd - parenStart - 1);

        // Parse parameters
        var parameters = new List<(string label, TypeSpec typeSpec, string swiftType)>();
        if (!string.IsNullOrWhiteSpace(paramStr))
        {
            var parts = SplitParameters(paramStr);
            foreach (var part in parts)
            {
                var parsed = ParseParameter(part.Trim());
                if (parsed == null)
                {
                    logger.LogDebug("Skipping extension method {Method}: could not parse parameter '{Param}'",
                        extMethod.MethodName, part.Trim());
                    return null;
                }
                parameters.Add(parsed.Value);
            }
        }

        // Parse return type
        TypeSpec? returnTypeSpec = null;
        string returnTypeName = "void";
        if (extMethod.ReturnsSelf)
        {
            // Will be resolved to the concrete type later
            returnTypeName = "Self";
        }
        else
        {
            // Extract return type from after the closing paren
            var afterParen = line.Substring(parenEnd + 1).Trim();
            // Remove trailing "{"
            var braceIdx = afterParen.IndexOf('{');
            if (braceIdx >= 0)
                afterParen = afterParen.Substring(0, braceIdx).Trim();

            var arrowIdx = afterParen.IndexOf("->", StringComparison.Ordinal);
            if (arrowIdx >= 0)
            {
                var returnTypeStr = afterParen.Substring(arrowIdx + 2).Trim();
                if (returnTypeStr != "Self")
                {
                    try
                    {
                        returnTypeSpec = TypeSpecParser.Parse(returnTypeStr);
                    }
                    catch
                    {
                        logger.LogDebug("Skipping extension method {Method}: TypeSpecParser error for return type '{Type}'",
                            extMethod.MethodName, returnTypeStr);
                        return null;
                    }
                    if (returnTypeSpec == null)
                    {
                        logger.LogDebug("Skipping extension method {Method}: could not parse return type '{Type}'",
                            extMethod.MethodName, returnTypeStr);
                        return null;
                    }
                    returnTypeName = returnTypeStr;
                }
                else
                {
                    // Detected Self in arrow — set flag
                    returnTypeName = "Self";
                }
            }
        }

        return (parameters, returnTypeSpec, returnTypeName);
    }

    /// <summary>
    /// Parses a single parameter from a swiftinterface parameter declaration.
    /// e.g., "_ cache: Kingfisher.ImageCache" → ("_", NamedTypeSpec("Kingfisher.ImageCache"), "Kingfisher.ImageCache")
    /// </summary>
    private static (string label, TypeSpec typeSpec, string swiftType)?
        ParseParameter(string paramDecl)
    {
        var colonIdx = paramDecl.IndexOf(':');
        if (colonIdx < 0)
            return null;

        var beforeColon = paramDecl.Substring(0, colonIdx).Trim();
        var afterColon = paramDecl.Substring(colonIdx + 1).Trim();

        // Strip @escaping, @Sendable, @autoclosure attributes from the type string before
        // handing it to TypeSpecParser (which doesn't accept Swift parameter attributes), and
        // capture which closure-attributes were present so they can be reattached to the
        // parsed ClosureTypeSpec. The escaping flag is load-bearing for the _SBClosureCtx
        // owner-token wiring (Bug 1 Cat 3 / Bug 3 Case 2).
        var (strippedType, attrNames) = StripSwiftAttributes(afterColon);
        afterColon = strippedType;

        // Remove default value (e.g., "= true", "= .init()", "= StatementArguments()")
        var defaultIdx = FindDefaultValueStart(afterColon);
        if (defaultIdx >= 0)
            afterColon = afterColon.Substring(0, defaultIdx).Trim();

        if (string.IsNullOrWhiteSpace(afterColon))
            return null;

        // Extract label (first word before colon)
        var words = beforeColon.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var label = words.Length > 0 ? words[0] : "_";

        // Parse the type — catch TypeSpecParser errors for unrecognized syntax
        TypeSpec? typeSpec;
        try
        {
            typeSpec = TypeSpecParser.Parse(afterColon);
        }
        catch
        {
            return null;
        }
        if (typeSpec == null)
            return null;

        // Reattach closure-affecting attributes that the parser would not have seen.
        if (typeSpec is ClosureTypeSpec parsedClosure)
        {
            foreach (var attrName in attrNames)
            {
                if (!parsedClosure.Attributes.Exists(a => a.Name == attrName))
                    parsedClosure.Attributes.Add(new TypeSpecAttribute(attrName));
            }
        }

        return (label, typeSpec, afterColon);
    }

    /// <summary>
    /// Strips Swift parameter attributes like @escaping, @Sendable, @autoclosure from the
    /// front of <paramref name="typeStr"/> and returns the remaining type along with the
    /// list of attribute names that were stripped (in order). Also strips the "inout"
    /// prefix without recording it. The attribute list lets callers reattach
    /// closure-affecting attributes (notably <c>escaping</c>) to the parsed TypeSpec.
    /// </summary>
    private static (string remaining, List<string> attrNames) StripSwiftAttributes(string typeStr)
    {
        var attrNames = new List<string>();
        while (typeStr.StartsWith("@"))
        {
            var spaceIdx = typeStr.IndexOf(' ');
            if (spaceIdx < 0) break;
            // Capture the attribute name (without the '@' and without any parameter list)
            var attrToken = typeStr.Substring(1, spaceIdx - 1);
            var parenIdx = attrToken.IndexOf('(');
            var attrName = parenIdx >= 0 ? attrToken.Substring(0, parenIdx) : attrToken;
            attrNames.Add(attrName);
            typeStr = typeStr.Substring(spaceIdx + 1).TrimStart();
        }
        if (typeStr.StartsWith("inout "))
            typeStr = typeStr.Substring(6).TrimStart();
        return (typeStr, attrNames);
    }

    /// <summary>
    /// Finds the start of a default value assignment in a parameter type string.
    /// Handles nested angle brackets and parentheses.
    /// Returns -1 if no default value.
    /// </summary>
    private static int FindDefaultValueStart(string typeStr)
    {
        int depth = 0;
        for (int i = 0; i < typeStr.Length; i++)
        {
            char c = typeStr[i];
            if (c == '<' || c == '(' || c == '[') depth++;
            if (c == '>' || c == ')' || c == ']') depth--;
            if (c == '=' && depth == 0 && i > 0 && typeStr[i - 1] == ' ')
                return i - 1; // Include the space before '='
        }
        return -1;
    }

    /// <summary>
    /// Checks if a TypeSpec represents a cdecl-compatible type for Swift wrapper parameters.
    /// Recursively checks if a TypeSpec contains an AssociatedTypeReferenceSpec.
    /// These represent unresolved associated types (e.g., τ_0_0.Element) that can't be
    /// expressed in wrapper signatures outside the protocol generic context.
    /// </summary>
    private static bool ContainsAssociatedTypeReference(TypeSpec typeSpec)
    {
        if (typeSpec is AssociatedTypeReferenceSpec)
            return true;

        if (typeSpec is NamedTypeSpec namedType)
        {
            // Self.X references that weren't resolved
            if (namedType.Name.StartsWith("Self."))
                return true;

            foreach (var gp in namedType.GenericParameters)
            {
                if (ContainsAssociatedTypeReference(gp))
                    return true;
            }
        }
        else if (typeSpec is ClosureTypeSpec closure)
        {
            if (closure.HasArguments() && ContainsAssociatedTypeReference(closure.Arguments))
                return true;
            if (ContainsAssociatedTypeReference(closure.ReturnType))
                return true;
        }
        else if (typeSpec is TupleTypeSpec tuple && !tuple.IsEmptyTuple)
        {
            foreach (var elem in tuple.Elements)
            {
                if (ContainsAssociatedTypeReference(elem))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Currently supports: class types (IntPtr) and primitives (Bool, Int, Float, etc.).
    /// SimpleEnum and ObjCBridged are excluded — the wrapper marshals all non-primitives as Unmanaged.
    /// </summary>
    private static bool IsCdeclCompatibleType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec is NamedTypeSpec namedType)
        {
            // NamedTypeSpec{IsAny=true} is a single-protocol existential — check param support
            if (namedType.IsAny)
                return IsSupportedExistentialParam(typeSpec, typeDatabase);

            // Reject closures embedded in named types
            if (namedType.ContainsGenericParameters)
            {
                // Swift.Array<T>: single-pointer-width value type.
                // Wrapper passes as UnsafeMutableRawPointer, converts via unsafeBitCast.
                if (MarshallingHelpers.IsSwiftArray(typeSpec))
                    return true;

                // Optional<T> is ok if T is cdecl-compatible (but NOT Optional<BoundGeneric>
                // like Optional<Array<T>> — wrapper rendering can't handle optional bound generics)
                if (namedType.Name == "Swift.Optional" && namedType.GenericParameters.Count == 1)
                {
                    var inner = namedType.GenericParameters[0];
                    if (inner is NamedTypeSpec innerNamed && innerNamed.ContainsGenericParameters)
                        return false;
                    return IsCdeclCompatibleType(inner, typeDatabase);
                }
                return false; // Generic types not handled above
            }

            // Check built-in Swift primitives
            if (MarshallingHelpers.IsSwiftPrimitive(namedType.Name))
                return true;

            // Foundation.Data is a frozen blittable struct with NativeTypeRemapping.
            // C# Swift.Foundation.Data struct mirrors the ABI layout — pass by value through CallConvSwift.
            if (namedType.Name == "Foundation.Data")
                return true;

            // Check TypeDatabase for class types only
            // SimpleEnum and ObjCBridged are excluded because EmitSwiftWrapper marshals
            // all non-primitive NamedTypeSpec as Unmanaged<T> (class ABI).
            try
            {
                if (typeDatabase.TryGetTypeRecord(
                        SwiftTypeName.FromModuleQualifiedName(namedType.Name), out var typeRecord))
                {
                    return typeRecord.Kind == TypeRecordKind.Class;
                }
            }
            catch (ArgumentException)
            {
                // Not a valid module-qualified name (e.g., opaque types like "some Protocol")
                return false;
            }

            return false;
        }

        if (typeSpec is ClosureTypeSpec closureSpec)
            return IsClosureBridgeable(closureSpec, typeDatabase);

        if (typeSpec is TupleTypeSpec tuple)
            return tuple.IsEmptyTuple; // Only empty tuple (Void) is ok

        if (typeSpec is ProtocolListTypeSpec)
            return IsSupportedExistentialParam(typeSpec, typeDatabase);

        return false;
    }

    /// <summary>
    /// Checks if a TypeSpec represents a class type in the TypeDatabase.
    /// </summary>
    private static bool IsClassType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return false;

        // For generic types like Observable<Self.Element>, strip generic params and check base
        var lookupName = namedType.Name;

        try
        {
            if (typeDatabase.TryGetTypeRecord(
                    SwiftTypeName.FromModuleQualifiedName(lookupName), out var typeRecord))
            {
                return typeRecord.Kind == TypeRecordKind.Class;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Checks if a TypeSpec represents a Swift primitive type (Int, Bool, Float, etc.)
    /// that EmitSwiftWrapper already handles correctly via direct rendering.
    /// </summary>
    private static bool IsPrimitiveReturn(TypeSpec ts)
        => ts is NamedTypeSpec n && MarshallingHelpers.IsSwiftPrimitive(n.Name);

    /// <summary>
    /// Checks if a return TypeSpec is a supported existential type that the downstream
    /// MethodHandler → PInvokeEmitter → WrapperEmitter.Return pipeline can handle.
    /// Requires: recognized existential, ≤8 witness tables, all protocols have TypeRecords,
    /// no ObjC mixed-composition mismatch, a valid proxy class exists, and the proxy
    /// will actually be emitted (no associated types, Self requirements, or inherited-only
    /// requirements that skip proxy emission).
    /// </summary>
    internal static bool IsSupportedExistentialReturn(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (!IsSupportedExistentialCore(typeSpec, typeDatabase, out var protocolList, out var existentialHandler, out var earlyAccepted))
            return false;
        if (earlyAccepted)
            return true;

        // Must have a valid proxy class name (TryGetFilteredProxyClassName filters ObjC protocols)
        if (!existentialHandler.TryGetFilteredProxyClassName(protocolList!, out _))
            return false;

        // Verify each protocol's TypeRecord doesn't have flags that prevent proxy emission.
        // ProtocolProxyEmitter.Emit() skips protocols with associated types or Self requirements,
        // so the proxy class won't exist at compile time despite having a valid name.
        // InheritedRequirementsOnly also prevents proxy emission (return-only concern).
        if (HasBlockingProtocolFlags(protocolList!, typeDatabase,
                TypeRecordFlags.HasAssociatedTypes | TypeRecordFlags.HasSelfRequirement | TypeRecordFlags.InheritedRequirementsOnly))
            return false;

        return true;
    }

    /// <summary>
    /// Checks if a TypeSpec is a supported existential parameter type.
    /// Same safety checks as IsSupportedExistentialReturn EXCEPT proxy class name /
    /// InheritedRequirementsOnly (return-only concerns — params don't need proxies).
    /// PAT/Self-requirement protocols are still rejected because GetPublicExistentialType returns
    /// a non-generic interface name while the actual emitted interface is generic.
    /// </summary>
    internal static bool IsSupportedExistentialParam(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (!IsSupportedExistentialCore(typeSpec, typeDatabase, out var protocolList, out _, out var earlyAccepted))
            return false;
        if (earlyAccepted)
            return true;

        // Reject protocols with associated types or Self requirements.
        // GetPublicExistentialType returns a non-generic interface name (e.g., "ICollection"),
        // but PAT/Self protocols are emitted as generic interfaces (e.g., "ICollection<TElement>").
        if (HasBlockingProtocolFlags(protocolList!, typeDatabase,
                TypeRecordFlags.HasAssociatedTypes | TypeRecordFlags.HasSelfRequirement))
            return false;

        return true;
    }

    /// <summary>
    /// Shared validation core for existential return and param support.
    /// Returns false if the existential fails common checks. Returns true with earlyAccepted=true
    /// for Any/well-known protocols that need no further validation. Returns true with
    /// earlyAccepted=false when common checks pass and caller should apply its own validation.
    /// </summary>
    private static bool IsSupportedExistentialCore(TypeSpec typeSpec, ITypeDatabase typeDatabase,
        out ProtocolListTypeSpec? protocolList, out ExistentialHandler existentialHandler,
        out bool earlyAccepted)
    {
        existentialHandler = new ExistentialHandler(typeDatabase);
        protocolList = null;
        earlyAccepted = false;

        if (!existentialHandler.IsExistential(typeSpec))
            return false;

        protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
        if (protocolList == null)
            return false;

        if (!existentialHandler.IsSupportedExistential(protocolList))
            return false;

        // Zero-protocol "Any" → ExistentialContainer0, allowed
        if (existentialHandler.IsAnyType(protocolList))
        {
            earlyAccepted = true;
            return true;
        }

        // Well-known protocols (e.g., Swift.Error → AnyError) are always supported
        if (existentialHandler.TryGetWellKnownProtocolType(protocolList, out _))
        {
            earlyAccepted = true;
            return true;
        }

        // All protocols must have TypeRecords
        if (!existentialHandler.AllProtocolsHaveTypeRecords(protocolList))
            return false;

        // Block public types that don't map to a usable interface.
        // "object" = unresolved/unknown protocols.
        // AnyType = generic protocol existentials (e.g., "any EventStream<τ_0_0.Event>").
        var publicType = existentialHandler.GetPublicExistentialType(protocolList);
        if (publicType == "object")
            return false;
        if (publicType == TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName)
            return false;

        // ObjC filtering guard: if filtering drops protocols, ExistentialContainer size mismatches.
        // Mirrors ExistentialHandler.GetEffectiveProtocols so the parity check stays in sync.
        var filteredCount = protocolList.Protocols.Keys
            .Count(p => !TypeDatabaseExtensions.IsObjCExistentialBridgedProtocol(p));
        if (filteredCount != protocolList.Protocols.Count)
            return false;

        return true;
    }

    /// <summary>
    /// Checks if any protocol in the list has TypeRecord flags that prevent proxy emission
    /// for return types (HasAssociatedTypes, HasSelfRequirement, InheritedRequirementsOnly).
    /// Used by WitnessDispatchEmitter for Optional existential return validation.
    /// </summary>
    internal static bool HasBlockingProtocolFlagsForReturn(ProtocolListTypeSpec protocolList,
        ITypeDatabase typeDatabase)
    {
        return HasBlockingProtocolFlags(protocolList, typeDatabase,
            TypeRecordFlags.HasAssociatedTypes | TypeRecordFlags.HasSelfRequirement | TypeRecordFlags.InheritedRequirementsOnly);
    }

    /// <summary>
    /// Checks if any protocol in the list has TypeRecord flags matching the specified mask.
    /// Used by both return and param existential validators with different flag sets.
    /// </summary>
    private static bool HasBlockingProtocolFlags(ProtocolListTypeSpec protocolList,
        ITypeDatabase typeDatabase, TypeRecordFlags blockingFlags)
    {
        foreach (var protocol in protocolList.Protocols.Keys)
        {
            try
            {
                var swiftTypeName = SwiftTypeName.FromTypeSpec(protocol);
                if (typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
                {
                    if ((typeRecord.Flags & blockingFlags) != 0)
                        return true;
                }
            }
            catch
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Checks if a closure TypeSpec can be bridged via the protocol extension closure bridge.
    /// Requirements: not async, all args are generic params or classes (no primitives),
    /// return is Void, Bool, or method-level generic param (no primitive/class returns).
    /// </summary>
    internal static bool IsClosureBridgeable(ClosureTypeSpec closure, ITypeDatabase typeDatabase)
    {
        if (closure.IsAsync) return false;

        foreach (var arg in closure.EachArgument())
        {
            if (!IsClosureArgBridgeable(arg, typeDatabase))
                return false;
        }

        if (!closure.ReturnType.IsEmptyTuple)
        {
            if (!IsClosureReturnBridgeable(closure.ReturnType, typeDatabase))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if a closure argument type is bridgeable (can be passed as raw pointer).
    /// Only generic type parameters and class types are supported — primitives are NOT
    /// supported because the cdecl callback ABI hardcodes UnsafeMutableRawPointer for all args.
    /// </summary>
    private static bool IsClosureArgBridgeable(TypeSpec argType, ITypeDatabase typeDatabase)
    {
        if (argType is not NamedTypeSpec namedArg) return false;

        // Generic type parameters (τ_0_0 etc.) — passed as UnsafeMutableRawPointer
        if (TypeSpecHelpers.IsGenericTypeParameter(namedArg.Name)) return true;

        // Self.Element — resolved to generic param later
        if (namedArg.Name.StartsWith("Self.")) return true;

        // Primitives are NOT bridgeable — the Swift cdecl callback type uses
        // UnsafeMutableRawPointer for all args, but the primitive path passes values
        // directly (__arg{i}) instead of through a pointer buffer.
        if (MarshallingHelpers.IsSwiftPrimitive(namedArg.Name)) return false;

        // Bound generic (e.g., Event<Self.Element>) — check base type is class
        if (namedArg.ContainsGenericParameters)
        {
            try
            {
                if (typeDatabase.TryGetTypeRecord(
                    SwiftTypeName.FromModuleQualifiedName(namedArg.Name), out var record))
                    return record.Kind == TypeRecordKind.Class;
            }
            catch (ArgumentException) { }
            return false;
        }

        // Non-generic named type — check if class
        try
        {
            if (typeDatabase.TryGetTypeRecord(
                SwiftTypeName.FromModuleQualifiedName(namedArg.Name), out var record))
                return record.Kind == TypeRecordKind.Class;
        }
        catch (ArgumentException) { }
        return false;
    }

    /// <summary>
    /// Checks if a closure return type is bridgeable.
    /// Only Void, Bool, and method-level generic type parameters are supported.
    /// Primitive and class closure returns are NOT supported — the C# bridge only
    /// implements closureReturnIsVoid, closureReturnIsBool, and closureReturnIsMethodGeneric
    /// paths. Other shapes would produce malformed delegate types (Func&lt;..., &gt;).
    /// </summary>
    private static bool IsClosureReturnBridgeable(TypeSpec returnType, ITypeDatabase typeDatabase)
    {
        // Void — always bridgeable
        if (returnType.IsEmptyTuple) return true;
        if (returnType is not NamedTypeSpec namedRet) return false;

        // Bool — directly supported
        if (namedRet.Name == "Swift.Bool") return true;

        // Reject τ_X_X generic type parameters — these are class-level generics
        // resolved by ResolveSelfElement (e.g., Self.Element → τ_0_0). The bridge
        // only supports method-level generics, which appear as unqualified sugared
        // names (e.g., "Result") from swiftinterface parsing, handled below.
        if (namedRet.Name.StartsWith("τ_")) return false;

        // Reject Swift keywords that appear as opaque type placeholders
        if (namedRet.Name is "some" or "any" or "Self" or "Error" or "Never")
            return false;

        // Unqualified name not in DB → likely a method-level generic (e.g., "Result" in map<Result>)
        if (!namedRet.Name.Contains('.'))
        {
            try
            {
                // If it's a known concrete type, reject — we only support generics here
                if (typeDatabase.TryGetTypeRecord(
                    SwiftTypeName.FromModuleQualifiedName(namedRet.Name), out _))
                    return false;
            }
            catch (ArgumentException) { }
            // Not in DB + no module → probably a method-level generic
            return true;
        }

        // Module-qualified types (primitives, classes) are not bridgeable as closure returns
        return false;
    }

    /// <summary>
    /// Detects method-level generic type parameter names from a raw swiftinterface signature.
    /// e.g., "func map&lt;Result&gt;(...)" → ["Result"]
    /// </summary>
    internal static List<string> ExtractMethodLevelGenerics(string rawSignature, string methodName)
    {
        var result = new List<string>();
        var funcIdx = rawSignature.IndexOf($"func {methodName}", StringComparison.Ordinal);
        if (funcIdx < 0) return result;

        var afterMethodName = funcIdx + $"func {methodName}".Length;
        if (afterMethodName >= rawSignature.Length) return result;

        if (rawSignature[afterMethodName] == '<')
        {
            var closeAngle = rawSignature.IndexOf('>', afterMethodName);
            if (closeAngle > afterMethodName)
            {
                var genericStr = rawSignature.Substring(afterMethodName + 1, closeAngle - afterMethodName - 1);
                result.AddRange(genericStr.Split(',').Select(g => g.Trim()));
            }
        }

        return result;
    }

    /// <summary>
    /// Checks if the method's raw signature contains a method-level where clause
    /// (e.g., "where Source : ObservableConvertibleType"). These constrained generics
    /// often have associated types (Source.Element) that can't be represented in C#.
    /// </summary>
    private static bool HasMethodLevelWhereClause(string rawSignature)
    {
        // Find the outermost closing paren of the parameter list
        int depth = 0;
        int closeParen = -1;
        for (int i = 0; i < rawSignature.Length; i++)
        {
            if (rawSignature[i] == '(') depth++;
            if (rawSignature[i] == ')')
            {
                depth--;
                if (depth == 0) { closeParen = i; break; }
            }
        }
        if (closeParen < 0) return false;

        // Check for "where" after the closing paren
        var afterParen = rawSignature.Substring(closeParen + 1);
        return afterParen.Contains(" where ", StringComparison.Ordinal);
    }

    /// <summary>
    /// Checks if a raw swiftinterface signature represents an async method.
    /// Detects "async" keyword after the closing paren and before "->"/"{".
    /// </summary>
    internal static bool IsAsyncSignature(string rawSignature)
    {
        var qualifiers = ExtractQualifiers(rawSignature);
        if (qualifiers == null) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(qualifiers, @"\basync\b");
    }

    /// <summary>
    /// Checks if a raw swiftinterface signature represents an untyped throwing method.
    /// Matches "throws" but NOT "rethrows" or typed "throws(ErrorType)".
    /// </summary>
    internal static bool IsThrowingSignature(string rawSignature)
    {
        var qualifiers = ExtractQualifiers(rawSignature);
        if (qualifiers == null) return false;
        // Match "throws" as a whole word, but NOT "rethrows" and NOT "throws("
        return System.Text.RegularExpressions.Regex.IsMatch(qualifiers, @"(?<!\bre)throws(?!\s*\()");
    }

    /// <summary>
    /// Checks if a raw swiftinterface signature represents a typed throwing method.
    /// Matches "throws(ErrorType)" — these stay gated because extracting the error type
    /// from raw swiftinterface and resolving it to a TypeSpec is non-trivial.
    /// </summary>
    internal static bool IsTypedThrowsSignature(string rawSignature)
    {
        var qualifiers = ExtractQualifiers(rawSignature);
        if (qualifiers == null) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(qualifiers, @"\bthrows\s*\(");
    }

    /// <summary>
    /// Checks if a raw swiftinterface signature represents a rethrowing method.
    /// Rethrows methods are treated as non-throwing since the closure bridge doesn't
    /// propagate closure throws.
    /// </summary>
    internal static bool IsRethrowsSignature(string rawSignature)
    {
        var qualifiers = ExtractQualifiers(rawSignature);
        if (qualifiers == null) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(qualifiers, @"\brethrows\b");
    }

    /// <summary>
    /// Extracts the qualifier string between the closing paren and return arrow / opening brace.
    /// Returns null if parsing fails.
    /// </summary>
    private static string? ExtractQualifiers(string rawSignature)
    {
        int depth = 0;
        int parenEnd = -1;
        for (int i = 0; i < rawSignature.Length; i++)
        {
            if (rawSignature[i] == '(') depth++;
            if (rawSignature[i] == ')')
            {
                depth--;
                if (depth == 0) { parenEnd = i; break; }
            }
        }
        if (parenEnd < 0) return null;

        var afterParen = rawSignature.Substring(parenEnd + 1);
        var arrowIdx = afterParen.IndexOf("->", StringComparison.Ordinal);
        var braceIdx = afterParen.IndexOf('{');
        var endIdx = afterParen.Length;
        if (arrowIdx >= 0) endIdx = Math.Min(endIdx, arrowIdx);
        if (braceIdx >= 0) endIdx = Math.Min(endIdx, braceIdx);

        return afterParen.Substring(0, endIdx);
    }

    /// <summary>
    /// Checks if a TypeSpec represents Foundation.Data, a frozen blittable struct
    /// that passes by value through CallConvSwift (not as UnsafeMutableRawPointer).
    /// </summary>
    private static bool IsFoundationData(TypeSpec typeSpec)
        => typeSpec is NamedTypeSpec n && n.Name == "Foundation.Data";

    /// <summary>
    /// Recursive non-C-representable check: returns true if <paramref name="typeSpec"/> or
    /// any Optional generic argument it wraps is an existential or Foundation.Data. The
    /// useCdecl gate must reject Optional&lt;any P&gt; / Optional&lt;Foundation.Data&gt; alongside
    /// the bare forms — the wrapper would render Swift.Optional&lt;any P&gt; in the @_cdecl
    /// signature and swiftc refuses ("type is not representable in C").
    /// </summary>
    private static bool ContainsNonCRepresentable(TypeSpec typeSpec, ExistentialHandler existentialHandler)
    {
        if (existentialHandler.IsExistential(typeSpec) || IsFoundationData(typeSpec))
            return true;

        if (typeSpec is NamedTypeSpec named &&
            named.Name == "Swift.Optional" &&
            named.GenericParameters.Count == 1)
        {
            return ContainsNonCRepresentable(named.GenericParameters[0], existentialHandler);
        }

        return false;
    }

    /// <summary>
    /// Checks if a TypeSpec represents Swift.Array&lt;T&gt;.
    /// Used in wrapper param rendering to decide unsafeBitCast vs Unmanaged.
    /// </summary>
    private static bool IsSwiftArrayType(TypeSpec typeSpec)
        => MarshallingHelpers.IsSwiftArray(typeSpec);

    /// <summary>
    /// Checks if a Swift primitive type should be passed as UnsafeMutableRawPointer in the wrapper.
    /// CGSize/CGPoint/CGRect are struct types that need pointer passing.
    /// </summary>
    private static bool IsStructPrimitive(string typeName)
    {
        return typeName switch
        {
            "CoreFoundation.CGSize" or "CoreFoundation.CGPoint" or "CoreFoundation.CGRect" => true,
            _ => false,
        };
    }

    /// <summary>
    /// Renders a non-closure parameter's Swift declaration for the @_silgen_name wrapper.
    /// Existentials → "any Protocol", Data → "Foundation.Data", Array → "UnsafeMutableRawPointer",
    /// Class → "UnsafeMutableRawPointer", Optional&lt;Class&gt; → "UnsafeMutableRawPointer?"
    /// (mirrors <see cref="CdeclParamMapper.IsOptionalWithReferenceInner"/>'s nullable-pointer
    /// ABI), Primitive → rendered type. Noncopyable (~Copyable) named types fall through to the
    /// generic rendering path and require a `borrowing` ownership keyword in Swift 6.
    /// </summary>
    private static string RenderSwiftParam(string paramName, TypeSpec typeSpec,
        ExistentialHandler existentialHandler, ITypeDatabase typeDatabase)
    {
        if (existentialHandler.IsExistential(typeSpec))
        {
            // Module-qualify the protocol name in the wrapper parameter type. Foundation defines
            // a top-level `Expression` (and other Apple frameworks may add more), so emitting
            // unqualified `any Expression` triggers swiftc "ambiguous for type lookup" when the
            // wrapper compiles against the bound module + Foundation. The wrapper file imports
            // both modules, so the only safe disambiguation is the module-qualified existential.
            var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
            var renderedType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(protocolList ?? typeSpec);
            return $"_ {paramName}: {renderedType}";
        }
        if (IsFoundationData(typeSpec))
            return $"_ {paramName}: Foundation.Data";
        if (IsSwiftArrayType(typeSpec))
            return $"_ {paramName}: UnsafeMutableRawPointer";
        // Optional<reference type>: nullable pointer ABI matching CdeclParamMapper. The C# side
        // passes IntPtr (0 for nil); without this branch the wrapper renders `Optional<Entity>`
        // and swiftc rejects with "type is not representable in Objective-C".
        if (WrapperValidation.IsOptionalWithReferenceInner(typeSpec, typeDatabase))
            return $"_ {paramName}: UnsafeMutableRawPointer?";
        // Optional<value-type> (Optional<Double>, Optional<Int32>, Optional<Bool>, …):
        // bare Optional<…> isn't C-representable, so @_cdecl rejects it. The C# side already
        // passes a SwiftOptional<T>.Payload IntPtr through DangerousGetHandle, so accept
        // UnsafeRawPointer here and let RenderCallArg decode (tag-byte for blittable primitives,
        // assumingMemoryBound pointee fallback for everything else — mirrors CdeclParamMapper.Map).
        if (WrapperValidation.IsOptionalType(typeSpec))
            return $"_ {paramName}: UnsafeRawPointer";
        if (typeSpec is NamedTypeSpec namedType && !namedType.ContainsGenericParameters &&
            !MarshallingHelpers.IsSwiftPrimitive(namedType.Name))
            return $"_ {paramName}: UnsafeMutableRawPointer";

        var rendered = ExistentialBypassEmitter.RenderSwiftTypeSpec(typeSpec);
        // Noncopyable parameters require explicit ownership in Swift 6; we use `consuming`
        // (the default convention for ~Copyable params per SE-0390) so the wrapper body can
        // forward the value to the underlying method without needing to borrow re-borrow.
        var ownership = WrapperValidation.IsNonCopyableType(typeSpec, typeDatabase) ? "consuming " : "";
        return $"_ {paramName}: {ownership}{rendered}";
    }

    /// <summary>
    /// Renders a non-closure parameter's call-site argument for the method invocation.
    /// Existentials/Data → pass directly, Array → unsafeBitCast, Class → Unmanaged.fromOpaque,
    /// Optional&lt;Class&gt; → map nullable-pointer through Unmanaged.fromOpaque,
    /// Primitive → pass through. May emit a local `let` binding via ctx for conversions.
    /// Returns the call argument string (e.g., "label: __paramName" or just "__paramName").
    /// </summary>
    private static string RenderCallArg(string label, string paramName, TypeSpec typeSpec,
        ExistentialHandler existentialHandler, ITypeDatabase typeDatabase, ModuleEmissionContext ctx)
    {
        // Existential and Data: pass directly by value
        if (existentialHandler.IsExistential(typeSpec) || IsFoundationData(typeSpec))
            return label == "_" ? paramName : $"{label}: {paramName}";

        // Array: unsafeBitCast from raw pointer to [Element].
        // Module-qualify the element so an Array<Expression> stays `[FirebaseFirestore.Expression]`
        // when the wrapper compiles against multiple modules that declare the same protocol leaf
        // name (Foundation.Expression vs FirebaseFirestore.Expression).
        if (IsSwiftArrayType(typeSpec))
        {
            var arrayTypeSpec = (NamedTypeSpec)typeSpec;
            var elementType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(arrayTypeSpec.GenericParameters[0]);
            var localName = $"__{paramName}";
            ctx.AddProtocolExtWrapperLine($"    let {localName} = unsafeBitCast({paramName}, to: [{elementType}].self)");
            return label == "_" ? localName : $"{label}: {localName}";
        }

        // Optional<reference type>: paired with the UnsafeMutableRawPointer? param shape.
        // map over the nullable pointer so nil stays nil; reconstruct via AnyObject for
        // safety against ObjC-bridged structs (IndexPath etc.) — Unmanaged<T> requires
        // T: AnyObject so Unmanaged<IndexPath> would fail at runtime.
        if (WrapperValidation.IsOptionalWithReferenceInner(typeSpec, typeDatabase))
        {
            var innerType = ((NamedTypeSpec)typeSpec).GenericParameters[0];
            var renderedInner = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(innerType);
            var localName = $"__{paramName}";
            ctx.AddProtocolExtWrapperLine($"    let {localName}: {renderedInner}? = {paramName}.map {{ Unmanaged<AnyObject>.fromOpaque($0).takeUnretainedValue() as! {renderedInner} }}");
            return label == "_" ? localName : $"{label}: {localName}";
        }

        // Optional<value-type>: paired with the UnsafeRawPointer param shape.
        // The upstream IsCdeclCompatibleType gate only admits Optional<primitive> here
        // (Int*, UInt*, Float, Double, CGFloat, CGSize/CGPoint/CGRect, Bool) — structs and
        // complex enums are rejected entirely before reaching this branch. Two sub-paths:
        //   (a) Blittable primitive — tag-byte at offset sizeof(T); shared with
        //       CdeclParamMapper.Map via OptionalMarshalClassifier.TryGetBlittablePrimitiveOptionalDecode
        //       so the (blittable set × tag-offset table × decode RHS) can't drift.
        //   (b) Bool (extra-inhabitant encoding, no separate tag byte) and frozen primitive
        //       value-types (CGSize/CGPoint/CGRect) — assumingMemoryBound(to:
        //       Swift.Optional<T>.self).pointee fallback.
        // If the gate is ever lifted to admit non-class structs or complex enums, this branch
        // must grow the opaque-pointer decode path that mirrors CdeclParamMapper.Map's
        // Optional<OpaqueType> branch (lines ~215-247) — SwiftOptional<IntPtr> on the C# side
        // ships a pointer-or-null buffer, not an Optional<T> tag-byte buffer.
        if (WrapperValidation.IsOptionalType(typeSpec))
        {
            var localName = $"__{paramName}";
            var blittableDecode = OptionalMarshalClassifier.TryGetBlittablePrimitiveOptionalDecode(typeSpec, paramName);
            if (blittableDecode is not null)
            {
                var (localType, rhs) = blittableDecode.Value;
                ctx.AddProtocolExtWrapperLine($"    let {localName}: {localType} = {rhs}");
            }
            else
            {
                var renderedOptional = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(typeSpec);
                ctx.AddProtocolExtWrapperLine(
                    $"    let {localName} = {paramName}.assumingMemoryBound(to: {renderedOptional}.self).pointee");
            }
            return label == "_" ? localName : $"{label}: {localName}";
        }

        // Class/ObjC-bridged: Unmanaged.fromOpaque
        // Use Unmanaged<AnyObject> + cast to handle both true classes and ObjC-bridged structs
        // (e.g., IndexPath bridged to NSIndexPath). Unmanaged<T> requires T: AnyObject, so
        // Unmanaged<IndexPath> fails for bridged structs.
        if (typeSpec is NamedTypeSpec namedType && !namedType.ContainsGenericParameters &&
            !MarshallingHelpers.IsSwiftPrimitive(namedType.Name))
        {
            var renderedType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(typeSpec);
            var localName = $"__{paramName}";
            ctx.AddProtocolExtWrapperLine($"    let {localName} = Unmanaged<AnyObject>.fromOpaque({paramName}).takeUnretainedValue() as! {renderedType}");
            return label == "_" ? localName : $"{label}: {localName}";
        }

        // Primitive: pass through
        return label == "_" ? paramName : $"{label}: {paramName}";
    }

    /// <summary>
    /// Emits the @_silgen_name Swift wrapper function for a protocol extension method.
    /// For generic conforming types, emits a generic wrapper with unsafeBitCast and
    /// explicit T.Type metatype parameters (required by Swift 6).
    /// Handles both class self (Unmanaged) and struct self (assumingMemoryBound).
    /// </summary>
    private static void EmitSwiftWrapper(
        TypeDecl conformingType,
        ProtocolExtensionMethodDecl extMethod,
        List<(string label, TypeSpec typeSpec, string swiftType)> parameters,
        TypeSpec? returnTypeSpec,
        string symbolName,
        bool isThrows,
        bool useCdecl,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext ctx,
        IReadOnlyList<AvailabilityAnnotation>? protocolAvailability = null)
    {
        var typeName = conformingType.SwiftTypeName.ModuleQualifiedName;
        var isGenericConforming = conformingType.IsGeneric;

        // Build generic parameter names for the wrapper function
        var genericParamNames = new List<string>();
        if (isGenericConforming)
        {
            foreach (var gp in conformingType.GenericParameters)
            {
                genericParamNames.Add(gp.SugaredTypeName ?? $"T{genericParamNames.Count}");
            }
        }
        var genericClause = isGenericConforming
            ? $"<{string.Join(", ", genericParamNames)}>"
            : "";

        // Build where clause from parent type's generic constraints.
        // Free functions need module-qualified conformance names (e.g., GRDB.Cursor).
        var whereClause = isGenericConforming
            ? WrapperEmitterHelpers.BuildSwiftWhereClause(conformingType.GenericParameters, moduleQualify: true)
            : "";

        // Build the qualified type name with generic parameters for unsafeBitCast
        var qualifiedTypeName = isGenericConforming
            ? $"{typeName}<{string.Join(", ", genericParamNames)}>"
            : typeName;

        // Build Swift parameter list for the wrapper function.
        // Compute unique names up front so the wrapper signature and the call args agree
        // on suffixes (e.g. `expression, expression2`) when params share a leaf type.
        var swiftParams = new List<string>();
        swiftParams.Add("_ self_: UnsafeMutableRawPointer");

        var existentialHandler = new ExistentialHandler(typeDatabase);
        var uniqueParamNames = ComputeUniqueParamNames(parameters);

        for (int i = 0; i < parameters.Count; i++)
        {
            var (_, typeSpec, _) = parameters[i];
            swiftParams.Add(RenderSwiftParam(uniqueParamNames[i], typeSpec, existentialHandler, typeDatabase));
        }

        // For generic conforming types, add explicit T.Type metatype params.
        // Swift 6 requires generic params to appear in the function signature.
        // T.Type is ABI-equivalent to TypeMetadata* — C# passes TypeMetadata.
        if (isGenericConforming)
        {
            foreach (var gpName in genericParamNames)
            {
                swiftParams.Add($"_ __{gpName.ToLowerInvariant()}Type: {gpName}.Type");
            }
        }

        // Build return type
        string swiftReturnType;
        bool returnIsClass;
        if (extMethod.ReturnsSelf || (returnTypeSpec == null && !extMethod.ReturnsSelf))
        {
            if (extMethod.ReturnsSelf)
            {
                swiftReturnType = "UnsafeMutableRawPointer";
                returnIsClass = true;
            }
            else
            {
                // Void return
                swiftReturnType = "";
                returnIsClass = false;
            }
        }
        else
        {
            // Check existential first — return by value, not Unmanaged pointer
            if (existentialHandler.IsExistential(returnTypeSpec!))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(returnTypeSpec!);
                swiftReturnType = ExistentialBypassEmitter.RenderSwiftTypeSpec(protocolList ?? returnTypeSpec!);
                returnIsClass = false;  // existentials return by value
            }
            else if (returnTypeSpec is NamedTypeSpec retNamedType && !MarshallingHelpers.IsSwiftPrimitive(retNamedType.Name))
            {
                swiftReturnType = "UnsafeMutableRawPointer";
                returnIsClass = true;
            }
            else
            {
                swiftReturnType = ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec!);
                returnIsClass = false;
            }
        }

        var throwsClause = isThrows ? " throws" : "";
        var returnArrow = string.IsNullOrEmpty(swiftReturnType) ? "" : $" -> {swiftReturnType}";

        // Emit the wrapper function. The annotation choice mirrors the entry-point prefix:
        // useCdecl=true ⇒ SBW_ + @_cdecl (Cdecl P/Invoke); useCdecl=false ⇒ SBSW_ + @_silgen_name
        // (Swift CC P/Invoke). @_cdecl is illegal on generic functions, so generic conforming
        // types and method-level generics force @_silgen_name.
        ctx.AddProtocolExtWrapperLine("");
        // Top-level Swift wrappers don't inherit the conforming type's availability. Without
        // these annotations the wrapper body can reference types/constraints the host wrapper
        // module — built at the framework's deployment target — doesn't satisfy. Emit the
        // strictest per-platform introduced version walking the conforming type's ancestor chain.
        EmitProtocolExtAvailabilityLines(conformingType, ctx, protocolAvailability);
        bool needsMainActor = extMethod.IsMainActorIsolated || conformingType.IsMainActorIsolated;
        if (useCdecl)
        {
            if (needsMainActor)
            {
                ctx.AddProtocolExtWrapperLine("@MainActor");
            }
            ctx.AddProtocolExtWrapperLine($"@_cdecl(\"{symbolName}\")");
        }
        else
        {
            ctx.AddProtocolExtWrapperLine($"@_silgen_name(\"{symbolName}\")");
            if (needsMainActor)
            {
                ctx.AddProtocolExtWrapperLine("@MainActor");
            }
        }
        ctx.AddProtocolExtWrapperLine($"public func {symbolName}{genericClause}({string.Join(", ", swiftParams)}){throwsClause}{returnArrow}{whereClause} {{");

        var isStructConformer = conformingType is StructDecl;

        // Emit self conversion
        if (isStructConformer)
        {
            // Struct types: load value from opaque pointer via assumingMemoryBound.
            // Works for both generic and non-generic structs.
            ctx.AddProtocolExtWrapperLine($"    var instance = self_.assumingMemoryBound(to: {qualifiedTypeName}.self).pointee");
        }
        else if (isGenericConforming)
        {
            // Generic class types use unsafeBitCast to cast the opaque pointer to the
            // parameterized type. Unmanaged<T>.fromOpaque requires non-generic T.
            ctx.AddProtocolExtWrapperLine($"    let instance = unsafeBitCast(self_, to: {qualifiedTypeName}.self)");
        }
        else
        {
            ctx.AddProtocolExtWrapperLine($"    let instance = Unmanaged<{typeName}>.fromOpaque(self_).takeUnretainedValue()");
        }

        // Emit parameter conversions — use the same deduplicated names as the signature.
        var callArgs = new List<string>();
        for (int i = 0; i < parameters.Count; i++)
        {
            var (label, typeSpec, _) = parameters[i];
            callArgs.Add(RenderCallArg(label, uniqueParamNames[i], typeSpec, existentialHandler, typeDatabase, ctx));
        }

        // Emit method call
        var tryPrefix = isThrows ? "try " : "";
        var callStr = $"{tryPrefix}instance.{NameProvider.EscapeSwiftKeyword(extMethod.MethodName)}({string.Join(", ", callArgs)})";

        // For mutating methods on struct conformers, write back the mutated value
        // to the original pointer after the call. Non-frozen structs are heap-allocated
        // (ClassWithOpaquePayload), so the pointer is to a mutable buffer owned by
        // the C# SafeHandle.
        bool needsWriteBack = isStructConformer && extMethod.IsMutating;

        if (extMethod.ReturnsSelf || returnIsClass)
        {
            if (isStructConformer && extMethod.ReturnsSelf)
            {
                // Struct Self-return: allocate buffer, initialize with result value, return pointer.
                // The C# side receives this as IntPtr → SafeHandle (ClassWithOpaquePayload).
                ctx.AddProtocolExtWrapperLine($"    let result = {callStr}");
                if (needsWriteBack)
                {
                    ctx.AddProtocolExtWrapperLine($"    self_.assumingMemoryBound(to: {qualifiedTypeName}.self).pointee = instance");
                }
                ctx.AddProtocolExtWrapperLine($"    let buf = UnsafeMutableRawPointer.allocate(byteCount: MemoryLayout<{qualifiedTypeName}>.size, alignment: MemoryLayout<{qualifiedTypeName}>.alignment)");
                ctx.AddProtocolExtWrapperLine($"    buf.initializeMemory(as: {qualifiedTypeName}.self, repeating: result, count: 1)");
                ctx.AddProtocolExtWrapperLine($"    return buf");
            }
            else
            {
                // Class return: passRetained transfers +1 ownership to the caller so the object
                // stays alive after this wrapper returns. The C# SafeHandle calls
                // Arc.Release on Dispose to balance.
                // Use `as AnyObject` for safety — handles both true classes and ObjC-bridged structs
                // (e.g., IndexPath). Unmanaged.passRetained requires T: AnyObject.
                ctx.AddProtocolExtWrapperLine($"    let result = {callStr}");
                if (needsWriteBack)
                {
                    ctx.AddProtocolExtWrapperLine($"    self_.assumingMemoryBound(to: {qualifiedTypeName}.self).pointee = instance");
                }
                ctx.AddProtocolExtWrapperLine($"    return Unmanaged.passRetained(result as AnyObject).toOpaque()");
            }
        }
        else if (string.IsNullOrEmpty(swiftReturnType))
        {
            ctx.AddProtocolExtWrapperLine($"    {callStr}");
            if (needsWriteBack)
            {
                ctx.AddProtocolExtWrapperLine($"    self_.assumingMemoryBound(to: {qualifiedTypeName}.self).pointee = instance");
            }
        }
        else
        {
            if (needsWriteBack)
            {
                ctx.AddProtocolExtWrapperLine($"    let result = {callStr}");
                ctx.AddProtocolExtWrapperLine($"    self_.assumingMemoryBound(to: {qualifiedTypeName}.self).pointee = instance");
                ctx.AddProtocolExtWrapperLine($"    return result");
            }
            else
            {
                ctx.AddProtocolExtWrapperLine($"    return {callStr}");
            }
        }

        ctx.AddProtocolExtWrapperLine("}");
    }

    /// <summary>
    /// Emits a Swift wrapper that bridges a closure parameter via @convention(c) function pointer + context.
    /// The wrapper constructs a native Swift closure from the cdecl callback, handling generic type
    /// marshalling via buffer allocation.
    /// </summary>
    private static void EmitClosureSwiftWrapper(
        TypeDecl conformingType,
        ProtocolExtensionMethodDecl extMethod,
        List<(string label, TypeSpec typeSpec, string swiftType)> parameters,
        TypeSpec? returnTypeSpec,
        string symbolName,
        ClosureTypeSpec closureTypeSpec,
        int closureParamIndex,
        List<string> methodLevelGenerics,
        bool isThrows,
        bool useCdecl,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext ctx,
        IReadOnlyList<AvailabilityAnnotation>? protocolAvailability = null)
    {
        var typeName = conformingType.SwiftTypeName.ModuleQualifiedName;
        var isGenericConforming = conformingType.IsGeneric;

        // Build generic parameter names for the wrapper function
        var genericParamNames = new List<string>();
        if (isGenericConforming)
        {
            foreach (var gp in conformingType.GenericParameters)
            {
                genericParamNames.Add(gp.SugaredTypeName ?? $"T{genericParamNames.Count}");
            }
        }
        // Add method-level generics
        genericParamNames.AddRange(methodLevelGenerics);

        var genericClause = genericParamNames.Count > 0
            ? $"<{string.Join(", ", genericParamNames)}>"
            : "";

        // Build where clause from parent type's generic constraints.
        // Free functions need module-qualified conformance names (e.g., GRDB.Cursor).
        var whereClause = isGenericConforming
            ? WrapperEmitterHelpers.BuildSwiftWhereClause(conformingType.GenericParameters, moduleQualify: true)
            : "";

        // Build the qualified type name with generic parameters for unsafeBitCast
        var qualifiedTypeName = isGenericConforming
            ? $"{typeName}<{string.Join(", ", genericParamNames.Take(conformingType.GenericParameters.Count))}>"
            : typeName;

        // Analyze closure
        var closureArgs = closureTypeSpec.EachArgument().ToList();
        var closureReturnIsVoid = closureTypeSpec.ReturnType.IsEmptyTuple;
        var closureReturnIsBool = closureTypeSpec.ReturnType is NamedTypeSpec retNamed &&
            retNamed.Name == "Swift.Bool";
        var closureReturnIsGeneric = !closureReturnIsVoid && !closureReturnIsBool &&
            closureTypeSpec.ReturnType is NamedTypeSpec retGenNamed &&
            (TypeSpecHelpers.IsGenericTypeParameter(retGenNamed.Name) ||
             methodLevelGenerics.Contains(retGenNamed.Name));

        // Build Swift parameter list — precompute unique names so the wrapper signature
        // and the call args agree on suffixes when params share a leaf type name.
        var existentialHandler = new ExistentialHandler(typeDatabase);
        var uniqueParamNames = ComputeUniqueParamNames(parameters, closureTypeSpec, closureParamIndex);
        var swiftParams = new List<string>();
        swiftParams.Add("_ self_: UnsafeMutableRawPointer");

        for (int i = 0; i < parameters.Count; i++)
        {
            var (_, typeSpec, _) = parameters[i];
            if (i == closureParamIndex)
            {
                // Replace closure with funcPtr + context.
                var paramName = uniqueParamNames[i];
                swiftParams.Add($"_ {paramName}FuncPtr: UnsafeMutableRawPointer");
                swiftParams.Add($"_ {paramName}Context: UnsafeMutableRawPointer?");
            }
            else
            {
                swiftParams.Add(RenderSwiftParam(uniqueParamNames[i], typeSpec, existentialHandler, typeDatabase));
            }
        }

        // Add explicit T.Type metatype params for ALL generic params (class-level + method-level)
        foreach (var gpName in genericParamNames)
        {
            swiftParams.Add($"_ __{gpName.ToLowerInvariant()}Type: {gpName}.Type");
        }

        // Build return type
        string swiftReturnType;
        bool returnIsClass;
        if (extMethod.ReturnsSelf)
        {
            swiftReturnType = "UnsafeMutableRawPointer";
            returnIsClass = true;
        }
        else if (returnTypeSpec == null || returnTypeSpec.IsEmptyTuple)
        {
            swiftReturnType = "";
            returnIsClass = false;
        }
        else
        {
            // Check existential first — return by value, not Unmanaged pointer
            if (existentialHandler.IsExistential(returnTypeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(returnTypeSpec);
                swiftReturnType = ExistentialBypassEmitter.RenderSwiftTypeSpec(protocolList ?? returnTypeSpec);
                returnIsClass = false;  // existentials return by value
            }
            else if (returnTypeSpec is NamedTypeSpec retNamedType && !MarshallingHelpers.IsSwiftPrimitive(retNamedType.Name))
            {
                swiftReturnType = "UnsafeMutableRawPointer";
                returnIsClass = true;
            }
            else
            {
                swiftReturnType = ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec);
                returnIsClass = false;
            }
        }

        var throwsClause = isThrows ? " throws" : "";
        var returnArrow = string.IsNullOrEmpty(swiftReturnType) ? "" : $" -> {swiftReturnType}";

        // Emit the wrapper function. useCdecl=true ⇒ SBW_ + @_cdecl (Cdecl P/Invoke);
        // useCdecl=false ⇒ SBSW_ + @_silgen_name (Swift CC P/Invoke). @_cdecl is illegal on
        // generic functions, so generic conforming types and method-level generics force
        // @_silgen_name regardless of closure presence.
        ctx.AddProtocolExtWrapperLine("");
        EmitProtocolExtAvailabilityLines(conformingType, ctx, protocolAvailability);
        bool needsMainActorClosure = extMethod.IsMainActorIsolated || conformingType.IsMainActorIsolated;
        if (useCdecl)
        {
            if (needsMainActorClosure)
            {
                ctx.AddProtocolExtWrapperLine("@MainActor");
            }
            ctx.AddProtocolExtWrapperLine($"@_cdecl(\"{symbolName}\")");
        }
        else
        {
            ctx.AddProtocolExtWrapperLine($"@_silgen_name(\"{symbolName}\")");
            if (needsMainActorClosure)
            {
                ctx.AddProtocolExtWrapperLine("@MainActor");
            }
        }
        ctx.AddProtocolExtWrapperLine($"public func {symbolName}{genericClause}({string.Join(", ", swiftParams)}){throwsClause}{returnArrow}{whereClause} {{");

        var isStructConformer = conformingType is StructDecl;

        // Self conversion
        if (isStructConformer)
        {
            // Struct types: load value from opaque pointer via assumingMemoryBound.
            ctx.AddProtocolExtWrapperLine($"    var instance = self_.assumingMemoryBound(to: {qualifiedTypeName}.self).pointee");
        }
        else if (isGenericConforming)
        {
            ctx.AddProtocolExtWrapperLine($"    let instance = unsafeBitCast(self_, to: {qualifiedTypeName}.self)");
        }
        else
        {
            ctx.AddProtocolExtWrapperLine($"    let instance = Unmanaged<{typeName}>.fromOpaque(self_).takeUnretainedValue()");
        }

        // Build cdecl callback type
        var closureParamLabel = parameters[closureParamIndex].label;
        var closureParamName = SanitizeSwiftParamName(
            closureParamLabel == "_" ? GetClosureParamName(closureTypeSpec) : closureParamLabel);

        var cdeclArgTypes = new List<string>();
        foreach (var arg in closureArgs)
        {
            cdeclArgTypes.Add("UnsafeMutableRawPointer"); // All args as raw pointers
        }
        if (closureReturnIsGeneric)
        {
            cdeclArgTypes.Add("UnsafeMutableRawPointer"); // Result buffer for generic return
        }
        cdeclArgTypes.Add("UnsafeMutableRawPointer?"); // Context

        string cdeclReturnType = closureReturnIsBool ? "Bool" : "Void";
        var cdeclTypeStr = $"(@convention(c) ({string.Join(", ", cdeclArgTypes)}) -> {cdeclReturnType}).self";

        ctx.AddProtocolExtWrapperLine($"    let cdecl = unsafeBitCast({closureParamName}FuncPtr, to: {cdeclTypeStr})");

        // Wrap the GCHandle context pointer in a Swift-ARC-owned `_SBClosureCtx` box for
        // escaping closures so the box's deinit upcalls C# and frees the handle exactly
        // once when Swift releases the captured closure (Bug 1 Cat 3 / Bug 3 Case 2).
        // Non-escaping closures do not retain past the call — their handles are freed by
        // the C# wrapper's `finally`. The `_box` constant becomes part of the closure's
        // capture list so its lifetime tracks the closure.
        bool isEscapingProtoExt = closureTypeSpec.IsEscaping;
        if (isEscapingProtoExt)
        {
            ctx.AddProtocolExtWrapperLine($"    let _box: AnyObject = {ClosureContextHelperEmitter.WrapFunctionName}({closureParamName}Context!)");
            ctx.ProtocolExtUsesClosureContextHelper = true;
        }

        // Build the inline closure that calls the cdecl callback
        var closureSwiftArgs = new List<string>();
        int argIdx = 0;
        foreach (var arg in closureArgs)
        {
            var renderedType = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg);
            closureSwiftArgs.Add($"__arg{argIdx}: {renderedType}");
            argIdx++;
        }

        var closureReturnStr = closureReturnIsVoid ? "Void" :
            ExistentialBypassEmitter.RenderSwiftTypeSpec(closureTypeSpec.ReturnType);
        var throwsKeyword = closureTypeSpec.Throws ? " throws" : "";

        // Capture list `[_box]` for escaping closures keeps the owner-token alive for the
        // closure's full Swift-ARC lifetime. For zero-parameter closures, omit the parameter
        // list and `in` keyword (Swift treats `{ in ... }` as a syntax error) — but escaping
        // zero-arg closures still need an explicit `in` after `[_box]`.
        string closureParamList;
        if (closureArgs.Count > 0)
        {
            var args = string.Join(", ", Enumerable.Range(0, closureArgs.Count).Select(i => $"__arg{i}"));
            closureParamList = isEscapingProtoExt
                ? $" [_box] {args} in"
                : $" {args} in";
        }
        else
        {
            closureParamList = isEscapingProtoExt
                ? " [_box] in"
                : "";
        }
        ctx.AddProtocolExtWrapperLine($"    let __closure: ({string.Join(", ", closureSwiftArgs.Select(a => a.Split(':')[1].Trim()))}){throwsKeyword} -> {closureReturnStr} = {{{closureParamList}");

        if (isEscapingProtoExt)
        {
            // Observe `_box` inside the body so the capture list is non-vacuous and the
            // optimizer cannot release the box before the closure runs.
            ctx.AddProtocolExtWrapperLine("        _ = _box");
        }

        // For each arg: allocate buffer, copy, pass to cdecl
        for (int i = 0; i < closureArgs.Count; i++)
        {
            var arg = closureArgs[i];
            bool isGenericArg = arg is NamedTypeSpec gn &&
                (TypeSpecHelpers.IsGenericTypeParameter(gn.Name) ||
                 gn.Name.StartsWith("Self.") ||
                 (gn.ContainsGenericParameters && !MarshallingHelpers.IsSwiftPrimitive(gn.Name)));

            if (isGenericArg || (arg is NamedTypeSpec na && !MarshallingHelpers.IsSwiftPrimitive(na.Name)))
            {
                // Generic or class type: allocate buffer, copy bytes, pass pointer
                var argType = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg);
                ctx.AddProtocolExtWrapperLine($"        let __buf{i} = UnsafeMutableRawPointer.allocate(byteCount: max(MemoryLayout<{argType}>.size, 1), alignment: MemoryLayout<{argType}>.alignment)");
                ctx.AddProtocolExtWrapperLine($"        defer {{ __buf{i}.deallocate() }}");
                ctx.AddProtocolExtWrapperLine($"        withUnsafePointer(to: __arg{i}) {{ __buf{i}.copyMemory(from: UnsafeRawPointer($0), byteCount: MemoryLayout<{argType}>.size) }}");
            }
        }

        // Build cdecl call arguments
        var cdeclCallArgs = new List<string>();
        for (int i = 0; i < closureArgs.Count; i++)
        {
            var arg = closureArgs[i];
            bool isGenericArg = arg is NamedTypeSpec gn2 &&
                (TypeSpecHelpers.IsGenericTypeParameter(gn2.Name) ||
                 gn2.Name.StartsWith("Self.") ||
                 (gn2.ContainsGenericParameters && !MarshallingHelpers.IsSwiftPrimitive(gn2.Name)));

            if (isGenericArg || (arg is NamedTypeSpec na2 && !MarshallingHelpers.IsSwiftPrimitive(na2.Name)))
            {
                cdeclCallArgs.Add($"__buf{i}");
            }
            else
            {
                // Unreachable: IsClosureArgBridgeable rejects primitives.
                // Defensive fallback — pass value directly.
                cdeclCallArgs.Add($"__arg{i}");
            }
        }

        if (closureReturnIsGeneric)
        {
            // Generic return: allocate result buffer, pass to cdecl, load from buffer
            var retType = ExistentialBypassEmitter.RenderSwiftTypeSpec(closureTypeSpec.ReturnType);
            ctx.AddProtocolExtWrapperLine($"        let __resultBuf = UnsafeMutableRawPointer.allocate(byteCount: max(MemoryLayout<{retType}>.size, 1), alignment: MemoryLayout<{retType}>.alignment)");
            ctx.AddProtocolExtWrapperLine($"        defer {{ __resultBuf.deallocate() }}");
            cdeclCallArgs.Add("__resultBuf");
            cdeclCallArgs.Add($"{closureParamName}Context");
            ctx.AddProtocolExtWrapperLine($"        cdecl({string.Join(", ", cdeclCallArgs)})");
            ctx.AddProtocolExtWrapperLine($"        return __resultBuf.load(as: {retType}.self)");
        }
        else if (closureReturnIsBool)
        {
            // Bool return: cdecl returns Bool directly
            cdeclCallArgs.Add($"{closureParamName}Context");
            ctx.AddProtocolExtWrapperLine($"        return cdecl({string.Join(", ", cdeclCallArgs)})");
        }
        else
        {
            // Void return
            cdeclCallArgs.Add($"{closureParamName}Context");
            ctx.AddProtocolExtWrapperLine($"        cdecl({string.Join(", ", cdeclCallArgs)})");
        }

        ctx.AddProtocolExtWrapperLine("    }");

        // Build method call arguments — use the same deduplicated names as the signature.
        var callArgs = new List<string>();
        for (int i = 0; i < parameters.Count; i++)
        {
            var (label, typeSpec, _) = parameters[i];
            if (i == closureParamIndex)
            {
                callArgs.Add(label == "_" ? "__closure" : $"{label}: __closure");
            }
            else
            {
                callArgs.Add(RenderCallArg(label, uniqueParamNames[i], typeSpec, existentialHandler, typeDatabase, ctx));
            }
        }

        // Throwing semantics:
        // - isThrows (method-level "throws"): use "try" — error propagates via Swift error register
        // - closureTypeSpec.Throws (rethrows): use "try!" — closure bridge doesn't propagate errors
        var callStr = $"instance.{NameProvider.EscapeSwiftKeyword(extMethod.MethodName)}({string.Join(", ", callArgs)})";
        if (isThrows)
            callStr = $"try {callStr}";
        else if (closureTypeSpec.Throws)
            callStr = $"try! {callStr}";

        // For mutating methods on struct conformers, write back the mutated value
        bool needsWriteBack = isStructConformer && extMethod.IsMutating;

        if (extMethod.ReturnsSelf || returnIsClass)
        {
            if (isStructConformer && extMethod.ReturnsSelf)
            {
                // Struct Self-return: allocate buffer, initialize with result value, return pointer
                ctx.AddProtocolExtWrapperLine($"    let result = {callStr}");
                if (needsWriteBack)
                {
                    ctx.AddProtocolExtWrapperLine($"    self_.assumingMemoryBound(to: {qualifiedTypeName}.self).pointee = instance");
                }
                ctx.AddProtocolExtWrapperLine($"    let buf = UnsafeMutableRawPointer.allocate(byteCount: MemoryLayout<{qualifiedTypeName}>.size, alignment: MemoryLayout<{qualifiedTypeName}>.alignment)");
                ctx.AddProtocolExtWrapperLine($"    buf.initializeMemory(as: {qualifiedTypeName}.self, repeating: result, count: 1)");
                ctx.AddProtocolExtWrapperLine($"    return buf");
            }
            else
            {
                // Use `as AnyObject` for safety — handles both true classes and ObjC-bridged structs.
                ctx.AddProtocolExtWrapperLine($"    let result = {callStr}");
                if (needsWriteBack)
                {
                    ctx.AddProtocolExtWrapperLine($"    self_.assumingMemoryBound(to: {qualifiedTypeName}.self).pointee = instance");
                }
                ctx.AddProtocolExtWrapperLine($"    return Unmanaged.passRetained(result as AnyObject).toOpaque()");
            }
        }
        else if (string.IsNullOrEmpty(swiftReturnType))
        {
            ctx.AddProtocolExtWrapperLine($"    {callStr}");
            if (needsWriteBack)
            {
                ctx.AddProtocolExtWrapperLine($"    self_.assumingMemoryBound(to: {qualifiedTypeName}.self).pointee = instance");
            }
        }
        else
        {
            if (needsWriteBack)
            {
                ctx.AddProtocolExtWrapperLine($"    let result = {callStr}");
                ctx.AddProtocolExtWrapperLine($"    self_.assumingMemoryBound(to: {qualifiedTypeName}.self).pointee = instance");
                ctx.AddProtocolExtWrapperLine($"    return result");
            }
            else
            {
                ctx.AddProtocolExtWrapperLine($"    return {callStr}");
            }
        }

        ctx.AddProtocolExtWrapperLine("}");
    }

    /// <summary>
    /// Builds a synthetic MethodDecl for a closure-bearing protocol extension method.
    /// Preserves the ClosureTypeSpec in CSSignature so ProtocolExtensionClosureBridge
    /// can detect and handle it in MethodHandler.
    /// </summary>
    private static MethodDecl BuildClosureSyntheticMethodDecl(
        ModuleDecl moduleDecl,
        TypeDecl conformingType,
        ProtocolExtensionMethodDecl extMethod,
        List<(string label, TypeSpec typeSpec, string swiftType)> parameters,
        TypeSpec? returnTypeSpec,
        string returnTypeName,
        string symbolName,
        ClosureTypeSpec closureTypeSpec,
        List<string> methodLevelGenerics,
        bool isThrows,
        bool useCdecl)
    {
        var csSignature = new List<ArgumentDecl>();

        // Resolve Self.Element → τ_0_0 (generic) or concrete type (graph) in return type
        var conformanceGraph = moduleDecl.ConformanceGraph;
        if ((conformingType.IsGeneric || conformanceGraph.Count > 0) && returnTypeSpec != null)
        {
            returnTypeSpec = ResolveSelfElement(returnTypeSpec, conformingType,
                conformanceGraph, extMethod.ProtocolQualifiedName);
        }

        // Return type
        TypeSpec resolvedReturnTypeSpec;
        if (extMethod.ReturnsSelf || returnTypeName == "Self")
        {
            resolvedReturnTypeSpec = new NamedTypeSpec(conformingType.SwiftTypeName.ModuleQualifiedName);
        }
        else if (returnTypeSpec != null)
        {
            resolvedReturnTypeSpec = returnTypeSpec;
        }
        else
        {
            resolvedReturnTypeSpec = TupleTypeSpec.Empty;
        }

        csSignature.Add(new ArgumentDecl
        {
            Name = "",
            PrivateName = "",
            SwiftTypeSpec = resolvedReturnTypeSpec,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        });

        // Parameters — preserve ClosureTypeSpec (resolve Self.Element inside it).
        // Deduplicate internal names so two unlabelled params sharing a type leaf name
        // (e.g. two `Expression`) don't collide on the C# side as `expression, expression`.
        var seenInternalNames = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (label, typeSpec, _) in parameters)
        {
            var resolvedTypeSpec = typeSpec;
            if (conformingType.IsGeneric || conformanceGraph.Count > 0)
            {
                resolvedTypeSpec = ResolveSelfElement(typeSpec, conformingType,
                    conformanceGraph, extMethod.ProtocolQualifiedName);
            }

            string baseInternalName;
            if (typeSpec is ClosureTypeSpec)
            {
                // For closure params, use a sensible name based on closure shape
                // (GetParamNameFromType can't handle ClosureTypeSpec.ToString() output)
                baseInternalName = label != "_" ? label : GetClosureParamName(closureTypeSpec);
            }
            else
            {
                baseInternalName = label == "_" ? GetParamNameFromType(typeSpec.ToString()) : label;
            }
            string internalName;
            if (seenInternalNames.TryGetValue(baseInternalName, out var count))
            {
                seenInternalNames[baseInternalName] = count + 1;
                internalName = $"{baseInternalName}{count + 1}";
            }
            else
            {
                seenInternalNames[baseInternalName] = 1;
                internalName = baseInternalName;
            }
            csSignature.Add(new ArgumentDecl
            {
                Name = label,
                PrivateName = internalName,
                SwiftTypeSpec = resolvedTypeSpec,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = conformingType,
                ModuleDecl = moduleDecl
            });
        }

        // Build generic parameters: class-level + method-level
        var genericParams = conformingType.IsGeneric
            ? new List<GenericArgumentDecl>(conformingType.GenericParameters)
            : new List<GenericArgumentDecl>();

        // Add method-level generics (e.g., Result in map<Result>)
        for (int i = 0; i < methodLevelGenerics.Count; i++)
        {
            var methodGenericName = methodLevelGenerics[i];
            // Use τ_1_X naming for method-level generics (depth 1)
            var typeParamName = $"τ_1_{i}";
            genericParams.Add(new GenericArgumentDecl(
                TypeName: typeParamName,
                SugaredTypeName: methodGenericName,
                GenericConformances: new List<GenericParameterConformance>(),
                AssosiatedTypeConformances: new List<GenericParameterConformance>()
            ));
        }

        return new MethodDecl
        {
            Name = extMethod.MethodName,
            MangledName = symbolName,
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            Throws = isThrows,
            IsAsync = false,
            GenericParameters = genericParams,
            Visibility = Visibility.Public,
            ParentDecl = conformingType,
            ModuleDecl = moduleDecl,
            UsesWrapperLibrary = true,
            UsesFreeFunctionWrapper = true,
            UsesCdeclMethodWrapper = useCdecl,
            IsProtocolExtensionMethod = true,
            IsActorIsolated = extMethod.IsMainActorIsolated || conformingType.IsMainActorIsolated,
            IsMainActorIsolated = extMethod.IsMainActorIsolated || conformingType.IsMainActorIsolated,
        };
    }

    /// <summary>
    /// Builds a synthetic MethodDecl that the existing MethodHandler → PInvokeEmitter pipeline
    /// will process like any other method. Sets UsesWrapperLibrary + UsesFreeFunctionWrapper
    /// so PInvokeEmitter routes to the wrapper library with explicit IntPtr self.
    /// </summary>
    private static MethodDecl BuildSyntheticMethodDecl(
        ModuleDecl moduleDecl,
        TypeDecl conformingType,
        ProtocolExtensionMethodDecl extMethod,
        List<(string label, TypeSpec typeSpec, string swiftType)> parameters,
        TypeSpec? returnTypeSpec,
        string returnTypeName,
        string symbolName,
        bool isThrows,
        bool useCdecl)
    {
        // Build CSSignature: [returnType, param1, param2, ...]
        var csSignature = new List<ArgumentDecl>();

        // Resolve Self.Element → τ_0_0 (generic) or concrete type (graph) in the return type
        var conformanceGraph = moduleDecl.ConformanceGraph;
        if ((conformingType.IsGeneric || conformanceGraph.Count > 0) && returnTypeSpec != null)
        {
            returnTypeSpec = ResolveSelfElement(returnTypeSpec, conformingType,
                conformanceGraph, extMethod.ProtocolQualifiedName);
        }

        // Return type (first element of CSSignature)
        TypeSpec resolvedReturnTypeSpec;
        if (extMethod.ReturnsSelf || returnTypeName == "Self")
        {
            resolvedReturnTypeSpec = new NamedTypeSpec(conformingType.SwiftTypeName.ModuleQualifiedName);
        }
        else if (returnTypeSpec != null)
        {
            resolvedReturnTypeSpec = returnTypeSpec;
        }
        else
        {
            resolvedReturnTypeSpec = TupleTypeSpec.Empty;
        }

        csSignature.Add(new ArgumentDecl
        {
            Name = "",
            PrivateName = "",
            SwiftTypeSpec = resolvedReturnTypeSpec,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        });

        // Parameters — deduplicate internal names so two unlabelled params sharing a
        // type leaf name (e.g. two `any P` projecting to the same `p` base name) don't
        // collide on the C# side as duplicate parameter identifiers.
        var seenInternalNames = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (label, typeSpec, _) in parameters)
        {
            var baseInternalName = label == "_" ? GetParamNameFromType(typeSpec.ToString()) : label;
            string internalName;
            if (seenInternalNames.TryGetValue(baseInternalName, out var count))
            {
                seenInternalNames[baseInternalName] = count + 1;
                internalName = $"{baseInternalName}{count + 1}";
            }
            else
            {
                seenInternalNames[baseInternalName] = 1;
                internalName = baseInternalName;
            }
            csSignature.Add(new ArgumentDecl
            {
                Name = label,
                PrivateName = internalName,
                SwiftTypeSpec = typeSpec,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = conformingType,
                ModuleDecl = moduleDecl
            });
        }

        // For generic conforming types, copy the generic parameters so PInvokeEmitter
        // generates TypeMetadata parameters for each generic type parameter.
        var genericParams = conformingType.IsGeneric
            ? new List<GenericArgumentDecl>(conformingType.GenericParameters)
            : new List<GenericArgumentDecl>();

        return new MethodDecl
        {
            Name = extMethod.MethodName,
            MangledName = symbolName,
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            Throws = isThrows,
            IsAsync = false,
            GenericParameters = genericParams,
            Visibility = Visibility.Public,
            ParentDecl = conformingType,
            ModuleDecl = moduleDecl,
            UsesWrapperLibrary = true,
            UsesFreeFunctionWrapper = true,
            UsesCdeclMethodWrapper = useCdecl,
            IsProtocolExtensionMethod = true,
            IsActorIsolated = extMethod.IsMainActorIsolated || conformingType.IsMainActorIsolated,
            IsMainActorIsolated = extMethod.IsMainActorIsolated || conformingType.IsMainActorIsolated,
        };
    }

    /// <summary>
    /// Resolves Self.Element (and other Self.AssociatedType references) in a TypeSpec
    /// to the conforming type's generic parameter (τ_0_0, τ_0_1, etc.).
    /// For example, Observable<Element> conforming to ObservableType:
    ///   Self.Element → τ_0_0 (the first generic parameter of Observable)
    /// </summary>
    private static TypeSpec ResolveSelfElement(TypeSpec typeSpec, TypeDecl conformingType,
        ConformanceGraph? conformanceGraph = null, string? protocolQualifiedName = null)
    {
        if (typeSpec is NamedTypeSpec namedType)
        {
            // Direct Self.Element reference
            if (namedType.Name.StartsWith("Self."))
            {
                var assocTypeName = namedType.Name.Substring(5); // Remove "Self."
                // Find the matching generic parameter by sugared name
                for (int i = 0; i < conformingType.GenericParameters.Count; i++)
                {
                    if (conformingType.GenericParameters[i].SugaredTypeName == assocTypeName)
                    {
                        return new NamedTypeSpec(conformingType.GenericParameters[i].TypeName);
                    }
                }

                // ConformanceGraph fallback — resolve via TypeWitness data
                if (conformanceGraph != null && protocolQualifiedName != null &&
                    conformanceGraph.TryResolve(
                        conformingType.SwiftTypeName.ModuleQualifiedName,
                        protocolQualifiedName,
                        assocTypeName,
                        out var resolved) &&
                    resolved != null)
                {
                    // Concrete resolution (e.g., Self.Element → GRDB.Statement)
                    // or generic forwarding (e.g., Self.Element → τ_0_0).
                    // Chained references (AssociatedTypeReferenceSpec) fall through
                    // to unchanged → AnyType downstream.
                    if (resolved is not AssociatedTypeReferenceSpec)
                        return resolved;
                }

                // If no matching generic parameter or graph entry found, return
                // unchanged so downstream gates reject the unresolvable Self.X reference.
                // Do NOT silently fall back to GenericParameters[0] — that could
                // produce a valid-but-wrong signature for protocols with multiple
                // or differently-named associated types.
            }

            // Recurse into generic parameters: Observable<Self.Element> → Observable<τ_0_0>
            if (namedType.ContainsGenericParameters)
            {
                var resolvedGenericParams = namedType.GenericParameters
                    .Select(gp => ResolveSelfElement(gp, conformingType, conformanceGraph, protocolQualifiedName))
                    .ToList();

                // Check if any were actually resolved
                bool changed = false;
                for (int i = 0; i < resolvedGenericParams.Count; i++)
                {
                    if (!ReferenceEquals(resolvedGenericParams[i], namedType.GenericParameters[i]))
                    {
                        changed = true;
                        break;
                    }
                }

                if (changed)
                {
                    return new NamedTypeSpec(namedType.Name, resolvedGenericParams.ToArray());
                }
            }
        }

        // Recurse into closure arguments and return type
        if (typeSpec is ClosureTypeSpec closureType)
        {
            bool changed = false;

            // Resolve closure arguments
            TypeSpec resolvedArgs;
            if (closureType.HasArguments())
            {
                resolvedArgs = ResolveSelfElement(closureType.Arguments, conformingType, conformanceGraph, protocolQualifiedName);
                if (!ReferenceEquals(resolvedArgs, closureType.Arguments))
                    changed = true;
            }
            else
            {
                resolvedArgs = closureType.Arguments;
            }

            // Resolve closure return type
            var resolvedReturn = ResolveSelfElement(closureType.ReturnType, conformingType, conformanceGraph, protocolQualifiedName);
            if (!ReferenceEquals(resolvedReturn, closureType.ReturnType))
                changed = true;

            if (changed)
            {
                return new ClosureTypeSpec(resolvedArgs, resolvedReturn)
                {
                    Throws = closureType.Throws,
                    IsAsync = closureType.IsAsync,
                };
            }
        }

        return typeSpec;
    }

    /// <summary>
    /// Emits one <c>@available({Platform} {Version}, *)</c> line per platform for the conforming
    /// type's ancestor chain so the top-level <c>@_silgen_name</c> wrapper can reference
    /// availability-gated types and constraints. Mirrors the @_cdecl path which already runs
    /// availability through <see cref="WrapperEmitterHelpers.EmitSwiftAvailability"/>.
    /// <paramref name="extraAvailability"/> carries the @available floor of the protocol whose
    /// extension supplied the wrapper body — without it, a wrapper for an iOS-13 conforming
    /// type calling an iOS-18 protocol-extension method (e.g. RealityFoundation
    /// Entity.ChildCollection.insert(_:beforeIndex:) inherited from EntityCollection at iOS 18)
    /// would carry only the conforming type's lower floor and fail wrapper compile.
    /// </summary>
    private static void EmitProtocolExtAvailabilityLines(
        TypeDecl conformingType,
        ModuleEmissionContext ctx,
        IReadOnlyList<AvailabilityAnnotation>? extraAvailability = null)
    {
        var availability = WrapperEmitterHelpers.MergeAvailability(memberAnnotations: extraAvailability, parentDecl: conformingType);
        foreach (var key in WrapperEmitterHelpers.CollectStrictestAvailabilityKeys(availability))
        {
            ctx.AddProtocolExtWrapperLine($"@available({key}, *)");
        }
    }

    /// <summary>
    /// Resolves a protocol TypeDecl from <paramref name="moduleDecl"/> by qualified name
    /// (e.g. "RealityFoundation.EntityCollection") and returns its @available annotations.
    /// Returns null when the protocol isn't owned by this module — protocol extensions
    /// from foreign modules are out of scope here.
    /// </summary>
    private static IReadOnlyList<AvailabilityAnnotation>? LookupProtocolAvailability(
        ModuleDecl moduleDecl, string protocolQualifiedName)
    {
        var dotIdx = protocolQualifiedName.LastIndexOf('.');
        var unqualifiedName = dotIdx >= 0 ? protocolQualifiedName.Substring(dotIdx + 1) : protocolQualifiedName;
        return FindProtocol(moduleDecl.Types, unqualifiedName)?.AvailabilityAnnotations;
    }

    private static ProtocolDecl? FindProtocol(IEnumerable<TypeDecl> types, string unqualifiedName)
    {
        foreach (var type in types)
        {
            if (type is ProtocolDecl pd && pd.Name == unqualifiedName) return pd;
            var nested = FindProtocol(type.Types, unqualifiedName);
            if (nested != null) return nested;
        }
        return null;
    }

    /// <summary>
    /// Builds a unique symbol name for the Swift wrapper function.
    /// Format: <c>{prefix}{FlatTypeName}_{methodName}[_{label1}_{label2}_...]</c>.
    /// Uses parameter labels (like Swift's PrintedName) for precise overload disambiguation.
    /// <para>
    /// Prefix is <c>SBW_</c> when the wrapper is emitted as <c>@_cdecl</c> (Cdecl P/Invoke)
    /// and <c>SBSW_</c> when it must remain <c>@_silgen_name</c> (Swift CC P/Invoke — used
    /// for generic conforming types and method-level generics where <c>@_cdecl</c> is illegal).
    /// The distinct prefix keeps the (entry-point → calling-convention) pairing self-describing
    /// for <see cref="PInvokeEmitHelper.SelectCallingConvention"/>'s audit.
    /// </para>
    /// </summary>
    /// <summary>
    /// Cross-emitter structural identity for a protocol-extension method. Pairs
    /// the originating protocol's qualified name with the full raw signature so
    /// two emitters reaching the same underlying Swift function arrive at the
    /// same key independent of the rendered C symbol — and two overloads with
    /// identical external labels but different parameter types stay distinct.
    /// <see cref="ProtocolExtensionMethodDecl.PrintedName"/> alone is label-only
    /// (e.g. <c>step(_:)</c>), so a <c>step(Bool)</c> / <c>step(Int32)</c>
    /// overload pair collapses to one key and the second overload's wrapper
    /// gets silently dropped. <see cref="ProtocolExtensionMethodDecl.RawSignature"/>
    /// preserves the parameter and return types verbatim, which is enough to
    /// disambiguate every overload the swiftinterface parser produces. Stashed
    /// on the synthetic <see cref="MethodDecl.WrapperSourceKey"/> so a downstream
    /// <see cref="MethodWrapperEmitter"/> pass uses the same identity.
    /// </summary>
    private static string BuildSourceKey(ProtocolExtensionMethodDecl extMethod) =>
        $"{extMethod.ProtocolQualifiedName}::{extMethod.PrintedName}::{extMethod.RawSignature}";

    private static string BuildSymbolName(string flatTypeName, string methodName,
        List<(string label, TypeSpec typeSpec, string swiftType)> parameters,
        bool useCdecl = true)
    {
        var prefix = useCdecl ? "SBW_" : "SBSW_";
        var baseName = $"{prefix}{flatTypeName}_{methodName}";
        if (parameters.Count > 0)
        {
            // Use parameter labels for disambiguation (mirrors Swift's PrintedName semantics)
            var labels = string.Join("_", parameters.Select(p =>
            {
                var label = p.label == "_" ? "" : p.label;
                // Also append a short type suffix for same-label overloads
                var typeSpec = p.typeSpec;
                string typeSuffix;
                if (typeSpec is NamedTypeSpec named)
                    typeSuffix = named.Name.Substring(named.Name.LastIndexOf('.') + 1);
                else if (typeSpec is ClosureTypeSpec)
                    typeSuffix = "closure";
                else
                    typeSuffix = "";
                return string.IsNullOrEmpty(label) ? typeSuffix : $"{label}{typeSuffix}";
            }));
            baseName += $"_{labels}";
        }
        return baseName;
    }

    /// <summary>
    /// Reconstructs a PrintedName-like key from a MethodDecl for collision checking.
    /// Format: "methodName(label1:label2:)" matching Swift's PrintedName convention.
    /// </summary>
    internal static string BuildMethodKey(MethodDecl method)
    {
        // CSSignature[0] is the return type, params start at [1]
        if (method.CSSignature.Count <= 1)
            return $"{method.Name}()";

        var labels = string.Join("", method.CSSignature.Skip(1).Select(arg =>
        {
            var label = string.IsNullOrEmpty(arg.Name) || arg.Name == "_" ? "_" : arg.Name;
            return $"{label}:";
        }));
        return $"{method.Name}({labels})";
    }

    /// <summary>
    /// Overload-aware variant of <see cref="BuildMethodKey"/> that pairs each label
    /// with the parameter's printed Swift type name so genuine Swift overloads sharing
    /// the same external labels (e.g. <c>step(_:Bool)</c> and <c>step(_:Int32)</c>)
    /// produce distinct keys. Used by the early collision gate in
    /// <see cref="TryInjectMethod"/>; the projected C# signature check just below
    /// stays as the authoritative "would-this-shadow-a-C#-method" arbiter.
    /// </summary>
    internal static string BuildOverloadAwareMethodKey(MethodDecl method)
    {
        if (method.CSSignature.Count <= 1)
            return $"{method.Name}()";

        var labelTypes = string.Join(",", method.CSSignature.Skip(1).Select(arg =>
        {
            var label = string.IsNullOrEmpty(arg.Name) || arg.Name == "_" ? "_" : arg.Name;
            var typeName = arg.SwiftTypeSpec?.ToString() ?? string.Empty;
            return $"{label}:{typeName}";
        }));
        return $"{method.Name}({labelTypes})";
    }

    /// <summary>
    /// Mirror of <see cref="BuildOverloadAwareMethodKey"/> for a parsed protocol-extension
    /// method's parameter list. Same key shape so the two sides can be compared in a
    /// single HashSet lookup.
    /// </summary>
    private static string BuildOverloadAwareExtensionKey(
        string methodName,
        List<(string label, TypeSpec typeSpec, string swiftType)> parameters)
    {
        if (parameters.Count == 0)
            return $"{methodName}()";

        var labelTypes = string.Join(",", parameters.Select(p =>
        {
            var label = string.IsNullOrEmpty(p.label) || p.label == "_" ? "_" : p.label;
            return $"{label}:{p.typeSpec}";
        }));
        return $"{methodName}({labelTypes})";
    }

    /// <summary>
    /// Flattens a SwiftTypeName for use in symbol names.
    /// e.g., "Kingfisher.KF.Builder" → "KF_Builder"
    /// </summary>
    private static string FlattenTypeName(SwiftTypeName swiftTypeName)
    {
        // Use ModuleQualifiedName but strip module prefix and replace dots with underscores
        var name = swiftTypeName.ModuleQualifiedName;
        var dotIdx = name.IndexOf('.');
        if (dotIdx >= 0)
            name = name.Substring(dotIdx + 1);
        return name.Replace(".", "_");
    }

    /// <summary>
    /// Extracts a reasonable parameter name from a Swift type string.
    /// e.g., "Kingfisher.ImageCache" → "cache", "Swift.Bool" → "enabled"
    /// </summary>
    private static string GetParamNameFromType(string swiftType)
    {
        var trimmed = swiftType.Trim();

        // Array types: [Element] or [any Module.Type] → "items"
        if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
            return "items";

        // Closure/function types: (...) -> ... → "closure"
        if (trimmed.StartsWith("(", StringComparison.Ordinal) || trimmed.Contains("->"))
            return "closure";

        // Strip "any " prefix from existential types
        var cleaned = trimmed.StartsWith("any ", StringComparison.Ordinal) ? trimmed.Substring(4) : trimmed;

        // Strip generic parameters (e.g., "RetryStrategy<T>" → "RetryStrategy")
        var angleIdx = cleaned.IndexOf('<');
        if (angleIdx >= 0)
            cleaned = cleaned.Substring(0, angleIdx);

        var dotIdx = cleaned.LastIndexOf('.');
        var typeName = dotIdx >= 0 ? cleaned.Substring(dotIdx + 1) : cleaned;

        if (typeName == "Bool") return "enabled";
        if (typeName == "Int" || typeName == "Int32" || typeName == "Int64") return "value";
        if (typeName == "Float" || typeName == "Double" || typeName == "CGFloat") return "value";
        if (typeName == "String") return "str";

        // Lowercase first character and sanitize — strip any type-syntax chars (brackets, parens, etc.)
        string result;
        if (typeName.Length > 0)
            result = char.ToLowerInvariant(typeName[0]) + typeName.Substring(1);
        else
            return "arg";

        result = SwiftBuilder.SanitizeIdentifier(result);
        return string.IsNullOrEmpty(result) ? "arg" : result;
    }

    /// <summary>
    /// Derives a reasonable C# parameter name for a closure parameter.
    /// ClosureTypeSpec.ToString() produces the full closure type (e.g., "(Element) throws -> Bool")
    /// which can't be used as a parameter name — use closure shape to pick a standard name.
    /// </summary>
    private static string GetClosureParamName(ClosureTypeSpec closure)
    {
        if (closure.ReturnType is NamedTypeSpec retNamed && retNamed.Name == "Swift.Bool")
            return "predicate";
        if (closure.ReturnType.IsEmptyTuple)
            return "handler";
        return "transform";
    }

    /// <summary>
    /// Computes deduplicated Swift parameter names for the wrapper.
    /// Two parameters both labelled `_` whose types share a leaf name (e.g. both
    /// `Expression`) would otherwise produce identical internal names like
    /// `expression`, which swiftc rejects as "invalid redeclaration". Duplicates are
    /// suffixed with a numeric index: expression, expression2, expression3, ...
    /// </summary>
    /// <param name="parameters">The method's parameter list.</param>
    /// <param name="closureParamIndex">
    /// Index of a closure parameter that uses GetClosureParamName instead of
    /// GetParamNameFromType, or -1 if there is no closure replacement.
    /// </param>
    private static List<string> ComputeUniqueParamNames(
        List<(string label, TypeSpec typeSpec, string swiftType)> parameters,
        ClosureTypeSpec? closureTypeSpec = null,
        int closureParamIndex = -1)
    {
        var names = new List<string>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < parameters.Count; i++)
        {
            var (label, _, swiftType) = parameters[i];
            string baseName;
            if (i == closureParamIndex && closureTypeSpec != null)
            {
                baseName = SanitizeSwiftParamName(label == "_" ? GetClosureParamName(closureTypeSpec) : label);
            }
            else
            {
                baseName = SanitizeSwiftParamName(label == "_" ? GetParamNameFromType(swiftType) : label);
            }
            if (seen.TryGetValue(baseName, out var count))
            {
                seen[baseName] = count + 1;
                names.Add($"{baseName}{count + 1}");
            }
            else
            {
                seen[baseName] = 1;
                names.Add(baseName);
            }
        }
        return names;
    }

    /// <summary>
    /// Sanitizes a Swift parameter name to avoid keyword conflicts.
    /// </summary>
    private static string SanitizeSwiftParamName(string name)
    {
        // Avoid Swift keywords
        return name switch
        {
            "self" => "self_",
            "class" => "class_",
            "struct" => "struct_",
            "enum" => "enum_",
            "protocol" => "protocol_",
            "func" => "func_",
            "return" => "return_",
            "import" => "import_",
            "let" => "let_",
            "var" => "var_",
            "in" => "in_",
            "for" => "for_",
            "if" => "if_",
            "else" => "else_",
            "while" => "while_",
            "switch" => "switch_",
            "case" => "case_",
            "default" => "default_",
            "where" => "where_",
            "guard" => "guard_",
            "throw" => "throw_",
            "try" => "try_",
            "catch" => "catch_",
            "as" => "as_",
            "is" => "is_",
            "true" => "true_",
            "false" => "false_",
            "nil" => "nil_",
            _ => name,
        };
    }

    /// <summary>
    /// Splits a parameter list string by commas, respecting nested angle brackets,
    /// parentheses, and square brackets.
    /// </summary>
    private static List<string> SplitParameters(string paramStr)
    {
        var result = new List<string>();
        int depth = 0;
        int start = 0;

        for (int i = 0; i < paramStr.Length; i++)
        {
            char c = paramStr[i];
            if (c == '<' || c == '(' || c == '[') depth++;
            if (c == '>' || c == ')' || c == ']') depth--;
            if (c == ',' && depth == 0)
            {
                result.Add(paramStr.Substring(start, i - start));
                start = i + 1;
            }
        }
        result.Add(paramStr.Substring(start));
        return result;
    }
}
