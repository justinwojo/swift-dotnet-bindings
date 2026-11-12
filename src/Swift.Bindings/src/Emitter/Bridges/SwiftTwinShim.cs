// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BindingsGeneration;

/// <summary>
/// Writes the managed half of a bridged transition: the shim that replaces a Swift-convention import
/// under its own name and signature, and the C-convention twin import of the bridge it calls.
/// </summary>
/// <remarks>
/// The twin's arguments are the plan's twin arguments in order. An ordinary primitive keeps its
/// declaration (type, by-reference modifier and <c>MarshalAs</c>), so the twin's source generator
/// marshals it exactly as the import's did; an aggregate becomes one parameter per piece the plan
/// lists, or the address of the value when the plan passes it indirectly; the trailing pointers —
/// indirect result, result buffer, self, error slot — are pointer-sized integers the shim supplies.
/// </remarks>
internal static class SwiftTwinShim
{
    private const string Unsafe = "global::System.Runtime.CompilerServices.Unsafe";
    private const string ResultLocal = "__bridgeResult";
    private const string ErrorLocal = "__bridgeErrorSlot";

    /// <summary>
    /// The names of the trailing pointers. The transition gate tells a swapped pair of these
    /// pointer-sized arguments apart only by name, so an ordinary argument never takes one.
    /// </summary>
    private static readonly HashSet<string> TrailingNames = new(StringComparer.Ordinal)
    {
        "__self", "__error", "__indirectResult", "__resultBuffer",
    };

    /// <summary>
    /// The shim and twin replacing <paramref name="method"/>, or the reason they cannot be written
    /// (returned; <paramref name="replacement"/> is then null).
    /// </summary>
    /// <param name="indent">The import's indentation, repeated on every line after the first.</param>
    public static string? Build(
        MethodDeclarationSyntax method, SwiftCallPlan plan, string library, string twinName, string indent, string newline,
        out string? replacement)
    {
        replacement = null;
        if (Shape(method, out var import, out var returnLists) is { } shapeRefusal)
            return shapeRefusal;

        var parameters = method.ParameterList.Parameters;
        var names = parameters.Select(p => p.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
        if (names.Contains(ResultLocal) || names.Contains(ErrorLocal))
            return "a parameter name collides with the shim's locals";

        var view = plan.Body.Managed;
        var returnType = method.ReturnType.ToString();
        bool returnsVoid = method.ReturnType is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword };
        var used = new bool[parameters.Count];
        var twinParameters = new List<string>();
        var arguments = new List<string>();
        var prologue = new List<string>();
        string? fixedStatement = null;
        bool resultLocal = false;

        foreach (var argument in view.Arguments)
        {
            ParameterSyntax? parameter = argument.Parameter is { } index && index >= 0 && index < parameters.Count ? parameters[index] : null;
            if (argument.Parameter is not null && parameter is null)
                return $"the plan names parameter {argument.Parameter}, which the import does not have";
            var name = parameter?.Identifier.Text;
            switch (argument.Role)
            {
                case SwiftTwinRole.Ordinary when parameter is null:
                    return "an ordinary twin argument has no parameter";
                case SwiftTwinRole.Ordinary when argument.Source == SwiftPieceSource.Value:
                    if (Claim(used, argument.Parameter!.Value) is { } twice)
                        return twice;
                    if (parameter.Default is not null)
                        return $"parameter {name} has a default value";
                    // The twin's parameters are positional, so an ordinary argument the import happens to
                    // name like a trailing pointer (a wrapper's explicit self word, say) is declared
                    // under another name and still reaches the same register.
                    var renamed = TrailingNames.Contains(parameter.Identifier.ValueText)
                        ? parameter.Identifier.ValueText + "Argument"
                        : null;
                    twinParameters.Add(Declaration(parameter, keepAttributes: true, renamed));
                    arguments.Add(Modifiers(parameter) is { Length: > 0 } modifiers ? $"{modifiers} {name}" : name!);
                    break;
                case SwiftTwinRole.Ordinary when argument.Source == SwiftPieceSource.Piece:
                    if (parameter.Modifiers.Count > 0)
                        return $"parameter {name} is an aggregate passed by reference";
                    used[argument.Parameter!.Value] = true;
                    var pieceType = SwiftTwinSignature.TypeOf(argument.Class, argument.Size, argument.Signed);
                    twinParameters.Add($"{pieceType} {Base(parameter)}_{argument.Offset}");
                    var bytes = $"{Unsafe}.As<{parameter.Type}, byte>(ref {name})";
                    if (argument.Offset != 0)
                        bytes = $"{Unsafe}.AddByteOffset(ref {bytes}, (nint){argument.Offset})";
                    arguments.Add($"{Unsafe}.ReadUnaligned<{pieceType}>(ref {bytes})");
                    break;
                case SwiftTwinRole.Ordinary when argument.Source == SwiftPieceSource.Address:
                    if (parameter.Modifiers.Count > 0)
                        return $"parameter {name} is an aggregate passed by reference";
                    if (Claim(used, argument.Parameter!.Value) is { } twiceAddress)
                        return twiceAddress;
                    twinParameters.Add($"nint {Base(parameter)}_address");
                    arguments.Add($"(nint){Unsafe}.AsPointer(ref {name})");
                    break;
                case SwiftTwinRole.Ordinary:
                    return $"an ordinary twin argument from {argument.Source} has no shim";
                case SwiftTwinRole.IndirectResult when parameter is not null:
                    if (Claim(used, argument.Parameter!.Value) is { } twiceIndirect)
                        return twiceIndirect;
                    if (parameter.Modifiers.Count > 0)
                        return $"the indirect result {name} is passed by reference";
                    twinParameters.Add("nint __indirectResult");
                    arguments.Add($"(nint){name}.Value");
                    break;
                case SwiftTwinRole.IndirectResult:
                case SwiftTwinRole.ResultBuffer:
                    if (returnsVoid)
                        return $"the plan returns {view.Result.ManagedType} through a buffer, but the import returns nothing";
                    resultLocal = true;
                    twinParameters.Add(argument.Role == SwiftTwinRole.ResultBuffer ? "nint __resultBuffer" : "nint __indirectResult");
                    arguments.Add($"(nint){Unsafe}.AsPointer(ref {ResultLocal})");
                    break;
                case SwiftTwinRole.Self when parameter is not null:
                    if (Claim(used, argument.Parameter!.Value) is { } twiceSelf)
                        return twiceSelf;
                    if (parameter.Modifiers.Count > 0)
                        return $"self ({name}) is passed by reference";
                    twinParameters.Add("nint __self");
                    arguments.Add($"(nint){name}.Value");
                    break;
                case SwiftTwinRole.Error when parameter is not null:
                    if (Claim(used, argument.Parameter!.Value) is { } twiceError)
                        return twiceError;
                    twinParameters.Add("nint __error");
                    if (parameter.Type is PointerTypeSyntax && parameter.Modifiers.Count == 0)
                    {
                        arguments.Add($"(nint){name}");
                        break;
                    }
                    var refKind = Modifiers(parameter);
                    if (refKind is not ("ref" or "out") || parameter.Type is null)
                        return $"the error {name} is neither a pointer nor passed by ref or out";
                    // The bridge stores the error register into the slot after every call, so an out
                    // slot is assigned before its address is taken.
                    if (refKind == "out")
                        prologue.Add($"{name} = default;");
                    fixedStatement = $"fixed ({parameter.Type}* {ErrorLocal} = &{name})";
                    arguments.Add($"(nint){ErrorLocal}");
                    break;
                default:
                    return $"a {argument.Role} twin argument has no shim";
            }
        }

        var twinNames = twinParameters.Select(p => p[(p.LastIndexOf(' ') + 1)..]).ToList();
        if (twinNames.Distinct(StringComparer.Ordinal).Count() != twinNames.Count)
            return "two twin parameters would share a name";
        for (int i = 0; i < parameters.Count; i++)
        {
            // An aggregate the plan lowers to no pieces crosses as nothing; anything else unread is a mismatch.
            if (!used[i] && parameters[i].Modifiers.Count > 0)
                return $"parameter {parameters[i].Identifier.Text} is passed by reference but the plan does not pass it";
        }

        string twinReturn;
        switch (view.Result.Kind)
        {
            case SwiftResultKind.Void:
                if (!returnsVoid)
                    return $"the plan returns nothing but the import returns {returnType}";
                twinReturn = "void";
                break;
            case SwiftResultKind.Scalar:
                if (returnsVoid)
                    return $"the plan returns a {view.Result.ManagedType} but the import returns nothing";
                twinReturn = returnType;
                break;
            case SwiftResultKind.Direct or SwiftResultKind.Indirect:
                if (returnsVoid == resultLocal)
                    return $"the plan's {view.Result.Kind} result does not match the import's {returnType} return";
                twinReturn = "void";
                break;
            default:
                return $"a {view.Result.Kind} result has no shim";
        }

        var call = $"{twinName}({string.Join(", ", arguments)});";
        if (view.Result.Kind == SwiftResultKind.Scalar)
            call = "return " + call;

        var lines = new List<string>
        {
            "[global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]",
            $"{ShimModifiers(method)} {returnType} {method.Identifier.Text}({string.Join(", ", parameters.Select(p => Declaration(p, keepAttributes: false)))})",
            "{",
        };
        foreach (var statement in prologue)
            lines.Add("    " + statement);
        if (resultLocal)
            lines.Add($"    {returnType} {ResultLocal} = default;");
        if (fixedStatement is not null)
        {
            lines.Add("    " + fixedStatement);
            lines.Add("    {");
            lines.Add("        " + call);
            lines.Add("    }");
        }
        else
        {
            lines.Add("    " + call);
        }
        if (resultLocal)
            lines.Add($"    return {ResultLocal};");
        lines.Add("}");
        lines.Add("");
        lines.Add("[global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        lines.Add(ImportAttribute(import, library, plan.Symbol));
        if (view.Result.Kind == SwiftResultKind.Scalar)
            lines.AddRange(returnLists.Select(l => l.ToString()));
        var twinUnsafe = method.Modifiers.Any(m => m.IsKind(SyntaxKind.UnsafeKeyword)) ? "unsafe " : "";
        lines.Add($"private static {twinUnsafe}partial {twinReturn} {twinName}({string.Join(", ", twinParameters)});");

        var text = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                text.Append(newline);
                if (lines[i].Length > 0)
                    text.Append(indent);
            }
            text.Append(lines[i]);
        }
        replacement = text.ToString();
        return null;
    }

    /// <summary>Why the import is not one the shim reproduces, or null: a source-generated, partial, non-generic static import with only its import and convention attributes.</summary>
    private static string? Shape(MethodDeclarationSyntax method, out AttributeSyntax import, out List<AttributeListSyntax> returnLists)
    {
        import = null!;
        returnLists = new List<AttributeListSyntax>();
        if (method.TypeParameterList is not null || method.ConstraintClauses.Count > 0)
            return "the import is generic";
        if (method.Body is not null || method.ExpressionBody is not null)
            return "the import has a body";
        if (!method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)) || !method.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword))
            || method.Modifiers.Any(m => m.IsKind(SyntaxKind.ExternKeyword)))
            return "the import is not a static partial method";
        AttributeSyntax? found = null;
        foreach (var list in method.AttributeLists)
        {
            if (list.Target?.Identifier.IsKind(SyntaxKind.ReturnKeyword) == true)
            {
                returnLists.Add(list);
                continue;
            }
            foreach (var attribute in list.Attributes)
            {
                if (SwiftTransitionSyntax.IsAttribute(attribute, "LibraryImport"))
                    found = attribute;
                else if (!SwiftTransitionSyntax.IsAttribute(attribute, "UnmanagedCallConv"))
                    return $"the import carries the attribute {attribute.Name}";
            }
        }
        if (found is null)
            return "the import is not a LibraryImport";
        import = found;
        foreach (var parameter in method.ParameterList.Parameters)
        {
            if (parameter.Type is null || parameter.Modifiers.Any(m => m.Kind() is not (SyntaxKind.RefKeyword or SyntaxKind.OutKeyword or SyntaxKind.InKeyword)))
                return $"parameter {parameter.Identifier.Text} has a modifier the shim does not reproduce";
        }
        return null;
    }

    private static string? Claim(bool[] used, int index)
    {
        if (used[index])
            return $"the plan passes parameter {index} twice";
        used[index] = true;
        return null;
    }

    private static string Modifiers(ParameterSyntax parameter) => string.Join(" ", parameter.Modifiers.Select(m => m.Text));

    private static string Base(ParameterSyntax parameter) => parameter.Identifier.ValueText;

    private static string Declaration(ParameterSyntax parameter, bool keepAttributes, string? rename = null)
    {
        var parts = new List<string>();
        if (keepAttributes)
            parts.AddRange(parameter.AttributeLists.Select(l => l.ToString()));
        if (parameter.Modifiers.Count > 0)
            parts.Add(Modifiers(parameter));
        parts.Add(parameter.Type!.ToString());
        parts.Add(rename ?? parameter.Identifier.Text);
        return string.Join(" ", parts);
    }

    private static string ShimModifiers(MethodDeclarationSyntax method)
    {
        var modifiers = method.Modifiers.Where(m => !m.IsKind(SyntaxKind.PartialKeyword)).Select(m => m.Text).ToList();
        if (!modifiers.Contains("unsafe"))
            modifiers.Add("unsafe");
        return string.Join(" ", modifiers);
    }

    /// <summary>The twin's import: the bridge's library and symbol, and the import's other named arguments.</summary>
    private static string ImportAttribute(AttributeSyntax import, string library, string symbol)
    {
        var named = import.ArgumentList?.Arguments
            .Where(a => a.NameEquals is not null && a.NameEquals.Name.Identifier.ValueText != "EntryPoint")
            .Select(a => a.ToString())
            .ToList() ?? new List<string>();
        var arguments = new List<string> { SymbolDisplay.FormatLiteral(library, quote: true), $"EntryPoint = {SymbolDisplay.FormatLiteral(symbol, quote: true)}" };
        arguments.AddRange(named);
        return $"[global::System.Runtime.InteropServices.LibraryImport({string.Join(", ", arguments)})]";
    }
}
