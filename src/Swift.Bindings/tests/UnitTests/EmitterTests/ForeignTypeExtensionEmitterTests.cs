// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
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
    public void EmitSwiftWrappers_UserParamNamedSelf_EscapedAgainstInjectedReceiver()
    {
        // Foreign-type-extension path: a user parameter literally named `self_` collides with the
        // receiver pointer the wrapper injects (`_ self_: UnsafeMutableRawPointer`). Without the
        // reserved-escape in ComputeForeignExtParamNames, the wrapper declares `self_` twice; swiftc
        // rejects it and the entry point is silently dropped from the dylib (runtime
        // EntryPointNotFoundException). The fix escapes the user binding to `__self_`.
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("shift", "public func shift(self_: Swift.Int) -> Swift.Int");
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

        // A wrapper was emitted at all.
        Assert.Contains("SBSW_", result);
        // The injected receiver binding (`_ self_:`) must appear EXACTLY once. Parameter decls carry
        // the `_ ` positional prefix; the forwarded call label (`self_:`) does not, so this literal
        // isolates the binding decl. Pre-fix the user param re-declared `_ self_:` → count 2.
        Assert.Single(Regex.Matches(result, "_ self_:"));
        // The colliding user binding was escaped rather than re-declared.
        Assert.Contains("__self_", result);
        // ...and the rename is SOURCE-LOCAL only: the forwarded Swift call must still pass the value
        // under the original external argument label `self_:` (computed from the Swift label, not the
        // escaped binding). `self_: __self_` proves label and binding moved independently — escaping
        // the binding without also rewriting the call label would forward under the wrong label and
        // silently change which Swift parameter receives the value.
        Assert.Contains("self_: __self_", result);
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

    [Fact]
    public void EmitCSharpExtensionClasses_LabelOnlyOverloads_Disambiguated()
    {
        // Swift permits overloads that differ ONLY by argument label. Both PascalCase to the
        // same C# method name with identical projected parameter types, so a naive emit produces
        // two `public static double Scaled(this UIView, double)` — CS0111. Each colliding member
        // must be renamed with a label-derived suffix.
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new()
            {
                CreateExtMethod("scaled", "public func scaled(by factor: Swift.Double) -> Swift.Double"),
                CreateExtMethod("scaled", "public func scaled(to factor: Swift.Double) -> Swift.Double"),
            }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        ForeignTypeExtensionEmitter.EmitCSharpExtensionClasses(csWriter, typeDatabase, "TestModule", ctx);

        var result = csOutput.ToString();
        // Both overloads survive, under distinct label-derived names (not first-wins).
        Assert.Contains("ScaledBy", result);
        Assert.Contains("ScaledTo", result);
    }

    [Fact]
    public void EmitCSharpExtensionClasses_TypeDistinctOverloads_NotRenamed()
    {
        // Overloads that differ by projected parameter TYPE are legal C# overloads — they must
        // keep their natural name. Only a genuine same-name + same-signature collision is renamed;
        // "don't rename the world".
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new()
            {
                CreateExtMethod("scaled", "public func scaled(by factor: Swift.Double) -> Swift.Double"),
                CreateExtMethod("scaled", "public func scaled(from count: Swift.Int) -> Swift.Double"),
            }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        ForeignTypeExtensionEmitter.EmitCSharpExtensionClasses(csWriter, typeDatabase, "TestModule", ctx);

        var result = csOutput.ToString();
        Assert.Contains("Scaled(", result);
        Assert.DoesNotContain("ScaledBy", result);
        Assert.DoesNotContain("ScaledFrom", result);
    }

    [Fact]
    public void EmitCSharpExtensionClasses_RenamedGroupCollidesWithNaturalSibling_BumpedNotDuplicated()
    {
        // A label suffix can collide with an UNRELATED natural sibling in the same class: the
        // singleton `scaledBy(x:)` emits its natural `ScaledBy(double)`, while the label-only
        // pair `scaled(by:)`/`scaled(to:)` renames `scaled(by:)` to the SAME `ScaledBy(double)`.
        // Reserving only within the collision group would let both emit `ScaledBy(this,double)`
        // → CS0111 again. The natural sibling's signature is reserved first, so the renamed
        // member is bumped (ScaledBy1) instead of duplicating it.
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new()
            {
                CreateExtMethod("scaledBy", "public func scaledBy(x: Swift.Double) -> Swift.Double"),
                CreateExtMethod("scaled", "public func scaled(by factor: Swift.Double) -> Swift.Double"),
                CreateExtMethod("scaled", "public func scaled(to factor: Swift.Double) -> Swift.Double"),
            }
        };

        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);

        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        ForeignTypeExtensionEmitter.EmitCSharpExtensionClasses(csWriter, typeDatabase, "TestModule", ctx);

        var result = csOutput.ToString();
        // All three members survive under DISTINCT names. Each name declares exactly one method,
        // so no two share a signature (the CS0111 guard). The bumped name proves the natural
        // sibling's key was reserved before the rename ran.
        Assert.Single(Regex.Matches(result, @"\bScaledBy\("));
        Assert.Single(Regex.Matches(result, @"\bScaledBy1\("));
        Assert.Single(Regex.Matches(result, @"\bScaledTo\("));
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

    #region ProcessForeignTypeExtensions: absent-Apple surface ingress

    // A present, non-null surface index that declares one unrelated UIKit type models "the reference
    // assembly is installed and binds UIKit, but genuinely does not contain the referenced type" — so
    // the synthesis withdraw-on-no-hit path marks the type AbsentAppleProjection deterministically.
    // Distinct from the null/surface-unavailable fallback (degrades to name synthesis, withdraws
    // nothing) and from a namespace the index never reflected (no standing to call anything absent).
    private static AppleTypeSurfaceIndex SurfaceCoveringUIKit()
    {
        var entry = new AppleTypeSurfaceEntry("UnrelatedSurfaceType", "UIKit", AppleTypeSurfaceKind.Class, null, false);
        return new(
            new Dictionary<string, AppleTypeSurfaceEntry>(System.StringComparer.Ordinal)
            {
                ["UIKit.UnrelatedSurfaceType"] = entry,
            },
            new Dictionary<string, AppleTypeSurfaceEntry>(System.StringComparer.Ordinal)
            {
                [entry.Name] = entry,
            });
    }

    [Fact]
    public void ProcessForeignTypeExtensions_MethodWithRequiredAbsentAppleParam_WithdrawnAndReported()
    {
        // UIKit.UIWindowLevel is an auto-bridge module type absent from the .NET binding surface.
        // The coarse IsCdeclCompatibleType classifier treats ANY auto-bridge module type as a
        // compatible ObjC-class pointer, so pre-fix this method emitted a phantom
        // `UIKit.UIWindowLevel` parameter reference (CS0234). The surface-authoritative gate must
        // withdraw the member — the ForeignTypeExtensionEmitter twin of the class-path withdrawal
        // MemberValidationPipeline already performs — and record a report row rather than dropping
        // it silently.
        using var surface = AppleTypeSurfaceIndex.OverrideDefaultForTest(SurfaceCoveringUIKit());
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("configure", "public func configure(level: UIKit.UIWindowLevel)");
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        // The phantom reference is not produced ...
        Assert.Equal(0, ctx.ForeignExtEmittedCount);
        // ... and the withdrawal is reported, not silent.
        Assert.Contains(report!.SkippedItems, s => s.Reason == SkipReason.AbsentFrameworkType);
    }

    [Fact]
    public void ProcessForeignTypeExtensions_MethodWithAbsentAppleReturn_WithdrawnAndReported()
    {
        // A return type absent from the .NET surface would emit a dangling `UIKit.UIWindowLevel`
        // return reference (CS0234). Withdraw + report rather than emit the phantom type.
        using var surface = AppleTypeSurfaceIndex.OverrideDefaultForTest(SurfaceCoveringUIKit());
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("makeLevel", "public func makeLevel() -> UIKit.UIWindowLevel");
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        Assert.Equal(0, ctx.ForeignExtEmittedCount);
        Assert.Contains(report!.SkippedItems, s => s.Reason == SkipReason.AbsentFrameworkType);
    }

    [Fact]
    public void ProcessForeignTypeExtensions_PropertyWithAbsentAppleType_WithdrawnAndReported()
    {
        // A property whose type is absent from the .NET surface would emit a dangling getter return
        // reference. Withdraw + report at the property ingress too.
        using var surface = AppleTypeSurfaceIndex.OverrideDefaultForTest(SurfaceCoveringUIKit());
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var property = CreateExtMethod("level", "public var level: UIKit.UIWindowLevel { get }");
        property.IsProperty = true;
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { property }
        };

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        Assert.Equal(0, ctx.ForeignExtEmittedCount);
        Assert.Contains(report!.SkippedItems, s => s.Reason == SkipReason.AbsentFrameworkType);
    }

    [Fact]
    public void ProcessForeignTypeExtensions_PrimitiveParam_StillEmitsUnderPresentSurface()
    {
        // Surgical-fix guard: the surface-authoritative gate must not withdraw a member whose types
        // are all known-good. A primitive parameter is unaffected by the absent-Apple withdrawal even
        // when a present-but-empty surface is installed.
        using var surface = AppleTypeSurfaceIndex.OverrideDefaultForTest(SurfaceCoveringUIKit());
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("setAlpha", "public func setAlpha(value: Swift.Double)");
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        Assert.Equal(1, ctx.ForeignExtEmittedCount);
        Assert.DoesNotContain(report!.SkippedItems, s => s.Reason == SkipReason.AbsentFrameworkType);
    }

    [Fact]
    public void ProcessForeignTypeExtensions_SiblingPackageParam_UncoveredNamespace_StillEmits()
    {
        // The sibling-binding-package shape: an auto-bridge framework whose .NET types ship in a
        // separate binding package (Matter → SwiftBindings.Apple.Matter), not in the platform
        // reference assembly. The surface index reflects ONE platform assembly, so it holds no entry
        // in that namespace at all — and silence there is ignorance, not absence. Treating it as
        // absence withdraws every member that names a sibling-packaged type, deleting API from a
        // binding whose consumer references the sibling package and would have compiled fine.
        using var surface = AppleTypeSurfaceIndex.OverrideDefaultForTest(SurfaceCoveringUIKit());
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("pair", "public func pair(device: Matter.MTRDevice)");
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        Assert.Equal(1, ctx.ForeignExtEmittedCount);
        Assert.DoesNotContain(report!.SkippedItems, s => s.Reason == SkipReason.AbsentFrameworkType);
    }

    [Fact]
    public void ProcessForeignTypeExtensions_SiblingPackageReturn_UncoveredNamespace_StillEmits()
    {
        // Same authority boundary on the return-type ingress: a sibling-packaged type is kept, so the
        // member survives instead of being withdrawn on an absence the index cannot establish.
        using var surface = AppleTypeSurfaceIndex.OverrideDefaultForTest(SurfaceCoveringUIKit());
        var ctx = new ModuleEmissionContext();
        var moduleDecl = CreateModuleDecl();
        var typeDatabase = CreateTypeDatabase();

        var method = CreateExtMethod("makeDevice", "public func makeDevice() -> Matter.MTRDevice");
        var extensions = new Dictionary<string, List<ProtocolExtensionMethodDecl>>
        {
            ["UIKit.UIView"] = new() { method }
        };

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
            moduleDecl, extensions, typeDatabase, Logger, ctx);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        Assert.Equal(1, ctx.ForeignExtEmittedCount);
        Assert.DoesNotContain(report!.SkippedItems, s => s.Reason == SkipReason.AbsentFrameworkType);
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
