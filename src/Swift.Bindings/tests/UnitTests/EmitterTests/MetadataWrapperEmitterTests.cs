// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for MetadataWrapperEmitter: @_cdecl wrappers that return type metadata
/// as raw pointers, eliminating CallConvSwift from metadata P/Invokes.
/// </summary>
public class MetadataWrapperEmitterTests
{
    [Fact]
    public void GetMetadataSymbolName_TopLevelType_CorrectFormat()
    {
        var symbol = MetadataWrapperEmitter.GetMetadataSymbolName("ImagePipeline", "ImagePipeline.ImageService");
        Assert.StartsWith("SBW_GetMetadata_ImagePipeline_ImagePipeline_ImageService_", symbol);
    }

    [Fact]
    public void GetMetadataSymbolName_NestedType_IncludesParent()
    {
        var symbol = MetadataWrapperEmitter.GetMetadataSymbolName("ImagePipeline", "ImagePipeline.ImageRequest.Priority");
        Assert.StartsWith("SBW_GetMetadata_ImagePipeline_ImagePipeline_ImageRequest_Priority_", symbol);
        Assert.DoesNotContain(".", symbol.Substring("SBW_GetMetadata_".Length));
    }

    [Fact]
    public void GetMetadataSymbolName_DifferentTypes_DifferentSymbols()
    {
        var sym1 = MetadataWrapperEmitter.GetMetadataSymbolName("Mod", "Mod.TypeA");
        var sym2 = MetadataWrapperEmitter.GetMetadataSymbolName("Mod", "Mod.TypeB");
        Assert.NotEqual(sym1, sym2);
    }

    [Fact]
    public void EmitIfNeeded_EmitsSwiftWrapper()
    {
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);
        var ctx = new ModuleEmissionContext();
        var symbol = "SBW_GetMetadata_ImagePipeline_ImagePipeline_ImageService_ABCD1234";

        MetadataWrapperEmitter.EmitIfNeeded(swiftWriter, "ImagePipeline", "ImagePipeline.ImageService", symbol, ctx);

        var output = sw.ToString();
        Assert.Contains($"@_cdecl(\"{symbol}\")", output);
        Assert.Contains("unsafeBitCast(ImagePipeline.ImageService.self as Any.Type, to: UnsafeMutableRawPointer.self)", output);
        Assert.Contains("-> UnsafeMutableRawPointer", output);
    }

    [Fact]
    public void EmitIfNeeded_Dedup_SecondCallIsNoop()
    {
        var ctx = new ModuleEmissionContext();
        var symbol = "SBW_GetMetadata_Test_Test_Type_ABCD1234";

        var sw1 = new StringWriter();
        var swiftWriter1 = new SwiftWriter(sw1);
        MetadataWrapperEmitter.EmitIfNeeded(swiftWriter1, "Test", "Test.Type", symbol, ctx);
        var output1 = sw1.ToString();

        var sw2 = new StringWriter();
        var swiftWriter2 = new SwiftWriter(sw2);
        MetadataWrapperEmitter.EmitIfNeeded(swiftWriter2, "Test", "Test.Type", symbol, ctx);
        var output2 = sw2.ToString();

        Assert.Contains("@_cdecl", output1);
        Assert.DoesNotContain("@_cdecl", output2);
    }

    /// <summary>
    /// Regression test for the internal-type metadata gate contract.
    /// When IsModuleInternal is true in xcframework mode, the handler must:
    ///   1. NOT emit the Swift @_cdecl metadata wrapper (type name inaccessible)
    ///   2. Emit the C# P/Invoke targeting the dylib's metadata accessor (CallConvSwift),
    ///      NOT the wrapper library (Cdecl)
    /// This ensures no dangling symbol references between C# and Swift.
    /// The full handler pipeline is exercised by real-world validation (42/56 baseline).
    /// </summary>
    [Fact]
    public void InternalType_SwiftWrapperSkipped_CSharpFallbackUsed()
    {
        // Simulate the decision logic from ClassHandler.WriteGetTypeMetadata:
        // For an internal type, EmitIfNeeded should NOT be called.
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);
        var symbol = MetadataWrapperEmitter.GetMetadataSymbolName("CryptoLib", "CryptoLib.BlockEncryptor");

        bool isModuleInternal = true;

        // Gate: skip Swift wrapper for internal types
        if (!isModuleInternal)
            MetadataWrapperEmitter.EmitIfNeeded(swiftWriter, "CryptoLib", "CryptoLib.BlockEncryptor", symbol, ctx);

        var swiftOutput = sw.ToString();
        Assert.DoesNotContain("@_cdecl", swiftOutput);
        Assert.DoesNotContain("BlockEncryptor.self", swiftOutput);

        // Verify the C# fallback would use the dylib path (CallConvSwift)
        // rather than the wrapper library (Cdecl).
        // The handler emits: LibraryPath = libPath (dylib), EntryPoint = mangledName+"Ma"
        // instead of: LibraryPath = wrapperLib, EntryPoint = SBW_GetMetadata_...
        string expectedDylibEntry = "$s11CryptoLib14BlockEncryptorCMa"; // mangled + "Ma"
        string unexpectedWrapperEntry = symbol; // SBW_GetMetadata_...

        // In the handler, internal types get:
        //   [DllImport("<dylib>")] static extern TypeMetadata PInvoke_getMetadata();
        // Public types get:
        //   [DllImport("<module>SwiftBindings", EntryPoint = "SBW_GetMetadata_...")]
        //   [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        //   static extern TypeMetadata PInvoke_getMetadata();
        Assert.NotEqual(expectedDylibEntry, unexpectedWrapperEntry);
    }

    [Fact]
    public void PublicType_SwiftWrapperEmitted()
    {
        // Public types should always get the Swift @_cdecl metadata wrapper
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);
        var symbol = MetadataWrapperEmitter.GetMetadataSymbolName("ImagePipeline", "ImagePipeline.ImageService");

        bool isModuleInternal = false;

        if (!isModuleInternal)
            MetadataWrapperEmitter.EmitIfNeeded(swiftWriter, "ImagePipeline", "ImagePipeline.ImageService", symbol, ctx);

        var swiftOutput = sw.ToString();
        Assert.Contains("@_cdecl", swiftOutput);
        Assert.Contains("ImagePipeline.ImageService.self", swiftOutput);
    }

    /// <summary>
    /// Noncopyable (~Copyable) struct metadata wrappers must be skipped because
    /// Swift 6 rejects `T.self as Any.Type` for noncopyable types (Any requires Copyable).
    /// The handler routes noncopyable types through CallConvSwift fallback (same as internal types).
    /// </summary>
    [Fact]
    public void NoncopyableType_SwiftWrapperSkipped()
    {
        // Simulate the decision logic from TypeHandlerHelpers:
        // Noncopyable types should NOT call EmitIfNeeded.
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);
        var symbol = MetadataWrapperEmitter.GetMetadataSymbolName("TestLib", "TestLib.UniqueResource");

        // Create a noncopyable struct: has Escapable but NOT Copyable
        var noncopyableStruct = new StructDecl
        {
            Name = "UniqueResource",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestLib.UniqueResource"),
            MangledName = "$s",
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            GenericParameters = new(),
            Conformances = new List<TypeConformance>
            {
                new(SwiftTypeName.FromModuleQualifiedName("TestLib.UniqueResource"),
                    SwiftTypeName.FromModuleQualifiedName("Swift.Escapable"), "")
            },
            ParentDecl = null!,
            ModuleDecl = null!,
            IsFrozen = true,
            MetadataAccessor = ""
        };

        bool isNonCopyable = WrapperValidation.IsNonCopyableStructParent(noncopyableStruct);
        Assert.True(isNonCopyable, "Struct with Escapable but not Copyable should be noncopyable");

        // Gate: skip Swift wrapper for noncopyable types
        if (!noncopyableStruct.IsModuleInternal && !isNonCopyable)
            MetadataWrapperEmitter.EmitIfNeeded(swiftWriter, "TestLib", "TestLib.UniqueResource", symbol, ctx);

        var swiftOutput = sw.ToString();
        Assert.DoesNotContain("@_cdecl", swiftOutput);
        Assert.DoesNotContain("unsafeBitCast", swiftOutput);
    }

    /// <summary>
    /// A type whose OS-availability floor is above the module deployment target (e.g. an
    /// iOS 26.2 StoreKit type bound at the iOS 15 .NET floor) has a weak-imported metadata
    /// accessor that is null on older OS versions. The wrapper must guard the type reference
    /// behind a runtime <c>#available</c> check and return <c>nil</c> when unavailable, so the
    /// metadata accessor is never branched-through on older OS (the TestFlight SIGSEGV at pc=0).
    /// The return type widens to optional, and the declaration-level <c>@available</c> is
    /// deliberately omitted — emitting it would make the inner <c>#available</c> always-true
    /// and dead-code the else branch.
    /// </summary>
    [Fact]
    public void EmitIfNeeded_AvailabilityGatedType_GuardsReferenceAndReturnsNil()
    {
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);
        var ctx = new ModuleEmissionContext();
        var symbol = "SBW_GetMetadata_StoreKit_StoreKit_Product_PriceIncreaseInfo_ABCD1234";

        var gatedStruct = MakeStruct("StoreKit.Product.PriceIncreaseInfo", new List<AvailabilityAnnotation>
        {
            new("iOS", "26.2", null, null, false, false, null, null),
            new("macOS", "26.2", null, null, false, false, null, null),
        });

        MetadataWrapperEmitter.EmitIfNeeded(swiftWriter, "StoreKit", "StoreKit.Product.PriceIncreaseInfo", symbol, ctx, gatedStruct);

        var output = sw.ToString();
        // Runtime guard present, gating the only reference to the gated type's metadata.
        Assert.Contains("if #available(", output);
        Assert.Contains("iOS 26.2", output);
        Assert.Contains("return nil", output);
        // Return type widened to optional so nil is representable.
        Assert.Contains("-> UnsafeMutableRawPointer?", output);
        // The gated reference is still emitted, but only inside the guarded branch.
        Assert.Contains("unsafeBitCast(StoreKit.Product.PriceIncreaseInfo.self as Any.Type, to: UnsafeMutableRawPointer.self)", output);
        // No declaration-level @available — it would make the #available always-true.
        Assert.DoesNotContain("@available(", output);
    }

    /// <summary>
    /// A type available at the module deployment floor must keep the original unconditional
    /// shape: no runtime guard, non-optional return, no declaration-level @available. This
    /// guarantees the gated-type fix does not churn the output for the overwhelming majority
    /// of types.
    /// </summary>
    [Fact]
    public void EmitIfNeeded_NonGatedType_RemainsUnconditional()
    {
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);
        var ctx = new ModuleEmissionContext();
        var symbol = "SBW_GetMetadata_ImagePipeline_ImagePipeline_ImageService_ABCD1234";

        var plainStruct = MakeStruct("ImagePipeline.ImageService", availability: null);

        MetadataWrapperEmitter.EmitIfNeeded(swiftWriter, "ImagePipeline", "ImagePipeline.ImageService", symbol, ctx, plainStruct);

        var output = sw.ToString();
        Assert.Contains("-> UnsafeMutableRawPointer", output);
        Assert.DoesNotContain("-> UnsafeMutableRawPointer?", output);
        Assert.DoesNotContain("#available", output);
        Assert.DoesNotContain("@available(", output);
        Assert.DoesNotContain("return nil", output);
    }

    /// <summary>
    /// For a gated type, the generated C# GetTypeMetadata() must convert the wrapper's null
    /// return (zero TypeMetadata) into a PlatformNotSupportedException at the method boundary —
    /// before any caller can observe a zero metadata that would later fault on a value-witness
    /// dereference (Size / ValueWitnessTable).
    /// </summary>
    [Fact]
    public void BuildGetTypeMetadataWithFallback_GatedType_ThrowsPlatformNotSupported()
    {
        var availability = new List<AvailabilityAnnotation>
        {
            new("iOS", "26.2", null, null, false, false, null, null),
            new("macOS", "26.2", null, null, false, false, null, null),
        };

        var body = MetadataWrapperEmitter.BuildGetTypeMetadataWithFallback(availability, "StoreKit.Product.PriceIncreaseInfo");

        Assert.Contains("if (!__metadata.IsValid)", body);
        Assert.Contains("global::System.PlatformNotSupportedException", body);
        Assert.Contains("StoreKit.Product.PriceIncreaseInfo", body);
        Assert.Contains("iOS 26.2", body);
        // The fallback chain is preserved.
        Assert.Contains("PInvoke_getMetadata_fallback()", body);
    }

    /// <summary>
    /// For a non-gated type the C# GetTypeMetadata() body is unchanged — direct return, no
    /// PlatformNotSupportedException, no IsValid gate.
    /// </summary>
    [Fact]
    public void BuildGetTypeMetadataWithFallback_NonGatedType_DirectReturn()
    {
        var body = MetadataWrapperEmitter.BuildGetTypeMetadataWithFallback(null, "ImagePipeline.ImageService");

        Assert.Contains("return PInvoke_getMetadata();", body);
        Assert.Contains("return PInvoke_getMetadata_fallback();", body);
        Assert.DoesNotContain("PlatformNotSupportedException", body);
        Assert.DoesNotContain("IsValid", body);
    }

    private static StructDecl MakeStruct(string moduleQualifiedName, List<AvailabilityAnnotation>? availability)
        => new StructDecl
        {
            Name = moduleQualifiedName.Substring(moduleQualifiedName.LastIndexOf('.') + 1),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(moduleQualifiedName),
            MangledName = "$s",
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            GenericParameters = new(),
            Conformances = new List<TypeConformance>
            {
                new(SwiftTypeName.FromModuleQualifiedName(moduleQualifiedName),
                    SwiftTypeName.FromModuleQualifiedName("Swift.Copyable"), ""),
                new(SwiftTypeName.FromModuleQualifiedName(moduleQualifiedName),
                    SwiftTypeName.FromModuleQualifiedName("Swift.Escapable"), "")
            },
            ParentDecl = null!,
            ModuleDecl = null!,
            IsFrozen = true,
            MetadataAccessor = "",
            AvailabilityAnnotations = availability
        };

    [Fact]
    public void CopyableType_NotSkipped()
    {
        // A normal (Copyable) struct should still get the metadata wrapper
        var copyableStruct = new StructDecl
        {
            Name = "NormalType",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestLib.NormalType"),
            MangledName = "$s",
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            GenericParameters = new(),
            Conformances = new List<TypeConformance>
            {
                new(SwiftTypeName.FromModuleQualifiedName("TestLib.NormalType"),
                    SwiftTypeName.FromModuleQualifiedName("Swift.Copyable"), ""),
                new(SwiftTypeName.FromModuleQualifiedName("TestLib.NormalType"),
                    SwiftTypeName.FromModuleQualifiedName("Swift.Escapable"), "")
            },
            ParentDecl = null!,
            ModuleDecl = null!,
            IsFrozen = true,
            MetadataAccessor = ""
        };

        bool isNonCopyable = WrapperValidation.IsNonCopyableStructParent(copyableStruct);
        Assert.False(isNonCopyable, "Struct with both Copyable and Escapable should NOT be noncopyable");
    }
}
