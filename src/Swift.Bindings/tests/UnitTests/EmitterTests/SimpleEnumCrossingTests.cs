// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="SimpleEnumCrossing"/>: how a simple enum that does not cross as its raw value
/// maps between its C# constant and its Swift case. The constant is the case's declaration-order
/// index; the in-memory tag of the same case differs once a case is hidden or a newer OS inserts
/// one, so every mapping must go through a switch on the case, never through the enum's bytes.
/// </summary>
public class SimpleEnumCrossingTests
{
    private static SimpleEnumCaseMap Map(
        bool frozen = false,
        int slots = 3,
        int widthBits = 32,
        IReadOnlyList<AvailabilityAnnotation>? availability = null,
        params SimpleEnumCrossingCase[] cases)
        => new(cases.Length == 0
                ? new[] { new SimpleEnumCrossingCase("red", 0, null), new SimpleEnumCrossingCase("green", 1, null), new SimpleEnumCrossingCase("blue", 2, null) }
                : cases,
            slots, frozen, availability, widthBits, 0);

    [Fact]
    public void RawValueCrossing_UsesRawValueInitAndRawValue()
    {
        Assert.Equal("M.E(rawValue: v)", SimpleEnumCrossing.ScalarToOptionalCase(null, "M.E", "v"));
        Assert.Equal("Int64((x).rawValue)", SimpleEnumCrossing.CaseToScalar(null, "M.E", "Int64", "x"));
    }

    [Fact]
    public void IdentityCrossing_CallsTheEnumsHelpersAndNeverLoadsEnumBytes()
    {
        var map = Map();
        var toCase = SimpleEnumCrossing.ScalarToOptionalCase(map, "M.Shade", "v");
        var toScalar = SimpleEnumCrossing.CaseToScalar(map, "M.Shade", "Swift.Int32", "x");

        Assert.Matches(@"^__sbCaseOf_M_Shade_[0-9A-F]+\(Swift\.Int64\(truncatingIfNeeded: v\)\)$", toCase);
        Assert.Matches(@"^Swift\.Int32\(truncatingIfNeeded: __sbIndexOf_M_Shade_[0-9A-F]+\(x\)\)$", toScalar);
        Assert.DoesNotContain("load(as:", toCase);
        Assert.DoesNotContain("copyMemory", toScalar);
        Assert.DoesNotContain("rawValue", toCase + toScalar);
    }

    [Fact]
    public void HelperPair_MapsEveryCaseByNameInBothDirections()
    {
        var definition = SimpleEnumCrossing.GetDefinition(Map(), "M.Shade");

        Assert.Contains("case 0: return .red as M.Shade", definition);
        Assert.Contains("case 1: return .green as M.Shade", definition);
        Assert.Contains("case 2: return .blue as M.Shade", definition);
        // Never through `.some(_:)`, which a library may overload on Optional for its own enum.
        Assert.DoesNotContain(".some(", definition);
        Assert.Contains("case .red: return 0", definition);
        Assert.Contains("case .green: return 1", definition);
        Assert.Contains("case .blue: return 2", definition);
        // Unknown values decode through the shared helper, which validates them against the metadata.
        Assert.Contains("default: return __sbUnknownCase(value, slots: 3, mask: 0xFFFFFFFF, as: M.Shade.self", definition);
        Assert.Contains("fileprivate func __sbCaseOf_", definition);
        Assert.Contains("fileprivate func __sbIndexOf_", definition);
    }

    [Theory]
    // A non-frozen enum may hold a case this interface does not list: library evolution's @unknown default.
    [InlineData(3, "@unknown default: return nil")]
    // A hidden (@_spi) case holds a slot the switch cannot name: a plain default.
    [InlineData(4, "    default: return nil")]
    public void IndexOf_CarriesACaseTheBindingDoesNotKnow(int slots, string expected)
    {
        var definition = SimpleEnumCrossing.GetDefinition(Map(slots: slots), "M.Shade");
        Assert.Contains(expected, KnownIndexOf(definition));
        // A case the binding does not name gets its unnamed value, never a trap.
        Assert.Contains($"return __sbUnknownCaseCode(value, slots: {slots}, mask: 0xFFFFFFFF, knownIndexOf: __sbKnownIndexOf_M_Shade_", definition);
        Assert.DoesNotContain("fatalError", definition);
    }

    [Fact]
    public void IndexOf_FrozenEnumWithEveryCaseNamed_IsExhaustive()
    {
        var definition = SimpleEnumCrossing.GetDefinition(Map(frozen: true), "M.Shade");
        Assert.DoesNotContain("default", KnownIndexOf(definition));
    }

    [Fact]
    public void UnknownCaseCode_CountsTheBinarysUnknownCasesNotItsTag()
    {
        // The unnamed value is mask - 1 - rank, the rank counting only the cases the binding does
        // not name below this one: published constants fill [0, slots) however the binary orders
        // its tags, so a hidden case after 128 published ones in a byte-wide enum still gets 0xFE.
        var appended = SimpleEnumCrossing.AppendHelperDefinitions(
            $"let x = {SimpleEnumCrossing.CaseToScalar(Map(), "M.Shade", "Swift.Int32", "q")}\n");
        var code = appended[appended.IndexOf("fileprivate func __sbUnknownCaseCode<", StringComparison.Ordinal)..];
        code = code[..code.IndexOf("\n}\n", StringComparison.Ordinal)];
        Assert.Contains("if knownIndexOf(__sbUnknownCaseAt(below, as: E.self)) == nil { rank += 1 }", code);
        Assert.Contains("return Swift.Int64(bitPattern: mask - 1 - rank)", code);
        Assert.DoesNotContain("mask - 1 - tag", code);
    }

    [Fact]
    public void CaseCodec_SwitchesOnTheCaseAndWritesSwiftsOwnLayout()
    {
        var output = new StringWriter();
        SimpleEnumCrossing.EmitCaseCodec(new SwiftWriter(output), Map(), "M.Shade", "sbmd_M_Shade");
        var code = output.ToString();
        var (load, store, loadOptional, storeOptional) = SimpleEnumCrossing.CaseCodecSymbols("sbmd_M_Shade");
        foreach (var symbol in new[] { load, store, loadOptional, storeOptional })
            Assert.Contains($"@_cdecl(\"{symbol}\")", code);
        // Reads and writes go through the case helpers and Swift's typed initializers, so the
        // codec holds whatever the binary's layout (tag order, payload cases, size) is.
        Assert.Contains("__sbIndexOf_", code);
        Assert.Contains("__sbCaseOf_", code);
        Assert.Contains("destination.initializeMemory(as: M.Shade.self, repeating: __sbCase, count: 1)", code);
        Assert.Contains("destination.initializeMemory(as: Swift.Optional<M.Shade>.self, repeating: nil, count: 1)", code);
        Assert.Contains("destination.initializeMemory(as: Swift.Optional<M.Shade>.self, repeating: __sbCase, count: 1)", code);
        Assert.Contains("source.assumingMemoryBound(to: Swift.Optional<M.Shade>.self).pointee", code);
        // A constant that names no case is refused with nothing written.
        Assert.Contains("else { return 0 }", code);
        Assert.DoesNotContain("storeBytes", code);
        Assert.DoesNotContain("load(as:", code);
    }

    [Fact]
    public void CaseCodec_CarriesTheEnumsAvailability()
    {
        var output = new StringWriter();
        var availability = new[] { new AvailabilityAnnotation("iOS", "17.0", null, null, false, false, null, null) };
        SimpleEnumCrossing.EmitCaseCodec(new SwiftWriter(output), Map(availability: availability), "M.Shade", "sbmd_M_Shade");
        var code = output.ToString();
        Assert.Equal(4, CountOf(code, "@available(iOS 17.0, *)\n"));
        Assert.Equal(4, CountOf(code, "@_cdecl("));
    }

    [Fact]
    public void CaseCodecSymbols_DeriveFromTheMetadataSymbolAndAreDistinct()
    {
        var a = SimpleEnumCrossing.CaseCodecSymbols("sbmd_M_Shade");
        Assert.Equal(a, SimpleEnumCrossing.CaseCodecSymbols("sbmd_M_Shade"));
        Assert.Equal(4, new HashSet<string> { a.Load, a.Store, a.LoadOptional, a.StoreOptional }.Count);
        Assert.NotEqual(a.Load, SimpleEnumCrossing.CaseCodecSymbols("sbmd_M_Tint").Load);
    }

    [Fact]
    public void CaseCodec_OnlyForAnEnumWhoseRecordListsItsCases()
    {
        Assert.True(SimpleEnumCrossing.HasCaseCodec(Map()));
        Assert.False(SimpleEnumCrossing.HasCaseCodec(null));
        // A record from a module database that predates the case list crosses as its tag, codec-less.
        Assert.False(SimpleEnumCrossing.HasCaseCodec(new SimpleEnumCaseMap(null, 0, false, null, 32, 3)));
    }

    [Fact]
    public void IndirectResultStore_WritesSwiftsOwnOptionalForACaseIdentityEnum()
    {
        // Every indirect method result (plain, throwing, protocol extension) is written by this one
        // statement. An Optional of a simple enum is written in Swift's own layout, which the
        // managed SwiftOptional reads through the enum's case codec.
        var module = new ModuleTypeDatabase("M", "/fake/M.dylib");
        var swiftName = SwiftTypeName.FromModuleQualifiedName("M.Shade");
        module.RegisterType(swiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("M", "Shade"),
            SwiftTypeName = swiftName,
            MetadataAccessor = "$s1M5ShadeO",
            Flags = TypeRecordFlags.SimpleEnum,
            Kind = TypeRecordKind.Enum,
            CrossingCases = new[] { new SimpleEnumCrossingCase("light", 0, null), new SimpleEnumCrossingCase("dark", 1, null) },
            CrossingSlotCount = 2,
        });
        var typeDatabase = new TypeDatabase();
        typeDatabase.AddModuleDatabase(module);

        var optionalShade = new NamedTypeSpec("Swift.Optional", new TypeSpec[] { new NamedTypeSpec("M.Shade") });
        var store = MethodWrapperEmitter.RenderIndirectResultStore(optionalShade, "result");
        Assert.Contains("resultPtr.initializeMemory(as:", store);
        Assert.DoesNotContain("storeBytes", store);
        Assert.NotNull(SimpleEnumCrossing.GetOptionalCodecMap(optionalShade, typeDatabase));

        var optionalInt = new NamedTypeSpec("Swift.Optional", new TypeSpec[] { new NamedTypeSpec("Swift.Int") });
        Assert.Contains("resultPtr.initializeMemory(as:", MethodWrapperEmitter.RenderIndirectResultStore(optionalInt, "result"));
        Assert.Null(SimpleEnumCrossing.GetOptionalCodecMap(optionalInt, typeDatabase));
        Assert.Null(SimpleEnumCrossing.GetOptionalCodecMap(new NamedTypeSpec("M.Shade"), typeDatabase));
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Theory]
    [InlineData(8, "0xFF")]
    [InlineData(16, "0xFFFF")]
    [InlineData(32, "0xFFFFFFFF")]
    [InlineData(64, "0xFFFFFFFFFFFFFFFF")]
    public void UnknownValueMask_IsTheWidthOfTheCSharpUnderlyingType(int widthBits, string mask)
    {
        var definition = SimpleEnumCrossing.GetDefinition(Map(widthBits: widthBits), "M.Shade");
        Assert.Contains($"mask: {mask}, as:", definition);
        Assert.Contains($"mask: {mask}, knownIndexOf:", definition);
    }

    [Fact]
    public void FlooredCase_IsNamedOnlyUnderAvailableAndOtherwiseNamesNoCase()
    {
        var map = Map(cases: new[]
        {
            new SimpleEnumCrossingCase("old", 0, null),
            new SimpleEnumCrossingCase("new", 1, "iOS 99, *"),
        }, slots: 2);
        var definition = SimpleEnumCrossing.GetDefinition(map, "M.Staged");

        Assert.Contains("case 1: if #available(iOS 99, *) { return .new as M.Staged } else { return nil }", definition);
        // Matching the case needs no availability check.
        Assert.Contains("case .new: return 1", definition);
    }

    [Fact]
    public void Helpers_CarryTheEnumsOwnAvailability()
    {
        var map = Map(availability: new[] { new AvailabilityAnnotation("iOS", "17.0", null, null, false, false, null, null) });
        var definition = SimpleEnumCrossing.GetDefinition(map, "M.Shade");
        Assert.Equal(3, CountOccurrences(definition, "@available(iOS 17.0, *)"));
    }

    [Fact]
    public void CaseNamedWithAKeyword_IsEscaped()
    {
        var map = Map(cases: new[] { new SimpleEnumCrossingCase("default", 0, null), new SimpleEnumCrossingCase("in", 1, null) }, slots: 2);
        var definition = SimpleEnumCrossing.GetDefinition(map, "M.Word");
        Assert.Contains("return .`default` as M.Word", definition);
        Assert.Contains("case .`in`: return 1", definition);
    }

    [Fact]
    public void CaseNamedLikeAnOptionalOrMetatypeMember_IsWrappedNotReadAsOptionalsOwnCase()
    {
        // In the Optional-returning index -> case helper a bare `.none` is Optional.none, which
        // would turn the enum's own `none` case into nil; `M.Access.Type` would be a metatype.
        var map = Map(cases: new[]
        {
            new SimpleEnumCrossingCase("none", 0, null),
            new SimpleEnumCrossingCase("some", 1, null),
            new SimpleEnumCrossingCase("Type", 2, null),
        }, slots: 3);
        var definition = SimpleEnumCrossing.GetDefinition(map, "M.Access");
        Assert.Contains("case 0: return .none as M.Access", definition);
        Assert.Contains("case 1: return .some as M.Access", definition);
        Assert.Contains("case 2: return .Type as M.Access", definition);
        Assert.DoesNotContain("return .none\n", definition);
    }

    [Fact]
    public void ScalarToCaseOrTrap_RefusesAValueThatNamesNoCaseWithABreadcrumb()
    {
        var expr = SimpleEnumCrossing.ScalarToCaseOrTrap(Map(), "M.Shade", "p0");
        Assert.Contains("let __sbValue = p0; guard let __sbCase = __sbCaseOf_M_Shade_", expr);
        Assert.Contains("Swift.preconditionFailure(\"[SwiftBindings] \\(__sbValue) names no case of M.Shade\")", expr);
        // The scalar (which may be a call) is evaluated exactly once.
        Assert.Equal(1, CountOccurrences(expr, "p0"));
    }

    [Fact]
    public void AppendHelperDefinitions_AppendsEachCalledHelperOnceWithTheSharedHelpers()
    {
        var map = Map();
        var call = SimpleEnumCrossing.ScalarToOptionalCase(map, "M.Shade", "a");
        var source = $"let x = {call}\nlet y = {call}\nlet z = {SimpleEnumCrossing.CaseToScalar(map, "M.Shade", "Swift.Int32", "q")}\n";

        var appended = SimpleEnumCrossing.AppendHelperDefinitions(source);

        Assert.StartsWith(source, appended, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(appended, "fileprivate func __sbCaseOf_M_Shade_"));
        Assert.Equal(1, CountOccurrences(appended, "fileprivate func __sbIndexOf_M_Shade_"));
        Assert.Equal(1, CountOccurrences(appended, "fileprivate func __sbUnknownCase<"));
        Assert.Equal(1, CountOccurrences(appended, "fileprivate func __sbUnknownCaseCode<"));
        Assert.Equal(1, CountOccurrences(appended, "fileprivate func __sbEnumCaseCounts("));
    }

    [Fact]
    public void Helpers_AreSpelledWithTheMapsQualifiedNameWhateverTheSiteCallsTheType()
    {
        // A site may spell the enum unqualified ("Color"), which another imported module can make
        // ambiguous in the wrapper; the helpers use the module-qualified name the map carries.
        var map = Map() with { SwiftTypeName = "M.Color" };
        var call = SimpleEnumCrossing.ScalarToOptionalCase(map, "Color", "v");
        Assert.StartsWith("__sbCaseOf_M_Color_", call);
        var definition = SimpleEnumCrossing.GetDefinition(map, "M.Color");
        Assert.Contains("-> M.Color? {", definition);
        Assert.Contains("(_ value: M.Color) -> Swift.Int64 {", definition);
        Assert.StartsWith("{ () -> M.Color in", SimpleEnumCrossing.ScalarToCaseOrTrap(map, "Color", "p0"));
        Assert.StartsWith("Swift.Int32(truncatingIfNeeded: __sbIndexOf_M_Color_",
            SimpleEnumCrossing.CaseToScalar(map, "Color", "Swift.Int32", "x"));
    }

    [Fact]
    public void AppendHelperDefinitions_LeavesAFileThatCallsNoHelperUnchanged()
    {
        const string source = "public func f() -> Swift.Int { 1 }\n";
        Assert.Same(source, SimpleEnumCrossing.AppendHelperDefinitions(source));
    }

    [Fact]
    public void DifferentCaseLists_GetDifferentHelpers()
    {
        var a = SimpleEnumCrossing.ScalarToOptionalCase(Map(), "M.Shade", "v");
        var b = SimpleEnumCrossing.ScalarToOptionalCase(Map(slots: 4), "M.Shade", "v");
        Assert.NotEqual(a, b);
        Assert.Equal(a, SimpleEnumCrossing.ScalarToOptionalCase(Map(), "M.Shade", "v"));
    }

    [Fact]
    public void LegacyRecord_WithNoCaseList_KeepsTheTagCrossingItWasBuiltFor()
    {
        var legacy = new SimpleEnumCaseMap(null, 0, false, null, 64, 3);
        var tag = SimpleEnumCrossing.ScalarToOptionalCase(legacy, "M.E", "v");
        Assert.Contains("..<3).contains(Swift.Int64(truncatingIfNeeded: v))", tag);
        Assert.Contains("load(as: M.E.self)", tag);
        Assert.EndsWith(": Swift.Optional<M.E>.none", tag);
        Assert.Contains("byteCount: Swift.MemoryLayout<M.E>.size", SimpleEnumCrossing.CaseToScalar(legacy, "M.E", "Int64", "x"));
        // One with no recorded case count was loaded unchecked, as before.
        Assert.StartsWith("Swift.Optional<M.E>.some(",
            SimpleEnumCrossing.ScalarToOptionalCase(legacy with { LegacyTagCount = 0 }, "M.E", "v"));
    }

    [Fact]
    public void BuildCases_LeavesOutSpiCasesButKeepsTheirSlots()
    {
        var enumDecl = CreateEnum(("hidden", true, null), ("north", false, null), ("south", false, "99.0"));
        var cases = SimpleEnumCrossing.BuildCases(enumDecl);

        Assert.Equal(new[] { "north", "south" }, cases.Select(c => c.SwiftName));
        // The constant stays the declaration-order index, @_spi case included.
        Assert.Equal(new long[] { 1, 2 }, cases.Select(c => c.Value));
        Assert.Null(cases[0].AvailableClause);
        Assert.Equal("iOS 99.0, *", cases[1].AvailableClause);
        Assert.Equal(3, SimpleEnumCrossing.SlotCount(enumDecl));

        var map = SimpleEnumCrossing.GetCaseMap(enumDecl);
        Assert.NotNull(map);
        Assert.Equal(3, map!.SlotCount);
        var definition = SimpleEnumCrossing.GetDefinition(map, "M.Dir");
        Assert.Contains("    default: return nil", KnownIndexOf(definition));
        Assert.Contains("return __sbUnknownCaseCode(value, slots: 3", definition);
    }

    [Fact]
    public void GetCaseMap_IsNullForAnEnumCrossingAsItsRawValue()
    {
        var enumDecl = CreateEnum(("a", false, null));
        enumDecl.IsObjC = true;
        enumDecl.RawValueTypeName = "Int";
        Assert.Null(SimpleEnumCrossing.GetCaseMap(enumDecl));
    }

    private static string KnownIndexOf(string definition)
    {
        var start = definition.IndexOf("func __sbKnownIndexOf_", StringComparison.Ordinal);
        var end = definition.IndexOf("fileprivate func", start, StringComparison.Ordinal);
        return definition[start..(end < 0 ? definition.Length : end)];
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static EnumDecl CreateEnum(params (string Name, bool Spi, string? Floor)[] cases)
    {
        var module = new ModuleDecl
        {
            Name = "M",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
        var enumDecl = new EnumDecl
        {
            Name = "Dir",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("M.Dir"),
            MangledName = "$s1M3DirON",
            MetadataAccessor = "$s1M3DirOMa",
            IsFrozen = false,
            Cases = new List<EnumCaseDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = module,
            ModuleDecl = module,
        };
        foreach (var (name, spi, floor) in cases)
        {
            var caseDecl = new EnumCaseDecl
            {
                Name = name,
                MangledName = $"$s1M3DirO{name}yA2CmFWC",
                AssociatedValues = new List<TypeSpec>(),
                ParentDecl = enumDecl,
                ModuleDecl = module,
                IsSpiProtected = spi,
            };
            if (floor != null)
                caseDecl.AvailabilityAnnotations = new List<AvailabilityAnnotation> { new("iOS", floor, null, null, false, false, null, null) };
            enumDecl.Cases.Add(caseDecl);
        }
        return enumDecl;
    }
}
