// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Text;

namespace BindingsGeneration;

/// <summary>
/// The one place that turns a parsed declaration into a <see cref="DeclId"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every component is read straight off the decl, so an id is a pure function of the parse. The
/// methods are deliberately static and memo-free: an id costs a handful of string reads, while a
/// process-global cache would be exactly the kind of cross-run shared state that makes emission
/// order-dependent. Callers that need an id repeatedly should hold onto the value, not reach for
/// a shared dictionary.
/// </para>
/// <para>
/// Emitters must not hand-roll their own component extraction — the whole point of the id is that
/// two subsystems describing the same declaration produce the same string.
/// </para>
/// </remarks>
public static class DeclIdFactory
{
    /// <summary>Identity for a whole Swift module.</summary>
    public static DeclId ForModule(ModuleDecl moduleDecl)
    {
        ArgumentNullException.ThrowIfNull(moduleDecl);
        return DeclId.Create(moduleDecl.Name, declPath: null, BindingItemKind.Module, moduleDecl.Name);
    }

    /// <summary>Identity for a whole Swift module, from its name alone.</summary>
    public static DeclId ForModule(string moduleName) =>
        DeclId.Create(moduleName, declPath: null, BindingItemKind.Module, moduleName);

    /// <summary>
    /// Identity for a type declaration. The type's own generic parameter list is the generic
    /// context, so <c>Box&lt;T&gt;</c> and a non-generic <c>Box</c> are distinct.
    /// </summary>
    public static DeclId ForType(TypeDecl typeDecl)
    {
        ArgumentNullException.ThrowIfNull(typeDecl);
        var (module, declPath) = SplitContainer(typeDecl.ParentDecl);
        return DeclId.Create(
            module,
            declPath,
            BindingItemKind.Type,
            typeDecl.Name,
            genericContext: RenderGenericParameters(typeDecl.GenericParameters));
    }

    /// <summary>
    /// Identity for a method or constructor. Parameter labels and Swift type expressions are
    /// carried in declaration order, which is what separates overloads; the generic signature
    /// separates a generic overload from its concrete twin.
    /// </summary>
    public static DeclId ForMethod(MethodDecl methodDecl, BaseDecl? containingDecl = null)
    {
        ArgumentNullException.ThrowIfNull(methodDecl);
        var (module, declPath) = SplitContainer(containingDecl ?? methodDecl.ParentDecl);
        var (labels, types) = BuildParameterArrays(methodDecl.CSSignature);
        return DeclId.Create(
            module,
            declPath,
            BindingItemKind.Method,
            methodDecl.Name,
            labels,
            types,
            AccessorKind.None,
            GenericContextOf(methodDecl),
            methodDecl.MangledName);
    }

    /// <summary>
    /// Identity for a property. Pass an explicit <paramref name="accessor"/> for accessor-level
    /// identity; <see cref="AccessorKind.None"/> yields the property-level id that covers both
    /// accessors as one unit.
    /// </summary>
    public static DeclId ForProperty(
        PropertyDecl propertyDecl,
        AccessorKind accessor = AccessorKind.None,
        BaseDecl? containingDecl = null)
    {
        ArgumentNullException.ThrowIfNull(propertyDecl);
        var (module, declPath) = SplitContainer(containingDecl ?? propertyDecl.ParentDecl);
        return DeclId.Create(
            module,
            declPath,
            BindingItemKind.Property,
            propertyDecl.Name,
            accessor: accessor,
            // Swift allows an instance and a static property of the same name on one type, and a
            // property id carries no mangled symbol to tell them apart (the symbol lives on the
            // accessors, and folding it in here would change report-dedup semantics). Without this
            // the two collapse to a single id.
            discriminator: StaticnessOf(propertyDecl.IsStatic));
    }

    /// <summary>
    /// Identity for a subscript. Every Swift subscript shares the base name <c>subscript</c>, so
    /// the index parameter signature and the accessor kind carry the whole discrimination burden.
    /// </summary>
    public static DeclId ForSubscript(
        SubscriptDecl subscriptDecl,
        AccessorKind accessor = AccessorKind.None,
        BaseDecl? containingDecl = null)
    {
        ArgumentNullException.ThrowIfNull(subscriptDecl);
        var (module, declPath) = SplitContainer(containingDecl ?? subscriptDecl.ParentDecl);
        var (labels, types) = BuildParameterArrays(subscriptDecl.IndexParameters);
        return DeclId.Create(
            module,
            declPath,
            BindingItemKind.Subscript,
            subscriptDecl.Name,
            labels,
            types,
            accessor,
            genericContext: null,
            symbol: subscriptDecl.MangledName,
            discriminator: StaticnessOf(subscriptDecl.IsStatic));
    }

    /// <summary>
    /// Identity for an operator, taking its parameter signature from the underlying method so
    /// overloaded operators separate.
    /// </summary>
    public static DeclId ForOperator(OperatorDecl operatorDecl, BaseDecl? containingDecl = null)
    {
        ArgumentNullException.ThrowIfNull(operatorDecl);
        var (module, declPath) = SplitContainer(containingDecl ?? operatorDecl.ParentDecl);
        var (labels, types) = BuildParameterArrays(operatorDecl.UnderlyingMethod.CSSignature);
        return DeclId.Create(
            module,
            declPath,
            BindingItemKind.Operator,
            operatorDecl.OperatorSymbol,
            labels,
            types,
            AccessorKind.None,
            GenericContextOf(operatorDecl.UnderlyingMethod),
            operatorDecl.UnderlyingMethod.MangledName);
    }

    /// <summary>
    /// Identity from the coarse <c>(kind, name, containing decl)</c> triple, for call sites that
    /// don't have a richer decl in scope. Overloads sharing a base name collapse to one id under
    /// this entry point — prefer a decl-aware overload wherever the decl is available.
    /// </summary>
    public static DeclId ForMember(BindingItemKind kind, string name, BaseDecl? containingDecl)
    {
        var (module, declPath) = SplitContainer(containingDecl);
        return DeclId.Create(module, declPath, kind, name);
    }

    /// <summary>
    /// Dispatches to the decl-kind-appropriate factory. Returns <see langword="null"/> for a
    /// declaration shape with no id mapping, so callers can stay id-agnostic.
    /// </summary>
    public static DeclId? ForDecl(BaseDecl? decl) => decl switch
    {
        null => null,
        TypeDecl typeDecl => ForType(typeDecl),
        MethodDecl methodDecl => ForMethod(methodDecl),
        PropertyDecl propertyDecl => ForProperty(propertyDecl),
        SubscriptDecl subscriptDecl => ForSubscript(subscriptDecl),
        OperatorDecl operatorDecl => ForOperator(operatorDecl),
        ModuleDecl moduleDecl => ForModule(moduleDecl),
        _ => null,
    };

    /// <summary>
    /// Renders the static/instance axis as a discriminator component. Instance is the unmarked
    /// default, so only <c>static</c> contributes — an instance member's id is unchanged by this.
    /// </summary>
    private static string StaticnessOf(bool isStatic) => isStatic ? "static" : string.Empty;

    /// <summary>
    /// A method's generic context: the raw generic signature when the parser captured one,
    /// otherwise the declared parameter list. Both are stable ABI facts.
    /// </summary>
    private static string GenericContextOf(MethodDecl methodDecl) =>
        !string.IsNullOrWhiteSpace(methodDecl.RawGenericSig)
            ? methodDecl.RawGenericSig!
            : RenderGenericParameters(methodDecl.GenericParameters);

    /// <summary>Renders a generic parameter list as <c>&lt;T,U&gt;</c>; empty when non-generic.</summary>
    private static string RenderGenericParameters(IReadOnlyList<GenericArgumentDecl>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.Append('<');
        for (var i = 0; i < parameters.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(parameters[i].TypeName);
        }
        sb.Append('>');
        return sb.ToString();
    }

    /// <summary>
    /// Projects an argument list to parallel label/type arrays. Index 0 of a method's
    /// <c>CSSignature</c> is the return type, which is intentionally kept: two overloads may
    /// differ only in return type in the model even where Swift would forbid it.
    /// </summary>
    private static (ImmutableArray<string> Labels, ImmutableArray<string> Types) BuildParameterArrays(
        IReadOnlyList<ArgumentDecl> args)
    {
        if (args.Count == 0)
            return (ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);

        var labels = ImmutableArray.CreateBuilder<string>(args.Count);
        var types = ImmutableArray.CreateBuilder<string>(args.Count);
        foreach (var arg in args)
        {
            labels.Add(arg.Name ?? string.Empty);
            types.Add(arg.SwiftTypeSpec?.ToString() ?? string.Empty);
        }
        return (labels.MoveToImmutable(), types.MoveToImmutable());
    }

    /// <summary>
    /// Splits a containing declaration into its module and its dot-separated containing-type
    /// chain, so the two can be compared independently.
    /// </summary>
    private static (string Module, string DeclPath) SplitContainer(BaseDecl? containingDecl)
    {
        switch (containingDecl)
        {
            case TypeDecl typeDecl:
                {
                    // ModuleQualifiedName has the form "Module.TypeChain"
                    // (e.g. "TestModule.Loader.Payload"); split off the leading module token.
                    var qualified = typeDecl.SwiftTypeName.ModuleQualifiedName;
                    var firstDot = qualified.IndexOf('.');
                    return firstDot < 0
                        ? (qualified, string.Empty)
                        : (qualified.Substring(0, firstDot), qualified.Substring(firstDot + 1));
                }
            case ModuleDecl moduleDecl:
                return (moduleDecl.Name, string.Empty);
            case null:
                return (string.Empty, string.Empty);
            default:
                {
                    var moduleName = containingDecl.ModuleDecl?.Name ?? string.Empty;
                    return (moduleName, containingDecl.Name);
                }
        }
    }
}
