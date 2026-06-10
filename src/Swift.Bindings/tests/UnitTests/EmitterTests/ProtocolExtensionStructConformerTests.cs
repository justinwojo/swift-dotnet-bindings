// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for struct conformer support in protocol extension methods.
/// Exercises InjectExtensionMethods end-to-end for StructDecl conformers:
/// conformance collection, Swift wrapper self-conversion, struct Self-return,
/// and gate enforcement (frozen structs excluded).
/// </summary>
public class ProtocolExtensionStructConformerTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    // ─── Non-frozen struct conformer: method injected ─────────────────

    [Fact]
    public void NonFrozenStruct_VoidMethod_Injected()
    {
        var (moduleDecl, structDecl, typeDatabase) = CreateStructSetup("TestModule", "MyStruct", "TestProtocol", frozen: false);
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("doWork", "public func doWork()"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(structDecl.Methods);
        Assert.Equal("doWork", structDecl.Methods[0].Name);
    }

    // ─── Frozen struct conformer: method NOT injected ─────────────────

    [Fact]
    public void FrozenStruct_VoidMethod_NotInjected()
    {
        var (moduleDecl, structDecl, typeDatabase) = CreateStructSetup("TestModule", "FrozenStruct", "TestProtocol", frozen: true);
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("doWork", "public func doWork()"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Empty(structDecl.Methods);
    }

    // ─── Swift wrapper uses assumingMemoryBound for struct self ───────

    [Fact]
    public void NonFrozenStruct_SwiftWrapper_UsesAssumingMemoryBound()
    {
        var (moduleDecl, structDecl, typeDatabase) = CreateStructSetup("TestModule", "MyStruct", "TestProtocol", frozen: false);
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("doWork", "public func doWork()"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        Assert.Contains("assumingMemoryBound(to: TestModule.MyStruct.self).pointee", wrapperLines);
        // Must NOT use Unmanaged (class-only ABI)
        Assert.DoesNotContain("Unmanaged", wrapperLines);
    }

    // ─── Swift wrapper uses var (not let) for struct self ─────────────

    [Fact]
    public void NonFrozenStruct_SwiftWrapper_UsesVarForSelf()
    {
        var (moduleDecl, structDecl, typeDatabase) = CreateStructSetup("TestModule", "MyStruct", "TestProtocol", frozen: false);
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("doWork", "public func doWork()"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        // Struct self must be 'var' (value types may need mutation via protocol extension)
        Assert.Contains("var instance =", wrapperLines);
        Assert.DoesNotContain("let instance =", wrapperLines);
    }

    // ─── Struct Self-return uses buffer allocation ────────────────────

    [Fact]
    public void NonFrozenStruct_SelfReturn_UsesBufferAllocation()
    {
        var (moduleDecl, structDecl, typeDatabase) = CreateStructSetup("TestModule", "MyStruct", "TestProtocol", frozen: false);
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateSelfReturningExtMethod("chain", "public func chain() -> Self"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(structDecl.Methods);
        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        // Struct Self-return must use buffer allocation, not Unmanaged.passRetained
        Assert.Contains("UnsafeMutableRawPointer.allocate", wrapperLines);
        Assert.Contains("initializeMemory(as: TestModule.MyStruct.self", wrapperLines);
        Assert.Contains("return buf", wrapperLines);
        Assert.DoesNotContain("Unmanaged.passRetained", wrapperLines);
    }

    // ─── Struct Self-return wrapper returns UnsafeMutableRawPointer ───

    [Fact]
    public void NonFrozenStruct_SelfReturn_ReturnsPointer()
    {
        var (moduleDecl, structDecl, typeDatabase) = CreateStructSetup("TestModule", "MyStruct", "TestProtocol", frozen: false);
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateSelfReturningExtMethod("chain", "public func chain() -> Self"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        Assert.Contains("-> UnsafeMutableRawPointer", wrapperLines);
    }

    // ─── Synthetic MethodDecl has correct parent ──────────────────────

    [Fact]
    public void NonFrozenStruct_SyntheticMethod_HasStructParent()
    {
        var (moduleDecl, structDecl, typeDatabase) = CreateStructSetup("TestModule", "MyStruct", "TestProtocol", frozen: false);
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("doWork", "public func doWork()"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(structDecl.Methods);
        Assert.Same(structDecl, structDecl.Methods[0].ParentDecl);
        Assert.True(structDecl.Methods[0].UsesWrapperLibrary);
        Assert.True(structDecl.Methods[0].UsesFreeFunctionWrapper);
        Assert.True(structDecl.Methods[0].IsProtocolExtensionMethod);
    }

    // ─── Generic struct conformer ─────────────────────────────────────

    [Fact]
    public void GenericNonFrozenStruct_VoidMethod_Injected()
    {
        var (moduleDecl, structDecl, typeDatabase) = CreateGenericStructSetup("TestModule", "Container", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("process", "public func process()"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(structDecl.Methods);
        Assert.Equal("process", structDecl.Methods[0].Name);
    }

    [Fact]
    public void GenericNonFrozenStruct_SwiftWrapper_UsesAssumingMemoryBound()
    {
        var (moduleDecl, structDecl, typeDatabase) = CreateGenericStructSetup("TestModule", "Container", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("process", "public func process()"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        // Generic struct uses assumingMemoryBound with parameterized type
        Assert.Contains("assumingMemoryBound(to: TestModule.Container<Element>.self).pointee", wrapperLines);
        Assert.DoesNotContain("Unmanaged", wrapperLines);
    }

    [Fact]
    public void GenericNonFrozenStruct_SelfReturn_UsesBufferWithGenericType()
    {
        var (moduleDecl, structDecl, typeDatabase) = CreateGenericStructSetup("TestModule", "Container", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateSelfReturningExtMethod("chain", "public func chain() -> Self"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(structDecl.Methods);
        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        Assert.Contains("MemoryLayout<TestModule.Container<Element>>.size", wrapperLines);
        Assert.Contains("initializeMemory(as: TestModule.Container<Element>.self", wrapperLines);
    }

    // ─── Class and struct both collected ───────────────────────────────

    [Fact]
    public void MixedConformers_BothClassAndStruct_BothInjected()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.TestProtocol"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ITestProtocol"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.TestProtocol"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
                MetadataAccessor = "$s10TestModule7MyClassCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyStruct"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyStruct"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyStruct"),
                MetadataAccessor = "$s10TestModule8MyStructVMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(testModule);

        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("MyClass", moduleDecl);
        classDecl.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            SwiftTypeName.FromModuleQualifiedName("TestModule.TestProtocol"), ""));

        var structDecl = CreateStructDecl("MyStruct", moduleDecl, frozen: false);
        structDecl.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyStruct"),
            SwiftTypeName.FromModuleQualifiedName("TestModule.TestProtocol"), ""));

        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("doWork", "public func doWork()"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        // Both class and struct should have the method injected
        Assert.Single(classDecl.Methods);
        Assert.Single(structDecl.Methods);

        // Verify wrapper has both class and struct paths
        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        Assert.Contains("Unmanaged<TestModule.MyClass>", wrapperLines); // Class self
        Assert.Contains("assumingMemoryBound(to: TestModule.MyStruct.self)", wrapperLines); // Struct self
    }

    // ─── Class conformer Self-return still uses Unmanaged ─────────────

    [Fact]
    public void ClassConformer_SelfReturn_StillUsesUnmanaged()
    {
        var (moduleDecl, conformingType, typeDatabase) = CreateClassSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateSelfReturningExtMethod("chain", "public func chain() -> Self"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(conformingType.Methods);
        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        Assert.Contains("Unmanaged.passRetained(result as AnyObject).toOpaque()", wrapperLines);
        Assert.DoesNotContain("UnsafeMutableRawPointer.allocate", wrapperLines);
    }

    // ─── Self-return synthetic method has correct return type ─────────

    [Fact]
    public void NonFrozenStruct_SelfReturn_SyntheticMethodReturnType()
    {
        var (moduleDecl, structDecl, typeDatabase) = CreateStructSetup("TestModule", "MyStruct", "TestProtocol", frozen: false);
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateSelfReturningExtMethod("chain", "public func chain() -> Self"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(structDecl.Methods);
        var returnTypeSpec = structDecl.Methods[0].CSSignature[0].SwiftTypeSpec;
        // Self should be resolved to the concrete struct type
        Assert.IsType<NamedTypeSpec>(returnTypeSpec);
        Assert.Equal("TestModule.MyStruct", ((NamedTypeSpec)returnTypeSpec).Name);
    }

    // ─── Mutating method write-back tests ──────────────────────────────

    [Fact]
    public void NonFrozenStruct_MutatingVoidMethod_EmitsWriteBack()
    {
        var (moduleDecl, structDecl, typeDatabase) = CreateStructSetup("TestModule", "MyStruct", "TestProtocol", frozen: false);
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateMutatingExtMethod("mutate", "public mutating func mutate()"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(structDecl.Methods);
        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        // Must write back mutated value to the original pointer
        Assert.Contains("self_.assumingMemoryBound(to: TestModule.MyStruct.self).pointee = instance", wrapperLines);
    }

    [Fact]
    public void NonFrozenStruct_NonMutatingVoidMethod_NoWriteBack()
    {
        var (moduleDecl, structDecl, typeDatabase) = CreateStructSetup("TestModule", "MyStruct", "TestProtocol", frozen: false);
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("doWork", "public func doWork()"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        // Non-mutating: must NOT write back
        Assert.DoesNotContain(".pointee = instance", wrapperLines);
    }

    [Fact]
    public void NonFrozenStruct_MutatingSelfReturn_EmitsWriteBackBeforeBufferAlloc()
    {
        var (moduleDecl, structDecl, typeDatabase) = CreateStructSetup("TestModule", "MyStruct", "TestProtocol", frozen: false);
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateMutatingSelfReturningExtMethod("mutateAndReturn", "public mutating func mutateAndReturn() -> Self"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Single(structDecl.Methods);
        var wrapperLines = ctx.ProtocolExtSwiftWrapperLines.ToList();
        var writeBackIdx = wrapperLines.FindIndex(l => l.Contains(".pointee = instance"));
        var allocIdx = wrapperLines.FindIndex(l => l.Contains("UnsafeMutableRawPointer.allocate"));
        // Write-back must happen BEFORE buffer allocation
        Assert.True(writeBackIdx >= 0, "Write-back line must exist");
        Assert.True(allocIdx >= 0, "Buffer allocation line must exist");
        Assert.True(writeBackIdx < allocIdx, "Write-back must precede buffer allocation");
    }

    [Fact]
    public void ClassConformer_MutatingMethod_NoWriteBack()
    {
        var (moduleDecl, classDecl, typeDatabase) = CreateClassSetup("TestModule", "MyClass", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateMutatingExtMethod("mutate", "public mutating func mutate()"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        // Class conformers use reference semantics — no write-back needed
        Assert.DoesNotContain(".pointee = instance", wrapperLines);
    }

    [Fact]
    public void GenericNonFrozenStruct_MutatingVoidMethod_EmitsWriteBack()
    {
        var (moduleDecl, structDecl, typeDatabase) = CreateGenericStructSetup("TestModule", "Container", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateMutatingExtMethod("mutate", "public mutating func mutate()"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        // Generic struct write-back must use parameterized type
        Assert.Contains("self_.assumingMemoryBound(to: TestModule.Container<Element>.self).pointee = instance", wrapperLines);
    }

    [Fact]
    public void NonFrozenStruct_MutatingClassReturn_EmitsWriteBack()
    {
        // Class return hits the `returnIsClass` branch — write-back must still happen
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.TestProtocol"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ITestProtocol"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.TestProtocol"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyStruct"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyStruct"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyStruct"),
                MetadataAccessor = "$s10TestModule8MyStructVMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Item"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Item"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Item"),
                MetadataAccessor = "$s10TestModule4ItemCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(testModule);

        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDecl("MyStruct", moduleDecl, frozen: false);
        structDecl.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyStruct"),
            SwiftTypeName.FromModuleQualifiedName("TestModule.TestProtocol"), ""));

        var extMethod = CreateMutatingExtMethod("pop", "public mutating func pop() -> TestModule.Item");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol", extMethod);

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        // Class return on mutating struct method — write-back must still happen
        // (class returns use Unmanaged.passRetained, but self is still mutated)
        Assert.Contains(".pointee = instance", wrapperLines);
        Assert.Contains("Unmanaged.passRetained", wrapperLines);
    }

    [Fact]
    public void Parser_MutatingFunc_SetsIsMutating()
    {
        var result = new Dictionary<string, List<ProtocolExtensionMethodDecl>>();
        SwiftInterfaceAccessParser.ProcessProtocolExtensionMemberForTesting(
            "  public mutating func upsert(_ db: RecordStore.Database) throws",
            "RecordStore.MutablePersistableRecord",
            new List<string>(), false, result);

        Assert.Single(result);
        Assert.True(result["RecordStore.MutablePersistableRecord"][0].IsMutating);
    }

    [Fact]
    public void Parser_NonMutatingFunc_IsMutatingFalse()
    {
        var result = new Dictionary<string, List<ProtocolExtensionMethodDecl>>();
        SwiftInterfaceAccessParser.ProcessProtocolExtensionMemberForTesting(
            "  public func doWork()",
            "TestModule.TestProtocol",
            new List<string>(), false, result);

        Assert.Single(result);
        Assert.False(result["TestModule.TestProtocol"][0].IsMutating);
    }

    // ─── Helper Methods ──────────────────────────────────────────────

    private static (ModuleDecl moduleDecl, StructDecl structDecl, TypeDatabase typeDatabase)
        CreateStructSetup(string moduleName, string structName, string protocolName, bool frozen)
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase(moduleName, $"/tmp/{moduleName}.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{moduleName}", $"I{protocolName}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{structName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{moduleName}", structName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{structName}"),
                MetadataAccessor = $"$s10{moduleName}{structName.Length}{structName}VMa",
                Flags = frozen ? TypeRecordFlags.Frozen : TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(testModule);

        var moduleDecl = CreateModuleDecl(moduleName);
        var structDecl = CreateStructDecl(structName, moduleDecl, frozen);
        structDecl.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{structName}"),
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}"),
            ""));

        return (moduleDecl, structDecl, typeDatabase);
    }

    private static (ModuleDecl moduleDecl, StructDecl structDecl, TypeDatabase typeDatabase)
        CreateGenericStructSetup(string moduleName, string structName, string protocolName)
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase(moduleName, $"/tmp/{moduleName}.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{moduleName}", $"I{protocolName}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{structName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{moduleName}", structName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{structName}"),
                MetadataAccessor = $"$s10{moduleName}{structName.Length}{structName}VMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(testModule);

        var moduleDecl = CreateModuleDecl(moduleName);
        var structDecl = CreateStructDecl(structName, moduleDecl, frozen: false);
        // Add generic parameter
        structDecl.GenericParameters.Add(new GenericArgumentDecl(
            TypeName: "τ_0_0",
            SugaredTypeName: "Element",
            GenericConformances: new List<GenericParameterConformance>(),
            AssosiatedTypeConformances: new List<GenericParameterConformance>()
        ));
        structDecl.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{structName}"),
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}"),
            ""));

        return (moduleDecl, structDecl, typeDatabase);
    }

    private static (ModuleDecl moduleDecl, ClassDecl conformingType, TypeDatabase typeDatabase)
        CreateClassSetup(string moduleName, string className, string protocolName)
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase(moduleName, $"/tmp/{moduleName}.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{moduleName}", $"I{protocolName}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{className}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{moduleName}", className),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{className}"),
                MetadataAccessor = $"$s10{moduleName}{className.Length}{className}CMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(testModule);

        var moduleDecl = CreateModuleDecl(moduleName);
        var classDecl = CreateClassDecl(className, moduleDecl);
        classDecl.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{className}"),
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}"),
            ""));

        return (moduleDecl, classDecl, typeDatabase);
    }

    private static ModuleDecl CreateModuleDecl(string name)
    {
        return new ModuleDecl
        {
            Name = name,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
    {
        var classDecl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10{moduleDecl.Name}{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(classDecl);
        return classDecl;
    }

    private static StructDecl CreateStructDecl(string name, ModuleDecl moduleDecl, bool frozen)
    {
        var structDecl = new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10{moduleDecl.Name}{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            IsFrozen = frozen,
            MetadataAccessor = $"$s10{moduleDecl.Name}{name.Length}{name}VMa",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(structDecl);
        return structDecl;
    }

    private static ProtocolExtensionMethodDecl CreateExtMethod(string methodName, string rawSignature)
    {
        var printedName = BuildPrintedName(methodName, rawSignature);
        return new ProtocolExtensionMethodDecl
        {
            ProtocolQualifiedName = "",
            MethodName = methodName,
            RawSignature = rawSignature,
            ReturnsSelf = false,
            IsMainActorIsolated = false,
            IsStatic = false,
            IsProperty = false,
            PrintedName = printedName,
            WhereConstraints = new List<string>()
        };
    }

    private static ProtocolExtensionMethodDecl CreateSelfReturningExtMethod(string methodName, string rawSignature)
    {
        var printedName = BuildPrintedName(methodName, rawSignature);
        return new ProtocolExtensionMethodDecl
        {
            ProtocolQualifiedName = "",
            MethodName = methodName,
            RawSignature = rawSignature,
            ReturnsSelf = true,
            IsMainActorIsolated = false,
            IsStatic = false,
            IsProperty = false,
            PrintedName = printedName,
            WhereConstraints = new List<string>()
        };
    }

    private static ProtocolExtensionMethodDecl CreateMutatingExtMethod(string methodName, string rawSignature)
    {
        var printedName = BuildPrintedName(methodName, rawSignature);
        return new ProtocolExtensionMethodDecl
        {
            ProtocolQualifiedName = "",
            MethodName = methodName,
            RawSignature = rawSignature,
            ReturnsSelf = false,
            IsMutating = true,
            IsMainActorIsolated = false,
            IsStatic = false,
            IsProperty = false,
            PrintedName = printedName,
            WhereConstraints = new List<string>()
        };
    }

    private static ProtocolExtensionMethodDecl CreateMutatingSelfReturningExtMethod(string methodName, string rawSignature)
    {
        var printedName = BuildPrintedName(methodName, rawSignature);
        return new ProtocolExtensionMethodDecl
        {
            ProtocolQualifiedName = "",
            MethodName = methodName,
            RawSignature = rawSignature,
            ReturnsSelf = true,
            IsMutating = true,
            IsMainActorIsolated = false,
            IsStatic = false,
            IsProperty = false,
            PrintedName = printedName,
            WhereConstraints = new List<string>()
        };
    }

    private static string BuildPrintedName(string methodName, string rawSignature)
    {
        var printedName = $"{methodName}()";
        var parenStart = rawSignature.IndexOf('(');
        if (parenStart >= 0)
        {
            var parenEnd = rawSignature.IndexOf(')', parenStart);
            if (parenEnd > parenStart + 1)
            {
                var paramStr = rawSignature.Substring(parenStart + 1, parenEnd - parenStart - 1);
                var labels = paramStr.Split(',').Select(p =>
                {
                    var trimmed = p.Trim();
                    var colonIdx = trimmed.IndexOf(':');
                    if (colonIdx < 0) return "_";
                    var label = trimmed.Substring(0, colonIdx).Trim();
                    if (label.StartsWith("_ ")) return "_";
                    return label;
                });
                printedName = $"{methodName}({string.Join("", labels.Select(l => l + ":"))})";
            }
        }
        return printedName;
    }

    private static Dictionary<string, List<ProtocolExtensionMethodDecl>> CreateExtensionMethodDict(
        string protocolQualifiedName, params ProtocolExtensionMethodDecl[] methods)
    {
        foreach (var m in methods)
            m.ProtocolQualifiedName = protocolQualifiedName;

        return new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            [protocolQualifiedName] = methods.ToList()
        };
    }
}
