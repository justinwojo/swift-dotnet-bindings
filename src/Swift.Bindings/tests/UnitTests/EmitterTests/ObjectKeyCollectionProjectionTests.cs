// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

#nullable enable

namespace BindingsGeneration.Tests;

/// <summary>
/// A dictionary key or set element that crosses as one Objective-C object pointer (an ObjC class, a
/// string-backed key type such as <c>UIApplication.LaunchOptionsKey</c>) must be carried in the
/// Swift collection as a value of its own Swift type, never as the bare pointer: a collection over
/// <c>IntPtr</c> is one over <c>Swift.Int</c>, hashed by address with its keys unretained, while
/// Swift reads it as the key type and hashes by value.
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
    public void ObjCClassValueAndArrayElement_KeepTheBarePointer()
    {
        // Only a hashed position carries the identity: a dictionary value or an array element is
        // never hashed, so it stays the pointer and records nothing.
        var emission = new ModuleEmissionContext();
        var dictionary = Assert.IsType<DictionaryProjection>(_factory.Project(
            Dictionary(new NamedTypeSpec("Swift.String"), new NamedTypeSpec("UIKit.UIImage")), Context(new TypeDatabase(), emission)));
        var array = Assert.IsType<ArrayProjection>(_factory.Project(
            new NamedTypeSpec("Swift.Array", new NamedTypeSpec("UIKit.UIImage")), Context(new TypeDatabase(), emission)));

        Assert.DoesNotContain("SwiftObjectKey", dictionary.SwiftContainerGenericType);
        Assert.DoesNotContain("SwiftObjectKey", array.SwiftContainerGenericType);
        Assert.Empty(emission.ObjectKeyIdentities);
    }

    [Fact]
    public async Task KeyConversions_BorrowTheObjectIntoTheCarrierAndAdoptTheCopyReadBack()
    {
        var projection = Assert.IsType<DictionaryProjection>(_factory.Project(
            Dictionary(new NamedTypeSpec("UIKit.UIApplication.LaunchOptionsKey"), new NamedTypeSpec("Swift.Int")),
            Context(await LoadAppleDatabaseAsync(), new ModuleEmissionContext())));
        var key = projection.KeyProjection;

        // Into Swift: the carrier borrows the live object; marshalling it copies (+1) through the
        // key type's value witness.
        var into = key.GetParameterElementConversion("k");
        Assert.Equal($"new {LaunchOptionsCarrier}({LiveHandleExpressions.ObjCSelf("k")})", into);

        // Out of Swift: a key read from the collection is a copy holding +1, adopted by the wrapper,
        // on the owned and the borrowed read alike.
        foreach (var read in new[] { key.GetReturnElementConversion("k"), key.GetOwnedReturnElementConversion("k") })
            Assert.Equal("ObjCRuntime.Runtime.GetINativeObject<Foundation.NSString>(k.Handle, true)!", read);

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
        // A closure parameter's collection is spelled by the raw translator, not a projection: its
        // hashed slot must name the same carrier, its unhashed slots the bare pointer.
        var handler = new BoundGenericsHandler(await LoadAppleDatabaseAsync());

        const string carrier = "global::Swift.Runtime.SwiftObjectKey<__SwiftObjectKeys.UIKit_UIImage>";
        Assert.EndsWith($"SwiftDictionary<{carrier}, IntPtr>", handler.TranslateBoundGenericTypeToCSharp(
            Dictionary(new NamedTypeSpec("UIKit.UIImage"), new NamedTypeSpec("UIKit.UIColor")), GenericContext.Empty));
        Assert.EndsWith($"SwiftSet<{carrier}>", handler.TranslateBoundGenericTypeToCSharp(
            new NamedTypeSpec("Swift.Set", new NamedTypeSpec("UIKit.UIImage")), GenericContext.Empty));
        Assert.EndsWith("SwiftArray<IntPtr>", handler.TranslateBoundGenericTypeToCSharp(
            new NamedTypeSpec("Swift.Array", new NamedTypeSpec("UIKit.UIImage")), GenericContext.Empty));
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
