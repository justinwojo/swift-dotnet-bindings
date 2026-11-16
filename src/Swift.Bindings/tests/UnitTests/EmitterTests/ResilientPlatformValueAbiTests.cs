// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// <c>Foundation.URLError.Code</c> holds one <c>Int</c> and the bundled database projects it frozen
/// onto <c>nint</c>, but the SDK does not declare it <c>@frozen</c>: to a client it is address-only.
/// The SIL of <c>func f(_ c: URLError.Code)</c> takes <c>@in_guaranteed URLError.Code</c> and its IR
/// takes <c>ptr noalias</c>; a result comes back through <c>sret</c>. A Swift-convention P/Invoke
/// that passes the <c>nint</c> in a register hands Swift an integer it dereferences.
///
/// <para>These pin the three halves of the fix: the wrapper parameter carries the value as a plain
/// <c>Swift.Int</c> and rebuilds it, the transition layout calls the type indirect, and a member that
/// would still reach Swift directly is refused before any native call.</para>
/// </summary>
public class ResilientPlatformValueAbiTests
{
    private static readonly string s_dbDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift");

    private static async Task<(ModuleDecl Module, TypeDatabase Database)> CreateEnvironmentAsync()
    {
        var database = new TypeDatabase();
        await database.LoadModuleDatabaseFromFile(Path.Combine(s_dbDir, "SwiftDatabase.xml"));
        await database.LoadModuleDatabaseFromFile(Path.Combine(s_dbDir, "FoundationDatabase.xml"));
        var module = new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
        return (module, database);
    }

    private static readonly NamedTypeSpec s_code = new("Foundation.URLError.Code");

    [Fact]
    public async Task CdeclParameter_UrlErrorCode_TravelsAsSwiftInt()
    {
        var (_, database) = await CreateEnvironmentAsync();
        Assert.True(database.TryGetTypeRecord(s_code, out var record));

        Assert.Equal("Swift.Int", CdeclParamMapper.PrimitiveCarriedPlatformValue(s_code, record));
    }

    /// <summary>
    /// The closure, ArraySlice, optional-pointer and async wrappers take a member through
    /// <c>@_cdecl</c> only when every sibling parameter has a C representation. URLError.Code has
    /// one, the Int its cdecl lowering carries; refused, the member fell back to a Swift-convention
    /// wrapper that takes the code by address while the managed side passes the integer.
    /// </summary>
    [Fact]
    public async Task CdeclGate_UrlErrorCode_IsNotANestedStructWithoutACRepresentation()
    {
        var (_, database) = await CreateEnvironmentAsync();

        Assert.False(WrapperValidation.IsNestedFrozenStructParam(Arg(s_code, "value"), database));
    }

    [Fact]
    public async Task CdeclParameter_PlatformValueOnANonPrimitiveCarrier_IsNotReinterpreted()
    {
        // Date has its own conversion (seconds since the reference date); bit-casting a managed
        // DateTimeOffset's carrier to Date would be wrong, so the primitive arm must leave it alone.
        var (_, database) = await CreateEnvironmentAsync();
        var date = new NamedTypeSpec("Foundation.Date");
        Assert.True(database.TryGetTypeRecord(date, out var record));

        Assert.Null(CdeclParamMapper.PrimitiveCarriedPlatformValue(date, record));
    }

    [Fact]
    public async Task TransitionLayout_UrlErrorCode_IsIndirect()
    {
        var (_, database) = await CreateEnvironmentAsync();

        var result = new SwiftLayoutResolver([], database).Resolve(s_code);

        Assert.True(result.IsIndirect);
        Assert.Null(result.Layout);
    }

    [Theory]
    [InlineData("Foundation.URLError.Code", true)]
    [InlineData("Foundation.Date", true)]
    [InlineData("Swift.Int", false)]
    public async Task ResilientValueQuestion_AnswersOnlyForResilientPlatformValues(string name, bool expected)
    {
        var (_, database) = await CreateEnvironmentAsync();

        Assert.Equal(expected, WrapperValidation.IsValueCarriedResilientPlatformValue(new NamedTypeSpec(name), database));
    }

    [Fact]
    public async Task Floor_DirectParameter_IsRefusedAsUncallable()
    {
        var (module, database) = await CreateEnvironmentAsync();
        var env = new MethodEnvironment(Method("take", module, Arg(new NamedTypeSpec("Swift.Int"), ""), Arg(s_code, "code")), database);

        Assert.True(WrapperValidation.HasAddressOnlyPlatformValueDirectDispatch(env));
        Assert.True(WrapperValidation.IsAbiFloorTombstoned(env));
        var issue = WrapperValidation.GetNonBlittableCallConvSwiftIssue(env);
        Assert.NotNull(issue);
        Assert.Equal(WrapperValidation.UncallableAbiDiagnosticId, issue!.Value.DiagnosticId);
    }

    [Fact]
    public async Task Floor_DirectResult_IsRefusedAsUncallable()
    {
        var (module, database) = await CreateEnvironmentAsync();
        var env = new MethodEnvironment(Method("make", module, Arg(s_code, "")), database);

        Assert.True(WrapperValidation.IsAbiFloorTombstoned(env));
    }

    [Fact]
    public async Task Floor_CdeclWrapperRoute_IsCallable()
    {
        // The wrapper takes the value as a plain Int and rebuilds it inside Swift, so the managed
        // side never meets the Swift convention for it.
        var (module, database) = await CreateEnvironmentAsync();
        var method = Method("take", module, Arg(new NamedTypeSpec("Swift.Int"), ""), Arg(s_code, "code"));
        method.UsesCdeclMethodWrapper = true;

        Assert.False(WrapperValidation.HasAddressOnlyPlatformValueDirectDispatch(new MethodEnvironment(method, database)));
    }

    [Fact]
    public async Task Floor_InOutParameter_IsAlreadyAnAddress()
    {
        // An inout argument crosses as a pointer to the value on every route, which is what Swift wants.
        var (module, database) = await CreateEnvironmentAsync();
        var inout = Arg(s_code, "code");
        inout.IsInOut = true;
        var env = new MethodEnvironment(Method("take", module, Arg(new NamedTypeSpec("Swift.Int"), ""), inout), database);

        Assert.False(WrapperValidation.HasAddressOnlyPlatformValueDirectDispatch(env));
    }

    private static ArgumentDecl Arg(TypeSpec spec, string name) =>
        new()
        {
            SwiftTypeSpec = spec,
            Name = name,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };

    private static MethodDecl Method(string name, ModuleDecl module, params ArgumentDecl[] signature)
    {
        var parent = new ClassDecl
        {
            Name = "Host",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Host"),
            MangledName = "$s10TestModule4HostCN",
            IsFinal = true,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = module,
            ModuleDecl = module
        };
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(signature),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parent,
            ModuleDecl = module,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }
}
