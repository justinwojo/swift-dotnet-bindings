// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for ForeignTypeExtensionEmitter — C# static extension classes for Swift extensions
/// on foreign types (types not defined in the current module, e.g., UIKit.UIView).
/// </summary>
public class ForeignTypeExtensionEmitterTests
{
    private static readonly Microsoft.Extensions.Logging.ILogger Logger = NullLogger.Instance;

    #region ProcessForeignTypeExtensions: empty input

    [Fact]
    public void ProcessForeignTypeExtensions_EmptyDict_NoOutput()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, new Dictionary<string, List<ProtocolExtensionMethodDecl>>(),
            typeDatabase, Logger, ctx);

        Assert.Equal(0, ctx.ForeignExtEmittedCount);
    }

    #endregion

    #region ProcessForeignTypeExtensions: non-ObjC type skipped

    [Fact]
    public void ProcessForeignTypeExtensions_NonObjCType_Skipped()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        // SwiftModule.Int is not an ObjC class — should be skipped
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["Swift.Int"] = new()
            {
                CreateExtMethod("doubled", "public func doubled() -> Swift.Int")
            }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        Assert.Equal(0, ctx.ForeignExtEmittedCount);
    }

    #endregion

    #region ProcessForeignTypeExtensions: constrained extension skipped

    [Fact]
    public void ProcessForeignTypeExtensions_ConstrainedExtension_Skipped()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("customMethod", "public func customMethod() -> Swift.Int");
        method.WhereConstraints = new List<string> { "Element : Comparable" };

        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        Assert.Equal(0, ctx.ForeignExtEmittedCount);
    }

    #endregion

    #region ProcessForeignTypeExtensions: deprecated member skipped

    [Fact]
    public void ProcessForeignTypeExtensions_DeprecatedMember_Skipped()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("oldMethod", "public func oldMethod()");
        method.IsDeprecated = true;

        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        Assert.Equal(0, ctx.ForeignExtEmittedCount);
    }

    #endregion

    #region ProcessForeignTypeExtensions: static member skipped

    [Fact]
    public void ProcessForeignTypeExtensions_StaticMember_Skipped()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("shared", "public static func shared()");
        method.IsStatic = true;

        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        Assert.Equal(0, ctx.ForeignExtEmittedCount);
    }

    #endregion

    #region ProcessForeignTypeExtensions: property getter

    [Fact]
    public void ProcessForeignTypeExtensions_PropertyGetter_Emitted()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var property = CreateExtMethod("isEnabled", "public var isEnabled: Swift.Bool { get }");
        property.IsProperty = true;

        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { property }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        Assert.Equal(1, ctx.ForeignExtEmittedCount);
    }

    [Fact]
    public void ProcessForeignTypeExtensions_FrozenStructPropertyGetter_Skipped()
    {
        // UIKit.UIEdgeInsets is a frozen struct from a UIKit extension on UILabel;
        // the wrapper switch has no FrozenStruct arm so accepting it produces an empty
        // C# body and a void-return P/Invoke. TryProcessProperty must reject FrozenStruct
        // for parity with TryProcessMethod.
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var uikitModule = new ModuleTypeDatabase("UIKit", "/System/Library/Frameworks/UIKit.framework/UIKit");
        uikitModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("UIKit.UIEdgeInsets"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("UIKit", "UIEdgeInsets"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIEdgeInsets"),
                MetadataAccessor = "$sSo12UIEdgeInsetsVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(uikitModule);

        var property = CreateExtMethod("skeletonPaddingInsets",
            "public var skeletonPaddingInsets: UIKit.UIEdgeInsets { get set }");
        property.IsProperty = true;
        property.HasSetter = true;

        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UILabel"] = new() { property }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        Assert.Equal(0, ctx.ForeignExtEmittedCount);
    }

    #endregion

    #region ProcessForeignTypeExtensions: method with void return

    [Fact]
    public void ProcessForeignTypeExtensions_VoidMethod_Emitted()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("configure", "public func configure()");

        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        Assert.Equal(1, ctx.ForeignExtEmittedCount);
    }

    #endregion

    #region ProcessForeignTypeExtensions: generic method skipped

    [Fact]
    public void ProcessForeignTypeExtensions_GenericMethod_Skipped()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("transform", "public func transform<T>(_ value: T) -> T");

        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        Assert.Equal(0, ctx.ForeignExtEmittedCount);
    }

    #endregion

    #region EmitSwiftWrappers: emits wrappers

    [Fact]
    public void EmitSwiftWrappers_WithProcessedMembers_EmitsOutput()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("configure", "public func configure()");
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);
        ForeignTypeExtensionEmitter.EmitSwiftWrappers(swiftWriter, ctx);

        var result = swiftOutput.ToString();
        Assert.Contains("@_silgen_name", result);
        // SBSW_ (Swift CC wrapper convention) — foreign-type extension wrappers stay on
        // @_silgen_name so SwiftIndirectResult maps correctly, and PInvokeEmitHelper
        // enforces SBW_ ↔ Cdecl exclusively. See ForeignTypeExtensionEmitter.BuildSymbolName.
        Assert.Contains("SBSW_", result);
        Assert.Contains("UIView", result);
        Assert.Contains("Unmanaged", result);
    }

    [Fact]
    public void EmitSwiftWrappers_NoProcessedMembers_NoOutput()
    {
        var ctx = new ModuleEmissionContext();
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);

        ForeignTypeExtensionEmitter.EmitSwiftWrappers(swiftWriter, ctx);

        Assert.Empty(swiftOutput.ToString());
    }

    #endregion

    #region EmitCSharpExtensionClasses: emits C# extension class

    [Fact]
    public void EmitCSharpExtensionClasses_WithProcessedMembers_EmitsClass()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("configure", "public func configure()");
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        ForeignTypeExtensionEmitter.EmitCSharpExtensionClasses(csWriter, typeDatabase, "TestModule", ctx);

        var result = csOutput.ToString();
        Assert.Contains("public static partial class", result);
        Assert.Contains("Extensions", result);
        Assert.Contains("Configure", result);
        Assert.Contains("NativeMethods", result);
        Assert.Contains("LibraryImport", result);
    }

    [Fact]
    public void EmitCSharpExtensionClasses_NoProcessedMembers_NoOutput()
    {
        var ctx = new ModuleEmissionContext();
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var typeDatabase = CreateTypeDatabase();

        ForeignTypeExtensionEmitter.EmitCSharpExtensionClasses(csWriter, typeDatabase, "TestModule", ctx);

        Assert.Empty(csOutput.ToString());
    }

    #endregion

    #region ProcessForeignTypeExtensions: property with setter

    [Fact]
    public void ProcessForeignTypeExtensions_PrimitivePropertyWithSetter_EmitsBothGetAndSet()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var property = CreateExtMethod("alpha", "public var alpha: Swift.Double { get set }");
        property.IsProperty = true;
        property.HasSetter = true;

        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { property }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        // Should emit both getter and setter (2 members)
        Assert.Equal(2, ctx.ForeignExtEmittedCount);
    }

    #endregion

    #region ProcessForeignTypeExtensions: method with primitive param

    [Fact]
    public void ProcessForeignTypeExtensions_MethodWithPrimitiveParam_Emitted()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("setAlpha", "public func setAlpha(value: Swift.Double)");

        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        Assert.Equal(1, ctx.ForeignExtEmittedCount);
    }

    #endregion

    #region ProcessForeignTypeExtensions: tracks foreign module imports

    [Fact]
    public void ProcessForeignTypeExtensions_TracksNeededImports()
    {
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("configure", "public func configure()");
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        Assert.Contains("UIKit", ctx.ForeignExtNeededImports);
    }

    #endregion

    #region Helpers

    private static ModuleDecl CreateModuleDecl()
    {
        return new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ProtocolExtensionMethodDecl CreateExtMethod(string methodName, string rawSignature)
    {
        return new ProtocolExtensionMethodDecl
        {
            ProtocolQualifiedName = "",
            MethodName = methodName,
            RawSignature = rawSignature,
            ReturnsSelf = false,
            IsMainActorIsolated = false,
            IsStatic = false,
            IsProperty = false,
            PrintedName = $"{methodName}()",
            WhereConstraints = new List<string>()
        };
    }

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "nint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                MetadataAccessor = "$sSbMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Double"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Double"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Double"),
                MetadataAccessor = "$sSdMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    #endregion
}
