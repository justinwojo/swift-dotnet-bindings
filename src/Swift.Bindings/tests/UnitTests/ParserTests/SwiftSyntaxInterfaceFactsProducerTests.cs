// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BindingsGeneration.Producers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Unit coverage for <see cref="SwiftSyntaxInterfaceFactsProducer"/>, the sole
/// interface-facts producer.
/// Each test shells out to the SwiftInterfaceParser host binary and asserts the
/// fact it extracts from a hand-written .swiftinterface.
/// <para/>
/// SKIP BEHAVIOR: when the host binary isn't built, every test is skipped instead of
/// failing — <c>dotnet test</c> is a no-op in environments without a Swift toolchain.
/// CI runs <c>nuke compile</c> first, which produces the binary; local devs hit a
/// <c>Skip</c> reason telling them how to fix it.
/// </summary>
public class SwiftSyntaxInterfaceFactsProducerTests
{
    #region Actor isolation — imported custom global actor at member level

    /// <summary>
    /// Member-level isolation to an imported custom global actor. A member annotated with
    /// a global actor imported from ANOTHER module (<c>@SomeMod.RemoteActor func work()</c>,
    /// with no <c>actor RemoteActor</c> declared in this file) must be recognized as
    /// actor-isolated so the generator surfaces it as a <c>Task&lt;T&gt;</c> API — calling
    /// it from outside that actor's domain requires <c>await</c> exactly like a same-file
    /// custom actor. SwiftSyntax closes the gap by enabling the imported
    /// <c>@(?:\w+\.)+(?!MainActor\b)(\w*Actor)\b</c> heuristic at the member level, the same
    /// fallback it already used for type-level <c>customActorIsolatorMap</c>. There is no
    /// corpus library that exercises this (the one imported actor in the corpus,
    /// <c>@BlinkID.ProcessingActor</c>, is declared same-file and caught by the local
    /// short-name set), so this test is the durable guard for the divergence.
    /// </summary>
    [SkippableFact]
    public void MemberIsolatedToImportedCustomActor_IsActorIsolated()
    {
        var binaryPath = ResolveBinaryOrSkip(nameof(MemberIsolatedToImportedCustomActor_IsActorIsolated));
        // `RemoteActor` is NOT declared in this file — it is imported from `SomeMod`, so the
        // local short-name set is empty and only the imported heuristic can match it.
        var path = WriteTempFile(
            "public class RemoteThing {\n" +
            "  @SomeMod.RemoteActor public func remoteWork()\n" +
            "  public func localWork()\n" +
            "}\n");
        try
        {
            var swiftSyntax = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);

            Assert.Contains(InterfaceFactKind.ActorIsolatedMembers, swiftSyntax.CoveredFacts);
            var isolated = swiftSyntax.Facts.ActorIsolatedMembers;
            Assert.NotNull(isolated);

            // The imported-actor-isolated member is detected...
            Assert.Contains("RemoteThing.remoteWork()", isolated!);
            // ...its un-isolated sibling is not...
            Assert.DoesNotContain("RemoteThing.localWork()", isolated!);

            // ...and a custom (non-Main) actor never lands in the MainActor subset, which
            // controls whether the API is exposed as sync (@MainActor) vs Task<T> (custom).
            var mainIsolated = swiftSyntax.Facts.MainActorIsolatedMembers;
            Assert.NotNull(mainIsolated);
            Assert.DoesNotContain("RemoteThing.remoteWork()", mainIsolated!);
        }
        finally
        {
            File.Delete(path);
        }
    }

    #endregion

    #region Source provenance — positions for MainActor / availability / convention(c)

    [SkippableFact]
    public void MainActorType_EmitsLineAndColumnFromTypeDeclaration()
    {
        // Line 1: header import
        // Line 2: blank
        // Line 3: @MainActor pending annotation
        // Line 4: type declaration — position should point here
        var positions = ProducePositions(
            "import Swift\n" +
            "\n" +
            "@MainActor\n" +
            "public struct Widget {\n" +
            "}\n",
            f => f.MainActorTypePositions,
            out var path);
        try
        {
            Assert.True(positions.TryGetValue("Widget", out var pos), "Widget should have a recorded position.");
            Assert.Equal(path, pos.FilePath);
            Assert.Equal(4, pos.Line);
            // "public struct Widget" begins at column 1 (no leading whitespace).
            Assert.Equal(1, pos.Column);
        }
        finally { File.Delete(path); }
    }

    [SkippableFact]
    public void MainActorType_RecordsLeadingWhitespaceColumn()
    {
        // Indented type — column must reflect the leading whitespace, not start at 1.
        // "  public struct Inner" — "public" begins at column 3.
        var positions = ProducePositions(
            "public struct Outer {\n" +
            "  @MainActor\n" +
            "  public struct Inner {\n" +
            "  }\n" +
            "}\n",
            f => f.MainActorTypePositions,
            out var path);
        try
        {
            // Outer.Inner is the qualified path for the nested @MainActor type.
            Assert.True(positions.TryGetValue("Outer.Inner", out var pos), "Outer.Inner should have a recorded position.");
            Assert.Equal(3, pos.Line);
            Assert.Equal(3, pos.Column);
        }
        finally { File.Delete(path); }
    }

    [SkippableFact]
    public void Availability_EmitsLineAndColumnAtDeclaration()
    {
        // Line 1: header
        // Line 2: pending @available
        // Line 3: type — position should point here, NOT at the annotation line
        var positions = ProducePositions(
            "import Foundation\n" +
            "@available(iOS 16.0, *)\n" +
            "public struct Gadget {\n" +
            "}\n",
            f => f.AvailabilityAnnotationPositions,
            out var path);
        try
        {
            Assert.True(positions.TryGetValue("Gadget", out var pos), "Gadget should have a recorded position.");
            Assert.Equal(path, pos.FilePath);
            Assert.Equal(3, pos.Line);
            Assert.Equal(1, pos.Column);
        }
        finally { File.Delete(path); }
    }

    [SkippableFact]
    public void Availability_InlineAnnotation_ColumnSkipsPastAtToken()
    {
        // `@available(iOS 16, *) public struct Inline {` — the column should point at
        // `public` (after the inline @available), not at `@`.
        var positions = ProducePositions(
            "import Foundation\n" +
            "@available(iOS 16.0, *) public struct Inline {\n" +
            "}\n",
            f => f.AvailabilityAnnotationPositions,
            out var path);
        try
        {
            Assert.True(positions.TryGetValue("Inline", out var pos), "Inline should have a recorded position.");
            Assert.Equal(2, pos.Line);
            // "@available(iOS 16.0, *) " is 24 chars; "public" starts at column 25.
            Assert.Equal(25, pos.Column);
        }
        finally { File.Delete(path); }
    }

    [SkippableFact]
    public void Availability_QualifiedInlineAttribute_ColumnSkipsPastDottedAt()
    {
        // Swiftinterface attributes can be dotted (`@_Concurrency.MainActor`,
        // `@Module.Actor`). The annotation skipper must walk through the dot-separated
        // identifier components, not stop at the first `.`.
        var positions = ProducePositions(
            "import Foundation\n" +
            "@available(iOS 16.0, *) @_Concurrency.MainActor public struct Stacked {\n" +
            "}\n",
            f => f.AvailabilityAnnotationPositions,
            out var path);
        try
        {
            Assert.True(positions.TryGetValue("Stacked", out var pos), "Stacked should have a recorded position.");
            Assert.Equal(2, pos.Line);
            // "@available(iOS 16.0, *) @_Concurrency.MainActor " is 48 chars; "public" at column 49.
            Assert.Equal(49, pos.Column);
        }
        finally { File.Delete(path); }
    }

    [SkippableFact]
    public void ConventionCProtocol_EmitsPositionAtProtocolHeader()
    {
        // Line 1: header
        // Line 2: blank
        // Line 3: protocol declaration — position target
        // Line 4: convention(c) param triggers detection
        var positions = ProducePositions(
            "import Swift\n" +
            "\n" +
            "public protocol Callback {\n" +
            "  func register(_ cb: @convention(c) (Swift.Int) -> Swift.Void)\n" +
            "}\n",
            f => f.ConventionCProtocolPositions,
            out var path);
        try
        {
            Assert.True(positions.TryGetValue("Callback", out var pos), "Callback should have a recorded position.");
            Assert.Equal(path, pos.FilePath);
            Assert.Equal(3, pos.Line);
            Assert.Equal(1, pos.Column);
        }
        finally { File.Delete(path); }
    }

    [SkippableFact]
    public void NonexistentInputFile_ReturnsEmptyAndZeroCoverage()
    {
        var binaryPath = ResolveBinaryOrSkip(nameof(NonexistentInputFile_ReturnsEmptyAndZeroCoverage));
        var bogus = "/tmp/nonexistent-" + Guid.NewGuid() + ".swiftinterface";
        var result = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(bogus, NullLogger.Instance);
        // A missing swiftinterface yields zero coverage and null fact payloads (NOT fabricated
        // empties) — callers treat "no file" as "facts unknown", not "facts empty".
        Assert.Empty(result.CoveredFacts);
        Assert.Null(result.Facts.MainActorTypes);
        Assert.Null(result.Facts.MainActorTypePositions);
        Assert.Null(result.Facts.AvailabilityAnnotationPositions);
        Assert.Null(result.Facts.ConventionCProtocolPositions);
        Assert.Null(result.Facts.TypedThrowsErrors);
    }

    #endregion

    #region Protocol-extension members — mutating detection

    [SkippableFact]
    public void ProtocolExtensionMember_MutatingDetectionDiscriminatesPerMethod()
    {
        var binaryPath = ResolveBinaryOrSkip(nameof(ProtocolExtensionMember_MutatingDetectionDiscriminatesPerMethod));
        var path = WriteTempFile(
            "import Swift\n" +
            "public protocol MutablePersistableRecord {\n" +
            "}\n" +
            "extension RecordStore.MutablePersistableRecord {\n" +
            "  public mutating func upsert(_ db: Swift.Int) throws\n" +
            "  public func doWork()\n" +
            "}\n");
        try
        {
            var result = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);
            var methods = result.Facts.ProtocolExtensionMethods;
            Assert.NotNull(methods);
            Assert.True(methods!.TryGetValue("RecordStore.MutablePersistableRecord", out var decls),
                "Protocol-extension methods should be keyed by the protocol's qualified name.");

            var upsert = Assert.Single(decls!, d => d.MethodName == "upsert");
            Assert.True(upsert.IsMutating, "`mutating func upsert` should set IsMutating.");

            var doWork = Assert.Single(decls!, d => d.MethodName == "doWork");
            Assert.False(doWork.IsMutating, "Non-mutating `func doWork` should leave IsMutating false.");
        }
        finally { File.Delete(path); }
    }

    #endregion

    #region @objc(CustomName) extraction

    [SkippableFact]
    public void ObjCRuntimeName_ExtractsCustomName()
    {
        var binaryPath = ResolveBinaryOrSkip(nameof(ObjCRuntimeName_ExtractsCustomName));
        var path = WriteTempFile(
            "import Foundation\n" +
            "@objc(MOSWidget) public class Widget {\n" +
            "  @objc public init()\n" +
            "}\n");
        try
        {
            var result = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);
            var names = result.Facts.ObjCRuntimeNames;
            Assert.NotNull(names);
            // The Swift source name keys the map; the @objc(...) value is the ObjC runtime name.
            Assert.True(names!.TryGetValue("Widget", out var runtimeName));
            Assert.Equal("MOSWidget", runtimeName);
        }
        finally { File.Delete(path); }
    }

    #endregion

    #region Enum raw values — integer-literal extraction

    /// <summary>
    /// An integer-backed RawRepresentable enum with explicit raw values (e.g. an error-code
    /// enum declaring <c>case wrongPassword = 17009</c>) must surface those Swift raw values
    /// so the lowered C# enum members and the @_cdecl marshalling switches carry the real
    /// value rather than declaration-order ordinals. The walker normalizes hex/octal/binary/
    /// underscored/negative source forms to a base-10 string; a case with no explicit raw
    /// value contributes nothing (the host fills it via Swift's auto-increment rule
    /// downstream). A regression here silently restores the ordinal-instead-of-raw-value bug.
    /// </summary>
    [SkippableFact]
    public void EnumIntegerRawValues_ExtractedAndNormalizedToDecimal()
    {
        var binaryPath = ResolveBinaryOrSkip(nameof(EnumIntegerRawValues_ExtractedAndNormalizedToDecimal));
        var path = WriteTempFile(
            "public enum AuthErrorCode : Swift.Int {\n" +
            "  case wrongPassword = 17009\n" +
            "  case userNotFound = 17011\n" +
            "  case maskHex = 0xFF\n" +
            "  case maskOctal = 0o17\n" +
            "  case maskBinary = 0b101\n" +
            "  case grouped = 1_000\n" +
            "  case negative = -1\n" +
            "  case autoAssigned\n" +
            "}\n");
        try
        {
            var result = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);
            var raw = result.Facts.EnumCaseRawValues;
            Assert.NotNull(raw);
            Assert.Equal("17009", raw!["AuthErrorCode.wrongPassword"]);
            Assert.Equal("17011", raw["AuthErrorCode.userNotFound"]);
            // hex / octal / binary / underscore-grouped / negative source forms all normalize to base-10.
            Assert.Equal("255", raw["AuthErrorCode.maskHex"]);
            Assert.Equal("15", raw["AuthErrorCode.maskOctal"]);
            Assert.Equal("5", raw["AuthErrorCode.maskBinary"]);
            Assert.Equal("1000", raw["AuthErrorCode.grouped"]);
            Assert.Equal("-1", raw["AuthErrorCode.negative"]);
            // A case with no explicit raw value emits no fact.
            Assert.False(raw.ContainsKey("AuthErrorCode.autoAssigned"));
        }
        finally { File.Delete(path); }
    }

    #endregion

    #region Availability — protocol requirements without an access modifier (F-2)

    /// <summary>
    /// Protocol requirements carry no <c>public</c>/<c>open</c> modifier (the protocol's
    /// own access controls them), yet their <c>@available</c> annotations must still be
    /// harvested so the lowered C# surfaces the right <c>[SupportedOSPlatform]</c>. This
    /// re-homes the F-2 case from the deleted SwiftInterfaceAccessParserTests onto the
    /// SwiftSyntax host's AvailabilityWalker. A regression here silently detaches platform
    /// gating from protocol members.
    /// </summary>
    [SkippableFact]
    public void Availability_OnProtocolRequirementsWithoutAccessModifier_IsHarvested()
    {
        var annotations = ProduceAvailability(
            "@objc public protocol PaymentDelegate {\n" +
            "  @available(iOS 15.0, *)\n" +
            "  @objc optional func paymentSheetWillPresent()\n" +
            "\n" +
            "  @available(iOS 16.0, *)\n" +
            "  var supportedNetworks: [Swift.Int] { get }\n" +
            "}\n",
            out var path);
        try
        {
            Assert.True(annotations.TryGetValue("PaymentDelegate.paymentSheetWillPresent()", out var methodAnns),
                "A protocol-method @available must be harvested even without a `public` modifier.");
            Assert.Contains(methodAnns!, a => a.Platform == "iOS" && a.IntroducedVersion == "15.0");

            Assert.True(annotations.TryGetValue("PaymentDelegate.supportedNetworks", out var propAnns),
                "A protocol-property @available must be harvested even without a `public` modifier.");
            Assert.Contains(propAnns!, a => a.Platform == "iOS" && a.IntroducedVersion == "16.0");
        }
        finally { File.Delete(path); }
    }

    #endregion

    #region Typed throws — per-method error type extraction

    // These migrate the unit-level coverage that lived in the deleted
    // SwiftInterfaceTypedThrowsTests (which drove the retired regex
    // SwiftInterfaceAccessParser.GetTypedThrowsErrors). The producer now sources
    // typed-throws facts from the SwiftSyntax host's ThrowsWalker. The host's key shape
    // is the top-of-scope SIMPLE type name + "." + printedName (bare printedName for free
    // functions), and the value is the error type's source spelling — the same identity
    // SwiftABIParser later queries with parentDecl.Name. BindingTests exercises the
    // end-to-end binding; these pin the extraction itself.

    [SkippableFact]
    public void FreeFunction_TypedThrows_KeyedByPrintedName_PreservesQualifiedErrorType()
    {
        var errors = ProduceTypedThrows(
            "import Swift\n" +
            "public func parseNumber(_ s: Swift.String) throws(SwiftBindingsTestLib.ParseError) -> Swift.Int\n",
            out var path);
        try
        {
            Assert.True(errors.TryGetValue("parseNumber(_:)", out var errorType),
                "A free function with typed throws keys by its bare printed name.");
            // The fully-qualified error type spelling is preserved verbatim — not stripped to `ParseError`.
            Assert.Equal("SwiftBindingsTestLib.ParseError", errorType);
        }
        finally { File.Delete(path); }
    }

    [SkippableFact]
    public void InstanceMethod_TypedThrows_KeyedByTypeDotPrintedName()
    {
        var errors = ProduceTypedThrows(
            "import Swift\n" +
            "public class TypedThrowingParser {\n" +
            "  public func parse(_ s: Swift.String) throws(SwiftBindingsTestLib.ParseError) -> Swift.Int\n" +
            "}\n",
            out var path);
        try
        {
            Assert.True(errors.TryGetValue("TypedThrowingParser.parse(_:)", out var errorType),
                "An instance method keys by the enclosing type's simple name + printed name.");
            Assert.Equal("SwiftBindingsTestLib.ParseError", errorType);
        }
        finally { File.Delete(path); }
    }

    [SkippableFact]
    public void ExtensionMethod_TypedThrows_KeyedBySimpleExtendedTypeName()
    {
        var errors = ProduceTypedThrows(
            "import Swift\n" +
            "extension SwiftBindingsTestLib.NumberBox {\n" +
            "  public func validate(_ x: Swift.Int) throws(SwiftBindingsTestLib.ParseError)\n" +
            "}\n",
            out var path);
        try
        {
            // The extension scope keys by the LAST dotted component of the extended type
            // (`SwiftBindingsTestLib.NumberBox` -> `NumberBox`), not the fully-qualified path.
            Assert.True(errors.TryGetValue("NumberBox.validate(_:)", out var errorType),
                "An extension member keys by the extended type's simple (last-component) name.");
            Assert.Equal("SwiftBindingsTestLib.ParseError", errorType);
        }
        finally { File.Delete(path); }
    }

    [SkippableFact]
    public void UntypedThrowsAndNonThrowing_AreNotExtracted()
    {
        var errors = ProduceTypedThrows(
            "import Swift\n" +
            "public class Mixed {\n" +
            "  public func typed(_ s: Swift.String) throws(SwiftBindingsTestLib.ParseError) -> Swift.Int\n" +
            "  public func untyped(_ s: Swift.String) throws -> Swift.Int\n" +
            "  public func plain(_ s: Swift.String) -> Swift.Int\n" +
            "}\n",
            out var path);
        try
        {
            // Only the `throws(T)` form contributes; untyped `throws` and non-throwing
            // functions never key.
            Assert.True(errors.ContainsKey("Mixed.typed(_:)"), "The typed-throws method is extracted.");
            Assert.False(errors.ContainsKey("Mixed.untyped(_:)"), "An untyped `throws` method must NOT key.");
            Assert.False(errors.ContainsKey("Mixed.plain(_:)"), "A non-throwing method must NOT key.");
        }
        finally { File.Delete(path); }
    }

    [SkippableFact]
    public void MultipleTypedThrowingMethods_AreAllExtracted()
    {
        var errors = ProduceTypedThrows(
            "import Swift\n" +
            "public enum Codec {\n" +
            "  public func encode(_ s: Swift.String) throws(SwiftBindingsTestLib.EncodeError) -> Swift.Int\n" +
            "  public func decode(_ n: Swift.Int) throws(SwiftBindingsTestLib.DecodeError) -> Swift.String\n" +
            "}\n",
            out var path);
        try
        {
            Assert.Equal("SwiftBindingsTestLib.EncodeError", Assert.Contains("Codec.encode(_:)", errors));
            Assert.Equal("SwiftBindingsTestLib.DecodeError", Assert.Contains("Codec.decode(_:)", errors));
        }
        finally { File.Delete(path); }
    }

    // The four guards below re-home the structural-immunity coverage that the deleted
    // SwiftInterfaceTypedThrowsTests held over the retired regex producer. The host reads
    // ONLY a function's own effect-specifier throws clause (ThrowsWalker.extractTypedThrows),
    // so the misattribution shapes that defeated a text-scanning regex (a `throws` token
    // inside a string literal; a `throws` in the RETURN type of a returned closure;
    // multi-line signatures) cannot key here. These pin that structurally.

    [SkippableFact]
    public void ThrowsKeywordInsideStringLiteral_IsNotMisattributed()
    {
        var errors = ProduceTypedThrows(
            "import Swift\n" +
            "public func real(_ s: Swift.String) throws(SwiftBindingsTestLib.ParseError) -> Swift.Int\n" +
            "public func describe(_ s: Swift.String = \"throws(SwiftBindingsTestLib.NotReal)\") -> Swift.String\n",
            out var path);
        try
        {
            // Positive control: the genuine typed-throws function still keys, proving the
            // walker ran and the negative below is real absence, not a wholesale parse miss.
            Assert.True(errors.ContainsKey("real(_:)"), "The genuine typed-throws function keys (positive control).");
            Assert.False(errors.ContainsKey("describe(_:)"),
                "A `throws(...)` spelled inside a string-literal default value is not an effect specifier and must NOT key.");
        }
        finally { File.Delete(path); }
    }

    [SkippableFact]
    public void NonThrowingFunctionReturningThrowingClosure_IsNotMisattributed()
    {
        var errors = ProduceTypedThrows(
            "import Swift\n" +
            "public func makeHandler() -> (Swift.Int) throws -> Swift.Void\n" +
            "public func makeParenHandler() -> ((Swift.Int) throws -> Swift.Void)\n",
            out var path);
        try
        {
            // The `throws` lives in the return-type syntax, not the function's own effect
            // specifier — including when the closure type is extra-parenthesized.
            Assert.False(errors.ContainsKey("makeHandler()"),
                "A returned throwing closure is not the function's own typed-throws.");
            Assert.False(errors.ContainsKey("makeParenHandler()"),
                "Parenthesizing the returned throwing-closure type does not change that.");
        }
        finally { File.Delete(path); }
    }

    [SkippableFact]
    public void TypedThrowingFunctionReturningThrowingClosure_ExtractsOwnErrorOnly()
    {
        var errors = ProduceTypedThrows(
            "import Swift\n" +
            "public func makeHandler() throws(SwiftBindingsTestLib.ParseError) -> (Swift.Int) throws -> Swift.Void\n",
            out var path);
        try
        {
            // The function's own `throws(ParseError)` is extracted; the closure's untyped
            // `throws` in the return type must not perturb the error type.
            Assert.Equal("SwiftBindingsTestLib.ParseError", Assert.Contains("makeHandler()", errors));
        }
        finally { File.Delete(path); }
    }

    [SkippableFact]
    public void MultiLineSignatureAndAsyncExtension_TypedThrows_AreExtracted()
    {
        // A signature split across lines, and an `async throws(T)` extension member, both
        // key — the structured walker is neither line- nor async-sensitive for extraction.
        var errors = ProduceTypedThrows(
            "import Swift\n" +
            "public func transform(\n" +
            "  _ input: Swift.String\n" +
            ") throws(SwiftBindingsTestLib.ParseError) -> Swift.Int\n" +
            "extension SwiftBindingsTestLib.NumberBox {\n" +
            "  public func validateAsync(_ x: Swift.Int) async throws(SwiftBindingsTestLib.ParseError) -> Swift.Int\n" +
            "}\n",
            out var path);
        try
        {
            Assert.Equal("SwiftBindingsTestLib.ParseError", Assert.Contains("transform(_:)", errors));
            Assert.Equal("SwiftBindingsTestLib.ParseError", Assert.Contains("NumberBox.validateAsync(_:)", errors));
        }
        finally { File.Delete(path); }
    }

    #endregion

    #region Async accessors

    /// <summary>
    /// The swiftinterface is the only async-accessor oracle that does not depend on the TBD:
    /// the ABI JSON carries no async flag on accessor nodes and an async getter's mangled
    /// name is a plain <c>…vg</c>, so without this fact a library whose TBD symbol set is
    /// empty or unreadable emits every <c>get async</c> property as a synchronous one.
    ///
    /// Key shape is the full nested chain with the module prefix stripped, matching
    /// <c>SwiftABIParser.BuildTypeQualifiedPath</c>; a member declared in an
    /// <c>extension Mod.Outer.Inner</c> keys off the extension target with only its FIRST
    /// dot segment stripped, which a two-segment target cannot tell apart from a last-dot
    /// reading — hence the three-segment extension below. Only the <c>get</c> accessor's
    /// <c>async</c> specifier counts — a plain <c>{ get }</c>, and a <c>{ get throws }</c>
    /// without <c>async</c>, must stay absent.
    /// </summary>
    [SkippableFact]
    public void AsyncAccessors_ExtractedWithFullyQualifiedKeys()
    {
        var binaryPath = ResolveBinaryOrSkip(nameof(AsyncAccessors_ExtractedWithFullyQualifiedKeys));
        var path = WriteTempFile(
            "import Swift\n" +
            "public class Analyzer {\n" +
            "  public var subjects: Swift.Int32 {\n" +
            "    get async\n" +
            "  }\n" +
            "  public var plain: Swift.Int32 {\n" +
            "    get\n" +
            "  }\n" +
            "  public var checked: Swift.Int32 {\n" +
            "    get throws\n" +
            "  }\n" +
            "  public struct Subject {\n" +
            "    public var image: Swift.String {\n" +
            "      get async throws\n" +
            "    }\n" +
            "  }\n" +
            "}\n" +
            "extension TestModule.Analyzer {\n" +
            "  public var extended: Swift.String {\n" +
            "    get async\n" +
            "  }\n" +
            "}\n" +
            "extension TestModule.Analyzer.Subject {\n" +
            "  public var extra: Swift.String {\n" +
            "    get async\n" +
            "  }\n" +
            "}\n" +
            "public var topLevelAsync: Swift.Int32 {\n" +
            "  get async\n" +
            "}\n");
        try
        {
            var result = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);
            Assert.Contains(InterfaceFactKind.AsyncAccessorMembers, result.CoveredFacts);

            var members = result.Facts.AsyncAccessorMembers;
            Assert.NotNull(members);

            Assert.Contains("Analyzer.subjects", members!);
            Assert.Contains("Analyzer.Subject.image", members!);
            Assert.Contains("Analyzer.extended", members!);
            Assert.Contains("topLevelAsync", members!);

            // Only the module segment comes off the extension target: a last-dot reading
            // would key this "Subject.extra", which no BuildTypeQualifiedPath ever produces.
            Assert.Contains("Analyzer.Subject.extra", members!);
            Assert.DoesNotContain("Subject.extra", members!);

            Assert.DoesNotContain("Analyzer.plain", members!);
            Assert.DoesNotContain("Analyzer.checked", members!);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// A type may declare a <c>static</c> and an instance property of the same name, and the
    /// ABI exports a separate getter for each. Their keys must not collide: an unprefixed key
    /// for an async <c>static var value</c> would also name the synchronous instance
    /// <c>var value</c>, projecting a plain property as a Task-returning method and dropping
    /// its setter. Backticks come off both type and member names, because the ABI parser's half
    /// of the key carries the bare identifier.
    /// </summary>
    [SkippableFact]
    public void AsyncAccessors_SeparateStaticAndEscapedNamespaces()
    {
        var binaryPath = ResolveBinaryOrSkip(nameof(AsyncAccessors_SeparateStaticAndEscapedNamespaces));
        var path = WriteTempFile(
            "import Swift\n" +
            "public struct Holder {\n" +
            "  public static var value: Swift.Int32 {\n" +
            "    get async\n" +
            "  }\n" +
            "  public var value: Swift.Int32 {\n" +
            "    get\n" +
            "  }\n" +
            "  public var `switch`: Swift.Int32 {\n" +
            "    get async\n" +
            "  }\n" +
            "}\n" +
            "public struct `class` {\n" +
            "  public var thing: Swift.Int32 {\n" +
            "    get async\n" +
            "  }\n" +
            "}\n");
        try
        {
            var result = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);
            var members = result.Facts.AsyncAccessorMembers;
            Assert.NotNull(members);

            Assert.Contains("static Holder.value", members!);
            Assert.DoesNotContain("Holder.value", members!);

            Assert.Contains("Holder.switch", members!);
            Assert.Contains("class.thing", members!);
        }
        finally { File.Delete(path); }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Run the SwiftSyntax producer over <paramref name="swiftInterface"/> and return the
    /// selected position dictionary. Writes the interface to a temp file whose path is
    /// returned via <paramref name="path"/> so callers can assert <c>FilePath</c> and clean up.
    /// </summary>
    private static Dictionary<string, SourcePosition> ProducePositions(
        string swiftInterface,
        Func<PartialSwiftInterfaceFacts, Dictionary<string, SourcePosition>?> selector,
        out string path)
    {
        var binaryPath = ResolveBinaryOrSkip("Provenance");
        path = WriteTempFile(swiftInterface);
        var result = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);
        var positions = selector(result.Facts);
        Assert.NotNull(positions);
        return positions!;
    }

    /// <summary>
    /// Run the SwiftSyntax producer over <paramref name="swiftInterface"/> and return the
    /// extracted typed-throws map (method identity -> error type spelling). Asserts the
    /// fact is covered and non-null, matching the host's contract for a parsed file.
    /// </summary>
    private static Dictionary<string, string> ProduceTypedThrows(string swiftInterface, out string path)
    {
        var binaryPath = ResolveBinaryOrSkip("TypedThrows");
        path = WriteTempFile(swiftInterface);
        var result = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);
        Assert.Contains(InterfaceFactKind.TypedThrowsErrors, result.CoveredFacts);
        var errors = result.Facts.TypedThrowsErrors;
        Assert.NotNull(errors);
        return errors!;
    }

    /// <summary>
    /// Run the SwiftSyntax producer over <paramref name="swiftInterface"/> and return the
    /// extracted availability map (member identity -> annotation list).
    /// </summary>
    private static Dictionary<string, List<AvailabilityAnnotation>> ProduceAvailability(
        string swiftInterface, out string path)
    {
        var binaryPath = ResolveBinaryOrSkip("Availability");
        path = WriteTempFile(swiftInterface);
        var result = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);
        Assert.Contains(InterfaceFactKind.AvailabilityAnnotations, result.CoveredFacts);
        var annotations = result.Facts.AvailabilityAnnotations;
        Assert.NotNull(annotations);
        return annotations!;
    }

    private static string ResolveBinaryOrSkip(string label)
    {
        var path = SwiftSyntaxInterfaceFactsProducer.TryLocateBinary();
        // Xunit.Skip.IfNot from Xunit.SkippableFact: marks the [SkippableFact] as Skipped
        // (not Failed) when the precondition isn't met.
        Xunit.Skip.IfNot(path is not null && File.Exists(path),
            $"[{label}] SwiftInterfaceParser binary not found. Run `nuke compile` " +
            "(or set SWIFT_INTERFACE_PARSER_PATH) and re-run tests.");
        return path!;
    }

    private static string WriteTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"SwiftSyntaxProducer-{Guid.NewGuid()}.swiftinterface");
        File.WriteAllText(path, content);
        return path;
    }

    #endregion
}
