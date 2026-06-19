// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins <see cref="PublicMethodNameContext"/> and its <see cref="PublicMethodNameContext.ForMethod"/>
/// factory (AF05 Target C). The positional <c>GetPublicMethodName(string, bool, …)</c> shim and the
/// <c>in PublicMethodNameContext</c> overload must agree, and <c>ForMethod</c> must derive the seven
/// shaping fields from a <see cref="MethodDecl"/> the same way the authoritative emitted name does —
/// so no method-derived call site can silently drop a collision-shaping arg. The parent-name
/// collision axis (step 4c) is the one most easily lost; it is isolable from the noun→"Get" prefix
/// (step 3) only when the parameter count is non-zero, so the collision cases below use a parameter.
/// </summary>
public class PublicMethodNameContextTests
{
    private static ClassDecl MakeClass(string name, ModuleDecl module) => new()
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module.Name}.{name}"),
        MangledName = $"$s{module.Name.Length}{module.Name}{name.Length}{name}C",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        GenericParameters = new List<GenericArgumentDecl>(),
        Conformances = new List<TypeConformance>(),
        ParentDecl = module,
        ModuleDecl = module,
    };

    private static ArgumentDecl Arg(TypeSpec type, string name) => new()
    {
        SwiftTypeSpec = type,
        Name = name,
        PrivateName = name,
        IsInOut = false,
        IsGeneric = false,
        ParentDecl = null,
        ModuleDecl = null,
    };

    // A #file-style implicit debug parameter: HasDefaultArg + a name/type pair IsDebugParameter accepts.
    private static ArgumentDecl DebugArg(string name) => new()
    {
        SwiftTypeSpec = new NamedTypeSpec("Swift.StaticString"),
        Name = name,
        PrivateName = name,
        IsInOut = false,
        IsGeneric = false,
        HasDefaultArg = true,
        ParentDecl = null,
        ModuleDecl = null,
    };

    #region Context overload — parent-name collision axis (step 4c)

    [Fact]
    public void Context_ParentNameCollision_PrefixesGet()
    {
        // databaseRegion() on type DatabaseRegion would emit a member named identically to the
        // enclosing type (CS0542); step 4c rewrites it. ParameterCount=1 keeps step 3 from firing,
        // isolating the parent-name axis.
        var ctx = new PublicMethodNameContext(
            MethodName: "databaseRegion",
            IsAsync: false,
            HasReturnValue: true,
            PropertyNames: null,
            IsSelfReturning: false,
            ParentTypeName: "DatabaseRegion",
            ParameterCount: 1);

        Assert.Equal("GetDatabaseRegion", NameProvider.GetPublicMethodName(in ctx));
    }

    [Fact]
    public void Context_NoParentNameCollision_KeepsName()
    {
        var ctx = new PublicMethodNameContext(
            MethodName: "databaseRegion",
            IsAsync: false,
            HasReturnValue: true,
            PropertyNames: null,
            IsSelfReturning: false,
            ParentTypeName: null,
            ParameterCount: 1);

        Assert.Equal("DatabaseRegion", NameProvider.GetPublicMethodName(in ctx));
    }

    #endregion

    #region Shim ≡ context overload

    [Fact]
    public void PositionalShim_EqualsContextOverload_ForParentCollision()
    {
        var ctx = new PublicMethodNameContext(
            MethodName: "databaseRegion",
            IsAsync: false,
            HasReturnValue: true,
            PropertyNames: null,
            IsSelfReturning: false,
            ParentTypeName: "DatabaseRegion",
            ParameterCount: 1);

        var viaShim = NameProvider.GetPublicMethodName(
            "databaseRegion", isAsync: false, hasReturnValue: true, propertyNames: null,
            isSelfReturning: false, parentTypeName: "DatabaseRegion", parameterCount: 1);

        Assert.Equal(NameProvider.GetPublicMethodName(in ctx), viaShim);
    }

    #endregion

    #region ForMethod — derives all seven fields from a MethodDecl

    [Fact]
    public void ForMethod_DerivesAllFields()
    {
        var module = TestModelFactory.CreateModuleDecl();
        var cls = MakeClass("DatabaseRegion", module);
        // CSSignature[0] = return slot (Swift.Int), [1] = one real parameter.
        var method = TestModelFactory.CreateMethod(
            "databaseRegion",
            parent: cls,
            args: new[] { ("", "Swift.Int"), ("other", "Swift.String") });

        var ctx = PublicMethodNameContext.ForMethod(method, siblingPropertyNames: null);

        Assert.Equal("databaseRegion", ctx.MethodName);
        Assert.False(ctx.IsAsync);
        Assert.True(ctx.HasReturnValue);          // non-void return slot
        Assert.Null(ctx.PropertyNames);
        Assert.False(ctx.IsSelfReturning);
        Assert.Equal("DatabaseRegion", ctx.ParentTypeName);
        Assert.Equal(1, ctx.ParameterCount);       // Skip(1) → one real param
    }

    [Fact]
    public void ForMethod_VoidReturn_HasNoReturnValue()
    {
        var module = TestModelFactory.CreateModuleDecl();
        var cls = MakeClass("Widget", module);
        // No args → factory emits a single empty-tuple slot → void return, zero params.
        var method = TestModelFactory.CreateMethod("refresh", parent: cls);

        var ctx = PublicMethodNameContext.ForMethod(method, siblingPropertyNames: null);

        Assert.False(ctx.HasReturnValue);
        Assert.Equal(0, ctx.ParameterCount);
    }

    [Fact]
    public void ForMethod_Accessor_HasNoReturnValue_EvenWithNonVoidReturnSlot()
    {
        // The `!IsAccessor` clause in HasReturnValue is the one formula delta the three key builders
        // gained over their former inline derivation. Accessors never reach the key builders in
        // practice, but pin the clause directly so a future change can't silently flip it.
        var module = TestModelFactory.CreateModuleDecl();
        var cls = MakeClass("Widget", module);
        var method = TestModelFactory.CreateMethod(
            "value",
            parent: cls,
            args: new[] { ("", "Swift.Int") }) with { IsAccessor = true };

        var ctx = PublicMethodNameContext.ForMethod(method, siblingPropertyNames: null);

        Assert.False(ctx.HasReturnValue);   // forced false by !IsAccessor despite the Swift.Int return slot
    }

    [Fact]
    public void ForMethod_ParameterCount_ExcludesDebugAndEmptyTupleParams()
    {
        // ParameterCount = CSSignature.Skip(1).Count(!IsDebugParameter && !IsEmptyTuple). Build a
        // signature with [return, realParam, debugParam, emptyTupleParam] and assert only the real
        // parameter is counted.
        var module = TestModelFactory.CreateModuleDecl();
        var cls = MakeClass("Widget", module);
        var method = TestModelFactory.CreateMethod("doWork", parent: cls) with
        {
            CSSignature = new List<ArgumentDecl>
            {
                Arg(new NamedTypeSpec("Swift.Int"), name: ""),                 // [0] return slot
                Arg(new NamedTypeSpec("Swift.String"), name: "value"),         // real parameter
                DebugArg("_file"),                                             // #file default arg
                Arg(TupleTypeSpec.Empty, name: ""),                            // empty-tuple param
            }
        };

        var ctx = PublicMethodNameContext.ForMethod(method, siblingPropertyNames: null);

        Assert.Equal(1, ctx.ParameterCount);
    }

    [Fact]
    public void ForMethod_ParentTypeName_ComesFromTypeParent_AndProtocolKeyPathSuppressesIt()
    {
        // ForMethod reads ParentTypeName from a TypeDecl parent — including a ProtocolDecl (which is a
        // TypeDecl). ProtocolSignatureHelper deliberately overrides it to null (`with { ParentTypeName
        // = null }`) to preserve the protocol-interface key's historical omission; reproduce that here
        // so the suppression is observably load-bearing (the raw ForMethod value is non-null).
        var module = TestModelFactory.CreateModuleDecl();
        var protocol = (ProtocolDecl)module.Protocols[0];   // "IThing" from the factory
        var method = TestModelFactory.CreateMethod(
            "doWork",
            parent: protocol,
            args: new[] { ("", "Swift.Int"), ("v", "Swift.String") });

        var raw = PublicMethodNameContext.ForMethod(method, siblingPropertyNames: null);
        Assert.Equal(protocol.Name, raw.ParentTypeName);            // protocol parent IS a TypeDecl

        var suppressed = raw with { ParentTypeName = null };
        Assert.Null(suppressed.ParentTypeName);
    }

    [Fact]
    public void ForMethod_ThroughGetPublicMethodName_MatchesPositional_IncludingParentCollision()
    {
        var module = TestModelFactory.CreateModuleDecl();
        var cls = MakeClass("DatabaseRegion", module);
        var method = TestModelFactory.CreateMethod(
            "databaseRegion",
            parent: cls,
            args: new[] { ("", "Swift.Int"), ("other", "Swift.String") });

        var ctx = PublicMethodNameContext.ForMethod(method, siblingPropertyNames: null);
        var viaContext = NameProvider.GetPublicMethodName(in ctx);

        // The legacy positional call shaped from the same decl's fields must agree — and the
        // parent-name collision must be present (proving ForMethod carries ParentTypeName).
        var viaPositional = NameProvider.GetPublicMethodName(
            ctx.MethodName, ctx.IsAsync, ctx.HasReturnValue, ctx.PropertyNames,
            ctx.IsSelfReturning, ctx.ParentTypeName, ctx.ParameterCount);

        Assert.Equal("GetDatabaseRegion", viaContext);
        Assert.Equal(viaContext, viaPositional);
    }

    #endregion

    #region Property collision still folds in (regression guard for the propertyNames axis)

    [Fact]
    public void Context_PropertyCollision_AppendsMethod()
    {
        var ctx = new PublicMethodNameContext(
            MethodName: "title",
            IsAsync: false,
            HasReturnValue: false,
            PropertyNames: new HashSet<string> { "Title" },
            IsSelfReturning: false,
            ParentTypeName: null,
            ParameterCount: 1);

        Assert.Equal("TitleMethod", NameProvider.GetPublicMethodName(in ctx));
    }

    #endregion
}
