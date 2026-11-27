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

    [Fact]
    public void AComputedPropertyOfANonFrozenStruct_DoesNotTouchTheLayout()
    {
        // Swift takes `@frozen struct Holder { var count: Int; var payload: Payload { … } }` as one
        // i64 and returns it in x0, whatever the computed property's type.
        var payload = CreateStructDecl("Payload", ("count", IntSpec));
        payload.IsFrozen = false;
        var holder = CreateStructDecl("Holder", ("count", IntSpec));
        holder.Properties.Add(CreateProperty("payload", Spec("Payload"), hasStorage: false));

        var records = Process(("TestModule.Payload", payload), ("TestModule.Holder", holder));

        Assert.True(MarshallingHelpers.IsTypeFrozen(records["TestModule.Holder"]));
        Assert.True(holder.IsFrozen);
    }

    [Fact]
    public void AComputedPropertyOfAnUnregisteredType_DoesNotTouchTheLayout()
    {
        var holder = CreateStructDecl("Holder", ("count", IntSpec));
        holder.Properties.Add(CreateProperty("path",
            new NamedTypeSpec("Swift.KeyPath", Spec("Holder"), IntSpec), hasStorage: false));

        var records = Process(("TestModule.Holder", holder));

        Assert.True(MarshallingHelpers.IsTypeFrozen(records["TestModule.Holder"]));
    }

    [Fact]
    public void AStoredFrozenStructWhoseComputedPropertyIsResilient_KeepsTheHolderLoadable()
    {
        var payload = CreateStructDecl("Payload", ("count", IntSpec));
        payload.IsFrozen = false;
        var inner = CreateStructDecl("Inner", ("count", IntSpec));
        inner.Properties.Add(CreateProperty("payload", Spec("Payload"), hasStorage: false));
        var holder = CreateStructDecl("Holder", ("inner", Spec("Inner")), ("tag", IntSpec));

        var records = Process(("TestModule.Payload", payload), ("TestModule.Inner", inner), ("TestModule.Holder", holder));

        Assert.True(MarshallingHelpers.IsTypeFrozen(records["TestModule.Inner"]));
        Assert.True(MarshallingHelpers.IsTypeFrozen(records["TestModule.Holder"]));
    }

    [Fact]
    public void AStaticPropertyOfANonFrozenStruct_DoesNotTouchTheLayout()
    {
        // A static stored property lives outside every value, though the descriptor marks its storage.
        var payload = CreateStructDecl("Payload", ("count", IntSpec));
        payload.IsFrozen = false;
        var holder = CreateStructDecl("Holder", ("count", IntSpec));
        var shared = CreateProperty("shared", Spec("Payload"));
        shared.IsStatic = true;
        holder.Properties.Add(shared);

        var records = Process(("TestModule.Payload", payload), ("TestModule.Holder", holder));

        Assert.True(MarshallingHelpers.IsTypeFrozen(records["TestModule.Holder"]));
    }

    [Fact]
    public void ALazyPropertysHiddenStorage_MakesTheStructAddressOnly()
    {
        // `lazy var payload: Payload` is computed; the descriptor lists its storage as a hidden
        // `Payload?`, which the struct stores inline.
        var payload = CreateStructDecl("Payload", ("count", IntSpec));
        payload.IsFrozen = false;
        var holder = CreateStructDecl("Holder", ("count", IntSpec));
        var lazy = CreateProperty("payload", Spec("Payload"), hasStorage: false);
        lazy.IsLazy = true;
        holder.Properties.Add(lazy);
        holder.Properties.Add(CreateProperty("$__lazy_storage_$_payload", Optional(Spec("Payload"))));

        var records = Process(("TestModule.Payload", payload), ("TestModule.Holder", holder));

        Assert.False(MarshallingHelpers.IsTypeFrozen(records["TestModule.Holder"]));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void APropertyWrappersBackingStorage_DecidesTheLayout(bool wrapsInt, bool expectFrozen)
    {
        // `@Box var value: T` is computed; its backing `_value: Box<T>` is the stored field.
        var payload = CreateStructDecl("Payload", ("count", IntSpec));
        payload.IsFrozen = false;
        var box = MakeGeneric(CreateStructDecl("Box", ("wrappedValue", new NamedTypeSpec("τ_0_0"))), "τ_0_0");
        TypeSpec wrapped = wrapsInt ? IntSpec : Spec("Payload");
        var holder = CreateStructDecl("Holder", ("count", IntSpec));
        holder.Properties.Add(CreateProperty("value", wrapped, hasStorage: false));
        holder.Properties.Add(CreateProperty("_value", new NamedTypeSpec("TestModule.Box", wrapped)));

        var records = Process(("TestModule.Payload", payload), ("TestModule.Box", box), ("TestModule.Holder", holder));

        Assert.Equal(expectFrozen, MarshallingHelpers.IsTypeFrozen(records["TestModule.Holder"]));
    }

    [Theory]
    [InlineData("resilient", false)]
    [InlineData("any", false)]
    [InlineData("int", true)]
    [InlineData("indirect-case", true)]
    [InlineData("indirect-enum", true)]
    public void AFrozenPayloadEnum_IsAddressOnlyWhenAnInlinePayloadIs(string payloadShape, bool expectFrozen)
    {
        // Swift moves `@frozen enum Choice { case value(Payload); case none }` through memory when
        // Payload is resilient or an Any existential, and in registers when the payload is an Int or
        // sits in an indirect case's box. A frozen struct storing the enum follows it.
        var payload = CreateStructDecl("Payload", ("count", IntSpec));
        payload.IsFrozen = false;
        var choice = CreateEnumDecl("Choice", isFrozen: true);
        choice.Cases[0].AssociatedValues.Add(payloadShape switch
        {
            "any" => new ProtocolListTypeSpec(),
            "int" => IntSpec,
            _ => Spec("Payload"),
        });
        choice.Cases[0].IsIndirect = payloadShape == "indirect-case";
        choice.IsIndirect = payloadShape == "indirect-enum";
        var holder = CreateStructDecl("Holder", ("choice", Spec("Choice")), ("count", IntSpec));

        var records = Process(("TestModule.Payload", payload), ("TestModule.Holder", holder), ("TestModule.Choice", choice));

        Assert.Equal(expectFrozen, MarshallingHelpers.IsTypeFrozen(records["TestModule.Choice"]));
        Assert.Equal(expectFrozen, MarshallingHelpers.IsTypeFrozen(records["TestModule.Holder"]));
        Assert.Equal(expectFrozen, choice.IsFrozen);
    }

    [Fact]
    public void AGenericFrozenEnum_IsAddressOnlyWhereItsArgumentIs()
    {
        var mode = CreateEnumDecl("Mode", isFrozen: false);
        var choice = CreateEnumDecl("Choice", isFrozen: true);
        choice.RawGenericSig = "<τ_0_0>";
        choice.GenericParameters = [new GenericArgumentDecl("τ_0_0", "τ_0_0", [], [])];
        choice.Cases[0].AssociatedValues.Add(new NamedTypeSpec("τ_0_0"));
        var resilient = CreateStructDecl("Resilient", ("choice", new NamedTypeSpec("TestModule.Choice", Spec("Mode"))));
        var loadable = CreateStructDecl("Loadable", ("choice", new NamedTypeSpec("TestModule.Choice", IntSpec)));

        var records = Process(("TestModule.Mode", mode), ("TestModule.Choice", choice),
            ("TestModule.Resilient", resilient), ("TestModule.Loadable", loadable));

        Assert.False(MarshallingHelpers.IsTypeFrozen(records["TestModule.Resilient"]));
        Assert.True(MarshallingHelpers.IsTypeFrozen(records["TestModule.Loadable"]));
        Assert.True(MarshallingHelpers.IsTypeFrozen(records["TestModule.Choice"]), "unspecialized, the enum's layout is its argument's");
    }

    [Theory]
    [InlineData(SwiftReferenceOwnership.Weak, "native", false)]
    [InlineData(SwiftReferenceOwnership.Unowned, "native", true)]
    [InlineData(SwiftReferenceOwnership.Unowned, "native subclass", true)]
    [InlineData(SwiftReferenceOwnership.Unowned, "objc", false)]
    [InlineData(SwiftReferenceOwnership.Unowned, "objc subclass", false)]
    [InlineData(SwiftReferenceOwnership.Unowned, "AnyObject", false)]
    [InlineData(SwiftReferenceOwnership.Unowned, "any ClassShape", false)]
    [InlineData(SwiftReferenceOwnership.Unmanaged, "objc", true)]
    [InlineData(SwiftReferenceOwnership.Strong, "objc", true)]
    public void AReferenceFieldsOwnership_DecidesWhetherTheStructIsLoadable(
        SwiftReferenceOwnership ownership, string referent, bool expectFrozen)
    {
        var native = MakeClass("Native", superclassUsr: null);
        var nativeSubclass = MakeClass("NativeSubclass", superclassUsr: null, "TestModule.Native");
        var objc = MakeClass("Bridged", "c:objc(cs)NSObject", "ObjectiveC.NSObject");
        var objcSubclass = MakeClass("BridgedSubclass", superclassUsr: null, "TestModule.Bridged", "ObjectiveC.NSObject");
        var classShape = TestDecls.Protocol("ClassShape", "TestModule");
        classShape.IsClassBound = true;
        TypeSpec spec = referent switch
        {
            "native" => Spec("Native"),
            "native subclass" => Spec("NativeSubclass"),
            "objc" => Spec("Bridged"),
            "objc subclass" => Spec("BridgedSubclass"),
            "AnyObject" => new NamedTypeSpec("Swift.AnyObject"),
            "any ClassShape" => Existential("TestModule.ClassShape"),
            _ => throw new ArgumentOutOfRangeException(nameof(referent)),
        };
        if (ownership == SwiftReferenceOwnership.Weak)
            spec = Optional(spec);
        var holder = CreateStructDecl("Holder", ("reference", spec), ("count", IntSpec));
        holder.Properties[0].ReferenceOwnership = ownership;

        var records = Process(("TestModule.Native", native), ("TestModule.NativeSubclass", nativeSubclass),
            ("TestModule.Bridged", objc), ("TestModule.BridgedSubclass", objcSubclass),
            ("TestModule.ClassShape", classShape), ("TestModule.Holder", holder));

        Assert.Equal(expectFrozen, MarshallingHelpers.IsTypeFrozen(records["TestModule.Holder"]));
    }

    [Theory]
    [InlineData("holder")]
    [InlineData("optional holder")]
    [InlineData("enum payload")]
    public void AWeakFieldInsideAFrozenField_MakesTheOuterValueAddressOnly(string shape)
    {
        var native = MakeClass("Native", superclassUsr: null);
        var cell = CreateStructDecl("Cell", ("reference", Optional(Spec("Native"))), ("count", IntSpec));
        cell.Properties[0].ReferenceOwnership = SwiftReferenceOwnership.Weak;
        var choice = CreateEnumDecl("Choice", isFrozen: true);
        choice.Cases[0].AssociatedValues.Add(Spec("Cell"));
        var outer = shape switch
        {
            "holder" => CreateStructDecl("Outer", ("cell", Spec("Cell")), ("count", IntSpec)),
            "optional holder" => CreateStructDecl("Outer", ("cell", Optional(Spec("Cell"))), ("count", IntSpec)),
            "enum payload" => CreateStructDecl("Outer", ("choice", Spec("Choice")), ("count", IntSpec)),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        // The outer value is processed first, before the cell has a record.
        var records = Process(("TestModule.Outer", outer), ("TestModule.Choice", choice),
            ("TestModule.Cell", cell), ("TestModule.Native", native));

        Assert.False(MarshallingHelpers.IsTypeFrozen(records["TestModule.Cell"]));
        Assert.False(MarshallingHelpers.IsTypeFrozen(records["TestModule.Outer"]));
    }

    [Theory]
    // Another module's native Swift class: an unowned reference to it is one pointer.
    [InlineData("Foreign.Node", TypeRecordFlags.None, true)]
    // Another module's class rooted in or bridged from Objective-C, and an SDK class: tracked like a
    // weak reference.
    [InlineData("Foreign.Node", TypeRecordFlags.ObjCRooted, false)]
    [InlineData("Foreign.Node", TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement, false)]
    [InlineData("UIKit.UIView", TypeRecordFlags.None, false)]
    public void AnUnownedFieldOfAnotherModulesClass_IsLoadableOnlyForANativeClass(string className, TypeRecordFlags classFlags, bool expectFrozen)
    {
        var typeDatabase = new TypeDatabase();
        SeedPrimitives(typeDatabase);
        var swiftName = SwiftTypeName.FromModuleQualifiedName(className);
        typeDatabase.AddOutOfModuleTypes(
        [
            (swiftName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(swiftName.Module!, swiftName.Name),
                SwiftTypeName = swiftName,
                MetadataAccessor = "",
                Flags = classFlags,
                Kind = TypeRecordKind.Class,
            }),
        ]);
        var holder = CreateStructDecl("Holder", ("reference", new NamedTypeSpec(className)), ("count", IntSpec));
        holder.Properties[0].ReferenceOwnership = SwiftReferenceOwnership.Unowned;
        // This module's subclass of the same class is as native as its superclass.
        var subclass = MakeClass("Child", className.StartsWith("Foreign.", StringComparison.Ordinal) ? $"s:{className}" : "c:objc(cs)UIView", className);
        var subclassHolder = CreateStructDecl("SubclassHolder", ("reference", Spec("Child")), ("count", IntSpec));
        subclassHolder.Properties[0].ReferenceOwnership = SwiftReferenceOwnership.Unowned;

        var records = Process(typeDatabase, ("TestModule.Holder", holder), ("TestModule.Child", subclass),
            ("TestModule.SubclassHolder", subclassHolder));

        Assert.Equal(expectFrozen, MarshallingHelpers.IsTypeFrozen(records["TestModule.Holder"]));
        Assert.Equal(expectFrozen, MarshallingHelpers.IsTypeFrozen(records["TestModule.SubclassHolder"]));
    }

    [Fact]
    public void ACompositionWithAnSdkProtocolKnownToBindToClasses_IsLoadable()
    {
        var unbound = TestDecls.Protocol("Unbound", "TestModule");
        var composition = new ProtocolListTypeSpec([new NamedTypeSpec("CloudKit.CKSyncEngineDelegate"), Spec("Unbound")]);
        var choice = CreateEnumDecl("Choice", isFrozen: true);
        choice.Cases[0].AssociatedValues.Add(composition);

        var records = Process(("TestModule.Unbound", unbound), ("TestModule.Choice", choice));

        Assert.True(MarshallingHelpers.IsTypeFrozen(records["TestModule.Choice"]));
    }

    [Fact]
    public void ACompositionOfProtocolsWithoutRecords_IsAddressOnly()
    {
        var composition = new ProtocolListTypeSpec([new NamedTypeSpec("Swift.Hashable"), new NamedTypeSpec("Swift.CustomStringConvertible")]);
        var choice = CreateEnumDecl("Choice", isFrozen: true);
        choice.Cases[0].AssociatedValues.Add(composition);
        var holder = CreateStructDecl("Holder", ("value", composition), ("count", IntSpec));

        var records = Process(("TestModule.Choice", choice), ("TestModule.Holder", holder));

        Assert.False(MarshallingHelpers.IsTypeFrozen(records["TestModule.Choice"]));
        Assert.False(MarshallingHelpers.IsTypeFrozen(records["TestModule.Holder"]));
    }

    private static ClassDecl MakeClass(string name, string? superclassUsr, params string[] superclassNames) => new()
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
        MangledName = $"$s10TestModule{name.Length}{name}CN",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Conformances = new List<TypeConformance>(),
        SuperclassNames = superclassNames.ToList(),
        SuperclassUsr = superclassUsr ?? (superclassNames.Length > 0 ? $"s:{superclassNames[0]}" : null),
        ParentDecl = null,
        ModuleDecl = null,
    };

    [Theory]
    // Address-only to every client: Any, and an existential of a protocol not bound to classes.
    [InlineData("Any", false)]
    [InlineData("any Shape", false)]
    [InlineData("Shape (no any)", false)]
    [InlineData("any Shape & Named", false)]
    [InlineData("Optional<any Shape>", false)]
    // Loadable: a reference and its witness tables, a boxed Error, a metatype.
    [InlineData("any ClassShape", true)]
    [InlineData("any RefinedClassShape", true)]
    [InlineData("any Shape & AnyObject", true)]
    [InlineData("AnyObject", true)]
    [InlineData("any Error", true)]
    [InlineData("any Shape.Type", true)]
    public void AnExistentialField_IsAddressOnlyUnlessItIsBoundToClasses(string field, bool expectFrozen)
    {
        var shape = TestDecls.Protocol("Shape", "TestModule");
        var named = TestDecls.Protocol("Named", "TestModule");
        var classShape = TestDecls.Protocol("ClassShape", "TestModule");
        classShape.IsClassBound = true;
        var refinedClassShape = TestDecls.Protocol("RefinedClassShape", "TestModule");
        refinedClassShape.InheritedProtocols.Add(Spec("ClassShape"));
        TypeSpec spec = field switch
        {
            "Any" => new ProtocolListTypeSpec(),
            "any Shape" => Existential("TestModule.Shape"),
            "Shape (no any)" => Spec("Shape"),
            "any Shape & Named" => new ProtocolListTypeSpec([Spec("Shape"), Spec("Named")]),
            "Optional<any Shape>" => Optional(Existential("TestModule.Shape")),
            "any ClassShape" => Existential("TestModule.ClassShape"),
            "any RefinedClassShape" => Existential("TestModule.RefinedClassShape"),
            "any Shape & AnyObject" => new ProtocolListTypeSpec([Spec("Shape"), new NamedTypeSpec("Swift.AnyObject")]),
            "AnyObject" => new NamedTypeSpec("Swift.AnyObject"),
            "any Error" => Existential("Swift.Error"),
            "any Shape.Type" => Existential("TestModule.Shape.Type"),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        var holder = CreateStructDecl("Holder", ("value", spec), ("count", IntSpec));

        var records = Process(("TestModule.Shape", shape), ("TestModule.Named", named), ("TestModule.ClassShape", classShape),
            ("TestModule.RefinedClassShape", refinedClassShape), ("TestModule.Holder", holder));

        Assert.Equal(expectFrozen, MarshallingHelpers.IsTypeFrozen(records["TestModule.Holder"]));
    }

    private static NamedTypeSpec Existential(string name) => new(name) { IsAny = true };

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
