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
/// Parity gate for the M2 SwiftSyntax migration. Drives a representative corpus of
/// .swiftinterface inputs through the regex producer and the SwiftSyntax producer
/// and asserts every fact SwiftSyntax covers comes out byte-equal.
/// <para/>
/// Session 3 brings SwiftSyntax to 100% fact coverage (24/24) — every fact this
/// test exercises is now produced by SwiftSyntax. The remaining role of the regex
/// producer is the per-release rollback / parity diff path; M2 S4 retires it.
/// <para/>
/// SKIP BEHAVIOR: when the SwiftInterfaceParser host binary isn't built, every
/// fact in the test class is skipped instead of failing — `dotnet test` is a no-op
/// in environments without a Swift toolchain. CI runs `nuke compile` first, which
/// produces the binary. Local devs hit `Skip` reasons telling them how to fix.
/// </summary>
public class InterfaceFactsProducerParityTests
{
    /// <summary>
    /// Corpus for the MainActor parity gate inherited from M2 Session 1. Every entry
    /// is one the regex parser is documented to handle correctly today, so any
    /// divergence between the two producers is a real regression.
    /// </summary>
    public static IEnumerable<object[]> MainActorCorpus =>
        new[]
        {
            new object[] { "BasicMainActor",
                "import Swift\n" +
                "\n" +
                "@MainActor\n" +
                "public struct Widget {\n" +
                "}\n" },
            new object[] { "QualifiedMainActor",
                "import Swift\n" +
                "@_Concurrency.MainActor\n" +
                "public class Notifier {\n" +
                "}\n" },
            new object[] { "InlineAttribute",
                "import Swift\n" +
                "@MainActor public struct Inline {\n" +
                "}\n" },
            new object[] { "NestedType",
                "public struct Outer {\n" +
                "  @MainActor\n" +
                "  public struct Inner {\n" +
                "  }\n" +
                "}\n" },
            new object[] { "InternalAccessModifier",
                "@MainActor\n" +
                "internal struct Hidden {\n" +
                "}\n" },
            new object[] { "OpenAccessModifier",
                "@MainActor\n" +
                "open class Base {\n" +
                "}\n" },
            new object[] { "ProtocolDecl",
                "@MainActor\n" +
                "public protocol P {\n" +
                "}\n" },
            new object[] { "EnumDecl",
                "@MainActor\n" +
                "public enum E {\n" +
                "}\n" },
            new object[] { "MainActorOnActorIsSuppressed",
                // Regex parser's ActorDeclRegex matches `public|open actor`, so @MainActor
                // on a public actor is suppressed.
                "@MainActor\n" +
                "public actor Suppressed {\n" +
                "}\n" },
            new object[] { "MainActorOnInternalActorIsEmitted",
                // ActorDeclRegex requires public/open — internal actor falls through and IS
                // emitted by the regex parser. SwiftSyntax must match this quirk exactly.
                "@MainActor\n" +
                "internal actor InternallyHidden {\n" +
                "}\n" },
            new object[] { "MultipleTypes",
                "@MainActor\n" +
                "public struct First {}\n" +
                "@MainActor\n" +
                "public struct Second {}\n" +
                "public struct Third {}\n" },
            new object[] { "NoMainActorAtAll",
                "import Swift\n" +
                "public struct Plain {\n" +
                "}\n" },
            new object[] { "ExtensionScopeNotEmitted",
                // Extensions push scope but never emit. Regex parser doesn't add `extension`
                // bodies' types as MainActorTypes, even when @MainActor decorates them.
                "public struct Outer {}\n" +
                "@MainActor\n" +
                "extension Mod.Outer {\n" +
                "  public struct AppendedNested {}\n" +
                "}\n" },
            new object[] { "FinalClass",
                "@MainActor\n" +
                "public final class Locked {\n" +
                "}\n" },
            new object[] { "IndentedNested",
                "public struct Outer {\n" +
                "    @MainActor\n" +
                "    public struct DeepInner {\n" +
                "    }\n" +
                "}\n" },
        };

    /// <summary>
    /// Actor isolation cluster corpus. Exercises ActorIsolatedMembers,
    /// MainActorIsolatedMembers, NonisolatedMembers, CustomActorTypes, and
    /// CustomActorIsolatorMap. Each entry covers one or more drift-prone shapes.
    /// </summary>
    public static IEnumerable<object[]> ActorIsolationCorpus =>
        new[]
        {
            new object[] { "MemberLevelMainActor",
                "public class Mixed {\n" +
                "  @MainActor\n" +
                "  public func uiOnly()\n" +
                "  public func bgOnly()\n" +
                "}\n" },
            new object[] { "InlineQualifiedMainActor",
                "public class Mixed {\n" +
                "  @_Concurrency.MainActor public func uiInline()\n" +
                "}\n" },
            new object[] { "NonisolatedFunc",
                "public class C {\n" +
                "  nonisolated public func neutral()\n" +
                "}\n" },
            new object[] { "NonisolatedVar",
                "public class C {\n" +
                "  nonisolated public var name: Swift.String\n" +
                "}\n" },
            new object[] { "TopLevelMainActorFunc",
                "@MainActor public func tlMain()\n" },
            new object[] { "BareProtocolMainActor",
                // Bare protocol member: the regex's BareFuncRegex catches it inside
                // a protocol body. SwiftSyntax must match.
                "public protocol P {\n" +
                "  @MainActor func bare()\n" +
                "}\n" },
            new object[] { "PublicActorKeywordType",
                "public actor Pipeline {\n" +
                "}\n" },
            new object[] { "InternalActorKeywordIsNotCustom",
                // ActorDeclRegex requires public/open — internal actors are NOT in
                // CustomActorTypes per regex semantics. Mirror.
                "internal actor Hidden {\n" +
                "}\n" },
            new object[] { "CustomActorIsolation_BareName",
                // Local-actor regex matches bare @ActorName when ActorName is in the
                // file's customActorTypeNames set.
                "public actor PipelineActor {\n" +
                "}\n" +
                "@PipelineActor\n" +
                "public class Pipeline {\n" +
                "}\n" },
            new object[] { "CustomActorIsolation_QualifiedName",
                // Qualified module-prefixed annotation — local-actor regex's
                // (?:\\w+\\.)? handles one prefix. Same actor declared in this file.
                "public actor PipelineActor {\n" +
                "}\n" +
                "@MyMod.PipelineActor\n" +
                "public class Pipeline {\n" +
                "}\n" },
            new object[] { "ImportedCustomActor",
                // No local actor decl. ImportedCustomActorAnnotationRegex matches
                // `@<Module>.<Name>Actor` heuristically. MainActor excluded.
                "@SomeMod.RemoteActor\n" +
                "public class RemoteThing {\n" +
                "}\n" },
            new object[] { "MainActorOnTypeDoesNotEmitMembers",
                // Regex doesn't propagate type-level @MainActor to its members.
                // ActorIsolatedMembers stays empty for `bgOnly` even though `Pipeline`
                // is in MainActorTypes.
                "@MainActor\n" +
                "public class Pipeline {\n" +
                "  public func bgOnly()\n" +
                "}\n" },
        };

    /// <summary>
    /// Availability annotations corpus.
    /// </summary>
    public static IEnumerable<object[]> AvailabilityCorpus =>
        new[]
        {
            new object[] { "TypeLevelShorthand",
                "@available(iOS 16.0, macOS 13, *)\n" +
                "public struct Modern {\n" +
                "}\n" },
            new object[] { "TypeLevelDeprecated",
                "@available(*, deprecated, message: \"use Modern\")\n" +
                "public struct Old {\n" +
                "}\n" },
            new object[] { "MemberLevelLifecycle",
                "public struct Holder {\n" +
                "  @available(iOS, introduced: 10, deprecated: 12)\n" +
                "  public func legacy()\n" +
                "}\n" },
            new object[] { "MemberInline",
                "public struct Holder {\n" +
                "  @available(*, unavailable) public var blocked: Swift.Int\n" +
                "}\n" },
            new object[] { "FreeFunctionAvailability",
                "@available(iOS 17.0, *)\n" +
                "public func recent()\n" },
            new object[] { "MessageWithEmbeddedParens",
                "@available(*, deprecated, message: \"Use init(config:) instead\")\n" +
                "public struct Old {\n" +
                "}\n" },
            new object[] { "EnumCase_AvailabilityOnSingle",
                "public enum Status {\n" +
                "  @available(iOS 16.0, *)\n" +
                "  case fresh\n" +
                "}\n" },
            new object[] { "ExtensionScopeInherited",
                // @available on the extension propagates to every public member.
                "public struct T {}\n" +
                "@available(iOS 17.0, *)\n" +
                "extension Mod.T {\n" +
                "  public func added()\n" +
                "}\n" },
            new object[] { "ProtocolRequirementWithoutAccessModifier",
                // Family-F-2 (StripeApplePay): a bare protocol requirement carries no
                // explicit `public` modifier, but the @available annotation MUST still
                // be captured. Pre-fix, the regex parser's modifier gate swallowed the
                // requirement and silently dropped the annotation. The .NET-side fix
                // is covered by `GetAvailabilityAnnotations_F2_…` in
                // SwiftInterfaceAccessParserTests; this corpus case asserts that the
                // SwiftSyntax-side AvailabilityWalker produces byte-equal output for
                // the same shape so the dual-parser contract holds.
                "public protocol HasOptional {\n" +
                "  @available(iOS 17.0, *)\n" +
                "  func ping()\n" +
                "}\n" },
            new object[] { "OverloadedInlineAvailableWithCollectionSugar",
                // Inline @available on an overloaded member where one variant uses
                // Swift collection sugar (`[T]`) and the other uses an explicit
                // generic. The .NET-side disamb suffix and the SwiftSyntax-side
                // suffix MUST converge byte-equal — otherwise the producer stages
                // the annotation under a key the consumer never looks up.
                "public struct Holder {\n" +
                "  @available(iOS 17.0, *)\n" +
                "  public func f(_ xs: [Swift.Int]) -> Swift.Int\n" +
                "  public func f(_ xs: Swift.Set<Swift.Int>) -> Swift.Int\n" +
                "}\n" },
        };

    /// <summary>
    /// Typed throws corpus.
    /// </summary>
    public static IEnumerable<object[]> TypedThrowsCorpus =>
        new[]
        {
            new object[] { "FreeFunctionTypedThrows",
                "public func parseNumber(_ input: Swift.String) throws(SwiftBindingsTestLib.ParseError) -> Swift.Int32\n" },
            new object[] { "InstanceMethodTypedThrows",
                "public class Parser {\n" +
                "  public func parse(_ s: Swift.String) throws(MyMod.E) -> Swift.Int\n" +
                "}\n" },
            new object[] { "InitTypedThrows",
                "public struct Reader {\n" +
                "  public init(buffer: Swift.String) throws(MyMod.IOError)\n" +
                "}\n" },
            new object[] { "ExtensionMethodTypedThrows",
                // Extension keys use the LAST-component (simple) name, distinct from
                // ActorIsolatedMembers' first-stripped path. Mirror exactly.
                "extension SomeMod.Outer.Target {\n" +
                "  public func emit() throws(MyMod.E)\n" +
                "}\n" },
            new object[] { "UntypedThrowsExcluded",
                // Plain `throws` does NOT contribute to TypedThrowsErrors.
                "public func unsafe() throws -> Swift.Int\n" },
            new object[] { "NonThrowingExcluded",
                "public func plain() -> Swift.Int\n" },
        };

    /// <summary>Public-type-name collection corpus.</summary>
    public static IEnumerable<object[]> PublicTypeNamesCorpus =>
        new[]
        {
            new object[] { "TopLevelStruct",
                "public struct Foo {}\n" },
            new object[] { "TopLevelMixedAccess",
                "public struct A {}\n" +
                "internal struct B {}\n" +
                "public class C {}\n" },
            new object[] { "NestedTypes",
                "public struct Outer {\n" +
                "  public enum Inner {\n" +
                "    case fast\n" +
                "  }\n" +
                "}\n" },
            new object[] { "GenericStripped",
                "public class Box<T> {}\n" },
            new object[] { "FinalClass",
                "public final class Locked {}\n" },
            new object[] { "OpenClass",
                "open class Base {}\n" },
            new object[] { "ProtocolsAndActors",
                "public protocol P {}\n" +
                "public actor A {}\n" },
            new object[] { "InternalScope_PublicNestedKept",
                "public class Outer {\n" +
                "  internal class Hidden {\n" +
                "    public class Sub {}\n" +
                "  }\n" +
                "}\n" },
            // Regex `TypeDeclRegex` requires `(public|internal|open) (final )? (class|struct|enum|actor|protocol)`.
            // `public indirect enum` has `indirect` between access and the type keyword, so the
            // tracker does NOT push `Tree` onto its scope stack. Inner `Inner` should therefore
            // emit at module scope as `Inner`, NOT `Tree.Inner`.
            new object[] { "IndirectEnumScopeNotPushed",
                "public indirect enum Tree {\n" +
                "  public class Inner {}\n" +
                "}\n" },
        };

    /// <summary>Marker-protocol conformance corpus.</summary>
    public static IEnumerable<object[]> MarkerProtocolCorpus =>
        new[]
        {
            new object[] { "EmptyConformance",
                "extension Swift.Int : SnapKit.ConstraintOffsetTarget { }\n" },
            new object[] { "MultiProtocol",
                "extension Foo.Bar : Some.Proto1, Other.Proto2 { }\n" },
            new object[] { "BodyExtensionExcluded",
                "extension Foo : Bar {\n" +
                "  public func extra() {}\n" +
                "}\n" },
            new object[] { "PlainExtensionNoConformance",
                "extension Foo {}\n" },
            // `ConformanceExtensionRegex` is `extension\s+([\w.]+)\s*:\s*([\w.,\s]+)\s*\{` — the
            // brace must follow directly after the inheritance list with only whitespace, so
            // any `where` clause forces the regex to fail and the extension is NOT a marker
            // conformance.
            new object[] { "WhereClauseExcluded",
                "extension Foo : Proto where T : Bar { }\n" },
            // The capture groups `[\w.]+` reject `<`, so `extension Foo<T> : Proto { }` does
            // not match the marker-conformance regex.
            new object[] { "GenericTypeExcluded",
                "extension Foo<T> : Proto { }\n" },
        };

    /// <summary>Enum-case labels + raw values corpus.</summary>
    public static IEnumerable<object[]> EnumFactsCorpus =>
        new[]
        {
            new object[] { "BasicAssoc",
                "public enum Shape {\n" +
                "  case circle(radius: Double)\n" +
                "}\n" },
            new object[] { "UnlabeledAssoc",
                "public enum Item {\n" +
                "  case raw(Int)\n" +
                "}\n" },
            new object[] { "NoAssoc",
                "public enum Status {\n" +
                "  case ok\n" +
                "}\n" },
            new object[] { "RawString",
                "public enum HttpMethod : String {\n" +
                "  case get = \"GET\"\n" +
                "  case post = \"POST\"\n" +
                "}\n" },
            new object[] { "ExtensionFirstDot",
                "public enum Foo {}\n" +
                "extension Mod.Foo {\n" +
                "  case extended(label: Int)\n" +
                "}\n" },
            // `EnumCaseRegex.Match(line)` returns the FIRST `case <name>(` on the line, so
            // `case a, b(Int)` is anchored on `a` (no parens, no match) and emits NOTHING for
            // associated values — even though SwiftSyntax could see `b(Int)` independently.
            // Walker must mirror by inspecting only `elements.first`.
            new object[] { "GroupedFirstElementOnly",
                "public enum Mixed {\n" +
                "  case a, b(Int)\n" +
                "}\n" },
            // `EnumCaseRawValueRegex.Match(line)` is similarly first-only — `case a = \"A\", b = \"B\"`
            // emits a raw value entry only for `a`.
            new object[] { "GroupedRawValuesFirstOnly",
                "public enum Mixed : String {\n" +
                "  case a = \"A\", b = \"B\"\n" +
                "}\n" },
            // `public indirect enum Tree { case node(Tree) }` fails `TypeDeclRegex`'s strict
            // shape (`indirect` between access and `enum`), so the tracker does NOT push
            // `Tree` onto the scope stack. With no scope, the walker must skip the case
            // emission entirely (regex parser's `typeStack.Count > 0` guard).
            new object[] { "IndirectEnumNoCaseEmission",
                "public indirect enum Tree {\n" +
                "  case node(left: Tree, right: Tree)\n" +
                "}\n" },
        };

    /// <summary>Subscript-labels corpus.</summary>
    public static IEnumerable<object[]> SubscriptLabelsCorpus =>
        new[]
        {
            new object[] { "ExternalLabel",
                "public struct AES {\n" +
                "  public subscript(bitAt index: Swift.Int) -> Swift.Int { get }\n" +
                "}\n" },
            new object[] { "SingleNameNoLabel",
                "public struct Bag {\n" +
                "  public subscript(index: Swift.Int) -> Swift.Int { get }\n" +
                "}\n" },
            new object[] { "UnderscoreUnlabeled",
                "public struct Bag {\n" +
                "  public subscript(_ index: Swift.Int) -> Swift.Int { get }\n" +
                "}\n" },
            new object[] { "ExtensionFirstDotNesting",
                "public struct Outer { public struct Signing {} }\n" +
                "extension Mod.Outer.Signing {\n" +
                "  public subscript(bitAt index: Swift.Int) -> Swift.Int { get }\n" +
                "}\n" },
            // `public indirect enum` fails `TypeDeclRegex`'s strict shape, so the tracker
            // never pushes `E` onto the scope stack. Module-level subscripts are skipped
            // (`typeStack.Count > 0` guard), so this corpus entry must produce zero subscript
            // facts on both producers.
            new object[] { "IndirectEnumScopeNotPushed_NoSubscript",
                "public indirect enum E {\n" +
                "  public subscript(_ i: Swift.Int) -> Swift.Int { get }\n" +
                "}\n" },
        };

    /// <summary>Parameter-names + signature-fact corpus (defaults / autoclosure / variadic).</summary>
    public static IEnumerable<object[]> SignatureCorpus =>
        new[]
        {
            new object[] { "FreeFunctionLabels",
                "public func add(x: Swift.Int, y: Swift.Int) -> Swift.Int\n" },
            new object[] { "MethodWithDefault",
                "public class C {\n" +
                "  public func choose(_ first: Swift.Int = 1, second: Swift.Int = 2) -> Swift.Int\n" +
                "}\n" },
            new object[] { "AutoclosureFlag",
                "public class C {\n" +
                "  public func lazy(_ thunk: @autoclosure () -> Swift.Int) -> Swift.Int\n" +
                "}\n" },
            new object[] { "VariadicTrailing",
                "public class C {\n" +
                "  public func sum(_ values: Swift.Int...) -> Swift.Int\n" +
                "}\n" },
            new object[] { "ExtensionLastDotKey",
                "extension Mod.Outer.Target {\n" +
                "  public func emit(label x: Swift.Int) -> Swift.Int\n" +
                "}\n" },
            new object[] { "InternalParamNames_AnyAccess",
                "internal class Hidden {\n" +
                "  internal func helper(label internalName: Swift.Int)\n" +
                "}\n" },
            new object[] { "InitWithDefault",
                "public struct Reader {\n" +
                "  public init(buffer: Swift.String = \"\") \n" +
                "}\n" },
            // Nested-type parameterNames key uses ONLY the immediate parent (top-of-stack),
            // NOT the full nesting chain. Regex emits `KeyWrap.wrap(_:using:)` (typeStack.Peek()),
            // and the ABI consumer looks up by `parentDecl.Name + "." + printedName`. This
            // diverges from defaults/autoclosure/variadic, which use the full chain
            // (`AES.KeyWrap.wrap(_:using:)`). CryptoKit shape: `enum AES { struct KeyWrap { ... } }`
            // with `func wrap(_ keyToWrap: K, using kek: K)` — must yield internal name `kek`,
            // not the label `using`.
            new object[] { "NestedTypeParamNames_ImmediateParentKey",
                "public enum AES {\n" +
                "  public struct KeyWrap {\n" +
                "    public static func wrap(_ keyToWrap: Swift.Int, using kek: Swift.Int) -> Swift.Int\n" +
                "  }\n" +
                "}\n" },
        };

    /// <summary>Member-collection corpus: internal + public member keys.</summary>
    public static IEnumerable<object[]> MemberCollectionCorpus =>
        new[]
        {
            new object[] { "InternalFunc_PeekOnlyPrefix",
                "public class Outer {\n" +
                "  public class Inner {\n" +
                "    @inlinable internal func helper() {}\n" +
                "  }\n" +
                "}\n" },
            new object[] { "PublicFreeFunc_BareKey",
                "public func tlMain()\n" },
            new object[] { "PublicMethod_TypePrefix",
                "public struct S {\n" +
                "  public func ping() -> Swift.Int\n" +
                "}\n" },
            new object[] { "ExtensionLastDot",
                "extension CryptoSwift.AES {\n" +
                "  public func encrypt() -> Swift.Int\n" +
                "}\n" },
            new object[] { "BackticksStrippedFromVar",
                "public struct KeywordTest {\n" +
                "  public var `operator`: Swift.Int { get }\n" +
                "}\n" },
            new object[] { "InternalSetIsPublic",
                "public struct V {\n" +
                "  public internal(set) var counter: Swift.Int { get set }\n" +
                "}\n" },
            // Operator funcs (`==`, `+`, `<`, ...) — name token has `binaryOperator` /
            // `prefixOperator` / `postfixOperator` kind whose `.text` is the symbol literal.
            // Regex `(\w+)` capture rejects these; walker mirrors via Unicode word-class gate.
            new object[] { "OperatorFuncSkipped",
                "public struct V {\n" +
                "  public static func == (lhs: V, rhs: V) -> Swift.Bool\n" +
                "  public static func + (lhs: V, rhs: V) -> V\n" +
                "}\n" },
            // Backtick-escaped function name. `BroadPublicFuncRegex`/`AnyFuncRegex` capture is
            // bare `(\w+)`, no `\\?` wrapper, so a leading backtick character makes `\w` fail.
            // Walker matches by NOT stripping backticks before the identifier-name check.
            new object[] { "PublicBacktickFuncSkipped",
                "public struct V {\n" +
                "  public func `class`() -> Swift.Int\n" +
                "}\n" },
            // `BroadPublicVarRegex` accepts `\\?` (so it captures the inner word), but
            // `InternalVarRegex` is bare `(\w+)` with no backtick wrapper. So `internal var
            // \\`class\\`: Int` does NOT contribute to internalMemberKeys. Walker must
            // suppress the internal emission for backtick-escaped var names.
            new object[] { "InternalBacktickVarSkipped",
                "public struct V {\n" +
                "  @inlinable internal var `class`: Swift.Int { get }\n" +
                "}\n" },
            // `public indirect enum` fails the gated scope push, so members inside its body
            // are keyed at MODULE scope. `walk()` therefore emits as `walk()` with no
            // `Tree.` prefix in publicMemberNames — matching the regex tracker which never
            // pushed `Tree` onto its scope stack.
            new object[] { "IndirectEnumMembersFreeKeyed",
                "public indirect enum Tree {\n" +
                "  public func walk() -> Swift.Int\n" +
                "}\n" },
        };

    /// <summary>Protocol-level facts corpus: convention(c) + hidden requirements.</summary>
    public static IEnumerable<object[]> ProtocolFactsCorpus =>
        new[]
        {
            new object[] { "DirectConventionC",
                "// swift-module-flags: -module-name Mod\n" +
                "public protocol HasCallback {\n" +
                "  func install(_ cb: @convention(c) () -> Void)\n" +
                "}\n" },
            new object[] { "AliasResolvesConvention",
                "// swift-module-flags: -module-name Mod\n" +
                "public typealias FTS5TokenCallback = @convention(c) (Swift.Int) -> Void\n" +
                "public protocol UsesAlias {\n" +
                "  func install(_ cb: FTS5TokenCallback)\n" +
                "}\n" },
            new object[] { "BareProtocolNoConvention",
                "// swift-module-flags: -module-name Mod\n" +
                "public protocol Plain {\n" +
                "  func tick()\n" +
                "}\n" },
            new object[] { "HiddenRequirementUnsatisfied",
                "// swift-module-flags: -module-name Mod\n" +
                "public protocol HasSecret {\n" +
                "  var __secret: Swift.Int { get }\n" +
                "}\n" },
            new object[] { "HiddenRequirementSatisfied",
                "// swift-module-flags: -module-name Mod\n" +
                "public protocol HasSecret {\n" +
                "  var __secret: Swift.Int { get }\n" +
                "}\n" +
                "extension HasSecret {\n" +
                "  public var __secret: Swift.Int { 0 }\n" +
                "}\n" },
            // The regex's `ConventionTypeAliasRegex` scans EVERY line in the file, including
            // typealiases inside nested types. The walker's previous `tree.statements`-only
            // scan missed nested aliases; now uses a recursive `ConventionAliasCollector`.
            new object[] { "NestedConventionAlias",
                "// swift-module-flags: -module-name Mod\n" +
                "public enum Outer {\n" +
                "  public typealias Callback = @convention(c) (Swift.Int) -> Void\n" +
                "}\n" +
                "public protocol UsesNested {\n" +
                "  func install(_ cb: Outer.Callback)\n" +
                "}\n" },
        };

    /// <summary>Protocol-names corpus (M2 S4). Mirrors `ProtocolDeclRegex` shape:
    /// <c>(?:public|open)\s+protocol\s+(\w+)</c>. Internal protocols, modifier-prefixed
    /// protocols, and backtick-escaped names are excluded by the regex; SwiftSyntax must
    /// match.</summary>
    public static IEnumerable<object[]> ProtocolNamesCorpus =>
        new[]
        {
            new object[] { "PublicProtocol",
                "public protocol Foo {}\n" },
            new object[] { "OpenProtocol",
                "open protocol Bar {}\n" },
            new object[] { "InternalProtocolExcluded",
                "internal protocol Hidden {}\n" },
            new object[] { "MultipleProtocols",
                "public protocol A {}\n" +
                "open protocol B {}\n" +
                "internal protocol C {}\n" },
            new object[] { "NestedPublicProtocol",
                "public class Outer {\n" +
                "  public protocol Inner {}\n" +
                "}\n" },
            // Backtick-escaped names fail the regex's `(\w+)` capture (Unicode word class).
            new object[] { "BacktickedNameExcluded",
                "public protocol `class` {}\n" },
            // The regex's `(?:public|open)\s+protocol` shape rejects any modifier between
            // the access keyword and `protocol`. Note that `public final protocol` is
            // semantically illegal in Swift but exercises the modifier-shape gate.
            new object[] { "ModifierBeforeProtocolRejectedShape",
                "@available(iOS 17.0, *)\n" +
                "public protocol Available {}\n" },
        };

    /// <summary>Extension-member-candidate corpus (M2 S4). Exercises every shape the
    /// regex producer's `GetExtensionMemberCandidates` walker fires on, plus shapes it
    /// deliberately rejects.</summary>
    public static IEnumerable<object[]> ExtensionMemberCandidatesCorpus =>
        new[]
        {
            new object[] { "PublicFunc",
                "extension Mod.Type {\n" +
                "  public func ping() -> Swift.Int\n" +
                "}\n" },
            new object[] { "PublicVar_GetSet",
                "extension Mod.Type {\n" +
                "  public var counter: Swift.Int { get set }\n" +
                "}\n" },
            new object[] { "PublicVar_GetOnly",
                "extension Mod.Type {\n" +
                "  public var label: Swift.String { get }\n" +
                "}\n" },
            // `nonmutating set` flips HasSetter (regex matches `nonmutating set` line).
            new object[] { "NonmutatingSet",
                "extension Mod.Type {\n" +
                "  public var name: Swift.String {\n" +
                "    get\n" +
                "    nonmutating set\n" +
                "  }\n" +
                "}\n" },
            // Mixed multi-line accessor: tokens follow the opening `{` on the same source line,
            // with `set` on a later line. Regex captures the full first trimmed line
            // (`public var x: Swift.Int { get`); SwiftSyntax must clip
            // `accessorBlock.description` at the first newline rather than dropping the
            // whole body and re-stamping just `{`.
            new object[] { "MixedMultilineAccessor",
                "extension Mod.Type {\n" +
                "  public var x: Swift.Int { get\n" +
                "    set\n" +
                "  }\n" +
                "}\n" },
            new object[] { "StaticFunc",
                "extension Mod.Type {\n" +
                "  public static func make() -> Mod.Type\n" +
                "}\n" },
            new object[] { "MutatingFunc",
                "extension Mod.Type {\n" +
                "  public mutating func clear()\n" +
                "}\n" },
            // `@available(*, deprecated, ...)` flips IsDeprecated (regex matches the same-line attr).
            new object[] { "DeprecatedFunc",
                "extension Mod.Type {\n" +
                "  @available(*, deprecated, message: \"use newAPI\")\n" +
                "  public func legacy()\n" +
                "}\n" },
            // Generic method `func foo<T>(...)` — RawSignature must contain "func foo<".
            new object[] { "GenericFunc",
                "extension Mod.Type {\n" +
                "  public func transform<T>(_ value: T) -> T\n" +
                "}\n" },
            // Where-constraint on the extension flows into WhereConstraints.
            new object[] { "ExtensionWhereConstraint",
                "extension Mod.Type where Self : SomeMod.Bound {\n" +
                "  public func conditional()\n" +
                "}\n" },
            // Multi-component where with two constraints.
            new object[] { "ExtensionWhereTwoConstraints",
                "extension Mod.Type where Self : SomeMod.A, Self : SomeMod.B {\n" +
                "  public func twoBound()\n" +
                "}\n" },
            // Throws / typed-throws / async — signature substrings drive consumer behavior.
            new object[] { "ThrowingFunc",
                "extension Mod.Type {\n" +
                "  public func unsafe() throws -> Swift.Int\n" +
                "}\n" },
            new object[] { "TypedThrowsFunc",
                "extension Mod.Type {\n" +
                "  public func parse() throws(Mod.E) -> Swift.Int\n" +
                "}\n" },
            new object[] { "AsyncFunc",
                "extension Mod.Type {\n" +
                "  public func waitFor() async -> Swift.Int\n" +
                "}\n" },
            // Returns Self — DetectSelfReturn parity (regex's `EndsWith("-> Self")`).
            new object[] { "ReturnsSelf",
                "extension Mod.Type {\n" +
                "  public func clone() -> Self\n" +
                "}\n" },
            // Nested type members in extension are NOT direct — must be skipped.
            new object[] { "NestedTypeMembersExcluded",
                "extension Mod.Type {\n" +
                "  public func direct()\n" +
                "  public struct Nested {\n" +
                "    public func nestedMember()\n" +
                "  }\n" +
                "}\n" },
            // `let` is excluded — `ExtensionVarRegex` uses literal `var`.
            new object[] { "LetExcluded",
                "extension Mod.Type {\n" +
                "  public let constant: Swift.Int = 0\n" +
                "}\n" },
            // `init`, `subscript` are not collected by the candidate walker.
            new object[] { "InitAndSubscriptExcluded",
                "extension Mod.Type {\n" +
                "  public init()\n" +
                "  public subscript(i: Swift.Int) -> Swift.Int { get }\n" +
                "}\n" },
            // Operator funcs fail `(\w+)` capture.
            new object[] { "OperatorFuncExcluded",
                "extension Mod.Type {\n" +
                "  public static func == (lhs: Mod.Type, rhs: Mod.Type) -> Swift.Bool\n" +
                "}\n" },
            // Backtick-escaped property name fails `(\w+)` capture (regex parser path).
            new object[] { "BacktickedVarExcluded",
                "extension Mod.Type {\n" +
                "  public var `class`: Swift.Int { get }\n" +
                "}\n" },
            // Multiple extensions of the same type — both contribute candidates in source order.
            new object[] { "MultipleExtensionsSameType",
                "extension Mod.Type {\n" +
                "  public func first()\n" +
                "}\n" +
                "extension Mod.Type {\n" +
                "  public func second()\n" +
                "}\n" },
            // Unqualified extension target — partitioning happens .NET-side, but the candidate
            // is captured verbatim (no module-prefix transformation).
            new object[] { "UnqualifiedExtensionTarget",
                "extension Type {\n" +
                "  public func bare()\n" +
                "}\n" },
            // `#if`/`#endif` lines are skipped — both producers must collect the same members.
            new object[] { "IfBranchSkipped",
                "extension Mod.Type {\n" +
                "  #if SOMETHING\n" +
                "  public func conditional()\n" +
                "  #endif\n" +
                "  public func always()\n" +
                "}\n" },
        };

    /// <summary>ProtocolExtensionMethods derivation corpus (M2 S4). Verifies the dictionary
    /// derived from `ExtensionMemberCandidates` + `ProtocolNames` parity-matches between
    /// producers (both producers route through the same first-dot-stripped lookup).</summary>
    public static IEnumerable<object[]> ProtocolExtensionMethodsCorpus =>
        new[]
        {
            // Qualified extension target on a same-file public protocol — first dot strips
            // the module prefix, so `Mod.MyProto` resolves to `MyProto` ∈ ProtocolNames.
            new object[] { "QualifiedExtensionTargetsProtocol",
                "public protocol MyProto {}\n" +
                "extension Mod.MyProto {\n" +
                "  public func defaulted()\n" +
                "}\n" },
            // Unqualified extension target on a same-file public protocol.
            new object[] { "UnqualifiedExtensionTargetsProtocol",
                "public protocol MyProto {}\n" +
                "extension MyProto {\n" +
                "  public func defaulted()\n" +
                "}\n" },
            // Extension on a non-protocol — should NOT appear in ProtocolExtensionMethods.
            new object[] { "ExtensionOnTypeNotProtocol",
                "public protocol MyProto {}\n" +
                "extension Mod.SomeType {\n" +
                "  public func added()\n" +
                "}\n" },
            // Mixed: one protocol extension and one type extension. Only the protocol one appears.
            new object[] { "MixedProtocolAndType",
                "public protocol MyProto {}\n" +
                "extension Mod.MyProto {\n" +
                "  public func protoMember()\n" +
                "}\n" +
                "extension Mod.OtherType {\n" +
                "  public func typeMember()\n" +
                "}\n" },
            // No protocols — protocolNames is empty, so no derivations even from extensions.
            new object[] { "NoProtocols",
                "extension Mod.OnlyType {\n" +
                "  public func only()\n" +
                "}\n" },
            // Two members on the same protocol-target extension — list ordering preserved.
            new object[] { "MultipleMembersSameProtocol",
                "public protocol MyProto {}\n" +
                "extension Mod.MyProto {\n" +
                "  public func first()\n" +
                "  public func second()\n" +
                "}\n" },
        };

    [SkippableTheory]
    [MemberData(nameof(MainActorCorpus))]
    public void RegexAndSwiftSyntaxProducers_ProduceIdenticalMainActorFacts(string label, string swiftInterface)
    {
        var binaryPath = ResolveBinaryOrSkip(label);
        var path = WriteTempFile(swiftInterface);
        try
        {
            var regex = new RegexInterfaceFactsProducer().Produce(path, NullLogger.Instance);
            var swiftSyntax = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);

            // Coverage: SwiftSyntax declares MainActorTypes + MainActorTypePositions in Session 1+.
            Assert.Contains(InterfaceFactKind.MainActorTypes, swiftSyntax.CoveredFacts);
            Assert.Contains(InterfaceFactKind.MainActorTypePositions, swiftSyntax.CoveredFacts);

            AssertSetParity(label, "MainActorTypes", regex.Facts.MainActorTypes, swiftSyntax.Facts.MainActorTypes);
            AssertPositionsParity(label, "MainActorTypePositions",
                regex.Facts.MainActorTypePositions, swiftSyntax.Facts.MainActorTypePositions);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(ActorIsolationCorpus))]
    public void RegexAndSwiftSyntaxProducers_ProduceIdenticalActorIsolationFacts(string label, string swiftInterface)
    {
        var binaryPath = ResolveBinaryOrSkip(label);
        var path = WriteTempFile(swiftInterface);
        try
        {
            var regex = new RegexInterfaceFactsProducer().Produce(path, NullLogger.Instance);
            var swiftSyntax = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);

            Assert.Contains(InterfaceFactKind.ActorIsolatedMembers, swiftSyntax.CoveredFacts);
            Assert.Contains(InterfaceFactKind.MainActorIsolatedMembers, swiftSyntax.CoveredFacts);
            Assert.Contains(InterfaceFactKind.NonisolatedMembers, swiftSyntax.CoveredFacts);
            Assert.Contains(InterfaceFactKind.CustomActorTypes, swiftSyntax.CoveredFacts);
            Assert.Contains(InterfaceFactKind.CustomActorIsolatorMap, swiftSyntax.CoveredFacts);

            AssertSetParity(label, "ActorIsolatedMembers",
                regex.Facts.ActorIsolatedMembers, swiftSyntax.Facts.ActorIsolatedMembers);
            AssertSetParity(label, "MainActorIsolatedMembers",
                regex.Facts.MainActorIsolatedMembers, swiftSyntax.Facts.MainActorIsolatedMembers);
            AssertSetParity(label, "NonisolatedMembers",
                regex.Facts.NonisolatedMembers, swiftSyntax.Facts.NonisolatedMembers);
            AssertSetParity(label, "CustomActorTypes",
                regex.Facts.CustomActorTypes, swiftSyntax.Facts.CustomActorTypes);
            AssertStringDictParity(label, "CustomActorIsolatorMap",
                regex.Facts.CustomActorIsolatorMap, swiftSyntax.Facts.CustomActorIsolatorMap);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(AvailabilityCorpus))]
    public void RegexAndSwiftSyntaxProducers_ProduceIdenticalAvailabilityFacts(string label, string swiftInterface)
    {
        var binaryPath = ResolveBinaryOrSkip(label);
        var path = WriteTempFile(swiftInterface);
        try
        {
            var regex = new RegexInterfaceFactsProducer().Produce(path, NullLogger.Instance);
            var swiftSyntax = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);

            Assert.Contains(InterfaceFactKind.AvailabilityAnnotations, swiftSyntax.CoveredFacts);
            Assert.Contains(InterfaceFactKind.AvailabilityAnnotationPositions, swiftSyntax.CoveredFacts);

            AssertAvailabilityParity(label,
                regex.Facts.AvailabilityAnnotations, swiftSyntax.Facts.AvailabilityAnnotations);
            AssertPositionsParity(label, "AvailabilityAnnotationPositions",
                regex.Facts.AvailabilityAnnotationPositions, swiftSyntax.Facts.AvailabilityAnnotationPositions);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(TypedThrowsCorpus))]
    public void RegexAndSwiftSyntaxProducers_ProduceIdenticalTypedThrowsFacts(string label, string swiftInterface)
    {
        var binaryPath = ResolveBinaryOrSkip(label);
        var path = WriteTempFile(swiftInterface);
        try
        {
            var regex = new RegexInterfaceFactsProducer().Produce(path, NullLogger.Instance);
            var swiftSyntax = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);

            Assert.Contains(InterfaceFactKind.TypedThrowsErrors, swiftSyntax.CoveredFacts);

            AssertStringDictParity(label, "TypedThrowsErrors",
                regex.Facts.TypedThrowsErrors, swiftSyntax.Facts.TypedThrowsErrors);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableFact]
    public void SwiftSyntaxProducer_NonexistentInputFile_ReturnsEmptyAndZeroCoverage()
    {
        var binaryPath = ResolveBinaryOrSkip("NoFileGuard");
        var bogus = "/tmp/nonexistent-" + Guid.NewGuid() + ".swiftinterface";
        var result = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(bogus, NullLogger.Instance);
        Assert.Empty(result.CoveredFacts);
        Assert.Null(result.Facts.MainActorTypes);
        Assert.Null(result.Facts.MainActorTypePositions);
        Assert.Null(result.Facts.ActorIsolatedMembers);
        Assert.Null(result.Facts.AvailabilityAnnotations);
        Assert.Null(result.Facts.TypedThrowsErrors);
    }

    [SkippableTheory]
    [MemberData(nameof(PublicTypeNamesCorpus))]
    public void RegexAndSwiftSyntaxProducers_ProduceIdenticalPublicTypeNames(string label, string swiftInterface)
    {
        var binaryPath = ResolveBinaryOrSkip(label);
        var path = WriteTempFile(swiftInterface);
        try
        {
            var regex = new RegexInterfaceFactsProducer().Produce(path, NullLogger.Instance);
            var swiftSyntax = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);

            Assert.Contains(InterfaceFactKind.PublicTypeNames, swiftSyntax.CoveredFacts);
            AssertSetParity(label, "PublicTypeNames",
                regex.Facts.PublicTypeNames, swiftSyntax.Facts.PublicTypeNames);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(MarkerProtocolCorpus))]
    public void RegexAndSwiftSyntaxProducers_ProduceIdenticalMarkerProtocolConformances(string label, string swiftInterface)
    {
        var binaryPath = ResolveBinaryOrSkip(label);
        var path = WriteTempFile(swiftInterface);
        try
        {
            var regex = new RegexInterfaceFactsProducer().Produce(path, NullLogger.Instance);
            var swiftSyntax = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);

            Assert.Contains(InterfaceFactKind.MarkerProtocolConformances, swiftSyntax.CoveredFacts);
            AssertStringListDictParity(label, "MarkerProtocolConformances",
                regex.Facts.MarkerProtocolConformances, swiftSyntax.Facts.MarkerProtocolConformances);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(EnumFactsCorpus))]
    public void RegexAndSwiftSyntaxProducers_ProduceIdenticalEnumFacts(string label, string swiftInterface)
    {
        var binaryPath = ResolveBinaryOrSkip(label);
        var path = WriteTempFile(swiftInterface);
        try
        {
            var regex = new RegexInterfaceFactsProducer().Produce(path, NullLogger.Instance);
            var swiftSyntax = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);

            Assert.Contains(InterfaceFactKind.EnumCaseLabels, swiftSyntax.CoveredFacts);
            Assert.Contains(InterfaceFactKind.EnumCaseRawValues, swiftSyntax.CoveredFacts);
            AssertNullableStringListDictParity(label, "EnumCaseLabels",
                regex.Facts.EnumCaseLabels, swiftSyntax.Facts.EnumCaseLabels);
            AssertStringDictParity(label, "EnumCaseRawValues",
                regex.Facts.EnumCaseRawValues, swiftSyntax.Facts.EnumCaseRawValues);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(SubscriptLabelsCorpus))]
    public void RegexAndSwiftSyntaxProducers_ProduceIdenticalSubscriptLabels(string label, string swiftInterface)
    {
        var binaryPath = ResolveBinaryOrSkip(label);
        var path = WriteTempFile(swiftInterface);
        try
        {
            var regex = new RegexInterfaceFactsProducer().Produce(path, NullLogger.Instance);
            var swiftSyntax = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);

            Assert.Contains(InterfaceFactKind.SubscriptLabels, swiftSyntax.CoveredFacts);
            AssertStringListDictParity(label, "SubscriptLabels",
                regex.Facts.SubscriptLabels, swiftSyntax.Facts.SubscriptLabels);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(SignatureCorpus))]
    public void RegexAndSwiftSyntaxProducers_ProduceIdenticalSignatureFacts(string label, string swiftInterface)
    {
        var binaryPath = ResolveBinaryOrSkip(label);
        var path = WriteTempFile(swiftInterface);
        try
        {
            var regex = new RegexInterfaceFactsProducer().Produce(path, NullLogger.Instance);
            var swiftSyntax = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);

            Assert.Contains(InterfaceFactKind.ParameterNames, swiftSyntax.CoveredFacts);
            Assert.Contains(InterfaceFactKind.DefaultParameterValues, swiftSyntax.CoveredFacts);
            Assert.Contains(InterfaceFactKind.AutoclosureParameters, swiftSyntax.CoveredFacts);
            Assert.Contains(InterfaceFactKind.VariadicMembers, swiftSyntax.CoveredFacts);

            AssertStringListDictParity(label, "ParameterNames",
                regex.Facts.ParameterNames, swiftSyntax.Facts.ParameterNames);
            AssertNullableStringListDictParity(label, "DefaultParameterValues",
                regex.Facts.DefaultParameterValues, swiftSyntax.Facts.DefaultParameterValues);
            AssertBoolListDictParity(label, "AutoclosureParameters",
                regex.Facts.AutoclosureParameters, swiftSyntax.Facts.AutoclosureParameters);
            AssertSetParity(label, "VariadicMembers",
                regex.Facts.VariadicMembers, swiftSyntax.Facts.VariadicMembers);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(MemberCollectionCorpus))]
    public void RegexAndSwiftSyntaxProducers_ProduceIdenticalMemberCollection(string label, string swiftInterface)
    {
        var binaryPath = ResolveBinaryOrSkip(label);
        var path = WriteTempFile(swiftInterface);
        try
        {
            var regex = new RegexInterfaceFactsProducer().Produce(path, NullLogger.Instance);
            var swiftSyntax = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);

            Assert.Contains(InterfaceFactKind.InternalMemberKeys, swiftSyntax.CoveredFacts);
            Assert.Contains(InterfaceFactKind.PublicMemberNames, swiftSyntax.CoveredFacts);

            AssertSetParity(label, "InternalMemberKeys",
                regex.Facts.InternalMemberKeys, swiftSyntax.Facts.InternalMemberKeys);
            AssertSetParity(label, "PublicMemberNames",
                regex.Facts.PublicMemberNames, swiftSyntax.Facts.PublicMemberNames);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(ProtocolFactsCorpus))]
    public void RegexAndSwiftSyntaxProducers_ProduceIdenticalProtocolFacts(string label, string swiftInterface)
    {
        var binaryPath = ResolveBinaryOrSkip(label);
        var path = WriteTempFile(swiftInterface);
        try
        {
            var regex = new RegexInterfaceFactsProducer().Produce(path, NullLogger.Instance);
            var swiftSyntax = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);

            Assert.Contains(InterfaceFactKind.ConventionCProtocols, swiftSyntax.CoveredFacts);
            Assert.Contains(InterfaceFactKind.ConventionCProtocolPositions, swiftSyntax.CoveredFacts);
            Assert.Contains(InterfaceFactKind.HiddenRequirementProtocols, swiftSyntax.CoveredFacts);

            AssertSetParity(label, "ConventionCProtocols",
                regex.Facts.ConventionCProtocols, swiftSyntax.Facts.ConventionCProtocols);
            AssertPositionsParity(label, "ConventionCProtocolPositions",
                regex.Facts.ConventionCProtocolPositions, swiftSyntax.Facts.ConventionCProtocolPositions);
            AssertHiddenRequirementParity(label,
                regex.Facts.HiddenRequirementProtocols, swiftSyntax.Facts.HiddenRequirementProtocols);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(ProtocolNamesCorpus))]
    public void RegexAndSwiftSyntaxProducers_ProduceIdenticalProtocolNames(string label, string swiftInterface)
    {
        var binaryPath = ResolveBinaryOrSkip(label);
        var path = WriteTempFile(swiftInterface);
        try
        {
            var regex = new RegexInterfaceFactsProducer().Produce(path, NullLogger.Instance);
            var swiftSyntax = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);

            Assert.Contains(InterfaceFactKind.ProtocolNames, swiftSyntax.CoveredFacts);
            AssertSetParity(label, "ProtocolNames",
                regex.Facts.ProtocolNames, swiftSyntax.Facts.ProtocolNames);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(ExtensionMemberCandidatesCorpus))]
    public void RegexAndSwiftSyntaxProducers_ProduceIdenticalExtensionMemberCandidates(string label, string swiftInterface)
    {
        var binaryPath = ResolveBinaryOrSkip(label);
        var path = WriteTempFile(swiftInterface);
        try
        {
            var regex = new RegexInterfaceFactsProducer().Produce(path, NullLogger.Instance);
            var swiftSyntax = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);

            Assert.Contains(InterfaceFactKind.ExtensionMemberCandidates, swiftSyntax.CoveredFacts);
            AssertExtensionCandidatesParity(label,
                regex.Facts.ExtensionMemberCandidates, swiftSyntax.Facts.ExtensionMemberCandidates);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(ProtocolExtensionMethodsCorpus))]
    public void RegexAndSwiftSyntaxProducers_ProduceIdenticalProtocolExtensionMethods(string label, string swiftInterface)
    {
        var binaryPath = ResolveBinaryOrSkip(label);
        var path = WriteTempFile(swiftInterface);
        try
        {
            var regex = new RegexInterfaceFactsProducer().Produce(path, NullLogger.Instance);
            var swiftSyntax = new SwiftSyntaxInterfaceFactsProducer(binaryPath).Produce(path, NullLogger.Instance);

            Assert.Contains(InterfaceFactKind.ProtocolExtensionMethods, swiftSyntax.CoveredFacts);
            AssertProtocolExtensionMethodsParity(label,
                regex.Facts.ProtocolExtensionMethods, swiftSyntax.Facts.ProtocolExtensionMethods);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableFact]
    public void Aggregator_WithSwiftSyntaxThenRegex_RoutesMigratedFactsToSwiftSyntax()
    {
        // End-to-end seam test: the aggregator must merge per fact. With
        // [SwiftSyntax, Regex], all facts SwiftSyntax declares come from SwiftSyntax;
        // facts SwiftSyntax does NOT cover (e.g., PublicTypeNames) fall through to Regex.
        var binaryPath = ResolveBinaryOrSkip("AggregatorRouting");
        var swiftInterface =
            "import Swift\n" +
            "\n" +
            "@MainActor\n" +
            "public struct A {\n" +
            "}\n" +
            "\n" +
            "public struct B {\n" +
            "}\n" +
            "\n" +
            "public func parse(_ s: Swift.String) throws(MyMod.E) -> Swift.Int\n";

        var path = WriteTempFile(swiftInterface);
        try
        {
            var aggregator = new InterfaceFactsAggregator(new IInterfaceFactsProducer[]
            {
                new SwiftSyntaxInterfaceFactsProducer(binaryPath),
                new RegexInterfaceFactsProducer(),
            });
            var facts = aggregator.Aggregate(path, NullLogger.Instance);

            // SwiftSyntax-covered facts.
            Assert.Contains("A", facts.MainActorTypes);
            Assert.DoesNotContain("B", facts.MainActorTypes);
            Assert.True(facts.MainActorTypePositions.ContainsKey("A"));
            Assert.True(facts.TypedThrowsErrors.ContainsKey("parse(_:)"));
            Assert.Equal("MyMod.E", facts.TypedThrowsErrors["parse(_:)"]);

            // Regex-covered facts (SwiftSyntax doesn't cover): PublicTypeNames sees both.
            Assert.Contains("A", facts.PublicTypeNames);
            Assert.Contains("B", facts.PublicTypeNames);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertSetParity(
        string label, string factName,
        HashSet<string>? regexSet, HashSet<string>? swiftSyntaxSet)
    {
        Assert.NotNull(regexSet);
        Assert.NotNull(swiftSyntaxSet);
        Assert.True(regexSet!.SetEquals(swiftSyntaxSet!),
            $"[{label}] {factName} diverged.\n  regex:        {Join(regexSet)}\n  swift-syntax: {Join(swiftSyntaxSet)}");
    }

    private static void AssertStringDictParity(
        string label, string factName,
        Dictionary<string, string>? regexDict, Dictionary<string, string>? swiftSyntaxDict)
    {
        Assert.NotNull(regexDict);
        Assert.NotNull(swiftSyntaxDict);
        Assert.True(regexDict!.Count == swiftSyntaxDict!.Count,
            $"[{label}] {factName} count diverged. regex={regexDict.Count} swift-syntax={swiftSyntaxDict.Count}");
        foreach (var key in regexDict.Keys)
        {
            Assert.True(swiftSyntaxDict.ContainsKey(key),
                $"[{label}] {factName}: swift-syntax missing key '{key}'");
            Assert.True(regexDict[key] == swiftSyntaxDict[key],
                $"[{label}] {factName}['{key}'] diverged. regex='{regexDict[key]}' swift-syntax='{swiftSyntaxDict[key]}'");
        }
    }

    private static void AssertPositionsParity(
        string label, string factName,
        Dictionary<string, SourcePosition>? regexPositions,
        Dictionary<string, SourcePosition>? swiftSyntaxPositions)
    {
        Assert.NotNull(regexPositions);
        Assert.NotNull(swiftSyntaxPositions);
        Assert.True(regexPositions!.Count == swiftSyntaxPositions!.Count,
            $"[{label}] {factName} count diverged. regex={regexPositions.Count} swift-syntax={swiftSyntaxPositions.Count}\n  regex keys: {Join(regexPositions.Keys)}\n  swift keys: {Join(swiftSyntaxPositions.Keys)}");
        foreach (var key in regexPositions.Keys)
        {
            Assert.True(swiftSyntaxPositions.ContainsKey(key),
                $"[{label}] {factName}: swift-syntax missing position for '{key}'");
            var r = regexPositions[key];
            var s = swiftSyntaxPositions[key];
            Assert.True(r.FilePath == s.FilePath,
                $"[{label}] {factName}['{key}'] FilePath diverged.");
            Assert.True(r.Line == s.Line,
                $"[{label}] {factName}['{key}'] Line diverged. regex={r.Line} swift-syntax={s.Line}");
            Assert.True(r.Column == s.Column,
                $"[{label}] {factName}['{key}'] Column diverged. regex={r.Column} swift-syntax={s.Column}");
        }
    }

    private static void AssertStringListDictParity(
        string label, string factName,
        Dictionary<string, List<string>>? regexDict, Dictionary<string, List<string>>? swiftSyntaxDict)
    {
        Assert.NotNull(regexDict);
        Assert.NotNull(swiftSyntaxDict);
        Assert.True(regexDict!.Count == swiftSyntaxDict!.Count,
            $"[{label}] {factName} count diverged. regex={regexDict.Count} swift-syntax={swiftSyntaxDict.Count}\n  regex keys: {Join(regexDict.Keys)}\n  swift keys: {Join(swiftSyntaxDict.Keys)}");
        foreach (var key in regexDict.Keys)
        {
            Assert.True(swiftSyntaxDict.ContainsKey(key),
                $"[{label}] {factName}: swift-syntax missing key '{key}'");
            var r = regexDict[key];
            var s = swiftSyntaxDict[key];
            Assert.True(r.Count == s.Count,
                $"[{label}] {factName}['{key}'] length diverged. regex={r.Count} swift-syntax={s.Count}\n  regex: {Join(r)}\n  swift: {Join(s)}");
            for (int i = 0; i < r.Count; i++)
            {
                Assert.True(r[i] == s[i],
                    $"[{label}] {factName}['{key}'][{i}] diverged. regex='{r[i]}' swift-syntax='{s[i]}'");
            }
        }
    }

    private static void AssertNullableStringListDictParity(
        string label, string factName,
        Dictionary<string, List<string?>>? regexDict, Dictionary<string, List<string?>>? swiftSyntaxDict)
    {
        Assert.NotNull(regexDict);
        Assert.NotNull(swiftSyntaxDict);
        Assert.True(regexDict!.Count == swiftSyntaxDict!.Count,
            $"[{label}] {factName} count diverged. regex={regexDict.Count} swift-syntax={swiftSyntaxDict.Count}\n  regex keys: {Join(regexDict.Keys)}\n  swift keys: {Join(swiftSyntaxDict.Keys)}");
        foreach (var key in regexDict.Keys)
        {
            Assert.True(swiftSyntaxDict.ContainsKey(key),
                $"[{label}] {factName}: swift-syntax missing key '{key}'");
            var r = regexDict[key];
            var s = swiftSyntaxDict[key];
            Assert.True(r.Count == s.Count,
                $"[{label}] {factName}['{key}'] length diverged. regex={r.Count} swift-syntax={s.Count}");
            for (int i = 0; i < r.Count; i++)
            {
                Assert.True(r[i] == s[i],
                    $"[{label}] {factName}['{key}'][{i}] diverged. regex='{r[i] ?? "<null>"}' swift-syntax='{s[i] ?? "<null>"}'");
            }
        }
    }

    private static void AssertBoolListDictParity(
        string label, string factName,
        Dictionary<string, List<bool>>? regexDict, Dictionary<string, List<bool>>? swiftSyntaxDict)
    {
        Assert.NotNull(regexDict);
        Assert.NotNull(swiftSyntaxDict);
        Assert.True(regexDict!.Count == swiftSyntaxDict!.Count,
            $"[{label}] {factName} count diverged. regex={regexDict.Count} swift-syntax={swiftSyntaxDict.Count}");
        foreach (var key in regexDict.Keys)
        {
            Assert.True(swiftSyntaxDict.ContainsKey(key),
                $"[{label}] {factName}: swift-syntax missing key '{key}'");
            var r = regexDict[key];
            var s = swiftSyntaxDict[key];
            Assert.True(r.Count == s.Count,
                $"[{label}] {factName}['{key}'] length diverged. regex={r.Count} swift-syntax={s.Count}");
            for (int i = 0; i < r.Count; i++)
            {
                Assert.True(r[i] == s[i],
                    $"[{label}] {factName}['{key}'][{i}] diverged. regex={r[i]} swift-syntax={s[i]}");
            }
        }
    }

    private static void AssertHiddenRequirementParity(
        string label,
        Dictionary<string, HashSet<string>>? regexDict,
        Dictionary<string, HashSet<string>>? swiftSyntaxDict)
    {
        Assert.NotNull(regexDict);
        Assert.NotNull(swiftSyntaxDict);
        Assert.True(regexDict!.Count == swiftSyntaxDict!.Count,
            $"[{label}] HiddenRequirementProtocols count diverged. regex={regexDict.Count} swift-syntax={swiftSyntaxDict.Count}\n  regex keys: {Join(regexDict.Keys)}\n  swift keys: {Join(swiftSyntaxDict.Keys)}");
        foreach (var key in regexDict.Keys)
        {
            Assert.True(swiftSyntaxDict.ContainsKey(key),
                $"[{label}] HiddenRequirementProtocols: swift-syntax missing key '{key}'");
            var r = regexDict[key];
            var s = swiftSyntaxDict[key];
            Assert.True(r.SetEquals(s),
                $"[{label}] HiddenRequirementProtocols['{key}'] diverged.\n  regex:        {Join(r)}\n  swift-syntax: {Join(s)}");
        }
    }

    private static void AssertAvailabilityParity(
        string label,
        Dictionary<string, List<AvailabilityAnnotation>>? regexDict,
        Dictionary<string, List<AvailabilityAnnotation>>? swiftSyntaxDict)
    {
        Assert.NotNull(regexDict);
        Assert.NotNull(swiftSyntaxDict);
        Assert.True(regexDict!.Count == swiftSyntaxDict!.Count,
            $"[{label}] AvailabilityAnnotations count diverged. regex={regexDict.Count} swift-syntax={swiftSyntaxDict.Count}\n  regex keys: {Join(regexDict.Keys)}\n  swift keys: {Join(swiftSyntaxDict.Keys)}");
        foreach (var key in regexDict.Keys)
        {
            Assert.True(swiftSyntaxDict.ContainsKey(key),
                $"[{label}] AvailabilityAnnotations: swift-syntax missing key '{key}'");
            var r = regexDict[key];
            var s = swiftSyntaxDict[key];
            Assert.True(r.Count == s.Count,
                $"[{label}] AvailabilityAnnotations['{key}'] count diverged. regex={r.Count} swift-syntax={s.Count}");
            for (int i = 0; i < r.Count; i++)
            {
                Assert.True(r[i] == s[i],
                    $"[{label}] AvailabilityAnnotations['{key}'][{i}] diverged.\n  regex:        {r[i]}\n  swift-syntax: {s[i]}");
            }
        }
    }

    /// <summary>Compares two flat candidate lists element-wise. Every field is asserted
    /// byte-equal — the SwiftSyntax walker filters attributes to the access-modifier
    /// source line so its <c>RawSignature</c> matches the regex producer's
    /// <c>RawSignature = trimmed</c> capture exactly. Earlier-line attributes are dropped
    /// from both producers' RawSignature (regex via <c>pendingMainActor</c>/
    /// <c>pendingDeprecated</c> booleans, SwiftSyntax via the same-line filter).</summary>
    private static void AssertExtensionCandidatesParity(
        string label,
        List<ExtensionMemberCandidate>? regexList,
        List<ExtensionMemberCandidate>? swiftSyntaxList)
    {
        Assert.NotNull(regexList);
        Assert.NotNull(swiftSyntaxList);
        Assert.True(regexList!.Count == swiftSyntaxList!.Count,
            $"[{label}] ExtensionMemberCandidates count diverged. regex={regexList.Count} swift-syntax={swiftSyntaxList.Count}\n  regex: {Join(regexList.Select(c => $"{c.ExtendedTypeName}.{c.PrintedName}"))}\n  swift: {Join(swiftSyntaxList.Select(c => $"{c.ExtendedTypeName}.{c.PrintedName}"))}");

        for (int i = 0; i < regexList.Count; i++)
        {
            var r = regexList[i];
            var s = swiftSyntaxList[i];
            AssertCandidatesEqual(label, $"[{i}]", r, s);
        }
    }

    private static void AssertCandidatesEqual(string label, string idx,
        ExtensionMemberCandidate r, ExtensionMemberCandidate s)
    {
        Assert.True(r.ExtendedTypeName == s.ExtendedTypeName,
            $"[{label}]{idx} ExtendedTypeName diverged. regex='{r.ExtendedTypeName}' swift-syntax='{s.ExtendedTypeName}'");
        Assert.True(r.MethodName == s.MethodName,
            $"[{label}]{idx} MethodName diverged. regex='{r.MethodName}' swift-syntax='{s.MethodName}'");
        Assert.True(r.PrintedName == s.PrintedName,
            $"[{label}]{idx} PrintedName diverged. regex='{r.PrintedName}' swift-syntax='{s.PrintedName}'");
        Assert.True(r.ReturnsSelf == s.ReturnsSelf,
            $"[{label}]{idx} ReturnsSelf diverged. regex={r.ReturnsSelf} swift-syntax={s.ReturnsSelf}");
        Assert.True(r.IsMainActorIsolated == s.IsMainActorIsolated,
            $"[{label}]{idx} IsMainActorIsolated diverged. regex={r.IsMainActorIsolated} swift-syntax={s.IsMainActorIsolated}");
        Assert.True(r.IsStatic == s.IsStatic,
            $"[{label}]{idx} IsStatic diverged. regex={r.IsStatic} swift-syntax={s.IsStatic}");
        Assert.True(r.IsProperty == s.IsProperty,
            $"[{label}]{idx} IsProperty diverged. regex={r.IsProperty} swift-syntax={s.IsProperty}");
        Assert.True(r.HasSetter == s.HasSetter,
            $"[{label}]{idx} HasSetter diverged. regex={r.HasSetter} swift-syntax={s.HasSetter}");
        Assert.True(r.IsDeprecated == s.IsDeprecated,
            $"[{label}]{idx} IsDeprecated diverged. regex={r.IsDeprecated} swift-syntax={s.IsDeprecated}");
        Assert.True(r.IsMutating == s.IsMutating,
            $"[{label}]{idx} IsMutating diverged. regex={r.IsMutating} swift-syntax={s.IsMutating}");

        Assert.True(r.WhereConstraints.Count == s.WhereConstraints.Count,
            $"[{label}]{idx} WhereConstraints count diverged. regex={r.WhereConstraints.Count} swift-syntax={s.WhereConstraints.Count}\n  regex: {Join(r.WhereConstraints)}\n  swift: {Join(s.WhereConstraints)}");
        for (int j = 0; j < r.WhereConstraints.Count; j++)
        {
            Assert.True(r.WhereConstraints[j] == s.WhereConstraints[j],
                $"[{label}]{idx} WhereConstraints[{j}] diverged. regex='{r.WhereConstraints[j]}' swift-syntax='{s.WhereConstraints[j]}'");
        }

        Assert.True(r.RawSignature == s.RawSignature,
            $"[{label}]{idx} RawSignature diverged.\n  regex='{r.RawSignature}'\n  swift='{s.RawSignature}'");
    }

    /// <summary>Compares two ProtocolExtensionMethods dictionaries. Keys must match;
    /// per-key value lists are compared element-wise via the same field-equality rules
    /// as <see cref="AssertExtensionCandidatesParity"/>.</summary>
    private static void AssertProtocolExtensionMethodsParity(
        string label,
        Dictionary<string, List<ProtocolExtensionMethodDecl>>? regexDict,
        Dictionary<string, List<ProtocolExtensionMethodDecl>>? swiftSyntaxDict)
    {
        Assert.NotNull(regexDict);
        Assert.NotNull(swiftSyntaxDict);
        Assert.True(regexDict!.Count == swiftSyntaxDict!.Count,
            $"[{label}] ProtocolExtensionMethods count diverged. regex={regexDict.Count} swift-syntax={swiftSyntaxDict.Count}\n  regex keys: {Join(regexDict.Keys)}\n  swift keys: {Join(swiftSyntaxDict.Keys)}");

        foreach (var key in regexDict.Keys)
        {
            Assert.True(swiftSyntaxDict.ContainsKey(key),
                $"[{label}] ProtocolExtensionMethods: swift-syntax missing key '{key}'");
            var rList = regexDict[key];
            var sList = swiftSyntaxDict[key];
            Assert.True(rList.Count == sList.Count,
                $"[{label}] ProtocolExtensionMethods['{key}'] count diverged. regex={rList.Count} swift-syntax={sList.Count}");

            for (int i = 0; i < rList.Count; i++)
            {
                var rDecl = rList[i];
                var sDecl = sList[i];
                Assert.True(rDecl.ProtocolQualifiedName == sDecl.ProtocolQualifiedName,
                    $"[{label}] ProtocolExtensionMethods['{key}'][{i}] ProtocolQualifiedName diverged. regex='{rDecl.ProtocolQualifiedName}' swift='{sDecl.ProtocolQualifiedName}'");
                AssertCandidatesEqual(label, $"['{key}'][{i}]",
                    DeclToCandidate(rDecl), DeclToCandidate(sDecl));
            }
        }
    }

    /// <summary>Reverse of <c>SwiftInterfaceFacts.CandidateToDecl</c> — strips
    /// <see cref="ProtocolExtensionMethodDecl.ProtocolQualifiedName"/> back to
    /// <see cref="ExtensionMemberCandidate.ExtendedTypeName"/> so we can reuse the
    /// candidate parity assertion on the decl form.</summary>
    private static ExtensionMemberCandidate DeclToCandidate(ProtocolExtensionMethodDecl decl) =>
        new()
        {
            ExtendedTypeName = decl.ProtocolQualifiedName,
            MethodName = decl.MethodName,
            RawSignature = decl.RawSignature,
            PrintedName = decl.PrintedName,
            ReturnsSelf = decl.ReturnsSelf,
            IsMainActorIsolated = decl.IsMainActorIsolated,
            IsStatic = decl.IsStatic,
            IsProperty = decl.IsProperty,
            HasSetter = decl.HasSetter,
            IsDeprecated = decl.IsDeprecated,
            IsMutating = decl.IsMutating,
            WhereConstraints = new List<string>(decl.WhereConstraints),
        };

    private static string ResolveBinaryOrSkip(string label)
    {
        var path = SwiftSyntaxInterfaceFactsProducer.TryLocateBinary();
        // Xunit.Skip.IfNot from Xunit.SkippableFact: marks the [SkippableFact]/[SkippableTheory]
        // as Skipped (not Failed) when the precondition isn't met.
        Xunit.Skip.IfNot(path is not null && File.Exists(path),
            $"[{label}] SwiftInterfaceParser binary not found. Run `nuke compile` " +
            "(or set SWIFT_INTERFACE_PARSER_PATH) and re-run tests.");
        return path!;
    }

    private static string WriteTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"InterfaceFactsParity-{Guid.NewGuid()}.swiftinterface");
        File.WriteAllText(path, content);
        return path;
    }

    private static string Join<T>(IEnumerable<T> items) => "[" + string.Join(", ", items.Select(x => x?.ToString() ?? "<null>")) + "]";
}
