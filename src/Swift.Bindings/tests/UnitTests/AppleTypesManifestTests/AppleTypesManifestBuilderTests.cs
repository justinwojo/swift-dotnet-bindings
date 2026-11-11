// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using BindingsGeneration.AppleTypesManifest;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

public class AppleTypesManifestBuilderTests
{
    // Minimal ABI JSON factory. Only the fields the builder reads are populated; the rest
    // use default empty sentinels so deserialization succeeds. Intentionally hand-built
    // rather than loaded from a fixture file to keep test intent localized.
    // Each dump records the digester's `-target`, which names the one manifest column it fills.
    private const string IosTarget = "arm64-apple-ios26.0-simulator";
    private const string CatalystTarget = "arm64-apple-ios26.0-macabi";
    private const string TvosTarget = "arm64-apple-tvos26.0-simulator";
    private const string MacosTarget = "arm64-apple-macos26.0";

    private static string MakeAbi(string moduleName, string typesJsonArray, string? target = IosTarget)
    {
        var toolArguments = target is null
            ? ""
            : $", \"tool_arguments\": [\"-dump-sdk\", \"-module\", \"{moduleName}\", \"-target\", \"{target}\", \"-sdk\", \"/sdk\"]";
        return $$"""
        {
          "ABIRoot": {
            "kind": "Root",
            "name": "{{moduleName}}",
            "printedName": "{{moduleName}}",
            "children": {{typesJsonArray}},
            "json_format_version": 8{{toolArguments}}
          }
        }
        """;
    }

    // The ABI dump's marker conformances, as a `conformances` array: since Swift 6 the digester
    // lists `Copyable` explicitly on every copyable nominal, so its ABSENCE alongside `Escapable`
    // is how a `~Copyable` type is spelled.
    private static string Conformances(params string[] protocolNames) =>
        "[" + string.Join(",", protocolNames.Select(p =>
            $$"""{"kind":"Conformance","name":"{{p}}","printedName":"{{p}}","usr":"s:s{{p}}P","mangledName":"$ss{{p}}P"}""")) + "]";

    private static string TypeDecl(string declKind, string name, string mangled, string? introIos = null, string? introMacosx = null, string? introTvos = null, string? introMaccat = null, string[]? attrs = null, string? childrenJson = null, string? conformancesJson = null, string? introAnyAppleOS = null)
    {
        string attrsJson = attrs is null ? "[]" : "[" + string.Join(",", attrs.Select(a => $"\"{a}\"")) + "]";
        string intros = string.Join(",", new[]
        {
            introIos   is null ? null : $"\"intro_iOS\": \"{introIos}\"",
            introMacosx is null ? null : $"\"intro_Macosx\": \"{introMacosx}\"",
            introTvos  is null ? null : $"\"intro_tvOS\": \"{introTvos}\"",
            introMaccat is null ? null : $"\"intro_macCatalyst\": \"{introMaccat}\"",
            introAnyAppleOS is null ? null : $"\"intro_anyAppleOS\": \"{introAnyAppleOS}\"",
        }.Where(s => s is not null));
        if (!string.IsNullOrEmpty(intros))
            intros = "," + intros;
        return $$"""
        {
          "kind": "TypeDecl",
          "declKind": "{{declKind}}",
          "name": "{{name}}",
          "mangledName": "{{mangled}}",
          "printedName": "{{name}}",
          "moduleName": "",
          "declAttributes": {{attrsJson}},
          "static": null,
          "isInternal": null,
          "genericSig": null,
          "sugared_genericSig": null,
          "throwing": null,
          "accessorKind": null,
          "enumRawTypeName": null,
          "paramValueOwnership": null,
          "hasDefaultArg": null,
          "children": {{childrenJson ?? "[]"}},
          "conformances": {{conformancesJson ?? "[]"}},
          "accessors": []
          {{intros}}
        }
        """;
    }

    private static AppleTypesManifestBuilder NewBuilder(params string[] includes)
    {
        return new AppleTypesManifestBuilder(new IncludeFilter(includes), NullLogger.Instance);
    }

    private static void IngestString(AppleTypesManifestBuilder builder, string abiJson)
    {
        var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".abi.json");
        try
        {
            File.WriteAllText(tmp, abiJson);
            builder.IngestAbiJson(tmp);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void Emits_matched_type_with_canonical_metadata_accessor()
    {
        var abi = MakeAbi("ManagedSettings", "[" +
            TypeDecl("Struct", "Application", "$s15ManagedSettings11ApplicationV", introIos: "15.0") +
            "]");
        var builder = NewBuilder("ManagedSettings.Application");
        IngestString(builder, abi);
        var manifest = builder.Build(new ManifestOptions { SdkTrainMajor = 26 });

        var entry = Assert.Single(manifest.Modules["ManagedSettings"].Types);
        Assert.Equal("ManagedSettings.Application", entry.SwiftIdentity);
        Assert.Equal("Swift.ManagedSettings", entry.ManagedProjection.Namespace);
        Assert.Equal(new[] { "Application" }, entry.ManagedProjection.DeclarationPath);
        Assert.Equal("$s15ManagedSettings11ApplicationVMa", entry.MetadataAccessor?.Symbol);
        Assert.Equal("ManagedSettings", entry.MetadataAccessor?.Library);
        Assert.Equal("15.0", entry.MetadataAccessor?.Availability.Ios);
        Assert.True(entry.MetadataAccessor?.WeakLink);
        Assert.Equal("vwt_opaque", entry.StorageStrategy);
        Assert.False(entry.SequentialLayoutWhitelisted);
        Assert.Equal("generated", entry.Status);
    }

    [Fact]
    public void Include_listed_noncopyable_type_fails_the_manifest_build()
    {
        // Every manifest entry is `vwt_opaque`, so the C# carrier emitted from it copies through
        // the value witness. For a `~Copyable` type that witness slot is
        // `__swift_cannot_copy_noncopyable_type` — an unconditional runtime trap, not a compile
        // error, so nothing downstream would catch it. The include list is hand-authored, so the
        // build fails rather than shipping a carrier that traps on first use.
        var abi = MakeAbi("MoveOnlyKit", "[" +
            TypeDecl("Struct", "Token", "$s11MoveOnlyKit5TokenV", introIos: "18.0",
                conformancesJson: Conformances("Escapable", "Sendable")) +
            "]");
        var builder = NewBuilder("MoveOnlyKit.Token");

        var ex = Assert.Throws<InvalidOperationException>(() => IngestString(builder, abi));
        Assert.Contains("MoveOnlyKit.Token", ex.Message);
        Assert.Contains("~Copyable", ex.Message);
    }

    [Fact]
    public void Copyable_type_declaring_escapable_still_builds()
    {
        // The negative control for the check above: an ordinary Swift 6 struct lists BOTH
        // `Escapable` and `Copyable`, so keying the refusal on `Escapable` alone would refuse
        // essentially the whole SDK.
        var abi = MakeAbi("MoveOnlyKit", "[" +
            TypeDecl("Struct", "Ticket", "$s11MoveOnlyKit6TicketV", introIos: "18.0",
                conformancesJson: Conformances("Escapable", "Copyable", "Sendable")) +
            "]");
        var builder = NewBuilder("MoveOnlyKit.Ticket");
        IngestString(builder, abi);
        var manifest = builder.Build(new ManifestOptions { SdkTrainMajor = 26 });

        var entry = Assert.Single(manifest.Modules["MoveOnlyKit"].Types);
        Assert.Equal("MoveOnlyKit.Ticket", entry.SwiftIdentity);
    }

    [Fact]
    public void Filters_out_non_whitelisted_types()
    {
        var abi = MakeAbi("Foundation", "[" +
            TypeDecl("Struct", "Date", "$s10Foundation4DateV", introIos: "8.0") + "," +
            TypeDecl("Struct", "AnyError", "$s10Foundation8AnyErrorV", introIos: "15.0") +
            "]");
        var builder = NewBuilder(); // no includes -> everything is filtered out
        IngestString(builder, abi);
        var manifest = builder.Build(new ManifestOptions { SdkTrainMajor = 26 });
        Assert.Empty(manifest.Modules);
    }

    [Fact]
    public void Nested_type_inherits_parent_availability_via_max_merge()
    {
        var child = TypeDecl("Struct", "ECDSASignature", "$s9CryptoKit4P256O7SigningO14ECDSASignatureV");
        var signing = TypeDecl("Enum", "Signing", "$s9CryptoKit4P256O7SigningO", childrenJson: "[" + child + "]");
        var p256 = TypeDecl("Enum", "P256", "$s9CryptoKit4P256O", introIos: "13.0", introMacosx: "10.15", introTvos: "13.0", childrenJson: "[" + signing + "]");

        var builder = NewBuilder("CryptoKit.P256.Signing.ECDSASignature");
        foreach (var target in new[] { IosTarget, TvosTarget, MacosTarget })
            IngestString(builder, MakeAbi("CryptoKit", "[" + p256 + "]", target));
        var manifest = builder.Build(new ManifestOptions { SdkTrainMajor = 26 });

        var entry = Assert.Single(manifest.Modules["CryptoKit"].Types);
        Assert.Equal("CryptoKit.P256.Signing.ECDSASignature", entry.SwiftIdentity);
        Assert.Equal(new[] { "P256", "Signing", "ECDSASignature" }, entry.ManagedProjection.DeclarationPath);
        // iOS/macOS/tvOS inherited from P256's intro_* floor even though nested nodes are bare.
        Assert.Equal("13.0", entry.MetadataAccessor?.Availability.Ios);
        Assert.Equal("10.15", entry.MetadataAccessor?.Availability.Macos);
        Assert.Equal("13.0", entry.MetadataAccessor?.Availability.Tvos);
    }

    [Fact]
    public void Cross_input_merge_unions_availability_per_platform()
    {
        // Two ABI JSONs each carrying one-platform intro_* coverage for the same type.
        var iosAbi = MakeAbi("ManagedSettings", "[" +
            TypeDecl("Struct", "Application", "$s15ManagedSettings11ApplicationV", introIos: "15.0") +
            "]");
        var macAbi = MakeAbi("ManagedSettings", "[" +
            TypeDecl("Struct", "Application", "$s15ManagedSettings11ApplicationV", introMacosx: "14.0") +
            "]", MacosTarget);

        var builder = NewBuilder("ManagedSettings.Application");
        IngestString(builder, iosAbi);
        IngestString(builder, macAbi);

        var manifest = builder.Build(new ManifestOptions { SdkTrainMajor = 26 });
        var entry = Assert.Single(manifest.Modules["ManagedSettings"].Types);
        Assert.Equal("15.0", entry.MetadataAccessor?.Availability.Ios);
        Assert.Equal("14.0", entry.MetadataAccessor?.Availability.Macos);
        Assert.Null(entry.MetadataAccessor?.Availability.Tvos);
    }

    private static Availability BuildAcrossDumps(string include, Func<string, string?> typesForTarget)
    {
        var builder = NewBuilder(include);
        var module = include.Substring(0, include.IndexOf('.'));
        foreach (var target in new[] { IosTarget, CatalystTarget, TvosTarget, MacosTarget })
        {
            var types = typesForTarget(target);
            if (types is not null)
                IngestString(builder, MakeAbi(module, "[" + types + "]", target));
        }
        var entry = Assert.Single(builder.Build(new ManifestOptions { SdkTrainMajor = 27 }).Modules[module].Types);
        Assert.NotNull(entry.MetadataAccessor);
        return entry.MetadataAccessor!.Availability;
    }

    [Fact]
    public void AnyAppleOS_floors_each_platform_whose_dump_contains_the_type()
    {
        // Swift 6.4 SDK shape: `@available(anyAppleOS 27.0, *) @available(tvOS, unavailable)`.
        // Every dump prints only `intro_anyAppleOS`; the tvOS dump leaves the type out. Each
        // platform that has it must floor at 27.0 and weak-link the accessor, or the
        // supplement strong-links a symbol that is missing on the 26.x OSes it supports.
        var availability = BuildAcrossDumps("StoreKit.RedeemOption", target => target == TvosTarget
            ? null
            : TypeDecl("Struct", "RedeemOption", "$s8StoreKit12RedeemOptionV", introAnyAppleOS: "27.0"));

        Assert.Equal("27.0", availability.Ios);
        Assert.Equal("27.0", availability.Maccatalyst);
        Assert.Equal("27.0", availability.Macos);
        Assert.Null(availability.Tvos);
    }

    [Fact]
    public void Named_platform_replaces_anyAppleOS_and_Catalyst_follows_named_iOS()
    {
        // `@available(iOS 18.0, *) @available(anyAppleOS 27.0, *)`: the iOS record replaces
        // the umbrella on iOS even though it is earlier, Mac Catalyst follows the iOS record,
        // and tvOS and macOS take the umbrella.
        var availability = BuildAcrossDumps("StoreKit.Offer", _ =>
            TypeDecl("Struct", "Offer", "$s8StoreKit5OfferV", introIos: "18.0", introAnyAppleOS: "27.0"));

        Assert.Equal("18.0", availability.Ios);
        Assert.Equal("18.0", availability.Maccatalyst);
        Assert.Equal("27.0", availability.Tvos);
        Assert.Equal("27.0", availability.Macos);
    }

    [Theory]
    // An outer umbrella floors a nested type whose own iOS record is earlier.
    [InlineData(null, "27.0", "18.0", null, "27.0", "27.0")]
    // An inner umbrella floors a nested type above its parent's named records.
    [InlineData("15.0", null, null, "27.2", "27.2", "27.2")]
    public void Outer_floor_and_inner_anyAppleOS_both_constrain_a_nested_type(
        string? outerIos, string? outerAnyAppleOS, string? innerIos, string? innerAnyAppleOS,
        string expectedIos, string expectedTvos)
    {
        var inner = TypeDecl("Enum", "TokenType", "$s8StoreKit4LinkV9TokenTypeO",
            introIos: innerIos, introAnyAppleOS: innerAnyAppleOS);
        var outer = TypeDecl("Struct", "Link", "$s8StoreKit4LinkV",
            introIos: outerIos, introTvos: outerIos, introAnyAppleOS: outerAnyAppleOS,
            childrenJson: "[" + inner + "]");

        var availability = BuildAcrossDumps("StoreKit.Link.TokenType", _ => outer);

        Assert.Equal(expectedIos, availability.Ios);
        Assert.Equal(expectedIos, availability.Maccatalyst);
        Assert.Equal(expectedTvos, availability.Tvos);
    }

    [Fact]
    public void A_dump_fills_only_the_column_of_its_own_platform()
    {
        // Each dump prints every platform's intro_* keys. A type the tvOS dump leaves out is
        // unavailable there, even though the iOS dump shows an intro_tvOS for it.
        var availability = BuildAcrossDumps("Kit.Widget", target => target == TvosTarget
            ? null
            : TypeDecl("Struct", "Widget", "$s3Kit6WidgetV", introIos: "16.0", introTvos: "16.0", introMacosx: "13.0"));

        Assert.Equal("16.0", availability.Ios);
        Assert.Equal("16.0", availability.Maccatalyst);
        Assert.Equal("13.0", availability.Macos);
        Assert.Null(availability.Tvos);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Type_left_out_of_the_Catalyst_dump_is_unsupported_on_Catalyst(bool nested)
    {
        // `@available(iOS 15.0, *) @available(macCatalyst, unavailable)`: the digester prints
        // no Mac Catalyst record, and its macabi dump leaves the type out, at top level or
        // under a parent that stays in the dump. The Catalyst column must stay null, not
        // follow the iOS record, so the emitter marks the type [UnsupportedOSPlatform].
        var leaf = TypeDecl("Struct", "Shield", nested ? "$s3Kit5OuterO6ShieldV" : "$s3Kit6ShieldV", introIos: "15.0");
        string Dump(bool withLeaf) => nested
            ? TypeDecl("Enum", "Outer", "$s3Kit5OuterO", childrenJson: withLeaf ? "[" + leaf + "]" : "[]")
            : withLeaf ? leaf : TypeDecl("Struct", "Other", "$s3Kit5OtherV");

        var availability = BuildAcrossDumps(nested ? "Kit.Outer.Shield" : "Kit.Shield",
            target => target == CatalystTarget ? Dump(withLeaf: false) : Dump(withLeaf: true));

        Assert.Equal("15.0", availability.Ios);
        Assert.Null(availability.Maccatalyst);
    }

    [Theory]
    [InlineData(null, "records no -target")]
    [InlineData("arm64-apple-watchos11.0-simulator", "columns only for")]
    [InlineData("arm64-unknown-linux-gnu", "names no Apple OS")]
    public void Dump_whose_platform_has_no_manifest_column_is_rejected(string? target, string message)
    {
        var builder = NewBuilder("Kit.Widget");
        var abi = MakeAbi("Kit", "[" + TypeDecl("Struct", "Widget", "$s3Kit6WidgetV", introIos: "16.0") + "]", target);

        var ex = Assert.Throws<InvalidDataException>(() => IngestString(builder, abi));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Digester_NO_MODULE_placeholder_is_rejected()
    {
        // The digester exits 0 with this root when the module fails to load for the target.
        var builder = NewBuilder("StoreKit.Product");
        var abi = MakeAbi("NO_MODULE", "[]", CatalystTarget);

        var ex = Assert.Throws<InvalidDataException>(() => IngestString(builder, abi));
        Assert.Contains("NO_MODULE", ex.Message);
    }

    [Theory]
    [InlineData("arm64-apple-ios27.0-simulator", AbiApplePlatform.iOS)]
    [InlineData("arm64-apple-ios27.0", AbiApplePlatform.iOS)]
    [InlineData("arm64-apple-ios27.0-macabi", AbiApplePlatform.macCatalyst)]
    [InlineData("x86_64-apple-macosx10.15", AbiApplePlatform.macOS)]
    [InlineData("arm64-apple-macos27.0", AbiApplePlatform.macOS)]
    [InlineData("arm64-apple-tvos27.0-simulator", AbiApplePlatform.tvOS)]
    [InlineData("arm64_32-apple-watchos11.0", AbiApplePlatform.watchOS)]
    [InlineData("arm64-apple-xros2.0-simulator", AbiApplePlatform.visionOS)]
    [InlineData("arm64-unknown-linux-gnu", null)]
    [InlineData("", null)]
    public void Target_triple_names_the_dump_platform(string triple, AbiApplePlatform? expected)
    {
        Assert.Equal(expected, AbiIntroducedAvailability.FromTargetTriple(triple));
    }

    [Fact]
    public void Frozen_attribute_is_lifted_into_manifest_entry()
    {
        var abi = MakeAbi("Foundation", "[" +
            TypeDecl("Struct", "Locale", "$s10Foundation6LocaleV", attrs: new[] { "Frozen", "Available" }) +
            "]");
        var builder = NewBuilder("Foundation.Locale");
        IngestString(builder, abi);
        var manifest = builder.Build(new ManifestOptions { SdkTrainMajor = 26 });
        Assert.True(manifest.Modules["Foundation"].Types[0].Frozen);
    }

    [Fact]
    public void Command_runs_without_platform_or_platform_version_inputs()
    {
        // Guards against regressing the CLI dispatch ordering: the manifest fast-path runs
        // BEFORE --platform/--platform-version validation in BindingsGeneratorCommand.Execute.
        // If --emit-apple-types-manifest ever slipped back behind platform validation, a
        // caller passing only manifest flags would fail with an unrelated platform error.
        // This test proves the Run entry point doesn't require any binding-generator platform
        // state to produce a valid manifest.
        var abi = MakeAbi("Foundation", "[" +
            TypeDecl("Struct", "Locale", "$s10Foundation6LocaleV", introIos: "10.0") +
            "]");
        var tmpAbi = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".abi.json");
        var tmpInclude = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".include.json");
        var tmpOut = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".manifest.json");
        try
        {
            File.WriteAllText(tmpAbi, abi);
            File.WriteAllText(tmpInclude, "{\"types\": [\"Foundation.Locale\"]}");
            var exit = AppleTypesManifestCommand.Run(
                abiJsonPaths: new[] { tmpAbi },
                includeTypesPath: tmpInclude,
                outputPath: tmpOut,
                sdkTrainMajor: 18,
                sdkTrainLabel: null,
                platforms: null,
                generatedBy: "unit-test",
                allowPartial: false,
                logger: NullLogger.Instance);
            Assert.Equal(0, exit);
            Assert.True(File.Exists(tmpOut));
            var parsed = JObject.Parse(File.ReadAllText(tmpOut));
            Assert.Equal("Foundation.Locale", (string?)parsed["modules"]!["Foundation"]!["types"]![0]!["swift_identity"]);
        }
        finally
        {
            foreach (var p in new[] { tmpAbi, tmpInclude, tmpOut })
                if (File.Exists(p)) File.Delete(p);
        }
    }

    [Fact]
    public void Command_rejects_missing_include_filter()
    {
        // Positive-list is REQUIRED: the supplement cannot shadow Runtime-owned canonicals.
        // If include-types is omitted, Run must fail fast rather than emitting an unfiltered
        // (or empty) manifest.
        var abi = MakeAbi("Foundation", "[]");
        var tmpAbi = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".abi.json");
        var tmpOut = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".manifest.json");
        try
        {
            File.WriteAllText(tmpAbi, abi);
            var exit = AppleTypesManifestCommand.Run(
                abiJsonPaths: new[] { tmpAbi },
                includeTypesPath: null,
                outputPath: tmpOut,
                sdkTrainMajor: 18,
                sdkTrainLabel: null,
                platforms: null,
                generatedBy: null,
                allowPartial: false,
                logger: NullLogger.Instance);
            Assert.NotEqual(0, exit);
            Assert.False(File.Exists(tmpOut));
        }
        finally
        {
            if (File.Exists(tmpAbi)) File.Delete(tmpAbi);
            if (File.Exists(tmpOut)) File.Delete(tmpOut);
        }
    }

    [Fact]
    public void Serialized_output_matches_expected_top_level_shape()
    {
        var abi = MakeAbi("Foundation", "[" +
            TypeDecl("Struct", "Locale", "$s10Foundation6LocaleV",
                childrenJson: "[" + TypeDecl("Struct", "Language", "$s10Foundation6LocaleV8LanguageV", introIos: "16.0") + "]") +
            "]");
        var builder = NewBuilder("Foundation.Locale.Language");
        IngestString(builder, abi);
        var manifest = builder.Build(new ManifestOptions
        {
            SdkTrainMajor = 26,
            SdkTrainLabel = "Xcode 16",
            Platforms = new Availability { Ios = "18.0", Macos = "15.0" },
            GeneratedBy = "unit-test",
        });

        var json = AppleTypesManifestSerializer.Serialize(manifest);
        var parsed = JObject.Parse(json);
        Assert.Equal(1, (int)parsed["manifest_version"]!);
        Assert.Equal(26, (int)parsed["sdk_train"]!["major"]!);
        Assert.Equal("18.0", (string)parsed["sdk_train"]!["platforms"]!["ios"]!);
        var entry = parsed["modules"]!["Foundation"]!["types"]![0]!;
        Assert.Equal("Foundation.Locale.Language", (string)entry["swift_identity"]!);
        Assert.Equal("Swift.Foundation", (string)entry["managed_projection"]!["namespace"]!);
        Assert.Equal("$s10Foundation6LocaleV8LanguageVMa", (string)entry["metadata_accessor"]!["symbol"]!);
        Assert.Equal("generated", (string)entry["status"]!);
        Assert.Equal("vwt_opaque", (string)entry["storage_strategy"]!);
        // Json.NET auto-discovery of `IsEmpty` should stay suppressed.
        Assert.Null(entry["metadata_accessor"]!["availability"]!["IsEmpty"]);
    }
}
