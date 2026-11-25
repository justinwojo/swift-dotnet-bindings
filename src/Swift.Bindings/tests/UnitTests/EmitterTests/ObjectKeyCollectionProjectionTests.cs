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
        Assert.Contains("GetINativeObject<UIKit.UIImage>(global::Swift.Runtime.SwiftObjectKeys.TakeReference(e), true)", plan.PInvokeExpression);
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
        var element = new ClassProjection("Lib.Tracked").AsCollectionSlot();
        var array = new ArrayProjection(element, isParameter: true);
        Assert.Equal("SwiftArray<Lib.Tracked>", array.SwiftContainerGenericType);
        var setup = string.Join("\n", array.GetParameterPlan("items").SetupStatements.Select(Render));
        Assert.Contains("SwiftArray<Lib.Tracked>.FromEnumerable(items)", setup);
        Assert.DoesNotContain("GetLiveHandle", setup);

        Assert.Equal("IntPtr", new ClassProjection("Lib.Tracked").SwiftContainerGenericType);
        Assert.Equal("Lib.Rooted", new ObjCRootedClassProjection("Lib.Rooted").AsCollectionSlot().SwiftContainerGenericType);
        Assert.Equal("IntPtr", new ObjCRootedClassProjection("Lib.Rooted").SwiftContainerGenericType);

        // The factory applies it to an array element and a dictionary value, never to a key.
        var entity = new NamedTypeSpec("RealityFoundation.Entity");
        var classArray = Assert.IsType<ArrayProjection>(_factory.Project(new NamedTypeSpec("Swift.Array", entity), Context(new TypeDatabase(), new ModuleEmissionContext())));
        Assert.IsType<ClassProjection>(classArray.ElementProjection);
        Assert.DoesNotContain("IntPtr", classArray.SwiftContainerGenericType);
        var classDictionary = Assert.IsType<DictionaryProjection>(_factory.Project(Dictionary(new NamedTypeSpec("Swift.String"), entity), Context(new TypeDatabase(), new ModuleEmissionContext())));
        Assert.DoesNotContain("IntPtr", classDictionary.SwiftContainerGenericType);
        var classSet = Assert.IsType<SetProjection>(_factory.Project(new NamedTypeSpec("Swift.Set", entity), Context(new TypeDatabase(), new ModuleEmissionContext())));
        Assert.Equal("SwiftSet<RealityFoundation.Entity>", classSet.SwiftContainerGenericType);
    }

    [Fact]
    public async Task SwiftClassSetElementAndDictionaryKey_CarryTheWrapperAndRecordItsHashableConformance()
    {
        // A set over the bare handle is a Set<Int>: hashed by address, its elements unretained. The
        // element is carried as its wrapper, whose Hashable witness the module initializer
        // registers, so Swift's storage hashes it the class's way and retains it.
        var typeDatabase = NodeDatabase(await LoadAppleDatabaseAsync());
        var emission = new ModuleEmissionContext();
        var node = new NamedTypeSpec("TestModule.Node");

        var set = Assert.IsType<SetProjection>(_factory.Project(new NamedTypeSpec("Swift.Set", node), Context(typeDatabase, emission)));
        Assert.Equal("SwiftSet<TestModule.Node>", set.SwiftContainerGenericType);
        var setup = string.Join("\n", set.GetParameterPlan("nodes").SetupStatements.Select(Render));
        Assert.DoesNotContain("GetLiveHandle", setup);
        Assert.DoesNotContain("SwiftSet<global::System.IntPtr>", setup);

        var dictionary = Assert.IsType<DictionaryProjection>(_factory.Project(
            Dictionary(node, new NamedTypeSpec("Swift.Int")), Context(typeDatabase, emission, isParameter: false)));
        Assert.Equal("SwiftDictionary<TestModule.Node, nint>", dictionary.SwiftContainerGenericType);

        var hashed = Assert.Single(emission.HashedSwiftClasses);
        Assert.Equal("TestModule.Node", hashed.Identity.SwiftTypeName);
        Assert.Equal("global::TestModule.Node", hashed.WrapperType);
        Assert.Empty(emission.ObjectKeyIdentities);

        // An array element or dictionary value is not hashed: nothing to register.
        var unhashed = new ModuleEmissionContext();
        _factory.Project(new NamedTypeSpec("Swift.Array", node), Context(typeDatabase, unhashed));
        _factory.Project(Dictionary(new NamedTypeSpec("Swift.String"), node), Context(typeDatabase, unhashed));
        Assert.Empty(unhashed.HashedSwiftClasses);
    }

    [Fact]
    public async Task OptionalSwiftClassKey_IsHashedAsTheOptionalType()
    {
        // Set<Node?>: Optional<Node>'s Hashable conformance is the one Swift hashes by, read from
        // Set<Optional<Node>>'s metadata, in the projection and the raw translation alike.
        var typeDatabase = NodeDatabase(await LoadAppleDatabaseAsync());
        var emission = new ModuleEmissionContext();
        var optionalNode = Optional(new NamedTypeSpec("TestModule.Node"));
        const string carrier = "global::Swift.Runtime.SwiftObjectKey<__SwiftObjectKeys.Swift_Optional_TestModule_Node_>";

        var set = Assert.IsType<SetProjection>(_factory.Project(new NamedTypeSpec("Swift.Set", optionalNode), Context(typeDatabase, emission)));
        Assert.Equal($"SwiftSet<{carrier}>", set.SwiftContainerGenericType);
        Assert.True(emission.IsHashedObjectKey(Assert.Single(emission.ObjectKeyIdentities)));

        var handler = new BoundGenericsHandler(typeDatabase);
        Assert.EndsWith($"SwiftSet<{carrier}>", handler.TranslateBoundGenericTypeToCSharp(
            new NamedTypeSpec("Swift.Set", optionalNode), GenericContext.Empty));
    }

    [Fact]
    public async Task RawTranslation_SwiftClassSetElementIsTheWrapperAndNamesItsHashableClass()
    {
        // A closure parameter's set is spelled by the raw translator: the element is the wrapper, and
        // the handler names the class the module initializer registers a Hashable witness for.
        var handler = new BoundGenericsHandler(NodeDatabase(await LoadAppleDatabaseAsync()));
        var node = new NamedTypeSpec("TestModule.Node");
        var translated = handler.TranslateBoundGenericTypeToCSharp(new NamedTypeSpec("Swift.Set", node), GenericContext.Empty);
        Assert.EndsWith("TestModule.Node>", translated);
        Assert.DoesNotContain("IntPtr", translated);

        var hashed = handler.HashedSwiftClassArgument(node);
        Assert.NotNull(hashed);
        Assert.Equal("global::TestModule.Node", hashed!.WrapperType);
        Assert.Null(handler.HashedSwiftClassArgument(new NamedTypeSpec("Swift.Int")));
    }

    [Fact]
    public void RecordHashedSwiftClass_DedupesOneClassAndKeepsTwoClassesWhoseNamesFlattenAlike()
    {
        var emission = new ModuleEmissionContext();
        emission.RecordHashedSwiftClass(HashedSwiftClass.For(new NamedTypeSpec("TestModule.Node"), "TestModule.Node")!);
        emission.RecordHashedSwiftClass(HashedSwiftClass.For(new NamedTypeSpec("TestModule.Node"), "global::TestModule.Node")!);
        Assert.Single(emission.HashedSwiftClasses);

        // TestModule.Outer.Key and TestModule.Outer_Key flatten to one identity name: both are
        // registered, each through its own accessor import.
        emission.RecordHashedSwiftClass(HashedSwiftClass.For(new NamedTypeSpec("TestModule.Outer.Key"), "TestModule.Outer.Key")!);
        emission.RecordHashedSwiftClass(HashedSwiftClass.For(new NamedTypeSpec("TestModule.Outer_Key"), "TestModule.Outer_Key")!);
        Assert.Equal(3, emission.HashedSwiftClasses.Count);
        Assert.Equal(3, emission.HashedSwiftClasses.Select(c => c.AccessorMethodName("TestModule")).Distinct().Count());
    }

    private static TypeDatabase NodeDatabase(TypeDatabase? typeDatabase = null)
    {
        typeDatabase ??= new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");
        var nodeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Node");
        module.RegisterType(nodeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Node"),
            SwiftTypeName = nodeName,
            MetadataAccessor = "$s10TestModule4NodeCMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
        });
        typeDatabase.AddModuleDatabase(module);
        return typeDatabase;
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

        // Out of Swift: a key read from the collection holds its own reference, which the wrapper
        // adopts, on the owned and the borrowed read alike.
        foreach (var read in new[] { key.GetReturnElementConversion("k"), key.GetOwnedReturnElementConversion("k") })
            Assert.Equal("ObjCRuntime.Runtime.GetINativeObject<Foundation.NSString>(global::Swift.Runtime.SwiftObjectKeys.TakeReference(k), true)!", read);

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

    // ── Optional object elements, bound generic Objective-C elements ──────────────────────────

    private const string OptionalUuidCarrier =
        "global::Swift.Runtime.SwiftObjectKey<__SwiftObjectKeys.Swift_Optional_Foundation_NSUUID_>";

    private static NamedTypeSpec Optional(TypeSpec inner) => new("Swift.Optional", inner);

    [Fact]
    public void ObjectKeyIdentity_SpellsGenericArgumentsModuleQualified()
    {
        var optional = ObjectKeyIdentity.For(Optional(new NamedTypeSpec("Foundation.NSUUID")));
        Assert.Equal("Swift.Optional<Foundation.NSUUID>", optional?.SwiftTypeName);
        Assert.Equal("Swift_Optional_Foundation_NSUUID_", optional?.IdentityClassName);

        var cache = ObjectKeyIdentity.For(new NamedTypeSpec("Foundation.NSCache",
            new NamedTypeSpec("Foundation.NSString"), new NamedTypeSpec("Foundation.NSUUID")));
        Assert.Equal("Foundation.NSCache<Foundation.NSString, Foundation.NSUUID>", cache?.SwiftTypeName);

        // A part with no module-qualified Swift name has no identity.
        Assert.Null(ObjectKeyIdentity.For(Optional(new NamedTypeSpec("T"))));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OptionalObjCArrayElement_CarriesTheOptionalTypeIdentity(bool isParameter)
    {
        // [NSUUID?] is an array of one object reference or nil per element, which Swift's storage
        // retains: carried as Optional<NSUUID>, never a bare pointer (a Swift.Int).
        var emission = new ModuleEmissionContext();
        var array = Assert.IsType<ArrayProjection>(_factory.Project(
            new NamedTypeSpec("Swift.Array", Optional(new NamedTypeSpec("Foundation.NSUUID"))),
            Context(await LoadAppleDatabaseAsync(), emission, isParameter)));

        Assert.Equal($"SwiftArray<{OptionalUuidCarrier}>", array.SwiftContainerGenericType);
        var element = Assert.IsType<OptionalProjection>(array.ElementProjection);
        Assert.Equal("Swift.Optional<Foundation.NSUUID>", element.ObjectCarrier?.SwiftTypeName);
        var identity = Assert.Single(emission.ObjectKeyIdentities);
        Assert.Equal("Swift.Optional<Foundation.NSUUID>", identity.SwiftTypeName);
        Assert.False(emission.IsHashedObjectKey(identity));

        // Into Swift: nil is a null carrier, an object borrows into one. Out of Swift: each read
        // adopts the reference its carrier holds; a null one reads as null.
        var into = element.GetParameterElementConversion("e");
        Assert.StartsWith($"new {OptionalUuidCarrier}(e is {{ }} ", into);
        Assert.EndsWith(": IntPtr.Zero)", into);
        Assert.Equal(
            "ObjCRuntime.Runtime.GetINativeObject<Foundation.NSUuid>(global::Swift.Runtime.SwiftObjectKeys.TakeReference(e), true)",
            element.GetReturnElementConversion("e"));
    }

    [Fact]
    public void OptionalSwiftClassDictionaryValue_CarriesTheOptionalTypeAndAdoptsEachRead()
    {
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");
        var nodeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Node");
        module.RegisterType(nodeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Node"),
            SwiftTypeName = nodeName,
            MetadataAccessor = "$s10TestModule4NodeCMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
        });
        typeDatabase.AddModuleDatabase(module);
        var emission = new ModuleEmissionContext();
        var dictionary = Assert.IsType<DictionaryProjection>(_factory.Project(
            Dictionary(new NamedTypeSpec("Swift.String"), Optional(new NamedTypeSpec("TestModule.Node"))),
            Context(typeDatabase, emission, isParameter: false)));

        const string carrier = "global::Swift.Runtime.SwiftObjectKey<__SwiftObjectKeys.Swift_Optional_TestModule_Node_>";
        Assert.EndsWith($", {carrier}>", dictionary.SwiftContainerGenericType);
        var value = Assert.IsType<OptionalProjection>(dictionary.ValueProjection);
        Assert.Contains("TakeSwiftObject<__SwiftObjectKeys.Swift_Optional_TestModule_Node_, ", value.GetReturnElementConversion("v"));
        Assert.Contains(".Payload", value.GetParameterElementConversion("v"));
    }

    [Fact]
    public async Task OptionalObjCSetElement_IsHashedAsTheOptionalType()
    {
        var emission = new ModuleEmissionContext();
        var set = Assert.IsType<SetProjection>(_factory.Project(
            new NamedTypeSpec("Swift.Set", Optional(new NamedTypeSpec("Foundation.NSUUID"))),
            Context(await LoadAppleDatabaseAsync(), emission)));

        Assert.Equal($"SwiftSet<{OptionalUuidCarrier}>", set.SwiftContainerGenericType);
        Assert.True(emission.IsHashedObjectKey(Assert.Single(emission.ObjectKeyIdentities)));
    }

    [Fact]
    public async Task RawTranslation_OptionalObjectSlotsCarryTheOptionalTypeIdentity()
    {
        // A closure parameter or accessor spells the collection raw: the optional's slot must name the
        // projection's carrier, not SwiftOptional<IntPtr> (an Optional<Int>, nine bytes wide).
        var handler = new BoundGenericsHandler(await LoadAppleDatabaseAsync());
        Assert.EndsWith($"SwiftArray<{OptionalUuidCarrier}>", handler.TranslateBoundGenericTypeToCSharp(
            new NamedTypeSpec("Swift.Array", Optional(new NamedTypeSpec("Foundation.NSUUID"))), GenericContext.Empty));
        Assert.EndsWith($"SwiftSet<{OptionalUuidCarrier}>", handler.TranslateBoundGenericTypeToCSharp(
            new NamedTypeSpec("Swift.Set", Optional(new NamedTypeSpec("Foundation.NSUUID"))), GenericContext.Empty));
        Assert.EndsWith($"{OptionalUuidCarrier}>", handler.TranslateBoundGenericTypeToCSharp(
            Dictionary(new NamedTypeSpec("Swift.String"), Optional(new NamedTypeSpec("Foundation.NSUUID"))), GenericContext.Empty));

        // A bare optional is not a collection slot.
        Assert.DoesNotContain("SwiftObjectKey", handler.TranslateBoundGenericTypeToCSharp(
            Optional(new NamedTypeSpec("Foundation.NSUUID")), GenericContext.Empty));
    }

    [Fact]
    public async Task BoundGenericObjCElement_ProjectsAsTheClassCarriedWithItsArguments()
    {
        // NSCache<NSString, NSUUID> is one object reference whose generic arguments .NET's NSCache does
        // not carry: it projects as NSCache, and its slot carries the identity spelled with them, in
        // the projection and the raw translation alike.
        var typeDatabase = await LoadAppleDatabaseAsync();
        var cache = new NamedTypeSpec("Foundation.NSCache", new NamedTypeSpec("Foundation.NSString"), new NamedTypeSpec("Foundation.NSUUID"));
        const string carrier =
            "global::Swift.Runtime.SwiftObjectKey<__SwiftObjectKeys.Foundation_NSCache_Foundation_NSString__Foundation_NSUUID_>";

        var emission = new ModuleEmissionContext();
        var array = Assert.IsType<ArrayProjection>(_factory.Project(new NamedTypeSpec("Swift.Array", cache), Context(typeDatabase, emission)));
        Assert.Equal("IEnumerable<Foundation.NSCache>", array.PublicType);
        Assert.Equal($"SwiftArray<{carrier}>", array.SwiftContainerGenericType);
        Assert.Equal("Foundation.NSCache<Foundation.NSString, Foundation.NSUUID>", Assert.Single(emission.ObjectKeyIdentities).SwiftTypeName);

        Assert.EndsWith($"SwiftArray<{carrier}>", new BoundGenericsHandler(typeDatabase).TranslateBoundGenericTypeToCSharp(
            new NamedTypeSpec("Swift.Array", cache), GenericContext.Empty));
    }

    [Fact]
    public async Task OptionalBoundGenericObjCElement_ProjectsAsTheOptionalClassCarriedWithItsArguments()
    {
        // [NSCache<NSString, NSUUID>?]: the optional of the class's projection, its slot carrying the
        // optional type's identity, never the class spelled with Swift generic arguments C# lacks.
        var typeDatabase = await LoadAppleDatabaseAsync();
        var optionalCache = Optional(new NamedTypeSpec("Foundation.NSCache",
            new NamedTypeSpec("Foundation.NSString"), new NamedTypeSpec("Foundation.NSUUID")));
        const string carrier = "global::Swift.Runtime.SwiftObjectKey<" +
            "__SwiftObjectKeys.Swift_Optional_Foundation_NSCache_Foundation_NSString__Foundation_NSUUID__>";

        var emission = new ModuleEmissionContext();
        var array = Assert.IsType<ArrayProjection>(_factory.Project(
            new NamedTypeSpec("Swift.Array", optionalCache), Context(typeDatabase, emission)));
        Assert.Equal("IEnumerable<Foundation.NSCache?>", array.PublicType);
        Assert.Equal($"SwiftArray<{carrier}>", array.SwiftContainerGenericType);
        Assert.Equal("Swift.Optional<Foundation.NSCache<Foundation.NSString, Foundation.NSUUID>>",
            Assert.Single(emission.ObjectKeyIdentities).SwiftTypeName);
        Assert.Equal(
            "ObjCRuntime.Runtime.GetINativeObject<Foundation.NSCache>(global::Swift.Runtime.SwiftObjectKeys.TakeReference(e), true)",
            Assert.IsType<OptionalProjection>(array.ElementProjection).GetReturnElementConversion("e"));

        var dictionary = Assert.IsType<DictionaryProjection>(_factory.Project(
            Dictionary(new NamedTypeSpec("Swift.String"), optionalCache), Context(typeDatabase, new ModuleEmissionContext())));
        Assert.EndsWith($", {carrier}>", dictionary.SwiftContainerGenericType);

        var handler = new BoundGenericsHandler(typeDatabase);
        Assert.EndsWith($"SwiftArray<{carrier}>", handler.TranslateBoundGenericTypeToCSharp(
            new NamedTypeSpec("Swift.Array", optionalCache), GenericContext.Empty));

        // The member is emitted: no C# generic over the class's Swift arguments is involved.
        Assert.False(handler.HasNonSwiftObjectGenericArg(new NamedTypeSpec("Swift.Array", optionalCache), isParameterPosition: true));
        Assert.False(handler.HasNonSwiftObjectGenericArg(new NamedTypeSpec("Swift.Array", optionalCache), isParameterPosition: false));
    }

    [Fact]
    public async Task CollectionValuedSlots_DisposeEachTemporaryOnceCopied()
    {
        // A dictionary whose values are Swift collections, and an array of optional arrays, build
        // temporaries Swift only copies: each is disposed once copied, not left to its finalizer.
        var typeDatabase = await LoadAppleDatabaseAsync();
        var uuids = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Foundation.NSUUID"));

        var groups = Assert.IsType<DictionaryProjection>(_factory.Project(
            Dictionary(new NamedTypeSpec("Swift.String"), uuids), Context(typeDatabase, new ModuleEmissionContext())));
        Assert.Contains("SwiftCollectionSource.ConsumingValues(value, ",
            AccessorSetterConversion(groups, "value"));

        var nested = Assert.IsType<ArrayProjection>(_factory.Project(
            new NamedTypeSpec("Swift.Array", Optional(uuids)), Context(typeDatabase, new ModuleEmissionContext())));
        Assert.Contains("SwiftCollectionSource.Consuming(value, ", AccessorSetterConversion(nested, "value"));

        // An element that builds nothing disposable is a plain conversion.
        var plain = Assert.IsType<DictionaryProjection>(_factory.Project(
            Dictionary(new NamedTypeSpec("Swift.String"), Optional(new NamedTypeSpec("Swift.Int"))), Context(typeDatabase, new ModuleEmissionContext())));
        Assert.DoesNotContain("Consuming", AccessorSetterConversion(plain, "value"));
    }

    private static string AccessorSetterConversion(ITypeProjection projection, string valueExpr)
        => projection.Accept(new AccessorSetterConversionVisitor(valueExpr)).Item1 ?? "";
}
