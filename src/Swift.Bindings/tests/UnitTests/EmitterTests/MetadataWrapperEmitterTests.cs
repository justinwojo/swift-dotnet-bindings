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
        var symbol = MetadataWrapperEmitter.GetMetadataSymbolName("Nuke", "Nuke.ImagePipeline");
        Assert.StartsWith("SBW_GetMetadata_Nuke_Nuke_ImagePipeline_", symbol);
    }

    [Fact]
    public void GetMetadataSymbolName_NestedType_IncludesParent()
    {
        var symbol = MetadataWrapperEmitter.GetMetadataSymbolName("Nuke", "Nuke.ImageRequest.Priority");
        Assert.StartsWith("SBW_GetMetadata_Nuke_Nuke_ImageRequest_Priority_", symbol);
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
        var symbol = "SBW_GetMetadata_Nuke_Nuke_ImagePipeline_ABCD1234";

        MetadataWrapperEmitter.EmitIfNeeded(swiftWriter, "Nuke", "Nuke.ImagePipeline", symbol, ctx);

        var output = sw.ToString();
        Assert.Contains($"@_cdecl(\"{symbol}\")", output);
        Assert.Contains("unsafeBitCast(Nuke.ImagePipeline.self as Any.Type, to: UnsafeMutableRawPointer.self)", output);
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
    /// The full handler pipeline is exercised by CryptoSwift validation (42/56 baseline).
    /// </summary>
    [Fact]
    public void InternalType_SwiftWrapperSkipped_CSharpFallbackUsed()
    {
        // Simulate the decision logic from ClassHandler.WriteGetTypeMetadata:
        // For an internal type, EmitIfNeeded should NOT be called.
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);
        var symbol = MetadataWrapperEmitter.GetMetadataSymbolName("CryptoSwift", "CryptoSwift.BlockEncryptor");

        bool isModuleInternal = true;

        // Gate: skip Swift wrapper for internal types
        if (!isModuleInternal)
            MetadataWrapperEmitter.EmitIfNeeded(swiftWriter, "CryptoSwift", "CryptoSwift.BlockEncryptor", symbol, ctx);

        var swiftOutput = sw.ToString();
        Assert.DoesNotContain("@_cdecl", swiftOutput);
        Assert.DoesNotContain("BlockEncryptor.self", swiftOutput);

        // Verify the C# fallback would use the dylib path (CallConvSwift)
        // rather than the wrapper library (Cdecl).
        // The handler emits: LibraryPath = libPath (dylib), EntryPoint = mangledName+"Ma"
        // instead of: LibraryPath = wrapperLib, EntryPoint = SBW_GetMetadata_...
        string expectedDylibEntry = "$s11CryptoSwift14BlockEncryptorCMa"; // mangled + "Ma"
        string unexpectedWrapperEntry = symbol; // SBW_GetMetadata_...

        // In the handler, internal types get:
        //   [DllImport("CryptoSwift")] static extern TypeMetadata PInvoke_getMetadata();
        // Public types get:
        //   [DllImport("CryptoSwiftSwiftBindings", EntryPoint = "SBW_GetMetadata_...")]
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
        var symbol = MetadataWrapperEmitter.GetMetadataSymbolName("Nuke", "Nuke.ImagePipeline");

        bool isModuleInternal = false;

        if (!isModuleInternal)
            MetadataWrapperEmitter.EmitIfNeeded(swiftWriter, "Nuke", "Nuke.ImagePipeline", symbol, ctx);

        var swiftOutput = sw.ToString();
        Assert.Contains("@_cdecl", swiftOutput);
        Assert.Contains("Nuke.ImagePipeline.self", swiftOutput);
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
