// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public sealed record SurfaceSyntaxScanResult(
    IReadOnlyList<SurfaceMemberObservation> Members,
    IReadOnlyList<SurfaceNativeBinding> Imports,
    IReadOnlyList<string> Diagnostics);

public static class SurfaceSyntaxScanner
{
    public static SurfaceSyntaxScanResult Scan(
        string captureId,
        SurfaceTargetKey targetKey,
        string surfaceLane,
        string module,
        IReadOnlyList<string> sourceFiles,
        IReadOnlyList<string>? preprocessorSymbols = null,
        string? evidenceRoot = null)
    {
        var parseOptions = new CSharpParseOptions(
            LanguageVersion.Preview,
            preprocessorSymbols: preprocessorSymbols ?? []);
        var drafts = new List<MemberDraft>();
        var callables = new List<CallableDraft>();
        var diagnostics = new List<string>();

        var inputs = sourceFiles.OrderBy(p => p, StringComparer.Ordinal).Select(path =>
        {
            var text = File.ReadAllText(path);
            var tree = CSharpSyntaxTree.ParseText(text, parseOptions, path);
            return (Path: path, Tree: tree, Root: tree.GetCompilationUnitRoot());
        }).ToList();
        var compilation = CSharpCompilation.Create(
            "SurfaceSyntaxScan",
            inputs.Select(input => input.Tree),
            TrustedPlatformReferences());

        foreach (var input in inputs)
        {
            var path = input.Path;
            var tree = input.Tree;
            foreach (var diagnostic in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
                diagnostics.Add(diagnostic.ToString());

            var root = input.Root;
            var context = new FileContext(path, evidenceRoot ?? Path.GetDirectoryName(path)!, root);
            CollectTypes(root, context, drafts);
            CollectMembers(root, context, drafts, callables, compilation.GetSemanticModel(tree));
        }

        var callableIndex = callables
            .GroupBy(c => (c.Name, c.ArgumentCount))
            .ToDictionary(g => g.Key, g => g.ToList());
        foreach (var callable in callables)
            callable.NativeClosure = ResolveNativeClosure(callable, callableIndex);

        foreach (var draft in drafts)
        {
            var bindings = draft.Callables
                .SelectMany(c => c.NativeClosure)
                .DistinctBy(NativeIdentity)
                .OrderBy(NativeIdentity, StringComparer.Ordinal)
                .ToList();
            draft.NativeBindings = bindings;
        }

        var members = drafts
            .GroupBy(d => d.Key.Digest, StringComparer.Ordinal)
            .Select(group => MergeDrafts(captureId, targetKey, surfaceLane, module, group.ToList()))
            .OrderBy(m => m.PublicKey.Display, StringComparer.Ordinal)
            .ThenBy(m => m.ObservationId, StringComparer.Ordinal)
            .ToList();

        var imports = callables
            .Where(c => c.Import is not null)
            .Select(c => c.Import!)
            .DistinctBy(NativeIdentity)
            .OrderBy(NativeIdentity, StringComparer.Ordinal)
            .ToList();
        return new SurfaceSyntaxScanResult(members, imports, diagnostics);
    }

    public static string BuildManifestSignature(SurfacePublicKey key)
    {
        var owner = string.Join('.', key.EnclosingTypes.Select(RemoveArity));
        var prefix = string.IsNullOrEmpty(owner) ? "" : owner + ".";
        if (key.DeclarationKind is "property" or "field" or "event" or "enum-constant")
            return prefix + key.Name;
        if (key.DeclarationKind == "indexer")
            return $"{prefix}this[{string.Join(',', key.Parameters.Select(p =>
                RestoreManifestGenericNames(p.Type, key.ManifestGenericNames)))}]";

        var parameters = string.Join(',', key.Parameters.Select(p =>
        {
            var type = RestoreManifestGenericNames(p.Type, key.ManifestGenericNames);
            return p.Modifier is "ref" or "out" or "in" ? $"{p.Modifier} {type}" : type;
        }));
        var totalArity = key.GenericArity + key.EnclosingTypes.Sum(ParseArity);
        var genericArity = totalArity == 0 ? "" : $"`{totalArity}";
        return $"{prefix}{key.Name}({parameters}){genericArity}";
    }

    private static string RestoreManifestGenericNames(string type, IReadOnlyList<string> names)
    {
        for (var index = names.Count - 1; index >= 0; index--)
            type = Regex.Replace(type, $@"\bT{index}\b", $"__SURFACE_GENERIC_{index}__");
        for (var index = 0; index < names.Count; index++)
            type = type.Replace($"__SURFACE_GENERIC_{index}__", names[index], StringComparison.Ordinal);
        return type;
    }

    public static string NormalizeManifestSignature(string signature)
    {
        var normalized = signature.Replace("global::", "", StringComparison.Ordinal);
        return Regex.Replace(normalized, @"\s*([<>,?\[\]\(\)\*&:])\s*", "$1");
    }

    private static void CollectTypes(CompilationUnitSyntax root, FileContext context, List<MemberDraft> drafts)
    {
        foreach (var node in root.DescendantNodes().Where(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
        {
            if (!IsConsumerVisible(node)) continue;
            switch (node)
            {
                case BaseTypeDeclarationSyntax type:
                {
                    var typeParameters = type is TypeDeclarationSyntax td ? td.TypeParameterList?.Parameters : null;
                    var normalizer = context.Normalizer(type, typeParameters);
                    var kind = type switch
                    {
                        ClassDeclarationSyntax => "class",
                        StructDeclarationSyntax => "struct",
                        InterfaceDeclarationSyntax => "interface",
                        EnumDeclarationSyntax => "enum",
                        RecordDeclarationSyntax record when record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) => "record-struct",
                        RecordDeclarationSyntax => "record",
                        _ => "type",
                    };
                    var baseTypes = type.BaseList?.Types.Select(b => normalizer.Type(b.Type)).ToList() ?? [];
                    var key = new SurfacePublicKey
                    {
                        Namespace = GetNamespace(type),
                        EnclosingTypes = GetEnclosingTypes(type),
                        DeclarationKind = kind,
                        Name = WithArity(type.Identifier.ValueText, typeParameters?.Count ?? 0),
                        GenericArity = typeParameters?.Count ?? 0,
                        Parameters = [],
                        Static = HasModifier(type.Modifiers, SyntaxKind.StaticKeyword),
                    };
                    var shape = new SurfacePublicShape
                    {
                        Accessibility = GetAccessibility(type),
                        Type = null,
                        ParameterDefaults = [],
                        Accessors = [],
                        Constraints = GetConstraints(type, normalizer),
                        BaseTypes = baseTypes,
                        Attributes = GetAttributes(type.AttributeLists, normalizer),
                        Tombstoned = false,
                    };
                    drafts.Add(new MemberDraft(key, shape, [context.Reference(type)], []));
                    break;
                }
                case DelegateDeclarationSyntax del:
                {
                    var normalizer = context.Normalizer(del, del.TypeParameterList?.Parameters);
                    var key = new SurfacePublicKey
                    {
                        Namespace = GetNamespace(del),
                        EnclosingTypes = GetEnclosingTypes(del),
                        DeclarationKind = "delegate",
                        Name = WithArity(del.Identifier.ValueText, del.TypeParameterList?.Parameters.Count ?? 0),
                        GenericArity = del.TypeParameterList?.Parameters.Count ?? 0,
                        Parameters = del.ParameterList.Parameters.Select(p => Parameter(p, normalizer)).ToList(),
                        Static = false,
                    };
                    var shape = Shape(
                        del,
                        normalizer.Type(del.ReturnType),
                        [],
                        normalizer,
                        ParameterDefaults(del.ParameterList.Parameters));
                    drafts.Add(new MemberDraft(key, shape, [context.Reference(del)], []));
                    break;
                }
            }
        }
    }

    private static void CollectMembers(
        CompilationUnitSyntax root,
        FileContext context,
        List<MemberDraft> drafts,
        List<CallableDraft> callables,
        SemanticModel semanticModel)
    {
        foreach (var node in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            if (node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
                continue;

            var consumerVisible = IsConsumerVisible(node);
            if (!consumerVisible && node is MethodDeclarationSyntax privateMethod)
            {
                var normalizer = context.Normalizer(privateMethod, privateMethod.TypeParameterList?.Parameters);
                callables.Add(Callable(
                    privateMethod,
                    privateMethod.Identifier.ValueText,
                    privateMethod.ParameterList.Parameters.Count,
                    context,
                    normalizer));
                continue;
            }
            if (!consumerVisible) continue;

            switch (node)
            {
                case MethodDeclarationSyntax method:
                    AddMethod(method, context, drafts, callables);
                    break;
                case ConstructorDeclarationSyntax ctor:
                    AddConstructor(ctor, context, drafts, callables);
                    break;
                case OperatorDeclarationSyntax op:
                    AddOperator(op, context, drafts, callables);
                    break;
                case ConversionOperatorDeclarationSyntax conversion:
                    AddConversion(conversion, context, drafts, callables);
                    break;
                case PropertyDeclarationSyntax property:
                    AddProperty(property, context, drafts, callables);
                    break;
                case IndexerDeclarationSyntax indexer:
                    AddIndexer(indexer, context, drafts, callables);
                    break;
                case FieldDeclarationSyntax field:
                    AddField(field, context, drafts, semanticModel);
                    break;
                case EventFieldDeclarationSyntax eventField:
                    AddEventField(eventField, context, drafts);
                    break;
                case EventDeclarationSyntax eventDeclaration:
                    AddEvent(eventDeclaration, context, drafts, callables);
                    break;
                case EnumMemberDeclarationSyntax enumMember:
                    AddEnumMember(enumMember, context, drafts, semanticModel);
                    break;
            }
        }
    }

    private static void AddMethod(
        MethodDeclarationSyntax node,
        FileContext context,
        List<MemberDraft> drafts,
        List<CallableDraft> callables)
    {
        var normalizer = context.Normalizer(node, node.TypeParameterList?.Parameters);
        var explicitInterface = node.ExplicitInterfaceSpecifier is null
            ? null
            : normalizer.Type(node.ExplicitInterfaceSpecifier.Name);
        var key = MethodKey(
            node,
            normalizer,
            "method",
            node.Identifier.ValueText,
            node.TypeParameterList?.Parameters.Count ?? 0,
            node.ParameterList.Parameters,
            explicitInterface);
        var callable = Callable(node, key.Name, node.ParameterList.Parameters.Count, context, normalizer);
        callables.Add(callable);
        drafts.Add(new MemberDraft(
            key,
            Shape(
                node,
                normalizer.Type(node.ReturnType),
                [],
                normalizer,
                ParameterDefaults(node.ParameterList.Parameters)),
            [context.Reference(node)],
            [callable]));
    }

    private static void AddConstructor(
        ConstructorDeclarationSyntax node,
        FileContext context,
        List<MemberDraft> drafts,
        List<CallableDraft> callables)
    {
        var normalizer = context.Normalizer(node, null);
        var key = MethodKey(node, normalizer, "constructor", node.Identifier.ValueText, 0, node.ParameterList.Parameters, null);
        var callable = Callable(node, node.Identifier.ValueText, node.ParameterList.Parameters.Count, context, normalizer);
        callables.Add(callable);
        drafts.Add(new MemberDraft(
            key,
            Shape(node, null, [], normalizer, ParameterDefaults(node.ParameterList.Parameters)),
            [context.Reference(node)],
            [callable]));
    }

    private static void AddOperator(
        OperatorDeclarationSyntax node,
        FileContext context,
        List<MemberDraft> drafts,
        List<CallableDraft> callables)
    {
        var normalizer = context.Normalizer(node, null);
        var name = "operator " + node.OperatorToken.ValueText;
        var key = MethodKey(node, normalizer, "operator", name, 0, node.ParameterList.Parameters, null);
        var callable = Callable(node, name, node.ParameterList.Parameters.Count, context, normalizer);
        callables.Add(callable);
        drafts.Add(new MemberDraft(
            key,
            Shape(node, normalizer.Type(node.ReturnType), [], normalizer, ParameterDefaults(node.ParameterList.Parameters)),
            [context.Reference(node)],
            [callable]));
    }

    private static void AddConversion(
        ConversionOperatorDeclarationSyntax node,
        FileContext context,
        List<MemberDraft> drafts,
        List<CallableDraft> callables)
    {
        var normalizer = context.Normalizer(node, null);
        var name = node.ImplicitOrExplicitKeyword.ValueText + " operator";
        var key = MethodKey(node, normalizer, "conversion", name, 0, node.ParameterList.Parameters, null) with
        {
            ConversionDestination = normalizer.Type(node.Type),
        };
        var callable = Callable(node, name, node.ParameterList.Parameters.Count, context, normalizer);
        callables.Add(callable);
        drafts.Add(new MemberDraft(
            key,
            Shape(node, normalizer.Type(node.Type), [], normalizer, ParameterDefaults(node.ParameterList.Parameters)),
            [context.Reference(node)],
            [callable]));
    }

    private static void AddProperty(
        PropertyDeclarationSyntax node,
        FileContext context,
        List<MemberDraft> drafts,
        List<CallableDraft> callables)
    {
        var normalizer = context.Normalizer(node, null);
        var key = PropertyKey(node, normalizer, "property", node.Identifier.ValueText, []);
        var propertyCallables = AccessorCallables(
            node,
            node.AccessorList,
            node.Identifier.ValueText,
            0,
            context,
            normalizer);
        callables.AddRange(propertyCallables);
        drafts.Add(new MemberDraft(
            key,
            Shape(node, normalizer.Type(node.Type), Accessors(node.AccessorList, node), normalizer),
            [context.Reference(node)],
            propertyCallables));
    }

    private static void AddIndexer(
        IndexerDeclarationSyntax node,
        FileContext context,
        List<MemberDraft> drafts,
        List<CallableDraft> callables)
    {
        var normalizer = context.Normalizer(node, null);
        var parameters = node.ParameterList.Parameters.Select(p => Parameter(p, normalizer)).ToList();
        var key = PropertyKey(node, normalizer, "indexer", "this", parameters);
        var indexerCallables = AccessorCallables(
            node,
            node.AccessorList,
            "this",
            parameters.Count,
            context,
            normalizer);
        callables.AddRange(indexerCallables);
        drafts.Add(new MemberDraft(
            key,
            Shape(
                node,
                normalizer.Type(node.Type),
                Accessors(node.AccessorList, node),
                normalizer,
                ParameterDefaults(node.ParameterList.Parameters)),
            [context.Reference(node)],
            indexerCallables));
    }

    private static void AddField(
        FieldDeclarationSyntax node,
        FileContext context,
        List<MemberDraft> drafts,
        SemanticModel semanticModel)
    {
        var normalizer = context.Normalizer(node, null);
        foreach (var variable in node.Declaration.Variables)
        {
            var key = PropertyKey(node, normalizer, "field", variable.Identifier.ValueText, []) with
            {
                Static = HasModifier(node.Modifiers, SyntaxKind.StaticKeyword)
                    || HasModifier(node.Modifiers, SyntaxKind.ConstKeyword),
            };
            var shape = Shape(node, normalizer.Type(node.Declaration.Type), [], normalizer);
            if (HasModifier(node.Modifiers, SyntaxKind.ConstKeyword))
                shape = shape with { ConstantValue = ConstantValue(semanticModel, variable.Initializer?.Value) };
            drafts.Add(new MemberDraft(
                key,
                shape,
                [context.Reference(variable)],
                []));
        }
    }

    private static void AddEventField(EventFieldDeclarationSyntax node, FileContext context, List<MemberDraft> drafts)
    {
        var normalizer = context.Normalizer(node, null);
        foreach (var variable in node.Declaration.Variables)
        {
            var key = PropertyKey(node, normalizer, "event", variable.Identifier.ValueText, []);
            drafts.Add(new MemberDraft(
                key,
                Shape(node, normalizer.Type(node.Declaration.Type), [], normalizer),
                [context.Reference(variable)],
                []));
        }
    }

    private static void AddEvent(
        EventDeclarationSyntax node,
        FileContext context,
        List<MemberDraft> drafts,
        List<CallableDraft> callables)
    {
        var normalizer = context.Normalizer(node, null);
        var key = PropertyKey(node, normalizer, "event", node.Identifier.ValueText, []);
        var eventCallables = AccessorCallables(
            node,
            node.AccessorList,
            node.Identifier.ValueText,
            0,
            context,
            normalizer);
        callables.AddRange(eventCallables);
        drafts.Add(new MemberDraft(
            key,
            Shape(node, normalizer.Type(node.Type), Accessors(node.AccessorList, node), normalizer),
            [context.Reference(node)],
            eventCallables));
    }

    private static void AddEnumMember(
        EnumMemberDeclarationSyntax node,
        FileContext context,
        List<MemberDraft> drafts,
        SemanticModel semanticModel)
    {
        var normalizer = context.Normalizer(node, null);
        var key = new SurfacePublicKey
        {
            Namespace = GetNamespace(node),
            EnclosingTypes = GetEnclosingTypes(node),
            DeclarationKind = "enum-constant",
            Name = node.Identifier.ValueText,
            Parameters = [],
            Static = true,
        };
        drafts.Add(new MemberDraft(
            key,
            new SurfacePublicShape
            {
                Accessibility = "public",
                Type = null,
                ConstantValue = ConstantValue(semanticModel, node),
                ParameterDefaults = [],
                Accessors = [],
                Constraints = [],
                BaseTypes = [],
                Attributes = GetAttributes(node.AttributeLists, normalizer),
                Tombstoned = false,
            },
            [context.Reference(node)],
            []));
    }

    private static string ConstantValue(SemanticModel semanticModel, EnumMemberDeclarationSyntax member)
    {
        if (semanticModel.GetDeclaredSymbol(member) is IFieldSymbol { HasConstantValue: true } field)
            return FormatConstant(field.ConstantValue);
        return member.EqualsValue is null
            ? $"implicit:{((EnumDeclarationSyntax)member.Parent!).Members.IndexOf(member)}"
            : "expression:" + member.EqualsValue.Value.NormalizeWhitespace().ToFullString();
    }

    private static string ConstantValue(SemanticModel semanticModel, ExpressionSyntax? expression)
    {
        if (expression is null)
            return "missing";
        var constant = semanticModel.GetConstantValue(expression);
        return constant.HasValue
            ? FormatConstant(constant.Value)
            : "expression:" + expression.NormalizeWhitespace().ToFullString();
    }

    private static string FormatConstant(object? value)
        => value switch
        {
            null => "null",
            bool boolean => boolean ? "bool:true" : "bool:false",
            char character => $"char:{(int)character}",
            string text => "string:" + text,
            float number => "float:" + number.ToString("R", CultureInfo.InvariantCulture),
            double number => "double:" + number.ToString("R", CultureInfo.InvariantCulture),
            decimal number => "decimal:" + number.ToString(CultureInfo.InvariantCulture),
            IFormattable formattable => value.GetType().Name + ":" + formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.GetType().Name + ":" + value,
        };

    private static IReadOnlyList<MetadataReference> TrustedPlatformReferences()
    {
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string paths)
            return [];
        var coreLibrary = paths.Split(Path.PathSeparator)
            .FirstOrDefault(path => string.Equals(
                Path.GetFileName(path), "System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase));
        return coreLibrary is null ? [] : [MetadataReference.CreateFromFile(coreLibrary)];
    }

    private static SurfacePublicKey MethodKey(
        MemberDeclarationSyntax node,
        TypeNormalizer normalizer,
        string kind,
        string name,
        int arity,
        SeparatedSyntaxList<ParameterSyntax> parameters,
        string? explicitInterface)
        => new()
        {
            Namespace = GetNamespace(node),
            EnclosingTypes = GetEnclosingTypes(node),
            DeclarationKind = kind,
            Name = name,
            GenericArity = arity,
            Parameters = parameters.Select(p => Parameter(p, normalizer)).ToList(),
            Static = HasModifier(node.GetModifiers(), SyntaxKind.StaticKeyword),
            ExplicitInterface = explicitInterface,
            ManifestGenericNames = normalizer.OriginalTypeParameterNames,
        };

    private static SurfacePublicKey PropertyKey(
        MemberDeclarationSyntax node,
        TypeNormalizer normalizer,
        string kind,
        string name,
        IReadOnlyList<SurfaceParameterKey> parameters)
        => new()
        {
            Namespace = GetNamespace(node),
            EnclosingTypes = GetEnclosingTypes(node),
            DeclarationKind = kind,
            Name = name,
            Parameters = parameters,
            Static = HasModifier(node.GetModifiers(), SyntaxKind.StaticKeyword),
            ManifestGenericNames = normalizer.OriginalTypeParameterNames,
            ExplicitInterface = node switch
            {
                PropertyDeclarationSyntax p when p.ExplicitInterfaceSpecifier is not null => normalizer.Type(p.ExplicitInterfaceSpecifier.Name),
                IndexerDeclarationSyntax i when i.ExplicitInterfaceSpecifier is not null => normalizer.Type(i.ExplicitInterfaceSpecifier.Name),
                EventDeclarationSyntax e when e.ExplicitInterfaceSpecifier is not null => normalizer.Type(e.ExplicitInterfaceSpecifier.Name),
                _ => null,
            },
        };

    private static SurfaceParameterKey Parameter(ParameterSyntax parameter, TypeNormalizer normalizer)
    {
        var modifier = parameter.Modifiers.FirstOrDefault(m => m.IsKind(SyntaxKind.RefKeyword)
            || m.IsKind(SyntaxKind.OutKeyword)
            || m.IsKind(SyntaxKind.InKeyword)
            || m.IsKind(SyntaxKind.ParamsKeyword)
            || m.IsKind(SyntaxKind.ThisKeyword)).ValueText;
        return new SurfaceParameterKey(
            parameter.Type is null ? "unknown" : normalizer.Type(parameter.Type),
            modifier);
    }

    private static IReadOnlyList<SurfaceParameterDefaultShape> ParameterDefaults(
        SeparatedSyntaxList<ParameterSyntax> parameters)
        => parameters.Select(parameter => new SurfaceParameterDefaultShape(
                parameter.Default is not null,
                parameter.Default?.Value.NormalizeWhitespace().ToFullString()))
            .ToList();

    private static SurfacePublicShape Shape(
        MemberDeclarationSyntax node,
        string? type,
        IReadOnlyList<SurfaceAccessorShape> accessors,
        TypeNormalizer normalizer,
        IReadOnlyList<SurfaceParameterDefaultShape>? parameterDefaults = null)
        => new()
        {
            Accessibility = HasExplicitInterface(node) ? "explicit-interface" : GetAccessibility(node),
            Type = type,
            ParameterDefaults = parameterDefaults ?? [],
            Accessors = accessors,
            Constraints = GetConstraints(node, normalizer),
            BaseTypes = [],
            Attributes = GetAttributes(node.GetAttributeLists(), normalizer),
            Tombstoned = IsExplicitTombstone(node),
        };

    private static bool IsExplicitTombstone(MemberDeclarationSyntax node)
    {
        var throwsNotSupported = node.DescendantNodes().OfType<ThrowStatementSyntax>()
                .Any(t => t.Expression?.ToString().Contains("NotSupportedException", StringComparison.Ordinal) == true)
            || node.DescendantNodes().OfType<ThrowExpressionSyntax>()
                .Any(t => t.Expression.ToString().Contains("NotSupportedException", StringComparison.Ordinal));
        if (!throwsNotSupported) return false;

        foreach (var attribute in node.GetAttributeLists().SelectMany(list => list.Attributes))
        {
            var name = attribute.Name.ToString();
            if (name.EndsWith("UnsupportedSwiftType", StringComparison.Ordinal)
                || name.EndsWith("UnsupportedSwiftTypeAttribute", StringComparison.Ordinal))
                return true;
            if (name.EndsWith("Obsolete", StringComparison.Ordinal)
                || name.EndsWith("ObsoleteAttribute", StringComparison.Ordinal))
            {
                if (attribute.ArgumentList?.Arguments.Any(argument =>
                        argument.Expression.IsKind(SyntaxKind.TrueLiteralExpression)) == true)
                    return true;
            }
        }
        return false;
    }

    private static IReadOnlyList<SurfaceAccessorShape> Accessors(AccessorListSyntax? list, MemberDeclarationSyntax owner)
    {
        if (list is null)
            return owner is PropertyDeclarationSyntax { ExpressionBody: not null }
                or IndexerDeclarationSyntax { ExpressionBody: not null }
                ? [new SurfaceAccessorShape("get", GetAccessibility(owner))]
                : [];
        return list.Accessors.Select(a => new SurfaceAccessorShape(
                a.Keyword.ValueText,
                a.Modifiers.Count == 0 ? GetAccessibility(owner) : GetAccessibility(a.Modifiers, isTopLevelType: false, inInterface: false)))
            .OrderBy(a => a.Kind, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> GetConstraints(SyntaxNode node, TypeNormalizer normalizer)
        => node.ChildNodes().OfType<TypeParameterConstraintClauseSyntax>()
            .Select(c => normalizer.Text(c))
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<string> GetAttributes(SyntaxList<AttributeListSyntax> lists, TypeNormalizer normalizer)
        => lists.SelectMany(l => l.Attributes)
            .Select(normalizer.Text)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();

    private static CallableDraft Callable(
        SyntaxNode node,
        string name,
        int argumentCount,
        FileContext context,
        TypeNormalizer normalizer,
        string? accessor = null)
    {
        var typeOwner = string.Join('.', GetEnclosingTypes(node));
        var @namespace = GetNamespace(node);
        var owner = string.IsNullOrEmpty(@namespace) ? typeOwner
            : string.IsNullOrEmpty(typeOwner) ? @namespace
            : @namespace + "." + typeOwner;
        var import = node is MethodDeclarationSyntax method
            ? ParseImport(method, context, normalizer)
            : null;
        var calls = node.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(invocation => new CallSite(
                invocation.Expression switch
                {
                    IdentifierNameSyntax id => id.Identifier.ValueText,
                    GenericNameSyntax generic => generic.Identifier.ValueText,
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                    _ => invocation.Expression.ToString(),
                },
                invocation.ArgumentList.Arguments.Count,
                invocation.Expression is MemberAccessExpressionSyntax access ? access.Expression.ToString() : null))
            .ToList();
        return new CallableDraft(name, argumentCount, owner, @namespace, calls, import, accessor);
    }

    private static IReadOnlyList<CallableDraft> AccessorCallables(
        MemberDeclarationSyntax owner,
        AccessorListSyntax? accessorList,
        string name,
        int argumentCount,
        FileContext context,
        TypeNormalizer normalizer)
    {
        if (accessorList is null)
            return [Callable(owner, name + "#get", argumentCount, context, normalizer, "get")];
        return accessorList.Accessors.Select(accessor => Callable(
                accessor,
                name + "#" + accessor.Keyword.ValueText,
                argumentCount,
                context,
                normalizer,
                accessor.Keyword.ValueText))
            .ToList();
    }

    private static SurfaceNativeBinding? ParseImport(
        MethodDeclarationSyntax method,
        FileContext context,
        TypeNormalizer normalizer)
    {
        var importAttribute = method.AttributeLists.SelectMany(a => a.Attributes).FirstOrDefault(a =>
        {
            var name = a.Name.ToString();
            return name.EndsWith("LibraryImport", StringComparison.Ordinal)
                || name.EndsWith("LibraryImportAttribute", StringComparison.Ordinal)
                || name.EndsWith("DllImport", StringComparison.Ordinal)
                || name.EndsWith("DllImportAttribute", StringComparison.Ordinal);
        });
        if (importAttribute is null) return null;

        var arguments = importAttribute.ArgumentList?.Arguments ?? default;
        var library = arguments.Count > 0 ? ConstantString(arguments[0].Expression) : "unknown";
        var entryPoint = arguments.FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == "EntryPoint");
        var symbol = entryPoint is null ? method.Identifier.ValueText : ConstantString(entryPoint.Expression);
        var attributeText = method.AttributeLists.ToString();
        var convention = attributeText.Contains("CallConvCdecl", StringComparison.Ordinal) ? "cdecl"
            : attributeText.Contains("CallConvSwift", StringComparison.Ordinal) ? "swift"
            : attributeText.Contains("CallingConvention.Cdecl", StringComparison.Ordinal) ? "cdecl"
            : "unknown";
        var parameters = method.ParameterList.Parameters.Select(p =>
        {
            var type = p.Type is null ? "unknown" : normalizer.Type(p.Type);
            var modifier = p.Modifiers.FirstOrDefault(token => token.IsKind(SyntaxKind.RefKeyword)
                || token.IsKind(SyntaxKind.OutKeyword)
                || token.IsKind(SyntaxKind.InKeyword)).ValueText;
            return new
            {
                Type = type,
                Modifier = modifier,
                UntypedSwiftSelf = IsType(type, "SwiftSelf"),
                TypedSwiftSelf = IsGenericType(type, "SwiftSelf"),
                SwiftError = IsType(type, "SwiftError"),
            };
        }).ToList();
        var abi = $"{normalizer.Type(method.ReturnType)}({string.Join(',', parameters.Select(p =>
            string.IsNullOrEmpty(p.Modifier) ? p.Type : $"{p.Modifier} {p.Type}"))})";
        return new SurfaceNativeBinding
        {
            Library = library,
            EntryPoint = symbol,
            Convention = convention,
            AbiSignature = abi,
            Route = symbol.StartsWith("thunk_", StringComparison.Ordinal) ? "native-thunk"
                : symbol.StartsWith("SBW_", StringComparison.Ordinal) || symbol.StartsWith("SBSW_", StringComparison.Ordinal) ? "cdecl"
                : symbol.StartsWith("$s", StringComparison.Ordinal) ? "direct-swift"
                : "unknown",
            UntypedSwiftSelf = parameters.Any(p => p.UntypedSwiftSelf),
            TypedSwiftSelf = parameters.Any(p => p.TypedSwiftSelf),
            SwiftError = parameters.Any(p => p.SwiftError),
            Evidence = context.Reference(method),
        };
    }

    private static bool IsType(string type, string expected)
        => string.Equals(type, expected, StringComparison.Ordinal)
            || type.EndsWith('.' + expected, StringComparison.Ordinal);

    private static bool IsGenericType(string type, string expected)
        => type.StartsWith(expected + "<", StringComparison.Ordinal)
            || type.Contains('.' + expected + "<", StringComparison.Ordinal);

    private static IReadOnlyList<SurfaceNativeBinding> ResolveNativeClosure(
        CallableDraft root,
        IReadOnlyDictionary<(string Name, int ArgumentCount), List<CallableDraft>> index)
    {
        var result = new List<SurfaceNativeBinding>();
        var queue = new Queue<CallableDraft>();
        var visited = new HashSet<CallableDraft>(ReferenceEqualityComparer.Instance);
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current)) continue;
            if (current.Import is not null)
                result.Add(root.Accessor is null ? current.Import : current.Import with { Accessor = root.Accessor });
            foreach (var call in current.Calls)
            {
                if (!index.TryGetValue((call.Name, call.ArgumentCount), out var candidates)) continue;
                var resolved = candidates.Where(c => c.Owner == current.Owner).ToList();
                if (call.Qualifier is not null)
                {
                    var qualifier = call.Qualifier.Replace("global::", "", StringComparison.Ordinal);
                    if (qualifier is not ("this" or "base"))
                    {
                        var possibleOwners = new HashSet<string>(StringComparer.Ordinal) { qualifier };
                        if (!string.IsNullOrEmpty(current.Namespace))
                            possibleOwners.Add(current.Namespace + "." + qualifier);
                        possibleOwners.Add(current.Owner + "." + qualifier);
                        var ownerSeparator = current.Owner.LastIndexOf('.');
                        if (ownerSeparator >= 0)
                            possibleOwners.Add(current.Owner[..ownerSeparator] + "." + qualifier);
                        resolved = candidates.Where(c => possibleOwners.Contains(c.Owner)).ToList();
                    }
                }
                if (resolved.Count == 1) queue.Enqueue(resolved[0]);
            }
        }
        return result;
    }

    private static SurfaceMemberObservation MergeDrafts(
        string captureId,
        SurfaceTargetKey targetKey,
        string surfaceLane,
        string module,
        IReadOnlyList<MemberDraft> drafts)
    {
        var first = drafts[0];
        var shapeDigests = drafts.Select(d => d.Shape.Digest).Distinct(StringComparer.Ordinal).ToList();
        if (shapeDigests.Count != 1)
            throw new InvalidDataException(
                $"Conflicting duplicate public key '{first.Key.Display}' in target {targetKey.TargetName}.");
        var references = drafts.SelectMany(d => d.References)
            .DistinctBy(r => (r.FileSha256, r.RelativePath, r.StartLine, r.EndLine))
            .OrderBy(r => r.RelativePath, StringComparer.Ordinal)
            .ThenBy(r => r.StartLine)
            .ToList();
        var native = drafts.SelectMany(d => d.NativeBindings)
            .DistinctBy(NativeIdentity)
            .OrderBy(NativeIdentity, StringComparer.Ordinal)
            .ToList();
        var observationId = SurfaceCanonicalJson.Hash(new
        {
            captureId,
            targetKey,
            surfaceLane,
            publicKey = first.Key,
        });
        return new SurfaceMemberObservation
        {
            Schema = SurfaceAccountingSchema.Artifact,
            ObservationId = observationId,
            CaptureId = captureId,
            TargetKey = targetKey,
            Module = module,
            SurfaceLane = surfaceLane,
            PublicKey = first.Key,
            PublicShape = first.Shape,
            SourceReferences = references,
            ApiManifest = [],
            NativeBindings = native,
            State = first.Shape.Tombstoned ? "present-tombstoned" : "present-callable-unproven",
            JoinStatus = native.Count == 0 ? "native-unmapped" : "native-syntax-resolved",
        };
    }

    private static bool IsConsumerVisible(SyntaxNode node)
    {
        if (node is EnumMemberDeclarationSyntax)
            return node.Ancestors().OfType<EnumDeclarationSyntax>().All(IsConsumerVisible);
        if (node is not MemberDeclarationSyntax member) return false;
        var explicitImplementation = member switch
        {
            MethodDeclarationSyntax m => m.ExplicitInterfaceSpecifier is not null,
            PropertyDeclarationSyntax p => p.ExplicitInterfaceSpecifier is not null,
            IndexerDeclarationSyntax i => i.ExplicitInterfaceSpecifier is not null,
            EventDeclarationSyntax e => e.ExplicitInterfaceSpecifier is not null,
            _ => false,
        };
        var accessible = explicitImplementation || IsAccessible(GetAccessibility(member));
        return accessible && node.Ancestors().OfType<BaseTypeDeclarationSyntax>()
            .All(type => IsAccessible(GetAccessibility(type)));
    }

    private static bool IsAccessible(string accessibility)
        => accessibility is "public" or "protected" or "protected internal";

    private static bool HasExplicitInterface(MemberDeclarationSyntax member)
        => member is MethodDeclarationSyntax { ExplicitInterfaceSpecifier: not null }
            or PropertyDeclarationSyntax { ExplicitInterfaceSpecifier: not null }
            or IndexerDeclarationSyntax { ExplicitInterfaceSpecifier: not null }
            or EventDeclarationSyntax { ExplicitInterfaceSpecifier: not null };

    private static string GetAccessibility(MemberDeclarationSyntax member)
    {
        var inInterface = member.Parent is InterfaceDeclarationSyntax;
        var isTopLevelType = member is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax
            && member.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax;
        return GetAccessibility(member.GetModifiers(), isTopLevelType, inInterface);
    }

    private static string GetAccessibility(
        SyntaxTokenList modifiers,
        bool isTopLevelType,
        bool inInterface)
    {
        var isPublic = HasModifier(modifiers, SyntaxKind.PublicKeyword);
        var isProtected = HasModifier(modifiers, SyntaxKind.ProtectedKeyword);
        var isInternal = HasModifier(modifiers, SyntaxKind.InternalKeyword);
        var isPrivate = HasModifier(modifiers, SyntaxKind.PrivateKeyword);
        if (isPublic) return "public";
        if (isProtected && isInternal) return "protected internal";
        if (isPrivate && isProtected) return "private protected";
        if (isProtected) return "protected";
        if (isInternal) return "internal";
        if (isPrivate) return "private";
        if (inInterface) return "public";
        return isTopLevelType ? "internal" : "private";
    }

    private static string GetNamespace(SyntaxNode node)
        => string.Join('.', node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse()
            .Select(n => n.Name.ToString()));

    private static IReadOnlyList<string> GetEnclosingTypes(SyntaxNode node)
        => node.Ancestors().OfType<BaseTypeDeclarationSyntax>()
            .Reverse()
            .Select(t => WithArity(
                t.Identifier.ValueText,
                t is TypeDeclarationSyntax td ? td.TypeParameterList?.Parameters.Count ?? 0 : 0))
            .ToList();

    private static string WithArity(string name, int arity) => arity == 0 ? name : $"{name}`{arity}";
    private static int ParseArity(string name)
    {
        var tick = name.LastIndexOf('`');
        return tick >= 0 && int.TryParse(name[(tick + 1)..], out var arity) ? arity : 0;
    }

    private static string RemoveArity(string name)
    {
        var tick = name.LastIndexOf('`');
        return tick < 0 ? name : name[..tick];
    }

    private static bool HasModifier(SyntaxTokenList modifiers, SyntaxKind kind)
        => modifiers.Any(m => m.IsKind(kind));

    private static string ConstantString(ExpressionSyntax expression)
    {
        var text = expression.ToString();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            return text[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal);
        return text;
    }

    private static string NativeIdentity(SurfaceNativeBinding binding)
        => $"{binding.Accessor}\u001f{binding.Library}\u001f{binding.EntryPoint}\u001f{binding.Convention}\u001f{binding.AbiSignature}" +
            $"\u001f{binding.Route}\u001f{binding.UntypedSwiftSelf}\u001f{binding.TypedSwiftSelf}\u001f{binding.SwiftError}";

    private sealed class MemberDraft(
        SurfacePublicKey key,
        SurfacePublicShape shape,
        IReadOnlyList<SurfaceFileReference> references,
        IReadOnlyList<CallableDraft> callables)
    {
        public SurfacePublicKey Key { get; } = key;
        public SurfacePublicShape Shape { get; } = shape;
        public IReadOnlyList<SurfaceFileReference> References { get; } = references;
        public IReadOnlyList<CallableDraft> Callables { get; } = callables;
        public IReadOnlyList<SurfaceNativeBinding> NativeBindings { get; set; } = [];
    }

    private sealed class CallableDraft(
        string name,
        int argumentCount,
        string owner,
        string @namespace,
        IReadOnlyList<CallSite> calls,
        SurfaceNativeBinding? import,
        string? accessor)
    {
        public string Name { get; } = name;
        public int ArgumentCount { get; } = argumentCount;
        public string Owner { get; } = owner;
        public string Namespace { get; } = @namespace;
        public IReadOnlyList<CallSite> Calls { get; } = calls;
        public SurfaceNativeBinding? Import { get; } = import;
        public string? Accessor { get; } = accessor;
        public IReadOnlyList<SurfaceNativeBinding> NativeClosure { get; set; } = [];
    }

    private sealed record CallSite(string Name, int ArgumentCount, string? Qualifier);

    private sealed class FileContext(string path, string evidenceRoot, CompilationUnitSyntax root)
    {
        public TypeNormalizer Normalizer(SyntaxNode node, SeparatedSyntaxList<TypeParameterSyntax>? localParameters)
        {
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var usingDirective in root.Usings.Concat(
                         node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
                             .Reverse()
                             .SelectMany(namespaceNode => namespaceNode.Usings))
                         .Where(usingDirective => usingDirective.Alias is not null))
                aliases[usingDirective.Alias!.Name.Identifier.ValueText] = usingDirective.Name?.ToString() ?? "";
            var parameters = node.Ancestors().OfType<TypeDeclarationSyntax>()
                .Reverse()
                .SelectMany(t => t.TypeParameterList?.Parameters ?? default)
                .Concat(localParameters ?? default)
                .ToList();
            var typeParameters = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < parameters.Count; index++)
                typeParameters[parameters[index].Identifier.ValueText] = $"T{index}";
            return new TypeNormalizer(
                aliases,
                typeParameters,
                parameters.Select(parameter => parameter.Identifier.ValueText).ToList());
        }

        public SurfaceFileReference Reference(SyntaxNode node)
        {
            var span = node.SyntaxTree.GetLineSpan(node.Span);
            return new SurfaceFileReference(
                SurfaceCanonicalJson.Sha256File(path),
                Path.GetRelativePath(evidenceRoot, path).Replace('\\', '/'),
                span.StartLinePosition.Line + 1,
                span.EndLinePosition.Line + 1);
        }
    }

    private sealed class TypeNormalizer(
        IReadOnlyDictionary<string, string> aliases,
        IReadOnlyDictionary<string, string> typeParameters,
        IReadOnlyList<string> originalTypeParameterNames)
    {
        public IReadOnlyList<string> OriginalTypeParameterNames { get; } = originalTypeParameterNames;

        public string Type(NameSyntax syntax) => Text(syntax);
        public string Type(TypeSyntax syntax) => Text(syntax);

        public string Text(SyntaxNode syntax)
        {
            var rewritten = new IdentifierRewriter(aliases, typeParameters).Visit(syntax)
                ?? throw new InvalidOperationException("Roslyn returned null while normalizing syntax.");
            var text = rewritten.NormalizeWhitespace().ToFullString()
                .Replace("global::", "", StringComparison.Ordinal);
            return Regex.Replace(text, @"\s*([<>,?\[\]\(\)\*&:])\s*", "$1");
        }
    }

    private sealed class IdentifierRewriter(
        IReadOnlyDictionary<string, string> aliases,
        IReadOnlyDictionary<string, string> typeParameters) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            var name = node.Identifier.ValueText;
            if (typeParameters.TryGetValue(name, out var normalized))
                return SyntaxFactory.IdentifierName(normalized).WithTriviaFrom(node);
            if (aliases.TryGetValue(name, out var aliasTarget))
                return SyntaxFactory.ParseName(aliasTarget).WithTriviaFrom(node);
            return base.VisitIdentifierName(node);
        }
    }

    private static SyntaxTokenList GetModifiers(this MemberDeclarationSyntax member)
        => member switch
        {
            BaseTypeDeclarationSyntax n => n.Modifiers,
            DelegateDeclarationSyntax n => n.Modifiers,
            MethodDeclarationSyntax n => n.Modifiers,
            ConstructorDeclarationSyntax n => n.Modifiers,
            OperatorDeclarationSyntax n => n.Modifiers,
            ConversionOperatorDeclarationSyntax n => n.Modifiers,
            PropertyDeclarationSyntax n => n.Modifiers,
            IndexerDeclarationSyntax n => n.Modifiers,
            FieldDeclarationSyntax n => n.Modifiers,
            EventFieldDeclarationSyntax n => n.Modifiers,
            EventDeclarationSyntax n => n.Modifiers,
            _ => default,
        };

    private static SyntaxList<AttributeListSyntax> GetAttributeLists(this MemberDeclarationSyntax member)
        => member switch
        {
            BaseTypeDeclarationSyntax n => n.AttributeLists,
            DelegateDeclarationSyntax n => n.AttributeLists,
            MethodDeclarationSyntax n => n.AttributeLists,
            ConstructorDeclarationSyntax n => n.AttributeLists,
            OperatorDeclarationSyntax n => n.AttributeLists,
            ConversionOperatorDeclarationSyntax n => n.AttributeLists,
            PropertyDeclarationSyntax n => n.AttributeLists,
            IndexerDeclarationSyntax n => n.AttributeLists,
            FieldDeclarationSyntax n => n.AttributeLists,
            EventFieldDeclarationSyntax n => n.AttributeLists,
            EventDeclarationSyntax n => n.AttributeLists,
            _ => default,
        };
}
