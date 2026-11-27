// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Whether a <c>@frozen</c> struct's layout is fixed to clients, as the module processor records it.
/// A struct storing a value type that is resilient to clients (a non-frozen enum or struct, also as an
/// Optional payload or a tuple element) is address-only to them: Swift returns it through the
/// indirect-result register and takes it as an argument by address, whatever its size. The record
/// has to say so, or a direct call or a tail-call thunk returns it in registers Swift never writes.
/// A struct storing only fixed-layout values keeps its frozen record and its register lowering.
/// </summary>
public class FrozenStructClientLayoutTests
{
    private static readonly NamedTypeSpec IntSpec = new("Swift.Int");

    [Fact]
    public void ANonFrozenEnumField_MakesTheStructAddressOnly()
    {
        var mode = CreateEnumDecl("Mode", isFrozen: false);
        var holder = CreateStructDecl("Holder", ("mode", Spec("Mode")), ("count", IntSpec));

        var records = Process(("TestModule.Mode", mode), ("TestModule.Holder", holder));

        var record = records["TestModule.Holder"];
        Assert.False(MarshallingHelpers.IsTypeFrozen(record));
        Assert.Equal(true, TypeLowering.LowerFromTypeRecord(record, new TypeDatabase())?.IsIndirect);
        Assert.False(holder.IsFrozen);
    }

    [Fact]
    public void AFrozenEnumField_KeepsTheStructLoadable()
    {
        var mode = CreateEnumDecl("Mode", isFrozen: true);
        var holder = CreateStructDecl("Holder", ("mode", Spec("Mode")), ("count", IntSpec));

        var records = Process(("TestModule.Mode", mode), ("TestModule.Holder", holder));

        var record = records["TestModule.Holder"];
        Assert.True(MarshallingHelpers.IsTypeFrozen(record));
        Assert.True(holder.IsFrozen);
    }

    [Fact]
    public void AnObjCEnumField_KeepsTheStructLoadable()
    {
        // An @objc enum is a C enum stored as its raw value, @frozen or not.
        var mode = CreateEnumDecl("Mode", isFrozen: false, rawValueTypeName: "Int");
        mode.IsObjC = true;
        var holder = CreateStructDecl("Holder", ("mode", Spec("Mode")), ("count", IntSpec));

        var records = Process(("TestModule.Mode", mode), ("TestModule.Holder", holder));

        Assert.True(MarshallingHelpers.IsTypeFrozen(records["TestModule.Holder"]));
    }

    [Fact]
    public void AResilientStructStoredInline_IsFoundWhileItIsStillBeingProcessed()
    {
        // The resilient payload is processed first and reaches the holder through a computed
        // property, so the holder is judged while the payload has no record yet.
        var payload = CreateStructDecl("Payload", ("count", IntSpec));
        payload.IsFrozen = false;
        payload.Properties.Add(CreateProperty("holder", Spec("Holder"), hasStorage: false));
        var holder = CreateStructDecl("Holder",
            ("payload", new NamedTypeSpec("Swift.Optional", Spec("Payload"))), ("count", IntSpec));

        var records = Process(("TestModule.Payload", payload), ("TestModule.Holder", holder));

        Assert.False(MarshallingHelpers.IsTypeFrozen(records["TestModule.Holder"]));
    }

    [Fact]
    public void AFrozenStructWhoseRecordIsNotFrozenForAComputedProperty_StaysLoadableInsideAnOptional()
    {
        // A computed property of a resilient type clears the inner struct's record flag, but its
        // stored layout is one Int, so an Optional of it is as loadable as the Int.
        var payload = CreateStructDecl("Payload", ("count", IntSpec));
        payload.IsFrozen = false;
        var inner = CreateStructDecl("Inner", ("count", IntSpec));
        inner.Properties.Add(CreateProperty("payload", Spec("Payload"), hasStorage: false));
        var holder = CreateStructDecl("Holder",
            ("inner", new NamedTypeSpec("Swift.Optional", Spec("Inner"))), ("count", IntSpec));

        var records = Process(("TestModule.Payload", payload), ("TestModule.Inner", inner), ("TestModule.Holder", holder));

        Assert.True(MarshallingHelpers.IsTypeFrozen(records["TestModule.Holder"]));
    }

    [Fact]
    public void AnOptionalPointerField_KeepsTheStructLoadable()
    {
        // The pointer's own record is not frozen, but a pointer is one word to every client.
        var holder = CreateStructDecl("Holder",
            ("pointer", new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.UnsafePointer", IntSpec))), ("count", IntSpec));

        var records = Process(("TestModule.Holder", holder));

        Assert.True(MarshallingHelpers.IsTypeFrozen(records["TestModule.Holder"]));
    }

    [Fact]
    public void AResilientArgumentAGenericStructNeverStores_KeepsTheHolderLoadable()
    {
        var mode = CreateEnumDecl("Mode", isFrozen: false);
        var phantom = MakeGeneric(CreateStructDecl("Phantom", ("count", IntSpec)), "τ_0_0");
        var holder = CreateStructDecl("Holder", ("phantom", Optional(new NamedTypeSpec("TestModule.Phantom", Spec("Mode")))));

        var records = Process(("TestModule.Mode", mode), ("TestModule.Phantom", phantom), ("TestModule.Holder", holder));

        Assert.True(MarshallingHelpers.IsTypeFrozen(records["TestModule.Holder"]));
    }

    [Theory]
    [InlineData(1)]
    // The same struct specialized twice along one path, each specialization judged on its own.
    [InlineData(2)]
    public void AResilientArgumentAGenericStructStores_MakesTheHolderAddressOnly(int nesting)
    {
        var mode = CreateEnumDecl("Mode", isFrozen: false);
        var box = MakeGeneric(CreateStructDecl("Box", ("value", Optional(new NamedTypeSpec("τ_0_0")))), "τ_0_0");
        TypeSpec field = Spec("Mode");
        for (int i = 0; i < nesting; i++)
            field = new NamedTypeSpec("TestModule.Box", field);
        var holder = CreateStructDecl("Holder", ("box", Optional(field)), ("count", IntSpec));

        var records = Process(("TestModule.Mode", mode), ("TestModule.Box", box), ("TestModule.Holder", holder));

        Assert.True(MarshallingHelpers.IsTypeFrozen(records["TestModule.Box"]), "unspecialized, the box's layout is its argument's");
        Assert.False(MarshallingHelpers.IsTypeFrozen(records["TestModule.Holder"]));
    }

    [Fact]
    public void AResilientArgumentOfANestedGenericStruct_MakesTheHolderAddressOnly()
    {
        // Outer<Int>.Inner<Mode>: the inner declaration's signature lists the outer parameter first.
        var mode = CreateEnumDecl("Mode", isFrozen: false);
        var inner = MakeGeneric(CreateStructDecl("Inner", ("value", Optional(new NamedTypeSpec("τ_1_0")))), "τ_0_0", "τ_1_0");
        inner.SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Outer.Inner");
        var use = new NamedTypeSpec("TestModule.Outer", IntSpec) { InnerType = new NamedTypeSpec("Inner", Spec("Mode")) };
        var holder = CreateStructDecl("Holder", ("inner", Optional(use)), ("count", IntSpec));

        var records = Process(("TestModule.Mode", mode), ("TestModule.Outer.Inner", inner), ("TestModule.Holder", holder));

        Assert.False(MarshallingHelpers.IsTypeFrozen(records["TestModule.Holder"]));
    }

    [Fact]
    public void AnotherModulesStructThisModuleExtends_IsJudgedByItsOwnLayout()
    {
        // Extending another module's struct leaves a partial declaration of it, not marked frozen,
        // among this module's; the struct's layout is still the one its own module fixed.
        var extended = CreateStructDecl("_NSRange");
        extended.SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation._NSRange");
        extended.IsFrozen = false;
        var holder = CreateStructDecl("Holder", ("range", Optional(new NamedTypeSpec("Foundation._NSRange"))), ("count", IntSpec));

        var records = Process(("Foundation._NSRange", extended), ("TestModule.Holder", holder));

        Assert.True(MarshallingHelpers.IsTypeFrozen(records["TestModule.Holder"]));
    }

    [Fact]
    public void AResilientStructThisModuleNestsInAnotherModulesType_IsFoundWhileItIsStillBeingProcessed()
    {
        // Declared in an extension of another module's type, so its name is that module's. It is
        // processed first and reaches the holder through a computed property, so the holder is
        // judged while it has no record yet.
        var receiver = CreateStructDecl("Int");
        receiver.SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int");
        var nestedSpec = new NamedTypeSpec("Swift.Int") { InnerType = new NamedTypeSpec("Payload") };
        var payload = CreateStructDecl("Payload", ("count", IntSpec));
        payload.SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int.Payload");
        payload.ParentDecl = receiver;
        payload.IsFrozen = false;
        payload.Properties.Add(CreateProperty("holder", Spec("Holder"), hasStorage: false));
        var holder = CreateStructDecl("Holder", ("payload", Optional(nestedSpec)), ("count", IntSpec));

        var records = Process(("Swift.Int.Payload", payload), ("TestModule.Holder", holder));

        Assert.False(MarshallingHelpers.IsTypeFrozen(records["TestModule.Holder"]));
    }

    [Theory]
    [InlineData("optional")]
    [InlineData("tuple")]
    public void ANonFrozenEnumStoredInline_MakesTheStructAddressOnly(string wrapping)
    {
        // An Optional stores its payload inline and a tuple its elements, so either leaves the
        // container's layout as resilient as the enum it holds.
        TypeSpec field = wrapping == "optional"
            ? new NamedTypeSpec("Swift.Optional", Spec("Mode"))
            : new TupleTypeSpec(new List<TypeSpec> { Spec("Mode"), IntSpec });
        var mode = CreateEnumDecl("Mode", isFrozen: false);
        var holder = CreateStructDecl("Holder", ("field", field), ("count", IntSpec));

        var records = Process(("TestModule.Mode", mode), ("TestModule.Holder", holder));

        Assert.False(MarshallingHelpers.IsTypeFrozen(records["TestModule.Holder"]));
    }

    [Fact]
    public void AFrozenStructStoringAnAddressOnlyFrozenStruct_IsAddressOnlyToo()
    {
        var mode = CreateEnumDecl("Mode", isFrozen: false);
        var inner = CreateStructDecl("Inner", ("mode", Spec("Mode")));
        var outer = CreateStructDecl("Outer", ("inner", Spec("Inner")), ("count", IntSpec));

        var records = Process(("TestModule.Outer", outer), ("TestModule.Inner", inner), ("TestModule.Mode", mode));

        Assert.False(MarshallingHelpers.IsTypeFrozen(records["TestModule.Inner"]));
        Assert.False(MarshallingHelpers.IsTypeFrozen(records["TestModule.Outer"]));
    }

    [Fact]
    public void AComputedPropertyOfANonFrozenEnum_DoesNotTouchTheLayout()
    {
        // Only stored fields sit in the value; a computed property's type says nothing about it.
        var mode = CreateEnumDecl("Mode", isFrozen: false);
        var holder = CreateStructDecl("Holder", ("count", IntSpec));
        holder.Properties.Add(CreateProperty("mode", Spec("Mode"), hasStorage: false));

        var records = Process(("TestModule.Mode", mode), ("TestModule.Holder", holder));

        Assert.True(MarshallingHelpers.IsTypeFrozen(records["TestModule.Holder"]));
    }

    [Theory]
    [InlineData(false, SimpleEnumStorage.CaseTag, false)]
    [InlineData(true, SimpleEnumStorage.CaseTag, true)]
    // Another module's @objc enum: a C enum stored as its raw value, frozen or not.
    [InlineData(false, SimpleEnumStorage.RawValue, true)]
    public void AnotherModulesEnum_IsJudgedByItsRecord(bool foreignEnumFrozen, SimpleEnumStorage storage, bool expectFrozen)
    {
        var typeDatabase = new TypeDatabase();
        SeedPrimitives(typeDatabase);
        var foreignName = SwiftTypeName.FromModuleQualifiedName("Foreign.Mode");
        typeDatabase.AddOutOfModuleTypes(
        [
            (foreignName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foreign", "Mode"),
                SwiftTypeName = foreignName,
                MetadataAccessor = "",
                Flags = (foreignEnumFrozen ? TypeRecordFlags.Frozen : TypeRecordFlags.None) | TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum,
                SimpleEnumStorage = storage,
            }),
        ]);
        var holder = CreateStructDecl("Holder", ("mode", new NamedTypeSpec("Foreign.Mode")), ("count", IntSpec));

        var records = Process(typeDatabase, ("TestModule.Holder", holder));

        Assert.Equal(expectFrozen, MarshallingHelpers.IsTypeFrozen(records["TestModule.Holder"]));
    }

    private static NamedTypeSpec Spec(string name) => new($"TestModule.{name}");

    private static NamedTypeSpec Optional(TypeSpec payload) => new("Swift.Optional", payload);

    private static StructDecl MakeGeneric(StructDecl decl, params string[] parameters)
    {
        decl.RawGenericSig = "<" + string.Join(", ", parameters) + ">";
        decl.GenericParameters = parameters
            .Select(p => new GenericArgumentDecl(p, p, new List<GenericParameterConformance>(), new List<GenericParameterConformance>()))
            .ToList();
        return decl;
    }

    private static Dictionary<string, TypeRecord> Process(params (string name, TypeDecl decl)[] decls)
    {
        var typeDatabase = new TypeDatabase();
        SeedPrimitives(typeDatabase);
        return Process(typeDatabase, decls);
    }

    private static Dictionary<string, TypeRecord> Process(TypeDatabase typeDatabase, params (string name, TypeDecl decl)[] decls)
    {
        var typeDecls = new Dictionary<NamedTypeSpec, TypeDecl>();
        foreach (var (name, decl) in decls)
            typeDecls[new NamedTypeSpec(name)] = decl;

        var processor = new ModuleProcessor(
            "TestModule", "/tmp/TestModule.dylib", "TestModule", typeDecls, typeDatabase, NullLogger.Instance);
        var result = processor.FinalizeTypeProcessingAndCreateModuleDatabase();
        typeDatabase.AddModuleDatabase(result.ModuleDatabase);

        var records = new Dictionary<string, TypeRecord>();
        // Only this module's own declarations are registered; another module's partial one is not.
        foreach (var (name, _) in decls.Where(d => d.name.StartsWith("TestModule.", StringComparison.Ordinal)))
        {
            Assert.True(
                typeDatabase.TryGetTypeRecord(SwiftTypeName.FromModuleQualifiedName(name), out var record),
                $"Expected a registered type record for '{name}'.");
            records[name] = record!;
        }
        return records;
    }

    /// <summary>The stdlib records a stored field's frozen check looks up.</summary>
    private static void SeedPrimitives(TypeDatabase typeDatabase)
    {
        var seeded = new List<(SwiftTypeName, TypeRecord)>();
        // As the stdlib database records them: the typed pointers are not marked frozen.
        foreach (var (name, frozen) in new[] { ("Swift.Int", true), ("Swift.Optional", true), ("Swift.UnsafePointer", false) })
        {
            var swiftName = SwiftTypeName.FromModuleQualifiedName(name);
            seeded.Add((swiftName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", swiftName.Name),
                SwiftTypeName = swiftName,
                MetadataAccessor = "",
                Flags = frozen ? TypeRecordFlags.Frozen : TypeRecordFlags.None,
                Kind = name == "Swift.Optional" ? TypeRecordKind.Enum : TypeRecordKind.Struct,
            }));
        }
        typeDatabase.AddOutOfModuleTypes(seeded);
    }

    private static PropertyDecl CreateProperty(string name, TypeSpec type, bool hasStorage = true)
        => new()
        {
            Name = name,
            SwiftTypeSpec = type,
            IsStatic = false,
            HasStorage = hasStorage,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };

    private static StructDecl CreateStructDecl(string name, params (string propName, TypeSpec propType)[] properties)
        => new()
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
            Properties = properties.Select(p => CreateProperty(p.propName, p.propType)).ToList(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
        };

    private static EnumDecl CreateEnumDecl(string name, bool isFrozen, string? rawValueTypeName = null)
        => new()
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}ON",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Cases = new[] { "first", "second" }.Select(caseName => new EnumCaseDecl
            {
                Name = caseName,
                MangledName = $"$s10TestModule{name.Length}{name}O{caseName.Length}{caseName}yA2CmF",
                ParentDecl = null,
                ModuleDecl = null,
            }).ToList(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = isFrozen,
            MetadataAccessor = "",
            RawValueTypeName = rawValueTypeName,
        };
}
