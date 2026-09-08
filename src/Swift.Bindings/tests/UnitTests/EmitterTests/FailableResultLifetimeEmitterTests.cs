// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

public class FailableResultLifetimeEmitterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryIndirectResult_MarksSuccessBeforeErrorConversion(bool throws)
    {
        var output = Emit(cdecl: true, throws, typedThrows: false, isClass: false, frozen: false, constructor: false);
        var call = output.IndexOf("PInvoke_make_", StringComparison.Ordinal);
        var condition = throws ? "errorPtr == IntPtr.Zero" : "true";
        var marker = output.IndexOf($"_cdeclResultLive = {condition};", StringComparison.Ordinal);
        Assert.True(call >= 0 && marker > call, output);
        Assert.Equal(marker, output.LastIndexOf($"_cdeclResultLive = {condition};", StringComparison.Ordinal));
        if (throws)
            Assert.True(marker < output.IndexOf("SwiftMarshal.ThrowSwiftError", StringComparison.Ordinal), output);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void IndirectFactory_DestroysOnlyInitializedOptional(bool cdecl, bool throws, bool typedThrows)
    {
        var output = Emit(cdecl, throws, typedThrows, isClass: false, frozen: false);
        int declaration = output.IndexOf("bool __failableResultLive = false;", StringComparison.Ordinal);
        int call = output.IndexOf("PInvoke_init_", StringComparison.Ordinal);
        string condition = !throws ? "true" : cdecl ? "errorPtr == IntPtr.Zero" : "swiftError.Value == null";
        int marker = output.IndexOf($"__failableResultLive = {condition};", StringComparison.Ordinal);
        int tag = output.IndexOf("uint tag =", StringComparison.Ordinal);
        int destroy = output.IndexOf("optionalMetadata.ValueWitnessTable->Destroy(resultBuffer, optionalMetadata);", StringComparison.Ordinal);
        int free = output.IndexOf("NativeMemory.Free(resultBuffer);", StringComparison.Ordinal);
        Assert.True(declaration >= 0 && declaration < call && call < marker && marker < tag && tag < destroy && destroy < free, output);
        Assert.Contains("if (__failableResultLive)", output.Substring(tag, destroy - tag));
        Assert.Contains("InitializeWithCopy", output);
        Assert.DoesNotContain("InitializeWithTake", output);
        if (throws)
        {
            var error = output.IndexOf(cdecl ? "if (errorPtr != IntPtr.Zero)" : "if (swiftError.Value != null)", marker, StringComparison.Ordinal);
            Assert.True(marker < error && error < tag, output);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClassCdeclFactory_HasNoOptionalStorage(bool throws)
    {
        var output = Emit(cdecl: true, throws, typedThrows: false, isClass: true, frozen: false);
        Assert.Contains("TryCreate(", output);
        Assert.Contains("== IntPtr.Zero", output);
        Assert.DoesNotContain("__failableResultLive", output);
        Assert.DoesNotContain("optionalMetadata", output);
        Assert.DoesNotContain("resultBuffer", output);
    }

    [Fact]
    public void FrozenPodCdeclFactory_HasFreeOnlyCleanup()
    {
        var output = Emit(cdecl: true, throws: true, typedThrows: false, isClass: false, frozen: true);
        Assert.Contains("NativeMemory.Free(resultBuffer);", output);
        Assert.DoesNotContain("__failableResultLive", output);
        Assert.DoesNotContain("->Destroy", output);
    }

    private static string Emit(bool cdecl, bool throws, bool typedThrows, bool isClass, bool frozen, bool constructor = true)
    {
        var module = new ModuleDecl { Name = "TestModule", Properties = new(), Methods = new(),
            Types = new(), Dependencies = new(), Protocols = new(), ParentDecl = null, ModuleDecl = null };
        var name = SwiftTypeName.FromModuleQualifiedName("TestModule.Value");
        TypeDecl parent = isClass
            ? new ClassDecl { Name = "Value", SwiftTypeName = name, MangledName = "$s10TestModule5ValueCN",
                Properties = new(), Methods = new(), Types = new(), Operators = new(), Subscripts = new(),
                GenericParameters = new(), Conformances = new(), ParentDecl = module, ModuleDecl = module }
            : new StructDecl { Name = "Value", SwiftTypeName = name, MangledName = "$s10TestModule5ValueVN",
                Properties = new(), Methods = new(), Types = new(), Operators = new(), Subscripts = new(),
                GenericParameters = new(), Conformances = new(), ParentDecl = module, ModuleDecl = module,
                IsFrozen = frozen, MetadataAccessor = "$s10TestModule5ValueVMa" };
        module.Types.Add(parent);
        var database = new TypeDatabase();
        var types = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        types.RegisterType(name, new TypeRecord { SwiftTypeName = name,
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Value"),
            MetadataAccessor = "$s10TestModule5ValueVMa", Kind = isClass ? TypeRecordKind.Class : TypeRecordKind.Struct,
            Flags = frozen ? TypeRecordFlags.Frozen : TypeRecordFlags.RequiresMemoryManagement });
        var errorName = SwiftTypeName.FromModuleQualifiedName("TestModule.Failure");
        types.RegisterType(errorName, new TypeRecord { SwiftTypeName = errorName,
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Failure"),
            MetadataAccessor = "$s10TestModule7FailureOMa", Kind = TypeRecordKind.Enum,
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum });
        database.AddModuleDatabase(types);
        var method = new MethodDecl { Name = constructor ? "init" : "make", MangledName = "$s10TestModule5ValueVACycfC",
            MethodType = MethodType.Static, IsConstructor = constructor, IsFailable = constructor, Throws = throws,
            ThrownErrorType = typedThrows ? new NamedTypeSpec("TestModule.Failure") : null,
            GenericParameters = new(), ParentDecl = parent, ModuleDecl = module, IsAsync = false,
            IsSynthesizedAccessor = false, CSSignature = new List<ArgumentDecl> {
                new() { Name = "", PrivateName = "", SwiftTypeSpec = new NamedTypeSpec("TestModule.Value"),
                    ParentDecl = null, ModuleDecl = module, IsInOut = false, IsGeneric = false } } };
        if (constructor) method.UsesCdeclConstructorWrapper = cdecl;
        else method.UsesCdeclMethodWrapper = cdecl;
        parent.Methods.Add(method);
        var output = new StringWriter();
        var environment = new MethodEnvironment(method, database);
        var conductor = new Conductor(new NullLoggerFactory());
        if (constructor)
            new ConstructorHandler(new NullLogger<ConstructorHandler>(), new HashSet<string>()).Emit(
                new CSharpWriter(output), new SwiftWriter(new StringWriter()), environment, conductor, TypeHandlerContext.Empty);
        else
            new MethodHandler(new NullLogger<MethodHandler>()).Emit(
                new CSharpWriter(output), new SwiftWriter(new StringWriter()), environment, conductor, TypeHandlerContext.Empty);
        return output.ToString();
    }
}
