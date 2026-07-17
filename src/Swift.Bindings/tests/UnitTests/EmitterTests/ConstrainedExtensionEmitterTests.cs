// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="ConstrainedExtensionEmitter"/> — the constrained-extension
/// specialization detection and grouping logic.
/// </summary>
public class ConstrainedExtensionEmitterTests
{
    [Fact]
    public void FindConstrainedSpecializations_NoGenericType_ReturnsEmpty()
    {
        var typeDecl = CreateStructDecl("MyStruct", isGeneric: false);
        var result = ConstrainedExtensionEmitter.FindConstrainedSpecializations(typeDecl);
        Assert.Empty(result);
    }

    [Fact]
    public void FindConstrainedSpecializations_SingleSpecPerName_GroupsByConcreteType()
    {
        // Single-specialization same-type-constraint properties are
        // bound to a closed-generic mangled symbol (e.g.,
        // Forecast<MinuteWeather>.Summary), so they must be routed to the
        // closed-generic extension-method emitter even though there is no
        // sibling-name conflict.
        var typeDecl = CreateGenericStructDecl("Wrapper", "T");
        typeDecl.Properties.Add(CreatePropertyWithConstraint("propA", "TestModule.ConcreteA"));
        typeDecl.Properties.Add(CreatePropertyWithConstraint("propB", "TestModule.ConcreteB"));

        var result = ConstrainedExtensionEmitter.FindConstrainedSpecializations(typeDecl);
        Assert.Equal(2, result.Count);

        var concreteA = SwiftTypeName.FromModuleQualifiedName("TestModule.ConcreteA");
        var concreteB = SwiftTypeName.FromModuleQualifiedName("TestModule.ConcreteB");

        Assert.True(result.ContainsKey(concreteA));
        Assert.True(result.ContainsKey(concreteB));
        Assert.Single(result[concreteA]);
        Assert.Equal("propA", result[concreteA][0].Name);
        Assert.Single(result[concreteB]);
        Assert.Equal("propB", result[concreteB][0].Name);
    }

    [Fact]
    public void FindConstrainedSpecializations_NonGenericType_ReturnsEmpty()
    {
        // Non-generic types cannot have same-type-constrained extensions —
        // skip the whole walk regardless of property shape.
        var typeDecl = CreateStructDecl("Concrete", isGeneric: false);
        typeDecl.Properties.Add(CreateUnconstrainedProperty("prop"));

        var result = ConstrainedExtensionEmitter.FindConstrainedSpecializations(typeDecl);
        Assert.Empty(result);
    }

    [Fact]
    public void FindConstrainedSpecializations_TwoSpecializations_GroupsByConcreteType()
    {
        var typeDecl = CreateGenericStructDecl("Wrapper", "T");
        typeDecl.Properties.Add(CreatePropertyWithConstraint("label", "TestModule.Alpha"));
        typeDecl.Properties.Add(CreatePropertyWithConstraint("label", "TestModule.Beta"));

        var result = ConstrainedExtensionEmitter.FindConstrainedSpecializations(typeDecl);
        Assert.Equal(2, result.Count);

        var alphaKey = SwiftTypeName.FromModuleQualifiedName("TestModule.Alpha");
        var betaKey = SwiftTypeName.FromModuleQualifiedName("TestModule.Beta");

        Assert.True(result.ContainsKey(alphaKey), "Should contain Alpha specialization");
        Assert.True(result.ContainsKey(betaKey), "Should contain Beta specialization");
        Assert.Single(result[alphaKey]);
        Assert.Single(result[betaKey]);
    }

    [Fact]
    public void FindConstrainedSpecializations_MixedConstrainedAndUnconstrained_ReturnsEmpty()
    {
        var typeDecl = CreateGenericStructDecl("Wrapper", "T");
        typeDecl.Properties.Add(CreatePropertyWithConstraint("label", "TestModule.Alpha"));
        typeDecl.Properties.Add(CreateUnconstrainedProperty("label")); // no constraint

        var result = ConstrainedExtensionEmitter.FindConstrainedSpecializations(typeDecl);
        Assert.Empty(result); // not all siblings are constrained → skip all
    }

    [Fact]
    public void ExtractSameTypeConstraint_PropertyWithConcreteConstraint_ReturnsTarget()
    {
        var property = CreatePropertyWithConstraint("prop", "TestModule.ConcreteType");
        var result = ConstrainedExtensionEmitter.ExtractSameTypeConstraint(property);

        Assert.NotNull(result);
        Assert.Equal("TestModule", result!.Module);
        Assert.Equal("ConcreteType", result.Name);
    }

    // When the sugared generic signature is unavailable the parser falls back to the raw ABI
    // spelling, where a dependent-member pin reads as a placeholder-rooted path. It is dotted,
    // so it parses into a plausible-looking name whose "module" is really a generic parameter.
    // Nothing concrete was ever substituted, so it cannot close the parent: rendering it spells
    // Parent<τ_0_0.Bridge.T> into Swift and into the @_cdecl symbol, which cannot compile.
    [Theory]
    [InlineData("τ_0_0.Bridge.T")]
    [InlineData("τ_0_0.Element")]
    [InlineData("τ_1_0.Wrapped")]
    public void ExtractSameTypeConstraint_PlaceholderRootedPin_ReturnsNull(string pin)
    {
        var property = CreatePropertyWithConstraint("prop", pin);
        Assert.Null(ConstrainedExtensionEmitter.ExtractSameTypeConstraint(property));
    }

    // The root segment is what disqualifies a pin. A type genuinely named T in type position is
    // a real, specializable target — rejecting it would drop bindable constrained members.
    [Theory]
    [InlineData("TestModule.T")]
    [InlineData("TestModule.Outer.T")]
    public void ExtractSameTypeConstraint_ConcretePinWithShortLeafName_ReturnsTarget(string pin)
    {
        var property = CreatePropertyWithConstraint("prop", pin);
        var result = ConstrainedExtensionEmitter.ExtractSameTypeConstraint(property);

        Assert.NotNull(result);
        Assert.Equal("TestModule", result!.Module);
        Assert.Equal("T", result.Name);
    }

    [Fact]
    public void ExtractSameTypeConstraint_PropertyWithProtocolConstraint_ReturnsNull()
    {
        var property = CreatePropertyWithProtocolConstraint("prop", "TestModule.SomeProtocol");
        var result = ConstrainedExtensionEmitter.ExtractSameTypeConstraint(property);
        Assert.Null(result); // Protocol constraints are not same-type
    }

    [Fact]
    public void ExtractSameTypeConstraint_PropertyWithNoConstraint_ReturnsNull()
    {
        var property = CreateUnconstrainedProperty("prop");
        var result = ConstrainedExtensionEmitter.ExtractSameTypeConstraint(property);
        Assert.Null(result);
    }

    // ==================== Dependent-member same-type constraint detection ====================
    //
    // Bug A from AppIntents 0.12.0: `extension IntentParameter where Value.ValueType == X`
    // properties land in `GenericArgumentDecl.AssosiatedTypeConformances` (note typo) rather
    // than `GenericConformances`. `ExtractSameTypeConstraint` intentionally only inspects
    // the direct-constraint list (it needs a concrete parent generic arg to re-emit a
    // closed extension), so a separate predicate is needed to gate the open-generic
    // emission. Without it, the open-generic protocol-group emission produces unsatisfiable
    // `_SBW_PG_*` conformances like
    //   extension AppIntents.IntentParameter: _SBW_PG_82163CB5 {}
    // for properties only available when `Value.ValueType == Bool`.

    [Fact]
    public void HasParentExtensionSameTypeConstraint_DirectSameTypeConstraint_ReturnsTrue()
    {
        // `where T == Concrete` shape — already handled by ExtractSameTypeConstraint
        // for the closed-extension re-emit path; the predicate must also return true
        // so the suppression-at-open-generic path stays consistent (this property is
        // not in the protocol-group-conformance surface).
        var property = CreatePropertyWithConstraint("prop", "TestModule.ConcreteA");
        Assert.True(ConstrainedExtensionEmitter.HasParentExtensionSameTypeConstraint(property));
    }

    [Fact]
    public void HasParentExtensionSameTypeConstraint_DependentMemberSameTypeConstraint_ReturnsTrue()
    {
        // `where Value.ValueType == Concrete` shape — the property is unsatisfiable
        // at the open-generic level. The closed-extension emitter cannot re-surface
        // this (no single concrete parent generic argument to bind to), so it's
        // dropped from emission entirely; the predicate must still return true so
        // the open-generic path knows to skip it.
        var property = CreatePropertyWithAssociatedTypeConstraint(
            "currencyCodes", "TestModule.IntentCurrencyAmount");
        Assert.True(ConstrainedExtensionEmitter.HasParentExtensionSameTypeConstraint(property));
    }

    [Fact]
    public void HasParentExtensionSameTypeConstraint_ProtocolConstraint_ReturnsFalse()
    {
        // `where T: Hashable` — protocol constraints are filtered/lifted by separate
        // emitter machinery; they don't make the property unsatisfiable in the way
        // same-type constraints do.
        var property = CreatePropertyWithProtocolConstraint("prop", "TestModule.SomeProtocol");
        Assert.False(ConstrainedExtensionEmitter.HasParentExtensionSameTypeConstraint(property));
    }

    [Fact]
    public void HasParentExtensionSameTypeConstraint_UnconstrainedProperty_ReturnsFalse()
    {
        var property = CreateUnconstrainedProperty("prop");
        Assert.False(ConstrainedExtensionEmitter.HasParentExtensionSameTypeConstraint(property));
    }

    /// <summary>
    /// Builds a property whose getter's generic parameter carries a dependent-member
    /// same-type constraint (e.g. <c>where Value.ValueType == X</c>) — the constraint
    /// lands in <see cref="GenericArgumentDecl.AssosiatedTypeConformances"/> with a
    /// multi-segment <c>Path</c>.
    /// </summary>
    private static PropertyDecl CreatePropertyWithAssociatedTypeConstraint(string name, string concreteType)
    {
        // Path length > 1 marks this as a dependent-member constraint
        // (`Value.ValueType` rather than just `Value`).
        var conformance = new GenericParameterConformance(
            new[] { "τ_0_0", "ValueType" },
            SwiftTypeName.FromModuleQualifiedName(concreteType),
            ConformanceKind.ConcreteType);

        var getterMethod = new MethodDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s10TestModule7WrapperV{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "Value",
                    new List<GenericParameterConformance>(),
                    new List<GenericParameterConformance> { conformance })
            },
            CSSignature = new List<ArgumentDecl>(),
            AvailabilityAnnotations = null
        };

        return new PropertyDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            HasStorage = false,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            AvailabilityAnnotations = null
        };
    }

    // ==================== Helpers ====================

    private static StructDecl CreateStructDecl(string name, bool isGeneric)
    {
        return new StructDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = "",
            IsFrozen = true,
            GenericParameters = isGeneric
                ? new List<GenericArgumentDecl> { new("τ_0_0", "T", new(), new()) }
                : new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            AvailabilityAnnotations = null
        };
    }

    private static StructDecl CreateGenericStructDecl(string name, string genericParamName)
    {
        return new StructDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", genericParamName, new(), new())
            },
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            AvailabilityAnnotations = null
        };
    }

    private static PropertyDecl CreatePropertyWithConstraint(string name, string concreteType)
    {
        var conformance = new GenericParameterConformance(
            new[] { "τ_0_0" },
            SwiftTypeName.FromModuleQualifiedName(concreteType),
            ConformanceKind.ConcreteType);

        var getterMethod = new MethodDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s10TestModule7WrapperV{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T", new List<GenericParameterConformance> { conformance }, new())
            },
            CSSignature = new List<ArgumentDecl>(),
            AvailabilityAnnotations = null
        };

        return new PropertyDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            HasStorage = false,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            AvailabilityAnnotations = null
        };
    }

    private static PropertyDecl CreatePropertyWithProtocolConstraint(string name, string protocolType)
    {
        var conformance = new GenericParameterConformance(
            new[] { "τ_0_0" },
            SwiftTypeName.FromModuleQualifiedName(protocolType),
            ConformanceKind.Protocol);

        var getterMethod = new MethodDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s10TestModule7WrapperV{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T", new List<GenericParameterConformance> { conformance }, new())
            },
            CSSignature = new List<ArgumentDecl>(),
            AvailabilityAnnotations = null
        };

        return new PropertyDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            HasStorage = false,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            AvailabilityAnnotations = null
        };
    }

    private static PropertyDecl CreateUnconstrainedProperty(string name)
    {
        var getterMethod = new MethodDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s10TestModule7WrapperV{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T", new(), new()) // no constraints
            },
            CSSignature = new List<ArgumentDecl>(),
            AvailabilityAnnotations = null
        };

        return new PropertyDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            HasStorage = false,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            AvailabilityAnnotations = null
        };
    }

    // ==================== Emission tests for new return-shape coverage ====================
    //
    // Multi-specialization generic property accessor coverage:
    //   1. EnumHandler now invokes ConstrainedExtensionEmitter, so a generic enum's
    //      `where T == Concrete` extension properties can re-surface as closed-generic
    //      extension methods. (Tested separately by validating StoreKit2's
    //      VerificationResult<SignedType>.jwsRepresentation surfaces; see
    //      EnumHandler.cs comment.)
    //   2. CEReturnShape adds NonFrozenStruct so generic-enum-extension properties
    //      whose return type is a resilient Swift struct (or class) re-surface with
    //      the SwiftIndirectResult shape.
    //
    // The two emission tests below exercise the End-to-end emission for both shapes.

    [Fact]
    public void EmitConstrainedExtensions_GenericEnum_StringProperty_EmitsExtensionMethodAndUtf8Slice()
    {
        // Reproduces StoreKit2's VerificationResult<SignedType>.jwsRepresentation
        // shape: a generic enum with a `where T == Concrete` String property.
        var (csOutput, swiftOutput) = EmitForGenericEnumWithProperty(
            propertyName: "jwsRepresentation",
            returnTypeName: "Swift.String");

        // C# extension class wraps a closed-generic extension method bound to the
        // SBW_CEGet_* @_cdecl symbol. Utf8Slice marshalling reads the result.
        Assert.Contains("public static partial class TestModule_DWrapper_TestModule_DConcreteAExtensions", csOutput);
        Assert.Contains("public static string GetJwsRepresentation(this Wrapper<TestModule.ConcreteA> self)", csOutput);
        Assert.Contains("SBW_CEGet_TestModule_DWrapper_TestModule_DConcreteA_jwsRepresentation_instance", csOutput);
        Assert.Contains("SwiftMarshal.ReadUtf8Slice(resultPtr)", csOutput);
        Assert.Contains("(IntPtr resultPtr, IntPtr _self)", csOutput);
        // Finding 56c: the 2-word (SBW_Utf8Slice) scratch buffer is stackalloc'd, not heap-allocated.
        // ReadUtf8Slice copies the bytes out before return, so no per-call NativeMemory.Alloc/Free.
        Assert.Contains("stackalloc byte[nint.Size * 2]", csOutput);
        Assert.DoesNotContain("NativeMemory.Alloc", csOutput);
        Assert.DoesNotContain("NativeMemory.Free", csOutput);

        // Swift wrapper hands back via Utf8Slice; @_cdecl + indirect-buffer shape.
        Assert.Contains("@_cdecl(\"SBW_CEGet_TestModule_DWrapper_TestModule_DConcreteA_jwsRepresentation_instance\")", swiftOutput);
        Assert.Contains("_ resultPtr: UnsafeMutableRawPointer", swiftOutput);
        Assert.Contains("_ self_: UnsafeRawPointer", swiftOutput);
        Assert.Contains("obj.jwsRepresentation", swiftOutput);
    }

    [Fact]
    public void EmitConstrainedExtensions_GenericEnum_NonFrozenStructProperty_UsesIndirectResult()
    {
        // Reproduces StoreKit2's VerificationResult<SignedType>.signature shape:
        // generic enum with a `where T == Concrete` non-frozen-struct (or
        // resilient-class) return type.
        var (csOutput, swiftOutput) = EmitForGenericEnumWithProperty(
            propertyName: "signature",
            returnTypeName: "TestModule.SigBlob",
            extraTypes: new[]
            {
                (
                    SwiftTypeName.FromModuleQualifiedName("TestModule.SigBlob"),
                    new TypeRecord
                    {
                        CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "SigBlob"),
                        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SigBlob"),
                        MetadataAccessor = "$s10TestModule7SigBlobVMa",
                        Flags = TypeRecordFlags.RequiresMemoryManagement,
                        Kind = TypeRecordKind.Struct,
                    }
                )
            });

        // C# extension method allocates a VWT-sized buffer and PInvokes via
        // SwiftIndirectResult (the resilient-struct return shape).
        Assert.Contains("public static TestModule.SigBlob GetSignature(this Wrapper<TestModule.ConcreteA> self)", csOutput);
        Assert.Contains("SwiftObjectHelper<TestModule.SigBlob>.GetTypeMetadata()", csOutput);
        Assert.Contains("var indirectResult = new SwiftIndirectResult", csOutput);
        Assert.Contains("SwiftMarshal.MarshalFromSwift<TestModule.SigBlob>(buffer)", csOutput);
        Assert.Contains("(SwiftIndirectResult indirectResult, IntPtr _self)", csOutput);

        // Swift wrapper writes the value into the caller-provided buffer via
        // initializeMemory(as:repeating:count:) — the resilient-struct shape.
        Assert.Contains("@_cdecl(\"SBW_CEGet_TestModule_DWrapper_TestModule_DConcreteA_signature_instance\")", swiftOutput);
        Assert.Contains("_ resultPtr: UnsafeMutableRawPointer", swiftOutput);
        // Module-qualified Swift type spec — `.initializeMemory(as:)`
        // needs the source module prefix because the wrapper file may
        // not import the type's defining module (e.g. CryptoKit's
        // `P256.Signing.ECDSASignature` only resolves as
        // `CryptoKit.P256.Signing.ECDSASignature` from a wrapper that
        // doesn't import CryptoKit).
        Assert.Contains("resultPtr.initializeMemory(as: TestModule.SigBlob.self, repeating: result, count: 1)", swiftOutput);
    }

    [Fact]
    public void EmitConstrainedExtensions_GenericEnum_FoundationDateProperty_ReturnsDateTimeOffsetViaEpoch()
    {
        // StoreKit2 VerificationResult<SignedType>.signedDate shape: extension property
        // returning Foundation.Date (frozen value-type with timeIntervalSinceReferenceDate
        // ABI). Should emit a System.DateTimeOffset return + epoch arithmetic, with the
        // P/Invoke signature returning Double directly (no indirect-result buffer).
        var (csOutput, swiftOutput) = EmitForGenericEnumWithProperty(
            propertyName: "signedDate",
            returnTypeName: "Foundation.Date");

        Assert.Contains("public static System.DateTimeOffset GetSignedDate(this Wrapper<TestModule.ConcreteA> self)", csOutput);
        // P/Invoke takes only _self and returns double — Date is NOT routed through indirect-result.
        Assert.Contains("partial double SBW_CEGet_TestModule_DWrapper_TestModule_DConcreteA_signedDate_instance(IntPtr _self)", csOutput);
        Assert.DoesNotContain("SwiftIndirectResult indirectResult, IntPtr _self", csOutput.Replace("\r", "")); // sanity: not the indirect shape
        // Epoch arithmetic mirrors DateProjection.GetReturnPlan(Direct).
        Assert.Contains("AddSeconds(seconds)", csOutput);
        Assert.Contains("new System.DateTimeOffset(2001, 1, 1, 0, 0, 0, System.TimeSpan.Zero)", csOutput);

        // Swift wrapper returns Double directly via timeIntervalSinceReferenceDate.
        Assert.Contains("@_cdecl(\"SBW_CEGet_TestModule_DWrapper_TestModule_DConcreteA_signedDate_instance\")", swiftOutput);
        Assert.Contains("-> Double", swiftOutput);
        Assert.Contains("return obj.signedDate.timeIntervalSinceReferenceDate", swiftOutput);
    }

    [Fact]
    public void EmitConstrainedExtensions_GenericEnum_FoundationUUIDProperty_UsesIndirectResultAndGuidCast()
    {
        // StoreKit2 VerificationResult<SignedType>.deviceVerificationNonce shape:
        // extension property returning Foundation.UUID (frozen 16-byte tuple) — emit a
        // System.Guid return via indirect-result buffer + memcpy.
        var (csOutput, swiftOutput) = EmitForGenericEnumWithProperty(
            propertyName: "deviceVerificationNonce",
            returnTypeName: "Foundation.UUID");

        Assert.Contains("public static System.Guid GetDeviceVerificationNonce(this Wrapper<TestModule.ConcreteA> self)", csOutput);
        Assert.Contains("(SwiftIndirectResult indirectResult, IntPtr _self)", csOutput);
        // Finding 56c: the fixed 16-byte scratch buffer is stackalloc'd, not heap-allocated.
        // The Guid value is copied out (*(System.Guid*)buffer) before the frame exits, so the
        // former NativeMemory.Alloc(16)/Free pair (which only reclaimed the container) is gone
        // entirely — the stack reclaims the buffer identically, including the exceptional path.
        Assert.Contains("stackalloc byte[16]", csOutput);
        Assert.Contains("*(System.Guid*)buffer", csOutput);
        Assert.DoesNotContain("NativeMemory.Alloc", csOutput);
        Assert.DoesNotContain("NativeMemory.Free", csOutput);

        // Swift wrapper writes the UUID into the caller buffer.
        Assert.Contains("@_cdecl(\"SBW_CEGet_TestModule_DWrapper_TestModule_DConcreteA_deviceVerificationNonce_instance\")", swiftOutput);
        Assert.Contains("_ resultPtr: UnsafeMutableRawPointer", swiftOutput);
        Assert.Contains("resultPtr.initializeMemory(as: Foundation.UUID.self, repeating: result, count: 1)", swiftOutput);
    }

    [Fact]
    public void EmitConstrainedExtensions_GenericEnum_FoundationDataProperty_UsesIndirectResultAndToByteArray()
    {
        // StoreKit2 VerificationResult<SignedType>.headerData shape: extension property
        // returning Foundation.Data — emit a byte[] return via indirect-result buffer +
        // (*(Swift.Foundation.Data*)buffer).ToByteArray() + free in finally.
        var (csOutput, swiftOutput) = EmitForGenericEnumWithProperty(
            propertyName: "headerData",
            returnTypeName: "Foundation.Data");

        Assert.Contains("public static byte[] GetHeaderData(this Wrapper<TestModule.ConcreteA> self)", csOutput);
        Assert.Contains("(SwiftIndirectResult indirectResult, IntPtr _self)", csOutput);
        // Finding 56c: the 16-byte scratch buffer is stackalloc'd; ToByteArray() copies the
        // underlying bytes out before return, so the per-call heap alloc/free is removed.
        Assert.Contains("stackalloc byte[16]", csOutput);
        Assert.Contains("(*(Swift.Foundation.Data*)(void*)buffer).ToByteArray()", csOutput);
        Assert.DoesNotContain("NativeMemory.Alloc", csOutput);
        Assert.DoesNotContain("NativeMemory.Free", csOutput);

        // Swift wrapper writes the Data into the caller buffer (it's a 16-byte struct).
        Assert.Contains("@_cdecl(\"SBW_CEGet_TestModule_DWrapper_TestModule_DConcreteA_headerData_instance\")", swiftOutput);
        Assert.Contains("resultPtr.initializeMemory(as: Foundation.Data.self, repeating: result, count: 1)", swiftOutput);
    }

    [Fact]
    public void EmitConstrainedExtensions_GenericEnum_FoundationDataProperty_RecordsAppleSupplementReference()
    {
        // The emitted getter casts through Swift.Foundation.Data, which lives in the
        // SwiftBindings.Apple supplement. The csproj emitter only adds the supplement
        // PackageReference when AppleSupplementReferences.Record was called for an
        // identity that resolved to the supplement. This emitter bypasses
        // TypeProjectionFactory (which records "Foundation.Data" itself), so without an
        // explicit Record call here a binding whose only Foundation.Data usage is via
        // a constrained-extension property would compile-fail at the consumer.
        AppleSupplementReferences.Reset();
        try
        {
            EmitForGenericEnumWithProperty(
                propertyName: "headerData",
                returnTypeName: "Foundation.Data");

            Assert.Contains("Foundation.Data", AppleSupplementReferences.Current);
        }
        finally
        {
            // Reset to avoid thread-static state leaking into adjacent tests.
            AppleSupplementReferences.Reset();
        }
    }

    [Fact]
    public void EmitConstrainedExtensions_GenericEnum_FoundationDateAndUUIDProperties_DoNotRecordAppleSupplement()
    {
        // Date marshals as a plain Double (DateProjection.SwiftEpoch is a literal
        // System.DateTimeOffset constructor) and UUID is read as System.Guid via direct
        // memory cast — neither path emits any Swift.Foundation.* identifier, so the
        // supplement reference must NOT be recorded for them.
        AppleSupplementReferences.Reset();
        try
        {
            EmitForGenericEnumWithProperty(
                propertyName: "signedDate",
                returnTypeName: "Foundation.Date");
            EmitForGenericEnumWithProperty(
                propertyName: "deviceVerificationNonce",
                returnTypeName: "Foundation.UUID");

            Assert.DoesNotContain("Foundation.Date", AppleSupplementReferences.Current);
            Assert.DoesNotContain("Foundation.UUID", AppleSupplementReferences.Current);
        }
        finally
        {
            AppleSupplementReferences.Reset();
        }
    }

    [Fact]
    public void EmitConstrainedExtensions_GenericEnum_StaticConstrainedProperty_EmitsSkipDiagnostic()
    {
        // The property emit shape always reconstructs `obj` from `_self` and
        // accesses `obj.{name}` — no static branch mirroring the method path.
        // A constrained `static var` must therefore be skipped with an explicit
        // diagnostic rather than silently emitting an instance-shaped wrapper
        // that the Swift wrapper-build script strips, leaving a missing-symbol
        // C# extern.
        var tdb = BuildEmissionTypeDatabase(extraTypes: null);
        var enumDecl = CreateGenericEnumDecl("Wrapper", "T");
        enumDecl.Properties.Add(CreateStaticPropertyWithConstraint(
            "rank", "TestModule.ConcreteA", new NamedTypeSpec("Swift.Int32")));

        var csSw = new StringWriter();
        var csWriter = new CSharpWriter(csSw);
        var swiftSw = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftSw);
        var emissionContext = new ModuleEmissionContext();
        ILogger logger = NullLogger<ConstrainedExtensionEmitterTests>.Instance;

        ConstrainedExtensionEmitter.EmitConstrainedExtensions(
            csWriter, swiftWriter, enumDecl, tdb, emissionContext, logger);

        var csOutput = csSw.ToString();
        var swiftOutput = swiftSw.ToString();

        Assert.Contains("constrained static property emission not yet supported", csOutput);
        // The Swift wrapper must NOT be emitted — no SBW_CEGet symbol on the
        // wire for a skipped declaration.
        Assert.DoesNotContain("SBW_CEGet_TestModule_DWrapper_TestModule_DConcreteA_rank", swiftOutput);
    }

    private static PropertyDecl CreateStaticPropertyWithConstraint(string name, string concreteType, TypeSpec returnTypeSpec)
    {
        var conformance = new GenericParameterConformance(
            new[] { "τ_0_0" },
            SwiftTypeName.FromModuleQualifiedName(concreteType),
            ConformanceKind.ConcreteType);

        var getterMethod = new MethodDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s10TestModule7WrapperV{name}Z",
            MethodType = MethodType.Static,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T", new List<GenericParameterConformance> { conformance }, new())
            },
            CSSignature = new List<ArgumentDecl>(),
            AvailabilityAnnotations = null
        };

        return new PropertyDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeSpec = returnTypeSpec,
            HasStorage = false,
            IsStatic = true,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            AvailabilityAnnotations = null
        };
    }

    /// <summary>
    /// Emission harness: builds a generic enum (`Wrapper&lt;T&gt;`) with a single
    /// `where T == TestModule.ConcreteA` extension property, runs
    /// <see cref="ConstrainedExtensionEmitter.EmitConstrainedExtensions"/>, and
    /// returns the C# + Swift output for the test to assert against.
    /// </summary>
    private static (string csOutput, string swiftOutput) EmitForGenericEnumWithProperty(
        string propertyName,
        string returnTypeName,
        IReadOnlyList<(SwiftTypeName, TypeRecord)>? extraTypes = null)
    {
        var tdb = BuildEmissionTypeDatabase(extraTypes);

        var enumDecl = CreateGenericEnumDecl("Wrapper", "T");
        enumDecl.Properties.Add(CreatePropertyWithConstraint(propertyName, "TestModule.ConcreteA",
            returnTypeSpec: new NamedTypeSpec(returnTypeName)));

        var csSw = new StringWriter();
        var csWriter = new CSharpWriter(csSw);
        var swiftSw = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftSw);
        var emissionContext = new ModuleEmissionContext();
        ILogger logger = NullLogger<ConstrainedExtensionEmitterTests>.Instance;

        ConstrainedExtensionEmitter.EmitConstrainedExtensions(
            csWriter, swiftWriter, enumDecl, tdb, emissionContext, logger);

        return (csSw.ToString(), swiftSw.ToString());
    }

    private static TypeDatabase BuildEmissionTypeDatabase(
        IReadOnlyList<(SwiftTypeName, TypeRecord)>? extraTypes)
    {
        var tdb = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "String"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
            });
        tdb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(SwiftTypeName.FromModuleQualifiedName("TestModule.ConcreteA"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ConcreteA"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ConcreteA"),
                MetadataAccessor = "$s10TestModule9ConcreteAVMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
            });
        if (extraTypes != null)
        {
            foreach (var (name, rec) in extraTypes)
                testModule.RegisterType(name, rec);
        }
        tdb.AddModuleDatabase(testModule);

        return tdb;
    }

    private static EnumDecl CreateGenericEnumDecl(string name, string genericParamName)
    {
        return new EnumDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}OMa",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", genericParamName, new(), new())
            },
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            Cases = new List<EnumCaseDecl>(),
            MetadataAccessor = "",
            AvailabilityAnnotations = null,
        };
    }

    private static PropertyDecl CreatePropertyWithConstraint(string name, string concreteType, TypeSpec returnTypeSpec)
    {
        var conformance = new GenericParameterConformance(
            new[] { "τ_0_0" },
            SwiftTypeName.FromModuleQualifiedName(concreteType),
            ConformanceKind.ConcreteType);

        var getterMethod = new MethodDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s10TestModule7WrapperV{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T", new List<GenericParameterConformance> { conformance }, new())
            },
            CSSignature = new List<ArgumentDecl>(),
            AvailabilityAnnotations = null
        };

        return new PropertyDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeSpec = returnTypeSpec,
            HasStorage = false,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            AvailabilityAnnotations = null
        };
    }

    // ==================== Fix J: method specializations + open-generic returns ====================
    //
    // Coverage for the Fix J extensions in ConstrainedExtensionEmitter:
    //   - ExtractSameTypeConstraintForMethod parallels the property-side helper for
    //     MethodDecl shapes (constraints live on the method's GenericParameters
    //     rather than on a getter accessor).
    //   - FindConstrainedMethodSpecializations groups methods by (name, isStatic, paramSig)
    //     and accepts only all-constrained sibling groups, mirroring the property path.
    //   - FindOpenGenericReturnProperties surfaces the `payloadValue` shape: properties
    //     on the unconstrained base extension whose return spec references the parent's
    //     open generic param.
    //   - SubstituteParentGenericParameter rewrites the return spec for a single-param
    //     parent. Multi-param parents return null (caller drops the surface for that
    //     specialization rather than guess which name to substitute).

    [Fact]
    public void ExtractSameTypeConstraintForMethod_WithConcreteConstraint_ReturnsTarget()
    {
        var method = CreateMethodWithConcreteConstraint("temperature", "TestModule.ConcreteA",
            new NamedTypeSpec("Swift.String"));
        var result = ConstrainedExtensionEmitter.ExtractSameTypeConstraintForMethod(method);

        Assert.NotNull(result);
        Assert.Equal("TestModule", result!.Module);
        Assert.Equal("ConcreteA", result.Name);
    }

    [Fact]
    public void ExtractSameTypeConstraintForMethod_WithProtocolConstraint_ReturnsNull()
    {
        // Protocol conformances are not same-type — only ConformanceKind.ConcreteType
        // counts as a constrained-extension specialization.
        var method = CreateMethodWithProtocolConstraint("foo", "TestModule.SomeProtocol");
        var result = ConstrainedExtensionEmitter.ExtractSameTypeConstraintForMethod(method);
        Assert.Null(result);
    }

    [Fact]
    public void ExtractSameTypeConstraintForMethod_NoGenericParams_ReturnsNull()
    {
        var method = CreateUnconstrainedMethod("foo");
        var result = ConstrainedExtensionEmitter.ExtractSameTypeConstraintForMethod(method);
        Assert.Null(result);
    }

    [Fact]
    public void FindConstrainedMethodSpecializations_NoGenericType_ReturnsEmpty()
    {
        var typeDecl = CreateGenericStructDecl("Wrapper", "T");
        // No methods at all
        var result = ConstrainedExtensionEmitter.FindConstrainedMethodSpecializations(typeDecl);
        Assert.Empty(result);
    }

    [Fact]
    public void FindConstrainedMethodSpecializations_SingleSpecPerName_GroupsByConcreteType()
    {
        // Canonical WeatherKit shape: static factory `temperature()` defined on two
        // different `where T == Concrete` extensions. Each surfaces under its
        // concrete type separately.
        var typeDecl = CreateGenericStructDecl("Wrapper", "T");
        typeDecl.Methods.Add(CreateMethodWithConcreteConstraint("temperature", "TestModule.Alpha",
            new NamedTypeSpec("Swift.String"), isStatic: true));
        typeDecl.Methods.Add(CreateMethodWithConcreteConstraint("humidity", "TestModule.Beta",
            new NamedTypeSpec("Swift.String"), isStatic: true));

        var result = ConstrainedExtensionEmitter.FindConstrainedMethodSpecializations(typeDecl);
        Assert.Equal(2, result.Count);

        var alpha = SwiftTypeName.FromModuleQualifiedName("TestModule.Alpha");
        var beta = SwiftTypeName.FromModuleQualifiedName("TestModule.Beta");
        Assert.True(result.ContainsKey(alpha));
        Assert.True(result.ContainsKey(beta));
        Assert.Single(result[alpha]);
        Assert.Equal("temperature", result[alpha][0].Name);
        Assert.Single(result[beta]);
        Assert.Equal("humidity", result[beta][0].Name);
    }

    [Fact]
    public void FindConstrainedMethodSpecializations_MixedConstrainedAndUnconstrained_RejectsGroup()
    {
        // Mirrors the property-side rule: if any sibling in a (name, isStatic, paramSig)
        // group lacks a same-type constraint, the whole group is dropped to avoid
        // namespace collision with the open-generic emission of the same overload.
        var typeDecl = CreateGenericStructDecl("Wrapper", "T");
        typeDecl.Methods.Add(CreateMethodWithConcreteConstraint("foo", "TestModule.Alpha",
            new NamedTypeSpec("Swift.String"), isStatic: true));
        typeDecl.Methods.Add(CreateUnconstrainedMethod("foo", isStatic: true));

        var result = ConstrainedExtensionEmitter.FindConstrainedMethodSpecializations(typeDecl);
        Assert.Empty(result);
    }

    [Fact]
    public void FindConstrainedMethodSpecializations_AsyncMethod_Skipped()
    {
        // Async/throws are out of scope for the first method-extension pass — see
        // gates inside FindConstrainedMethodSpecializations. Sync-only siblings
        // therefore won't collide with hypothetical async overloads of the same name.
        var typeDecl = CreateGenericStructDecl("Wrapper", "T");
        var asyncMethod = CreateMethodWithConcreteConstraint("foo", "TestModule.Alpha",
            new NamedTypeSpec("Swift.String"), isStatic: true);
        asyncMethod.IsAsync = true;
        typeDecl.Methods.Add(asyncMethod);

        var result = ConstrainedExtensionEmitter.FindConstrainedMethodSpecializations(typeDecl);
        Assert.Empty(result);
    }

    [Fact]
    public void FindConstrainedMethodSpecializations_ConstructorMethod_Skipped()
    {
        // Constructors take a bespoke `From{Conformer}` factory shape that the
        // standard constrained-extension method emitter doesn't model.
        var typeDecl = CreateGenericStructDecl("Wrapper", "T");
        var ctor = CreateMethodWithConcreteConstraint("init", "TestModule.Alpha",
            new NamedTypeSpec("Swift.String"), isStatic: false);
        ctor.IsConstructor = true;
        typeDecl.Methods.Add(ctor);

        var result = ConstrainedExtensionEmitter.FindConstrainedMethodSpecializations(typeDecl);
        Assert.Empty(result);
    }

    [Fact]
    public void FindOpenGenericReturnProperties_PropertyReferencingParentParam_IsCollected()
    {
        // payloadValue shape: extension property on the unconstrained base extension
        // whose return type is the parent's open generic param itself
        // (e.g. `var payloadValue: SignedType { get }`). The parser canonicalizes
        // generic-param refs as τ_0_0; matching the parent's `TypeName` field.
        var typeDecl = CreateGenericStructDecl("Wrapper", "SignedType");
        typeDecl.Properties.Add(CreateOpenGenericReturnProperty(
            "payloadValue", new NamedTypeSpec("τ_0_0"), isStatic: false));

        var result = ConstrainedExtensionEmitter.FindOpenGenericReturnProperties(typeDecl);
        Assert.Single(result);
        Assert.Equal("payloadValue", result[0].Name);
    }

    [Fact]
    public void FindOpenGenericReturnProperties_StaticProperty_Excluded()
    {
        // The current emit shape passes `self.Payload.DangerousGetHandle()` and
        // there is no `self` for static accessors — exclude them up front.
        var typeDecl = CreateGenericStructDecl("Wrapper", "SignedType");
        typeDecl.Properties.Add(CreateOpenGenericReturnProperty(
            "payloadValue", new NamedTypeSpec("τ_0_0"), isStatic: true));

        var result = ConstrainedExtensionEmitter.FindOpenGenericReturnProperties(typeDecl);
        Assert.Empty(result);
    }

    [Fact]
    public void FindOpenGenericReturnProperties_ConstrainedProperty_NotCollected()
    {
        // Properties carrying their own `where T == Concrete` constraint already
        // surface via FindConstrainedSpecializations — they are not in the
        // open-generic-return surface.
        var typeDecl = CreateGenericStructDecl("Wrapper", "T");
        typeDecl.Properties.Add(CreatePropertyWithConstraint(
            "payloadValue", "TestModule.ConcreteA",
            returnTypeSpec: new NamedTypeSpec("τ_0_0")));

        var result = ConstrainedExtensionEmitter.FindOpenGenericReturnProperties(typeDecl);
        Assert.Empty(result);
    }

    [Fact]
    public void FindOpenGenericReturnProperties_ReturnSpecDoesNotReferenceParentParam_Excluded()
    {
        // Unconstrained property whose return type is `Swift.String` — does not
        // reference the parent param, so it surfaces via the normal property path
        // and is not in the open-generic-return surface.
        var typeDecl = CreateGenericStructDecl("Wrapper", "T");
        typeDecl.Properties.Add(CreateUnconstrainedProperty("plain"));

        var result = ConstrainedExtensionEmitter.FindOpenGenericReturnProperties(typeDecl);
        Assert.Empty(result);
    }

    [Fact]
    public void SubstituteParentGenericParameter_DirectHit_ReturnsConcreteSpec()
    {
        // payloadValue: SignedType — direct substitution of the open param. The
        // parser uses the canonical τ_0_0 form for generic-param refs, so the
        // input TypeSpec must use that spelling.
        var parent = CreateGenericStructDecl("Wrapper", "SignedType");
        var concrete = SwiftTypeName.FromModuleQualifiedName("TestModule.ConcreteA");

        var result = ConstrainedExtensionEmitter.SubstituteParentGenericParameter(
            new NamedTypeSpec("τ_0_0"), parent, concrete);

        Assert.NotNull(result);
        Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("TestModule.ConcreteA", ((NamedTypeSpec)result!).Name);
    }

    [Fact]
    public void SubstituteParentGenericParameter_MultiParamParent_ReturnsNull()
    {
        // Multi-param parents are intentionally unsupported — there's no signal in
        // the constrained-extension grouping for which open name maps to which
        // concrete name. Caller drops the open-generic-return surface for that
        // specialization rather than guess.
        var parent = CreateGenericStructDecl("Wrapper", "T");
        parent.GenericParameters.Add(new GenericArgumentDecl("τ_0_1", "U", new(), new()));

        var concrete = SwiftTypeName.FromModuleQualifiedName("TestModule.ConcreteA");

        var result = ConstrainedExtensionEmitter.SubstituteParentGenericParameter(
            new NamedTypeSpec("τ_0_0"), parent, concrete);

        Assert.Null(result);
    }

    [Fact]
    public void SubstituteParentGenericParameter_TupleSpec_ReturnsNull()
    {
        // Tuple / closure / nested-type substitutions are intentionally unsupported
        // for the initial open-generic-return surface — they expand the matrix
        // significantly without matching canonical Apple-framework shapes.
        var parent = CreateGenericStructDecl("Wrapper", "T");
        var concrete = SwiftTypeName.FromModuleQualifiedName("TestModule.ConcreteA");

        var tuple = new TupleTypeSpec(new TypeSpec[]
        {
            new NamedTypeSpec("τ_0_0"),
            new NamedTypeSpec("Swift.Int"),
        });

        var result = ConstrainedExtensionEmitter.SubstituteParentGenericParameter(
            tuple, parent, concrete);

        Assert.Null(result);
    }

    [Fact]
    public void SubstituteParentGenericParameter_GenericArgSubstitution_ReturnsRewrittenSpec()
    {
        // Nested case: `Optional<T>` should rewrite the inner `T` in place,
        // preserving the outer Optional shape so the renderer downstream still
        // produces a valid ABI signature.
        var parent = CreateGenericStructDecl("Wrapper", "T");
        var concrete = SwiftTypeName.FromModuleQualifiedName("TestModule.ConcreteA");

        var optionalT = new NamedTypeSpec("Swift.Optional", new[] { (TypeSpec)new NamedTypeSpec("τ_0_0") });

        var result = ConstrainedExtensionEmitter.SubstituteParentGenericParameter(
            optionalT, parent, concrete);

        Assert.NotNull(result);
        var named = Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("Swift.Optional", named.Name);
        Assert.Single(named.GenericParameters);
        var inner = Assert.IsType<NamedTypeSpec>(named.GenericParameters[0]);
        Assert.Equal("TestModule.ConcreteA", inner.Name);
    }

    // ==================== Method-extension emission tests ====================

    [Fact]
    public void EmitConstrainedExtensions_GenericEnum_StaticPrimitiveMethod_EmitsExtensionAndPInvoke()
    {
        // Mirrors a canonical WeatherKit-shape factory: static method on a
        // `where T == Concrete` extension returning a primitive (Int).
        var (csOutput, swiftOutput) = EmitForGenericEnumWithMethod(
            methodName: "rank",
            returnTypeSpec: new NamedTypeSpec("Swift.Int"),
            isStatic: true);

        // Method emits as a plain static (no `this`) on the extensions class — C# can't
        // dispatch static extension methods on closed generic instantiations, so static
        // factories live on the extensions class itself.
        Assert.Contains("public static partial class TestModule_DWrapper_TestModule_DConcreteAExtensions", csOutput);
        // Swift.Int -> nint (word-sized) per ExtensionMarshallingHelper.ResolveCSharpTypeName.
        Assert.Contains("public static nint Rank()", csOutput);
        // Symbol prefix distinguishes methods from properties.
        Assert.Contains("SBW_CEMethod_TestModule_DWrapper_TestModule_DConcreteA_rank_static", csOutput);
        // Static methods omit `_self` from the P/Invoke signature entirely.
        Assert.Contains("partial nint SBW_CEMethod_TestModule_DWrapper_TestModule_DConcreteA_rank_static()", csOutput);

        // Swift wrapper calls the static factory on the closed-generic type.
        Assert.Contains("@_cdecl(\"SBW_CEMethod_TestModule_DWrapper_TestModule_DConcreteA_rank_static\")", swiftOutput);
        Assert.Contains("TestModule.Wrapper<TestModule.ConcreteA>.rank()", swiftOutput);
    }

    [Fact]
    public void EmitConstrainedExtensions_GenericEnum_InstanceVoidMethod_EmitsThisExtensionAndSelfArg()
    {
        // Instance methods emit as `this` extension methods so consumers can call them
        // directly on a closed-generic value. Void return uses the Primitive shape
        // synthesized for void, so the wrapper takes only `_self`.
        var (csOutput, swiftOutput) = EmitForGenericEnumWithMethod(
            methodName: "ping",
            returnTypeSpec: TupleTypeSpec.Empty,
            isStatic: false);

        Assert.Contains("public static void Ping(this Wrapper<TestModule.ConcreteA> self)", csOutput);
        Assert.Contains("partial void SBW_CEMethod_TestModule_DWrapper_TestModule_DConcreteA_ping_instance(IntPtr _self)", csOutput);
        // The C# call passes the SafeHandle.
        Assert.Contains("self.Payload.DangerousGetHandle()", csOutput);

        // Swift wrapper materializes `obj` from `self_` and invokes the no-arg method.
        Assert.Contains("@_cdecl(\"SBW_CEMethod_TestModule_DWrapper_TestModule_DConcreteA_ping_instance\")", swiftOutput);
        Assert.Contains("_ self_: UnsafeRawPointer", swiftOutput);
        Assert.Contains("obj.ping()", swiftOutput);
    }

    [Fact]
    public void EmitConstrainedExtensions_GenericEnum_MethodWithParameters_NotEmitted()
    {
        // Methods with parameters are out of scope for the first method-extension
        // pass — closure / complex parameter marshalling needs the full param-projection
        // pipeline. The emitter should skip them silently (logger.LogDebug). Outputs
        // should not contain the would-be symbol.
        var typeDecl = CreateGenericEnumDecl("Wrapper", "T");
        var methodWithArgs = CreateMethodWithConcreteConstraint(
            "withArg", "TestModule.ConcreteA",
            returnTypeSpec: new NamedTypeSpec("Swift.Int"),
            isStatic: true);
        methodWithArgs.CSSignature.Add(new ArgumentDecl
        {
            Name = "x",
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            PrivateName = "x",
            IsInOut = false,
            IsGeneric = false,
        });
        typeDecl.Methods.Add(methodWithArgs);

        var (csOutput, _) = RunEmitter(typeDecl);

        Assert.DoesNotContain("SBW_CEMethod_TestModule_DWrapper_TestModule_DConcreteA_withArg", csOutput);
        Assert.DoesNotContain("WithArg(", csOutput);
    }

    [Fact]
    public void EmitConstrainedExtensions_GenericEnum_OpenGenericReturnProperty_ReSurfacesPerSpecialization()
    {
        // payloadValue shape: an open-generic-return property anchored by a
        // sibling constrained-extension property. The property re-surfaces under
        // each specialization with the open param substituted.
        var typeDecl = CreateGenericEnumDecl("Wrapper", "SignedType");

        // Anchor: a regular `where T == ConcreteA` property forces the
        // specialization class to emit. Without it, FindOpenGenericReturnProperties
        // does not run (no anchor specialization to attach to).
        typeDecl.Properties.Add(CreatePropertyWithConstraint(
            "anchor", "TestModule.ConcreteA",
            returnTypeSpec: new NamedTypeSpec("Swift.String")));
        typeDecl.Properties.Add(CreateOpenGenericReturnProperty(
            "payloadValue", new NamedTypeSpec("τ_0_0"), isStatic: false));

        // ConcreteA is registered with RequiresMemoryManagement so it routes via
        // the NonFrozenStruct shape. The substituted return spec is
        // TestModule.ConcreteA — same as if the property were declared
        // `where SignedType == ConcreteA`.
        var (csOutput, swiftOutput) = RunEmitter(typeDecl);

        Assert.Contains("public static TestModule.ConcreteA GetPayloadValue(this Wrapper<TestModule.ConcreteA> self)", csOutput);
        // The substituted concrete name flows into both the C# marshal call and
        // the Swift wrapper's initializeMemory(as:) line.
        Assert.Contains("SwiftMarshal.MarshalFromSwift<TestModule.ConcreteA>(buffer)", csOutput);
        Assert.Contains("resultPtr.initializeMemory(as: TestModule.ConcreteA.self, repeating: result, count: 1)", swiftOutput);
    }

    // ==================== Method test helpers ====================

    private static MethodDecl CreateMethodWithConcreteConstraint(
        string name, string concreteType, TypeSpec returnTypeSpec, bool isStatic = true)
    {
        var conformance = new GenericParameterConformance(
            new[] { "τ_0_0" },
            SwiftTypeName.FromModuleQualifiedName(concreteType),
            ConformanceKind.ConcreteType);

        return new MethodDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s10TestModule7WrapperO{name}",
            MethodType = isStatic ? MethodType.Static : MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T", new List<GenericParameterConformance> { conformance }, new())
            },
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    ParentDecl = null,
                    ModuleDecl = null,
                    SwiftTypeSpec = returnTypeSpec,
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                },
            },
            AvailabilityAnnotations = null,
        };
    }

    private static MethodDecl CreateMethodWithProtocolConstraint(string name, string protocolType)
    {
        var conformance = new GenericParameterConformance(
            new[] { "τ_0_0" },
            SwiftTypeName.FromModuleQualifiedName(protocolType),
            ConformanceKind.Protocol);

        return new MethodDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s10TestModule7WrapperO{name}",
            MethodType = MethodType.Static,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T", new List<GenericParameterConformance> { conformance }, new())
            },
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    ParentDecl = null,
                    ModuleDecl = null,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                },
            },
            AvailabilityAnnotations = null,
        };
    }

    private static MethodDecl CreateUnconstrainedMethod(string name, bool isStatic = true)
    {
        return new MethodDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s10TestModule7WrapperO{name}",
            MethodType = isStatic ? MethodType.Static : MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T", new(), new())
            },
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    ParentDecl = null,
                    ModuleDecl = null,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                },
            },
            AvailabilityAnnotations = null,
        };
    }

    private static PropertyDecl CreateOpenGenericReturnProperty(
        string name, TypeSpec returnTypeSpec, bool isStatic)
    {
        // Open-generic-return properties live on the *unconstrained* base
        // extension — getter has no same-type constraint on its generic params.
        var getterMethod = new MethodDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s10TestModule7WrapperO{name}",
            MethodType = isStatic ? MethodType.Static : MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T", new(), new()) // no constraints
            },
            CSSignature = new List<ArgumentDecl>(),
            AvailabilityAnnotations = null,
        };

        return new PropertyDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeSpec = returnTypeSpec,
            HasStorage = false,
            IsStatic = isStatic,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            AvailabilityAnnotations = null,
        };
    }

    /// <summary>
    /// Run the emitter end-to-end on a pre-built generic enum and return the
    /// captured C# + Swift output. Centralizes the IO + logger plumbing for
    /// emission tests that need to add methods / open-generic-return properties
    /// rather than the single-property shape covered by <see cref="EmitForGenericEnumWithProperty"/>.
    /// </summary>
    private static (string csOutput, string swiftOutput) RunEmitter(TypeDecl typeDecl)
    {
        var tdb = BuildEmissionTypeDatabase(extraTypes: null);

        var csSw = new StringWriter();
        var csWriter = new CSharpWriter(csSw);
        var swiftSw = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftSw);
        var emissionContext = new ModuleEmissionContext();
        ILogger logger = NullLogger<ConstrainedExtensionEmitterTests>.Instance;

        ConstrainedExtensionEmitter.EmitConstrainedExtensions(
            csWriter, swiftWriter, typeDecl, tdb, emissionContext, logger);

        return (csSw.ToString(), swiftSw.ToString());
    }

    /// <summary>
    /// Emit a generic enum with a single constrained-extension *method* under
    /// `where T == TestModule.ConcreteA`. Mirrors <see cref="EmitForGenericEnumWithProperty"/>
    /// but for the method-extension surface.
    /// </summary>
    private static (string csOutput, string swiftOutput) EmitForGenericEnumWithMethod(
        string methodName,
        TypeSpec returnTypeSpec,
        bool isStatic)
    {
        var enumDecl = CreateGenericEnumDecl("Wrapper", "T");
        enumDecl.Methods.Add(CreateMethodWithConcreteConstraint(
            methodName, "TestModule.ConcreteA", returnTypeSpec, isStatic));

        return RunEmitter(enumDecl);
    }
}
