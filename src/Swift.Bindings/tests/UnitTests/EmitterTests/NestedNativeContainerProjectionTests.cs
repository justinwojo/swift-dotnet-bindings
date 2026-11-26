// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

#nullable enable

namespace BindingsGeneration.Tests;

/// <summary>
/// A container nested in one that crosses as an Objective-C collection is itself an Objective-C
/// collection inside it, whether or not it would bridge on its own: Swift bridges the inner
/// <c>[Int]</c> of a <c>[URL: [Int]]</c> to an NSArray of NSNumbers, the inner <c>[String: Int]</c>
/// of a <c>[URL: [String: Int]]</c> to an NSDictionary, and each <c>[URL]</c> of a
/// <c>Set&lt;[URL]&gt;</c> to an NSArray. Each nested container is read and written as that
/// collection, element by element, never cast between an NSObject and a managed collection
/// interface (a cast that compiles and throws InvalidCastException at run time).
/// </summary>
public class NestedNativeContainerProjectionTests
{
    private static readonly string s_dbDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift");
    private readonly TypeProjectionFactory _factory = new();

    private static async Task<TypeDatabase> LoadDatabaseAsync()
    {
        var typeDatabase = new TypeDatabase();
        await typeDatabase.LoadModuleDatabaseFromFile(Path.Combine(s_dbDir, "SwiftDatabase.xml"));
        await typeDatabase.LoadModuleDatabaseFromFile(Path.Combine(s_dbDir, "FoundationDatabase.xml"));
        return typeDatabase;
    }

    private static ProjectionContext Context(ITypeDatabase typeDatabase, bool isParameter)
        => new() { TypeDatabase = typeDatabase, IsParameter = isParameter, EmissionContext = new ModuleEmissionContext(), CurrentModuleName = "TestModule" };

    private static NamedTypeSpec Url => new("Foundation.URL");
    private static NamedTypeSpec Int => new("Swift.Int");
    private static NamedTypeSpec Str => new("Swift.String");
    private static NamedTypeSpec Array(TypeSpec element) => new("Swift.Array", element);
    private static NamedTypeSpec Set(TypeSpec element) => new("Swift.Set", element);
    private static NamedTypeSpec Dictionary(TypeSpec key, TypeSpec value) => new("Swift.Dictionary", key, value);

    private static string Render(MarshalStatement statement) => statement switch
    {
        MarshalStatement.Line line => line.Code,
        MarshalStatement.Using u => $"using {u.Type} {u.Name} = {u.InitExpression};",
        MarshalStatement.Block block => $"{block.Header} {{ {string.Join(" ", block.Body.Select(Render))} }}",
        _ => statement.ToString() ?? string.Empty,
    };

    public static TheoryData<string> NestedShapes => new() { "[URL: [Int]]", "[URL: [String: Int]]", "Set<[URL]>" };

    private static TypeSpec Shape(string shape) => shape switch
    {
        "[URL: [Int]]" => Dictionary(Url, Array(Int)),
        "[URL: [String: Int]]" => Dictionary(Url, Dictionary(Str, Int)),
        _ => Set(Array(Url)),
    };

    private static ITypeProjection Inner(ITypeProjection outer) => outer switch
    {
        DictionaryProjection dictionary => dictionary.ValueProjection,
        SetProjection set => set.ElementProjection,
        _ => throw new InvalidOperationException(outer.GetType().Name),
    };

    private (ITypeProjection Parameter, ITypeProjection Result) Project(TypeDatabase typeDatabase, string shape)
        => (_factory.Project(Shape(shape), Context(typeDatabase, isParameter: true))!,
            _factory.Project(Shape(shape), Context(typeDatabase, isParameter: false))!);

    [Theory]
    [MemberData(nameof(NestedShapes))]
    public async Task NestedContainer_IsReadAsTheCollectionSwiftStores(string shape)
    {
        var (_, result) = Project(await LoadDatabaseAsync(), shape);
        Assert.True(result.UsesObjCContainerBridge);
        var inner = Inner(result);

        var reads = new[]
        {
            string.Join("\n", result.GetReturnPlan("result", ReturnStrategy.Direct).SetupStatements.Select(Render)),
            result.GetReturnContainerConversion("d")!,
            result.GetReturnElementConversion("e")!,
        };
        foreach (var read in reads)
        {
            // Neither a cast of the NSObject to the managed collection, nor a bridge call that
            // treats the nested collection as a leaf.
            Assert.DoesNotContain($"({inner.PublicType})", read);
            Assert.DoesNotContain($"GetNSObject<{inner.PublicType}>", read);
            Assert.Contains(inner is DictionaryProjection ? "Foundation.NSDictionary" : "Foundation.NSArray", read);
        }
        if (shape == "[URL: [Int]]")
            Assert.Contains("Foundation.NSNumber", reads[1]);
    }

    [Theory]
    [MemberData(nameof(NestedShapes))]
    public async Task NestedContainer_IsWrittenAsTheCollectionSwiftReads(string shape)
    {
        var (parameter, _) = Project(await LoadDatabaseAsync(), shape);
        Assert.True(parameter.UsesObjCContainerBridge);
        var owner = Assert.IsAssignableFrom<IObjCContainerBridgeOwnerSource>(parameter).BuildObjCBridgeOwner("groups", sourceIsNullable: false);
        var writes = new[]
        {
            string.Join("\n", owner.Setup.Select(Render)),
            parameter.GetParameterElementConversion("groups")!,
            parameter switch
            {
                DictionaryProjection dictionary => dictionary.GetReverseReceiverObjCBridgeConversion("groups"),
                SetProjection set => set.GetReverseReceiverObjCBridgeConversion("groups"),
                _ => throw new InvalidOperationException(),
            },
        };
        foreach (var write in writes)
        {
            // The inner container is built as its own collection, never cast to NSObject whole.
            Assert.DoesNotContain("(Foundation.NSObject)kvp.Value)", write);
            Assert.DoesNotContain("(Foundation.NSObject)e)", write);
            switch (shape)
            {
                case "[URL: [Int]]":
                    Assert.Contains("(Foundation.NSObject)Foundation.NSArray.FromNSObjects(", write);
                    Assert.Contains("Foundation.NSNumber", write);
                    break;
                case "[URL: [String: Int]]":
                    // Each key and its value come from the same pass over the inner dictionary.
                    Assert.Contains("(Foundation.NSObject)((global::System.Func<Foundation.NSObject[][], Foundation.NSDictionary>)(static __pairs => Foundation.NSDictionary.FromObjectsAndKeys(", write);
                    Assert.Contains("=> new Foundation.NSObject[] { (Foundation.NSObject)new Foundation.NSString(", write);
                    break;
                default:
                    Assert.Contains("Foundation.NSArray.FromNSObjects(", write);
                    break;
            }
        }
    }

    [Fact]
    public async Task NestedOptionalElement_ReadsEitherFormOfNilAsNull()
    {
        // Swift stores the nil of an `[Int?]` nested in a bridged dictionary as NSNull, which
        // NSArray.ArrayFromHandle hands back as null: both read as a nil element, never as an
        // NSNumber to unbox.
        var result = _factory.Project(Dictionary(Url, Array(new NamedTypeSpec("Swift.Optional", Int))), Context(await LoadDatabaseAsync(), isParameter: false))!;
        Assert.True(result.UsesObjCContainerBridge);
        var read = string.Join("\n", result.GetReturnPlan("result", ReturnStrategy.Direct).SetupStatements.Select(Render));
        Assert.Contains("Foundation.NSArray.ArrayFromHandle<Foundation.NSObject>(", read);
        Assert.Contains(" ?? Foundation.NSNull.Null) is Foundation.NSNull ? (nint?)null : ", read);
    }

    [Fact]
    public void LambdaName_IsDistinctPerSourceAndAValidIdentifier()
    {
        // A nested conversion names its lambda parameter after its source, so a conversion inside
        // another's lambda never redeclares the outer one's name (CS0136).
        Assert.Equal("_ekvpValue", DictionaryProjection.LambdaName("e", "kvp.Value"));
        Assert.NotEqual(DictionaryProjection.LambdaName("e", "kvp.Value"), DictionaryProjection.LambdaName("e", "_ekvpValue"));
        Assert.Equal("_kvpe", DictionaryProjection.LambdaName("kvp", "e!"));
    }
}
