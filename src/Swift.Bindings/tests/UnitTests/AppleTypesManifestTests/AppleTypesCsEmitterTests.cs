// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using BindingsGeneration.AppleTypesManifest;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

// Exercises the --emit-apple-types-cs emitter. Assertions check observable behavior
// (attribute tokens, interface implementations, platform normalization, gate refusals)
// rather than exact string matches — the emitter's formatting is free to change as
// long as the generated C# keeps the VWT-opaque contract the runtime consumes.
public class AppleTypesCsEmitterTests
{
    private static TypeEntry MakeEntry(
        string swiftIdentity,
        string ns,
        string[] declPath,
        string library,
        string symbol,
        Availability? availability = null,
        string storage = "vwt_opaque",
        bool whitelisted = false,
        bool frozen = false,
        int? size = null,
        int? alignment = null)
    {
        return new TypeEntry
        {
            SwiftIdentity = swiftIdentity,
            Kind = "struct",
            Frozen = frozen,
            Size = size,
            Alignment = alignment,
            StorageStrategy = storage,
            SequentialLayoutWhitelisted = whitelisted,
            ManagedProjection = new ManagedRef
            {
                Namespace = ns,
                DeclarationPath = new List<string>(declPath),
            },
            AbiCarrier = new ManagedRef
            {
                Namespace = ns,
                DeclarationPath = new List<string>(declPath),
            },
            MetadataAccessor = new MetadataAccessor
            {
                Symbol = symbol,
                Library = library,
                Availability = availability ?? new Availability(),
            },
        };
    }

    private static Manifest MakeManifest(string moduleName, params TypeEntry[] entries)
    {
        var manifest = new Manifest
        {
            SdkTrain = new SdkTrain { Major = 18 },
        };
        manifest.Modules[moduleName] = new Module
        {
            Types = new List<TypeEntry>(entries),
        };
        return manifest;
    }

    private static (string generatedDir, string outputPath, string contents) Emit(
        TypeEntry entry, string moduleName, SequentialLayoutWhitelist? whitelist = null)
    {
        var manifest = MakeManifest(moduleName, entry);
        var dir = Path.Combine(Path.GetTempPath(), "apple-cs-emitter-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var emitter = new AppleTypesCsEmitter(whitelist ?? SequentialLayoutWhitelist.Empty(), NullLogger.Instance);
        emitter.Emit(manifest, dir);
        var emitted = Assert.Single(emitter.EmittedFiles);
        return (dir, emitted, File.ReadAllText(emitted));
    }

    [Fact]
    public void VwtOpaque_EmitsSealedClass_ImplementingISwiftObjectAndISwiftStruct()
    {
        var entry = MakeEntry(
            "Foundation.Locale.Language",
            "Swift.Foundation",
            new[] { "Locale", "Language" },
            "Foundation",
            "$s10Foundation6LocaleV8LanguageVMa");
        var (_, _, contents) = Emit(entry, "Foundation");

        Assert.Contains("public sealed partial class Language : ISwiftObject, ISwiftStruct, IDisposable", contents);
        Assert.Contains("public readonly partial struct Locale", contents);
        Assert.Contains("namespace Swift.Foundation;", contents);
    }

    [Fact]
    public void VwtOpaque_EmitsMetadataAccessorPInvoke_WithCallConvSwift()
    {
        var entry = MakeEntry(
            "Foundation.Locale.Language",
            "Swift.Foundation",
            new[] { "Locale", "Language" },
            "Foundation",
            "$s10Foundation6LocaleV8LanguageVMa");
        var (_, _, contents) = Emit(entry, "Foundation");

        Assert.Contains("[UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]", contents);
        Assert.Contains(
            "[DllImport(\"/System/Library/Frameworks/Foundation.framework/Foundation\", EntryPoint = \"$s10Foundation6LocaleV8LanguageVMa\")]",
            contents);
        Assert.Contains("private static extern TypeMetadata PInvoke_GetMetadata();", contents);
    }

    [Fact]
    public void VwtOpaque_NewFromPayload_AllocatesHeapCopyAndUsesVwtInitializeWithCopy()
    {
        var entry = MakeEntry(
            "Foundation.Locale.Language",
            "Swift.Foundation",
            new[] { "Locale", "Language" },
            "Foundation",
            "$s10Foundation6LocaleV8LanguageVMa");
        var (_, _, contents) = Emit(entry, "Foundation");

        Assert.Contains("NativeMemory.Alloc((nuint)size)", contents);
        Assert.Contains("metadata.ValueWitnessTable->InitializeWithCopy(heapCopy", contents);
    }

    [Fact]
    public void Availability_NullPlatformBecomesUnsupportedOSPlatform()
    {
        var entry = MakeEntry(
            "ManagedSettings.Application",
            "Swift.ManagedSettings",
            new[] { "Application" },
            "ManagedSettings",
            "$s15ManagedSettings11ApplicationVMa",
            availability: new Availability { Ios = "15.0", Maccatalyst = null, Tvos = null, Macos = null });
        var (_, _, contents) = Emit(entry, "ManagedSettings");

        Assert.Contains("[SupportedOSPlatform(\"ios15.0\")]", contents);
        Assert.Contains("[UnsupportedOSPlatform(\"maccatalyst\")]", contents);
        Assert.Contains("[UnsupportedOSPlatform(\"tvos\")]", contents);
        Assert.Contains("[UnsupportedOSPlatform(\"macos\")]", contents);
    }

    [Fact]
    public void Availability_BareMajorIsPaddedToMajorMinor()
    {
        // CA1418 rejects "ios16" — the emitter must pad a dotless version to "ios16.0".
        var entry = MakeEntry(
            "Foundation.Locale.Language",
            "Swift.Foundation",
            new[] { "Locale", "Language" },
            "Foundation",
            "$s10Foundation6LocaleV8LanguageVMa",
            availability: new Availability { Ios = "16", Macos = "13" });
        var (_, _, contents) = Emit(entry, "Foundation");

        Assert.Contains("[SupportedOSPlatform(\"ios16.0\")]", contents);
        Assert.Contains("[SupportedOSPlatform(\"macos13.0\")]", contents);
        Assert.DoesNotContain("ios16\"", contents);
        Assert.DoesNotContain("macos13\"", contents);
    }

    [Fact]
    public void LibraryResolution_MapsSwiftToLibswiftCore()
    {
        var entry = MakeEntry(
            "Swift.SomeType",
            "Swift",
            new[] { "SomeType" },
            "Swift",
            "$sSome");
        var (_, _, contents) = Emit(entry, "Swift");

        Assert.Contains("[DllImport(\"/usr/lib/swift/libswiftCore.dylib\"", contents);
    }

    [Fact]
    public void Idempotent_SameInputProducesByteIdenticalOutput()
    {
        var entry = MakeEntry(
            "Foundation.Locale.Language",
            "Swift.Foundation",
            new[] { "Locale", "Language" },
            "Foundation",
            "$s10Foundation6LocaleV8LanguageVMa",
            availability: new Availability { Ios = "16", Macos = "13" });

        var (dir1, _, contents1) = Emit(entry, "Foundation");
        var (dir2, _, contents2) = Emit(entry, "Foundation");
        Directory.Delete(dir1, recursive: true);
        Directory.Delete(dir2, recursive: true);

        Assert.Equal(contents1, contents2);
    }

    [Fact]
    public void Whitelist_OptInWithoutExternalWhitelistEntry_IsRefused()
    {
        // Manifest says "sequential_layout_whitelisted": true but the external whitelist
        // file does not list the identity — emission must refuse rather than silently
        // falling back to VWT-opaque (that would hide a misconfiguration).
        var entry = MakeEntry(
            "Foundation.SomeStruct",
            "Swift.Foundation",
            new[] { "SomeStruct" },
            "Foundation",
            "$s10Foundation10SomeStructVMa",
            storage: "sequential",
            whitelisted: true,
            frozen: true,
            size: 8,
            alignment: 8);
        var manifest = MakeManifest("Foundation", entry);

        var dir = Path.Combine(Path.GetTempPath(), "apple-cs-refuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var emitter = new AppleTypesCsEmitter(SequentialLayoutWhitelist.Empty(), NullLogger.Instance);
        emitter.Emit(manifest, dir);
        Directory.Delete(dir, recursive: true);

        Assert.Empty(emitter.EmittedFiles);
        var skipped = Assert.Single(emitter.SkippedEntries);
        Assert.Equal("Foundation.SomeStruct", skipped.SwiftIdentity);
        Assert.Contains("not in sequential-layout-whitelist.json", skipped.Reason);
    }

    [Fact]
    public void Whitelist_OptInWithNullSize_IsRefused()
    {
        // The sequential path REQUIRES size + alignment to have been validated against
        // the live SDK. The baseline manifest has size=null everywhere until Session 6
        // fills it in, so whitelist opt-in must refuse until that happens.
        var entry = MakeEntry(
            "Foundation.SomeStruct",
            "Swift.Foundation",
            new[] { "SomeStruct" },
            "Foundation",
            "$s10Foundation10SomeStructVMa",
            storage: "sequential",
            whitelisted: true,
            frozen: true,
            size: null,
            alignment: null);
        var manifest = MakeManifest("Foundation", entry);

        var whitelist = new SequentialLayoutWhitelist
        {
            ApprovedIdentities = new List<string> { "Foundation.SomeStruct" },
        };

        var dir = Path.Combine(Path.GetTempPath(), "apple-cs-refuse-size-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var emitter = new AppleTypesCsEmitter(whitelist, NullLogger.Instance);
        emitter.Emit(manifest, dir);
        Directory.Delete(dir, recursive: true);

        Assert.Empty(emitter.EmittedFiles);
        var skipped = Assert.Single(emitter.SkippedEntries);
        Assert.Contains("size/alignment are null", skipped.Reason);
    }

    [Fact]
    public void Whitelist_OptInNonFrozen_IsRefused()
    {
        var entry = MakeEntry(
            "Foundation.SomeStruct",
            "Swift.Foundation",
            new[] { "SomeStruct" },
            "Foundation",
            "$s10Foundation10SomeStructVMa",
            storage: "sequential",
            whitelisted: true,
            frozen: false,
            size: 8,
            alignment: 8);
        var manifest = MakeManifest("Foundation", entry);

        var whitelist = new SequentialLayoutWhitelist
        {
            ApprovedIdentities = new List<string> { "Foundation.SomeStruct" },
        };

        var dir = Path.Combine(Path.GetTempPath(), "apple-cs-refuse-frozen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var emitter = new AppleTypesCsEmitter(whitelist, NullLogger.Instance);
        emitter.Emit(manifest, dir);
        Directory.Delete(dir, recursive: true);

        Assert.Empty(emitter.EmittedFiles);
        var skipped = Assert.Single(emitter.SkippedEntries);
        Assert.Contains("frozen=false", skipped.Reason);
    }

    [Fact]
    public void Sequential_WithFullValidation_EmitsStructLayoutAttribute()
    {
        var entry = MakeEntry(
            "Foundation.SomeStruct",
            "Swift.Foundation",
            new[] { "SomeStruct" },
            "Foundation",
            "$s10Foundation10SomeStructVMa",
            storage: "sequential",
            whitelisted: true,
            frozen: true,
            size: 16,
            alignment: 8);
        var whitelist = new SequentialLayoutWhitelist
        {
            ApprovedIdentities = new List<string> { "Foundation.SomeStruct" },
        };
        var (_, _, contents) = Emit(entry, "Foundation", whitelist);

        Assert.Contains("[StructLayout(LayoutKind.Sequential, Size = 16, Pack = 8)]", contents);
        Assert.Contains("public partial struct SomeStruct", contents);
        Assert.DoesNotContain("ISwiftObject", contents);
    }
}
