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
        // Bug 0.10.0 — single-specialization same-type-constraint properties are
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
            Visibility = Visibility.Public,
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
            Visibility = Visibility.Public,
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
            Visibility = Visibility.Public,
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
    // Resolves gap-0.10.0-multispecialization-drops-generic-property-accessors:
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
        Assert.Contains("public static partial class WrapperConcreteAExtensions", csOutput);
        Assert.Contains("public static string GetJwsRepresentation(this Wrapper<TestModule.ConcreteA> self)", csOutput);
        Assert.Contains("SBW_CEGet_TestModule_Wrapper_ConcreteA_jwsRepresentation", csOutput);
        Assert.Contains("SwiftMarshal.ReadUtf8Slice(resultPtr)", csOutput);
        Assert.Contains("(IntPtr resultPtr, IntPtr _self)", csOutput);

        // Swift wrapper hands back via Utf8Slice; @_cdecl + indirect-buffer shape.
        Assert.Contains("@_cdecl(\"SBW_CEGet_TestModule_Wrapper_ConcreteA_jwsRepresentation\")", swiftOutput);
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
        Assert.Contains("@_cdecl(\"SBW_CEGet_TestModule_Wrapper_ConcreteA_signature\")", swiftOutput);
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
        Assert.Contains("partial double SBW_CEGet_TestModule_Wrapper_ConcreteA_signedDate(IntPtr _self)", csOutput);
        Assert.DoesNotContain("SwiftIndirectResult indirectResult, IntPtr _self", csOutput.Replace("\r", "")); // sanity: not the indirect shape
        // Epoch arithmetic mirrors DateProjection.GetReturnPlan(Direct).
        Assert.Contains("AddSeconds(seconds)", csOutput);
        Assert.Contains("new System.DateTimeOffset(2001, 1, 1, 0, 0, 0, System.TimeSpan.Zero)", csOutput);

        // Swift wrapper returns Double directly via timeIntervalSinceReferenceDate.
        Assert.Contains("@_cdecl(\"SBW_CEGet_TestModule_Wrapper_ConcreteA_signedDate\")", swiftOutput);
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
        Assert.Contains("NativeMemory.Alloc(16)", csOutput);
        // Buffer is freed in finally — value is copied out before the buffer is released.
        Assert.Contains("*(System.Guid*)buffer", csOutput);
        Assert.Contains("finally", csOutput);
        Assert.Contains("NativeMemory.Free((void*)buffer)", csOutput);

        // Swift wrapper writes the UUID into the caller buffer.
        Assert.Contains("@_cdecl(\"SBW_CEGet_TestModule_Wrapper_ConcreteA_deviceVerificationNonce\")", swiftOutput);
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
        Assert.Contains("NativeMemory.Alloc(16)", csOutput);
        Assert.Contains("(*(Swift.Foundation.Data*)(void*)buffer).ToByteArray()", csOutput);
        Assert.Contains("finally", csOutput);
        Assert.Contains("NativeMemory.Free((void*)buffer)", csOutput);

        // Swift wrapper writes the Data into the caller buffer (it's a 16-byte struct).
        Assert.Contains("@_cdecl(\"SBW_CEGet_TestModule_Wrapper_ConcreteA_headerData\")", swiftOutput);
        Assert.Contains("resultPtr.initializeMemory(as: Foundation.Data.self, repeating: result, count: 1)", swiftOutput);
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
            Visibility = Visibility.Public,
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
}
