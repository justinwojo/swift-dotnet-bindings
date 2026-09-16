// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BindingsGeneration;

/// <summary>
/// Bounded reader for final generated C# call forms. It follows statically named calls from public
/// declarations/accessors to local helpers and native imports, and recognizes direct Swift function
/// pointer invocations inside those helpers. It is deliberately not a general call-graph analyzer.
/// </summary>
public static class GeneratedSwiftCallReader
{
    public static GeneratedSwiftCallScanResult ScanDirectory(
        string outputDirectory,
        IReadOnlyList<string>? preprocessorSymbols = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (!Directory.Exists(outputDirectory))
            throw new DirectoryNotFoundException(outputDirectory);

        var files = Directory.EnumerateFiles(outputDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        return Scan(files, outputDirectory, preprocessorSymbols);
    }

    public static GeneratedSwiftCallScanResult Scan(
        IReadOnlyList<string> sourceFiles,
        string evidenceRoot,
        IReadOnlyList<string>? preprocessorSymbols = null)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceRoot);

        var parseOptions = new CSharpParseOptions(
            LanguageVersion.Preview,
            preprocessorSymbols: preprocessorSymbols ?? []);
        var callables = new List<CallableDraft>();
        var publicMembers = new List<PublicMemberDraft>();
        var unresolved = new List<string>();

        foreach (var path in sourceFiles.OrderBy(path => path, StringComparer.Ordinal))
        {
            var source = File.ReadAllText(path);
            var tree = CSharpSyntaxTree.ParseText(source, parseOptions, path);
            var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
                unresolved.AddRange(errors.Select(error => $"{Path.GetFileName(path)}: {error}"));

            var root = tree.GetCompilationUnitRoot();
            CollectCallables(root, path, evidenceRoot, callables);
            CollectPublicMembers(root, path, evidenceRoot, publicMembers);
        }

        var callableIndex = callables
            .GroupBy(callable => (callable.Name, callable.ArgumentCount))
            .ToDictionary(group => group.Key, group => group.ToList());

        var observations = new List<DirectSwiftSelfObservation>();
        foreach (var member in publicMembers)
        {
            foreach (var root in member.Roots)
            {
                var bindings = ResolveNativeClosure(root, callableIndex, unresolved);
                foreach (var binding in bindings.Where(binding => binding.IsExposure))
                {
                    var native = binding.NativeCall;
                    if (native.CallKind == "FunctionPointer")
                    {
                        var slot = string.Concat(member.PublicApiKey, "#", root.Accessor ?? "member", "#", binding.Slot);
                        native = native with
                        {
                            ManagedHolder = member.ContainingType,
                            ManagedName = slot,
                            StableKey = slot,
                        };
                    }

                    observations.Add(new DirectSwiftSelfObservation
                    {
                        PublicApiKey = member.PublicApiKey,
                        PublicName = member.PublicName,
                        ContainingType = member.ContainingType,
                        DeclarationKind = member.DeclarationKind,
                        IsStatic = member.IsStatic,
                        Accessor = root.Accessor,
                        SourceMemberName = SourceMemberName(native.ManagedName, member.PublicName),
                        OriginalSwiftSymbol = native.EntryPoint is { } symbol && symbol.StartsWith("$s", StringComparison.Ordinal)
                            ? symbol
                            : null,
                        NativeCall = native,
                        Route = binding.Route,
                        SelfRole = binding.SelfRole,
                        HasSwiftError = binding.HasSwiftError,
                        GeneratedFile = member.GeneratedFile,
                        GeneratedSpan = member.GeneratedSpan,
                    });
                }
            }
        }

        var deduped = observations
            .GroupBy(observation => observation.IdentityKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(observation => observation.PublicApiKey, StringComparer.Ordinal)
            .ThenBy(observation => observation.Accessor ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(observation => observation.NativeCall.StableKey, StringComparer.Ordinal)
            .ToList();

        return new GeneratedSwiftCallScanResult
        {
            ScannedFileCount = sourceFiles.Count,
            InventoryHash = ComputeInventoryHash(sourceFiles, evidenceRoot),
            Observations = deduped,
            UnresolvedSpecimens = unresolved.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList(),
        };
    }

    private static void CollectCallables(
        CompilationUnitSyntax root,
        string path,
        string evidenceRoot,
        List<CallableDraft> callables)
    {
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            callables.Add(CreateCallable(method, method.Identifier.ValueText, method.ParameterList.Parameters.Count, path, evidenceRoot));
        foreach (var constructor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
            callables.Add(CreateCallable(constructor, constructor.Identifier.ValueText, constructor.ParameterList.Parameters.Count, path, evidenceRoot));
        foreach (var op in root.DescendantNodes().OfType<OperatorDeclarationSyntax>())
            callables.Add(CreateCallable(op, "operator " + op.OperatorToken.ValueText, op.ParameterList.Parameters.Count, path, evidenceRoot));
        foreach (var conversion in root.DescendantNodes().OfType<ConversionOperatorDeclarationSyntax>())
            callables.Add(CreateCallable(conversion, conversion.ImplicitOrExplicitKeyword.ValueText + " operator", conversion.ParameterList.Parameters.Count, path, evidenceRoot));

        foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            AddAccessorCallables(property, property.AccessorList, property.Identifier.ValueText, path, evidenceRoot, callables);
        foreach (var indexer in root.DescendantNodes().OfType<IndexerDeclarationSyntax>())
            AddAccessorCallables(indexer, indexer.AccessorList, "this", path, evidenceRoot, callables);
    }

    private static void AddAccessorCallables(
        MemberDeclarationSyntax owner,
        AccessorListSyntax? accessorList,
        string name,
        string path,
        string evidenceRoot,
        List<CallableDraft> callables)
    {
        if (accessorList is null)
        {
            callables.Add(CreateCallable(owner, name + "#get", 0, path, evidenceRoot, "get"));
            return;
        }

        foreach (var accessor in accessorList.Accessors)
        {
            var kind = accessor.Keyword.ValueText;
            callables.Add(CreateCallable(accessor, name + "#" + kind, 0, path, evidenceRoot, kind));
        }
    }

    private static CallableDraft CreateCallable(
        SyntaxNode node,
        string name,
        int argumentCount,
        string path,
        string evidenceRoot,
        string? accessor = null)
    {
        var owner = FullContainingType(node, includeArity: true);
        var @namespace = GetNamespace(node);
        var calls = node.DescendantNodes().OfType<InvocationExpressionSyntax>()
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

        var bindings = new List<NativeBindingDraft>();
        if (node is MethodDeclarationSyntax method && ParseImport(method, owner) is { } import)
            bindings.Add(import);
        bindings.AddRange(ParseFunctionPointerCalls(node, owner));
        return new CallableDraft(name, argumentCount, owner, @namespace, accessor, calls, bindings);
    }

    private static NativeBindingDraft? ParseImport(MethodDeclarationSyntax method, string owner)
    {
        var import = method.AttributeLists.SelectMany(list => list.Attributes).FirstOrDefault(attribute =>
        {
            var name = attribute.Name.ToString();
            return name.EndsWith("LibraryImport", StringComparison.Ordinal)
                || name.EndsWith("LibraryImportAttribute", StringComparison.Ordinal)
                || name.EndsWith("DllImport", StringComparison.Ordinal)
                || name.EndsWith("DllImportAttribute", StringComparison.Ordinal);
        });
        if (import is null)
            return null;

        var arguments = import.ArgumentList?.Arguments ?? default;
        var library = arguments.Count > 0 ? ConstantString(arguments[0].Expression) : "unknown";
        var entryArgument = arguments.FirstOrDefault(argument =>
            argument.NameEquals?.Name.Identifier.ValueText == "EntryPoint");
        var entryPoint = entryArgument is null
            ? method.Identifier.ValueText
            : ConstantString(entryArgument.Expression);
        var attributeText = method.AttributeLists.ToString();
        var convention = attributeText.Contains("CallConvSwift", StringComparison.Ordinal) ? "CallConvSwift"
            : attributeText.Contains("CallConvCdecl", StringComparison.Ordinal)
                || attributeText.Contains("CallingConvention.Cdecl", StringComparison.Ordinal) ? "CallConvCdecl"
            : "Unknown";
        var carriers = method.ParameterList.Parameters
            .Select(parameter => NormalizeParameter(parameter))
            .ToList();
        var untypedSelf = method.ParameterList.Parameters.Any(parameter => IsUntypedSwiftSelf(parameter.Type));
        var hasSwiftError = method.ParameterList.Parameters.Any(parameter => IsType(parameter.Type, "SwiftError"));
        var selfRole = method.ParameterList.Parameters.Any(parameter =>
            IsUntypedSwiftSelf(parameter.Type)
            && parameter.Identifier.ValueText.Contains("metatype", StringComparison.OrdinalIgnoreCase))
            ? "allocating_metatype"
            : "instance";
        var route = entryPoint.StartsWith("SBSW_", StringComparison.Ordinal)
            ? "swift_silgen_wrapper"
            : "swift_native";
        // NativeCallKey is deliberately independent of the generated holder/name. Two public
        // owners (or two hoisted helpers) can share one physical call and must remain two exposure
        // rows while rolling up as one native call.
        var stableKey = string.Join('\u001f', "PInvoke", library, entryPoint,
            convention, NormalizeType(method.ReturnType), string.Join(',', carriers));

        return new NativeBindingDraft(
            new DirectSwiftSelfNativeCall
            {
                CallKind = "PInvoke",
                ManagedHolder = owner,
                ManagedName = method.Identifier.ValueText,
                Library = library,
                EntryPoint = entryPoint,
                Convention = convention,
                ReturnCarrier = NormalizeType(method.ReturnType),
                ParameterCarriers = carriers,
                StableKey = stableKey,
            },
            convention == "CallConvSwift" && untypedSelf,
            route,
            selfRole,
            hasSwiftError,
            "import");
    }

    private static IEnumerable<NativeBindingDraft> ParseFunctionPointerCalls(SyntaxNode node, string owner)
    {
        var ordinal = 0;
        foreach (var pointer in node.DescendantNodes().OfType<FunctionPointerTypeSyntax>())
        {
            var text = pointer.ToString();
            if (!text.Contains("unmanaged[Swift]", StringComparison.Ordinal)
                && !text.Contains("CallConvSwift", StringComparison.Ordinal))
                continue;

            var parameters = pointer.ParameterList.Parameters;
            if (parameters.Count == 0)
                continue;
            var parameterCarriers = parameters.Take(parameters.Count - 1)
                .Select(parameter => NormalizeType(parameter.Type))
                .ToList();
            if (!parameters.Take(parameters.Count - 1).Any(parameter => IsUntypedSwiftSelf(parameter.Type)))
                continue;

            // A type mention alone is not a call. Require this exact cast/local slot to be the
            // invocation target; an unrelated call in the same helper must not turn a reverse
            // callback declaration or diagnostic type mention into managed-to-native exposure.
            if (!IsInvokedFunctionPointer(pointer, node))
                continue;

            ordinal++;
            var returnCarrier = NormalizeType(parameters[^1].Type);
            var slot = $"function-pointer-{ordinal}";
            yield return new NativeBindingDraft(
                new DirectSwiftSelfNativeCall
                {
                    CallKind = "FunctionPointer",
                    ManagedHolder = owner,
                    ManagedName = slot,
                    Convention = "CallConvSwift",
                    ReturnCarrier = returnCarrier,
                    ParameterCarriers = parameterCarriers,
                    StableKey = string.Join('\u001f', "FunctionPointer", owner, slot, returnCarrier,
                        string.Join(',', parameterCarriers)),
                },
                true,
                "swift_function_pointer",
                "closure_context",
                parameters.Take(parameters.Count - 1).Any(parameter => IsType(parameter.Type, "SwiftError")),
                slot);
        }
    }

    private static bool IsInvokedFunctionPointer(FunctionPointerTypeSyntax pointer, SyntaxNode callable)
    {
        foreach (var invocation in callable.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression.DescendantNodesAndSelf().Contains(pointer))
                return true;
        }

        var declarator = pointer.Ancestors().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(candidate => callable.Span.Contains(candidate.Span));
        if (declarator is null)
            return false;

        var localName = declarator.Identifier.ValueText;
        return callable.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(invocation =>
            invocation.Expression is IdentifierNameSyntax identifier
            && string.Equals(identifier.Identifier.ValueText, localName, StringComparison.Ordinal));
    }

    private static void CollectPublicMembers(
        CompilationUnitSyntax root,
        string path,
        string evidenceRoot,
        List<PublicMemberDraft> publicMembers)
    {
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(IsConsumerVisible))
            publicMembers.Add(PublicMethod(method, "method", method.Identifier.ValueText, method.ParameterList.Parameters, path, evidenceRoot));
        foreach (var constructor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>().Where(IsConsumerVisible))
            publicMembers.Add(PublicMethod(constructor, "constructor", constructor.Identifier.ValueText, constructor.ParameterList.Parameters, path, evidenceRoot));
        foreach (var op in root.DescendantNodes().OfType<OperatorDeclarationSyntax>().Where(IsConsumerVisible))
            publicMembers.Add(PublicMethod(op, "operator", "operator " + op.OperatorToken.ValueText, op.ParameterList.Parameters, path, evidenceRoot));
        foreach (var conversion in root.DescendantNodes().OfType<ConversionOperatorDeclarationSyntax>().Where(IsConsumerVisible))
            publicMembers.Add(PublicMethod(conversion, "conversion", conversion.ImplicitOrExplicitKeyword.ValueText + " operator", conversion.ParameterList.Parameters, path, evidenceRoot));

        foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>().Where(IsConsumerVisible))
            publicMembers.Add(PublicProperty(property, property.Identifier.ValueText, property.Type, property.AccessorList, path, evidenceRoot));
        foreach (var indexer in root.DescendantNodes().OfType<IndexerDeclarationSyntax>().Where(IsConsumerVisible))
            publicMembers.Add(PublicProperty(indexer, "this", indexer.Type, indexer.AccessorList, path, evidenceRoot, indexer.ParameterList.Parameters));
    }

    private static PublicMemberDraft PublicMethod(
        MemberDeclarationSyntax node,
        string kind,
        string name,
        SeparatedSyntaxList<ParameterSyntax> parameters,
        string path,
        string evidenceRoot)
    {
        var owner = FullContainingType(node, includeArity: false);
        var publicKey = $"{FullContainingType(node, includeArity: true)}.{name}({string.Join(',', parameters.Select(NormalizeParameter))})";
        if (node is ConversionOperatorDeclarationSyntax conversion)
            publicKey += "->" + NormalizeType(conversion.Type);
        var callable = CreateCallable(node, name, parameters.Count, path, evidenceRoot);
        return new PublicMemberDraft(publicKey, name, owner, kind,
            GetModifiers(node).Any(token => token.IsKind(SyntaxKind.StaticKeyword)),
            [callable], Relative(path, evidenceRoot), Span(node));
    }

    private static PublicMemberDraft PublicProperty(
        MemberDeclarationSyntax node,
        string name,
        TypeSyntax type,
        AccessorListSyntax? accessorList,
        string path,
        string evidenceRoot,
        SeparatedSyntaxList<ParameterSyntax> indexParameters = default)
    {
        var owner = FullContainingType(node, includeArity: false);
        var signature = indexParameters.Count == 0
            ? name
            : $"this[{string.Join(',', indexParameters.Select(NormalizeParameter))}]";
        var publicKey = $"{FullContainingType(node, includeArity: true)}.{signature}:{NormalizeType(type)}";
        var roots = new List<CallableDraft>();
        if (accessorList is null)
        {
            roots.Add(CreateCallable(node, name + "#get", 0, path, evidenceRoot, "get"));
        }
        else
        {
            foreach (var accessor in accessorList.Accessors.Where(accessor => IsAccessibleAccessor(accessor, node)))
            {
                var kind = accessor.Keyword.ValueText;
                roots.Add(CreateCallable(accessor, name + "#" + kind, 0, path, evidenceRoot, kind));
            }
        }
        return new PublicMemberDraft(publicKey, name, owner, indexParameters.Count == 0 ? "property" : "subscript",
            GetModifiers(node).Any(token => token.IsKind(SyntaxKind.StaticKeyword)),
            roots, Relative(path, evidenceRoot), Span(node));
    }

    private static IReadOnlyList<NativeBindingDraft> ResolveNativeClosure(
        CallableDraft root,
        IReadOnlyDictionary<(string Name, int ArgumentCount), List<CallableDraft>> index,
        List<string> unresolved)
    {
        var result = new List<NativeBindingDraft>();
        var queue = new Queue<CallableDraft>();
        var visited = new HashSet<CallableDraft>(ReferenceEqualityComparer.Instance);
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current))
                continue;
            result.AddRange(current.Bindings);

            foreach (var call in current.Calls)
            {
                if (!index.TryGetValue((call.Name, call.ArgumentCount), out var candidates))
                    continue;
                var resolved = candidates.Where(candidate => candidate.Owner == current.Owner).ToList();
                if (call.Qualifier is { } rawQualifier)
                {
                    var qualifier = rawQualifier.Replace("global::", string.Empty, StringComparison.Ordinal);
                    if (qualifier is not ("this" or "base"))
                    {
                        var possibleOwners = new HashSet<string>(StringComparer.Ordinal) { qualifier };
                        if (!string.IsNullOrEmpty(current.Namespace))
                            possibleOwners.Add(current.Namespace + "." + qualifier);
                        possibleOwners.Add(current.Owner + "." + qualifier);
                        var separator = current.Owner.LastIndexOf('.');
                        if (separator >= 0)
                            possibleOwners.Add(current.Owner[..separator] + "." + qualifier);
                        resolved = candidates.Where(candidate => possibleOwners.Contains(candidate.Owner)).ToList();
                    }
                }

                if (resolved.Count == 1)
                    queue.Enqueue(resolved[0]);
                else if (resolved.Count > 1 && resolved.Any(candidate => candidate.Bindings.Any(binding => binding.IsExposure)))
                    unresolved.Add($"{root.Owner}.{root.Name}: ambiguous call {call.Name}/{call.ArgumentCount}");
            }
        }
        return result;
    }

    private static bool IsConsumerVisible(MemberDeclarationSyntax member)
    {
        var explicitInterface = member is MethodDeclarationSyntax { ExplicitInterfaceSpecifier: not null }
            or PropertyDeclarationSyntax { ExplicitInterfaceSpecifier: not null }
            or IndexerDeclarationSyntax { ExplicitInterfaceSpecifier: not null };
        if (!explicitInterface && !IsAccessible(GetAccessibility(member)))
            return false;
        return member.Ancestors().OfType<BaseTypeDeclarationSyntax>()
            .All(type => IsAccessible(GetAccessibility(type)));
    }

    private static bool IsAccessibleAccessor(AccessorDeclarationSyntax accessor, MemberDeclarationSyntax owner)
        => accessor.Modifiers.Count == 0
            ? IsAccessible(GetAccessibility(owner))
            : IsAccessible(GetAccessibility(accessor.Modifiers, false, false));

    private static bool IsAccessible(string accessibility)
        => accessibility is "public" or "protected" or "protected internal";

    private static string GetAccessibility(MemberDeclarationSyntax member)
    {
        var inInterface = member.Parent is InterfaceDeclarationSyntax;
        var isTopLevelType = member is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax
            && member.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax;
        return GetAccessibility(GetModifiers(member), isTopLevelType, inInterface);
    }

    private static string GetAccessibility(SyntaxTokenList modifiers, bool isTopLevelType, bool inInterface)
    {
        var isPublic = modifiers.Any(token => token.IsKind(SyntaxKind.PublicKeyword));
        var isProtected = modifiers.Any(token => token.IsKind(SyntaxKind.ProtectedKeyword));
        var isInternal = modifiers.Any(token => token.IsKind(SyntaxKind.InternalKeyword));
        var isPrivate = modifiers.Any(token => token.IsKind(SyntaxKind.PrivateKeyword));
        if (isPublic) return "public";
        if (isProtected && isInternal) return "protected internal";
        if (isPrivate && isProtected) return "private protected";
        if (isProtected) return "protected";
        if (isInternal) return "internal";
        if (isPrivate) return "private";
        if (inInterface) return "public";
        return isTopLevelType ? "internal" : "private";
    }

    private static SyntaxTokenList GetModifiers(MemberDeclarationSyntax member) => member switch
    {
        BaseTypeDeclarationSyntax node => node.Modifiers,
        DelegateDeclarationSyntax node => node.Modifiers,
        MethodDeclarationSyntax node => node.Modifiers,
        ConstructorDeclarationSyntax node => node.Modifiers,
        OperatorDeclarationSyntax node => node.Modifiers,
        ConversionOperatorDeclarationSyntax node => node.Modifiers,
        PropertyDeclarationSyntax node => node.Modifiers,
        IndexerDeclarationSyntax node => node.Modifiers,
        _ => default,
    };

    private static string FullContainingType(SyntaxNode node, bool includeArity)
    {
        var @namespace = GetNamespace(node);
        var types = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(type =>
        {
            var name = type.Identifier.ValueText;
            if (!includeArity || type is not TypeDeclarationSyntax declaration)
                return name;
            var arity = declaration.TypeParameterList?.Parameters.Count ?? 0;
            return arity == 0 ? name : $"{name}`{arity}";
        });
        var owner = string.Join('.', types);
        return string.IsNullOrEmpty(@namespace) ? owner
            : string.IsNullOrEmpty(owner) ? @namespace
            : @namespace + "." + owner;
    }

    private static string GetNamespace(SyntaxNode node)
        => string.Join('.', node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse().Select(@namespace => @namespace.Name.ToString()));

    private static bool IsUntypedSwiftSelf(TypeSyntax? type)
        => type is not null
            && type.DescendantNodesAndSelf().OfType<GenericNameSyntax>()
                .All(generic => generic.Identifier.ValueText != "SwiftSelf")
            && IsType(type, "SwiftSelf");

    private static bool IsType(TypeSyntax? type, string name)
    {
        if (type is null)
            return false;
        var normalized = NormalizeType(type);
        return string.Equals(normalized, name, StringComparison.Ordinal)
            || normalized.EndsWith('.' + name, StringComparison.Ordinal);
    }

    private static string NormalizeParameter(ParameterSyntax parameter)
    {
        var modifier = parameter.Modifiers.FirstOrDefault(token =>
            token.IsKind(SyntaxKind.RefKeyword)
            || token.IsKind(SyntaxKind.OutKeyword)
            || token.IsKind(SyntaxKind.InKeyword)).ValueText;
        var type = parameter.Type is null ? "unknown" : NormalizeType(parameter.Type);
        return string.IsNullOrEmpty(modifier) ? type : modifier + " " + type;
    }

    private static string NormalizeType(TypeSyntax type)
    {
        var text = type.NormalizeWhitespace().ToFullString()
            .Replace("global::", string.Empty, StringComparison.Ordinal);
        return Regex.Replace(text, @"\s*([<>,?\[\]\(\)\*&:])\s*", "$1");
    }

    private static string ConstantString(ExpressionSyntax expression)
    {
        if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            return literal.Token.ValueText;
        return expression.ToString();
    }

    private static string SourceMemberName(string nativeManagedName, string publicName)
    {
        if (!nativeManagedName.StartsWith("PInvoke_", StringComparison.Ordinal))
            return LowerFirst(publicName);
        var name = nativeManagedName["PInvoke_".Length..];
        var accessor = Regex.Match(name, @"^(?<name>.+)_(?:Get|Set)(?:_|$)");
        if (accessor.Success)
            return accessor.Groups["name"].Value;
        var hash = Regex.Match(name, @"^(?<name>.+)_[0-9A-F]{8}$");
        return hash.Success ? hash.Groups["name"].Value : name;
    }

    private static string LowerFirst(string name)
        => string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];

    private static GeneratedSourceSpan Span(SyntaxNode node)
    {
        var span = node.SyntaxTree.GetLineSpan(node.Span);
        return new GeneratedSourceSpan
        {
            StartLine = span.StartLinePosition.Line + 1,
            EndLine = span.EndLinePosition.Line + 1,
        };
    }

    private static string Relative(string path, string root)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string ComputeInventoryHash(IReadOnlyList<string> sourceFiles, string evidenceRoot)
    {
        using var sha = SHA256.Create();
        var builder = new StringBuilder();
        foreach (var path in sourceFiles.OrderBy(path => path, StringComparer.Ordinal))
        {
            builder.Append(Relative(path, evidenceRoot));
            builder.Append('\0');
            builder.Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
            builder.Append('\n');
        }
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private sealed record PublicMemberDraft(
        string PublicApiKey,
        string PublicName,
        string ContainingType,
        string DeclarationKind,
        bool IsStatic,
        IReadOnlyList<CallableDraft> Roots,
        string GeneratedFile,
        GeneratedSourceSpan GeneratedSpan);

    private sealed record CallableDraft(
        string Name,
        int ArgumentCount,
        string Owner,
        string Namespace,
        string? Accessor,
        IReadOnlyList<CallSite> Calls,
        IReadOnlyList<NativeBindingDraft> Bindings);

    private sealed record CallSite(string Name, int ArgumentCount, string? Qualifier);

    private sealed record NativeBindingDraft(
        DirectSwiftSelfNativeCall NativeCall,
        bool IsExposure,
        string Route,
        string SelfRole,
        bool HasSwiftError,
        string Slot);
}
