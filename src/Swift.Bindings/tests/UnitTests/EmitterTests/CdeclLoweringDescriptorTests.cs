// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Golden-by-equality coverage for <see cref="CdeclParamMapper.Describe"/> — the single
/// per-parameter @_cdecl lowering decision that classifies one parameter into exactly one
/// <see cref="CdeclParamCategory"/> and produces the Swift-side wrapper signature text, body
/// reconstruction, and call-site expression for that category. Every category the by-value/inout
/// classifier reaches from a bare <see cref="TypeSpec"/> in isolation is pinned by its
/// <see cref="CdeclLoweringDescriptor.Category"/> AND the exact Swift-text it lowers to, so the
/// classification can't silently drift before the leg-B/leg-C consumers start reading the category.
/// Two arms fire only from richer emitter context and are not reachable from a bare TypeSpec here —
/// the <c>ObjCBridgedClassPointer</c> NSString-typedef special case (needs an AppleFrameworkRegistry
/// name that remaps to <c>Foundation.NSString</c>) and the metatype-routed <c>ProtocolTypeRecord</c>
/// arm (a single named protocol is caught earlier as <c>ProtocolExistential</c>); both are covered by
/// <c>MethodWrapperEmitterTests</c>/<c>ConstructorWrapperEmitterTests</c>.
///
/// Two structural invariants of the producer/shim split are also pinned:
/// <list type="bullet">
///   <item><see cref="CdeclParamMapper.Map"/> is a pure projection of the descriptor's three
///   Swift-text fields (the by-value callers' contract).</item>
///   <item><see cref="CdeclParamMapper.MapInout"/> is a pure projection of
///   <c>Describe(..., isInout: true)</c>, whose category is always <see cref="CdeclParamCategory.Inout"/>
///   with a populated write-back.</item>
/// </list>
/// Type names embedded in the lowering strings are rendered through the same
/// <see cref="ExistentialBypassEmitter"/> helpers the production arms use, so the test pins the
/// per-category template assembly without re-hardcoding the renderer's output.
/// </summary>
public class CdeclLoweringDescriptorTests
{
    private const string Label = "value";

    // ----- fixture -------------------------------------------------------------------------

    private static (TypeDatabase db, ModuleDecl module) NewFixture()
    {
        var db = new TypeDatabase();

        var swift = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        RegisterStruct(swift, "Swift.Int", "$sSiMa", frozen: true);
        RegisterStruct(swift, "Swift.Int32", "$ss5Int32VMa", frozen: true);
        RegisterStruct(swift, "Swift.Bool", "$sSbMa", frozen: true);
        RegisterStruct(swift, "Swift.String", "$sSSMa",
            flags: TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement);
        db.AddModuleDatabase(swift);

        var cg = new ModuleTypeDatabase("CoreGraphics", "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics");
        RegisterStruct(cg, "CoreGraphics.CGRect", "$s12CoreGraphics6CGRectVMa", frozen: true);
        db.AddModuleDatabase(cg);

        // Frozen Foundation value struct that is ALSO ObjC-bridgeable: registered the way the
        // shipped Foundation database registers it, so the bridgeable-value arm is reachable here.
        var foundation = new ModuleTypeDatabase("Foundation", "/System/Library/Frameworks/Foundation.framework/Foundation");
        RegisterStruct(foundation, "Foundation.UUID", "$s10Foundation4UUIDVMa", frozen: true);
        db.AddModuleDatabase(foundation);

        var test = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        Register(test, "TestModule.MyClass", TypeRecordKind.Class, TypeRecordFlags.None);
        Register(test, "TestModule.MyObjCClass", TypeRecordKind.Class, TypeRecordFlags.ObjCBridged);
        Register(test, "TestModule.MyBridgeableValue", TypeRecordKind.Struct, TypeRecordFlags.ObjCBridgeable);
        Register(test, "TestModule.MyNonCopyable", TypeRecordKind.Struct, TypeRecordFlags.NonCopyable);
        RegisterStruct(test, "TestModule.MyStruct", "$s10TestModule8MyStructVMa", frozen: true);
        Register(test, "TestModule.MyNonFrozenStruct", TypeRecordKind.Struct, TypeRecordFlags.None);
        Register(test, "TestModule.MyEnum", TypeRecordKind.Enum, TypeRecordFlags.SimpleEnum, rawValueType: "Swift.Int");
        Register(test, "TestModule.MyOptionSet", TypeRecordKind.Enum,
            TypeRecordFlags.SimpleEnum | TypeRecordFlags.OptionSet, rawValueType: "Swift.Int");
        Register(test, "TestModule.MyOptionSetU", TypeRecordKind.Enum,
            TypeRecordFlags.SimpleEnum | TypeRecordFlags.OptionSet, rawValueType: "Swift.UInt");
        Register(test, "TestModule.MyComplexEnum", TypeRecordKind.Enum, TypeRecordFlags.None);
        db.AddModuleDatabase(test);

        var module = new ModuleDecl
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
        return (db, module);
    }

    private static void RegisterStruct(ModuleTypeDatabase mod, string fqName, string metadataAccessor,
        bool frozen = false, TypeRecordFlags? flags = null)
        => Register(mod, fqName, TypeRecordKind.Struct,
            flags ?? (frozen ? TypeRecordFlags.Frozen : TypeRecordFlags.None), metadataAccessor: metadataAccessor);

    private static void Register(ModuleTypeDatabase mod, string fqName, TypeRecordKind kind, TypeRecordFlags flags,
        string? rawValueType = null, string? metadataAccessor = null)
    {
        var swiftName = SwiftTypeName.FromModuleQualifiedName(fqName);
        var leaf = fqName.Contains('.') ? fqName.Substring(fqName.LastIndexOf('.') + 1) : fqName;
        var ns = fqName.Contains('.') ? fqName.Substring(0, fqName.LastIndexOf('.')) : "";
        mod.RegisterType(swiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName(ns, leaf),
            SwiftTypeName = swiftName,
            MetadataAccessor = metadataAccessor ?? $"$s{leaf}Ma",
            Flags = flags,
            Kind = kind,
            RawValueTypeName = rawValueType,
        });
    }

    private static MethodEnvironment Env(TypeDatabase db, ModuleDecl module)
    {
        var parent = new ClassDecl
        {
            Name = "Owner",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Owner"),
            MangledName = "$s10TestModule5OwnerCN",
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
        var method = new MethodDecl
        {
            Name = "m",
            MangledName = "$s10TestModule1m",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = module,
                },
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parent,
            ModuleDecl = module,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
        };
        return new MethodEnvironment(method, db);
    }

    private static ArgumentDecl Arg(TypeSpec spec, ModuleDecl module, ParameterOwnership ownership)
        => new ArgumentDecl
        {
            SwiftTypeSpec = spec,
            // Empty Name => BuildSwiftCallArgLabel returns "" => the call-arg label is omitted, so the
            // emitted callArg is deterministic ("value"/"valueVal") and independent of label shaping.
            Name = "",
            PrivateName = "",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = module,
            Ownership = ownership,
        };

    private static CdeclLoweringDescriptor Describe(TypeSpec spec, bool useUtf8 = false,
        ParameterOwnership ownership = ParameterOwnership.Default, bool omitLabels = false)
    {
        var (db, module) = NewFixture();
        return CdeclParamMapper.Describe(Arg(spec, module, ownership), Label, Env(db, module),
            omitLabels: omitLabels, useUtf8Strings: useUtf8);
    }

    private static NamedTypeSpec Named(string name, params TypeSpec[] generics)
        => generics.Length == 0 ? new NamedTypeSpec(name) : new NamedTypeSpec(name, generics);

    private static NamedTypeSpec Optional(TypeSpec inner) => Named("Swift.Optional", inner);

    private static string RenderMQ(TypeSpec spec) => ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(spec);

    private static void AssertDescriptor(CdeclLoweringDescriptor d, CdeclParamCategory category,
        string cdeclParam, string? reconstruction, string callArg)
    {
        Assert.Equal(category, d.Category);
        Assert.Equal(cdeclParam, d.CdeclParam);
        Assert.Equal(reconstruction, d.Reconstruction);
        Assert.Equal(callArg, d.CallArg);
    }

    // ----- per-category goldens ------------------------------------------------------------

    [Fact]
    public void Primitive_PassesThrough()
        => AssertDescriptor(Describe(Named("Swift.Int")),
            CdeclParamCategory.Primitive, "_ value: Int", null, "value");

    [Fact]
    public void Bool_LowersToInt8()
        => AssertDescriptor(Describe(Named("Swift.Bool")),
            CdeclParamCategory.Bool, "_ value: Int8", "let valueVal = value != 0", "valueVal");

    [Fact]
    public void AnyObject_UsesUnmanagedAnyObject()
        => AssertDescriptor(Describe(Named("Swift.AnyObject")),
            CdeclParamCategory.AnyObject, "_ value: UnsafeMutableRawPointer",
            "let valueVal: AnyObject = Unmanaged<AnyObject>.fromOpaque(value).takeUnretainedValue()", "valueVal");

    [Fact]
    public void OptionalAny_LoadsOptionalAny()
    {
        var spec = Optional(new ProtocolListTypeSpec());
        AssertDescriptor(Describe(spec), CdeclParamCategory.OptionalAny, "_ value: UnsafeRawPointer",
            "let valueVal: Any? = value.load(as: Optional<Any>.self)", "valueVal");
    }

    [Fact]
    public void ProtocolExistential_LoadsByPointer()
    {
        var (db, module) = NewFixture();
        var spec = new ProtocolListTypeSpec(new[] { Named("TestModule.MyProto") });
        // Assemble the expected load type through the same helper + parenthesization rule the
        // producer uses, so the full reconstruction is pinned without re-hardcoding the renderer.
        var swiftType = CdeclParamMapper.RenderModuleQualifiedSwiftTypeWithExistentialAny(spec, db);
        var loadType = swiftType.StartsWith("any ") ? $"({swiftType})" : swiftType;
        AssertDescriptor(
            CdeclParamMapper.Describe(Arg(spec, module, ParameterOwnership.Default), Label, Env(db, module)),
            CdeclParamCategory.ProtocolExistential, "_ value: UnsafeRawPointer",
            $"let valueVal: {loadType} = value.load(as: {loadType}.self)", "valueVal");
    }

    [Fact]
    public void OptionalReference_UsesNullablePointer()
    {
        var spec = Optional(Named("TestModule.MyClass"));
        AssertDescriptor(Describe(spec), CdeclParamCategory.OptionalReference,
            "_ value: UnsafeMutableRawPointer?",
            "let valueVal: TestModule.MyClass? = value.map { Unmanaged<TestModule.MyClass>.fromOpaque($0).takeUnretainedValue() }",
            "valueVal");
    }

    [Fact]
    public void OptionalBlittablePrimitive_DecodesTagByte()
    {
        var spec = Optional(Named("Swift.Int32"));
        // Pin the full reconstruction by assembling it from the same classifier output the producer
        // wires in — `let {label}Opt: {localType} = {rhs}` — rather than a substring check.
        var decode = OptionalMarshalClassifier.TryGetBlittablePrimitiveOptionalDecode(spec, Label);
        Assert.NotNull(decode);
        var (localType, rhs) = decode!.Value;
        AssertDescriptor(Describe(spec), CdeclParamCategory.OptionalBlittablePrimitive,
            "_ value: UnsafeRawPointer", $"let valueOpt: {localType} = {rhs}", "valueOpt");
    }

    [Fact]
    public void OptionalBlittablePrimitive_OmitLabels_PassesPointerThrough()
        => AssertDescriptor(Describe(Optional(Named("Swift.Int32")), omitLabels: true),
            CdeclParamCategory.OptionalBlittablePrimitive, "_ value: UnsafeRawPointer", null, "value");

    [Fact]
    public void OptionalOpaque_ReadsPointerOptional()
    {
        var inner = Named("TestModule.MyComplexEnum");
        var d = Describe(Optional(inner));
        Assert.Equal(CdeclParamCategory.OptionalOpaque, d.Category);
        Assert.Equal("_ value: UnsafeRawPointer", d.CdeclParam);
        Assert.Equal("valueVal", d.CallArg);
        Assert.Contains("assumingMemoryBound(to: UnsafeMutableRawPointer?.self)", d.Reconstruction);
    }

    [Fact]
    public void ObjCBridgeableContainer_BridgesCollection()
    {
        var spec = Named("Swift.Array", Named("TestModule.MyBridgeableValue"));
        var expected = RenderMQ(spec);
        AssertDescriptor(Describe(spec), CdeclParamCategory.ObjCBridgeableContainer,
            "_ value: UnsafeMutableRawPointer",
            $"let valueVal: {expected} = Unmanaged<AnyObject>.fromOpaque(value).takeUnretainedValue() as! {expected}",
            "valueVal");
    }

    [Fact]
    public void OptionalObjCBridgeableContainer_BridgesNullableCollection()
    {
        var inner = Named("Swift.Array", Named("TestModule.MyBridgeableValue"));
        var expected = RenderMQ(inner);
        AssertDescriptor(Describe(Optional(inner)), CdeclParamCategory.OptionalObjCBridgeableContainer,
            "_ value: UnsafeMutableRawPointer?",
            $"let valueVal: {expected}? = value.map {{ Unmanaged<AnyObject>.fromOpaque($0).takeUnretainedValue() as! {expected} }}",
            "valueVal");
    }

    [Fact]
    public void GenericContainer_ReadsThroughPointer()
    {
        var spec = Named("Swift.Array", Named("Swift.Int"));
        var expected = RenderMQ(spec);
        AssertDescriptor(Describe(spec), CdeclParamCategory.GenericContainer,
            "_ value: UnsafeRawPointer",
            $"let valueVal = value.assumingMemoryBound(to: {expected}.self).pointee", "valueVal");
    }

    [Fact]
    public void Date_LowersToDouble()
        => AssertDescriptor(Describe(Named("Foundation.Date")), CdeclParamCategory.Date,
            "_ value: Double",
            "let valueVal = Foundation.Date(timeIntervalSinceReferenceDate: value)", "valueVal");

    [Fact]
    public void Data_DecomposesIntoTwoWords()
        => AssertDescriptor(Describe(Named("Foundation.Data")), CdeclParamCategory.Data,
            "_ _dW0_value: Int, _ _dW1_value: Int",
            "let valueVal = unsafeBitCast((_dW0_value, _dW1_value), to: Foundation.Data.self)", "valueVal");

    /// <summary>
    /// A frozen Foundation value struct that also bridges to an ObjC class must NOT reach the
    /// by-value system-frozen arm: <c>@_cdecl</c> would lower the parameter to a bridged object
    /// pointer rather than to the struct's value bytes, so the caller's 16 bytes would be read as
    /// a pointer. It takes the indirect pointer shape instead, which is also the exact inverse of
    /// the return side's verbatim byte reinterpretation.
    /// </summary>
    [Fact]
    public void ObjCBridgedValueStruct_ReadsThroughPointer()
    {
        var spec = Named("Foundation.UUID");
        AssertDescriptor(Describe(spec), CdeclParamCategory.ObjCBridgedValueStruct,
            "_ value: UnsafeRawPointer",
            $"let valueVal = value.assumingMemoryBound(to: {RenderMQ(spec)}.self).pointee", "valueVal");
    }

    /// <summary>
    /// The same lowering holds for every by-value parameter shape the wrapper emitters produce —
    /// plain, and each ownership specifier — so no wrapper kind is left declaring a bare bridgeable
    /// struct parameter that would silently bridge.
    /// </summary>
    [Theory]
    [InlineData(ParameterOwnership.Default)]
    [InlineData(ParameterOwnership.Shared)]
    [InlineData(ParameterOwnership.Owned)]
    public void ObjCBridgedValueStruct_NeverDeclaresABareStructParameter(ParameterOwnership ownership)
    {
        var d = Describe(Named("Foundation.UUID"), ownership: ownership);
        Assert.Equal(CdeclParamCategory.ObjCBridgedValueStruct, d.Category);
        Assert.Equal("_ value: UnsafeRawPointer", d.CdeclParam);
        Assert.DoesNotContain("UUID", d.CdeclParam);
        Assert.Equal("valueVal", d.CallArg);
    }

    /// <summary>
    /// The wedge is narrow: an ordinary system frozen struct that does not bridge to an ObjC class
    /// keeps the by-value lowering it has always had.
    /// </summary>
    [Fact]
    public void SystemFrozenStruct_WithoutObjCBridge_StaysByValue()
        => Assert.Equal(CdeclParamCategory.SystemFrozenStruct,
            Describe(Named("CoreGraphics.CGRect")).Category);

    [Fact]
    public void String_TwoWord_DecomposesIntoTwoWords()
        => AssertDescriptor(Describe(Named("Swift.String")), CdeclParamCategory.String,
            "_ _sW0_value: Int, _ _sW1_value: Int",
            "let valueVal = unsafeBitCast((_sW0_value, _sW1_value), to: String.self)", "valueVal");

    [Fact]
    public void String_Utf8_DecomposesIntoPtrAndLen()
        => AssertDescriptor(Describe(Named("Swift.String"), useUtf8: true), CdeclParamCategory.String,
            "_ valueUtf8Ptr: UnsafePointer<UInt8>, _ valueUtf8Len: Int",
            "let valueVal = String(bytes: UnsafeBufferPointer(start: valueUtf8Ptr, count: valueUtf8Len), encoding: .utf8)!",
            "valueVal");

    [Fact]
    public void ClassPointer_ReconstructsViaUnmanaged()
        => AssertDescriptor(Describe(Named("TestModule.MyClass")), CdeclParamCategory.ClassPointer,
            "_ value: UnsafeMutableRawPointer",
            "let valueVal = Unmanaged<TestModule.MyClass>.fromOpaque(value).takeUnretainedValue()", "valueVal");

    [Fact]
    public void ObjCBridgedClassPointer_CastsAnyObject()
        => AssertDescriptor(Describe(Named("TestModule.MyObjCClass")), CdeclParamCategory.ObjCBridgedClassPointer,
            "_ value: UnsafeMutableRawPointer",
            "let valueVal = Unmanaged<AnyObject>.fromOpaque(value).takeUnretainedValue() as! TestModule.MyObjCClass",
            "valueVal");

    [Fact]
    public void ObjCBridgeableValue_CastsAnyObject()
        => AssertDescriptor(Describe(Named("TestModule.MyBridgeableValue")), CdeclParamCategory.ObjCBridgeableValue,
            "_ value: UnsafeMutableRawPointer",
            "let valueVal = Unmanaged<AnyObject>.fromOpaque(value).takeUnretainedValue() as! TestModule.MyBridgeableValue",
            "valueVal");

    [Fact]
    public void SimpleEnum_ReconstructsViaRawValue()
        => AssertDescriptor(Describe(Named("TestModule.MyEnum")), CdeclParamCategory.SimpleEnum,
            "_ value: Int",
            "guard let valueVal = TestModule.MyEnum(rawValue: value) else { preconditionFailure(\"[SwiftBindings] Invalid raw value \\(value) for TestModule.MyEnum\") }",
            "valueVal");

    [Fact]
    public void OptionSet_ReconstructsNonFailably()
        // An imported ObjC NS_OPTIONS bitmask carries SimpleEnum | OptionSet. Its Swift OptionSet
        // init(rawValue:) is NON-failable and returns a non-optional, so the reconstruction is a
        // direct `let` bind — NOT the failable `guard let … else { preconditionFailure }` form the
        // plain RawRepresentable enum (MyEnum) uses. Same SimpleEnum category and cdecl param shape.
        => AssertDescriptor(Describe(Named("TestModule.MyOptionSet")), CdeclParamCategory.SimpleEnum,
            "_ value: Int",
            "let valueVal = TestModule.MyOptionSet(rawValue: value)",
            "valueVal");

    [Fact]
    public void OptionSet_UnsignedNativeWidthRaw_ReconstructsNonFailably()
        // The realistic ObjC NS_OPTIONS shape backs on NSUInteger → native-width Swift UInt.
        // The cdecl scalar is UInt and the reconstruction stays the non-failable OptionSet form —
        // the C# [Flags] companion's ulong underlying transports the raw bits across the boundary.
        => AssertDescriptor(Describe(Named("TestModule.MyOptionSetU")), CdeclParamCategory.SimpleEnum,
            "_ value: UInt",
            "let valueVal = TestModule.MyOptionSetU(rawValue: value)",
            "valueVal");

    [Fact]
    public void ComplexEnum_ReadsThroughPointer()
        => AssertDescriptor(Describe(Named("TestModule.MyComplexEnum")), CdeclParamCategory.ComplexEnum,
            "_ value: UnsafeRawPointer",
            "let valueVal = value.assumingMemoryBound(to: TestModule.MyComplexEnum.self).pointee", "valueVal");

    [Fact]
    public void NonFrozenStruct_ReadsThroughPointer()
        => AssertDescriptor(Describe(Named("TestModule.MyNonFrozenStruct")), CdeclParamCategory.NonFrozenStruct,
            "_ value: UnsafeRawPointer",
            "let valueVal = value.assumingMemoryBound(to: TestModule.MyNonFrozenStruct.self).pointee", "valueVal");

    [Fact]
    public void SystemFrozenStruct_PassesByValue()
        => AssertDescriptor(Describe(Named("CoreGraphics.CGRect")), CdeclParamCategory.SystemFrozenStruct,
            "_ value: CGRect", null, "value");

    [Fact]
    public void CustomFrozenStruct_ReadsThroughPointer()
        => AssertDescriptor(Describe(Named("TestModule.MyStruct")), CdeclParamCategory.CustomFrozenStruct,
            "_ value: UnsafeRawPointer",
            "let valueVal = value.assumingMemoryBound(to: TestModule.MyStruct.self).pointee", "valueVal");

    [Fact]
    public void RawBufferPointer_SplitsIntoPtrAndLen()
        => AssertDescriptor(Describe(Named("Swift.UnsafeRawBufferPointer")), CdeclParamCategory.RawBufferPointer,
            "_ valuePtr: UnsafeRawPointer?, _ valueLen: Int",
            "let valueVal = UnsafeRawBufferPointer(start: valuePtr, count: valueLen)", "valueVal");

    [Fact]
    public void RawBufferPointer_Mutable_SplitsIntoPtrAndLen()
        => AssertDescriptor(Describe(Named("Swift.UnsafeMutableRawBufferPointer")), CdeclParamCategory.RawBufferPointer,
            "_ valuePtr: UnsafeMutableRawPointer?, _ valueLen: Int",
            "let valueVal = UnsafeMutableRawBufferPointer(start: valuePtr, count: valueLen)", "valueVal");

    [Fact]
    public void NonCopyableBorrow_InlineBorrowNoCopy()
        => AssertDescriptor(Describe(Named("TestModule.MyNonCopyable"), ownership: ParameterOwnership.Shared),
            CdeclParamCategory.NonCopyableBorrow, "_ value: UnsafeRawPointer", null,
            "value.assumingMemoryBound(to: TestModule.MyNonCopyable.self).pointee");

    [Fact]
    public void NonCopyableConsume_MovesOutOfBuffer()
        => AssertDescriptor(Describe(Named("TestModule.MyNonCopyable"), ownership: ParameterOwnership.Owned),
            CdeclParamCategory.NonCopyableConsume, "_ value: UnsafeMutableRawPointer",
            "let valueVal = value.assumingMemoryBound(to: TestModule.MyNonCopyable.self).move()", "valueVal");

    [Fact]
    public void Fallback_ReadsThroughPointer()
    {
        // A named type with no TypeRecord, not a container/optional/primitive — the last-resort arm.
        var spec = Named("TestModule.Unknown");
        AssertDescriptor(Describe(spec), CdeclParamCategory.Fallback,
            "_ value: UnsafeRawPointer",
            $"let valueVal = value.assumingMemoryBound(to: {RenderMQ(spec)}.self).pointee", "valueVal");
    }

    // ----- inout (folded into Describe via Category=Inout) ----------------------------------

    [Fact]
    public void Inout_Bool_LowersWithWriteBack()
    {
        var (db, module) = NewFixture();
        var d = CdeclParamMapper.Describe(Arg(Named("Swift.Bool"), module, ParameterOwnership.InOut), Label,
            Env(db, module), isInout: true);
        Assert.Equal(CdeclParamCategory.Inout, d.Category);
        Assert.Equal("_ value: UnsafeMutableRawPointer", d.CdeclParam);
        Assert.Equal("var valueVal: Bool = value.assumingMemoryBound(to: Int8.self).pointee != 0", d.Reconstruction);
        Assert.Equal("&valueVal", d.CallArg);
        Assert.Equal("value.assumingMemoryBound(to: Int8.self).pointee = valueVal ? 1 : 0", d.WriteBack);
    }

    [Fact]
    public void Inout_NonBool_LowersWithWriteBack()
    {
        var (db, module) = NewFixture();
        var d = CdeclParamMapper.Describe(Arg(Named("TestModule.MyStruct"), module, ParameterOwnership.InOut), Label,
            Env(db, module), isInout: true);
        Assert.Equal(CdeclParamCategory.Inout, d.Category);
        Assert.Equal("_ value: UnsafeMutableRawPointer", d.CdeclParam);
        Assert.Equal("var valueVal = value.assumingMemoryBound(to: TestModule.MyStruct.self).pointee", d.Reconstruction);
        Assert.Equal("&valueVal", d.CallArg);
        Assert.Equal("value.assumingMemoryBound(to: TestModule.MyStruct.self).pointee = valueVal", d.WriteBack);
    }

    [Fact]
    public void Inout_Primitive_LowersWithWriteBack()
    {
        var (db, module) = NewFixture();
        var spec = Named("Swift.Int32");
        // Assemble the expected element type through the same helper the non-Bool inout arm uses.
        var t = CdeclParamMapper.RenderModuleQualifiedSwiftTypeWithExistentialAny(spec, db);
        var d = CdeclParamMapper.Describe(Arg(spec, module, ParameterOwnership.InOut), Label,
            Env(db, module), isInout: true);
        Assert.Equal(CdeclParamCategory.Inout, d.Category);
        Assert.Equal("_ value: UnsafeMutableRawPointer", d.CdeclParam);
        Assert.Equal($"var valueVal = value.assumingMemoryBound(to: {t}.self).pointee", d.Reconstruction);
        Assert.Equal("&valueVal", d.CallArg);
        Assert.Equal($"value.assumingMemoryBound(to: {t}.self).pointee = valueVal", d.WriteBack);
    }

    // ----- shim/producer parity (the refactor's load-bearing contract) ---------------------
    // The basic cases pin that the shims project Describe's fields; the *forwards* cases pick inputs
    // where a flag changes the lowering, so a shim that dropped or mis-forwarded a flag would diverge
    // from Describe called with the same flag (and from the flag's off form — asserted via NotEqual).

    [Fact]
    public void Map_IsPureProjectionOfDescribe()
    {
        var (db, module) = NewFixture();
        var env = Env(db, module);
        var arg = Arg(Named("TestModule.MyClass"), module, ParameterOwnership.Default);

        var d = CdeclParamMapper.Describe(arg, Label, env);
        var m = CdeclParamMapper.Map(arg, Label, env);

        Assert.Equal(d.CdeclParam, m.cdeclParam);
        Assert.Equal(d.Reconstruction, m.reconstruction);
        Assert.Equal(d.CallArg, m.callArg);
    }

    [Fact]
    public void Map_ForwardsUseUtf8StringsFlag()
    {
        var (db, module) = NewFixture();
        var env = Env(db, module);
        var arg = Arg(Named("Swift.String"), module, ParameterOwnership.Default);

        var dUtf8 = CdeclParamMapper.Describe(arg, Label, env, useUtf8Strings: true);
        var dTwoWord = CdeclParamMapper.Describe(arg, Label, env, useUtf8Strings: false);
        var mUtf8 = CdeclParamMapper.Map(arg, Label, env, useUtf8Strings: true);

        Assert.NotEqual(dTwoWord.CdeclParam, dUtf8.CdeclParam); // the flag actually changes the lowering
        Assert.Equal(dUtf8.CdeclParam, mUtf8.cdeclParam);
        Assert.Equal(dUtf8.Reconstruction, mUtf8.reconstruction);
        Assert.Equal(dUtf8.CallArg, mUtf8.callArg);
    }

    [Fact]
    public void Map_ForwardsOmitLabelsFlag()
    {
        var (db, module) = NewFixture();
        var env = Env(db, module);
        var arg = Arg(Optional(Named("Swift.Int32")), module, ParameterOwnership.Default);

        var dOmit = CdeclParamMapper.Describe(arg, Label, env, omitLabels: true);
        var dKeep = CdeclParamMapper.Describe(arg, Label, env, omitLabels: false);
        var mOmit = CdeclParamMapper.Map(arg, Label, env, omitLabels: true);

        Assert.NotEqual(dKeep.Reconstruction, dOmit.Reconstruction); // omitLabels actually changes the lowering
        Assert.Equal(dOmit.CdeclParam, mOmit.cdeclParam);
        Assert.Equal(dOmit.Reconstruction, mOmit.reconstruction);
        Assert.Equal(dOmit.CallArg, mOmit.callArg);
    }

    [Fact]
    public void MapInout_IsPureProjectionOfDescribeInout()
    {
        var (db, module) = NewFixture();
        var env = Env(db, module);
        var arg = Arg(Named("TestModule.MyStruct"), module, ParameterOwnership.InOut);

        var d = CdeclParamMapper.Describe(arg, Label, env, isInout: true);
        var m = CdeclParamMapper.MapInout(arg, Label, env);

        Assert.Equal(CdeclParamCategory.Inout, d.Category);
        Assert.Equal(d.CdeclParam, m.cdeclParam);
        Assert.Equal(d.Reconstruction, m.reconstruction);
        Assert.Equal(d.CallArg, m.callArg);
        Assert.Equal(d.WriteBack, m.writeBack);
    }

    [Fact]
    public void MapInout_ForwardsReservedSiblings()
    {
        var (db, module) = NewFixture();
        var env = Env(db, module);
        // The label "tag" spells a reserved synthetic, so it escapes to "__tag"; a sibling already
        // named "__tag" bumps the escape to "__tag2". The escaped binding appears in the cdeclParam/
        // reconstruction/writeBack text, so a shim that dropped reservedSiblings would diverge from
        // Describe given the same set.
        var arg = Arg(Named("TestModule.MyStruct"), module, ParameterOwnership.InOut);
        var siblings = new HashSet<string>(StringComparer.Ordinal) { "__tag" };

        var dWith = CdeclParamMapper.Describe(arg, "tag", env, isInout: true, reservedSiblings: siblings);
        var dWithout = CdeclParamMapper.Describe(arg, "tag", env, isInout: true);
        var m = CdeclParamMapper.MapInout(arg, "tag", env, reservedSiblings: siblings);

        Assert.NotEqual(dWithout.CdeclParam, dWith.CdeclParam); // the sibling set actually changes the escaped name
        Assert.Equal(dWith.CdeclParam, m.cdeclParam);
        Assert.Equal(dWith.Reconstruction, m.reconstruction);
        Assert.Equal(dWith.CallArg, m.callArg);
        Assert.Equal(dWith.WriteBack, m.writeBack);
    }
}
