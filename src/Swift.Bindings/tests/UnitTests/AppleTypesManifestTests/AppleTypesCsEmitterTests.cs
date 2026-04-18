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
        int? alignment = null,
        SequentialLayoutEvidence? evidence = null)
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
            SequentialLayoutEvidence = evidence,
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

    private static SequentialLayoutEvidence FullEvidence()
    {
        return new SequentialLayoutEvidence
        {
            StoredFieldsKnown = true,
            CopyDestroyHandling = "trivial",
            RoundtripValidated = true,
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

    private const string RegistrationFileName = "_AppleSupplementRegistration.cs";

    // Filters out the bare-name DllImport registration side-car so test assertions
    // target the type-emission file. See `AppleTypesCsEmitter.EmitFrameworkResolverRegistration`.
    private static string EntryFile(AppleTypesCsEmitter emitter)
        => Assert.Single(
            emitter.EmittedFiles,
            f => !f.EndsWith(RegistrationFileName, StringComparison.Ordinal));

    private static (string generatedDir, string outputPath, string contents) Emit(
        TypeEntry entry, string moduleName, SequentialLayoutWhitelist? whitelist = null)
    {
        var manifest = MakeManifest(moduleName, entry);
        var dir = Path.Combine(Path.GetTempPath(), "apple-cs-emitter-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var emitter = new AppleTypesCsEmitter(whitelist ?? SequentialLayoutWhitelist.Empty(), NullLogger.Instance);
        emitter.Emit(manifest, dir);
        var emitted = EntryFile(emitter);
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
        // Bare library name (no `.framework/` substring) so the macios linker doesn't
        // force-add `-framework Foundation`. SwiftFrameworkResolver maps it to a system
        // framework path at runtime via per-assembly DllImportResolver.
        Assert.Contains(
            "[DllImport(\"Foundation\", EntryPoint = \"$s10Foundation6LocaleV8LanguageVMa\")]",
            contents);
        Assert.DoesNotContain("/System/Library/Frameworks/Foundation.framework/Foundation", contents);
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
        // file does not list the identity — the gate refuses the sequential path and
        // falls back to VWT-opaque emission so the type is still shippable, while
        // recording the refusal so the CLI can fail the build.
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
            alignment: 8,
            evidence: FullEvidence());
        var manifest = MakeManifest("Foundation", entry);

        var dir = Path.Combine(Path.GetTempPath(), "apple-cs-refuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var emitter = new AppleTypesCsEmitter(SequentialLayoutWhitelist.Empty(), NullLogger.Instance);
        emitter.Emit(manifest, dir);
        var emittedPath = EntryFile(emitter);
        var contents = File.ReadAllText(emittedPath);
        Directory.Delete(dir, recursive: true);

        Assert.Empty(emitter.SkippedEntries);
        var refused = Assert.Single(emitter.RefusedWhitelistEntries);
        Assert.Equal("Foundation.SomeStruct", refused.SwiftIdentity);
        Assert.Contains("sequential-layout-whitelist.json", refused.Reason);
        // VWT-opaque fallback must be the emitted shape.
        Assert.Contains("ISwiftObject", contents);
        Assert.DoesNotContain("[StructLayout(LayoutKind.Sequential", contents);
    }

    [Fact]
    public void Whitelist_OptInWithNullSize_IsRefused()
    {
        // The sequential path REQUIRES size + alignment to have been validated against
        // the live SDK. A whitelist opt-in with size=null must refuse and fall back.
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
            alignment: null,
            evidence: FullEvidence());
        var manifest = MakeManifest("Foundation", entry);

        var whitelist = new SequentialLayoutWhitelist
        {
            ApprovedIdentities = new List<string> { "Foundation.SomeStruct" },
        };

        var dir = Path.Combine(Path.GetTempPath(), "apple-cs-refuse-size-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var emitter = new AppleTypesCsEmitter(whitelist, NullLogger.Instance);
        emitter.Emit(manifest, dir);
        var emittedPath = EntryFile(emitter);
        var contents = File.ReadAllText(emittedPath);
        Directory.Delete(dir, recursive: true);

        Assert.Empty(emitter.SkippedEntries);
        var refused = Assert.Single(emitter.RefusedWhitelistEntries);
        Assert.Contains("size/alignment are null", refused.Reason);
        Assert.Contains("ISwiftObject", contents);
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
            alignment: 8,
            evidence: FullEvidence());
        var manifest = MakeManifest("Foundation", entry);

        var whitelist = new SequentialLayoutWhitelist
        {
            ApprovedIdentities = new List<string> { "Foundation.SomeStruct" },
        };

        var dir = Path.Combine(Path.GetTempPath(), "apple-cs-refuse-frozen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var emitter = new AppleTypesCsEmitter(whitelist, NullLogger.Instance);
        emitter.Emit(manifest, dir);
        var emittedPath = EntryFile(emitter);
        var contents = File.ReadAllText(emittedPath);
        Directory.Delete(dir, recursive: true);

        Assert.Empty(emitter.SkippedEntries);
        var refused = Assert.Single(emitter.RefusedWhitelistEntries);
        Assert.Contains("frozen=false", refused.Reason);
        Assert.Contains("ISwiftObject", contents);
    }

    [Fact]
    public void Whitelist_OptInMissingEvidence_IsRefused()
    {
        // All structural gates pass but sequential_layout_evidence is null — the whitelist
        // claim is unsupported and must refuse, falling back to VWT-opaque.
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
            alignment: 8,
            evidence: null);
        var manifest = MakeManifest("Foundation", entry);

        var whitelist = new SequentialLayoutWhitelist
        {
            ApprovedIdentities = new List<string> { "Foundation.SomeStruct" },
        };

        var dir = Path.Combine(Path.GetTempPath(), "apple-cs-refuse-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var emitter = new AppleTypesCsEmitter(whitelist, NullLogger.Instance);
        emitter.Emit(manifest, dir);
        _ = EntryFile(emitter);
        Directory.Delete(dir, recursive: true);

        var refused = Assert.Single(emitter.RefusedWhitelistEntries);
        Assert.Contains("sequential_layout_evidence is missing", refused.Reason);
    }

    [Fact]
    public void Whitelist_OptInEvidenceRoundtripUnvalidated_IsRefused()
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
            alignment: 8,
            evidence: new SequentialLayoutEvidence
            {
                StoredFieldsKnown = true,
                CopyDestroyHandling = "trivial",
                RoundtripValidated = false,
            });
        var manifest = MakeManifest("Foundation", entry);

        var whitelist = new SequentialLayoutWhitelist
        {
            ApprovedIdentities = new List<string> { "Foundation.SomeStruct" },
        };

        var dir = Path.Combine(Path.GetTempPath(), "apple-cs-refuse-rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var emitter = new AppleTypesCsEmitter(whitelist, NullLogger.Instance);
        emitter.Emit(manifest, dir);
        _ = EntryFile(emitter);
        Directory.Delete(dir, recursive: true);

        var refused = Assert.Single(emitter.RefusedWhitelistEntries);
        Assert.Contains("roundtrip_validated=false", refused.Reason);
    }

    [Fact]
    public void Whitelist_OptInEvidenceBadCopyDestroyHandling_IsRefused()
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
            alignment: 8,
            evidence: new SequentialLayoutEvidence
            {
                StoredFieldsKnown = true,
                CopyDestroyHandling = "bogus",
                RoundtripValidated = true,
            });
        var manifest = MakeManifest("Foundation", entry);

        var whitelist = new SequentialLayoutWhitelist
        {
            ApprovedIdentities = new List<string> { "Foundation.SomeStruct" },
        };

        var dir = Path.Combine(Path.GetTempPath(), "apple-cs-refuse-cdh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var emitter = new AppleTypesCsEmitter(whitelist, NullLogger.Instance);
        emitter.Emit(manifest, dir);
        _ = EntryFile(emitter);
        Directory.Delete(dir, recursive: true);

        var refused = Assert.Single(emitter.RefusedWhitelistEntries);
        Assert.Contains("copy_destroy_handling", refused.Reason);
    }

    [Fact]
    public void FrameworkResolver_RegistrationFile_IsEmittedAlongsideTypes()
    {
        // Bare-name DllImports ("CryptoKit", "ManagedSettings") only resolve at runtime if
        // SwiftFrameworkResolver is registered for the supplement assembly. The emitter
        // must ship a [ModuleInitializer] side-car that wires it up — otherwise every
        // supplement P/Invoke would DllNotFoundException because the macios linker no
        // longer force-links the framework at build time.
        var entry = MakeEntry(
            "Foundation.Locale.Language",
            "Swift.Foundation",
            new[] { "Locale", "Language" },
            "Foundation",
            "$s10Foundation6LocaleV8LanguageVMa");
        var manifest = MakeManifest("Foundation", entry);

        var dir = Path.Combine(Path.GetTempPath(), "apple-cs-register-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var emitter = new AppleTypesCsEmitter(SequentialLayoutWhitelist.Empty(), NullLogger.Instance);
        emitter.Emit(manifest, dir);

        var registrationPath = Assert.Single(
            emitter.EmittedFiles,
            f => f.EndsWith(RegistrationFileName, StringComparison.Ordinal));
        var registrationContents = File.ReadAllText(registrationPath);
        Directory.Delete(dir, recursive: true);

        Assert.Contains("[ModuleInitializer]", registrationContents);
        Assert.Contains("SwiftFrameworkResolver.RegisterForAssembly", registrationContents);
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
            alignment: 8,
            evidence: FullEvidence());
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
