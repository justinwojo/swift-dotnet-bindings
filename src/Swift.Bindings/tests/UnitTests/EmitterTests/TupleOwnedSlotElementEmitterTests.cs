// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A @_cdecl tuple return is written whole into one buffer the binding frees raw after the
/// elements are read, so an inline element occupies a slot the binding owns but will not keep.
/// A non-frozen struct's wrapper adopts the payload pointer it is given, so wrapping the slot
/// address in place leaves the wrapper reading freed memory. Such an element has to be moved
/// out of its slot into storage of its own. The async tuple lane hands each element over in a
/// separate allocation the wrapper may adopt, so it keeps the plain read.
/// </summary>
public class TupleOwnedSlotElementEmitterTests
{
    [Fact]
    public void OwnedInlineSlot_NonFrozenStruct_MovesOutOfSlot()
    {
        var emitter = CreateWrapperEmitter();
        var label = new NamedTypeSpec("TestModule.Label");

        var code = emitter.GetTupleElementMarshalCode(label, "_raw0", "_te0", "TestModule.Label", ownedInlineSlot: true);

        Assert.NotNull(code);
        Assert.Contains("MarshalMovedValueFromSlot<TestModule.Label>((void*)_raw0", code!);
        Assert.Contains("GetTypeMetadataOrThrow<TestModule.Label>()", code);
        Assert.DoesNotContain("MarshalFromSwift<TestModule.Label>(_raw0)", code);
    }

    [Fact]
    public void SeparateAllocation_NonFrozenStruct_KeepsPlainRead()
    {
        var emitter = CreateWrapperEmitter();
        var label = new NamedTypeSpec("TestModule.Label");

        var code = emitter.GetTupleElementMarshalCode(label, "rawItem0", "item0", "TestModule.Label");

        Assert.NotNull(code);
        Assert.Contains("MarshalFromSwift<TestModule.Label>(rawItem0)", code!);
        Assert.DoesNotContain("MarshalMovedValueFromSlot", code);
    }

    [Fact]
    public void OwnedInlineSlot_BlittablePrimitive_ReadByValue()
    {
        var emitter = CreateWrapperEmitter();
        var intSpec = new NamedTypeSpec("Swift.Int");

        var code = emitter.GetTupleElementMarshalCode(intSpec, "_raw1", "_te1", "long", ownedInlineSlot: true);

        Assert.NotNull(code);
        Assert.DoesNotContain("MarshalMovedValueFromSlot", code!);
        Assert.DoesNotContain("MarshalFromSwift", code);
    }

    private static WrapperEmitter CreateWrapperEmitter()
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var methodDecl = new MethodDecl
        {
            Name = "make",
            MangledName = "$s10TestModule4makeSiyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = moduleDecl,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };

        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");
        var labelName = SwiftTypeName.FromModuleQualifiedName("TestModule.Label");
        module.RegisterType(labelName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Label"),
            SwiftTypeName = labelName,
            MetadataAccessor = "$s10TestModule5LabelVMa",
            Flags = TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Struct
        });
        typeDatabase.AddModuleDatabase(module);

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        var intTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int");
        swiftModule.RegisterType(intTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
            SwiftTypeName = intTypeName,
            MetadataAccessor = "$sSiMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        typeDatabase.AddModuleDatabase(swiftModule);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = (MethodEnvironment)handler.Marshal(methodDecl, typeDatabase);
        return new WrapperEmitter(env, new SignatureHandler(env));
    }
}
