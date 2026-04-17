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
    private static string MakeAbi(string moduleName, string typesJsonArray) =>
        $$"""
        {
          "ABIRoot": {
            "kind": "Root",
            "name": "{{moduleName}}",
            "printedName": "{{moduleName}}",
            "children": {{typesJsonArray}}
          }
        }
        """;

    private static string TypeDecl(string declKind, string name, string mangled, string? introIos = null, string? introMacosx = null, string? introTvos = null, string? introMaccat = null, string[]? attrs = null, string? childrenJson = null)
    {
        string attrsJson = attrs is null ? "[]" : "[" + string.Join(",", attrs.Select(a => $"\"{a}\"")) + "]";
        string intros = string.Join(",", new[]
        {
            introIos   is null ? null : $"\"intro_iOS\": \"{introIos}\"",
            introMacosx is null ? null : $"\"intro_Macosx\": \"{introMacosx}\"",
            introTvos  is null ? null : $"\"intro_tvOS\": \"{introTvos}\"",
            introMaccat is null ? null : $"\"intro_macCatalyst\": \"{introMaccat}\"",
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
          "conformances": [],
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
        var manifest = builder.Build(new ManifestOptions { SdkTrainMajor = 18 });

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
    public void Filters_out_non_whitelisted_types()
    {
        var abi = MakeAbi("Foundation", "[" +
            TypeDecl("Struct", "Date", "$s10Foundation4DateV", introIos: "8.0") + "," +
            TypeDecl("Struct", "AnyError", "$s10Foundation8AnyErrorV", introIos: "15.0") +
            "]");
        var builder = NewBuilder(); // no includes -> everything is filtered out
        IngestString(builder, abi);
        var manifest = builder.Build(new ManifestOptions { SdkTrainMajor = 18 });
        Assert.Empty(manifest.Modules);
    }

    [Fact]
    public void Nested_type_inherits_parent_availability_via_max_merge()
    {
        var child = TypeDecl("Struct", "ECDSASignature", "$s9CryptoKit4P256O7SigningO14ECDSASignatureV");
        var signing = TypeDecl("Enum", "Signing", "$s9CryptoKit4P256O7SigningO", childrenJson: "[" + child + "]");
        var p256 = TypeDecl("Enum", "P256", "$s9CryptoKit4P256O", introIos: "13.0", introMacosx: "10.15", introTvos: "13.0", childrenJson: "[" + signing + "]");
        var abi = MakeAbi("CryptoKit", "[" + p256 + "]");

        var builder = NewBuilder("CryptoKit.P256.Signing.ECDSASignature");
        IngestString(builder, abi);
        var manifest = builder.Build(new ManifestOptions { SdkTrainMajor = 18 });

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
            "]");

        var builder = NewBuilder("ManagedSettings.Application");
        IngestString(builder, iosAbi);
        IngestString(builder, macAbi);

        var manifest = builder.Build(new ManifestOptions { SdkTrainMajor = 18 });
        var entry = Assert.Single(manifest.Modules["ManagedSettings"].Types);
        Assert.Equal("15.0", entry.MetadataAccessor?.Availability.Ios);
        Assert.Equal("14.0", entry.MetadataAccessor?.Availability.Macos);
        Assert.Null(entry.MetadataAccessor?.Availability.Tvos);
    }

    [Fact]
    public void Frozen_attribute_is_lifted_into_manifest_entry()
    {
        var abi = MakeAbi("Foundation", "[" +
            TypeDecl("Struct", "Locale", "$s10Foundation6LocaleV", attrs: new[] { "Frozen", "Available" }) +
            "]");
        var builder = NewBuilder("Foundation.Locale");
        IngestString(builder, abi);
        var manifest = builder.Build(new ManifestOptions { SdkTrainMajor = 18 });
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
            SdkTrainMajor = 18,
            SdkTrainLabel = "Xcode 16",
            Platforms = new Availability { Ios = "18.0", Macos = "15.0" },
            GeneratedBy = "unit-test",
        });

        var json = AppleTypesManifestSerializer.Serialize(manifest);
        var parsed = JObject.Parse(json);
        Assert.Equal(1, (int)parsed["manifest_version"]!);
        Assert.Equal(18, (int)parsed["sdk_train"]!["major"]!);
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
