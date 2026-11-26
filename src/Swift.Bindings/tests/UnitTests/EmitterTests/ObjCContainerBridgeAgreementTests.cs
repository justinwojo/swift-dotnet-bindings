// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

#nullable enable

namespace BindingsGeneration.Tests;

/// <summary>
/// A collection Swift bridges to Objective-C (any key or element, at any depth, bridges) crosses
/// as that Objective-C collection on the Swift side of the wrapper. The C# side must bridge the
/// same collection and convert every key and element, or the member is refused before emission:
/// otherwise it compiles and passes the native Swift collection, or an element C# cannot build,
/// where Swift reads an Objective-C object.
/// </summary>
public class ObjCContainerBridgeAgreementTests
{
    private static readonly string s_dbDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift");

    private static async Task<TypeDatabase> LoadDatabaseAsync()
    {
        var typeDatabase = new TypeDatabase();
        await typeDatabase.LoadModuleDatabaseFromFile(Path.Combine(s_dbDir, "SwiftDatabase.xml"));
        await typeDatabase.LoadModuleDatabaseFromFile(Path.Combine(s_dbDir, "FoundationDatabase.xml"));
        Register(typeDatabase, "Point", TypeRecordKind.Struct, TypeRecordFlags.Frozen, inlineSize: 16);
        Register(typeDatabase, "Choice", TypeRecordKind.Enum, TypeRecordFlags.Frozen, inlineSize: 1);
        Register(typeDatabase, "Node", TypeRecordKind.Class, TypeRecordFlags.None, inlineSize: null);
        Register(typeDatabase, "RootedNode", TypeRecordKind.Class, TypeRecordFlags.ObjCRooted, inlineSize: null);
        return typeDatabase;
    }

    private static void Register(TypeDatabase typeDatabase, string name, TypeRecordKind kind, TypeRecordFlags flags, int? inlineSize)
    {
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}");
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: swiftTypeName, record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", name),
                SwiftTypeName = swiftTypeName,
                MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
                Flags = flags,
                Kind = kind,
                InlineSize = inlineSize,
            })
        });
    }

    private static string Render(MarshalStatement statement) => statement switch
    {
        MarshalStatement.Line line => line.Code,
        MarshalStatement.Using u => $"using {u.Type} {u.Name} = {u.InitExpression};",
        MarshalStatement.Block block => $"{block.Header} {{ {string.Join(" ", block.Body.Select(Render))} }}",
        _ => statement.ToString() ?? string.Empty,
    };

    private static NamedTypeSpec Url => new("Foundation.URL");
    private static NamedTypeSpec Named(string name) => new(name);
    private static NamedTypeSpec Array(TypeSpec element) => new("Swift.Array", element);
    private static NamedTypeSpec Optional(TypeSpec wrapped) => new("Swift.Optional", wrapped);
    private static NamedTypeSpec Dictionary(TypeSpec key, TypeSpec value) => new("Swift.Dictionary", key, value);

    private static TypeSpec Shape(string shape) => shape switch
    {
        "[URL: Point]" => Dictionary(Url, Named("TestModule.Point")),
        "[URL: [Point]]" => Dictionary(Url, Array(Named("TestModule.Point"))),
        "[URL: Point]?" => Optional(Dictionary(Url, Named("TestModule.Point"))),
        "[Int: [URL: Point]]" => Dictionary(Named("Swift.Int"), Dictionary(Url, Named("TestModule.Point"))),
        "[URL: Choice]" => Dictionary(Url, Named("TestModule.Choice")),
        "[URL: (Int, Int)]" => Dictionary(Url, new TupleTypeSpec(new List<TypeSpec> { Named("Swift.Int"), Named("Swift.Int") })),
        "[URL: UUID]" => Dictionary(Url, Named("Foundation.UUID")),
        "[URL: Date]" => Dictionary(Url, Named("Foundation.Date")),
        "[URL: [Date]]" => Dictionary(Url, Array(Named("Foundation.Date"))),
        "[URL: Int]" => Dictionary(Url, Named("Swift.Int")),
        "[URL: String]" => Dictionary(Url, Named("Swift.String")),
        "[URL: Node]" => Dictionary(Url, Named("TestModule.Node")),
        "[URL: [Node]]" => Dictionary(Url, Array(Named("TestModule.Node"))),
        "[URL: Set<Node>]" => Dictionary(Url, new NamedTypeSpec("Swift.Set", Named("TestModule.Node"))),
        "[URL: Set<RootedNode>]" => Dictionary(Url, new NamedTypeSpec("Swift.Set", Named("TestModule.RootedNode"))),
        "[URL]" => Array(Url),
        "[Int: Point]" => Dictionary(Named("Swift.Int"), Named("TestModule.Point")),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    [Theory]
    [InlineData("[URL: Point]", "TestModule.Point")]
    [InlineData("[URL: [Point]]", "TestModule.Point")]
    [InlineData("[URL: Point]?", "TestModule.Point")]
    [InlineData("[Int: [URL: Point]]", "TestModule.Point")]
    [InlineData("[URL: Choice]", "TestModule.Choice")]
    [InlineData("[URL: (Int, Int)]", "nint")]
    [InlineData("[URL: UUID]", "System.Guid")]
    [InlineData("[URL: Set<Node>]", "TestModule.Node")]
    public async Task BridgedCollection_HoldingAValueSwiftBoxes_IsRefusedWithTheElementNamed(string shape, string element)
    {
        var typeDatabase = await LoadDatabaseAsync();

        foreach (var isParameter in new[] { true, false })
        {
            Assert.True(ObjCContainerBridgeAgreement.Disagrees(Shape(shape), typeDatabase, null, isParameter, out var detail), shape);
            Assert.Contains("as the Objective-C collection it bridges to", detail);
            Assert.Contains(element, detail);
        }
    }

    [Theory]
    [InlineData("[URL: Date]")]
    [InlineData("[URL: [Date]]")]
    [InlineData("[URL: Int]")]
    [InlineData("[URL: String]")]
    [InlineData("[URL: Node]")]
    [InlineData("[URL: [Node]]")]
    [InlineData("[URL: Set<RootedNode>]")]
    [InlineData("[URL]")]
    public async Task BridgedCollection_EveryElementOfWhichCSharpConverts_IsAccepted(string shape)
    {
        var typeDatabase = await LoadDatabaseAsync();

        foreach (var isParameter in new[] { true, false })
            Assert.False(ObjCContainerBridgeAgreement.Disagrees(Shape(shape), typeDatabase, null, isParameter, out var detail), $"{shape}: {detail}");
    }

    [Fact]
    public async Task NativeCollection_IsNotThisGatesToJudge()
    {
        var typeDatabase = await LoadDatabaseAsync();

        Assert.False(ObjCContainerBridgeAgreement.Disagrees(Shape("[Int: Point]"), typeDatabase, null, isParameter: true, out _));
    }

    private static ProtocolDecl Protocol() => new()
    {
        Name = "Indexer",
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Indexer"),
        MangledName = "$s10TestModule7IndexerP",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        AssociatedTypes = new List<AssociatedTypeDecl>(),
        InheritedProtocols = new List<NamedTypeSpec>(),
        ParentDecl = null,
        ModuleDecl = null,
    };

    private static ArgumentDecl Arg(string name, TypeSpec spec) => new()
    {
        SwiftTypeSpec = spec, Name = name, PrivateName = name, IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null,
    };

    /// <summary>
    /// Protocol requirements never pass through MemberValidationPipeline, so the protocol gate
    /// refuses the same shapes: a requirement whose conformers are refused must not stay on the
    /// interface, where its proxy would emit the cast that does not compile.
    /// </summary>
    [Theory]
    [InlineData("parameter")]
    [InlineData("result")]
    [InlineData("property")]
    [InlineData("subscript")]
    public async Task ProtocolRequirement_WithABridgedCollectionCSharpCannotConvert_IsRefused(string position)
    {
        var typeDatabase = await LoadDatabaseAsync();
        var shape = Shape("[URL: Point]");
        var gate = new MemberGateEvaluator(typeDatabase);
        var proto = Protocol();
        MethodDecl Method(TypeSpec result, params ArgumentDecl[] parameters) => new()
        {
            Name = "index", MangledName = "$s10TestModule5index", MethodType = MethodType.Instance, IsConstructor = false,
            CSSignature = parameters.Prepend(Arg(string.Empty, result)).ToList(), GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = proto, ModuleDecl = null, Throws = false, IsAsync = false, IsSynthesizedAccessor = false,
        };

        var result = position switch
        {
            "parameter" => gate.EvaluateMethod(Method(new TupleTypeSpec(new List<TypeSpec>()), Arg("d", shape)), null, proto),
            "result" => gate.EvaluateMethod(Method(shape), null, proto),
            "property" => gate.EvaluateProperty(new PropertyDecl
            {
                Name = "index", IsStatic = false, HasStorage = false, IsProtocolRequirement = true, SwiftTypeSpec = shape,
                Accessors = new List<AccessorDecl>(), ParentDecl = proto, ModuleDecl = null,
            }, null, proto),
            _ => gate.EvaluateSubscript(new SubscriptDecl
            {
                Name = "subscript", ReturnTypeSpec = shape, IndexParameters = new List<ArgumentDecl> { Arg("i", Named("Swift.Int")) },
                IsStatic = false, Accessors = new List<AccessorDecl>(), MangledName = "$sSubscript", ParentDecl = proto, ModuleDecl = null,
            }, null, proto),
        };

        Assert.True(result.IsSkipped, position);
        Assert.Equal(SkipReason.UnsupportedSignature, result.Reason);
        Assert.Contains("as the Objective-C collection it bridges to", result.Details);
    }

    /// <summary>
    /// A Date in a bridged collection is the NSDate Swift bridges it to: C# builds one from the
    /// instant's seconds since the reference date, and reads the same seconds back.
    /// </summary>
    [Fact]
    public async Task BridgedCollection_OfDates_ConvertsEachToAndFromNSDate()
    {
        var typeDatabase = await LoadDatabaseAsync();
        var factory = new TypeProjectionFactory();
        ProjectionContext Context(bool isParameter) => new() { TypeDatabase = typeDatabase, IsParameter = isParameter, EmissionContext = new ModuleEmissionContext(), CurrentModuleName = "TestModule" };

        var parameter = factory.Project(Shape("[URL: Date]"), Context(isParameter: true))!;
        var result = factory.Project(Shape("[URL: Date]"), Context(isParameter: false))!;
        Assert.True(parameter.UsesObjCContainerBridge);
        Assert.True(result.UsesObjCContainerBridge);

        var write = string.Join("\n", parameter.GetParameterPlan("dates").SetupStatements.Select(Render));
        var readPlan = result.GetReturnPlan("result", ReturnStrategy.Direct);
        var read = string.Join("\n", readPlan.SetupStatements.Select(Render)) + "\n" + readPlan.PInvokeExpression;
        Assert.Contains("Foundation.NSDate.FromTimeIntervalSinceReferenceDate(", write);
        Assert.Contains(".SecondsSinceReferenceDate", read);
        Assert.Contains("new System.DateTimeOffset(2001, 1, 1, 0, 0, 0, System.TimeSpan.Zero)", read);
    }
}
