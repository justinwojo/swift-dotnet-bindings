// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration.ObjC;

public static class ApiDefinitionEmitter
{
    public static string Emit(ObjCModule module, string outputDir, string resolvedNamespace, ILogger logger, ObjCBindingDiagnostics? diagnostics = null, PlatformInfo? platformInfo = null)
    {
        var typedefMap = ObjCTypeMapper.BuildResolvedTypedefMap(module);
        var blockTypedefMap = ObjCTypeMapper.BuildBlockTypedefMap(module);

        // Build known types for source-aware type resolvability.
        // Types not in this set AND not in Apple SDK type names will be skipped.
        var knownTypes = ObjCTypeMapper.BuildKnownMappedTypes();
        foreach (var e in module.Enums) knownTypes.Add(e.Name);
        foreach (var s in module.Structs) knownTypes.Add(s.Name);
        foreach (var cls in module.Classes)
        {
            knownTypes.Add(cls.Name);
            knownTypes.Add(ObjCTypeMapper.MapClassName(cls.Name));
        }
        foreach (var proto in module.Protocols)
        {
            knownTypes.Add(proto.Name);
            knownTypes.Add($"I{proto.Name}");
            knownTypes.Add($"I{ObjCTypeMapper.MapProtocolName(proto.Name)}");
        }
        var appleSdkTypes = module.AppleSdkTypeNames;

        // Build set of delegate protocol names for WeakDelegate/Wrap pattern emission
        var delegateProtocolNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var proto in module.Protocols)
        {
            if (proto.IsDelegateProtocol)
                delegateProtocolNames.Add(proto.Name);
        }

        // Build set of enum names for out-param detection (enum pointer → out T)
        var enumNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in module.Enums)
            enumNames.Add(e.Name);

        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using AuthenticationServices;");
        sb.AppendLine("using AVFoundation;");
        sb.AppendLine("using BackgroundAssets;");
        sb.AppendLine("using CoreAnimation;");
        sb.AppendLine("using CoreFoundation;");
        sb.AppendLine("using CoreImage;");
        sb.AppendLine("using CoreLocation;");
        sb.AppendLine("using CoreMedia;");
        sb.AppendLine("using Foundation;");
        sb.AppendLine("using ImageIO;");
        sb.AppendLine("using MapKit;");
        sb.AppendLine("using Metal;");
        sb.AppendLine("using ObjCRuntime;");
        sb.AppendLine("using CoreGraphics;");
        sb.AppendLine("using UIKit;");
        sb.AppendLine("using UserNotifications;");
        sb.AppendLine("using WebKit;");
        sb.AppendLine();
        sb.AppendLine($"namespace {resolvedNamespace}");
        sb.AppendLine("{");

        foreach (var proto in module.Protocols)
            EmitProtocol(sb, proto, typedefMap, blockTypedefMap, knownTypes, appleSdkTypes, enumNames, logger, diagnostics, platformInfo);

        foreach (var cls in module.Classes)
            EmitClass(sb, cls, typedefMap, blockTypedefMap, knownTypes, appleSdkTypes, delegateProtocolNames, enumNames, logger, diagnostics, platformInfo);

        foreach (var cat in module.Categories)
            EmitCategory(sb, cat, typedefMap, blockTypedefMap, knownTypes, appleSdkTypes, enumNames, logger, diagnostics, platformInfo);

        sb.AppendLine("}");

        // Post-process: for [Model] delegate protocols, the type mapper emits I-prefixed references
        // (e.g., IFIRMessagingDelegate) but [Protocol, Model] interfaces use bare names.
        // bgen generates both IFoo (interface) and Foo (class), so references should use the bare
        // name (class type) for [Model] protocols to match the Xamarin convention.
        var result = sb.ToString();
        foreach (var dpName in delegateProtocolNames)
        {
            var mappedName = ObjCTypeMapper.MapProtocolName(dpName);
            // Replace I-prefixed references with bare name in type positions
            // Use word boundary to avoid replacing substrings (e.g., IFoo in IFooBar)
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                $@"\bI{System.Text.RegularExpressions.Regex.Escape(mappedName)}\b",
                mappedName);
        }

        Directory.CreateDirectory(outputDir);
        var filePath = Path.Combine(outputDir, "ApiDefinition.cs");
        File.WriteAllText(filePath, result);

        logger.LogInformation("Wrote {FilePath}", filePath);
        return filePath;
    }

    static void EmitProtocol(StringBuilder sb, ObjCProtocolDecl proto, Dictionary<string, ObjCTypeRef> typedefMap, Dictionary<string, ObjCTypeRef> blockTypedefMap, HashSet<string> knownTypes, HashSet<string>? appleSdkTypes, HashSet<string>? enumNames, ILogger logger, ObjCBindingDiagnostics? diagnostics, PlatformInfo? platformInfo = null)
    {
        if (EmitAvailabilityAttributes(sb, proto.Availability, "    ", platformInfo))
        {
            diagnostics?.RecordSkip("Protocol", proto.Name, ObjCSkipReason.UnavailableApi, "marked unavailable on iOS");
            return;
        }
        ObjCDocCommentEmitter.EmitDocComment(sb, proto.DocComment, null, "    ");

        // Delegate/data-source protocols get [Model] attribute.
        // With [Model], the Xamarin convention uses the bare protocol name (not I-prefixed).
        if (proto.IsDelegateProtocol)
        {
            sb.AppendLine("    [Protocol, Model]");
        }
        else
        {
            sb.AppendLine("    [Protocol]");
        }
        sb.AppendLine("    [BaseType(typeof(NSObject))]");

        // Filter out implicit protocols from inheritance — NSObject is implicit in .NET MAUI bindings,
        // NSFastEnumeration maps to IEnumerable but isn't a binding interface
        var filteredInherited = proto.InheritedProtocolNames
            .Where(n => n != "NSObject" && n != "NSFastEnumeration")
            .ToList();
        var inheritList = filteredInherited.Count > 0
            ? $" : {string.Join(", ", filteredInherited.Select(n => $"I{ObjCTypeMapper.MapProtocolName(n)}"))}"
            : "";

        // [Model] protocols use bare name (Xamarin convention); non-[Model] use I prefix
        var interfaceName = proto.IsDelegateProtocol ? proto.Name : $"I{proto.Name}";
        sb.AppendLine($"    partial interface {interfaceName}{inheritList}");
        sb.AppendLine("    {");

        // Protocols don't declare ObjC lightweight generics — only pass the common fallback set
        var emittedMethodSignatures = new HashSet<string>();
        var emittedMemberNames = new HashSet<string>();
        foreach (var method in proto.Methods)
        {
            var emittedName = EmitMethod(sb, method, declaringClassName: null, isProtocol: true, genericTypeParams: null, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, emittedMethodSignatures: emittedMethodSignatures, knownTypes: knownTypes, appleSdkTypes: appleSdkTypes, enumNames: enumNames, isDelegateProtocol: proto.IsDelegateProtocol, logger: logger, diagnostics: diagnostics, platformInfo: platformInfo);
            if (emittedName != null) emittedMemberNames.Add(emittedName);
        }

        foreach (var prop in proto.Properties)
            EmitProperty(sb, prop, declaringClassName: null, genericTypeParams: null, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, emittedPropertyNames: emittedMemberNames, knownTypes: knownTypes, appleSdkTypes: appleSdkTypes, logger: logger, diagnostics: diagnostics, platformInfo: platformInfo);

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static void EmitClass(StringBuilder sb, ObjCClassDecl cls, Dictionary<string, ObjCTypeRef> typedefMap, Dictionary<string, ObjCTypeRef> blockTypedefMap, HashSet<string> knownTypes, HashSet<string>? appleSdkTypes, HashSet<string>? delegateProtocolNames, HashSet<string>? enumNames, ILogger logger, ObjCBindingDiagnostics? diagnostics, PlatformInfo? platformInfo = null)
    {
        if (EmitAvailabilityAttributes(sb, cls.Availability, "    ", platformInfo))
        {
            diagnostics?.RecordSkip("Class", cls.Name, ObjCSkipReason.UnavailableApi, "marked unavailable on iOS");
            return;
        }
        ObjCDocCommentEmitter.EmitDocComment(sb, cls.DocComment, null, "    ");

        // Disable default constructor if the class declares any parameterless init
        // to avoid bgen generating a duplicate parameterless constructor.
        // When DisableDefaultCtor is set, we also suppress the explicit init constructor
        // to avoid contradicting attributes (Fix #6).
        var hasExplicitParameterlessInit = cls.Methods.Any(m =>
            m.Selector == "init" && m.Parameters.Count == 0);
        var hasParameterlessInitWith = cls.Methods.Any(m =>
            m.Selector.StartsWith("initWith", StringComparison.Ordinal)
            && m.Parameters.Count == 0);
        var disableDefaultCtor = hasExplicitParameterlessInit || hasParameterlessInitWith;
        if (disableDefaultCtor)
            sb.AppendLine("    [DisableDefaultCtor]");

        var baseType = ObjCTypeMapper.MapClassName(cls.SuperclassName ?? "NSObject");
        sb.AppendLine($"    [BaseType(typeof({baseType}))]");

        var filteredProtocols = cls.ProtocolNames
            .Where(n => n != "NSObject" && n != "NSFastEnumeration")
            .ToList();
        var protocols = filteredProtocols.Count > 0
            ? $" : {string.Join(", ", filteredProtocols.Select(n => $"I{ObjCTypeMapper.MapProtocolName(n)}"))}"
            : "";
        sb.AppendLine($"    partial interface {cls.Name}{protocols}");
        sb.AppendLine("    {");

        // Scope generic type params to THIS class only — avoids cross-type collisions
        // where one class's generic param name matches a real type used elsewhere.
        var classGenericParams = cls.GenericTypeParamNames.Count > 0
            ? new HashSet<string>(cls.GenericTypeParamNames)
            : null;

        // bgen auto-generates initWithCoder: for classes conforming to NSCoding/NSSecureCoding.
        // Skip our explicit emission to avoid CS0111 duplicate constructor.
        var conformsToNSCoding = cls.ProtocolNames.Any(p =>
            p is "NSCoding" or "NSSecureCoding");

        // Track emitted signatures to detect duplicates (constructors, methods, properties)
        var emittedConstructorSignatures = new HashSet<string>();
        var emittedMethodSignatures = new HashSet<string>();
        var emittedMemberNames = new HashSet<string>();

        foreach (var method in cls.Methods.Where(m =>
            !(conformsToNSCoding && m.Selector == "initWithCoder:")
            // Suppress explicit parameterless init when DisableDefaultCtor is emitted (Fix #6)
            && !(disableDefaultCtor && m.Selector == "init" && m.Parameters.Count == 0)))
        {
            var emittedName = EmitMethod(sb, method, declaringClassName: cls.Name, isProtocol: false, genericTypeParams: classGenericParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, emittedConstructorSignatures: emittedConstructorSignatures, emittedMethodSignatures: emittedMethodSignatures, knownTypes: knownTypes, appleSdkTypes: appleSdkTypes, enumNames: enumNames, logger: logger, diagnostics: diagnostics, platformInfo: platformInfo);
            if (emittedName != null) emittedMemberNames.Add(emittedName);
        }

        // Emit properties, with WeakDelegate/Wrap pattern for delegate properties (Fix #8)
        foreach (var prop in cls.Properties)
        {
            if (IsDelegateProperty(prop, delegateProtocolNames))
            {
                EmitWeakDelegatePattern(sb, prop, delegateProtocolNames, emittedMemberNames, platformInfo);
            }
            else
            {
                EmitProperty(sb, prop, declaringClassName: cls.Name, genericTypeParams: classGenericParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, emittedPropertyNames: emittedMemberNames, knownTypes: knownTypes, appleSdkTypes: appleSdkTypes, logger: logger, diagnostics: diagnostics, platformInfo: platformInfo);
            }
        }

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static void EmitCategory(StringBuilder sb, ObjCCategoryDecl cat, Dictionary<string, ObjCTypeRef> typedefMap, Dictionary<string, ObjCTypeRef> blockTypedefMap, HashSet<string> knownTypes, HashSet<string>? appleSdkTypes, HashSet<string>? enumNames, ILogger logger, ObjCBindingDiagnostics? diagnostics, PlatformInfo? platformInfo = null)
    {
        if (EmitAvailabilityAttributes(sb, cat.Availability, "    ", platformInfo))
        {
            diagnostics?.RecordSkip("Category", $"{cat.ClassName}.{cat.CategoryName}", ObjCSkipReason.UnavailableApi, "marked unavailable on iOS");
            return;
        }

        // MAUI bgen compiles [Category] interfaces into static extension classes.
        // Constraints: static classes cannot implement interfaces (CS0714) and
        // cannot have instance properties (CS0708). Only instance methods and
        // class (static) properties are valid members.
        // Filter out init methods — MAUI category interfaces cannot declare constructors.
        var emittableMethods = cat.Methods
            .Where(m => m.Selector != "init" && !m.Selector.StartsWith("initWith", StringComparison.Ordinal))
            .ToList();
        var emittableClassProperties = cat.Properties.Where(p => p.IsClass).ToList();

        // Skip category entirely if it has no emittable content
        if (emittableMethods.Count == 0 && emittableClassProperties.Count == 0)
        {
            diagnostics?.RecordSkip("Category", $"{cat.ClassName}.{cat.CategoryName}", ObjCSkipReason.EmptyCategory,
                cat.ProtocolNames.Count > 0
                    ? $"protocol-only category ({string.Join(", ", cat.ProtocolNames)}) — static classes cannot implement interfaces"
                    : "no emittable members (instance properties not supported in static extension classes)");
            return;
        }

        sb.AppendLine("    [Category]");
        sb.AppendLine($"    [BaseType(typeof({cat.ClassName}))]");

        // Strip protocol conformance — static classes cannot implement interfaces (CS0714)
        var interfaceName = GenerateCategoryInterfaceName(cat.ClassName, cat.CategoryName);
        sb.AppendLine($"    partial interface {interfaceName}");
        sb.AppendLine("    {");

        var categoryGenericParams = cat.GenericTypeParamNames.Count > 0
            ? new HashSet<string>(cat.GenericTypeParamNames)
            : null;

        var emittedMethodSignatures = new HashSet<string>();
        var emittedMemberNames = new HashSet<string>();

        foreach (var method in emittableMethods)
        {
            var emittedName = EmitMethod(sb, method, declaringClassName: cat.ClassName, isProtocol: false, genericTypeParams: categoryGenericParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, emittedMethodSignatures: emittedMethodSignatures, knownTypes: knownTypes, appleSdkTypes: appleSdkTypes, enumNames: enumNames, logger: logger, diagnostics: diagnostics, platformInfo: platformInfo);
            if (emittedName != null) emittedMemberNames.Add(emittedName);
        }

        // Skip instance properties — static classes cannot have instance members (CS0708).
        // Only emit [Static] properties (class methods/properties).
        foreach (var prop in cat.Properties.Where(p => p.IsClass))
            EmitProperty(sb, prop, declaringClassName: cat.ClassName, genericTypeParams: categoryGenericParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, emittedPropertyNames: emittedMemberNames, knownTypes: knownTypes, appleSdkTypes: appleSdkTypes, logger: logger, diagnostics: diagnostics, platformInfo: platformInfo);

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    internal static string GenerateCategoryInterfaceName(string className, string categoryName)
    {
        return string.IsNullOrEmpty(categoryName)
            ? $"{className}_Extensions"
            : $"{className}_{categoryName}";
    }

    /// <summary>
    /// Emits a method and returns the final emitted C# method name (after any dedup renaming),
    /// or null for constructors. Callers use this to track method-property name collisions.
    /// </summary>
    static string? EmitMethod(StringBuilder sb, ObjCMethodDecl method, string? declaringClassName, bool isProtocol, HashSet<string>? genericTypeParams, Dictionary<string, ObjCTypeRef>? typedefMap = null, Dictionary<string, ObjCTypeRef>? blockTypedefMap = null, HashSet<string>? emittedConstructorSignatures = null, HashSet<string>? emittedMethodSignatures = null, HashSet<string>? knownTypes = null, HashSet<string>? appleSdkTypes = null, HashSet<string>? enumNames = null, bool isDelegateProtocol = false, ILogger? logger = null, ObjCBindingDiagnostics? diagnostics = null, PlatformInfo? platformInfo = null)
    {
        // Pre-check: skip methods with types not resolvable in ApiDefinition context.
        if (knownTypes != null)
        {
            var checkReturn = ObjCTypeMapper.MapType(method.ReturnType, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap);
            if (!ObjCTypeMapper.IsApiDefinitionTypeResolvable(checkReturn, knownTypes, appleSdkTypes))
            {
                logger?.LogDebug("Skipping method {Selector}: unresolvable return type '{TypeName}'", method.Selector, checkReturn);
                diagnostics?.RecordSkip("Method", method.Selector, ObjCSkipReason.UnresolvableType, $"unresolvable return type '{checkReturn}'");
                return null;
            }
            foreach (var param in method.Parameters)
            {
                var checkParam = ObjCTypeMapper.MapType(param.Type, genericTypeParams: genericTypeParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap);
                if (!ObjCTypeMapper.IsApiDefinitionTypeResolvable(checkParam, knownTypes, appleSdkTypes))
                {
                    logger?.LogDebug("Skipping method {Selector}: unresolvable param type '{TypeName}'", method.Selector, checkParam);
                    diagnostics?.RecordSkip("Method", method.Selector, ObjCSkipReason.UnresolvableType, $"unresolvable param type '{checkParam}'");
                    return null;
                }
            }
        }

        if (EmitAvailabilityAttributes(sb, method.Availability, "        ", platformInfo))
        {
            diagnostics?.RecordSkip("Method", method.Selector, ObjCSkipReason.UnavailableApi, "marked unavailable on iOS");
            return null;
        }
        ObjCDocCommentEmitter.EmitDocComment(sb, method.DocComment, method.DocParams, "        ");

        var isConstructor = !isProtocol && (method.Selector == "init" || method.Selector.StartsWith("initWith", StringComparison.Ordinal));

        // Duplicate constructor detection: if the parameter signature has already been emitted,
        // emit this one as a named instance method instead
        if (isConstructor && emittedConstructorSignatures != null)
        {
            var paramSignature = string.Join(",", method.Parameters.Select(p => ObjCTypeMapper.MapType(p.Type, genericTypeParams: genericTypeParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap)));
            if (!emittedConstructorSignatures.Add(paramSignature))
                isConstructor = false; // Duplicate — emit as named method
        }

        if (isProtocol && !method.IsOptional)
            sb.AppendLine("        [Abstract]");

        if (method.IsVariadic)
            sb.AppendLine("        [Internal]");

        if (!method.IsInstanceMethod && !isConstructor)
            sb.AppendLine("        [Static]");

        if (isConstructor && method.IsDesignatedInitializer)
            sb.AppendLine("        [DesignatedInitializer]");

        if (method.IsVariadic)
            sb.AppendLine($"        [Export(\"{method.Selector}\", IsVariadic = true)]");
        else
            sb.AppendLine($"        [Export(\"{method.Selector}\")]");

        var returnType = isConstructor
            ? "NativeHandle"
            : ObjCTypeMapper.MapType(method.ReturnType, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap);

        if (!isConstructor && ObjCTypeMapper.IsNullableAttribute(method.ReturnType))
            sb.AppendLine("        [return: NullAllowed]");

        var methodName = isConstructor
            ? "Constructor"
            : isDelegateProtocol ? SelectorToDelegateMethodName(method.Selector) : SelectorToMethodName(method.Selector);

        // Duplicate method signature detection: rename with full selector parts if collision
        if (!isConstructor && emittedMethodSignatures != null)
        {
            var paramSignature = string.Join(",", method.Parameters.Select(p => ObjCTypeMapper.MapType(p.Type, genericTypeParams: genericTypeParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap)));
            // Include the variadic IntPtr param in the signature to detect collisions
            // with explicit args: variants (e.g., objectsWhere: + objectsWhere:args:)
            if (method.IsVariadic)
                paramSignature = paramSignature.Length > 0 ? $"{paramSignature},IntPtr" : "IntPtr";
            var methodSig = $"{methodName}({paramSignature})";
            if (!emittedMethodSignatures.Add(methodSig))
            {
                methodName = SelectorToFullMethodName(method.Selector);
                // Register the renamed signature; if it still collides (e.g., a method
                // already exists with the full selector form), append numeric suffix
                var renamedSig = $"{methodName}({paramSignature})";
                if (!emittedMethodSignatures.Add(renamedSig))
                {
                    var suffix = 2;
                    while (!emittedMethodSignatures.Add($"{methodName}{suffix}({paramSignature})"))
                        suffix++;
                    methodName = $"{methodName}{suffix}";
                }
            }
        }

        // Emit generic type hints as remarks
        EmitGenericTypeHints(sb, method.ReturnType, method.Parameters, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap);

        var parameters = EmitParameters(method.Parameters, genericTypeParams, typedefMap, blockTypedefMap, enumNames);
        if (method.IsVariadic)
        {
            // Variadic methods get an IntPtr varArgs parameter for the variable arguments
            if (parameters.Length > 0)
                parameters += ", ";
            parameters += "IntPtr varArgs";
        }
        sb.AppendLine($"        {returnType} {methodName}({parameters});");
        sb.AppendLine();

        return isConstructor ? null : methodName;
    }

    static void EmitProperty(StringBuilder sb, ObjCPropertyDecl prop, string? declaringClassName, HashSet<string>? genericTypeParams, Dictionary<string, ObjCTypeRef>? typedefMap = null, Dictionary<string, ObjCTypeRef>? blockTypedefMap = null, HashSet<string>? emittedPropertyNames = null, HashSet<string>? knownTypes = null, HashSet<string>? appleSdkTypes = null, ILogger? logger = null, ObjCBindingDiagnostics? diagnostics = null, PlatformInfo? platformInfo = null)
    {
        var propName = ToPascalCase(prop.Name);

        // Skip properties with types not resolvable in ApiDefinition context.
        // Check BEFORE dedup tracking so a skipped property doesn't reserve the name.
        if (knownTypes != null)
        {
            var checkType = ObjCTypeMapper.MapType(prop.Type, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap);
            if (!ObjCTypeMapper.IsApiDefinitionTypeResolvable(checkType, knownTypes, appleSdkTypes))
            {
                logger?.LogDebug("Skipping property {PropName}: unresolvable type '{TypeName}'", propName, checkType);
                diagnostics?.RecordSkip("Property", propName, ObjCSkipReason.UnresolvableType, $"unresolvable type '{checkType}'");
                return;
            }
        }

        if (emittedPropertyNames != null && !emittedPropertyNames.Add(propName))
            return;

        if (EmitAvailabilityAttributes(sb, prop.Availability, "        ", platformInfo))
        {
            diagnostics?.RecordSkip("Property", propName, ObjCSkipReason.UnavailableApi, "marked unavailable on iOS");
            return;
        }
        ObjCDocCommentEmitter.EmitDocComment(sb, prop.DocComment, null, "        ");

        if (!prop.IsOptional)
        {
            // Only emit [Abstract] for protocol properties (no declaringClassName)
            // Actually, IsOptional is only set on protocol members, so we need to check context.
            // For protocol properties that are required (not optional), emit [Abstract].
            // We use declaringClassName == null as the protocol indicator.
            if (declaringClassName == null)
                sb.AppendLine("        [Abstract]");
        }

        if (prop.IsClass)
            sb.AppendLine("        [Static]");

        var getterSelector = prop.GetterSelector ?? prop.Name;
        var argSemantic = FormatArgumentSemantic(prop.MemorySemantic);
        sb.AppendLine($"        [Export(\"{getterSelector}\"{argSemantic})]");

        if (ObjCTypeMapper.IsNullableAttribute(prop.Type))
            sb.AppendLine("        [NullAllowed]");

        var propGenericHint = ObjCTypeMapper.FormatGenericTypeHint(prop.Type, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap);
        if (propGenericHint != null)
            sb.AppendLine($"        // {propGenericHint}");

        var mappedType = ObjCTypeMapper.MapType(prop.Type, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap);

        // Emit [Bind] when getter selector differs from property name (e.g., isAutoInitEnabled vs autoInitEnabled)
        var hasCustomGetter = prop.GetterSelector != null && prop.GetterSelector != prop.Name;

        if (prop.IsReadonly)
        {
            if (hasCustomGetter)
            {
                sb.AppendLine($"        {mappedType} {ToPascalCase(prop.Name)} {{");
                sb.AppendLine($"            [Bind(\"{prop.GetterSelector}\")] get;");
                sb.AppendLine($"        }}");
            }
            else
            {
                sb.AppendLine($"        {mappedType} {ToPascalCase(prop.Name)} {{ get; }}");
            }
        }
        else
        {
            // Emit setter with custom selector if present
            var setterSelector = prop.SetterSelector ?? $"set{ToPascalCase(prop.Name)}:";
            sb.AppendLine($"        {mappedType} {ToPascalCase(prop.Name)} {{");
            if (hasCustomGetter)
                sb.AppendLine($"            [Bind(\"{prop.GetterSelector}\")] get;");
            else
                sb.AppendLine($"            get;");
            sb.AppendLine($"            [Export(\"{setterSelector}\")] set;");
            sb.AppendLine($"        }}");
        }
        sb.AppendLine();
    }

    /// <summary>
    /// Checks whether a property references a delegate protocol type and should use
    /// the WeakDelegate/Wrap pattern instead of normal property emission.
    /// </summary>
    static bool IsDelegateProperty(ObjCPropertyDecl prop, HashSet<string>? delegateProtocolNames)
    {
        if (delegateProtocolNames == null || delegateProtocolNames.Count == 0)
            return false;

        // Check protocol-qualified id (e.g., id<WKNavigationDelegate>)
        if (prop.Type.ProtocolQualifications.Count > 0
            && prop.Type.ProtocolQualifications.Any(p => delegateProtocolNames.Contains(p)))
            return true;

        // Check direct protocol name (e.g., WKNavigationDelegate *)
        if (prop.Type.IsPointer && delegateProtocolNames.Contains(prop.Type.Name))
            return true;

        return false;
    }

    /// <summary>
    /// Resolves the protocol type name from a delegate property's type reference.
    /// </summary>
    static string? ResolveDelegateProtocolName(ObjCPropertyDecl prop, HashSet<string>? delegateProtocolNames)
    {
        if (delegateProtocolNames == null) return null;

        if (prop.Type.ProtocolQualifications.Count > 0)
        {
            var match = prop.Type.ProtocolQualifications.FirstOrDefault(p => delegateProtocolNames.Contains(p));
            if (match != null) return match;
        }

        if (prop.Type.IsPointer && delegateProtocolNames.Contains(prop.Type.Name))
            return prop.Type.Name;

        return null;
    }

    /// <summary>
    /// Emits the Xamarin WeakDelegate/Wrap two-property pattern for delegate/dataSource properties.
    /// Preserves the original property's availability, doc comments, static, readonly shape,
    /// and argument semantics.
    /// </summary>
    static void EmitWeakDelegatePattern(StringBuilder sb, ObjCPropertyDecl prop, HashSet<string>? delegateProtocolNames, HashSet<string>? emittedPropertyNames, PlatformInfo? platformInfo = null)
    {
        var protocolName = ResolveDelegateProtocolName(prop, delegateProtocolNames);
        if (protocolName == null) return;

        // Skip unavailable properties (same check as EmitProperty)
        if (EmitAvailabilityAttributes(sb, prop.Availability, "        ", platformInfo))
            return;

        var propName = ToPascalCase(prop.Name);
        var weakPropName = $"Weak{propName}";
        var selector = prop.GetterSelector ?? prop.Name;

        // Track both property names
        emittedPropertyNames?.Add(propName);
        emittedPropertyNames?.Add(weakPropName);

        // Preserve doc comment from original property
        ObjCDocCommentEmitter.EmitDocComment(sb, prop.DocComment, null, "        ");

        // 1. Strong-typed property with [Wrap]
        if (prop.IsClass)
            sb.AppendLine("        [Static]");
        sb.AppendLine($"        [Wrap(\"{weakPropName}\")]");
        sb.AppendLine("        [NullAllowed]");
        if (prop.IsReadonly)
            sb.AppendLine($"        {protocolName} {propName} {{ get; }}");
        else
            sb.AppendLine($"        {protocolName} {propName} {{ get; set; }}");
        sb.AppendLine();

        // 2. Weak NSObject property with [Export]
        // Use the original property's ArgumentSemantic if set, otherwise default to Weak
        var argSemantic = prop.MemorySemantic != ObjCMemorySemantic.None
            ? FormatArgumentSemantic(prop.MemorySemantic)
            : ", ArgumentSemantic.Weak";
        if (prop.IsClass)
            sb.AppendLine("        [Static]");
        sb.AppendLine($"        [NullAllowed, Export(\"{selector}\"{argSemantic})]");
        if (prop.IsReadonly)
        {
            sb.AppendLine($"        NSObject {weakPropName} {{ get; }}");
        }
        else
        {
            var setterSelector = prop.SetterSelector ?? $"set{ToPascalCase(prop.Name)}:";
            sb.AppendLine($"        NSObject {weakPropName} {{");
            sb.AppendLine($"            get;");
            sb.AppendLine($"            [Export(\"{setterSelector}\")] set;");
            sb.AppendLine($"        }}");
        }
        sb.AppendLine();
    }

    static bool EmitAvailabilityAttributes(StringBuilder sb, List<ObjCAvailability> availability, string indent, PlatformInfo? platformInfo = null) =>
        ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, availability, indent, platformInfo);

    static void EmitGenericTypeHints(StringBuilder sb, ObjCTypeRef returnType, List<ObjCParameterDecl> parameters, string? declaringClassName, HashSet<string>? genericTypeParams, Dictionary<string, ObjCTypeRef>? typedefMap, Dictionary<string, ObjCTypeRef>? blockTypedefMap)
    {
        var hints = new List<string>();

        var returnHint = ObjCTypeMapper.FormatGenericTypeHint(returnType, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap);
        if (returnHint != null)
            hints.Add($"Return: {returnHint}");

        foreach (var param in parameters)
        {
            var paramHint = ObjCTypeMapper.FormatGenericTypeHint(param.Type, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap);
            if (paramHint != null)
                hints.Add($"Parameter '{param.Name}': {paramHint}");
        }

        if (hints.Count > 0)
        {
            foreach (var hint in hints)
                sb.AppendLine($"        // {hint}");
        }
    }

    static string EmitParameters(List<ObjCParameterDecl> parameters, HashSet<string>? genericTypeParams, Dictionary<string, ObjCTypeRef>? typedefMap = null, Dictionary<string, ObjCTypeRef>? blockTypedefMap = null, HashSet<string>? enumNames = null)
    {
        var parts = new List<string>();
        foreach (var param in parameters)
        {
            if (ObjCTypeMapper.IsNSErrorOutParameter(param.Type))
            {
                parts.Add("[NullAllowed] out NSError error");
            }
            else if (ObjCTypeMapper.IsValueTypePointerParameter(param.Type, typedefMap, enumNames))
            {
                // Value-type pointer parameters become `out T` (e.g., _Bool * → out bool, CGPoint * → out CGPoint)
                var pointeeType = ObjCTypeMapper.MapValueTypePointerParameterType(param.Type, typedefMap);
                var safeName = EscapeCSharpKeyword(param.Name);
                parts.Add($"out {pointeeType} {safeName}");
            }
            else
            {
                var mappedType = ObjCTypeMapper.MapType(param.Type, genericTypeParams: genericTypeParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap);
                var nullAttr = ObjCTypeMapper.IsNullableAttribute(param.Type)
                    ? "[NullAllowed] "
                    : "";
                var safeName = EscapeCSharpKeyword(param.Name);
                parts.Add($"{nullAttr}{mappedType} {safeName}");
            }
        }
        return string.Join(", ", parts);
    }

    // C# reserved keywords that cannot be used as identifiers without '@' prefix
    static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char",
        "checked", "class", "const", "continue", "decimal", "default", "delegate",
        "do", "double", "else", "enum", "event", "explicit", "extern", "false",
        "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit",
        "in", "int", "interface", "internal", "is", "lock", "long", "namespace",
        "new", "null", "object", "operator", "out", "override", "params", "private",
        "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
        "short", "sizeof", "stackalloc", "static", "string", "struct", "switch",
        "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked",
        "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };

    internal static string EscapeCSharpKeyword(string name) =>
        CSharpKeywords.Contains(name) ? $"@{name}" : name;

    internal static string SelectorToMethodName(string selector)
    {
        // Take text before first ':', PascalCase it
        var colonIndex = selector.IndexOf(':');
        var baseName = colonIndex >= 0 ? selector[..colonIndex] : selector;
        return ToPascalCase(baseName);
    }

    internal static string SelectorToFullMethodName(string selector)
    {
        // Use ALL selector parts, PascalCase each: "setObject:forKey:" → "SetObjectForKey"
        var parts = selector.Split(':', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(ToPascalCase));
    }

    /// <summary>
    /// For delegate protocol methods with multi-part selectors, concatenate all selector
    /// parts after the first (Xamarin convention). The first part is typically the delegate
    /// owner instance name (e.g., "messaging", "tableView", "URLSession"), while subsequent
    /// parts describe the action and context. Examples:
    ///   "messaging:didReceiveRegistrationToken:" → "DidReceiveRegistrationToken"
    ///   "URLSession:task:didCompleteWithError:"  → "TaskDidCompleteWithError"
    ///   "tableView:commitEditingStyle:forRowAtIndexPath:" → "CommitEditingStyleForRowAtIndexPath"
    ///   "didReceiveNotification:"                → "DidReceiveNotification"
    /// For single-part selectors, falls back to normal SelectorToMethodName behavior.
    /// </summary>
    internal static string SelectorToDelegateMethodName(string selector)
    {
        var parts = selector.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
            return string.Concat(parts.Skip(1).Select(ToPascalCase));
        return ToPascalCase(parts[0]);
    }

    static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// Formats the ArgumentSemantic suffix for [Export] attributes on properties.
    /// Returns empty string when no semantic is specified, otherwise ", ArgumentSemantic.X".
    /// Retain maps to Strong (they are equivalent in ARC).
    /// </summary>
    internal static string FormatArgumentSemantic(ObjCMemorySemantic semantic) => semantic switch
    {
        ObjCMemorySemantic.Copy => ", ArgumentSemantic.Copy",
        ObjCMemorySemantic.Assign or ObjCMemorySemantic.UnsafeUnretained => ", ArgumentSemantic.Assign",
        ObjCMemorySemantic.Weak => ", ArgumentSemantic.Weak",
        ObjCMemorySemantic.Strong or ObjCMemorySemantic.Retain => ", ArgumentSemantic.Retain",
        _ => ""
    };
}
