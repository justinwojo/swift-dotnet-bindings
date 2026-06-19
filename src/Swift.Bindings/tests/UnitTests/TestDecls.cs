// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;

namespace BindingsGeneration.Tests;

/// <summary>
/// One shared, <b>parser-faithful</b> factory for the decl models the emitter/marshaler tests feed.
/// Its defaults match what the production <c>SwiftABIParser</c> actually emits, so a member built here
/// exercises a combination the generator can really see — every "fiction" flag is opt-in.
///
/// <para>The load-bearing rule (Finding 31): a protocol requirement built through
/// <see cref="Protocol(string, BaseDecl[])"/> defaults <c>IsProtocolRequirement = true</c>. The bare
/// <see cref="Method"/>/<see cref="Property"/> factories default it <c>false</c> (a struct/class member,
/// or an as-yet-unattached decl, is not a protocol requirement) — but the instant such a decl is handed
/// to <c>Protocol(...)</c> as a requirement it is promoted to <c>true</c>. This stops vtable-struct tests
/// from silently exercising the parser-impossible <c>IsProtocolRequirement=false</c> requirement that the
/// plan-builder predicate (<c>ProtocolVtableMembers.IncludesProperty</c>) rejects — the unit-level shadow
/// of the Defect-F positional corruption.</para>
///
/// <para>A genuine protocol-extension default (Swift-owned, no vtable slot) is built via
/// <see cref="ExtensionDefault"/> / <see cref="ExtensionDefaultProperty"/>; those carry the
/// extension marker and <c>Protocol(...)</c> deliberately leaves their <c>IsProtocolRequirement</c>
/// at <c>false</c>.</para>
/// </summary>
internal static class TestDecls
{
    /// <summary>The module name baked into mangled names and qualified type names by default.</summary>
    internal const string DefaultModule = "TestModule";

    // ===================================================================
    //  Methods
    // ===================================================================

    /// <summary>
    /// An instance method whose defaults mirror the parser: not a constructor, non-throwing, non-async,
    /// not a protocol requirement (promoted to one only when passed to <see cref="Protocol"/>), with a
    /// parser-shaped mangled name. <paramref name="returnType"/> is the <c>CSSignature[0]</c> return
    /// slot (void by default); <paramref name="parameters"/> follow.
    /// </summary>
    internal static MethodDecl Method(
        string name,
        MethodType methodType = MethodType.Instance,
        bool isConstructor = false,
        bool throws = false,
        bool isAsync = false,
        IEnumerable<ArgumentDecl>? parameters = null,
        TypeSpec? returnType = null,
        string module = DefaultModule)
    {
        var signature = new List<ArgumentDecl> { ReturnSlot(returnType) };
        if (parameters is not null)
            signature.AddRange(parameters);

        return new MethodDecl
        {
            Name = name,
            // Parser-faithful symbol shape: $s<modLen><module><nameLen><name>yyF. Field names in the
            // emitted vtable derive from Name, not this, so a canonical mangled name is always safe.
            MangledName = MethodMangledName(name, module),
            MethodType = methodType,
            IsConstructor = isConstructor,
            CSSignature = signature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = throws,
            IsAsync = isAsync,
            IsSynthesizedAccessor = false,
        };
    }

    /// <summary>
    /// A protocol-<b>extension</b> default method: a Swift-owned default implementation, NOT a vtable
    /// requirement. Marked <c>IsProtocolExtensionMethod</c>/<c>IsExtensionMethod</c> so
    /// <see cref="Protocol"/> leaves its <c>IsProtocolRequirement</c> at <c>false</c>. Use this when a
    /// test genuinely wants the non-requirement path — never the bare <c>false</c> default by accident.
    /// </summary>
    internal static MethodDecl ExtensionDefault(
        string name,
        MethodType methodType = MethodType.Instance,
        bool throws = false,
        bool isAsync = false,
        IEnumerable<ArgumentDecl>? parameters = null,
        TypeSpec? returnType = null,
        string module = DefaultModule)
    {
        var method = Method(name, methodType, isConstructor: false, throws, isAsync, parameters, returnType, module);
        method.IsProtocolExtensionMethod = true;
        method.IsExtensionMethod = true;
        return method;
    }

    // ===================================================================
    //  Properties
    // ===================================================================

    /// <summary>
    /// A property whose defaults mirror the parser: instance, computed (no storage), <c>Swift.Int</c>,
    /// get-only, not a protocol requirement (promoted only via <see cref="Protocol"/>). Each requested
    /// accessor gets a backing <see cref="Method"/> so the decl is fully formed.
    /// </summary>
    internal static PropertyDecl Property(
        string name,
        TypeSpec? type = null,
        bool isStatic = false,
        bool hasGetter = true,
        bool hasSetter = false,
        string module = DefaultModule)
    {
        var accessors = new List<AccessorDecl>();
        if (hasGetter)
            accessors.Add(new GetAccessorDecl { Method = Method($"{name}_get", module: module) });
        if (hasSetter)
            accessors.Add(new SetAccessorDecl { Method = Method($"{name}_set", module: module) });

        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = type ?? new NamedTypeSpec("Swift.Int"),
            IsStatic = isStatic,
            HasStorage = false,
            Accessors = accessors,
            ParentDecl = null,
            ModuleDecl = null,
            // IsProtocolRequirement / IsFromExtension default false; Protocol(...) promotes requirements.
        };
    }

    /// <summary>
    /// A protocol-<b>extension</b> default property (Swift-owned, no vtable slot): marked
    /// <c>IsFromExtension</c> so <see cref="Protocol"/> leaves <c>IsProtocolRequirement</c> at
    /// <c>false</c>. The opt-in counterpart to <see cref="ExtensionDefault"/> for properties.
    /// </summary>
    internal static PropertyDecl ExtensionDefaultProperty(
        string name,
        TypeSpec? type = null,
        bool isStatic = false,
        bool hasGetter = true,
        bool hasSetter = false,
        string module = DefaultModule)
    {
        var property = Property(name, type, isStatic, hasGetter, hasSetter, module);
        property.IsFromExtension = true;
        return property;
    }

    // ===================================================================
    //  Protocol
    // ===================================================================

    /// <summary>
    /// A protocol carrying the supplied requirements. Each <see cref="MethodDecl"/>/<see cref="PropertyDecl"/>
    /// passed is promoted to <c>IsProtocolRequirement = true</c> UNLESS it was built via
    /// <see cref="ExtensionDefault"/>/<see cref="ExtensionDefaultProperty"/> (an extension default keeps
    /// <c>false</c>). This is the single rule that makes vtable-struct tests stop exercising the
    /// parser-impossible non-requirement requirement.
    /// </summary>
    internal static ProtocolDecl Protocol(string name, params BaseDecl[] requirements)
        => Protocol(name, DefaultModule, requirements);

    /// <inheritdoc cref="Protocol(string, BaseDecl[])"/>
    internal static ProtocolDecl Protocol(string name, string module, params BaseDecl[] requirements)
    {
        var properties = new List<PropertyDecl>();
        var methods = new List<MethodDecl>();
        var subscripts = new List<SubscriptDecl>();

        foreach (var requirement in requirements)
        {
            switch (requirement)
            {
                case PropertyDecl property:
                    if (!property.IsFromExtension)
                        property.IsProtocolRequirement = true;
                    properties.Add(property);
                    break;
                case MethodDecl method:
                    if (!method.IsProtocolExtensionMethod && !method.IsExtensionMethod)
                        method.IsProtocolRequirement = true;
                    methods.Add(method);
                    break;
                case SubscriptDecl subscript:
                    subscripts.Add(subscript);
                    break;
                default:
                    throw new ArgumentException(
                        $"TestDecls.Protocol cannot carry a {requirement.GetType().Name} requirement.",
                        nameof(requirements));
            }
        }

        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"),
            MangledName = $"$s{module.Length}{module}{name.Length}{name}P",
            Properties = properties,
            Methods = methods,
            Subscripts = subscripts,
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
    }

    // ===================================================================
    //  Argument helpers
    // ===================================================================

    /// <summary>A labelled value parameter (not inout, not generic).</summary>
    internal static ArgumentDecl Param(string label, TypeSpec type) => new()
    {
        Name = label,
        PrivateName = label,
        SwiftTypeSpec = type,
        IsInOut = false,
        IsGeneric = false,
        ParentDecl = null,
        ModuleDecl = null,
    };

    /// <summary>The <c>CSSignature[0]</c> return slot — the empty tuple (void) unless a type is given.</summary>
    private static ArgumentDecl ReturnSlot(TypeSpec? returnType) => new()
    {
        Name = "",
        PrivateName = "",
        SwiftTypeSpec = returnType ?? TupleTypeSpec.Empty,
        IsInOut = false,
        IsGeneric = false,
        ParentDecl = null,
        ModuleDecl = null,
    };

    private static string MethodMangledName(string name, string module)
        => $"$s{module.Length}{module}{name.Length}{name}yyF";
}
