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
///     (a protocol-extension default whose symbol collides with MethodWrapperEmitter — swiftc rejects with
///     "multiple definitions of symbol" at link time).
/// </summary>
public class ProtocolExtensionEmitterTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    // ─── Optional<Class> param renders nullable pointer ─────────────────

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

    // ─── Optional<value-type> param renders UnsafeRawPointer ────────────

    [Fact]
    public void OptionalDoubleParam_WrapperRendersUnsafeRawPointerWithTagByteDecode()
    {
        // Optional<Double> protocol-extension parameter shape
        // (stateDuration: Double? = nil). Before the fix, RenderSwiftParam fell through
        // ContainsGenericParameters and emitted bare `Swift.Optional<Swift.Double>` in the
        // @_cdecl wrapper signature; swiftc rejected with "type is not representable in
        // Objective-C". Wrapper must accept the payload via UnsafeRawPointer and decode using
        // the tag-byte pattern (the C# side already ships a SwiftOptional<double> payload via
        // DangerousGetHandle()), matching the proven pattern in CdeclParamMapper.Map for the
        // same shape.
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

    // ─── Cross-kind @_cdecl symbol dedup ────────────────────────────────

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

    // ─── Projected-key parity with the canonical dedup builder ──────────

    [Fact]
    public void OptionalClassParam_ProjectsOntoExistingMethodSignature_NotInjected()
    {
        // A protocol-extension default whose param is Optional<SomeClass> projects to the SAME
        // C# signature as an existing conforming-type method taking that class — nullable
        // annotations on reference types are erased for C# overload resolution. The
        // projected-key gate must route BOTH sides through the canonical builder
        // (IHandler.GetProjectedCSharpMethodKey) so the Optional<class> identity strip applies
        // symmetrically. The old hand-rolled key kept the trailing '?' on the extension side
        // only, so the collision slipped past the gate and B15 emitted a spurious second
        // member (Attach2).
        var (moduleDecl, conformingType, typeDatabase) = CreateSetupWithAdditionalClass(
            "TestModule", "MyClass", "TestProtocol", "OtherClass");

        // Existing ABI method: attach(from: OtherClass?) -> Void. A DIFFERENT external label
        // ("from") than the extension's ("using") so the Swift-overload-aware gate (which keys
        // off labels) does NOT short-circuit — the projected-key gate is what must catch it.
        const string existingMangled = "$s10TestModule7MyClassC6attachyyAA10OtherClassCSgF";
        var existing = new MethodDecl
        {
            Name = "attach",
            MangledName = existingMangled,
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { SwiftTypeSpec = TupleTypeSpec.Empty, Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null },
                new()
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("TestModule.OtherClass")),
                    Name = "from",
                    PrivateName = "from",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = conformingType,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = conformingType,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        conformingType.Methods.Add(existing);

        var extMethods = CreateExtensionMethodDict("TestModule.TestProtocol",
            CreateExtMethod("attach", "public func attach(using other: TestModule.OtherClass?)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        // The extension default projects onto the existing method's C# signature → it must be
        // dropped, leaving only the original ABI method (no spurious Attach2).
        Assert.Single(conformingType.Methods);
        // The survivor is the original ABI method, not an injected synthetic: synthetics carry
        // a StructuralIdentityKey and the protocol-ext symbol as their mangled name.
        Assert.Null(conformingType.Methods[0].StructuralIdentityKey);
        Assert.Equal(existingMangled, conformingType.Methods[0].MangledName);
    }

    [Fact]
    public void ClosureParamReferencingSelfElement_RejectedBeforeInjection_KeepsProjectedKeyGateSafe()
    {
        // The projected-key collision gate builds its preflight keying decl with the non-closure
        // BuildSyntheticMethodDecl (which leaves params raw), while the closure-injection path uses
        // BuildClosureSyntheticMethodDecl (which RESOLVES Self.Element → τ_0_0 inside the closure
        // arg). Those two builders only diverge on a param that contains `Self.X`; this test pins
        // the invariant that makes keying with the simpler builder correct: EC-17 (the
        // unresolved-generic/associated-type param gate) rejects ANY param containing `Self.X` —
        // including inside a closure — BEFORE the projected-key gate runs. So no Self.-bearing param
        // ever reaches the gate, ResolveSelfElement is therefore always a no-op on the params that
        // do reach it, and the canonical key (which ignores GenericParameters for closure args)
        // computes identically for both builders. If EC-17 is ever taught to resolve rather than
        // reject `Self.X` params, this test goes red and the keying decl must switch to
        // BuildClosureSyntheticMethodDecl to stay faithful to the emitted signature.
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "Observable", "ObservableType");

        // Generic conformer with an Element associated type → τ_0_0 (so Self.Element COULD be
        // resolved, were the method ever to reach the resolution path).
        conformingType.GenericParameters.Add(new GenericArgumentDecl(
            TypeName: "τ_0_0",
            SugaredTypeName: "Element",
            GenericConformances: new List<GenericParameterConformance>(),
            AssosiatedTypeConformances: new List<GenericParameterConformance>()));

        // The closure arg `(Self.Element) -> Bool` IS bridgeable (Self. arg + Bool return both pass
        // IsClosureBridgeable), so the cdecl-compat gate lets it through — only EC-17 stops it.
        var extMethods = CreateExtensionMethodDict("TestModule.ObservableType",
            CreateExtMethod("observe", "public func observe(using callback: (Self.Element) -> Swift.Bool)"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        // Rejected outright — not injected at all (a Self.-bearing closure default never reaches the
        // projected-key gate, so the gate's raw-param keying decl is always correct for it).
        Assert.Empty(conformingType.Methods);
        Assert.Empty(ctx.ProtocolExtSwiftWrapperLines);
    }

    // ─── Read-only extension-default PROPERTIES → synthetic getter methods ──

    [Fact]
    public void ReadOnlyBoolProperty_InjectedAsSyntheticGetterMethod()
    {
        // A get-only `var` declared in a protocol extension (TipKit.Tip.shouldDisplay shape)
        // is surfaced on the concrete conformer as a zero-parameter synthetic getter method
        // that flows through the SAME free-function wrapper pipeline as extension-default
        // methods. The Swift wrapper reads the property (no parens), and the synthetic
        // MethodDecl carries the protocol-ext free-function flags.
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "WelcomeTip", "TipLike");
        var extMethods = CreateExtensionMethodDict("TestModule.TipLike",
            CreateExtProperty("shouldDisplayTip", "public var shouldDisplayTip: Swift.Bool { get }"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        // Injected as exactly one synthetic method.
        Assert.Single(conformingType.Methods);
        var injected = conformingType.Methods[0];
        Assert.Equal("shouldDisplayTip", injected.Name);
        // Zero parameters: CSSignature is [returnType] only.
        Assert.Single(injected.CSSignature);
        // Routed through the free-function protocol-ext wrapper path, not an accessor.
        Assert.True(injected.IsProtocolExtensionMethod);
        Assert.True(injected.UsesFreeFunctionWrapper);
        Assert.True(injected.UsesWrapperLibrary);
        Assert.False(injected.IsAccessor);
        // Bool primitive return → cdecl (SBW_ + @_cdecl), matching the method siblings.
        Assert.True(injected.UsesCdeclMethodWrapper);

        var wrapperLines = string.Join("\n", ctx.ProtocolExtSwiftWrapperLines);
        Assert.Contains("@_cdecl(\"SBW_WelcomeTip_shouldDisplayTip\")", wrapperLines);
        Assert.Contains("-> Bool", wrapperLines);
        // Property read, NOT an invocation.
        Assert.Contains("return instance.shouldDisplayTip", wrapperLines);
        Assert.DoesNotContain("instance.shouldDisplayTip(", wrapperLines);
        // Class conformer self-reconstruction.
        Assert.Contains("Unmanaged<TestModule.WelcomeTip>.fromOpaque(self_).takeUnretainedValue()", wrapperLines);
    }

    [Fact]
    public void ReadWriteProperty_NotInjected()
    {
        // A read-write extension-default property (get set) is deferred — surfacing the
        // setter would need a paired write-back wrapper that doesn't exist. The HasSetter
        // gate must drop it before any wrapper is emitted.
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "WelcomeTip", "TipLike");
        var extMethods = CreateExtensionMethodDict("TestModule.TipLike",
            CreateExtProperty("displayCount", "public var displayCount: Swift.Int32 { get set }", hasSetter: true));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Empty(conformingType.Methods);
        Assert.Empty(ctx.ProtocolExtSwiftWrapperLines);
    }

    [Fact]
    public void AsyncStreamReturningProperty_RejectedByReturnGate()
    {
        // `var statusUpdates: AsyncStream<...> { get }` (TipKit.Tip.statusUpdates shape):
        // the accessor is a plain `{ get }`, so it is NOT mistaken for an async getter —
        // it parses, then the return-type gate drops it because AsyncStream is neither a
        // primitive, a registered class, nor a supported existential. This also pins that
        // the async-accessor guard does not false-positive on the "Async" in the type name.
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "WelcomeTip", "TipLike");
        var extMethods = CreateExtensionMethodDict("TestModule.TipLike",
            CreateExtProperty("statusUpdates", "public var statusUpdates: _Concurrency.AsyncStream<Swift.Int32> { get }"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Empty(conformingType.Methods);
        Assert.Empty(ctx.ProtocolExtSwiftWrapperLines);
    }

    [Fact]
    public void AsyncGetterProperty_NotInjected()
    {
        // A `{ get async }` getter cannot be read synchronously in the wrapper body —
        // ParsePropertyReturn drops it on the async accessor keyword.
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "WelcomeTip", "TipLike");
        var extMethods = CreateExtensionMethodDict("TestModule.TipLike",
            CreateExtProperty("liveScore", "public var liveScore: Swift.Int32 { get async }"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Empty(conformingType.Methods);
        Assert.Empty(ctx.ProtocolExtSwiftWrapperLines);
    }

    [Fact]
    public void ThrowingGetterProperty_NotInjected()
    {
        // A `{ get throws }` getter can't be recovered as throwing downstream
        // (IsThrowingSignature keys off the func parameter list, which a property lacks),
        // so emitting it would produce an invalid non-throwing wrapper that performs a
        // throwing access with no `try`. ParsePropertyReturn drops it on the accessor
        // `throws` keyword. A type name containing "throws" must NOT false-positive — only
        // the accessor tail is scanned.
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "WelcomeTip", "TipLike");
        var extMethods = CreateExtensionMethodDict("TestModule.TipLike",
            CreateExtProperty("riskyScore", "public var riskyScore: Swift.Int32 { get throws }"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Empty(conformingType.Methods);
        Assert.Empty(ctx.ProtocolExtSwiftWrapperLines);
    }

    [Fact]
    public void SelfReturningGetterProperty_NotInjected()
    {
        // A `var copy: Self { get }` extension default: the facts walker carries no
        // ReturnsSelf for properties, so the wrapper would emit a void function while the
        // synthetic decl resolves the return to the concrete conformer — an ABI mismatch.
        // ParsePropertyReturn drops it rather than mis-emit.
        var (moduleDecl, conformingType, typeDatabase) = CreateSetup("TestModule", "WelcomeTip", "TipLike");
        var extMethods = CreateExtensionMethodDict("TestModule.TipLike",
            CreateExtProperty("selfCopy", "public var selfCopy: Self { get }"));

        var ctx = new ModuleEmissionContext();
        ProtocolExtensionEmitter.InjectExtensionMethods(moduleDecl, extMethods, typeDatabase, Logger, ctx);

        Assert.Empty(conformingType.Methods);
        Assert.Empty(ctx.ProtocolExtSwiftWrapperLines);
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
