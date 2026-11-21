// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Load-safety P2: the OS symbol inventory, the per-run context that decides whether floors are
/// inferred, and the pass that floors every declaration the generating SDK exports but the inventory's
/// older OS lacks.
/// </summary>
public class OsSymbolInventoryTests
{
    // The Xcode 27.2 SDK exports these two getters without @available; iOS 26 does not ship them.
    internal const string ChaChaNonceCount = "$s9CryptoKit03ChaC4PolyO5NonceV5countSivg";
    internal const string AesGcmNonceCount = "$s9CryptoKit03AESO3GCMO5NonceV5countSivg";
    private const string ChaChaNonceDescriptor = "$s9CryptoKit03ChaC4PolyO5NonceVMn";
    private const string StringCount = "$sSS5countSivg";

    [Theory]
    [InlineData(StringCount, 0x1595145847UL)]
    [InlineData(ChaChaNonceCount, 0x2d0e895c3cUL)]
    [InlineData("", 0xe484222325UL)]
    [InlineData("a", 0x4c8601ec8cUL)]
    public void Hash_MatchesTheInventoryBuilder(string symbol, ulong expected)
        => Assert.Equal(expected, OsSymbolInventory.Hash(symbol));

    [Fact]
    public void Shipped_CoversEveryVariantAt26_2_FromXcode26_3()
    {
        var shipped = OsSymbolInventory.Shipped;
        Assert.Equal(new[] { "ios", "ios-simulator", "tvos", "tvos-simulator", "macos", "maccatalyst" },
            shipped.Platforms.Select(p => p.Variant));
        Assert.All(shipped.Platforms, p => Assert.Equal("26.2", p.OsVersion));
        Assert.Contains("Xcode 26.3", shipped.Provenance);
        Assert.Empty(shipped.Providers);
    }

    [Fact]
    public void Shipped_ShipsTheNonceType_ButNotTheCountGettersXcode27Added()
    {
        var shipped = OsSymbolInventory.Shipped;
        var ios = shipped.Platforms.Where(p => p.Variant is "ios" or "ios-simulator").ToList();
        Assert.True(shipped.ShipsOnAll(shipped.Platforms, StringCount));
        Assert.True(shipped.ShipsOnAll(ios, ChaChaNonceDescriptor));
        Assert.False(shipped.ShipsOnAll(ios, ChaChaNonceCount));
        Assert.False(shipped.ShipsOnAll(ios, AesGcmNonceCount));
    }

    [Fact]
    public void ReadBinary_DecodesEachGroupsMask()
    {
        using var file = new TempFile(BuildBinary(new Dictionary<string, int>
        {
            ["$sBoth"] = 0b11, ["$sDeviceOnly"] = 0b01, ["$sSimOnly"] = 0b10,
        }));
        var inventory = OsSymbolInventory.LoadFile(file.Path);
        var device = inventory.Platforms.Where(p => p.Variant == "ios").ToList();
        var both = inventory.Platforms.ToList();

        Assert.True(inventory.ShipsOnAll(both, "$sBoth"));
        Assert.True(inventory.ShipsOnAll(device, "$sDeviceOnly"));
        Assert.False(inventory.ShipsOnAll(both, "$sDeviceOnly"));
        Assert.False(inventory.ShipsOnAll(device, "$sSimOnly"));
        Assert.False(inventory.ShipsOnAll(device, "$sNowhere"));
        Assert.Contains("Xcode 26.3 (17C529)", inventory.Provenance);
    }

    [Fact]
    public void ReadBinary_RejectsAFileWithoutTheMagic()
    {
        using var file = new TempFile(Gzip(Encoding.ASCII.GetBytes("NOTANINVENTORY..")));
        Assert.Throws<InvalidDataException>(() => OsSymbolInventory.LoadFile(file.Path));
    }

    [Fact]
    public void Load_JsonProviders_ExtendTheInventoryInUse()
    {
        using var file = new TempFile(Encoding.UTF8.GetBytes("""
            { "format": "swift-os-symbol-inventory/1",
              "providers": [ { "installName": "@rpath/Lib.framework/Lib", "osVersion": "26.0", "floor": "99.0",
                               "variants": [ "ios-simulator" ], "symbols": [ "_$sLibA", "$sLibB" ] } ] }
            """));
        var current = OsSymbolInventory.Shipped;
        var loaded = OsSymbolInventory.Load(file.Path, current);

        var provider = Assert.Single(loaded.Providers);
        Assert.Same(provider, loaded.FindProvider("Lib"));
        Assert.Equal("Lib", provider.ModuleName);
        Assert.Equal("99.0", provider.Floor);
        Assert.Equal(new[] { "ios-simulator" }, provider.Variants);
        Assert.True(provider.Symbols.SetEquals(new[] { "$sLibA", "$sLibB" }));
        Assert.True(loaded.ShipsOnAll(loaded.Platforms, StringCount));
        Assert.Contains(file.Path, loaded.Provenance);
    }

    [Fact]
    public void Load_RejectsJsonWithoutTheFormat()
    {
        using var file = new TempFile(Encoding.UTF8.GetBytes("""{ "providers": [] }"""));
        Assert.Throws<InvalidDataException>(() => OsSymbolInventory.Load(file.Path, OsSymbolInventory.Shipped));
    }

    [Theory]
    [InlineData("""{ "format": "swift-os-symbol-inventory/1" }""")]
    [InlineData("""{ "format": "swift-os-symbol-inventory/1", "providers": {} }""")]
    [InlineData("""{ "format": "swift-os-symbol-inventory/1", "providers": [ { "osVersion": "26.0", "symbols": [] } ] }""")]
    [InlineData("""{ "format": "swift-os-symbol-inventory/1", "providers": [ { "installName": "@rpath/L.framework/L", "osVersion": "26.0", "symbols": [ null ] } ] }""")]
    public void Load_ReportsMalformedJsonAsUnreadable(string json)
    {
        using var file = new TempFile(Encoding.UTF8.GetBytes(json));
        Assert.Throws<InvalidDataException>(() => OsSymbolInventory.Load(file.Path, OsSymbolInventory.Shipped));
    }

    [Fact]
    public void ReadBinary_ReportsAHeaderRunningPastTheEndAsUnreadable()
    {
        var raw = new List<byte>(Encoding.ASCII.GetBytes("SBOSINV1"));
        raw.AddRange(BitConverter.GetBytes(uint.MaxValue));
        raw.AddRange(Encoding.ASCII.GetBytes("{}"));
        using var file = new TempFile(Gzip(raw.ToArray()));
        Assert.Throws<InvalidDataException>(() => OsSymbolInventory.Load(file.Path, OsSymbolInventory.Shipped));
    }

    [Fact]
    public void Load_ABinaryFileReplacesTheInventoryInUse()
    {
        using var json = new TempFile(Encoding.UTF8.GetBytes("""
            { "format": "swift-os-symbol-inventory/1",
              "providers": [ { "installName": "@rpath/Lib.framework/Lib", "osVersion": "26.0", "symbols": [] } ] }
            """));
        var withProvider = OsSymbolInventory.Load(json.Path, OsSymbolInventory.Shipped);
        using var binary = new TempFile(BuildBinary(new Dictionary<string, int> { ["$sOnly"] = 0b11 }));

        var replaced = OsSymbolInventory.Load(binary.Path, withProvider);
        Assert.Empty(replaced.Providers);
        Assert.True(replaced.ShipsOnAll(replaced.Platforms, "$sOnly"));
        Assert.False(replaced.ShipsOnAll(replaced.Platforms, StringCount));
    }

    /// <summary>A two-variant (ios bit 0, ios-simulator bit 1) inventory in the builder's format.</summary>
    internal static byte[] BuildBinary(IReadOnlyDictionary<string, int> symbolMasks)
    {
        var groups = symbolMasks.GroupBy(kv => kv.Value).OrderBy(g => g.Key)
            .Select(g => (Mask: g.Key, Hashes: g.Select(kv => OsSymbolInventory.Hash(kv.Key)).OrderBy(h => h).ToList()))
            .ToList();
        var header = "{\"format\":\"swift-os-symbol-inventory/1\",\"xcode\":{\"version\":\"26.3\",\"build\":\"17C529\"}," +
                     "\"platforms\":[{\"variant\":\"ios\",\"bit\":0,\"osVersion\":\"26.2\",\"sdkBuild\":\"23C57\"}," +
                     "{\"variant\":\"ios-simulator\",\"bit\":1,\"osVersion\":\"26.2\",\"sdkBuild\":\"23C57\"}]," +
                     "\"groups\":[" + string.Join(",", groups.Select(g => $"{{\"mask\":{g.Mask},\"count\":{g.Hashes.Count}}}")) + "]}";
        var raw = new List<byte>(Encoding.ASCII.GetBytes("SBOSINV1"));
        var headerBytes = Encoding.UTF8.GetBytes(header);
        raw.AddRange(BitConverter.GetBytes((uint)headerBytes.Length));
        raw.AddRange(headerBytes);
        foreach (var (_, hashes) in groups)
        {
            ulong previous = 0;
            foreach (var hash in hashes)
            {
                var delta = hash - previous;
                previous = hash;
                do
                {
                    var b = (byte)(delta & 0x7F);
                    delta >>= 7;
                    raw.Add(delta != 0 ? (byte)(b | 0x80) : b);
                } while (delta != 0);
            }
        }
        return Gzip(raw.ToArray());
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
            gzip.Write(data);
        return output.ToArray();
    }

    internal sealed class TempFile : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "osinv-" + Guid.NewGuid().ToString("N"));

        internal TempFile(byte[] content) => File.WriteAllBytes(Path, content);

        public void Dispose() => File.Delete(Path);
    }
}

public class OsSymbolFloorContextTests
{
    [Theory]
    [InlineData("27.2")]
    [InlineData("27.2.1")]
    public void TryCreate_FloorsAnOsFrameworkAtTheGeneratingSdksFullVersion(string sdkVersion)
    {
        var context = OsSymbolFloorContext.TryCreate(
            OsSymbolInventory.Shipped, "CryptoKit", ApplePlatform.iOS, isOsFramework: true, sdkVersion, out var reason);

        Assert.NotNull(context);
        Assert.Equal(string.Empty, reason);
        Assert.Equal("iOS", context!.AvailabilityPlatform);
        Assert.Equal("26.2", context.InventoryOsVersion);
        Assert.Equal(sdkVersion, context.Floor);
        Assert.True(context.ShipsOnInventoryOs("$sSS5countSivg"));
        Assert.False(context.ShipsOnInventoryOs(OsSymbolInventoryTests.ChaChaNonceCount));
    }

    [Theory]
    [InlineData(false, "27.2", "not an OS framework")]
    [InlineData(true, null, "version is unknown")]
    [InlineData(true, "26.2", "not newer")]
    public void TryCreate_InfersNothingWithAReason(bool isOsFramework, string? sdkVersion, string expectedReason)
    {
        var context = OsSymbolFloorContext.TryCreate(
            OsSymbolInventory.Shipped, "Kit", ApplePlatform.iOS, isOsFramework, sdkVersion, out var reason);
        Assert.Null(context);
        Assert.Contains(expectedReason, reason);
    }

    [Fact]
    public void TryCreate_MacCatalystReadsTheCatalystVariantAndPlatformName()
    {
        var context = OsSymbolFloorContext.TryCreate(
            OsSymbolInventory.Shipped, "CryptoKit", ApplePlatform.MacCatalyst, isOsFramework: true, "27.2", out _);
        Assert.Equal("macCatalyst", context!.AvailabilityPlatform);
        Assert.Equal(new[] { "maccatalyst" }, OsSymbolFloorContext.VariantsOf(ApplePlatform.MacCatalyst));
        Assert.Equal(new[] { "tvos", "tvos-simulator" }, OsSymbolFloorContext.VariantsOf(ApplePlatform.tvOS));
    }

    [Fact]
    public void LoadForRun_WeakLinksASuppliedProvider_InEveryLinkOfTheRun()
    {
        const string probe = "@rpath/LoadForRunProbe.framework/LoadForRunProbe";
        using var file = new OsSymbolInventoryTests.TempFile(Encoding.UTF8.GetBytes(
            $$"""{ "format": "swift-os-symbol-inventory/1", "providers": [ { "installName": "{{probe}}", "osVersion": "26.0", "symbols": [] } ] }"""));

        Assert.Same(OsSymbolInventory.Shipped, OsSymbolFloorContext.LoadForRun(null));
        Assert.False(OsFrameworkWeakLinking.IsOsResident(probe));
        Assert.Single(OsSymbolFloorContext.LoadForRun(file.Path).Providers);
        Assert.True(OsFrameworkWeakLinking.IsOsResident(probe));
    }

    [Fact]
    public void TryCreate_ASuppliedProviderUsesItsOwnFloorAndSymbols()
    {
        var inventory = WithProvider("""{ "installName": "@rpath/Lib.framework/Lib", "osVersion": "26.0", "floor": "99.0", "symbols": [ "$sLibA" ] }""");
        var context = OsSymbolFloorContext.TryCreate(inventory, "Lib", ApplePlatform.iOS, isOsFramework: false, "27.2", out _);

        Assert.Equal("99.0", context!.Floor);
        Assert.Equal("26.0", context.InventoryOsVersion);
        Assert.True(context.ShipsOnInventoryOs("$sLibA"));
        Assert.False(context.ShipsOnInventoryOs("$sLibB"));
        Assert.Contains("@rpath/Lib.framework/Lib", context.Source);
    }

    [Fact]
    public void TryCreate_ASuppliedProviderWithoutAFloorUsesTheGeneratingSdk_AndNeedsItAboveItsOs()
    {
        var inventory = WithProvider("""{ "installName": "@rpath/Lib.framework/Lib", "osVersion": "26.0", "symbols": [] }""");
        Assert.Equal("27.2", OsSymbolFloorContext.TryCreate(inventory, "Lib", ApplePlatform.iOS, false, "27.2", out _)!.Floor);

        Assert.Null(OsSymbolFloorContext.TryCreate(inventory, "Lib", ApplePlatform.iOS, false, "25.0", out var reason));
        Assert.Contains("does not exceed", reason);
    }

    [Fact]
    public void TryCreate_ASuppliedProviderForOtherVariantsDoesNotApply()
    {
        var inventory = WithProvider("""{ "installName": "@rpath/Lib.framework/Lib", "osVersion": "26.0", "floor": "99.0", "variants": [ "tvos" ], "symbols": [] }""");
        Assert.Null(OsSymbolFloorContext.TryCreate(inventory, "Lib", ApplePlatform.iOS, false, "27.2", out _));
        Assert.NotNull(OsSymbolFloorContext.TryCreate(inventory, "Lib", ApplePlatform.tvOS, false, "27.2", out _));
    }

    private static OsSymbolInventory WithProvider(string providerJson)
    {
        using var file = new OsSymbolInventoryTests.TempFile(Encoding.UTF8.GetBytes(
            "{ \"format\": \"swift-os-symbol-inventory/1\", \"providers\": [ " + providerJson + " ] }"));
        return OsSymbolInventory.Load(file.Path, OsSymbolInventory.Shipped);
    }
}

public class OsSymbolFloorInferenceTests
{
    private const string Module = "Kit";

    private static AvailabilityAnnotation Ann(string platform, string? introduced, bool unavailable = false) =>
        new(platform, introduced, null, null, false, unavailable, null, null);

    private static OsSymbolFloorContext Context(IEnumerable<string> ships, string platform = "iOS") =>
        new(Module, platform, "26.2", "27.2", new HashSet<string>(ships, StringComparer.Ordinal).Contains, "test");

    private static MethodDecl Method(string name, string mangled, BaseDecl? parent = null)
    {
        var method = TestDecls.Method(name, module: Module) with { MangledName = mangled };
        method.ParentDecl = parent;
        return method;
    }

    private static StructDecl Struct(string name, params TypeConformance[] conformances) => new()
    {
        Name = name,
        ParentDecl = null,
        ModuleDecl = null,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.{name}"),
        MangledName = $"$s3Kit{name.Length}{name}V",
        MetadataAccessor = $"$s3Kit{name.Length}{name}VMa",
        IsFrozen = false,
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Conformances = conformances.ToList(),
    };

    private static ModuleDecl ModuleWith(IEnumerable<string> exported, params BaseDecl[] members)
    {
        var module = new ModuleDecl
        {
            Name = Module,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            ExportedSymbols = new HashSet<string>(exported, StringComparer.Ordinal),
        };
        foreach (var member in members)
        {
            switch (member)
            {
                case MethodDecl method: module.Methods.Add(method); break;
                case PropertyDecl property: module.Properties.Add(property); break;
                case TypeDecl type: module.Types.Add(type); break;
            }
        }
        return module;
    }

    private static AvailabilityAnnotation? InferredFloor(IEnumerable<AvailabilityAnnotation>? annotations) =>
        annotations?.SingleOrDefault(a => a.IsInferredOsFloor);

    [Fact]
    public void AnAbsentMethod_IsFlooredAtTheGeneratingSdk_AsAnInferredRuntimeFloor()
    {
        var method = Method("count", "$s3Kit5countSivg");
        var floors = OsSymbolFloorInference.Apply(ModuleWith(new[] { method.MangledName }, method), Context(Array.Empty<string>()));

        var floor = InferredFloor(method.AvailabilityAnnotations);
        Assert.NotNull(floor);
        Assert.Equal("iOS", floor!.Platform);
        Assert.Equal("27.2", floor.IntroducedVersion);
        Assert.True(floor.IsRuntimeSupportFloor);
        var record = Assert.Single(floors);
        Assert.Equal(("method", "count", "$s3Kit5countSivg", "27.2", "26.2", true),
            (record.Kind, record.Declaration, record.Symbol, record.Floor, record.InventoryOsVersion, record.Raised));
        Assert.Equal(new[] { "$s3Kit5countSivg" }, record.Symbols);
    }

    [Fact]
    public void AShippedMethod_AndOneTheSdkDoesNotExport_AreLeftAlone()
    {
        var shipped = Method("shipped", "$s3Kit7shippedyyF");
        var inlined = Method("inlined", "$s3Kit7inlinedyyF");
        var floors = OsSymbolFloorInference.Apply(
            ModuleWith(new[] { shipped.MangledName }, shipped, inlined), Context(new[] { shipped.MangledName }));

        Assert.Empty(floors);
        Assert.Null(shipped.AvailabilityAnnotations);
        Assert.Null(inlined.AvailabilityAnnotations);
    }

    [Fact]
    public void AMethodDeclaredAboveTheInventoryOs_KeepsItsFloor_AndIsRecordedAsGuarded()
    {
        var method = Method("later", "$s3Kit5lateryyF");
        method.AvailabilityAnnotations = new List<AvailabilityAnnotation> { Ann("iOS", "27.0") };
        var floors = OsSymbolFloorInference.Apply(ModuleWith(new[] { method.MangledName }, method), Context(Array.Empty<string>()));

        Assert.Null(InferredFloor(method.AvailabilityAnnotations));
        var record = Assert.Single(floors);
        Assert.False(record.Raised);
        Assert.Equal("27.0", record.Floor);
    }

    [Fact]
    public void AMethodDeclaredAtOrBelowTheInventoryOs_IsRaisedAboveIt()
    {
        var method = Method("older", "$s3Kit5olderyyF");
        method.AvailabilityAnnotations = new List<AvailabilityAnnotation> { Ann("iOS", "16.0"), Ann("macOS", "13.0") };
        OsSymbolFloorInference.Apply(ModuleWith(new[] { method.MangledName }, method), Context(Array.Empty<string>()));

        Assert.Equal(3, method.AvailabilityAnnotations!.Count);
        Assert.Equal("27.2", InferredFloor(method.AvailabilityAnnotations)!.IntroducedVersion);
    }

    [Fact]
    public void AMethodUnavailableOnThePlatform_IsNeitherRaisedNorRecorded()
    {
        var method = Method("never", "$s3Kit5neveryyF");
        method.AvailabilityAnnotations = new List<AvailabilityAnnotation> { Ann("iOS", null, unavailable: true) };
        var floors = OsSymbolFloorInference.Apply(ModuleWith(new[] { method.MangledName }, method), Context(Array.Empty<string>()));

        Assert.Empty(floors);
        Assert.Single(method.AvailabilityAnnotations);
    }

    [Fact]
    public void AnAbsentType_FloorsItself_AndItsAbsentMembersAreGuardedThroughIt()
    {
        var type = Struct("Box");
        var member = Method("open", "$s3Kit3BoxV4openyyF", type);
        type.Methods.Add(member);
        var floors = OsSymbolFloorInference.Apply(
            ModuleWith(new[] { type.MangledName + "Mn", member.MangledName }, type), Context(Array.Empty<string>()));

        Assert.Equal("27.2", InferredFloor(type.AvailabilityAnnotations)!.IntroducedVersion);
        Assert.Null(member.AvailabilityAnnotations);
        Assert.Collection(floors,
            t => Assert.Equal(("type", true, type.MangledName + "Mn"), (t.Kind, t.Raised, t.Symbol)),
            m => Assert.Equal(("method", false, "27.2", "Kit.Box.open"), (m.Kind, m.Raised, m.Floor, m.Declaration)));
    }

    [Fact]
    public void AnAbsentGetter_FloorsThePropertyAndEveryAccessor()
    {
        var type = Struct("Nonce");
        var property = TestDecls.Property("count", hasSetter: true, module: Module);
        property.ParentDecl = type;
        type.Properties.Add(property);
        var getter = property.Accessors.OfType<GetAccessorDecl>().Single().Method.MangledName;
        var setter = property.Accessors.OfType<SetAccessorDecl>().Single().Method.MangledName;
        var floors = OsSymbolFloorInference.Apply(
            ModuleWith(new[] { type.MangledName + "Mn", getter, setter }, type), Context(new[] { type.MangledName + "Mn" }));

        Assert.NotNull(InferredFloor(property.AvailabilityAnnotations));
        Assert.All(property.Accessors, a => Assert.NotNull(InferredFloor(a.Method.AvailabilityAnnotations)));
        var record = Assert.Single(floors);
        Assert.Equal(("property", "Kit.Nonce.count", true), (record.Kind, record.Declaration, record.Raised));
        Assert.Equal(new[] { getter, setter }, record.Symbols);
    }

    [Fact]
    public void AnAbsentSetterOnAShippedProperty_FloorsTheSetterAlone()
    {
        var property = TestDecls.Property("value", hasSetter: true, module: Module);
        var getter = property.Accessors.OfType<GetAccessorDecl>().Single().Method;
        var setter = property.Accessors.OfType<SetAccessorDecl>().Single().Method;
        var floors = OsSymbolFloorInference.Apply(
            ModuleWith(new[] { getter.MangledName, setter.MangledName }, property), Context(new[] { getter.MangledName }));

        Assert.Null(property.AvailabilityAnnotations);
        Assert.Null(getter.AvailabilityAnnotations);
        Assert.Equal("27.2", InferredFloor(property.SetterAvailabilityAnnotations)!.IntroducedVersion);
        Assert.NotNull(InferredFloor(setter.AvailabilityAnnotations));
        Assert.Equal(("property setter", setter.MangledName), (Assert.Single(floors).Kind, floors[0].Symbol));
    }

    [Fact]
    public void AnAbsentCaseOnAShippedEnum_FloorsTheCaseAlone()
    {
        var shipped = new EnumCaseDecl { Name = "a", MangledName = "$s3Kit4ModeO1ayA2CmF", ParentDecl = null, ModuleDecl = null };
        var added = new EnumCaseDecl { Name = "b", MangledName = "$s3Kit4ModeO1byA2CmF", ParentDecl = null, ModuleDecl = null };
        var mode = new EnumDecl
        {
            Name = "Mode",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.Mode"),
            MangledName = "$s3Kit4ModeO",
            MetadataAccessor = "$s3Kit4ModeOMa",
            IsFrozen = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            Cases = new List<EnumCaseDecl> { shipped, added },
            ParentDecl = null,
            ModuleDecl = null,
        };
        var floors = OsSymbolFloorInference.Apply(
            ModuleWith(new[] { "$s3Kit4ModeOMn", shipped.MangledName, added.MangledName + "WC" }, mode),
            Context(new[] { "$s3Kit4ModeOMn", shipped.MangledName }));

        Assert.Null(mode.AvailabilityAnnotations);
        Assert.Null(shipped.AvailabilityAnnotations);
        Assert.Equal("27.2", InferredFloor(added.AvailabilityAnnotations)!.IntroducedVersion);
        Assert.Equal(("enum case", "Kit.Mode.b"), (Assert.Single(floors).Kind, floors[0].Declaration));
    }

    [Fact]
    public void AnAbsentOperator_FloorsTheOperatorAndItsMethod()
    {
        var box = Struct("Box");
        var method = Method("+", "$s3Kit3BoxV1poiyA2C_ACtFZ", box);
        var op = new OperatorDecl
        {
            Name = "+",
            OperatorSymbol = "+",
            Kind = OperatorKind.Binary,
            IsPrefix = false,
            UnderlyingMethod = method,
            ParentDecl = box,
            ModuleDecl = null,
        };
        box.Operators.Add(op);
        var floors = OsSymbolFloorInference.Apply(
            ModuleWith(new[] { box.MangledName + "Mn", method.MangledName }, box), Context(new[] { box.MangledName + "Mn" }));

        Assert.Equal("27.2", InferredFloor(op.AvailabilityAnnotations)!.IntroducedVersion);
        Assert.NotNull(InferredFloor(method.AvailabilityAnnotations));
        Assert.Equal("operator", Assert.Single(floors).Kind);
    }

    [Fact]
    public void AnAbsentSetterOnAShippedSubscript_FloorsTheSetterAlone()
    {
        var box = Struct("Box");
        var getter = Method("subscript_get", "$s3Kit3BoxVyS2icig", box);
        var setter = Method("subscript_set", "$s3Kit3BoxVyS2icis", box);
        var subscript = new SubscriptDecl
        {
            Name = "subscript",
            MangledName = "$s3Kit3BoxVyS2icip",
            ReturnTypeSpec = new NamedTypeSpec("Swift.Int"),
            IndexParameters = Array.Empty<ArgumentDecl>(),
            IsStatic = false,
            Accessors = new AccessorDecl[] { new GetAccessorDecl { Method = getter }, new SetAccessorDecl { Method = setter } },
            ParentDecl = box,
            ModuleDecl = null,
        };
        box.Subscripts.Add(subscript);
        var floors = OsSymbolFloorInference.Apply(
            ModuleWith(new[] { box.MangledName + "Mn", getter.MangledName, setter.MangledName }, box),
            Context(new[] { box.MangledName + "Mn", getter.MangledName }));

        Assert.Null(subscript.AvailabilityAnnotations);
        Assert.Null(getter.AvailabilityAnnotations);
        Assert.Equal("27.2", InferredFloor(setter.AvailabilityAnnotations)!.IntroducedVersion);
        Assert.Equal(("subscript setter", setter.MangledName), (Assert.Single(floors).Kind, floors[0].Symbol));

        // The indexer's set accessor advertises the setter's floor, under the CA1416 suppression its
        // call into the backing setter method needs (the analyzer does not narrow by accessor attributes).
        using var text = new StringWriter();
        var writer = new CSharpWriter(text);
        Assert.True(SubscriptHandler.EmitStricterSetterAvailability(writer, subscript));
        Assert.Contains("#pragma warning disable CA1416", text.ToString());
        Assert.Contains("SupportedOSPlatform(\"ios27.2\")", text.ToString());
    }

    [Fact]
    public void AnAbsentConformanceOnAShippedType_IsFlooredForTheModuleInitializer()
    {
        const string descriptor = "$s3Kit3BoxVSHAAMc";
        var type = Struct("Box", new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName($"{Module}.Box"), SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"), descriptor));
        var module = ModuleWith(new[] { type.MangledName + "Mn", descriptor }, type);
        var floors = OsSymbolFloorInference.Apply(module, Context(new[] { type.MangledName + "Mn" }));

        Assert.Null(type.AvailabilityAnnotations);
        Assert.Equal("27.2", InferredFloor(module.InferredConformanceFloors[descriptor])!.IntroducedVersion);
        Assert.Equal("conformance", Assert.Single(floors).Kind);
    }

    [Theory]
    [InlineData("27.0", null, false)]
    [InlineData("27.0", "16.0", true)]
    [InlineData(null, null, true)]
    public void MacCatalystInheritsTheIosFloorUnlessItNamesItsOwn(string? ios, string? catalyst, bool raised)
    {
        var method = Method("m", "$s3Kit1myyF");
        var own = new List<AvailabilityAnnotation>();
        if (ios is not null)
            own.Add(Ann("iOS", ios));
        if (catalyst is not null)
            own.Add(Ann("macCatalyst", catalyst));
        method.AvailabilityAnnotations = own;
        var floors = OsSymbolFloorInference.Apply(ModuleWith(new[] { method.MangledName }, method), Context(Array.Empty<string>(), "macCatalyst"));

        Assert.Equal(raised, InferredFloor(method.AvailabilityAnnotations) is { Platform: "macCatalyst" });
        Assert.Equal(raised, Assert.Single(floors).Raised);
    }

    [Fact]
    public void AClassMethodExportedOnlyAsItsDispatchThunk_IsJudgedByTheThunk()
    {
        var method = Method("run", "$s3Kit4TaskC3runyyF");
        var thunk = method.MangledName + "Tj";
        var floors = OsSymbolFloorInference.Apply(ModuleWith(new[] { thunk }, method), Context(Array.Empty<string>()));
        Assert.Equal(thunk, Assert.Single(floors).Symbol);

        var shipped = Method("run", "$s3Kit4TaskC3runyyF");
        Assert.Empty(OsSymbolFloorInference.Apply(ModuleWith(new[] { thunk }, shipped), Context(new[] { thunk })));
    }

    [Fact]
    public void AModuleWithoutExportedSymbols_InfersNothing()
    {
        var method = Method("count", "$s3Kit5countSivg");
        var module = ModuleWith(Array.Empty<string>(), method);
        module.ExportedSymbols = null;
        Assert.Empty(OsSymbolFloorInference.Apply(module, Context(Array.Empty<string>())));
        Assert.Null(method.AvailabilityAnnotations);
    }
}
