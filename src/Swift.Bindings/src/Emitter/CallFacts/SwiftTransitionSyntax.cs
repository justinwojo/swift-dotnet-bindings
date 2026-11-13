// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BindingsGeneration;

/// <summary>One managed↔Swift transition site the emitted C# contains, named the way the IL names it.</summary>
/// <param name="Member">The IL member the site belongs to (see <see cref="SwiftTransitionSyntax"/>).</param>
/// <param name="Method">For an import, its C# method name; otherwise the member's own method name.</param>
/// <param name="EntryPoint">For an import, the bound symbol; otherwise empty.</param>
/// <param name="Library">For an import, its library; otherwise empty.</param>
/// <param name="Target">For a callback address, the IL member of the callback it takes the address of.</param>
/// <param name="CallbackName">For a callback or a callback address, the callback's method name.</param>
/// <param name="FunctionPointerType">For a <c>calli</c>, the function-pointer type it calls through, as written.</param>
/// <param name="Carriers">For a forward transition, whether its signature carries only primitives.</param>
/// <param name="Specials">For a non-Swift import, the Swift register types its signature names.</param>
/// <param name="Signature">The managed signature the site passes: an import's or a callback's declaration, a <c>calli</c>'s pointer type.</param>
internal sealed record SwiftTransitionSite(
    SwiftTransitionKind Kind,
    string Member,
    string Method,
    string EntryPoint = "",
    string Library = "",
    string? Target = null,
    string? CallbackName = null,
    string? FunctionPointerType = null,
    SwiftTransitionCarriers? Carriers = null,
    ImmutableArray<string> Specials = default,
    SwiftManagedSignature? Signature = null);

/// <summary>
/// Reads the transition sites out of a module's emitted C#: forward Swift-convention imports, imports
/// of another convention that name a Swift register type, Swift-convention
/// <c>[UnmanagedCallersOnly]</c> callbacks, the places their addresses are taken, and calls through an
/// <c>unmanaged[Swift]</c> function pointer. Each is named as the compiled IL names the member that
/// holds it, so the ledger built from them joins against an IL inventory.
/// </summary>
/// <remarks>
/// <para>
/// Naming follows the C# compiler: a site in a method is <c>Type::Method</c>; in an accessor,
/// <c>Type::get_Name</c>; in a constructor, <c>.ctor</c> or <c>.cctor</c>; in a field or property
/// initializer, the constructor that runs it; in a lambda, <c>Type::Outer.&lt;lambda&gt;</c>; in a local
/// function, <c>Type::Outer.Local</c>. A lambda or local function is named after its innermost
/// enclosing function, as the IL is. The IL places a lambda in a closure class
/// (<c>+&lt;&gt;c__DisplayClassN_M</c> or <c>+&lt;&gt;c</c>) and an async or iterator body in a state
/// machine; the consumer of the IL folds those back into the declaring type.
/// </para>
/// <para>
/// Carriers are classified from syntax: a predefined numeric type, <c>bool</c>, <c>char</c>, a pointer
/// other than <c>SwiftError*</c>, a function pointer, a by-reference parameter, an enum the source
/// declares, the one-word runtime wrappers and, in a <c>LibraryImport</c>, a <c>SafeHandle</c> (which
/// its source generator passes as its handle) are primitive; everything else is not. The IL
/// classifier is the authority, so a cross-check that compares both catches any drift between them.
/// </para>
/// </remarks>
internal static class SwiftTransitionSyntax
{
    /// <summary>The Swift register types a C-convention signature must not carry.</summary>
    private static readonly HashSet<string> RegisterTypes = new(StringComparer.Ordinal) { "SwiftSelf", "SwiftIndirectResult" };

    /// <summary>The one-word runtime wrappers a primitive-only signature may carry, by simple name.</summary>
    private static readonly HashSet<string> OneWordWrappers = new(StringComparer.Ordinal)
    {
        "TypeMetadata", "TypeMetadataRequest", "ProtocolWitnessTable", "ProtocolDescriptor",
        "ProtocolConformanceDescriptor", "UnsafeRawPointer", "UnsafeMutableRawPointer", "UnsafePointer",
        "UnsafeMutablePointer",
    };

    private static readonly HashSet<string> PrimitiveStructs = new(StringComparer.Ordinal)
    {
        "nint", "nuint", "IntPtr", "UIntPtr", "Boolean", "Char", "Byte", "SByte", "Int16", "UInt16", "Int32", "UInt32",
        "Int64", "UInt64", "Single", "Double",
    };

    /// <summary>
    /// Managed types an import's source generator lowers to a primitive before the transition: a
    /// <c>SafeHandle</c> crosses as its handle, so the transition itself carries a pointer-sized integer.
    /// </summary>
    private static readonly HashSet<string> LoweredToPrimitive = new(StringComparer.Ordinal) { "SafeHandle" };

    public static List<SwiftTransitionSite> Scan(string source, List<string> issues) => Scan(source, issues, null);

    /// <summary>The sites of <paramref name="source"/>; the types it declares are added to <paramref name="types"/>.</summary>
    public static List<SwiftTransitionSite> Scan(string source, List<string> issues, SwiftManagedTypeIndex? types)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var root = tree.GetRoot();
        types?.Add(root);
        var enums = root.DescendantNodes().OfType<EnumDeclarationSyntax>().Select(e => e.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
        var sites = new List<SwiftTransitionSite>();
        var callbacks = new Dictionary<string, List<MethodDeclarationSyntax>>(StringComparer.Ordinal);

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var attributes = method.AttributeLists.SelectMany(l => l.Attributes).ToList();
            var import = attributes.FirstOrDefault(a => IsAttribute(a, "LibraryImport") || IsAttribute(a, "DllImport"));
            if (import is not null)
            {
                ScanImport(method, import, attributes, enums, sites, issues);
                continue;
            }
            var uco = attributes.FirstOrDefault(a => IsAttribute(a, "UnmanagedCallersOnly"));
            if (uco is not null && NamesCallConvSwift(uco))
            {
                var name = method.Identifier.ValueText;
                sites.Add(new SwiftTransitionSite(SwiftTransitionKind.ReverseCallback, MemberOf(method), name, CallbackName: name,
                    Signature: SwiftManagedSignature.Of(method, SwiftManagedMarshalling.None)));
                if (!callbacks.TryGetValue(name, out var list))
                    callbacks[name] = list = new List<MethodDeclarationSyntax>();
                list.Add(method);
            }
        }

        foreach (var address in root.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>().Where(p => p.IsKind(SyntaxKind.AddressOfExpression)))
        {
            var name = address.Operand switch
            {
                IdentifierNameSyntax id => id.Identifier.ValueText,
                MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
                _ => null,
            };
            if (name is null || !callbacks.TryGetValue(name, out var candidates))
                continue;
            var target = ResolveCallback(address, candidates);
            if (target is null)
            {
                issues.Add($"{ContainingMember(address)}: the address of '{name}' names {candidates.Count} Swift-convention callbacks; it is not listed");
                continue;
            }
            sites.Add(new SwiftTransitionSite(
                SwiftTransitionKind.CallbackAddress, ContainingMember(address), name, Target: MemberOf(target), CallbackName: name));
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var pointer = SwiftFunctionPointerCalled(invocation);
            if (pointer is null)
                continue;
            sites.Add(new SwiftTransitionSite(
                SwiftTransitionKind.ForwardCalli, ContainingMember(invocation), "", FunctionPointerType: pointer.ToString(),
                Carriers: pointer.ParameterList.Parameters.All(p => IsPrimitive(p.Type, p.Modifiers, enums))
                    ? SwiftTransitionCarriers.Primitive
                    : SwiftTransitionCarriers.NonPrimitive,
                Signature: SwiftManagedSignature.Of(pointer, invocation)));
        }

        // A site in an inactive region is not in the syntax tree; say so rather than miss it.
        foreach (var trivia in root.DescendantTrivia().Where(t => t.IsKind(SyntaxKind.DisabledTextTrivia)))
        {
            var text = trivia.ToString();
            if (text.Contains("CallConvSwift", StringComparison.Ordinal) || text.Contains("unmanaged[Swift]", StringComparison.Ordinal))
                issues.Add($"a Swift-convention declaration sits in an inactive preprocessor region at line {trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1} and is not listed");
        }

        return sites;
    }

    private static void ScanImport(
        MethodDeclarationSyntax method, AttributeSyntax import, List<AttributeSyntax> attributes, HashSet<string> enums,
        List<SwiftTransitionSite> sites, List<string> issues)
    {
        var callConv = attributes.FirstOrDefault(a => IsAttribute(a, "UnmanagedCallConv"));
        var isSwift = callConv is not null && NamesCallConvSwift(callConv);
        var specials = isSwift ? ImmutableArray<string>.Empty : RegisterTypesOf(method);
        if (!isSwift && specials.IsEmpty)
            return;

        var arguments = import.ArgumentList?.Arguments ?? default;
        // A literal, or a named constant (hand-written imports name KnownLibraries members), by its spelling.
        var library = arguments.FirstOrDefault(a => a.NameEquals is null && a.NameColon is null)?.Expression switch
        {
            LiteralExpressionSyntax lib => lib.Token.ValueText,
            MemberAccessExpressionSyntax constant => constant.ToString(),
            _ => null,
        };
        var entryArgument = arguments.FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == "EntryPoint");
        var entryPoint = entryArgument?.Expression is LiteralExpressionSyntax ep ? ep.Token.ValueText : method.Identifier.ValueText;
        if (library is null)
        {
            issues.Add($"{MemberOf(method)}: an import whose library is neither a literal nor a named constant is not listed");
            return;
        }

        // A source-generated import lowers its marshalled types before the transition; a runtime
        // (DllImport) one carries them as declared.
        var lowered = IsAttribute(import, "LibraryImport");
        var primitive = IsPrimitive(method.ReturnType, default, enums, lowered)
            && method.ParameterList.Parameters.All(p => p.Type is not null && IsPrimitive(p.Type, p.Modifiers, enums, lowered));
        sites.Add(new SwiftTransitionSite(
            isSwift ? SwiftTransitionKind.ForwardPInvoke : SwiftTransitionKind.CdeclSpecialPInvoke,
            MemberOf(method), method.Identifier.ValueText, entryPoint, library,
            Carriers: primitive ? SwiftTransitionCarriers.Primitive : SwiftTransitionCarriers.NonPrimitive,
            Specials: specials,
            Signature: SwiftManagedSignature.Of(method, lowered ? SwiftManagedMarshalling.SourceGenerated : SwiftManagedMarshalling.Runtime)));
    }

    /// <summary>The Swift register types a signature names, by simple name, in signature order.</summary>
    private static ImmutableArray<string> RegisterTypesOf(MethodDeclarationSyntax method) =>
        method.ParameterList.Parameters.Select(p => p.Type).Prepend(method.ReturnType)
            .Where(t => t is not null && RegisterTypes.Contains(SimpleName(t)))
            .Select(t => SimpleName(t!))
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();

    internal static bool NamesCallConvSwift(AttributeSyntax attribute) =>
        attribute.DescendantNodes().OfType<TypeOfExpressionSyntax>().Any(t => SimpleName(t.Type) == "CallConvSwift");

    /// <summary>
    /// The <c>unmanaged[Swift]</c> function-pointer type an invocation calls through, or null: a
    /// parenthesized cast to one, or a local, parameter or field declared as one (or a <c>var</c>
    /// initialized by a cast to one) in scope of the call.
    /// </summary>
    internal static FunctionPointerTypeSyntax? SwiftFunctionPointerCalled(InvocationExpressionSyntax invocation)
    {
        switch (invocation.Expression)
        {
            case ParenthesizedExpressionSyntax { Expression: CastExpressionSyntax cast }:
                return AsSwiftFunctionPointer(cast.Type);
            case IdentifierNameSyntax id:
                var name = id.Identifier.ValueText;
                for (SyntaxNode? scope = invocation.Parent; scope is not null; scope = scope.Parent)
                {
                    if (scope is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax
                        or AccessorDeclarationSyntax or TypeDeclarationSyntax)
                    {
                        if (FindDeclaredType(scope, name, invocation) is { } declared)
                            return AsSwiftFunctionPointer(declared);
                    }
                    if (scope is TypeDeclarationSyntax)
                        break;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>The type <paramref name="name"/> is declared with directly in <paramref name="scope"/>, if any.</summary>
    private static TypeSyntax? FindDeclaredType(SyntaxNode scope, string name, SyntaxNode use)
    {
        IEnumerable<ParameterSyntax> parameters = scope switch
        {
            BaseMethodDeclarationSyntax m => m.ParameterList.Parameters,
            LocalFunctionStatementSyntax l => l.ParameterList.Parameters,
            ParenthesizedLambdaExpressionSyntax p => p.ParameterList.Parameters,
            SimpleLambdaExpressionSyntax s => new[] { s.Parameter },
            AnonymousMethodExpressionSyntax a when a.ParameterList is not null => a.ParameterList.Parameters,
            _ => Array.Empty<ParameterSyntax>(),
        };
        if (parameters.FirstOrDefault(p => p.Identifier.ValueText == name) is { Type: { } parameterType })
            return parameterType;

        if (scope is TypeDeclarationSyntax type)
        {
            foreach (var field in type.Members.OfType<FieldDeclarationSyntax>())
                if (field.Declaration.Variables.Any(v => v.Identifier.ValueText == name))
                    return field.Declaration.Type;
            return null;
        }

        // A local declared in this function's own body (not in a nested function) before the use.
        foreach (var declaration in scope.DescendantNodes(n => n == scope || !IsFunction(n)).OfType<VariableDeclarationSyntax>())
        {
            if (declaration.SpanStart > use.SpanStart)
                continue;
            var variable = declaration.Variables.FirstOrDefault(v => v.Identifier.ValueText == name);
            if (variable is null)
                continue;
            if (!declaration.Type.IsVar)
                return declaration.Type;
            return variable.Initializer?.Value is CastExpressionSyntax cast ? cast.Type : null;
        }
        return null;
    }

    private static bool IsFunction(SyntaxNode node) =>
        node is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax;

    private static FunctionPointerTypeSyntax? AsSwiftFunctionPointer(TypeSyntax type) =>
        type is FunctionPointerTypeSyntax fp
        && fp.CallingConvention?.UnmanagedCallingConventionList?.CallingConventions.Any(c => c.Name.ValueText == "Swift") == true
            ? fp
            : null;

    /// <summary>The callback an address names: the one in the address's own type chain, else the only one.</summary>
    private static MethodDeclarationSyntax? ResolveCallback(SyntaxNode address, List<MethodDeclarationSyntax> candidates)
    {
        for (var type = address.FirstAncestorOrSelf<TypeDeclarationSyntax>(); type is not null; type = type.Parent?.FirstAncestorOrSelf<TypeDeclarationSyntax>())
        {
            var local = candidates.Where(c => c.Parent == type).ToList();
            if (local.Count == 1)
                return local[0];
            if (local.Count > 1)
                return null;
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }

    internal static bool IsPrimitive(TypeSyntax type, SyntaxTokenList modifiers, HashSet<string> enums, bool lowered = false)
    {
        var byReference = modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword) || m.IsKind(SyntaxKind.OutKeyword) || m.IsKind(SyntaxKind.InKeyword));
        if (type is RefTypeSyntax refType)
        {
            byReference = true;
            type = refType.Type;
        }
        switch (type)
        {
            case PointerTypeSyntax pointer:
                return SimpleName(pointer.ElementType) != "SwiftError";
            case FunctionPointerTypeSyntax:
                return true;
            case PredefinedTypeSyntax predefined:
                return byReference || !predefined.Keyword.IsKind(SyntaxKind.StringKeyword)
                    && !predefined.Keyword.IsKind(SyntaxKind.ObjectKeyword)
                    && !predefined.Keyword.IsKind(SyntaxKind.DecimalKeyword);
            case NameSyntax or AliasQualifiedNameSyntax:
                var simple = SimpleName(type);
                if (byReference)
                    return simple != "SwiftError";
                return PrimitiveStructs.Contains(simple) || OneWordWrappers.Contains(simple) || enums.Contains(simple)
                    || (lowered && LoweredToPrimitive.Contains(simple));
            default:
                return false;
        }
    }

    internal static bool IsAttribute(AttributeSyntax attribute, string name)
    {
        var simple = SimpleName(attribute.Name);
        return simple == name || simple == name + "Attribute";
    }

    internal static string SimpleName(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
        SimpleNameSyntax s => s.Identifier.ValueText,
        _ => type.ToString(),
    };

    // ---- naming, as the IL does ----

    /// <summary>A method declaration's IL member: <c>Namespace.Outer+Inner::Method</c>, generic types with their arity.</summary>
    internal static string MemberOf(MethodDeclarationSyntax method) => $"{TypePathOf(method)}::{method.Identifier.ValueText}";

    /// <summary>The IL member whose body holds <paramref name="node"/>.</summary>
    internal static string ContainingMember(SyntaxNode node)
    {
        string? suffix = null;
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case LocalFunctionStatementSyntax local:
                    suffix ??= "." + local.Identifier.ValueText;
                    break;
                case AnonymousFunctionExpressionSyntax:
                    suffix ??= ".<lambda>";
                    break;
                case AccessorDeclarationSyntax accessor when accessor.Parent?.Parent is MemberDeclarationSyntax owner:
                    return $"{TypePathOf(owner)}::{AccessorMethodName(accessor, owner)}{suffix}";
                case MemberDeclarationSyntax member when MethodNameOf(member) is { } name:
                    return $"{TypePathOf(member)}::{name}{suffix}";
            }
        }
        return $"<global>::{suffix}";
    }

    /// <summary>The IL method a member declaration's code lands in, or null for a declaration that holds no code.</summary>
    internal static string? MethodNameOf(MemberDeclarationSyntax member)
    {
        static bool IsStatic(MemberDeclarationSyntax m) => m.Modifiers.Any(t => t.IsKind(SyntaxKind.StaticKeyword) || t.IsKind(SyntaxKind.ConstKeyword));
        return member switch
        {
            MethodDeclarationSyntax m => m.Identifier.ValueText,
            ConstructorDeclarationSyntax c => IsStatic(c) ? ".cctor" : ".ctor",
            DestructorDeclarationSyntax => "Finalize",
            OperatorDeclarationSyntax o => OperatorMethodName(o),
            ConversionOperatorDeclarationSyntax c => c.ImplicitOrExplicitKeyword.IsKind(SyntaxKind.ImplicitKeyword) ? "op_Implicit" : "op_Explicit",
            PropertyDeclarationSyntax p when p.ExpressionBody is not null => "get_" + p.Identifier.ValueText,
            PropertyDeclarationSyntax p when p.Initializer is not null => IsStatic(p) ? ".cctor" : ".ctor",
            IndexerDeclarationSyntax i when i.ExpressionBody is not null => "get_Item",
            BaseFieldDeclarationSyntax f => IsStatic(f) ? ".cctor" : ".ctor",
            _ => null,
        };
    }

    /// <summary>An accessor's method: <c>get_Name</c>, <c>set_Name</c> (also for <c>init</c>), <c>add_Name</c>, <c>remove_Name</c>; an indexer's is named <c>Item</c>.</summary>
    internal static string AccessorMethodName(AccessorDeclarationSyntax accessor, MemberDeclarationSyntax owner)
    {
        var prefix = accessor.Keyword.Kind() switch
        {
            SyntaxKind.GetKeyword => "get_",
            SyntaxKind.SetKeyword or SyntaxKind.InitKeyword => "set_",
            SyntaxKind.AddKeyword => "add_",
            SyntaxKind.RemoveKeyword => "remove_",
            _ => accessor.Keyword.ValueText + "_",
        };
        var name = owner switch
        {
            PropertyDeclarationSyntax p => p.Identifier.ValueText,
            EventDeclarationSyntax e => e.Identifier.ValueText,
            IndexerDeclarationSyntax => "Item",
            _ => "?",
        };
        return prefix + name;
    }

    /// <summary>The IL name of the type that declares <paramref name="node"/>: <c>Namespace.Outer+Inner</c>, generic types with their arity.</summary>
    internal static string TypePathOf(SyntaxNode node)
    {
        var types = new List<string>();
        var namespaces = new List<string>();
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case BaseTypeDeclarationSyntax type:
                    var arity = (type as TypeDeclarationSyntax)?.TypeParameterList?.Parameters.Count ?? 0;
                    types.Add(arity > 0 ? $"{type.Identifier.ValueText}`{arity}" : type.Identifier.ValueText);
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    namespaces.Add(NamespaceOf(ns.Name));
                    break;
            }
        }
        types.Reverse();
        namespaces.Reverse();
        var typePath = string.Join("+", types);
        return namespaces.Count > 0 ? string.Join(".", namespaces) + "." + typePath : typePath;
    }

    private static string OperatorMethodName(OperatorDeclarationSyntax op) => op.OperatorToken.Kind() switch
    {
        SyntaxKind.PlusToken => op.ParameterList.Parameters.Count == 1 ? "op_UnaryPlus" : "op_Addition",
        SyntaxKind.MinusToken => op.ParameterList.Parameters.Count == 1 ? "op_UnaryNegation" : "op_Subtraction",
        SyntaxKind.AsteriskToken => "op_Multiply",
        SyntaxKind.SlashToken => "op_Division",
        SyntaxKind.PercentToken => "op_Modulus",
        SyntaxKind.AmpersandToken => "op_BitwiseAnd",
        SyntaxKind.BarToken => "op_BitwiseOr",
        SyntaxKind.CaretToken => "op_ExclusiveOr",
        SyntaxKind.LessThanLessThanToken => "op_LeftShift",
        SyntaxKind.GreaterThanGreaterThanToken => "op_RightShift",
        SyntaxKind.GreaterThanGreaterThanGreaterThanToken => "op_UnsignedRightShift",
        SyntaxKind.EqualsEqualsToken => "op_Equality",
        SyntaxKind.ExclamationEqualsToken => "op_Inequality",
        SyntaxKind.LessThanToken => "op_LessThan",
        SyntaxKind.GreaterThanToken => "op_GreaterThan",
        SyntaxKind.LessThanEqualsToken => "op_LessThanOrEqual",
        SyntaxKind.GreaterThanEqualsToken => "op_GreaterThanOrEqual",
        SyntaxKind.ExclamationToken => "op_LogicalNot",
        SyntaxKind.TildeToken => "op_OnesComplement",
        SyntaxKind.PlusPlusToken => "op_Increment",
        SyntaxKind.MinusMinusToken => "op_Decrement",
        SyntaxKind.TrueKeyword => "op_True",
        SyntaxKind.FalseKeyword => "op_False",
        _ => "op_" + op.OperatorToken.ValueText,
    };

    /// <summary>A namespace name as metadata spells it: identifiers without their <c>@</c> escapes.</summary>
    private static string NamespaceOf(NameSyntax name) => name switch
    {
        QualifiedNameSyntax q => NamespaceOf(q.Left) + "." + q.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
        SimpleNameSyntax s => s.Identifier.ValueText,
        _ => name.ToString(),
    };

    /// <summary>
    /// A function-pointer type's spelling with whitespace, <c>global::</c> and namespace qualifiers
    /// removed, so a producer's unqualified spelling matches the qualified one it rendered.
    /// </summary>
    internal static string NormalizeFunctionPointerType(string spelling)
    {
        var compact = Regex.Replace(spelling, @"\s+", "").Replace("global::", "", StringComparison.Ordinal);
        return Regex.Replace(compact, @"(?<![\w])(?:[A-Za-z_]\w*\.)+(?=[A-Za-z_])", "");
    }
}
