// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration.ObjC;

public static class ApiDefinitionEmitter
{
    public static string Emit(ObjCModule module, string outputDir, string resolvedNamespace, ILogger logger, ObjCBindingDiagnostics? diagnostics = null)
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
        sb.AppendLine("using Metal;");
        sb.AppendLine("using ObjCRuntime;");
        sb.AppendLine("using CoreGraphics;");
        sb.AppendLine("using UIKit;");
        sb.AppendLine();
        sb.AppendLine($"namespace {resolvedNamespace}");
        sb.AppendLine("{");

        foreach (var proto in module.Protocols)
            EmitProtocol(sb, proto, typedefMap, blockTypedefMap, knownTypes, appleSdkTypes, logger, diagnostics);

        foreach (var cls in module.Classes)
            EmitClass(sb, cls, typedefMap, blockTypedefMap, knownTypes, appleSdkTypes, logger, diagnostics);

        foreach (var cat in module.Categories)
            EmitCategory(sb, cat, typedefMap, blockTypedefMap, knownTypes, appleSdkTypes, logger, diagnostics);

        sb.AppendLine("}");

        Directory.CreateDirectory(outputDir);
        var filePath = Path.Combine(outputDir, "ApiDefinition.cs");
        File.WriteAllText(filePath, sb.ToString());

        logger.LogInformation("Wrote {FilePath}", filePath);
        return filePath;
    }

    static void EmitProtocol(StringBuilder sb, ObjCProtocolDecl proto, Dictionary<string, ObjCTypeRef> typedefMap, Dictionary<string, ObjCTypeRef> blockTypedefMap, HashSet<string> knownTypes, HashSet<string>? appleSdkTypes, ILogger logger, ObjCBindingDiagnostics? diagnostics)
    {
        ObjCDocCommentEmitter.EmitDocComment(sb, proto.DocComment, null, "    ");
        if (EmitAvailabilityAttributes(sb, proto.Availability, "    "))
        {
            diagnostics?.RecordSkip("Protocol", proto.Name, ObjCSkipReason.UnavailableApi, "marked unavailable on iOS");
            return;
        }

        sb.AppendLine("    [Protocol]");
        sb.AppendLine("    [BaseType(typeof(NSObject))]");

        // Filter out implicit protocols from inheritance — NSObject is implicit in .NET MAUI bindings,
        // NSFastEnumeration maps to IEnumerable but isn't a binding interface
        var filteredInherited = proto.InheritedProtocolNames
            .Where(n => n != "NSObject" && n != "NSFastEnumeration")
            .ToList();
        var inheritList = filteredInherited.Count > 0
            ? $" : {string.Join(", ", filteredInherited.Select(n => $"I{ObjCTypeMapper.MapProtocolName(n)}"))}"
            : "";
        sb.AppendLine($"    partial interface I{proto.Name}{inheritList}");
        sb.AppendLine("    {");

        // Protocols don't declare ObjC lightweight generics — only pass the common fallback set
        var emittedMethodSignatures = new HashSet<string>();
        var emittedMemberNames = new HashSet<string>();
        foreach (var method in proto.Methods)
        {
            var emittedName = EmitMethod(sb, method, declaringClassName: null, isProtocol: true, genericTypeParams: null, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, emittedMethodSignatures: emittedMethodSignatures, knownTypes: knownTypes, appleSdkTypes: appleSdkTypes, logger: logger, diagnostics: diagnostics);
            if (emittedName != null) emittedMemberNames.Add(emittedName);
        }

        foreach (var prop in proto.Properties)
            EmitProperty(sb, prop, declaringClassName: null, genericTypeParams: null, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, emittedPropertyNames: emittedMemberNames, knownTypes: knownTypes, appleSdkTypes: appleSdkTypes, logger: logger, diagnostics: diagnostics);

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static void EmitClass(StringBuilder sb, ObjCClassDecl cls, Dictionary<string, ObjCTypeRef> typedefMap, Dictionary<string, ObjCTypeRef> blockTypedefMap, HashSet<string> knownTypes, HashSet<string>? appleSdkTypes, ILogger logger, ObjCBindingDiagnostics? diagnostics)
    {
        ObjCDocCommentEmitter.EmitDocComment(sb, cls.DocComment, null, "    ");
        if (EmitAvailabilityAttributes(sb, cls.Availability, "    "))
        {
            diagnostics?.RecordSkip("Class", cls.Name, ObjCSkipReason.UnavailableApi, "marked unavailable on iOS");
            return;
        }

        // Disable default constructor if the class declares any parameterless init
        // to avoid bgen generating a duplicate parameterless constructor
        var hasParameterlessInit = cls.Methods.Any(m =>
            (m.Selector == "init" || m.Selector.StartsWith("initWith", StringComparison.Ordinal))
            && m.Parameters.Count == 0);
        if (hasParameterlessInit)
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
            !(conformsToNSCoding && m.Selector == "initWithCoder:")))
        {
            var emittedName = EmitMethod(sb, method, declaringClassName: cls.Name, isProtocol: false, genericTypeParams: classGenericParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, emittedConstructorSignatures: emittedConstructorSignatures, emittedMethodSignatures: emittedMethodSignatures, knownTypes: knownTypes, appleSdkTypes: appleSdkTypes, logger: logger, diagnostics: diagnostics);
            if (emittedName != null) emittedMemberNames.Add(emittedName);
        }

        foreach (var prop in cls.Properties)
            EmitProperty(sb, prop, declaringClassName: cls.Name, genericTypeParams: classGenericParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, emittedPropertyNames: emittedMemberNames, knownTypes: knownTypes, appleSdkTypes: appleSdkTypes, logger: logger, diagnostics: diagnostics);

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    static void EmitCategory(StringBuilder sb, ObjCCategoryDecl cat, Dictionary<string, ObjCTypeRef> typedefMap, Dictionary<string, ObjCTypeRef> blockTypedefMap, HashSet<string> knownTypes, HashSet<string>? appleSdkTypes, ILogger logger, ObjCBindingDiagnostics? diagnostics)
    {
        if (EmitAvailabilityAttributes(sb, cat.Availability, "    "))
        {
            diagnostics?.RecordSkip("Category", $"{cat.ClassName}.{cat.CategoryName}", ObjCSkipReason.UnavailableApi, "marked unavailable on iOS");
            return;
        }

        sb.AppendLine("    [Category]");
        sb.AppendLine($"    [BaseType(typeof({cat.ClassName}))]");

        var filteredProtocols = cat.ProtocolNames
            .Where(n => n != "NSObject" && n != "NSFastEnumeration")
            .ToList();
        var protocols = filteredProtocols.Count > 0
            ? $" : {string.Join(", ", filteredProtocols.Select(n => $"I{ObjCTypeMapper.MapProtocolName(n)}"))}"
            : "";

        var interfaceName = GenerateCategoryInterfaceName(cat.ClassName, cat.CategoryName);
        sb.AppendLine($"    partial interface {interfaceName}{protocols}");
        sb.AppendLine("    {");

        var categoryGenericParams = cat.GenericTypeParamNames.Count > 0
            ? new HashSet<string>(cat.GenericTypeParamNames)
            : null;

        var emittedMethodSignatures = new HashSet<string>();
        var emittedMemberNames = new HashSet<string>();

        // Filter out init methods — MAUI category interfaces cannot declare constructors
        foreach (var method in cat.Methods)
        {
            if (method.Selector == "init" || method.Selector.StartsWith("initWith", StringComparison.Ordinal))
                continue;
            var emittedName = EmitMethod(sb, method, declaringClassName: cat.ClassName, isProtocol: false, genericTypeParams: categoryGenericParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, emittedMethodSignatures: emittedMethodSignatures, knownTypes: knownTypes, appleSdkTypes: appleSdkTypes, logger: logger, diagnostics: diagnostics);
            if (emittedName != null) emittedMemberNames.Add(emittedName);
        }

        foreach (var prop in cat.Properties)
            EmitProperty(sb, prop, declaringClassName: cat.ClassName, genericTypeParams: categoryGenericParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap, emittedPropertyNames: emittedMemberNames, knownTypes: knownTypes, appleSdkTypes: appleSdkTypes, logger: logger, diagnostics: diagnostics);

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
    static string? EmitMethod(StringBuilder sb, ObjCMethodDecl method, string? declaringClassName, bool isProtocol, HashSet<string>? genericTypeParams, Dictionary<string, ObjCTypeRef>? typedefMap = null, Dictionary<string, ObjCTypeRef>? blockTypedefMap = null, HashSet<string>? emittedConstructorSignatures = null, HashSet<string>? emittedMethodSignatures = null, HashSet<string>? knownTypes = null, HashSet<string>? appleSdkTypes = null, ILogger? logger = null, ObjCBindingDiagnostics? diagnostics = null)
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

        ObjCDocCommentEmitter.EmitDocComment(sb, method.DocComment, method.DocParams, "        ");
        if (EmitAvailabilityAttributes(sb, method.Availability, "        "))
        {
            diagnostics?.RecordSkip("Method", method.Selector, ObjCSkipReason.UnavailableApi, "marked unavailable on iOS");
            return null;
        }

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

        if (!method.IsInstanceMethod && !isConstructor)
            sb.AppendLine("        [Static]");

        sb.AppendLine($"        [Export(\"{method.Selector}\")]");

        var returnType = isConstructor
            ? "NativeHandle"
            : ObjCTypeMapper.MapType(method.ReturnType, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap);

        if (!isConstructor && ObjCTypeMapper.IsNullableAttribute(method.ReturnType))
            sb.AppendLine("        [return: NullAllowed]");

        var methodName = isConstructor
            ? "Constructor"
            : SelectorToMethodName(method.Selector);

        // Duplicate method signature detection: rename with full selector parts if collision
        if (!isConstructor && emittedMethodSignatures != null)
        {
            var paramSignature = string.Join(",", method.Parameters.Select(p => ObjCTypeMapper.MapType(p.Type, genericTypeParams: genericTypeParams, typedefMap: typedefMap, blockTypedefMap: blockTypedefMap)));
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

        var parameters = EmitParameters(method.Parameters, genericTypeParams, typedefMap, blockTypedefMap);
        sb.AppendLine($"        {returnType} {methodName}({parameters});");
        sb.AppendLine();

        return isConstructor ? null : methodName;
    }

    static void EmitProperty(StringBuilder sb, ObjCPropertyDecl prop, string? declaringClassName, HashSet<string>? genericTypeParams, Dictionary<string, ObjCTypeRef>? typedefMap = null, Dictionary<string, ObjCTypeRef>? blockTypedefMap = null, HashSet<string>? emittedPropertyNames = null, HashSet<string>? knownTypes = null, HashSet<string>? appleSdkTypes = null, ILogger? logger = null, ObjCBindingDiagnostics? diagnostics = null)
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

        ObjCDocCommentEmitter.EmitDocComment(sb, prop.DocComment, null, "        ");
        if (EmitAvailabilityAttributes(sb, prop.Availability, "        "))
        {
            diagnostics?.RecordSkip("Property", propName, ObjCSkipReason.UnavailableApi, "marked unavailable on iOS");
            return;
        }

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
        sb.AppendLine($"        [Export(\"{getterSelector}\")]");

        if (ObjCTypeMapper.IsNullableAttribute(prop.Type))
            sb.AppendLine("        [NullAllowed]");

        var mappedType = ObjCTypeMapper.MapType(prop.Type, declaringClassName, genericTypeParams, typedefMap, blockTypedefMap);
        if (prop.IsReadonly)
        {
            sb.AppendLine($"        {mappedType} {ToPascalCase(prop.Name)} {{ get; }}");
        }
        else
        {
            // Emit setter with custom selector if present
            var setterSelector = prop.SetterSelector ?? $"set{ToPascalCase(prop.Name)}:";
            sb.AppendLine($"        {mappedType} {ToPascalCase(prop.Name)} {{");
            sb.AppendLine($"            get;");
            sb.AppendLine($"            [Export(\"{setterSelector}\")] set;");
            sb.AppendLine($"        }}");
        }
        sb.AppendLine();
    }

    static bool EmitAvailabilityAttributes(StringBuilder sb, List<ObjCAvailability> availability, string indent) =>
        ObjCAvailabilityEmitter.EmitAvailabilityAttributes(sb, availability, indent);

    static string EmitParameters(List<ObjCParameterDecl> parameters, HashSet<string>? genericTypeParams, Dictionary<string, ObjCTypeRef>? typedefMap = null, Dictionary<string, ObjCTypeRef>? blockTypedefMap = null)
    {
        var parts = new List<string>();
        foreach (var param in parameters)
        {
            if (ObjCTypeMapper.IsNSErrorOutParameter(param.Type))
            {
                parts.Add("[NullAllowed] out NSError error");
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

    static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;
        return char.ToUpperInvariant(name[0]) + name[1..];
    }
}
