// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Operators whose operands are not all the parent type. Two routes cannot carry them, and each
/// used to fail the whole module's compile in a way no verify-recover round could pin on the
/// operator:
/// <list type="bullet">
/// <item>The direct route (a non-frozen struct or a class parent) declares an Optional wider than
/// one word as a by-value carrier, but the operator body neither converts the operand into it nor
/// the result out of it (CS0103, CS0029).</item>
/// <item>The frozen struct's @_cdecl operator wrapper reads every operand as the parent type and names
/// a generic result without its arguments. A heterogeneous operand fails the wrapper's Swift compile,
/// or, beside a homogeneous overload, compiles into a call of that overload on the wrong bytes.</item>
/// </list>
/// Both are refused per operator before anything is written; the homogeneous controls still emit.
/// </summary>
public class OperatorOperandShapeTests
{
    private static readonly string s_dbDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift");

    private static async Task<TypeDatabase> CreateDatabaseAsync()
    {
        var database = new TypeDatabase();
        await database.LoadModuleDatabaseFromFile(Path.Combine(s_dbDir, "SwiftDatabase.xml"));
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(SwiftTypeName.FromModuleQualifiedName("TestModule.Point"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Point"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
            MetadataAccessor = "$s10TestModule5PointVMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct,
        });
        module.RegisterType(SwiftTypeName.FromModuleQualifiedName("TestModule.Meter"), new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Meter"),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Meter"),
            MetadataAccessor = "$s10TestModule5MeterVMa",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Struct,
        });
        database.AddModuleDatabase(module);
        // XCFramework mode is what turns the @_cdecl operator wrapper on at all.
        database.AsyncLibraryName = "TestModuleSwiftBindings";
        return database;
    }

    private static readonly ModuleDecl s_module = new()
    {
        Name = "TestModule",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Dependencies = new List<string>(),
        Protocols = new List<ProtocolDecl>(),
        ParentDecl = null,
        ModuleDecl = null,
    };

    private static StructDecl Struct(string name, bool frozen) => new()
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
        MangledName = $"$s10TestModule{name.Length}{name}VN",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        GenericParameters = new List<GenericArgumentDecl>(),
        Conformances = new List<TypeConformance>(),
        ParentDecl = s_module,
        ModuleDecl = s_module,
        IsFrozen = frozen,
        MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
    };

    private static NamedTypeSpec Optional(string payload) => new("Swift.Optional", new NamedTypeSpec(payload));

    private static OperatorDecl Binary(string symbol, StructDecl parent, TypeSpec result, TypeSpec lhs, TypeSpec rhs)
    {
        ArgumentDecl Arg(TypeSpec spec, string name) => new()
        {
            SwiftTypeSpec = spec, Name = name, PrivateName = name, IsInOut = false, IsGeneric = false,
            ParentDecl = parent, ModuleDecl = s_module,
        };
        var method = new MethodDecl
        {
            Name = symbol,
            MangledName = $"$s10TestModule{parent.Name}_operator_{symbol.GetHashCode():x}",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl> { Arg(result, string.Empty), Arg(lhs, "lhs"), Arg(rhs, "rhs") },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parent,
            ModuleDecl = s_module,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
        };
        return new OperatorDecl
        {
            Name = symbol, OperatorSymbol = symbol, Kind = OperatorKind.Binary, IsPrefix = false,
            UnderlyingMethod = method, ParentDecl = parent, ModuleDecl = s_module,
        };
    }

    private static (bool Emitted, string CSharp, string Swift) Emit(OperatorDecl op, TypeDatabase database)
    {
        var cs = new StringWriter();
        var swift = new StringWriter();
        var emitted = new OperatorHandler(new NullLogger<OperatorHandler>()).EmitOperator(
            new CSharpWriter(cs), op, database, pinvokeHelperContext: null,
            swiftWriter: new SwiftWriter(swift), emissionContext: new ModuleEmissionContext());
        return (emitted, cs.ToString(), swift.ToString());
    }

    [Fact]
    public async Task FrozenWrapper_HomogeneousOperator_StillEmits()
    {
        var database = await CreateDatabaseAsync();
        var point = Struct("Point", frozen: true);
        var spec = new NamedTypeSpec("TestModule.Point");

        var (emitted, cs, swift) = Emit(Binary("+", point, spec, spec, spec), database);

        Assert.True(emitted);
        Assert.Contains("operator +", cs);
        Assert.Contains("@_cdecl", swift);
    }

    [Theory]
    [InlineData("Swift.Int")]
    [InlineData("Swift.Double")]
    public async Task FrozenWrapper_OperandOfAnotherType_IsRefusedBeforeTheWrapper(string operand)
    {
        var database = await CreateDatabaseAsync();
        var point = Struct("Point", frozen: true);
        var spec = new NamedTypeSpec("TestModule.Point");

        var (emitted, cs, swift) = Emit(Binary("+", point, spec, spec, new NamedTypeSpec(operand)), database);

        Assert.False(emitted);
        Assert.DoesNotContain("operator +", cs);
        Assert.DoesNotContain("@_cdecl", swift);
    }

    [Fact]
    public async Task FrozenWrapper_WideOptionalOperand_IsRefused()
    {
        var database = await CreateDatabaseAsync();
        var point = Struct("Point", frozen: true);
        var spec = new NamedTypeSpec("TestModule.Point");

        var (emitted, cs, swift) = Emit(Binary("-", point, new NamedTypeSpec("Swift.Int"), spec, Optional("Swift.Int")), database);

        Assert.False(emitted);
        Assert.DoesNotContain("operator -", cs);
        Assert.DoesNotContain("@_cdecl", swift);
    }

    [Fact]
    public async Task FrozenWrapper_GenericResult_IsRefused()
    {
        var database = await CreateDatabaseAsync();
        var point = Struct("Point", frozen: true);
        var spec = new NamedTypeSpec("TestModule.Point");

        var (emitted, _, swift) = Emit(Binary("/", point, Optional("Swift.Int"), spec, spec), database);

        Assert.False(emitted);
        Assert.DoesNotContain("@_cdecl", swift);
    }

    [Fact]
    public async Task DirectRoute_PlainOperand_StillEmits()
    {
        var database = await CreateDatabaseAsync();
        var meter = Struct("Meter", frozen: false);
        var spec = new NamedTypeSpec("TestModule.Meter");

        var (emitted, cs, _) = Emit(Binary("*", meter, new NamedTypeSpec("Swift.Int"), spec, new NamedTypeSpec("Swift.Int")), database);

        Assert.True(emitted);
        Assert.Contains("operator *", cs);
    }

    /// <summary>
    /// A borrowed existential operand is read out of its C# owner into the call's argument list:
    /// a boxed <c>any Error</c> passes its box, which only the wrapper's count keeps alive. The
    /// operator's call site pins the operand until the call returns, as an ordinary body does;
    /// without the pin a collection during the call could release the box Swift is reading.
    /// </summary>
    [Fact]
    public async Task DirectRoute_BorrowedExistentialOperand_IsPinnedAcrossTheCall()
    {
        var database = await CreateDatabaseAsync();
        var meter = Struct("Meter", frozen: false);
        var spec = new NamedTypeSpec("TestModule.Meter");

        var (emitted, cs, _) = Emit(Binary("+", meter, new NamedTypeSpec("Swift.Int"), spec,
            new NamedTypeSpec("Swift.Error") { IsAny = true }), database);

        Assert.True(emitted);
        var call = cs.IndexOf("PInvoke_", cs.IndexOf("operator +", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(call > 0, cs);
        var pin = cs.IndexOf("GC.KeepAlive(rhs)", call, StringComparison.Ordinal);
        Assert.True(pin > call, cs);
    }

    [Theory]
    [InlineData("Swift.Int")]
    [InlineData("Swift.Double")]
    [InlineData("Swift.String")]
    public async Task DirectRoute_WideOptionalOperand_IsRefused(string payload)
    {
        var database = await CreateDatabaseAsync();
        var meter = Struct("Meter", frozen: false);
        var spec = new NamedTypeSpec("TestModule.Meter");

        var (emitted, cs, _) = Emit(Binary("+", meter, new NamedTypeSpec("Swift.Int"), spec, Optional(payload)), database);

        Assert.False(emitted);
        Assert.DoesNotContain("operator +", cs);
        Assert.DoesNotContain("SwiftOptionalCarrier", cs);
    }

    [Theory]
    [InlineData("Swift.Int")]
    [InlineData("Swift.String")]
    public async Task DirectRoute_WideOptionalResult_IsRefused(string payload)
    {
        var database = await CreateDatabaseAsync();
        var meter = Struct("Meter", frozen: false);
        var spec = new NamedTypeSpec("TestModule.Meter");

        var (emitted, cs, _) = Emit(Binary("/", meter, Optional(payload), spec, new NamedTypeSpec("Swift.Int")), database);

        Assert.False(emitted);
        Assert.DoesNotContain("SwiftOptionalCarrier", cs);
    }

    private static string EmitType(StructDecl parent, TypeDatabase database, ITypeHandler handler)
    {
        var cs = new StringWriter();
        var env = handler.Marshal(parent, database);
        handler.Emit(new CSharpWriter(cs), new SwiftWriter(new StringWriter()), env, new Conductor(new NullLoggerFactory()), TypeHandlerContext.Empty);
        return cs.ToString();
    }

    /// <summary>
    /// C# requires the partner of a comparison operator, which the type handler synthesizes from an
    /// emitted one. A refused overload of the same symbol, declared first, must not be the one it
    /// synthesizes from: the partner would take operands no emitted operator takes (CS0216), and the
    /// whole module would fail where only the refused overload should go.
    /// </summary>
    [Fact]
    public async Task RefusedOverload_DeclaredFirst_DoesNotSeedTheSynthesizedPartner()
    {
        var database = await CreateDatabaseAsync();
        var meter = Struct("Meter", frozen: false);
        var spec = new NamedTypeSpec("TestModule.Meter");
        var boolSpec = new NamedTypeSpec("Swift.Bool");
        meter.Operators.Add(Binary("<", meter, boolSpec, spec, Optional("Swift.Int")));
        meter.Operators.Add(Binary("<", meter, boolSpec, spec, spec));

        var cs = EmitType(meter, database, new NonFrozenStructHandler(new NullLogger<NonFrozenStructHandler>()));

        Assert.Contains("operator <(", cs);
        Assert.Contains("operator >(", cs);
        var partner = cs.Split('\n').Single(l => l.Contains("operator >(", StringComparison.Ordinal));
        Assert.DoesNotContain("?", partner);
        Assert.DoesNotContain("SwiftOptional", partner);
    }
}
