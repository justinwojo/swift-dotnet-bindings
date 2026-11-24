// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

#nullable enable

namespace BindingsGeneration.Tests;

/// <summary>
/// A collection key, set element, array element or dictionary value that crosses as one
/// Objective-C object pointer (an ObjC class, a string-backed key type such as
/// <c>UIApplication.LaunchOptionsKey</c>) must be carried in the Swift collection as a value of its
/// own Swift type, never as the bare pointer: a collection over <c>IntPtr</c> is one over
/// <c>Swift.Int</c>, its elements unretained and its keys hashed by address, while Swift reads it as
/// the element type, releases what it replaces and hashes by value.
/// </summary>
public class ObjectKeyCollectionProjectionTests
{
    private const string LaunchOptionsCarrier =
        "global::Swift.Runtime.SwiftObjectKey<__SwiftObjectKeys.UIKit_UIApplication_LaunchOptionsKey>";

    private static readonly string s_dbDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift");
    private readonly TypeProjectionFactory _factory = new();

    private static async Task<TypeDatabase> LoadAppleDatabaseAsync()
    {
        var typeDatabase = new TypeDatabase();
        await typeDatabase.LoadModuleDatabaseFromFile(Path.Combine(s_dbDir, "SwiftDatabase.xml"));
        await typeDatabase.LoadModuleDatabaseFromFile(Path.Combine(s_dbDir, "FoundationDatabase.xml"));
        await typeDatabase.LoadModuleDatabaseFromFile(Path.Combine(s_dbDir, "UIKitDatabase.xml"));
        return typeDatabase;
    }

    private static ProjectionContext Context(ITypeDatabase typeDatabase, ModuleEmissionContext? emission, bool isParameter = true)
        => new() { TypeDatabase = typeDatabase, IsParameter = isParameter, EmissionContext = emission, CurrentModuleName = "TestModule" };

    private static NamedTypeSpec Dictionary(TypeSpec key, TypeSpec value) => new("Swift.Dictionary", key, value);

    [Theory]
    [InlineData("UIKit.UIApplication.LaunchOptionsKey", "UIKit_UIApplication_LaunchOptionsKey")]
    [InlineData("UIKit.UIApplication.OpenURLOptionsKey", "UIKit_UIApplication_OpenURLOptionsKey")]
    [InlineData("Foundation.NSAttributedString.Key", "Foundation_NSAttributedString_Key")]
    public async Task StringBackedKey_DictionarySlotsCarryTheKeyTypeIdentity(string keyType, string identityName)
    {
        var emission = new ModuleEmissionContext();
        var projection = Assert.IsType<DictionaryProjection>(_factory.Project(
            Dictionary(new NamedTypeSpec(keyType), new NamedTypeSpec("Swift.Int")), Context(await LoadAppleDatabaseAsync(), emission)));

        var carrier = $"global::Swift.Runtime.SwiftObjectKey<__SwiftObjectKeys.{identityName}>";
        // The public surface is unchanged: the key is still the NSString a consumer holds.
        Assert.Contains("Foundation.NSString", projection.PublicType);
        Assert.StartsWith($"SwiftDictionary<{carrier},", projection.SwiftContainerGenericType);
        Assert.StartsWith($"SwiftDictionary<{carrier},", projection.MarshalFromSwiftType);
        Assert.DoesNotContain("SwiftDictionary<IntPtr", projection.SwiftContainerGenericType);

        var identity = Assert.Single(emission.ObjectKeyIdentities);
        Assert.Equal(keyType, identity.SwiftTypeName);
        Assert.Equal(identityName, identity.IdentityClassName);
    }

    [Fact]
    public void ObjCClassSetElement_CarriesTheClassIdentity()
    {
        // UIImage reaches the set through the ObjC-prefix element fallback, not a record.
        var emission = new ModuleEmissionContext();
        var projection = Assert.IsType<SetProjection>(_factory.Project(
            new NamedTypeSpec("Swift.Set", new NamedTypeSpec("UIKit.UIImage")), Context(new TypeDatabase(), emission)));

        Assert.Equal("IReadOnlySet<UIKit.UIImage>", projection.PublicType);
        Assert.Equal("SwiftSet<global::Swift.Runtime.SwiftObjectKey<__SwiftObjectKeys.UIKit_UIImage>>", projection.SwiftContainerGenericType);
        Assert.Equal("UIKit.UIImage", Assert.Single(emission.ObjectKeyIdentities).SwiftTypeName);
    }

    [Fact]
    public void ObjCClassValueAndArrayElement_CarryTheClassIdentityUnhashed()
    {
        // A dictionary value or an array element is never hashed, but a collection over the bare
        // pointer is one over Swift.Int, holding its elements unretained: it carries the identity
        // too, recorded as an unhashed element (metadata only, no Hashable conformance).
        var emission = new ModuleEmissionContext();
        var dictionary = Assert.IsType<DictionaryProjection>(_factory.Project(
            Dictionary(new NamedTypeSpec("Swift.String"), new NamedTypeSpec("UIKit.UIImage")), Context(new TypeDatabase(), emission)));
        var array = Assert.IsType<ArrayProjection>(_factory.Project(
            new NamedTypeSpec("Swift.Array", new NamedTypeSpec("UIKit.UIImage")), Context(new TypeDatabase(), emission)));

        const string carrier = "global::Swift.Runtime.SwiftObjectKey<__SwiftObjectKeys.UIKit_UIImage>";
        Assert.Equal("IEnumerable<UIKit.UIImage>", array.PublicType);
        Assert.Equal($"SwiftArray<{carrier}>", array.SwiftContainerGenericType);
        Assert.Equal($"SwiftArray<{carrier}>", array.ContainerTypeName);
        Assert.EndsWith($", {carrier}>", dictionary.SwiftContainerGenericType);
        Assert.EndsWith($", {carrier}>", dictionary.ContainerTypeName);

        var identity = Assert.Single(emission.ObjectKeyIdentities);
        Assert.Equal("UIKit.UIImage", identity.SwiftTypeName);
        Assert.False(emission.IsHashedObjectKey(identity));
    }

    [Fact]
    public void ObjCClassArrayElement_ParameterBuildsCarriersAndReturnRetainsEachRead()
    {
        // Into Swift: each element borrows into its carrier, which marshals as a +1 copy through the
        // element type's value witness. Out of Swift: each element read is borrowed from the array,
        // so the wrapper takes its own reference.
        const string carrier = "global::Swift.Runtime.SwiftObjectKey<__SwiftObjectKeys.UIKit_UIImage>";
        var parameter = Assert.IsType<ArrayProjection>(_factory.Project(
            new NamedTypeSpec("Swift.Array", new NamedTypeSpec("UIKit.UIImage")), Context(new TypeDatabase(), new ModuleEmissionContext())));
        var setup = string.Join("\n", parameter.GetParameterPlan("images").SetupStatements.Select(Render));
        Assert.Contains($"SwiftArray<{carrier}>.FromEnumerable(", setup);
        Assert.Contains($"new {carrier}(", setup);

        var returned = Assert.IsType<ArrayProjection>(_factory.Project(
            new NamedTypeSpec("Swift.Array", new NamedTypeSpec("UIKit.UIImage")),
            Context(new TypeDatabase(), new ModuleEmissionContext(), isParameter: false)));
        var plan = returned.GetReturnPlan("result", ReturnStrategy.IndirectResult);
        Assert.Contains($"SwiftArray<{carrier}>", plan.PInvokeExpression);
        Assert.Contains("GetNSObject<UIKit.UIImage>(e.Handle)", plan.PInvokeExpression);
    }

    public static TheoryData<TypeSpec> ObjectElementCollections => new()
    {
        new NamedTypeSpec("Swift.Array", new NamedTypeSpec("UIKit.UIImage")),
        new NamedTypeSpec("Swift.Set", new NamedTypeSpec("UIKit.UIImage")),
        Dictionary(new NamedTypeSpec("Swift.String"), new NamedTypeSpec("UIKit.UIImage")),
    };

    [Theory]
    [MemberData(nameof(ObjectElementCollections))]
    public void ObjCElementCollection_KeepsEachSourceElementAliveUntilCopied(TypeSpec collection)
    {
        // A converted element is the object's bare pointer: a lazy source that creates each object
        // would leave it collectable before Swift copies it, so every build site converts through
        // the runtime's borrowing sequence, which keeps each element reachable until the builder has
        // copied it and still reads the source one element at a time.
        var projection = Assert.IsAssignableFrom<ITypeProjection>(
            _factory.Project(collection, Context(new TypeDatabase(), new ModuleEmissionContext())));
        const string pinned = "global::Swift.Runtime.SwiftCollectionSource.Borrowing(images, ";

        var setup = string.Join("\n", projection.GetParameterPlan("images").SetupStatements.Select(Render));
        Assert.Contains(pinned, setup);

        var setter = projection switch
        {
            ArrayProjection array => AccessorSetterConversionVisitor.ArraySetterConversion(array, "images"),
            SetProjection set => AccessorSetterConversionVisitor.SetSetterConversion(set, "images"),
            DictionaryProjection dictionary => AccessorSetterConversionVisitor.DictSetterConversion(dictionary, "images"),
            _ => throw new InvalidOperationException(projection.GetType().Name),
        };
        Assert.NotNull(setter.Item1);
        Assert.Contains(pinned, setter.Item1);
        Assert.DoesNotContain("ToArray(images)", setup + setter.Item1);
    }

    [Fact]
    public void ValueElementCollection_ConvertsThroughAPlainSelect()
    {
        // Control: an element converted to a value holds what it needs, so a plain Select converts it.
        var projection = Assert.IsType<ArrayProjection>(_factory.Project(
            new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.String")), Context(new TypeDatabase(), new ModuleEmissionContext())));
        var (setter, _) = AccessorSetterConversionVisitor.ArraySetterConversion(projection, "names");
        Assert.NotNull(setter);
        Assert.Contains("global::System.Linq.Enumerable.Select(names, ", setter);
        Assert.DoesNotContain("SwiftCollectionSource", setter);
    }

    [Fact]
    public void NestedCollectionSetter_DisposesEachInnerCollectionOnceCopied()
    {
        // A [[T]] setter is one expression: each inner collection it builds is copied into the outer
        // one and would otherwise wait for its finalizer, holding its element references until then.
        var projection = Assert.IsType<ArrayProjection>(_factory.Project(
            new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Array", new NamedTypeSpec("UIKit.UIImage"))),
            Context(new TypeDatabase(), new ModuleEmissionContext())));
        var (setter, _) = AccessorSetterConversionVisitor.ArraySetterConversion(projection, "rows");

        Assert.NotNull(setter);
        Assert.Contains("global::Swift.Runtime.SwiftCollectionSource.Consuming(rows, ", setter);
        Assert.Contains("global::Swift.Runtime.SwiftCollectionSource.Borrowing(e, ", setter);
    }

    private static string Render(MarshalStatement statement) => statement switch
    {
        MarshalStatement.Line line => line.Code,
        MarshalStatement.Using u => $"using {u.Type} {u.Name} = {u.InitExpression};",
        MarshalStatement.Block block => $"{block.Header} {{ {string.Join(" ", block.Body.Select(Render))} }}",
        _ => statement.ToString() ?? string.Empty,
    };

    [Fact]
    public void ObjectUsedAsKeyAndValue_IsRegisteredHashed()
    {
        // One identity per type: a type some collection hashes needs its Hashable conformance, which
        // the Set<Key> registration provides together with the metadata a value slot needs.
        var emission = new ModuleEmissionContext();
        _factory.Project(Dictionary(new NamedTypeSpec("Swift.String"), new NamedTypeSpec("UIKit.UIImage")), Context(new TypeDatabase(), emission));
        var identity = Assert.Single(emission.ObjectKeyIdentities);
        Assert.False(emission.IsHashedObjectKey(identity));

        _factory.Project(new NamedTypeSpec("Swift.Set", new NamedTypeSpec("UIKit.UIImage")), Context(new TypeDatabase(), emission));
        Assert.Single(emission.ObjectKeyIdentities);
        Assert.True(emission.IsHashedObjectKey(identity));

        // A later value use does not demote it.
        _factory.Project(new NamedTypeSpec("Swift.Array", new NamedTypeSpec("UIKit.UIImage")), Context(new TypeDatabase(), emission));
        Assert.True(emission.IsHashedObjectKey(identity));
    }

    [Fact]
    public void SwiftClassArrayElementAndDictionaryValue_CarryTheWrapperAndKeepTheirKeysBare()
    {
        // A Swift class element is carried as its wrapper, copied in through the class's value
        // witness, not as a handle in an array of Swift.Int. Hashed positions are untouched.
        var element = new ClassProjection("Lib.Tracked").AsCollectionValue();
        var array = new ArrayProjection(element, isParameter: true);
        Assert.Equal("SwiftArray<Lib.Tracked>", array.SwiftContainerGenericType);
        var setup = string.Join("\n", array.GetParameterPlan("items").SetupStatements.Select(Render));
        Assert.Contains("SwiftArray<Lib.Tracked>.FromEnumerable(items)", setup);
        Assert.DoesNotContain("GetLiveHandle", setup);

        Assert.Equal("IntPtr", new ClassProjection("Lib.Tracked").SwiftContainerGenericType);
        Assert.Equal("Lib.Rooted", new ObjCRootedClassProjection("Lib.Rooted").AsCollectionValue().SwiftContainerGenericType);
        Assert.Equal("IntPtr", new ObjCRootedClassProjection("Lib.Rooted").SwiftContainerGenericType);

        // The factory applies it to an array element and a dictionary value, never to a key.
        var entity = new NamedTypeSpec("RealityFoundation.Entity");
        var classArray = Assert.IsType<ArrayProjection>(_factory.Project(new NamedTypeSpec("Swift.Array", entity), Context(new TypeDatabase(), new ModuleEmissionContext())));
        Assert.IsType<ClassProjection>(classArray.ElementProjection);
        Assert.DoesNotContain("IntPtr", classArray.SwiftContainerGenericType);
        var classDictionary = Assert.IsType<DictionaryProjection>(_factory.Project(Dictionary(new NamedTypeSpec("Swift.String"), entity), Context(new TypeDatabase(), new ModuleEmissionContext())));
        Assert.DoesNotContain("IntPtr", classDictionary.SwiftContainerGenericType);
        var classSet = Assert.IsType<SetProjection>(_factory.Project(new NamedTypeSpec("Swift.Set", entity), Context(new TypeDatabase(), new ModuleEmissionContext())));
        Assert.Equal("SwiftSet<IntPtr>", classSet.SwiftContainerGenericType);
    }

    [Fact]
    public async Task KeyConversions_BorrowTheObjectIntoTheCarrierAndRetainTheKeyReadBack()
    {
        var projection = Assert.IsType<DictionaryProjection>(_factory.Project(
            Dictionary(new NamedTypeSpec("UIKit.UIApplication.LaunchOptionsKey"), new NamedTypeSpec("Swift.Int")),
            Context(await LoadAppleDatabaseAsync(), new ModuleEmissionContext())));
        var key = projection.KeyProjection;

        // Into Swift: the carrier borrows the live object; marshalling it copies (+1) through the
        // key type's value witness.
        var into = key.GetParameterElementConversion("k");
        Assert.Equal($"new {LaunchOptionsCarrier}({LiveHandleExpressions.ObjCSelf("k")})", into);

        // Out of Swift: a key read from the collection is borrowed from it, so the wrapper takes its
        // own reference, on the owned and the borrowed read alike.
        foreach (var read in new[] { key.GetReturnElementConversion("k"), key.GetOwnedReturnElementConversion("k") })
            Assert.Equal("ObjCRuntime.Runtime.GetNSObject<Foundation.NSString>(k.Handle)!", read);

        // A returned dictionary's lookups go back through the carrier, so a C# lookup hashes the
        // way Swift does.
        var returned = Assert.IsType<DictionaryProjection>(_factory.Project(
            Dictionary(new NamedTypeSpec("UIKit.UIApplication.LaunchOptionsKey"), new NamedTypeSpec("Swift.Int")),
            Context(await LoadAppleDatabaseAsync(), new ModuleEmissionContext(), isParameter: false)));
        var plan = returned.GetReturnPlan("result", ReturnStrategy.IndirectResult);
        Assert.Contains($"SwiftDictionary<{LaunchOptionsCarrier}, nint>", plan.PInvokeExpression);
        Assert.Contains($"k => new {LaunchOptionsCarrier}(", plan.PInvokeExpression);
    }

    [Fact]
    public async Task NestedAndOptionalContainers_CarryTheIdentityOnTheInnerKey()
    {
        var typeDatabase = await LoadAppleDatabaseAsync();
        var emission = new ModuleEmissionContext();
        var inner = Dictionary(new NamedTypeSpec("Foundation.NSAttributedString.Key"), new NamedTypeSpec("Swift.Int"));
        const string carrier = "global::Swift.Runtime.SwiftObjectKey<__SwiftObjectKeys.Foundation_NSAttributedString_Key>";

        var nested = _factory.Project(Dictionary(new NamedTypeSpec("Swift.String"), inner), Context(typeDatabase, emission));
        var array = _factory.Project(new NamedTypeSpec("Swift.Array", inner), Context(typeDatabase, emission));
        var optional = _factory.Project(new NamedTypeSpec("Swift.Optional", inner), Context(typeDatabase, emission));

        Assert.Contains($"SwiftDictionary<{carrier}, nint>", Assert.IsType<DictionaryProjection>(nested).SwiftContainerGenericType);
        Assert.Contains($"SwiftDictionary<{carrier}, nint>", Assert.IsType<ArrayProjection>(array).SwiftContainerGenericType);
        Assert.Contains($"SwiftDictionary<{carrier}, nint>", Assert.IsType<OptionalProjection>(optional).InnerProjection.SwiftContainerGenericType);
        Assert.Single(emission.ObjectKeyIdentities);
    }

    [Fact]
    public async Task RawTranslation_ClosureParameterCollectionCarriesTheKeyIdentity()
    {
        // A closure parameter's collection is spelled by the raw translator, not a projection: every
        // collection slot must name the same carrier the projections use.
        var handler = new BoundGenericsHandler(await LoadAppleDatabaseAsync());

        const string carrier = "global::Swift.Runtime.SwiftObjectKey<__SwiftObjectKeys.UIKit_UIImage>";
        const string colorCarrier = "global::Swift.Runtime.SwiftObjectKey<__SwiftObjectKeys.UIKit_UIColor>";
        Assert.EndsWith($"SwiftDictionary<{carrier}, {colorCarrier}>", handler.TranslateBoundGenericTypeToCSharp(
            Dictionary(new NamedTypeSpec("UIKit.UIImage"), new NamedTypeSpec("UIKit.UIColor")), GenericContext.Empty));
        Assert.EndsWith($"SwiftSet<{carrier}>", handler.TranslateBoundGenericTypeToCSharp(
            new NamedTypeSpec("Swift.Set", new NamedTypeSpec("UIKit.UIImage")), GenericContext.Empty));
        Assert.EndsWith($"SwiftArray<{carrier}>", handler.TranslateBoundGenericTypeToCSharp(
            new NamedTypeSpec("Swift.Array", new NamedTypeSpec("UIKit.UIImage")), GenericContext.Empty));
        Assert.EndsWith($"SwiftArray<{carrier}>>", handler.TranslateBoundGenericTypeToCSharp(
            new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Array", new NamedTypeSpec("UIKit.UIImage"))), GenericContext.Empty));
    }

    [Fact]
    public void ObjectKeyIdentity_NamesAreDeterministicIdentifiers()
    {
        var identity = new ObjectKeyIdentity("UIKit.UIApplication.LaunchOptionsKey");

        Assert.Equal("UIKit_UIApplication_LaunchOptionsKey", identity.IdentityClassName);
        Assert.Equal("Swift.Set<UIKit.UIApplication.LaunchOptionsKey>", identity.SwiftSetTypeName);
        Assert.Equal(LaunchOptionsCarrier, identity.CarrierType);
        Assert.Equal(identity.AccessorSymbol("Lib"), new ObjectKeyIdentity("UIKit.UIApplication.LaunchOptionsKey").AccessorSymbol("Lib"));
        Assert.NotEqual(identity.AccessorSymbol("Lib"), identity.AccessorSymbol("OtherLib"));
        Assert.Matches("^SBW_GetObjectKeySetMetadata_Lib_UIKit_UIApplication_LaunchOptionsKey_[0-9A-Za-z]+$", identity.AccessorSymbol("Lib"));
        Assert.Matches("^SBW_GetObjectValueMetadata_Lib_UIKit_UIApplication_LaunchOptionsKey_[0-9A-Za-z]+$", identity.ValueAccessorSymbol("Lib"));
        Assert.NotEqual(identity.AccessorMethodName, identity.ValueAccessorMethodName);
    }

    [Fact]
    public void RecordObjectKey_DedupesOneTypeAndRefusesTwoTypesSharingAName()
    {
        var emission = new ModuleEmissionContext();
        emission.RecordObjectKey(new ObjectKeyIdentity("Foundation.NSUUID"));
        emission.RecordObjectKey(new ObjectKeyIdentity("Foundation.NSUUID"));
        Assert.Single(emission.ObjectKeyIdentities);

        Assert.Throws<InvalidOperationException>(() => emission.RecordObjectKey(new ObjectKeyIdentity("Foundation_NSUUID")));
    }
}
