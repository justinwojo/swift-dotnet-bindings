// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BindingsGeneration;

/// <summary>How a managed parameter is passed: by value, or by reference with one of C#'s modifiers.</summary>
public enum SwiftManagedRefKind
{
    None,
    Ref,
    Out,
    In,
}

/// <summary>Who marshals a transition's managed signature before it crosses.</summary>
public enum SwiftManagedMarshalling
{
    /// <summary>A <c>LibraryImport</c>: its source generator lowers <c>SafeHandle</c>, <c>bool</c> and by-reference parameters first.</summary>
    SourceGenerated,

    /// <summary>A <c>DllImport</c>: the runtime marshals it as declared.</summary>
    Runtime,

    /// <summary>A callback or a function pointer: nothing is marshalled, so every carrier must be blittable.</summary>
    None,
}

/// <summary>One parameter of a transition's managed signature, as the C# declares it.</summary>
/// <param name="Type">The type as written, without <c>global::</c>.</param>
/// <param name="MarshalAs">The <c>UnmanagedType</c> member a <c>[MarshalAs]</c> names, if any.</param>
public sealed record SwiftManagedParameter(string Name, string Type, SwiftManagedRefKind RefKind = SwiftManagedRefKind.None, string? MarshalAs = null);

/// <summary>
/// A transition's managed signature as its C# declares it: the source of the twin's carriers.
/// Read from the emitted C#, so it says what the IL passes, not what a producer meant to pass.
/// </summary>
/// <param name="ContextType">
/// The full name of the type that declares the transition, which the types it spells are resolved
/// against. Empty at the top level.
/// </param>
public sealed record SwiftManagedSignature(
    ImmutableArray<SwiftManagedParameter> Parameters,
    string ReturnType,
    string? ReturnMarshalAs,
    SwiftManagedMarshalling Marshalling,
    string ContextType = "")
{
    /// <summary>The namespaces (and <c>Alias=Target</c> aliases) the declaration's using directives bring into scope.</summary>
    public ImmutableArray<string> Usings { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>Where the signature's type names are looked up.</summary>
    public SwiftManagedScope Scope => new(ContextType, Usings);

    public bool Equals(SwiftManagedSignature? other) =>
        other is not null && ReturnType == other.ReturnType && ReturnMarshalAs == other.ReturnMarshalAs
        && Marshalling == other.Marshalling && ContextType == other.ContextType
        && Parameters.AsSpan().SequenceEqual(other.Parameters.AsSpan())
        && Usings.AsSpan().SequenceEqual(other.Usings.AsSpan());

    public override int GetHashCode() => HashCode.Combine(ReturnType, Marshalling, Parameters.Length);

    public override string ToString() =>
        $"{ReturnType} ({string.Join(", ", Parameters.Select(p => (p.RefKind == SwiftManagedRefKind.None ? "" : p.RefKind.ToString().ToLowerInvariant() + " ") + p.Type))})";

    /// <summary>The signature of a method declaration.</summary>
    internal static SwiftManagedSignature Of(MethodDeclarationSyntax method, SwiftManagedMarshalling marshalling) => new(
        method.ParameterList.Parameters.Select(Parameter).ToImmutableArray(),
        Spell(method.ReturnType),
        MarshalAsOf(method.AttributeLists.Where(l => l.Target?.Identifier.IsKind(SyntaxKind.ReturnKeyword) == true)),
        marshalling,
        SwiftManagedTypeIndex.ContextOf(method))
    {
        Usings = SwiftManagedTypeIndex.UsingsOf(method),
    };

    /// <summary>The signature of a function-pointer type: its last parameter is the return type.</summary>
    internal static SwiftManagedSignature Of(FunctionPointerTypeSyntax pointer, SyntaxNode site)
    {
        var parameters = pointer.ParameterList.Parameters;
        var list = ImmutableArray.CreateBuilder<SwiftManagedParameter>();
        for (int i = 0; i < parameters.Count - 1; i++)
        {
            var p = parameters[i];
            list.Add(new SwiftManagedParameter($"arg{i}", Spell(p.Type), RefKindOf(p.Modifiers), MarshalAsOf(p.AttributeLists)));
        }
        return new SwiftManagedSignature(
            list.ToImmutable(), Spell(parameters[^1].Type), null, SwiftManagedMarshalling.None, SwiftManagedTypeIndex.ContextOf(site))
        {
            Usings = SwiftManagedTypeIndex.UsingsOf(site),
        };
    }

    private static SwiftManagedParameter Parameter(ParameterSyntax p) =>
        new(p.Identifier.ValueText, p.Type is null ? "" : Spell(p.Type), RefKindOf(p.Modifiers), MarshalAsOf(p.AttributeLists));

    private static SwiftManagedRefKind RefKindOf(SyntaxTokenList modifiers) =>
        modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)) ? SwiftManagedRefKind.Ref
        : modifiers.Any(m => m.IsKind(SyntaxKind.OutKeyword)) ? SwiftManagedRefKind.Out
        : modifiers.Any(m => m.IsKind(SyntaxKind.InKeyword)) ? SwiftManagedRefKind.In
        : SwiftManagedRefKind.None;

    /// <summary>The <c>UnmanagedType</c> member of a <c>[MarshalAs]</c> among the attribute lists, if any.</summary>
    private static string? MarshalAsOf(IEnumerable<AttributeListSyntax> lists)
    {
        var marshalAs = lists.SelectMany(l => l.Attributes).FirstOrDefault(a =>
            SwiftManagedTypeIndex.SimpleName(a.Name) is "MarshalAs" or "MarshalAsAttribute");
        var argument = marshalAs?.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
        return argument switch
        {
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            IdentifierNameSyntax id => id.Identifier.ValueText,
            null => null,
            _ => argument.ToString(),
        };
    }

    /// <summary>A type as written, without <c>global::</c> and with its whitespace normalized.</summary>
    internal static string Spell(TypeSyntax type) =>
        type.NormalizeWhitespace().ToString().Replace("global::", "", StringComparison.Ordinal);
}

/// <summary>
/// Where a type name written in C# is looked up: the namespaces and types that enclose it, innermost
/// last, and the using directives in scope there.
/// </summary>
public readonly record struct SwiftManagedScope(string Context, ImmutableArray<string> Usings);

/// <summary>What one managed carrier is, once classified.</summary>
public enum SwiftManagedValueKind
{
    /// <summary>One scalar: an integer, a float, a pointer, an enum, a by-reference parameter.</summary>
    Primitive,

    /// <summary>A value type of known exact layout, passed in pieces or by address.</summary>
    Aggregate,

    /// <summary><c>SwiftIndirectResult</c>: the address of the result.</summary>
    IndirectResult,

    /// <summary><c>SwiftSelf</c>: the self (context) register.</summary>
    Self,

    /// <summary><c>SwiftError</c> by reference or by pointer: the error register.</summary>
    Error,

    /// <summary>Nothing: a <c>void</c> return.</summary>
    Void,

    /// <summary>Not a carrier a plan can carry; the reason says why.</summary>
    Refused,
}

/// <summary>Why a carrier, or a whole transition, cannot be planned. Every refusal carries one, with a reason.</summary>
public enum SwiftPlanRefusalKind
{
    /// <summary>A vector or SIMD carrier.</summary>
    Vector,

    /// <summary><c>Int128</c>, <c>UInt128</c>, <c>Half</c> (Float16) or <c>decimal</c>.</summary>
    UnsupportedScalar,

    /// <summary>A managed reference: a class, a string, an array, an object.</summary>
    ManagedReference,

    /// <summary>Explicit or overlapping layout.</summary>
    ExplicitLayout,

    /// <summary>A field or carrier the runtime would have to marshal.</summary>
    NonBlittable,

    /// <summary>A carrier that spells a generic parameter whose layout is not fixed.</summary>
    UnresolvedGeneric,

    /// <summary>A value type that is neither in the carrier table nor a layout the module declares.</summary>
    UnknownAggregate,

    /// <summary>The twin's incoming arguments do not land where the Swift callee reads them.</summary>
    PrefixMismatch,

    /// <summary>The result the managed signature takes is not the one the callee returns.</summary>
    ResultMismatch,

    /// <summary>The managed signature and the callee disagree on a special register (indirect result, self, error).</summary>
    SpecialRegisterMismatch,

    /// <summary>The managed views computed for two architectures differ.</summary>
    ManagedViewDisagreement,

    /// <summary>arm64e, which needs pointer authentication the bridge emitter does not yet test.</summary>
    Arm64e,

    /// <summary>Not a synchronous <c>swiftcc</c> call (async, typed throws, a coroutine, a C or <c>@objc</c> entry, a parameter pack).</summary>
    NotSynchronous,

    /// <summary>The transition has no facts, or some are unknown.</summary>
    Facts,

    /// <summary>The physical signature of the callee could not be predicted.</summary>
    Lowering,

    /// <summary>Two different plans hash to the same bridge name.</summary>
    HashCollision,
}

/// <summary>One refusal: what kind, on which architecture (null for all), and why.</summary>
public sealed record SwiftPlanRefusal(SwiftPlanRefusalKind Kind, string Reason, SwiftArchitecture? Architecture = null)
{
    public override string ToString() => Architecture is null ? $"{Kind}: {Reason}" : $"{Kind} ({Architecture}): {Reason}";
}

/// <summary>
/// A classified managed carrier: what kind it is and, for a primitive or an aggregate, its exact
/// layout in managed memory.
/// </summary>
/// <param name="Type">
/// The managed type in canonical form, however the C# spells it: a C# keyword for a primitive,
/// <c>nint</c> for anything that crosses as an address, a carrier's or enum's full name. Two
/// spellings of one type are one carrier. A refused value keeps the spelling it was refused for.
/// </param>
/// <param name="Layout">For a primitive or an aggregate, its fields as typed leaves at their managed offsets.</param>
/// <param name="ManagedSize">For an aggregate, its managed size (<c>sizeof</c>), tail padding included.</param>
/// <param name="Signed">For an integer primitive, whether it is signed (which extension the callee may assume).</param>
/// <param name="ByReference">For a primitive, whether it is the address of a by-reference parameter.</param>
public sealed record SwiftManagedValue(
    SwiftManagedValueKind Kind,
    string Type,
    SwiftLeafLayout? Layout = null,
    int ManagedSize = 0,
    bool Signed = false,
    bool ByReference = false,
    SwiftPlanRefusal? Refusal = null)
{
    public static SwiftManagedValue Refuse(string type, SwiftPlanRefusalKind kind, string reason) =>
        new(SwiftManagedValueKind.Refused, type, Refusal: new SwiftPlanRefusal(kind, reason));

    public static SwiftManagedValue Scalar(string type, SwiftScalarKind kind, bool signed = false, bool byReference = false) =>
        new(SwiftManagedValueKind.Primitive, type, SwiftLeafLayout.Scalar(kind), SwiftAggregateLowering.SizeOf(kind), signed, byReference);
}

/// <summary>A value type declared in C# source: its fields in declaration order and its layout attribute.</summary>
/// <param name="FullName">Namespace and enclosing types, dot-separated.</param>
/// <param name="Fields">The instance fields' types as written, in declaration order, auto-property backing fields included.</param>
/// <param name="Layout">The <c>LayoutKind</c> named, or <c>Sequential</c> by default.</param>
/// <param name="Pack">The <c>Pack</c> named, or 0.</param>
/// <param name="ExplicitSize">The <c>Size</c> named, or 0.</param>
/// <param name="Problem">A shape the layout cannot be read from (a fixed buffer, a ref field, generics), or null.</param>
public sealed record SwiftManagedStruct(
    string FullName,
    ImmutableArray<string> Fields,
    string Layout,
    int Pack,
    int ExplicitSize,
    string? Problem)
{
    /// <summary>The using directives in scope at the declaration, which its field types are looked up through.</summary>
    public ImmutableArray<string> Usings { get; init; } = ImmutableArray<string>.Empty;

    public bool Equals(SwiftManagedStruct? other) =>
        other is not null && FullName == other.FullName && Layout == other.Layout && Pack == other.Pack
        && ExplicitSize == other.ExplicitSize && Problem == other.Problem
        && Fields.AsSpan().SequenceEqual(other.Fields.AsSpan()) && Usings.AsSpan().SequenceEqual(other.Usings.AsSpan());

    public override int GetHashCode() => HashCode.Combine(FullName, Fields.Length);
}

/// <summary>
/// The value types and enums a module's C# declares, by full name, read from its syntax: what a
/// transition's carriers are resolved against when they are not in the runtime carrier table.
/// </summary>
public sealed class SwiftManagedTypeIndex
{
    private readonly Dictionary<string, string> _enums = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SwiftManagedStruct> _structs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _references = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _globalUsings = new(StringComparer.Ordinal);

    public static SwiftManagedTypeIndex Empty => new();

    public IReadOnlyDictionary<string, SwiftManagedStruct> Structs => _structs;

    /// <summary>The <c>global using</c> directives of every compilation unit added, which every name is looked up through.</summary>
    public IReadOnlyCollection<string> GlobalUsings => _globalUsings;

    /// <summary>Adds every enum and struct a compilation unit declares.</summary>
    public void Add(SyntaxNode root)
    {
        foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            if (!directive.GlobalKeyword.IsKind(SyntaxKind.None) && Using(directive) is { } global)
                _globalUsings.Add(global);
        }
        foreach (var e in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
        {
            var underlying = e.BaseList?.Types.FirstOrDefault()?.Type is { } t ? SwiftManagedSignature.Spell(t) : "int";
            _enums.TryAdd(FullNameOf(e), underlying);
        }
        foreach (var s in root.DescendantNodes().OfType<StructDeclarationSyntax>())
            _structs.TryAdd(FullNameOf(s), Describe(s));
        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (type is ClassDeclarationSyntax or InterfaceDeclarationSyntax
                || type is RecordDeclarationSyntax record && !record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword))
                _references.Add(FullNameOf(type));
        }
        foreach (var d in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
        {
            var context = ContextOf(d);
            _references.Add(context.Length == 0 ? d.Identifier.ValueText : context + "." + d.Identifier.ValueText);
        }
    }

    /// <summary>True when the module declares <paramref name="fullName"/> as a class, interface, record class or delegate.</summary>
    public bool IsReference(string fullName) => _references.Contains(fullName);

    public bool TryGetEnum(string fullName, out string underlying) => _enums.TryGetValue(fullName, out underlying!);

    public bool TryGetStruct(string fullName, out SwiftManagedStruct value) => _structs.TryGetValue(fullName, out value!);

    /// <summary>
    /// The full names a spelling may name from <paramref name="context"/>: the spelling under each
    /// enclosing type and namespace, innermost first, then the spelling itself.
    /// </summary>
    public static IEnumerable<string> Candidates(string spelling, string context) => ScopeCandidates(spelling, new SwiftManagedScope(context, []));

    /// <summary>
    /// The full names a spelling may name from <paramref name="scope"/>: the spelling under each
    /// enclosing type and namespace, innermost first, then the spelling itself, then the spelling
    /// under each namespace a using directive imports (an alias replaced by its target).
    /// </summary>
    public IEnumerable<string> Candidates(string spelling, SwiftManagedScope scope) =>
        ScopeCandidates(spelling, scope with { Usings = (scope.Usings.IsDefault ? [] : scope.Usings).AddRange(_globalUsings) });

    private static IEnumerable<string> ScopeCandidates(string spelling, SwiftManagedScope scope)
    {
        var context = scope.Context;
        while (context.Length > 0)
        {
            yield return context + "." + spelling;
            var dot = context.LastIndexOf('.');
            context = dot < 0 ? "" : context[..dot];
        }
        yield return spelling;
        foreach (var directive in scope.Usings.IsDefault ? [] : scope.Usings)
        {
            var equals = directive.IndexOf('=');
            if (equals < 0)
            {
                yield return directive + "." + spelling;
                continue;
            }
            var alias = directive[..equals];
            if (spelling == alias)
                yield return directive[(equals + 1)..];
            else if (spelling.StartsWith(alias + ".", StringComparison.Ordinal))
                yield return directive[(equals + 1)..] + spelling[alias.Length..];
        }
    }

    /// <summary>
    /// The using directives in scope at <paramref name="node"/>: each enclosing namespace's, innermost
    /// first, then the compilation unit's. A namespace import is its name; an alias is
    /// <c>Alias=Target</c>. A <c>using static</c> imports no type names and is left out.
    /// </summary>
    internal static ImmutableArray<string> UsingsOf(SyntaxNode node)
    {
        var usings = ImmutableArray.CreateBuilder<string>();
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            var directives = current switch
            {
                BaseNamespaceDeclarationSyntax ns => ns.Usings,
                CompilationUnitSyntax unit => unit.Usings,
                _ => default(SyntaxList<UsingDirectiveSyntax>?),
            };
            foreach (var directive in directives ?? default)
            {
                if (Using(directive) is { } value)
                    usings.Add(value);
            }
        }
        return usings.ToImmutable();
    }

    private static string? Using(UsingDirectiveSyntax directive)
    {
        if (!directive.StaticKeyword.IsKind(SyntaxKind.None) || directive.NamespaceOrType is not { } target)
            return null;
        var name = target.ToString().Replace("global::", "", StringComparison.Ordinal);
        return directive.Alias is { } alias ? $"{alias.Name.Identifier.ValueText}={name}" : name;
    }

    private static SwiftManagedStruct Describe(StructDeclarationSyntax s)
    {
        string? problem = null;
        if (s.TypeParameterList is { Parameters.Count: > 0 })
            problem = "a generic struct's layout depends on its arguments";
        if (s.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)))
            problem ??= "a ref struct cannot cross";

        var fields = ImmutableArray.CreateBuilder<string>();
        foreach (var member in s.Members)
        {
            switch (member)
            {
                case FieldDeclarationSyntax f when !IsStatic(f.Modifiers):
                    if (f.Modifiers.Any(m => m.IsKind(SyntaxKind.FixedKeyword)))
                        problem ??= "a fixed buffer's layout is not read";
                    if (f.Declaration.Type is RefTypeSyntax)
                        problem ??= "a ref field cannot cross";
                    if (f.AttributeLists.SelectMany(l => l.Attributes).Any(a => SimpleName(a.Name) is "FieldOffset" or "FieldOffsetAttribute"))
                        problem ??= "a field names its own offset";
                    foreach (var _ in f.Declaration.Variables)
                        fields.Add(SwiftManagedSignature.Spell(f.Declaration.Type));
                    break;
                case PropertyDeclarationSyntax p when !IsStatic(p.Modifiers) && IsAutoProperty(p):
                    fields.Add(SwiftManagedSignature.Spell(p.Type));
                    break;
            }
        }

        string layout = "Sequential";
        int pack = 0, size = 0;
        var structLayout = s.AttributeLists.SelectMany(l => l.Attributes)
            .FirstOrDefault(a => SimpleName(a.Name) is "StructLayout" or "StructLayoutAttribute");
        if (structLayout?.ArgumentList is { } arguments)
        {
            foreach (var argument in arguments.Arguments)
            {
                var value = argument.Expression;
                var name = argument.NameEquals?.Name.Identifier.ValueText;
                if (name is null)
                    layout = value is MemberAccessExpressionSyntax access ? access.Name.Identifier.ValueText : value.ToString();
                else if (name == "Pack" && value is LiteralExpressionSyntax packLiteral && packLiteral.Token.Value is int packValue)
                    pack = packValue;
                else if (name == "Size" && value is LiteralExpressionSyntax sizeLiteral && sizeLiteral.Token.Value is int sizeValue)
                    size = sizeValue;
                else if (name is "Pack" or "Size")
                    problem ??= $"its StructLayout {name} is not a literal";
            }
        }
        return new SwiftManagedStruct(FullNameOf(s), fields.ToImmutable(), layout, pack, size, problem) { Usings = UsingsOf(s) };
    }

    private static bool IsStatic(SyntaxTokenList modifiers) =>
        modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword) || m.IsKind(SyntaxKind.ConstKeyword));

    /// <summary>An auto-property, which the compiler gives a backing field: accessors with no bodies and no expression body.</summary>
    private static bool IsAutoProperty(PropertyDeclarationSyntax p) =>
        p.ExpressionBody is null && p.AccessorList is { } list
        && list.Accessors.All(a => a.Body is null && a.ExpressionBody is null)
        && !p.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword) || m.IsKind(SyntaxKind.ExternKeyword));

    /// <summary>A type declaration's full name: its namespaces and enclosing types, dot-separated.</summary>
    internal static string FullNameOf(BaseTypeDeclarationSyntax type)
    {
        var context = ContextOf(type);
        return context.Length == 0 ? type.Identifier.ValueText : context + "." + type.Identifier.ValueText;
    }

    /// <summary>The namespaces and types that enclose <paramref name="node"/>, dot-separated.</summary>
    internal static string ContextOf(SyntaxNode node)
    {
        var parts = new List<string>();
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case BaseTypeDeclarationSyntax type:
                    parts.Add(type.Identifier.ValueText);
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    parts.Add(ns.Name.ToString().Replace("global::", "", StringComparison.Ordinal));
                    break;
            }
        }
        parts.Reverse();
        return string.Join(".", parts);
    }

    internal static string SimpleName(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
        SimpleNameSyntax s => s.Identifier.ValueText,
        _ => type.ToString(),
    };
}

/// <summary>
/// The value types of <c>Swift.Runtime</c> (and the Apple supplement) a transition carries by value,
/// with their exact managed layouts. A unit test checks every entry against the struct the runtime's
/// or the supplement's source declares, so the layouts the planner assumes are the ones they ship.
/// </summary>
public static class SwiftManagedCarrierTable
{
    private static SwiftLayoutLeaf L(int offset, SwiftScalarKind kind) => new(offset, SwiftAggregateLowering.SizeOf(kind), kind);

    private static (SwiftLeafLayout Layout, int ManagedSize) Pointers(int count) =>
        (new SwiftLeafLayout(count * 8, 8, Enumerable.Range(0, count).Select(i => L(i * 8, SwiftScalarKind.Pointer)).ToImmutableArray()), count * 8);

    /// <summary>Full name (generic arity as <c>`N</c>) → layout and managed size.</summary>
    public static readonly ImmutableSortedDictionary<string, (SwiftLeafLayout Layout, int ManagedSize)> Entries = Build();

    private static ImmutableSortedDictionary<string, (SwiftLeafLayout Layout, int ManagedSize)> Build()
    {
        var b = ImmutableSortedDictionary.CreateBuilder<string, (SwiftLeafLayout, int)>(StringComparer.Ordinal);
        // One word.
        foreach (var name in new[]
        {
            "Swift.Runtime.TypeMetadata", "Swift.Runtime.ProtocolWitnessTable", "Swift.Runtime.ProtocolDescriptor",
            "Swift.Runtime.ProtocolConformanceDescriptor", "Swift.UnsafeRawPointer", "Swift.UnsafeMutableRawPointer",
            "Swift.UnsafePointer`1", "Swift.UnsafeMutablePointer`1",
        })
            b[name] = Pointers(1);

        b["Swift.SwiftString.Buffer"] = (new SwiftLeafLayout(16, 8, ImmutableArray.Create(L(0, SwiftScalarKind.Int64), L(8, SwiftScalarKind.Pointer))), 16);
        b["Swift.Runtime.SwiftClosureData"] = Pointers(2);
        b["Swift.Runtime.ClassExistentialContainer1"] = Pointers(2);
        b["Swift.Runtime.Utf8Slice"] = (new SwiftLeafLayout(16, 8, ImmutableArray.Create(L(0, SwiftScalarKind.Pointer), L(8, SwiftScalarKind.Pointer))), 16);
        b["Swift.Runtime.Marshalling.BlittableSwiftString"] = (new SwiftLeafLayout(16, 8, ImmutableArray.Create(L(0, SwiftScalarKind.Pointer), L(8, SwiftScalarKind.Pointer))), 16);
        // Pack = 1: no tail padding, so the managed size is the end of the last field.
        b["Swift.Runtime.SwiftOptionalCarrier9"] = (new SwiftLeafLayout(9, 1, ImmutableArray.Create(L(0, SwiftScalarKind.Int64), L(8, SwiftScalarKind.Int8))), 9);
        b["Swift.Runtime.SwiftOptionalCarrier16"] = (new SwiftLeafLayout(16, 1, ImmutableArray.Create(L(0, SwiftScalarKind.Int64), L(8, SwiftScalarKind.Int64))), 16);
        b["Swift.Runtime.Marshalling.BlittableOptionalInt32"] = (new SwiftLeafLayout(5, 1, ImmutableArray.Create(L(0, SwiftScalarKind.Int32), L(4, SwiftScalarKind.Int8))), 5);
        // Three payload words, the metadata, then one witness table per protocol.
        for (int n = 0; n <= 8; n++)
            b[$"Swift.Runtime.ExistentialContainer{n}"] = Pointers(4 + n);
        b["Swift.UnsafeBufferPointer`1"] = Pointers(2);
        b["Swift.UnsafeMutableBufferPointer`1"] = Pointers(2);
        b["Swift.UnsafeRawBufferPointer"] = Pointers(2);
        b["Swift.UnsafeMutableRawBufferPointer"] = Pointers(2);
        b["Swift.CGPoint"] = Doubles(2);
        b["Swift.CGSize"] = Doubles(2);
        b["Swift.CGRect"] = Doubles(4);
        b["SwiftUI.EdgeInsets.Buffer"] = Doubles(4);
        // The Apple supplement's Data: a flags word and an object pointer.
        b["Swift.Foundation.Data"] = (new SwiftLeafLayout(16, 8, ImmutableArray.Create(L(0, SwiftScalarKind.Int64), L(8, SwiftScalarKind.Pointer))), 16);
        return b.ToImmutable();
    }

    private static (SwiftLeafLayout, int) Doubles(int count) =>
        (new SwiftLeafLayout(count * 8, 8, Enumerable.Range(0, count).Select(i => L(i * 8, SwiftScalarKind.Double)).ToImmutableArray()), count * 8);

    /// <summary>The runtime enums a transition carries, by full name → underlying C# keyword.</summary>
    public static readonly ImmutableSortedDictionary<string, string> Enums =
        new Dictionary<string, string> { ["Swift.Runtime.TypeMetadataRequest"] = "int" }.ToImmutableSortedDictionary(StringComparer.Ordinal);

    /// <summary>The table entry a full name (with its generic arguments) names, if any.</summary>
    public static bool TryGet(string fullName, out SwiftLeafLayout layout, out int managedSize)
    {
        if (Entries.TryGetValue(fullName, out var entry))
        {
            (layout, managedSize) = entry;
            return true;
        }
        layout = null!;
        managedSize = 0;
        return false;
    }
}

/// <summary>
/// Classifies a transition's managed carriers into the twin's native carriers (design step 1): a
/// <c>SafeHandle</c> becomes a pointer-sized integer, a by-reference parameter a pointer, a
/// <c>bool</c> the byte or word its <c>MarshalAs</c> names, an enum its underlying integer, and a
/// value type its exact layout from the runtime carrier table or from the module's own declaration.
/// Anything else is refused with its reason.
/// </summary>
public sealed class SwiftManagedCarrierClassifier
{
    private readonly SwiftManagedTypeIndex _index;

    public SwiftManagedCarrierClassifier(SwiftManagedTypeIndex index) => _index = index;

    /// <summary>One parameter's carrier.</summary>
    public SwiftManagedValue Classify(SwiftManagedParameter parameter, SwiftManagedSignature signature) =>
        Classify(parameter.Type, parameter.RefKind, parameter.MarshalAs, signature.Marshalling, signature.Scope, isReturn: false);

    /// <summary>
    /// The layout a declared struct's own fields give it, without looking it up in the carrier table:
    /// what the table's entries are checked against.
    /// </summary>
    internal SwiftManagedValue LayoutOfDeclaration(SwiftManagedStruct declared) =>
        Declared(declared, declared.FullName, depth: 0, SwiftManagedMarshalling.None);

    /// <summary>The return carrier.</summary>
    public SwiftManagedValue ClassifyReturn(SwiftManagedSignature signature) =>
        Classify(signature.ReturnType, SwiftManagedRefKind.None, signature.ReturnMarshalAs, signature.Marshalling, signature.Scope, isReturn: true);

    private SwiftManagedValue Classify(string spelling, SwiftManagedRefKind refKind, string? marshalAs, SwiftManagedMarshalling marshalling, SwiftManagedScope scope, bool isReturn)
    {
        var type = SyntaxFactory.ParseTypeName(spelling);
        bool byReference = refKind != SwiftManagedRefKind.None;
        if (type is RefTypeSyntax refType)
        {
            byReference = true;
            type = refType.Type;
        }

        if (type is PointerTypeSyntax pointer)
        {
            if (SwiftManagedTypeIndex.SimpleName(pointer.ElementType) == "SwiftError" && !byReference)
                return new SwiftManagedValue(SwiftManagedValueKind.Error, ErrorType);
            return SwiftManagedValue.Scalar(AddressType, SwiftScalarKind.Pointer, byReference: byReference);
        }
        if (type is FunctionPointerTypeSyntax)
            return SwiftManagedValue.Scalar(AddressType, SwiftScalarKind.Pointer, byReference: byReference);

        var simple = type is PredefinedTypeSyntax predefined ? predefined.Keyword.ValueText : SwiftManagedTypeIndex.SimpleName(type);
        if (byReference)
        {
            if (simple == "SwiftError")
                return isReturn
                    ? SwiftManagedValue.Refuse(spelling, SwiftPlanRefusalKind.NonBlittable, "a SwiftError cannot be returned by reference")
                    : new SwiftManagedValue(SwiftManagedValueKind.Error, ErrorType);
            // The address of the caller's storage; what it points at never crosses.
            return SwiftManagedValue.Scalar(AddressType, SwiftScalarKind.Pointer, byReference: true);
        }

        switch (simple)
        {
            case "void" when type is PredefinedTypeSyntax:
                return new SwiftManagedValue(SwiftManagedValueKind.Void, "void");
            case "SwiftIndirectResult":
                return new SwiftManagedValue(SwiftManagedValueKind.IndirectResult, "SwiftIndirectResult");
            case "SwiftSelf" when type is GenericNameSyntax or QualifiedNameSyntax { Right: GenericNameSyntax }:
                return SwiftManagedValue.Refuse(spelling, SwiftPlanRefusalKind.NonBlittable,
                    "SwiftSelf<T> carries a lowered value in the self register, which a bridge does not place");
            case "SwiftSelf":
                return new SwiftManagedValue(SwiftManagedValueKind.Self, "SwiftSelf");
            case "SwiftError":
                return SwiftManagedValue.Refuse(spelling, SwiftPlanRefusalKind.NonBlittable, "a SwiftError by value is not the error register");
        }

        if (Scalar(simple) is { } scalar)
        {
            if (scalar.Kind == SwiftScalarKind.Bool)
                return Bool(spelling, marshalAs, marshalling);
            return SwiftManagedValue.Scalar(scalar.Keyword, scalar.Kind, scalar.Signed);
        }
        if (Refused(simple) is { } refused)
            return SwiftManagedValue.Refuse(spelling, refused.Kind, refused.Reason);

        if (simple is "SafeHandle")
        {
            // Both marshallers pass a SafeHandle as its handle, after taking a reference on it.
            return marshalling == SwiftManagedMarshalling.None || isReturn
                ? SwiftManagedValue.Refuse(spelling, SwiftPlanRefusalKind.ManagedReference, "a SafeHandle is a managed reference unless a marshaller lowers it")
                : SwiftManagedValue.Scalar(AddressType, SwiftScalarKind.Pointer);
        }

        return Named(type, spelling, scope, depth: 0, marshalling);
    }

    /// <summary>The canonical type of anything that crosses as an address.</summary>
    private const string AddressType = "nint";

    private const string ErrorType = "SwiftError*";

    private static SwiftManagedValue Bool(string spelling, string? marshalAs, SwiftManagedMarshalling marshalling) => marshalAs switch
    {
        "U1" or "I1" => SwiftManagedValue.Scalar("bool", SwiftScalarKind.Int8, signed: false),
        "I4" or "U4" or "Bool" => SwiftManagedValue.Scalar("bool", SwiftScalarKind.Int32, signed: marshalAs != "U4"),
        null when marshalling == SwiftManagedMarshalling.None => SwiftManagedValue.Scalar("bool", SwiftScalarKind.Int8, signed: false),
        null => SwiftManagedValue.Refuse(spelling, SwiftPlanRefusalKind.NonBlittable,
            "a bool with no MarshalAs is marshalled as a four-byte BOOL"),
        _ => SwiftManagedValue.Refuse(spelling, SwiftPlanRefusalKind.NonBlittable, $"a bool marshalled as {marshalAs}"),
    };

    /// <summary>A predefined or framework scalar: its kind, its signedness and its C# keyword.</summary>
    private static (SwiftScalarKind Kind, bool Signed, string Keyword)? Scalar(string simple) => simple switch
    {
        "bool" or "Boolean" => (SwiftScalarKind.Bool, false, "bool"),
        "sbyte" or "SByte" => (SwiftScalarKind.Int8, true, "sbyte"),
        "byte" or "Byte" => (SwiftScalarKind.Int8, false, "byte"),
        "short" or "Int16" => (SwiftScalarKind.Int16, true, "short"),
        "ushort" or "UInt16" => (SwiftScalarKind.Int16, false, "ushort"),
        "char" or "Char" => (SwiftScalarKind.Int16, false, "char"),
        "int" or "Int32" => (SwiftScalarKind.Int32, true, "int"),
        "uint" or "UInt32" => (SwiftScalarKind.Int32, false, "uint"),
        "long" or "Int64" => (SwiftScalarKind.Int64, true, "long"),
        "ulong" or "UInt64" => (SwiftScalarKind.Int64, false, "ulong"),
        "nint" or "IntPtr" => (SwiftScalarKind.Pointer, true, "nint"),
        "nuint" or "UIntPtr" => (SwiftScalarKind.Pointer, false, "nuint"),
        "float" or "Single" => (SwiftScalarKind.Float, true, "float"),
        "double" or "Double" => (SwiftScalarKind.Double, true, "double"),
        _ => null,
    };

    private static (SwiftPlanRefusalKind Kind, string Reason)? Refused(string simple) => simple switch
    {
        "decimal" or "Decimal" => (SwiftPlanRefusalKind.UnsupportedScalar, "decimal has no Swift register lowering"),
        "Int128" or "UInt128" => (SwiftPlanRefusalKind.UnsupportedScalar, $"{simple} is passed differently by the two conventions"),
        "Half" => (SwiftPlanRefusalKind.UnsupportedScalar, "Float16 (Half) is passed differently by the two conventions"),
        "string" or "String" or "object" or "Object" or "Delegate" or "Array" or "Type" =>
            (SwiftPlanRefusalKind.ManagedReference, $"{simple} is a managed reference"),
        // Vector<T>, Vector2–4, Vector64/128/256/512<T>.
        _ when simple == "Vector" || simple.StartsWith("Vector", StringComparison.Ordinal) && simple.Length > 6 && char.IsDigit(simple[6]) =>
            (SwiftPlanRefusalKind.Vector, $"{simple} is a vector carrier"),
        _ => null,
    };

    /// <summary>A named type: an enum, a table carrier, or a struct the module declares.</summary>
    private SwiftManagedValue Named(TypeSyntax type, string spelling, SwiftManagedScope scope, int depth, SwiftManagedMarshalling marshalling)
    {
        if (type is ArrayTypeSyntax)
            return SwiftManagedValue.Refuse(spelling, SwiftPlanRefusalKind.ManagedReference, "an array is a managed reference");
        if (type is NullableTypeSyntax)
            return SwiftManagedValue.Refuse(spelling, SwiftPlanRefusalKind.NonBlittable, "a Nullable<T> is not blittable");
        if (type is TupleTypeSyntax)
            return SwiftManagedValue.Refuse(spelling, SwiftPlanRefusalKind.NonBlittable, "a ValueTuple's layout is automatic");

        var (name, arity) = NameOf(type);
        foreach (var candidate in _index.Candidates(name, scope))
        {
            if ((_index.TryGetEnum(candidate, out var underlying) || SwiftManagedCarrierTable.Enums.TryGetValue(candidate, out underlying!)) && arity == 0)
            {
                var scalar = Scalar(underlying.Contains('.') ? underlying[(underlying.LastIndexOf('.') + 1)..] : underlying);
                return scalar is { Kind: not (SwiftScalarKind.Bool or SwiftScalarKind.Float or SwiftScalarKind.Double) } s
                    ? SwiftManagedValue.Scalar(candidate, s.Kind, s.Signed)
                    : SwiftManagedValue.Refuse(spelling, SwiftPlanRefusalKind.NonBlittable, $"enum {candidate} has the underlying type {underlying}");
            }
            var tableName = arity > 0 ? $"{candidate}`{arity}" : candidate;
            if (SwiftManagedCarrierTable.TryGet(tableName, out var tableLayout, out var tableSize))
            {
                if (arity > 0 && !IsLayoutIndependentGeneric(tableName))
                    return SwiftManagedValue.Refuse(spelling, SwiftPlanRefusalKind.UnresolvedGeneric, $"{tableName}'s layout depends on its arguments");
                return new SwiftManagedValue(SwiftManagedValueKind.Aggregate, tableName, tableLayout, tableSize);
            }
            if (_index.TryGetStruct(candidate, out var declared) && arity == 0)
                return Declared(declared, declared.FullName, depth, marshalling);
            if (_index.IsReference(candidate))
                return SwiftManagedValue.Refuse(spelling, SwiftPlanRefusalKind.ManagedReference, $"{candidate} is a managed reference");
        }
        if (arity > 0)
            return SwiftManagedValue.Refuse(spelling, SwiftPlanRefusalKind.UnresolvedGeneric, $"the generic type {name}`{arity} is neither in the carrier table nor declared by the module");
        return SwiftManagedValue.Refuse(spelling, SwiftPlanRefusalKind.UnknownAggregate,
            $"{name} is neither in the carrier table nor a value type the module declares (a class is a managed reference)");
    }

    /// <summary>A generic table carrier whose argument only appears behind a pointer, so its layout is fixed.</summary>
    private static bool IsLayoutIndependentGeneric(string tableName) => tableName is
        "Swift.UnsafePointer`1" or "Swift.UnsafeMutablePointer`1" or "Swift.UnsafeBufferPointer`1" or "Swift.UnsafeMutableBufferPointer`1";

    private static (string Name, int Arity) NameOf(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax q => (QualifiedLeft(q.Left) + "." + NameOf(q.Right).Name, NameOf(q.Right).Arity),
        GenericNameSyntax g => (g.Identifier.ValueText, g.TypeArgumentList.Arguments.Count),
        SimpleNameSyntax s => (s.Identifier.ValueText, 0),
        AliasQualifiedNameSyntax a => NameOf(a.Name),
        _ => (type.ToString(), 0),
    };

    private static string QualifiedLeft(NameSyntax left) => left switch
    {
        QualifiedNameSyntax q => QualifiedLeft(q.Left) + "." + NameOf(q.Right).Name,
        AliasQualifiedNameSyntax a => NameOf(a.Name).Name,
        SimpleNameSyntax s => s.Identifier.ValueText,
        _ => left.ToString(),
    };

    /// <summary>A struct the module declares: its fields laid out sequentially, each classified in turn.</summary>
    private SwiftManagedValue Declared(SwiftManagedStruct declared, string spelling, int depth, SwiftManagedMarshalling marshalling)
    {
        if (depth > 16)
            return SwiftManagedValue.Refuse(spelling, SwiftPlanRefusalKind.UnknownAggregate, $"{declared.FullName} nests too deeply");
        if (declared.Problem is { } problem)
            return SwiftManagedValue.Refuse(spelling, problem.Contains("generic", StringComparison.Ordinal) ? SwiftPlanRefusalKind.UnresolvedGeneric
                : problem.Contains("offset", StringComparison.Ordinal) ? SwiftPlanRefusalKind.ExplicitLayout
                : SwiftPlanRefusalKind.NonBlittable, $"{declared.FullName}: {problem}");
        if (declared.Layout != "Sequential")
            return SwiftManagedValue.Refuse(spelling, SwiftPlanRefusalKind.ExplicitLayout, $"{declared.FullName} has {declared.Layout} layout");
        if (declared.ExplicitSize != 0)
            return SwiftManagedValue.Refuse(spelling, SwiftPlanRefusalKind.ExplicitLayout, $"{declared.FullName} sets an explicit size");

        var scope = new SwiftManagedScope(declared.FullName, declared.Usings);
        var leaves = ImmutableArray.CreateBuilder<SwiftLayoutLeaf>();
        int cursor = 0, alignment = 1;
        foreach (var field in declared.Fields)
        {
            var value = Classify(field, SwiftManagedRefKind.None, null, marshalling, scope, isReturn: false, depth + 1);
            if (value.Kind == SwiftManagedValueKind.Refused)
                return SwiftManagedValue.Refuse(spelling, value.Refusal!.Kind, $"{declared.FullName} field of type {field}: {value.Refusal.Reason}");
            if (value.Kind != SwiftManagedValueKind.Primitive && value.Kind != SwiftManagedValueKind.Aggregate)
                return SwiftManagedValue.Refuse(spelling, SwiftPlanRefusalKind.NonBlittable, $"{declared.FullName} has a {field} field");
            var fieldLayout = value.Layout!;
            int fieldSize = value.Kind == SwiftManagedValueKind.Aggregate ? value.ManagedSize : fieldLayout.Size;
            int fieldAlignment = declared.Pack > 0 ? Math.Min(declared.Pack, fieldLayout.Alignment) : fieldLayout.Alignment;
            cursor = SwiftLeafLayout.AlignUp(cursor, fieldAlignment);
            foreach (var leaf in fieldLayout.Leaves)
                leaves.Add(leaf with { Offset = cursor + leaf.Offset });
            cursor += fieldSize;
            alignment = Math.Max(alignment, fieldAlignment);
        }
        var managedSize = SwiftLeafLayout.AlignUp(Math.Max(cursor, 1), alignment);
        return new SwiftManagedValue(SwiftManagedValueKind.Aggregate, spelling, new SwiftLeafLayout(cursor, alignment, leaves.ToImmutable()), managedSize);
    }

    private SwiftManagedValue Classify(string spelling, SwiftManagedRefKind refKind, string? marshalAs, SwiftManagedMarshalling marshalling, SwiftManagedScope scope, bool isReturn, int depth)
    {
        if (depth == 0)
            return Classify(spelling, refKind, marshalAs, marshalling, scope, isReturn);
        var type = SyntaxFactory.ParseTypeName(spelling);
        if (type is PointerTypeSyntax or FunctionPointerTypeSyntax)
            return SwiftManagedValue.Scalar(AddressType, SwiftScalarKind.Pointer);
        var simple = type is PredefinedTypeSyntax predefined ? predefined.Keyword.ValueText : SwiftManagedTypeIndex.SimpleName(type);
        // A marshaller copies a struct with a bool or char field into a native layout of its own
        // (a four-byte BOOL, an ANSI char); only with no marshaller does the field cross as it is.
        if (marshalling != SwiftManagedMarshalling.None && simple is "bool" or "Boolean" or "char" or "Char")
            return SwiftManagedValue.Refuse(spelling, SwiftPlanRefusalKind.NonBlittable, $"a {simple} field makes the struct non-blittable, so the marshaller converts it");
        if (Scalar(simple) is { } scalar)
            return scalar.Kind == SwiftScalarKind.Bool
                ? SwiftManagedValue.Scalar("bool", SwiftScalarKind.Int8)
                : SwiftManagedValue.Scalar(scalar.Keyword, scalar.Kind, scalar.Signed);
        if (Refused(simple) is { } refused)
            return SwiftManagedValue.Refuse(spelling, refused.Kind, refused.Reason);
        return Named(type, spelling, scope, depth, marshalling);
    }
}
