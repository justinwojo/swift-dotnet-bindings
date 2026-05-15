// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static BindingsGeneration.Tests.ProtocolExtensionTestHelpers;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the two ProtocolExtensionEmitter regressions surfaced by 0.11.0:
/// (1) Optional&lt;SwiftClass&gt; param rendered bare in the @_cdecl wrapper signature
///     (RealityFoundation / RealityKit setParent shape — swiftc rejects with
///     "type is not representable in Objective-C").
/// (2) Cross-kind @_cdecl symbol collision between MethodWrapperEmitter (running
///     over a synthetic protocol-extension MethodDecl) and ProtocolExtensionEmitter
///     (Kingfisher ImageDownloader.isValidStatusCode shape — swiftc rejects with
///     "multiple definitions of symbol" at link time).
/// </summary>
public class ProtocolExtensionEmitterTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    // ─── Bug 1: Optional<Class> param renders nullable pointer ─────────

    [Fact]
    public void OptionalClassParam_WrapperRendersUnsafeMutableRawPointerNullable()
    {
        // Mirrors RealityFoundation.AnchorEntity.setParent(Optional<Entity>, …):
        // a protocol extension method whose param is Optional<SomeClass>. Before the
        // fix, RenderSwiftParam fell through ContainsGenericParameters and emitted
        // bare `Optional<Other>` in the wrapper signature; swiftc rejected the @_cdecl
        // because Optional<Class> isn't ObjC-representable.
        var (moduleDecl, conformingType, typeDatabase) = CreateSetupWithAdditionalClass(
            "TestModule", "MyClass", "TestProtocol", "OtherClass");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("attach", "public func attach(_ other: TestModule.OtherClass?)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);

        // Method must inject (gate didn't reject the Optional<Class> param shape).
        Assert.Single(conformingType.Methods);

        // Param must render as nullable pointer, not bare Optional<…>.
        Assert.Contains("UnsafeMutableRawPointer?", wrapperLines);
        Assert.DoesNotContain("Swift.Optional<TestModule.OtherClass>", wrapperLines);
        Assert.DoesNotContain("Optional<TestModule.OtherClass>", wrapperLines);

        // Call site must reconstruct via Unmanaged<AnyObject>.fromOpaque mapped over
        // the nullable pointer (matches CdeclParamMapper's AnyObject-bridge path so
        // ObjC-bridged structs like IndexPath round-trip too).
        Assert.Contains("Unmanaged<AnyObject>.fromOpaque", wrapperLines);
        Assert.Contains(".map", wrapperLines);
    }

    // ─── Bug 1b: Optional<value-type> param renders UnsafeRawPointer ────

    [Fact]
    public void OptionalDoubleParam_WrapperRendersUnsafeRawPointerWithTagByteDecode()
    {
        // BlinkIDUX.ReticleStateMachineProtocol.calculateRemainingTime(stateDuration: Double? = nil)
        // shape. Before the fix, RenderSwiftParam fell through ContainsGenericParameters and
        // emitted bare `Swift.Optional<Swift.Double>` in the @_cdecl wrapper signature; swiftc
        // rejected with "type is not representable in Objective-C". Wrapper must accept the
        // payload via UnsafeRawPointer and decode using the tag-byte pattern (the C# side already
        // ships a SwiftOptional<double> payload via DangerousGetHandle()), matching the proven
        // pattern in CdeclParamMapper.Map for the same shape.
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyHolder", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("remainingTime", "public func remainingTime(stateDuration: Swift.Double?) -> Swift.Double"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);

        Assert.Single(conformingType.Methods);
        // Param must render as UnsafeRawPointer — @_cdecl rejects bare Optional<Double>.
        Assert.Contains("stateDuration: UnsafeRawPointer", wrapperLines);
        Assert.DoesNotContain("Swift.Optional<Swift.Double>", wrapperLines);
        Assert.DoesNotContain("stateDuration: Swift.Double?", wrapperLines);
        // Call site decodes via tag-byte pattern (mirrors CdeclParamMapper.Map line ~201).
        Assert.Contains("load(as: UInt8.self) == 0", wrapperLines);
        Assert.Contains("load(as: Double.self)", wrapperLines);
    }

    [Fact]
    public void OptionalInt32Param_WrapperRendersUnsafeRawPointerWithTagByteDecode()
    {
        // Sibling shape to Optional<Double> with a smaller primitive — exercises the
        // tag-byte offset lookup. Off-by-one in the offset would mis-read nil-vs-some
        // at runtime; we pin the offset string against the canonical map.
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyHolder", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("remainingCount", "public func remainingCount(count: Swift.Int32?) -> Swift.Int32"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);

        Assert.Single(conformingType.Methods);
        Assert.Contains("count: UnsafeRawPointer", wrapperLines);
        Assert.DoesNotContain("Swift.Optional<Swift.Int32>", wrapperLines);
        Assert.Contains("load(as: UInt8.self) == 0", wrapperLines);
        Assert.Contains("load(as: Int32.self)", wrapperLines);
    }

    [Fact]
    public void OptionalBoolParam_WrapperRendersUnsafeRawPointerWithPointeeFallback()
    {
        // Bool is excluded from IsBlittablePrimitiveSwiftType because Optional<Bool> uses
        // extra-inhabitant encoding (size 1, no separate tag byte). The wrapper must still
        // accept UnsafeRawPointer (bare Optional<Bool> isn't ObjC-representable), but the
        // call-site reconstruction goes through the generic-container fallback —
        // assumingMemoryBound(to: Swift.Optional<Bool>.self).pointee — matching the proven
        // pattern in CdeclParamMapper.Map line ~290.
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "MyHolder", "TestProtocol");
        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("remainingFlag", "public func remainingFlag(flag: Swift.Bool?) -> Swift.Int32"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);

        Assert.Single(conformingType.Methods);
        Assert.Contains("flag: UnsafeRawPointer", wrapperLines);
        Assert.DoesNotContain(": Swift.Bool?", wrapperLines);
        // Pointer-typed access fallback — no tag-byte read because Bool has no separate tag byte.
        Assert.Contains("assumingMemoryBound(to: Swift.Optional<Swift.Bool>.self).pointee", wrapperLines);
        Assert.DoesNotContain("load(as: UInt8.self) == 0", wrapperLines);
    }

    // ─── Bug 2: cross-kind @_cdecl symbol dedup ────────────────────────

    [Fact]
    public void SameSymbol_RegisteredViaMethodAndProtocolExt_RejectsSecondRegistration()
    {
        // Whenever ProtocolExtensionEmitter injects a synthetic MethodDecl onto a
        // conforming type, the standard MethodHandler → MethodWrapperEmitter pipeline
        // runs over it AND ProtocolExtensionEmitter still flushes its own buffered
        // @_cdecl wrapper. Both target the same C symbol; without cross-kind dedup
        // both emissions fire and swiftc rejects with "multiple definitions of symbol".
        var ctx = new ModuleEmissionContext();
        const string symbol = "SBW_ImageDownloader_isValidStatusCode_Int_forImageDownloader";

        Assert.True(ctx.TryAddMethodWrapperSymbol(symbol),
            "First emitter to claim the C symbol should succeed");
        Assert.False(ctx.TryAddProtocolExtSymbol(symbol),
            "Second emitter targeting the same C symbol must be rejected so it skips its emission");

        // Reverse direction — same contract regardless of which kind got there first.
        var ctx2 = new ModuleEmissionContext();
        Assert.True(ctx2.TryAddProtocolExtSymbol(symbol));
        Assert.False(ctx2.TryAddMethodWrapperSymbol(symbol));

        // Unified registry has one entry per symbol, not one per kind.
        Assert.Single(ctx.RegisteredWrapperSymbols, s => s == symbol);
    }

    [Fact]
    public void SameProtocolExtensionOnTwoConformingClasses_YieldsTwoDistinctSymbols()
    {
        // The cross-kind dedup must not collapse legitimately-distinct symbols. A
        // single protocol extension applied to two concrete conforming classes
        // emits one wrapper per class; the symbols differ via the flat type name
        // baked into BuildSymbolName.
        var (moduleDecl, conformingTypeA, conformingTypeB, typeDatabase) =
            CreateSetupWithTwoConformingClasses("TestModule", "ClassA", "ClassB", "TestProtocol");

        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("ping", "public func ping()"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        // Both conforming classes received the synthetic method.
        Assert.Single(conformingTypeA.Methods);
        Assert.Single(conformingTypeB.Methods);

        // Both @_cdecl symbols are present and distinct.
        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        Assert.Contains("@_cdecl(\"SBW_ClassA_ping\")", wrapperLines);
        Assert.Contains("@_cdecl(\"SBW_ClassB_ping\")", wrapperLines);
        Assert.True(ctx.IsWrapperSymbolRegistered("SBW_ClassA_ping"));
        Assert.True(ctx.IsWrapperSymbolRegistered("SBW_ClassB_ping"));
    }

    // ─── Helper Methods ──────────────────────────────────────────────

    /// <summary>
    /// Setup variant that registers an additional Class (not Protocol) for use as
    /// the type parameter inside an Optional&lt;…&gt;. The conforming type still
    /// claims its protocol conformance against TestProtocol.
    /// </summary>
    private static (ModuleDecl moduleDecl, ClassDecl conformingType, TypeDatabase typeDatabase)
        CreateSetupWithAdditionalClass(string moduleName, string className, string protocolName, string additionalClassName)
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
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{additionalClassName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{moduleName}", additionalClassName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{additionalClassName}"),
                MetadataAccessor = $"$s10{moduleName}{additionalClassName.Length}{additionalClassName}CMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(testModule);

        var moduleDecl = CreateModuleDecl(moduleName);
        var conformingType = CreateClassDecl(className, moduleDecl);
        conformingType.Conformances.Add(new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{className}"),
            SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}"),
            ""));

        return (moduleDecl, conformingType, typeDatabase);
    }

    /// <summary>
    /// Setup variant with two distinct conforming classes claiming the same protocol.
    /// Used to verify that one extension method produces one symbol per conformance.
    /// </summary>
    private static (ModuleDecl moduleDecl, ClassDecl conformingTypeA, ClassDecl conformingTypeB, TypeDatabase typeDatabase)
        CreateSetupWithTwoConformingClasses(string moduleName, string classA, string classB, string protocolName)
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
        foreach (var cls in new[] { classA, classB })
        {
            testModule.RegisterType(
                SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{cls}"),
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName($"Swift.{moduleName}", cls),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{cls}"),
                    MetadataAccessor = $"$s10{moduleName}{cls.Length}{cls}CMa",
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Class
                });
        }
        typeDatabase.AddModuleDatabase(testModule);

        var moduleDecl = CreateModuleDecl(moduleName);
        var typeA = CreateClassDecl(classA, moduleDecl);
        var typeB = CreateClassDecl(classB, moduleDecl);
        foreach (var t in new[] { typeA, typeB })
        {
            t.Conformances.Add(new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{t.Name}"),
                SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}"),
                ""));
        }

        return (moduleDecl, typeA, typeB, typeDatabase);
    }
}
