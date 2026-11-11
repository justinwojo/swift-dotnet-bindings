// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A synthesized wrapper function described from the declaration its producer writes: the header
/// reader, the self a declaring type implies, the facts the table builds from the description, and
/// how the emission context registers it.
/// </summary>
public class SyntheticSwiftFunctionTests
{
    private const string Wrapper = "TestModuleSwiftBindings";

    // ---- reading the written declaration ----

    [Fact]
    public void Header_ReadsSymbolNameParametersEffectsAndResult()
    {
        var f = SyntheticSwiftFunction.FromDeclaration(
            """
            @available(iOS 17.0, *)
            @MainActor @_silgen_name("SBSW_Point_move_1234")
            public func _sbw_move(_ x: Swift.Int, by delta: inout Swift.Double, _ token: __owned TestModule.Box) async throws -> Swift.String {
                fatalError()
            }
            """,
            SwiftWrapperSynthesizer.AsyncBridge, Wrapper);

        Assert.Equal("SBSW_Point_move_1234", f.Symbol);
        Assert.Equal("_sbw_move", f.MemberName);
        Assert.True(f.IsAsync);
        Assert.True(f.Throws);
        Assert.Equal("Swift.String", f.ResultType);
        Assert.Equal(SyntheticSwiftSelf.None, f.Self);
        Assert.Equal(new[] { "x", "delta", "token" }, f.Parameters.Select(p => p.Name));
        Assert.Equal(new[] { "Swift.Int", "Swift.Double", "TestModule.Box" }, f.Parameters.Select(p => p.SwiftType));
        Assert.Equal(new SwiftParameterConvention?[] { null, SwiftParameterConvention.Inout, SwiftParameterConvention.Owned },
            f.Parameters.Select(p => p.Convention));
    }

    [Theory]
    [InlineData("-> ()")]
    [InlineData("-> Swift.Void")]
    [InlineData("")]
    public void Header_AVoidResultIsNoResult(string result)
    {
        var f = SyntheticSwiftFunction.FromDeclaration(
            $"@_silgen_name(\"S\") public func f() {result} {{", SwiftWrapperSynthesizer.OptionalBuffer, Wrapper);

        Assert.Null(f.ResultType);
    }

    [Fact]
    public void Header_KeepsAttributedClosureTypesWholeAndIgnoresDefaultValues()
    {
        var f = SyntheticSwiftFunction.FromDeclaration(
            """
            @_silgen_name("S")
            public func f(_ cb: @escaping @convention(c) (Swift.UnsafeMutableRawPointer?, Swift.Int) -> Swift.Void, _ ctx: Swift.UnsafeMutableRawPointer, n: Swift.Int = 3) -> Swift.Bool {
            """,
            SwiftWrapperSynthesizer.ClosureParameterBridge, Wrapper);

        Assert.Equal(3, f.Parameters.Length);
        Assert.Equal("@escaping @convention(c) (Swift.UnsafeMutableRawPointer?, Swift.Int) -> Swift.Void", f.Parameters[0].SwiftType);
        Assert.Equal("Swift.Int", f.Parameters[2].SwiftType);
        Assert.Equal("Swift.Bool", f.ResultType);
    }

    [Fact]
    public void Header_MergesTheGenericClauseAndTheWhereClause()
    {
        var f = SyntheticSwiftFunction.FromDeclaration(
            "@_silgen_name(\"S\") public func f<T: TestModule.P, U>(_ t: T, _ u: U) -> T where U: Swift.Equatable {",
            SwiftWrapperSynthesizer.GenericClosureBridge, Wrapper);

        Assert.Equal("<T, U where T : TestModule.P, U: Swift.Equatable>", f.GenericSignature);
        Assert.Equal("T", f.ResultType);
    }

    [Fact]
    public void Header_AMemberOfAClassTakesItsSelfByReferenceAndAStaticOneTheThickMetatype()
    {
        var box = new SyntheticSwiftDeclaringType("TestModule.Box", IsClass: true);

        var instance = SyntheticSwiftFunction.FromDeclaration("@_silgen_name(\"I\") public func f() {", SwiftWrapperSynthesizer.MethodClosureBridge, Wrapper, declaringType: box);
        var type = SyntheticSwiftFunction.FromDeclaration("@_silgen_name(\"S\") public static func f() {", SwiftWrapperSynthesizer.MethodClosureBridge, Wrapper, declaringType: box);

        Assert.Equal(new SyntheticSwiftSelf(SwiftCallSelfKind.Reference, SwiftSelfMutation.Nonmutating, "TestModule.Box"), instance.Self);
        Assert.Equal(new SyntheticSwiftSelf(SwiftCallSelfKind.ThickMetatype, SwiftSelfMutation.NotApplicable, "TestModule.Box"), type.Self);
        Assert.Equal("TestModule.Box", instance.DeclaringType);
    }

    [Fact]
    public void Header_AMemberOfAValueTypeTakesItsMutationFromTheModifier()
    {
        var point = new SyntheticSwiftDeclaringType("TestModule.Point", IsClass: false);

        var plain = SyntheticSwiftFunction.FromDeclaration("@_silgen_name(\"A\") public func f() {", SwiftWrapperSynthesizer.DefaultParameterOverload, Wrapper, declaringType: point);
        var mutating = SyntheticSwiftFunction.FromDeclaration("@_silgen_name(\"B\") public mutating func f() {", SwiftWrapperSynthesizer.DefaultParameterOverload, Wrapper, declaringType: point);
        var type = SyntheticSwiftFunction.FromDeclaration("@_silgen_name(\"C\") public static func f() {", SwiftWrapperSynthesizer.DefaultParameterOverload, Wrapper, declaringType: point);

        Assert.Equal(SwiftCallSelfKind.Value, plain.Self.Kind);
        Assert.Equal(SwiftSelfMutation.Nonmutating, plain.Self.Mutation);
        Assert.Equal(SwiftSelfMutation.Mutating, mutating.Self.Mutation);
        Assert.Equal(SwiftCallSelfKind.ThinMetatype, type.Self.Kind);
    }

    [Theory]
    [InlineData("public func f() {")]                                         // no @_silgen_name
    [InlineData("@_silgen_name(\"S\") public var x: Swift.Int {")]           // not a function
    [InlineData("@_silgen_name(\"S\") public func f {")]                     // no parameter clause
    [InlineData("@_silgen_name(\"S\") public func f(x) {")]                  // a parameter without a type
    [InlineData("@_silgen_name(\"S\") public static func f() {")]            // a member with no declaring type
    [InlineData("@_silgen_name(\"S\") public func f() where T: P {")]        // a where clause with no generics
    public void Header_TextThatIsNotAWrapperDeclarationIsRefused(string declaration)
    {
        Assert.Throws<FormatException>(() =>
            SyntheticSwiftFunction.FromDeclaration(declaration, SwiftWrapperSynthesizer.OptionalBuffer, Wrapper));
    }

    [Theory]
    [InlineData("@convention(c) (Swift.Int) -> Swift.Void", "@convention[c] (Swift.Int) -> Swift.Void")]
    [InlineData("@escaping @convention(c) (Swift.Int) -> Swift.Void", "@escaping @convention[c] (Swift.Int) -> Swift.Void")]
    [InlineData("(Swift.Int) -> Swift.Void", "(Swift.Int) -> Swift.Void")]
    [InlineData("@escaping (Swift.Int, (Swift.Bool)) -> Swift.Void", "@escaping (Swift.Int, (Swift.Bool)) -> Swift.Void")]
    public void BracketAttributeArguments_RewritesOnlyAnAttributesOwnArguments(string written, string expected)
    {
        Assert.Equal(expected, SwiftCallFactsBuilder.BracketAttributeArguments(written));
    }

    // ---- facts from the description ----

    private static (ModuleDecl Module, TypeDatabase Db) Fixture()
    {
        var db = new TypeDatabase();
        static (SwiftTypeName, TypeRecord) Rec(string name, TypeRecordKind kind, TypeRecordFlags flags)
        {
            var swiftName = SwiftTypeName.FromModuleQualifiedName(name);
            return (swiftName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Ns", swiftName.Name),
                SwiftTypeName = swiftName,
                MetadataAccessor = string.Empty,
                Flags = flags,
                Kind = kind,
            });
        }
        db.AddOutOfModuleTypes(new[]
        {
            Rec("Swift.Int", TypeRecordKind.Struct, TypeRecordFlags.Frozen),
            Rec("Swift.Bool", TypeRecordKind.Struct, TypeRecordFlags.Frozen),
            Rec("TestModule.Point", TypeRecordKind.Struct, TypeRecordFlags.Frozen),
            Rec("TestModule.Box", TypeRecordKind.Class, TypeRecordFlags.None),
        });
        var module = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
        return (module, db);
    }

    private static SwiftCallFacts Describe(string declaration, SyntheticSwiftDeclaringType? declaringType = null)
    {
        var (module, db) = Fixture();
        var function = SyntheticSwiftFunction.FromDeclaration(declaration, SwiftWrapperSynthesizer.ClosureParameterBridge, Wrapper,
            forwards: "$s10TestModule1fyyF", declaringType: declaringType);
        return SwiftCallFactsTable.Build(module, db).Describe(function);
    }

    [Fact]
    public void Facts_IdentifyTheSyntheticTargetItsProducerAndWhatItForwardsTo()
    {
        var facts = Describe("@_silgen_name(\"SBSW_f\") public func f(_ x: Swift.Int) -> Swift.Bool {");

        Assert.True(facts.IsComplete, string.Join("; ", facts.Unknowns));
        Assert.Equal(SwiftCallTargetKind.SyntheticWrapper, facts.Target.Kind);
        Assert.Equal("SBSW_f", facts.Id);
        Assert.Equal(Wrapper, facts.Target.Module);
        Assert.Equal(SwiftWrapperSynthesizer.ClosureParameterBridge, facts.Target.Synthesizer);
        Assert.Equal("$s10TestModule1fyyF", facts.Target.Forwards);
        Assert.Equal(SwiftTypeCategory.Struct, Assert.Single(facts.Parameters).Type.Category);
        Assert.Equal(SwiftParameterConvention.Guaranteed, facts.Parameters[0].Convention);
        Assert.Equal(SwiftConventionSource.Default, facts.Parameters[0].ConventionSource);
    }

    [Fact]
    public void Facts_ACFunctionPointerIsOneWordWithNoContextAndNotAClosure()
    {
        var facts = Describe("@_silgen_name(\"S\") public func f(_ cb: @convention(c) (Swift.Int) -> Swift.Void, _ ctx: Swift.UnsafeMutableRawPointer) {");

        Assert.True(facts.IsComplete, string.Join("; ", facts.Unknowns));
        Assert.Equal(SwiftTypeCategory.CFunctionPointer, facts.Parameters[0].Type.Category);
        Assert.Equal(SwiftClosureKind.NotClosure, facts.Parameters[0].Closure);
        // A raw pointer is a frozen one-word struct.
        Assert.Equal(SwiftTypeCategory.Struct, facts.Parameters[1].Type.Category);
    }

    [Fact]
    public void Facts_ASwiftClosureKeepsItsEscapingness()
    {
        var facts = Describe("@_silgen_name(\"S\") public func f(_ a: @escaping (Swift.Int) -> Swift.Void, _ b: (Swift.Int) -> Swift.Void) {");

        Assert.Equal(SwiftClosureKind.Escaping, facts.Parameters[0].Closure);
        Assert.Equal(SwiftClosureKind.NonEscaping, facts.Parameters[1].Closure);
        Assert.All(facts.Parameters, p => Assert.Equal(SwiftTypeCategory.Closure, p.Type.Category));
    }

    [Fact]
    public void Facts_AnUnqualifiedNameResolvesInTheModuleTheWrapperImports()
    {
        var facts = Describe("@_silgen_name(\"S\") public func f(_ p: Point, _ b: Box) {");

        Assert.True(facts.IsComplete, string.Join("; ", facts.Unknowns));
        Assert.Equal(SwiftTypeCategory.Struct, facts.Parameters[0].Type.Category);
        Assert.Equal(SwiftTypeCategory.Class, facts.Parameters[1].Type.Category);
    }

    [Fact]
    public void Facts_ATypeNoDatabaseKnowsIsAnUnknownNeverAGuess()
    {
        var facts = Describe("@_silgen_name(\"S\") public func f(_ m: Elsewhere.Mystery) {");

        Assert.False(facts.IsComplete);
        Assert.Equal(SwiftTypeCategory.Unresolved, facts.Parameters[0].Type.Category);
        Assert.Contains(facts.Unknowns, u => u.Contains("Elsewhere.Mystery"));
    }

    [Fact]
    public void Facts_AMemberOfAGenericTypeIsGenericOverTheTypesParameters()
    {
        var facts = Describe(
            "@_silgen_name(\"S\") public func f(_ seed: T) -> Swift.Int {",
            new SyntheticSwiftDeclaringType("TestModule.Box", IsClass: true, "<τ_0_0>", ImmutableArray.Create("T")));

        Assert.True(facts.IsComplete, string.Join("; ", facts.Unknowns));
        Assert.Equal(SwiftSelfAbstraction.GenericParent, facts.Self.Abstraction);
        Assert.Equal(SwiftCallSelfKind.Reference, facts.Self.Kind);
        Assert.Equal(new[] { "τ_0_0" }, facts.Generics.Parameters);
        Assert.Equal(SwiftTypeCategory.GenericParameter, facts.Parameters[0].Type.Category);
    }

    [Fact]
    public void Facts_AGenericParentWithoutItsSignatureIsAnUnknown()
    {
        var facts = Describe(
            "@_silgen_name(\"S\") public func f(_ seed: T) {",
            new SyntheticSwiftDeclaringType("TestModule.Box", IsClass: true, GenericSignature: null, ImmutableArray.Create("T")));

        Assert.False(facts.IsComplete);
        Assert.Contains(facts.Unknowns, u => u.Contains("generic signature is not known"));
    }

    [Fact]
    public void DeclaringType_RecordsATypeParameterUnderEverySpellingAProducerMayWrite()
    {
        var module = Fixture().Module;
        var box = new ClassDecl
        {
            Name = "Box",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Box"),
            MangledName = "$s10TestModule3BoxCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl> { new("τ_0_0", "Element", new(), new()) },
            Conformances = new List<TypeConformance>(),
            ParentDecl = module,
            ModuleDecl = module,
            RawGenericSig = "<τ_0_0>",
        };

        var declaringType = SyntheticSwiftDeclaringType.Of(box);
        var facts = Describe("@_silgen_name(\"S\") public func f(_ e: Element, _ raw: τ_0_0) {", declaringType);

        Assert.Equal(new[] { "τ_0_0", "Element" }, declaringType.GenericParameters);
        Assert.True(facts.IsComplete, string.Join("; ", facts.Unknowns));
        Assert.All(facts.Parameters, p => Assert.Equal(SwiftTypeCategory.GenericParameter, p.Type.Category));
    }

    // ---- registration in the emission context ----

    [Fact]
    public void Context_ResolvesARegisteredFunctionBeforeADeclaredSymbol()
    {
        var context = new ModuleEmissionContext();
        context.RegisterSyntheticSwiftDeclaration(
            ["@_silgen_name(\"SBSW_f\")", "public func f() -> Swift.Int {"],
            SwiftWrapperSynthesizer.OptionalBuffer, Wrapper);

        Assert.True(context.TryResolveCallFacts("SBSW_f", out var facts, out var source));
        Assert.Equal(SwiftCallFactsSource.Synthesized, source);
        Assert.Equal(SwiftWrapperSynthesizer.OptionalBuffer, facts!.Target.Synthesizer);
        Assert.False(context.TryResolveCallFacts("SBSW_unregistered", out _, out _));
    }

    [Fact]
    public void Context_ASecondDifferentRegistrationOfASymbolIsAConflict()
    {
        var context = new ModuleEmissionContext();
        string[] declaration = ["@_silgen_name(\"SBSW_f\")", "public func f() -> Swift.Int {"];
        context.RegisterSyntheticSwiftDeclaration(declaration, SwiftWrapperSynthesizer.OptionalBuffer, Wrapper);
        context.RegisterSyntheticSwiftDeclaration(declaration, SwiftWrapperSynthesizer.OptionalBuffer, Wrapper);
        Assert.Empty(context.SyntheticSwiftFunctionConflicts);

        context.RegisterSyntheticSwiftDeclaration(
            ["@_silgen_name(\"SBSW_f\")", "public func f() -> Swift.Bool {"],
            SwiftWrapperSynthesizer.OptionalBuffer, Wrapper);

        Assert.Contains(context.SyntheticSwiftFunctionConflicts, c => c.Contains("SBSW_f"));
        Assert.Single(context.SyntheticSwiftFunctions);
    }

    [Fact]
    public void Context_AnUnreadableDeclarationBecomesAnIssueNotAnException()
    {
        var context = new ModuleEmissionContext();

        context.RegisterSyntheticSwiftDeclaration(["public func f() {"], SwiftWrapperSynthesizer.ExistentialBypass, Wrapper);

        Assert.Empty(context.SyntheticSwiftFunctions);
        var issue = Assert.Single(context.SyntheticSwiftFunctionConflicts);
        Assert.Contains("ExistentialBypass", issue);
        Assert.Contains("public func f() {", issue);
    }

    [Fact]
    public void Context_ARolledBackMemberTakesItsRegistrationWithIt()
    {
        var context = new ModuleEmissionContext();
        context.RegisterSyntheticSwiftDeclaration(["@_silgen_name(\"SBSW_kept\")", "public func f() {"], SwiftWrapperSynthesizer.OptionalBuffer, Wrapper);
        var snapshot = ModuleEmissionStateSnapshot.Capture(context);

        context.RegisterSyntheticSwiftDeclaration(["@_silgen_name(\"SBSW_dropped\")", "public func g() {"], SwiftWrapperSynthesizer.OptionalBuffer, Wrapper);
        context.RecordSwiftTransition(new SwiftTransitionRecord { MethodName = "M", EntryPoint = "SBSW_dropped", Library = Wrapper });
        snapshot.Restore();

        Assert.True(context.TryGetSyntheticSwiftFunction("SBSW_kept", out _));
        Assert.False(context.TryGetSyntheticSwiftFunction("SBSW_dropped", out _));
        Assert.Empty(context.SwiftTransitionRecords);
    }
}
