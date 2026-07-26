// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.ObjC;
using Xunit;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

/// <summary>
/// Tests for <see cref="ClangAstParser"/>'s availability recovery (Finding 22, recovery option a2).
/// <para/>
/// Clang's <c>-ast-dump=json</c> serializes <c>AvailabilityAttr</c> as only <c>{id, kind, range}</c>
/// — the platform / introduced / deprecated fields are NOT present. The parser recovers the data by
/// reading the consumer header at the attribute's <c>range.begin</c> source <em>byte offset</em> and
/// parsing the macro arguments. These tests write a real temp header and feed hand-authored clang
/// JSON whose offsets point into it, exercising the full byte-offset read + parse path without
/// needing clang itself. (An end-to-end real-clang round trip lives in
/// <c>ObjCPipelineIntegrationTests</c>.)
/// </summary>
public class ClangAstParserAvailabilityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _headerPath;

    public ClangAstParserAvailabilityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"objc_avail_recover_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _headerPath = Path.Combine(_tempDir, "TestLib.h");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private void WriteHeader(string content) => File.WriteAllText(_headerPath, content);

    // JSON string-escape a filesystem path (Windows backslashes / quotes; harmless on macOS).
    private string JsonPath(string p) => p.Replace("\\", "\\\\").Replace("\"", "\\\"");

    [Fact]
    public void Recovers_ApiAvailable_Macro_ViaExpansionLocOffset()
    {
        // Header content is just the annotation text; the offset points at the macro use-site,
        // exactly as clang's range.begin.expansionLoc would for a macro expansion.
        const string annotation = "API_AVAILABLE(ios(15.0))";
        WriteHeader(annotation);
        int offset = annotation.IndexOf("API_AVAILABLE", StringComparison.Ordinal);

        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "NewWidget",
            "loc": { "file": "{{JsonPath(_headerPath)}}" },
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "AvailabilityAttr",
                    "range": { "begin": { "expansionLoc": { "offset": {{offset}}, "file": "{{JsonPath(_headerPath)}}" } } }
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", _tempDir);

        var cls = Assert.Single(module.Classes);
        Assert.Equal("NewWidget", cls.Name);
        var avail = Assert.Single(cls.Availability);
        Assert.Equal("ios", avail.Platform);
        Assert.Equal("15.0", avail.IntroducedVersion);
    }

    [Fact]
    public void Recovers_BareAttribute_ViaDirectOffset()
    {
        // The bare __attribute__((availability(...))) form: clang's range.begin points directly at
        // the `availability` keyword (no expansionLoc indirection).
        const string annotation = "availability(macos, introduced=12.0, deprecated=13.0, message=\"gone\")";
        WriteHeader(annotation);
        int offset = annotation.IndexOf("availability", StringComparison.Ordinal);

        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "OldWidget",
            "loc": { "file": "{{JsonPath(_headerPath)}}" },
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "AvailabilityAttr",
                    "range": { "begin": { "offset": {{offset}}, "file": "{{JsonPath(_headerPath)}}" } }
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", _tempDir);

        var cls = Assert.Single(module.Classes);
        var avail = Assert.Single(cls.Availability);
        Assert.Equal("macos", avail.Platform);
        Assert.Equal("12.0", avail.IntroducedVersion);
        Assert.Equal("13.0", avail.DeprecatedVersion);
        Assert.Equal("gone", avail.Message);
    }

    [Fact]
    public void Recovers_MethodLevelAvailability()
    {
        const string annotation = "API_AVAILABLE(ios(16.0))";
        WriteHeader(annotation);
        int offset = annotation.IndexOf("API_AVAILABLE", StringComparison.Ordinal);

        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "NewWidget",
            "loc": { "file": "{{JsonPath(_headerPath)}}" },
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "ObjCMethodDecl",
                    "name": "doStuff",
                    "instance": true,
                    "returnType": { "qualType": "void" },
                    "inner": [
                        {
                            "kind": "AvailabilityAttr",
                            "range": { "begin": { "expansionLoc": { "offset": {{offset}}, "file": "{{JsonPath(_headerPath)}}" } } }
                        }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", _tempDir);
        var method = Assert.Single(module.Classes[0].Methods);
        var avail = Assert.Single(method.Availability);
        Assert.Equal("ios", avail.Platform);
        Assert.Equal("16.0", avail.IntroducedVersion);
    }

    [Fact]
    public void OffsetOutOfRange_DegradesToNoAvailability()
    {
        WriteHeader("API_AVAILABLE(ios(15.0))");

        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "NewWidget",
            "loc": { "file": "{{JsonPath(_headerPath)}}" },
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "AvailabilityAttr",
                    "range": { "begin": { "expansionLoc": { "offset": 99999, "file": "{{JsonPath(_headerPath)}}" } } }
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", _tempDir);
        // The class still parses; the unreadable offset just yields no availability.
        Assert.Single(module.Classes);
        Assert.Empty(module.Classes[0].Availability);
    }

    [Fact]
    public void OffsetNotAtAnnotation_DegradesToNoAvailability()
    {
        // Offset lands on plain text with no identifier(args) shape → no recovery, no throw.
        WriteHeader("   not an annotation   ");

        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "NewWidget",
            "loc": { "file": "{{JsonPath(_headerPath)}}" },
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "AvailabilityAttr",
                    "range": { "begin": { "offset": 0, "file": "{{JsonPath(_headerPath)}}" } }
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", _tempDir);
        Assert.Single(module.Classes);
        Assert.Empty(module.Classes[0].Availability);
    }

    [Fact]
    public void Recovers_EnumCaseAvailability_Independently()
    {
        // An NS_ENUM whose TYPE was introduced in ios(13.0) but one specific CASE was deprecated
        // later in ios(15.0). Apple SDKs do this constantly (a value retired in a newer OS than the
        // enum). The per-case AvailabilityAttr must be recovered and attached to that case only.
        const string typeAnno = "API_AVAILABLE(ios(13.0))";
        const string caseAnno = "API_DEPRECATED(\"use newMode\", ios(13.0, 15.0))";
        // Lay both annotations in the header so each carries a distinct, real byte offset.
        var header = typeAnno + "\n" + caseAnno + "\n";
        WriteHeader(header);
        int typeOffset = header.IndexOf("API_AVAILABLE", StringComparison.Ordinal);
        int caseOffset = header.IndexOf("API_DEPRECATED", StringComparison.Ordinal);

        var json = WrapInTranslationUnit($$"""
        {
            "kind": "EnumDecl",
            "name": "WidgetMode",
            "loc": { "file": "{{JsonPath(_headerPath)}}" },
            "fixedUnderlyingType": { "qualType": "NSInteger" },
            "inner": [
                {
                    "kind": "AvailabilityAttr",
                    "range": { "begin": { "expansionLoc": { "offset": {{typeOffset}}, "file": "{{JsonPath(_headerPath)}}" } } }
                },
                {
                    "kind": "EnumConstantDecl",
                    "name": "WidgetModeClassic"
                },
                {
                    "kind": "EnumConstantDecl",
                    "name": "WidgetModeLegacy",
                    "inner": [
                        {
                            "kind": "AvailabilityAttr",
                            "range": { "begin": { "expansionLoc": { "offset": {{caseOffset}}, "file": "{{JsonPath(_headerPath)}}" } } }
                        }
                    ]
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", _tempDir);

        var en = Assert.Single(module.Enums);
        // Enum-level availability recovered.
        var typeAvail = Assert.Single(en.Availability);
        Assert.Equal("ios", typeAvail.Platform);
        Assert.Equal("13.0", typeAvail.IntroducedVersion);

        Assert.Equal(2, en.Cases.Count);
        // The first case carries no annotation.
        Assert.Empty(en.Cases[0].Availability);
        // The second case carries its OWN deprecation, independent of the enum type's.
        var caseAvail = Assert.Single(en.Cases[1].Availability);
        Assert.Equal("ios", caseAvail.Platform);
        Assert.Equal("15.0", caseAvail.DeprecatedVersion);
    }

    [Fact]
    public void Merges_AvailabilityFromSparserDuplicate()
    {
        // Real-offset exercise of MergeAvailabilityInto: a class declared twice (umbrella + direct
        // include) where the availability macro landed on the SPARSER (non-richest by member count)
        // duplicate. The richest duplicate wins as the merge base, but the recovered availability from
        // the sparser one must be carried onto the merged class — otherwise a runtime-renamed or
        // OS-gated class loses its guard purely because of header include order.
        const string annotation = "API_AVAILABLE(ios(14.0))";
        WriteHeader(annotation);
        int offset = annotation.IndexOf("API_AVAILABLE", StringComparison.Ordinal);

        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "Widget",
            "loc": { "file": "{{JsonPath(_headerPath)}}" },
            "super": { "name": "NSObject" },
            "inner": [
                { "kind": "ObjCMethodDecl", "name": "doA", "instance": true, "returnType": { "qualType": "void" } },
                { "kind": "ObjCMethodDecl", "name": "doB", "instance": true, "returnType": { "qualType": "void" } }
            ]
        },
        {
            "kind": "ObjCInterfaceDecl",
            "name": "Widget",
            "loc": { "file": "{{JsonPath(_headerPath)}}" },
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "AvailabilityAttr",
                    "range": { "begin": { "expansionLoc": { "offset": {{offset}}, "file": "{{JsonPath(_headerPath)}}" } } }
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", _tempDir);

        var cls = Assert.Single(module.Classes);
        // Richest duplicate (2 methods) won the merge base...
        Assert.Equal(2, cls.Methods.Count);
        // ...but the availability recovered from the sparser duplicate is preserved.
        var avail = Assert.Single(cls.Availability);
        Assert.Equal("ios", avail.Platform);
        Assert.Equal("14.0", avail.IntroducedVersion);
    }

    [Fact]
    public void Merges_EnumAvailabilityFromSparserDuplicate()
    {
        // Enums dedup by RICHEST (most cases), but the availability macro can land on the SPARSER
        // duplicate — e.g. a forward-ish enum declaration carrying the OS gate, then a fuller
        // redeclaration with more cases but no annotation. DeduplicateByRichestMergingAvailability must
        // keep the richest as the base AND merge the sparser duplicate's recovered availability, exactly
        // like the class/protocol/function merge paths. Without the merge, the enum's [SupportedOSPlatform]
        // is silently dropped purely because the fuller decl came from a different header.
        const string annotation = "API_AVAILABLE(ios(14.0))";
        WriteHeader(annotation);
        int offset = annotation.IndexOf("API_AVAILABLE", StringComparison.Ordinal);

        var json = WrapInTranslationUnit($$"""
        {
            "kind": "EnumDecl",
            "name": "WidgetMode",
            "loc": { "file": "{{JsonPath(_headerPath)}}" },
            "fixedUnderlyingType": { "qualType": "NSInteger" },
            "inner": [
                {
                    "kind": "AvailabilityAttr",
                    "range": { "begin": { "expansionLoc": { "offset": {{offset}}, "file": "{{JsonPath(_headerPath)}}" } } }
                },
                { "kind": "EnumConstantDecl", "name": "WidgetModeClassic" }
            ]
        },
        {
            "kind": "EnumDecl",
            "name": "WidgetMode",
            "loc": { "file": "{{JsonPath(_headerPath)}}" },
            "fixedUnderlyingType": { "qualType": "NSInteger" },
            "inner": [
                { "kind": "EnumConstantDecl", "name": "WidgetModeClassic" },
                { "kind": "EnumConstantDecl", "name": "WidgetModeModern" }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", _tempDir);

        var en = Assert.Single(module.Enums);
        // Richest duplicate (2 cases) won the merge base...
        Assert.Equal(2, en.Cases.Count);
        // ...but the availability recovered from the sparser (1-case) duplicate is preserved.
        var avail = Assert.Single(en.Availability);
        Assert.Equal("ios", avail.Platform);
        Assert.Equal("14.0", avail.IntroducedVersion);
    }

    [Fact]
    public void WrapperMacro_DegradesToNoAvailability()
    {
        // GRACEFUL-DEGRADE limitation (documented): when a header nests the availability macro inside
        // a user/framework WRAPPER macro (e.g. Matter's MTR_AVAILABLE(...)), clang's expansionLoc
        // anchors at the OUTERMOST token — the wrapper, not the expanded API_AVAILABLE. The recovery
        // reads the wrapper token, which is not a known availability macro, so it emits nothing rather
        // than crashing or guessing garbage. This pins that degrade contract.
        const string annotation = "MTR_AVAILABLE(ios(16.4))";
        WriteHeader(annotation);
        int offset = annotation.IndexOf("MTR_AVAILABLE", StringComparison.Ordinal);

        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "MTRWidget",
            "loc": { "file": "{{JsonPath(_headerPath)}}" },
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "AvailabilityAttr",
                    "range": { "begin": { "expansionLoc": { "offset": {{offset}}, "file": "{{JsonPath(_headerPath)}}" } } }
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", _tempDir);
        // The class still parses cleanly; the unrecognized wrapper token yields no availability.
        var cls = Assert.Single(module.Classes);
        Assert.Equal("MTRWidget", cls.Name);
        Assert.Empty(cls.Availability);
    }

    [Fact]
    public void Merges_AvailabilityFromLaterDuplicateFunctionDecl()
    {
        // Real header shape (confirmed against clang AST): a bare forward declaration followed by a
        // redeclaration that carries the availability macro —
        //   void ProbeFunc(void);
        //   void ProbeFunc(void) API_AVAILABLE(ios(15.0));
        // clang emits TWO FunctionDecls (the second with a previousDecl back-ref); the FIRST is bare
        // and the SECOND carries the AvailabilityAttr. DeduplicateByFirstMergingAvailability must keep
        // the first decl but merge the later decl's recovered availability — otherwise the guard is
        // silently dropped (the bare forward decl wins and the annotation is lost).
        const string annotation = "API_AVAILABLE(ios(15.0))";
        WriteHeader(annotation);
        int offset = annotation.IndexOf("API_AVAILABLE", StringComparison.Ordinal);

        var json = WrapInTranslationUnit($$"""
        {
            "kind": "FunctionDecl",
            "name": "ProbeFunc",
            "loc": { "file": "{{JsonPath(_headerPath)}}" },
            "type": { "qualType": "void (void)" }
        },
        {
            "kind": "FunctionDecl",
            "name": "ProbeFunc",
            "loc": { "file": "{{JsonPath(_headerPath)}}" },
            "type": { "qualType": "void (void)" },
            "inner": [
                {
                    "kind": "AvailabilityAttr",
                    "range": { "begin": { "expansionLoc": { "offset": {{offset}}, "file": "{{JsonPath(_headerPath)}}" } } }
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", _tempDir);

        var fn = Assert.Single(module.Functions);
        Assert.Equal("ProbeFunc", fn.Name);
        var avail = Assert.Single(fn.Availability);
        Assert.Equal("ios", avail.Platform);
        Assert.Equal("15.0", avail.IntroducedVersion);
    }

    [Fact]
    public void Recovers_Availability_AfterMultibyteUtf8Comment()
    {
        // Clang source offsets are BYTE offsets. A header with a multi-byte UTF-8 comment before the
        // macro shifts the byte offset past the char count, so the recovery must slice over bytes (it
        // reads File.ReadAllBytes), not over a decoded string. This canary fails if the scan ever
        // regresses to char-indexing: the byte offset would land mid-token and degrade to nothing.
        const string comment = "// café ☕ déjà vu — naïve façade\n"; // multi-byte: é, ☕, à, ï
        const string annotation = "API_AVAILABLE(ios(15.0))";
        var header = comment + annotation;
        WriteHeader(header);

        // BYTE offset of the macro token (not the char index).
        int charIndex = header.IndexOf("API_AVAILABLE", StringComparison.Ordinal);
        int byteOffset = System.Text.Encoding.UTF8.GetByteCount(header[..charIndex]);
        // Sanity: the multi-byte comment makes the byte offset exceed the char index.
        Assert.True(byteOffset > charIndex);

        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "NewWidget",
            "loc": { "file": "{{JsonPath(_headerPath)}}" },
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "AvailabilityAttr",
                    "range": { "begin": { "expansionLoc": { "offset": {{byteOffset}}, "file": "{{JsonPath(_headerPath)}}" } } }
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", _tempDir);

        var cls = Assert.Single(module.Classes);
        var avail = Assert.Single(cls.Availability);
        Assert.Equal("ios", avail.Platform);
        Assert.Equal("15.0", avail.IntroducedVersion);
    }

    [Fact]
    public void NoRangeOnAttr_DegradesToNoAvailability()
    {
        // The literal real-clang AvailabilityAttr shape that carries {id, kind} but no usable range
        // offset (e.g. only a bare loc) recovers nothing — the decl still parses.
        var json = WrapInTranslationUnit($$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "NewWidget",
            "loc": { "file": "{{JsonPath(_headerPath)}}" },
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "AvailabilityAttr",
                    "loc": { "file": "{{JsonPath(_headerPath)}}" }
                }
            ]
        }
        """);

        var module = ClangAstParser.Parse(json, "TestLib", _tempDir);
        Assert.Single(module.Classes);
        Assert.Empty(module.Classes[0].Availability);
    }

    [Fact]
    public void SiblingHeaderSecondDecl_ResolvesOffsetAgainstItsOwnHeader_NotTheUmbrella()
    {
        // An attribute whose range.begin omits the file falls back to the DECLARATION's resolved
        // file, so getting that attribution wrong reads the byte offset out of the wrong header and
        // silently recovers the wrong version (or garbage).
        //
        // The shape is the ordinary named-umbrella framework: Umbrella.h #imports Sibling.h, and
        // Sibling.h declares more than one thing. Verified against real clang: the FIRST decl in
        // Sibling.h carries loc.file = Sibling.h, and every LATER decl in that same header carries
        // no file at all — only includedFrom = Umbrella.h, the file that included Sibling.h. So the
        // second decl's own file is knowable only by inheriting the tracked current file; the
        // includer says nothing about where the declaration lives.
        var umbrellaPath = Path.Combine(_tempDir, "Umbrella.h");
        var siblingPath = Path.Combine(_tempDir, "Sibling.h");

        // Same offset in both headers, different versions — so the assertion can only pass if the
        // read went to Sibling.h.
        const string umbrellaAnnotation = "API_AVAILABLE(ios(99.0))";
        const string siblingAnnotation = "API_AVAILABLE(ios(15.0))";
        File.WriteAllText(umbrellaPath, umbrellaAnnotation);
        File.WriteAllText(siblingPath, siblingAnnotation);
        int offset = siblingAnnotation.IndexOf("API_AVAILABLE", StringComparison.Ordinal);

        // First decl in Sibling.h: carries its own file, which becomes the tracked current file.
        var firstInSibling = $$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "FirstInSibling",
            "loc": { "file": "{{JsonPath(siblingPath)}}" },
            "super": { "name": "NSObject" }
        }
        """;

        // Second decl in the same header: no file of its own, includedFrom = the umbrella.
        var secondInSibling = $$"""
        {
            "kind": "ObjCInterfaceDecl",
            "name": "SecondInSibling",
            "loc": { "includedFrom": { "file": "{{JsonPath(umbrellaPath)}}" } },
            "super": { "name": "NSObject" },
            "inner": [
                {
                    "kind": "AvailabilityAttr",
                    "range": { "begin": { "expansionLoc": { "offset": {{offset}} } } }
                }
            ]
        }
        """;

        var json = WrapInTranslationUnit($"{firstInSibling},{secondInSibling}");
        var module = ClangAstParser.Parse(json, "TestLib", _tempDir);

        var second = Assert.Single(module.Classes, c => c.Name == "SecondInSibling");
        var avail = Assert.Single(second.Availability);
        Assert.Equal("ios", avail.Platform);
        Assert.Equal("15.0", avail.IntroducedVersion);
    }
}
