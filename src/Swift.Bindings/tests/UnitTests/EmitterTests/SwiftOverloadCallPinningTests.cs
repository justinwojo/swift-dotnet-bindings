// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The call-pinning replay: a wrapper that leaves defaulted arguments out must still reach the
/// declaration it was generated for, never a same-named sibling that Swift ranks higher.
/// </summary>
public class SwiftOverloadCallPinningTests
{
    [Fact]
    public void DisfavoredTarget_FavoredSiblingTakesTheReducedCall_SpellsOutTheDefault()
    {
        // @_disfavoredOverload create(grid:ifNotExists:using:) beside the favored
        // create(grid:options:using:). Leaving ifNotExists out makes the call create(grid:using:),
        // which the favored sibling also accepts and therefore wins. Writing the default out keeps
        // the call on the target with the value Swift would have supplied.
        var target = Method("create", disfavored: true,
            Arg("grid", "Swift.String"), Arg("ifNotExists", "Swift.Bool", "false"), Arg("using", "τ_0_0", generic: true));
        var favored = Method("create", disfavored: false,
            Arg("grid", "Swift.String"), Arg("options", "TestLib.Options", "[]"), Arg("using", "τ_0_0", generic: true));
        Host(target, favored);

        var plan = SwiftOverloadCallPinning.Plan(target, arg => !arg.IsGeneric, typeDatabase: null);

        Assert.False(plan.IsRefused);
        Assert.Equal("ifNotExists: false", plan.PinFor(target.CSSignature[2]));
        Assert.Equal(1, plan.PinCount);
    }

    [Fact]
    public void FavoredTarget_DisfavoredSiblingTakesTheReducedCall_NeedsNoPin()
    {
        // The same pair seen from the favored side: Swift prefers the favored declaration, so its
        // reduced call is already unambiguous.
        var target = Method("create", disfavored: false,
            Arg("grid", "Swift.String"), Arg("options", "TestLib.Options", "[]"), Arg("using", "τ_0_0", generic: true));
        var disfavored = Method("create", disfavored: true,
            Arg("grid", "Swift.String"), Arg("ifNotExists", "Swift.Bool", "false"), Arg("using", "τ_0_0", generic: true));
        Host(target, disfavored);

        var plan = SwiftOverloadCallPinning.Plan(target, arg => !arg.IsGeneric, typeDatabase: null);

        Assert.False(plan.IsRefused);
        Assert.Equal(0, plan.PinCount);
    }

    [Fact]
    public void SiblingNeedingFewerDefaults_UnspellableDefault_RefusesAndNamesTheSibling()
    {
        // describe(a:b:) with b = Config() leaves describe(a:), which describe(a:) matches with no
        // defaults at all and so wins. Config() is not a literal the wrapper can repeat, so nothing
        // steers the call back: the plan is refused and names the declaration that would run.
        var target = Method("describe", disfavored: false,
            Arg("a", "Swift.Int"), Arg("b", "Swift.Int", "Config()"));
        var shorter = Method("describe", disfavored: false, Arg("a", "Swift.Int"));
        Host(target, shorter);

        var plan = SwiftOverloadCallPinning.Plan(target, _ => true, typeDatabase: null);

        Assert.True(plan.IsRefused);
        Assert.Equal("describe(a:)", plan.WinningSibling);
    }

    [Fact]
    public void SiblingNeedingMoreDefaults_TargetStillWins()
    {
        // Both take describe(a:), but the sibling fills two defaults to the target's one; with the
        // same argument types Swift picks the target.
        var target = Method("describe", disfavored: false,
            Arg("a", "Swift.Int"), Arg("b", "Swift.Int", "Config()"));
        var longer = Method("describe", disfavored: false,
            Arg("a", "Swift.Int"), Arg("b", "Swift.String", "\"\""), Arg("c", "Swift.Int", "0"));
        Host(target, longer);

        var plan = SwiftOverloadCallPinning.Plan(target, _ => true, typeDatabase: null);

        Assert.False(plan.IsRefused);
        Assert.Equal(0, plan.PinCount);
    }

    [Fact]
    public void SiblingWithTheSameRequiredArguments_TieIsBrokenByTheSpellableDefault()
    {
        // describe(a:flag:) with flag = true beside describe(a:): omitting flag leaves a call the
        // sibling matches with no defaults, so the plan writes `flag: true` out — the one argument
        // the sibling cannot take.
        var target = Method("describe", disfavored: false,
            Arg("a", "Swift.Int"), Arg("flag", "Swift.Bool", "true"));
        var sibling = Method("describe", disfavored: false, Arg("a", "Swift.Int"));
        Host(target, sibling);

        var plan = SwiftOverloadCallPinning.Plan(target, _ => true, typeDatabase: null);

        Assert.False(plan.IsRefused);
        Assert.Equal("flag: true", plan.PinFor(target.CSSignature[2]));
    }

    [Fact]
    public void DebugParameter_RecordedMagicLiteral_IsPinned()
    {
        // log(_:file:line:) beside log(_:): a wrapper drops the debug parameters, and log(_:) then
        // wins. A written-out #file evaluates at the wrapper's call exactly as the omitted default.
        var file = Arg("file", "Swift.StaticString", "#file");
        var line = Arg("line", "Swift.UInt", "#line");
        var target = Method("log", disfavored: false, Arg("", "Swift.String"), file, line);
        var plain = Method("log", disfavored: false, Arg("", "Swift.String"));
        Host(target, plain);

        var plan = SwiftOverloadCallPinning.Plan(target, DefaultParameterOverloadEmitter.IsDebugParameter, typeDatabase: null);

        Assert.False(plan.IsRefused);
        Assert.Equal("file: #file", plan.PinFor(file));
        Assert.Null(plan.PinFor(line));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("#line")]
    [InlineData("#column")]
    public void DebugParameter_DefaultNotRepeatable_Refuses(string? recordedDefault)
    {
        // An unrecorded default may be anything, and a written-out #line or #column evaluates at its
        // own token rather than at the call; neither can stand in for the omitted default.
        var column = Arg("column", "Swift.UInt", recordedDefault, hasDefault: true);
        var target = Method("log", disfavored: false, Arg("", "Swift.String"), column);
        var plain = Method("log", disfavored: false, Arg("", "Swift.String"));
        Host(target, plain);

        var plan = SwiftOverloadCallPinning.Plan(target, DefaultParameterOverloadEmitter.IsDebugParameter, typeDatabase: null);

        Assert.True(plan.IsRefused);
    }

    [Fact]
    public void DebugParameterShim_CallWouldReachSibling_MemberIsDeclined()
    {
        // The debug-parameter shim is the member's only route once installed, so a refused call is
        // reported to the caller, which declines the member before installing it.
        var target = Method("log", disfavored: false, Arg("", "Swift.String"), Arg("line", "Swift.UInt", null, hasDefault: true));
        var plain = Method("log", disfavored: false, Arg("", "Swift.String"));
        Host(target, plain);

        Assert.Equal("log(_:)", DefaultParameterOverloadEmitter.DebugParamCallWinner(target, typeDatabase: null));
    }

    [Fact]
    public void PinThatLetsAnotherSiblingIn_IsNotAPlan()
    {
        // Disfavored make(x:a:b:) beside favored make(x:) and make(x:a:). Spelling out only `a`
        // rules out make(x:) but turns the call into make(x:a:), which the favored make(x:a:) takes
        // with no defaults. Spelling out `b` rules out both.
        var a = Arg("a", "Swift.Int", "0");
        var b = Arg("b", "Swift.Bool", "false");
        var target = Method("make", disfavored: true, Arg("x", "τ_0_0", generic: true), a, b);
        var shortest = Method("make", disfavored: false, Arg("x", "τ_0_0", generic: true));
        var middle = Method("make", disfavored: false, Arg("x", "τ_0_0", generic: true), Arg("a", "Swift.Int"));
        Host(target, shortest, middle);

        var plan = SwiftOverloadCallPinning.Plan(target, arg => !arg.IsGeneric, typeDatabase: null);

        Assert.False(plan.IsRefused);
        Assert.Equal("b: false", plan.PinFor(b));
        Assert.Equal(1, plan.PinCount);
    }

    [Fact]
    public void PinnedLiteral_SiblingTakingItAtAnotherType_StillCompetes()
    {
        // Disfavored set(t:value: Float = 0) beside favored set(t:value: Int = 0). A written-out
        // `value: 0` is an integer literal both accept, and Swift picks the favored Int one, so the
        // literal rules nothing out and the call is refused.
        var target = Method("set", disfavored: true, Arg("t", "τ_0_0", generic: true), Arg("value", "Swift.Float", "0"));
        var favored = Method("set", disfavored: false, Arg("t", "τ_0_0", generic: true), Arg("value", "Swift.Int", "0"));
        Host(target, favored);

        var plan = SwiftOverloadCallPinning.Plan(target, arg => !arg.IsGeneric, typeDatabase: null);

        Assert.True(plan.IsRefused);
    }

    [Fact]
    public void PassedArgumentOfAnotherType_SiblingThatMayConvertIt_StillCompetes()
    {
        // Disfavored use(t:x: Derived, flag: = false) beside favored use(t:x: Base). The model
        // cannot rule out a conversion from Derived to Base, so the sibling must be ruled out by the
        // spelled-out `flag`.
        var flag = Arg("flag", "Swift.Bool", "false");
        var target = Method("use", disfavored: true, Arg("t", "τ_0_0", generic: true), Arg("x", "TestLib.Derived"), flag);
        var favored = Method("use", disfavored: false, Arg("t", "τ_0_0", generic: true), Arg("x", "TestLib.Base"));
        Host(target, favored);

        var plan = SwiftOverloadCallPinning.Plan(target, arg => !arg.IsGeneric, typeDatabase: null);

        Assert.Equal("flag: false", plan.PinFor(flag));
    }

    [Fact]
    public void SiblingWithTheSamePrintedName_DefaultIsNotTrusted()
    {
        // Interface defaults are recorded under the printed name, so beside another describe(a:b:)
        // the recorded `b` default may be that sibling's. With a shorter describe(a:) winning the
        // reduced call, nothing is written out and the plan is refused.
        // (Were the default trusted, `b: true` would rule out both siblings: the shorter one has no
        // `b`, and the other one's `b` is a String.)
        var target = Method("describe", disfavored: false, Arg("a", "Swift.Int"), Arg("b", "Swift.Bool", "true"));
        var samePrinted = Method("describe", disfavored: false, Arg("a", "Swift.Int"), Arg("b", "Swift.String", "\"\""));
        var shorter = Method("describe", disfavored: false, Arg("a", "Swift.Int"));
        Host(target, samePrinted, shorter);

        var plan = SwiftOverloadCallPinning.Plan(target, arg => arg.HasDefaultArg, typeDatabase: null);

        Assert.True(plan.IsRefused);
    }

    [Fact]
    public void InheritedSibling_Competes()
    {
        // Derived declares a disfavored run(x:flag: = false); Base declares run(x:). The inherited
        // run(x:) takes the reduced run(x:) call, so `flag` is spelled out.
        var flag = Arg("flag", "Swift.Bool", "false");
        var target = Method("run", disfavored: true, Arg("x", "Swift.Int"), flag);
        var inherited = Method("run", disfavored: false, Arg("x", "Swift.Int"));
        var derived = Host(target);
        derived.ResolvedSuperclass = Host(inherited);

        var plan = SwiftOverloadCallPinning.Plan(target, arg => arg.HasDefaultArg, typeDatabase: null);

        Assert.False(plan.IsRefused);
        Assert.Equal("flag: false", plan.PinFor(flag));
    }

    [Fact]
    public void AncestorOverloadOnResultTypeOnly_IsNotTakenForAnOverride()
    {
        // Base declares f(x:) -> String; Derived declares f(x:) -> Int (disfavored) and the target
        // f(x:flag: = false) -> String. Derived's f(x:) overloads Base's on the result type rather
        // than overriding it, so Base's f(x:) still takes the reduced f(x:) call with no defaults.
        var flag = Arg("flag", "Swift.Bool", "false");
        var target = Method("f", disfavored: false, Arg("x", "Swift.Int"), flag) with { CSSignature = WithResult("Swift.String", Arg("x", "Swift.Int"), flag) };
        var intResult = Method("f", disfavored: true) with { CSSignature = WithResult("Swift.Int", Arg("x", "Swift.Int")) };
        var stringResult = Method("f", disfavored: false) with { CSSignature = WithResult("Swift.String", Arg("x", "Swift.Int")) };
        var derived = Host(target, intResult);
        derived.ResolvedSuperclass = Host(stringResult);

        var plan = SwiftOverloadCallPinning.Plan(target, arg => arg.HasDefaultArg, typeDatabase: null);

        Assert.Equal("flag: false", plan.PinFor(flag));
    }

    [Fact]
    public void AncestorOverloadOnGenericConstraintsOnly_IsNotTakenForAnOverride()
    {
        // Base declares f<T: P>(x:); Child declares the target f<T: P>(x:flag: = false) and a
        // disfavored f<T: Q>(x:). Child's f(x:) overloads Base's on its constraint, so Base's f(x:)
        // still takes the reduced f(x:) call.
        var flag = Arg("flag", "Swift.Bool", "false");
        var target = Method("f", disfavored: false, Arg("x", "τ_0_0", generic: true), flag) with { RawGenericSig = "<τ_0_0 where τ_0_0 : TestLib.P>" };
        var constrainedToQ = Method("f", disfavored: true, Arg("x", "τ_0_0", generic: true)) with { RawGenericSig = "<τ_0_0 where τ_0_0 : TestLib.Q>" };
        var inherited = Method("f", disfavored: false, Arg("x", "τ_0_0", generic: true)) with { RawGenericSig = "<τ_0_0 where τ_0_0 : TestLib.P>" };
        var child = Host(target, constrainedToQ);
        child.ResolvedSuperclass = Host(inherited);

        var plan = SwiftOverloadCallPinning.Plan(target, arg => arg.HasDefaultArg, typeDatabase: null);

        Assert.Equal("flag: false", plan.PinFor(flag));
    }

    [Fact]
    public void AncestorMemberTheTargetOverrides_IsNotARival()
    {
        var target = Method("f", disfavored: false, Arg("x", "Swift.Int"), Arg("flag", "Swift.Bool", "false")) with { IsOverride = true };
        var overridden = Method("f", disfavored: false, Arg("x", "Swift.Int"), Arg("flag", "Swift.Bool", "false"));
        var child = Host(target);
        child.ResolvedSuperclass = Host(overridden);

        var plan = SwiftOverloadCallPinning.Plan(target, arg => arg.HasDefaultArg, typeDatabase: null);

        Assert.Same(SwiftCallPinPlan.Unchanged, plan);
    }

    [Fact]
    public void StrippedDebugParameters_SharingALabelWithAPassedArgument_StillCountAsDefaults()
    {
        // f(file a: Int, b: Int = computed(), file: StaticString = #file, file: StaticString = #file)
        // has had both debug parameters removed; its reduced f(file:) call needs three defaults,
        // so the sibling f(file:c:d:) needing two wins it.
        var passed = Arg("file", "Swift.Int");
        var computed = Arg("b", "Swift.Int", "computed()");
        var target = Method("f", disfavored: false, passed, computed,
            Arg("file", "Swift.StaticString", "#file"), Arg("file", "Swift.StaticString", "#file"));
        target.SignatureBeforeDebugStrip = target.CSSignature;
        target.CSSignature = target.CSSignature.Take(3).ToList();
        var sibling = Method("f", disfavored: false, Arg("file", "Swift.Int"), Arg("c", "Swift.Int", "computed()"), Arg("d", "Swift.Int", "computed()"));
        Host(target, sibling);

        var plan = SwiftOverloadCallPinning.Plan(target, arg => arg.HasDefaultArg, typeDatabase: null);

        Assert.True(plan.IsRefused);
    }

    [Fact]
    public void VariadicSibling_TakesTheCallWithoutVariadicArguments()
    {
        // make(using:flag: = false) beside make(using:_: Int...): a variadic parameter may take no
        // arguments, so the sibling takes the reduced make(using:) call.
        var flag = Arg("flag", "Swift.Bool", "false");
        var target = Method("make", disfavored: false, Arg("using", "τ_0_0", generic: true), flag);
        var variadic = Arg("", "Swift.Int");
        variadic.SwiftTypeSpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Int") { IsVariadic = true });
        var sibling = Method("make", disfavored: false, Arg("using", "TestLib.Item"), variadic);
        Host(target, sibling);

        var plan = SwiftOverloadCallPinning.Plan(target, arg => !arg.IsGeneric, typeDatabase: null);

        Assert.Equal("flag: false", plan.PinFor(flag));
    }

    [Fact]
    public void SiblingWhoseDebugParametersEmissionRemoved_IsReplayedAsSwiftDeclaresIt()
    {
        // Favored f(x:file:line:) has already had its debug parameters removed for its own shim;
        // Swift still sees them. The disfavored target f(x:file:) spelling out `file:` therefore does
        // not rule the sibling out, and with nothing else to spell the call is refused.
        var target = Method("f", disfavored: true, Arg("x", "Swift.Int"), Arg("file", "Swift.StaticString", "#file"));
        var sibling = Method("f", disfavored: false,
            Arg("x", "Swift.Int"), Arg("file", "Swift.StaticString", "#file"), Arg("line", "Swift.UInt", "#line"));
        sibling.SignatureBeforeDebugStrip = sibling.CSSignature;
        sibling.CSSignature = sibling.CSSignature.Take(2).ToList();
        Host(target, sibling);

        var plan = SwiftOverloadCallPinning.Plan(target, DefaultParameterOverloadEmitter.IsDebugParameter, typeDatabase: null);

        Assert.True(plan.IsRefused);
    }

    [Fact]
    public void ValueOfAKnownType_IsNoFunction()
    {
        // equal(_: String, line: Int = compute()) beside equal<T>(_: (Int) -> T, line: Int =
        // compute()): a String is never a closure, so the sibling cannot take equal("x").
        var line = Arg("line", "Swift.Int", "compute()");
        var target = Method("equal", disfavored: false, Arg("", "Swift.String"), line);
        var sibling = Method("equal", disfavored: false, ArgOfSpec("", "(Swift.Int) -> τ_0_0"), Arg("line", "Swift.Int", "compute()"));
        Host(target, sibling);

        var plan = SwiftOverloadCallPinning.Plan(target, arg => arg.HasDefaultArg, typeDatabase: null);

        Assert.Same(SwiftCallPinPlan.Unchanged, plan);
    }

    [Theory]
    [InlineData("(Foundation.HTTPURLResponse, (Swift.Int) -> ()) -> ()", false)]
    [InlineData("(Foundation.HTTPURLResponse) -> ()", true)]
    public void ClosureSibling_TakesOnlyAClosureOfItsArity(string siblingHandler, bool refused)
    {
        // on(queue:perform:) overloaded on its handler's arity: the target's closure can only
        // reach a sibling handler taking as many parameters.
        var target = Method("on", disfavored: false,
            Arg("queue", "Foundation.DispatchQueue", "compute()"), ArgOfSpec("perform", "(Foundation.HTTPURLResponse) -> ()"));
        var sibling = Method("on", disfavored: false,
            Arg("queue", "Foundation.DispatchQueue", "compute()"), ArgOfSpec("perform", siblingHandler));
        Host(target, sibling);

        var plan = SwiftOverloadCallPinning.Plan(target, arg => arg.HasDefaultArg, typeDatabase: null);

        Assert.Equal(refused, plan.IsRefused);
    }

    [Theory]
    [InlineData("Foundation.URL", false)]
    [InlineData("Foundation.InputStream", false)]
    [InlineData("Foundation.UnknownBox", true)]
    public void StructValue_ReachesNoOtherKnownStructOrClass(string siblingType, bool refused)
    {
        // upload(_: Data, with: Int, interceptor: = compute()) beside upload(_: <sibling>, with:
        // Int, interceptor: = compute()): a struct converts into no other struct and no class; a
        // type the database does not know may be an alias of Data.
        var types = new TypeDatabase();
        var foundation = new ModuleTypeDatabase("Foundation", "/tmp/Foundation.dylib");
        foreach (var (name, kind) in new[] { ("Data", TypeRecordKind.Struct), ("URL", TypeRecordKind.Struct), ("InputStream", TypeRecordKind.Class) })
        {
            var swiftName = SwiftTypeName.FromModuleQualifiedName($"Foundation.{name}");
            foundation.RegisterType(swiftName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", name),
                SwiftTypeName = swiftName,
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.None,
                Kind = kind,
            });
        }
        types.AddModuleDatabase(foundation);
        var target = Method("upload", disfavored: false,
            Arg("", "Foundation.Data"), Arg("with", "Swift.Int"), Arg("interceptor", "Swift.Int", "compute()"));
        var sibling = Method("upload", disfavored: false,
            Arg("", siblingType), Arg("with", "Swift.Int"), Arg("interceptor", "Swift.Int", "compute()"));
        Host(target, sibling);

        var plan = SwiftOverloadCallPinning.Plan(target, arg => arg.HasDefaultArg, types);

        Assert.Equal(refused, plan.IsRefused);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void AsyncTarget_OutranksASynchronousSiblingUnlessDisfavoredAgainstIt(
        bool targetDisfavored, bool siblingDisfavored, bool refused)
    {
        // store(_:forKey:toDisk: = compute()) async beside a synchronous store(_:forKey:): the call
        // is awaited, so it is in an async context, where Swift ranks the synchronous declaration
        // below the async one even though it needs fewer defaults; only favoring weighs more.
        var target = Method("store", targetDisfavored,
            Arg("", "Swift.Int"), Arg("forKey", "Swift.String"), Arg("toDisk", "Swift.Bool", "compute()")) with { IsAsync = true };
        var sibling = Method("store", siblingDisfavored, Arg("", "Swift.Int"), Arg("forKey", "Swift.String"));
        Host(target, sibling);

        var plan = SwiftOverloadCallPinning.Plan(target, arg => arg.HasDefaultArg, typeDatabase: null);

        Assert.Equal(refused, plan.IsRefused);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SynchronousTarget_OutranksEveryAsyncSibling(bool targetDisfavored)
    {
        // log(_:line: = #line) beside log(_:) async: every wrapper calls a synchronous target from
        // a synchronous body, where an async declaration cannot be called; Swift picks the
        // synchronous one even when it is disfavored.
        var target = Method("log", targetDisfavored,
            Arg("", "Swift.Int"), Arg("line", "Swift.UInt", "#line"));
        var sibling = Method("log", disfavored: false, Arg("", "Swift.Int")) with { IsAsync = true };
        Host(target, sibling);

        var plan = SwiftOverloadCallPinning.Plan(target, arg => arg.HasDefaultArg, typeDatabase: null);

        Assert.Same(SwiftCallPinPlan.Unchanged, plan);
    }

    [Fact]
    public void AutoclosureArgument_IsPassedAsItsResult()
    {
        // choose(_: @autoclosure () -> Int, options: = []) beside choose(_: Int): the wrapper passes
        // `value()`, an Int, which the value-taking sibling accepts; spelling `options: []` keeps
        // the call on the target.
        var target = Method("choose", disfavored: false,
            ArgOfSpec("", "@autoclosure () -> Swift.Int"), Arg("options", "Swift.Array<Swift.Int>", "[]"));
        var sibling = Method("choose", disfavored: false, Arg("", "Swift.Int"));
        Host(target, sibling);

        var plan = SwiftOverloadCallPinning.Plan(target, arg => arg.HasDefaultArg, typeDatabase: null);

        Assert.False(plan.IsRefused);
        Assert.Equal("options: []", plan.PinFor(target.CSSignature[2]));
    }

    [Theory]
    [InlineData("Swift.Int", 1)]
    [InlineData("Swift.String", 0)]
    public void AutoclosureSibling_TakesWhatConvertsToItsResult(string resultType, int pins)
    {
        // choose(_: Int, options: = []) beside choose(_: @autoclosure () -> R): an autoclosure
        // parameter wraps any expression of a type that converts to R.
        var target = Method("choose", disfavored: false,
            Arg("", "Swift.Int"), Arg("options", "Swift.Array<Swift.Int>", "[]"));
        var sibling = Method("choose", disfavored: false, ArgOfSpec("", $"@autoclosure () -> {resultType}"));
        Host(target, sibling);

        var plan = SwiftOverloadCallPinning.Plan(target, arg => arg.HasDefaultArg, typeDatabase: null);

        Assert.False(plan.IsRefused);
        Assert.Equal(pins, plan.PinCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SpecializedCopy_IsRankedAsTheGenericDeclarationItCalls(bool isAsync)
    {
        // f<T: P>(x: T, a: = []) specialized for S, beside f(x: Any) and f(x: S, a:, b: = []):
        // spelling `a: []` shuts out the Any sibling, but the wrapper still calls the generic
        // declaration, which the concrete sibling outranks however many defaults it needs.
        var original = Method("f", disfavored: false,
            Arg("x", "τ_0_0", generic: true), Arg("a", "Swift.Array<Swift.Int>", "[]")) with { IsAsync = isAsync };
        var anySibling = Method("f", disfavored: false, Arg("x", "Swift.Any")) with { IsAsync = isAsync };
        var concreteSibling = Method("f", disfavored: false,
            Arg("x", "TestLib.S"), Arg("a", "Swift.Array<Swift.Int>"), Arg("b", "Swift.Array<Swift.Int>", "[]")) with { IsAsync = isAsync };
        Host(original, anySibling, concreteSibling);
        var specialized = original with
        {
            MangledName = "SBW_CSM_f_S",
            SpecializedFrom = original,
            CSSignature = new List<ArgumentDecl>
            {
                original.CSSignature[0],
                original.CSSignature[1] with { SwiftTypeSpec = new NamedTypeSpec("TestLib.S"), IsGeneric = false },
                original.CSSignature[2],
            },
        };

        var plan = SwiftOverloadCallPinning.Plan(specialized, arg => arg.HasDefaultArg, typeDatabase: null);

        Assert.True(plan.IsRefused);
    }

    [Theory]
    [InlineData("TestLib.Source", false)]
    [InlineData("TestLib.ImageResource", true)]
    [InlineData("TestLib.RefinedConformer", true)]
    [InlineData("TestLib.Unrecorded", true)]
    [InlineData("Foundation.URL", true)]
    public void ValueType_ReachesAnExistentialOfItsModuleOnlyThroughAConformance(string valueType, bool refused)
    {
        // retrieve(with: <value>, options: = compute()) beside retrieve(with: any Resource, options:
        // = compute()): a struct or enum converts to the existential only when it conforms, directly
        // or through a refinement. Unrecorded conformances, or a type of another module that this
        // one may extend, may conform.
        var types = TestLibDatabase();
        var target = Method("retrieve", disfavored: false,
            Arg("with", valueType), Arg("options", "Swift.Int", "compute()"));
        var sibling = Method("retrieve", disfavored: false,
            ArgOfSpec("with", "any TestLib.Resource"), Arg("options", "Swift.Int", "compute()"));
        Host(target, sibling);

        var plan = SwiftOverloadCallPinning.Plan(target, arg => arg.HasDefaultArg, types);

        Assert.Equal(refused, plan.IsRefused);
    }

    [Theory]
    [InlineData("TestLib.Source", false)]
    [InlineData("TestLib.Box", true)]
    public void Existential_ReachesNoValueType(string siblingType, bool refused)
    {
        // retrieve(with: any Resource, options: = compute()) beside retrieve(with: Source, ...): no
        // implicit conversion opens an existential into an enum, while a class may be its
        // protocol's superclass bound.
        var types = TestLibDatabase();
        var target = Method("retrieve", disfavored: false,
            ArgOfSpec("with", "any TestLib.Resource"), Arg("options", "Swift.Int", "compute()"));
        var sibling = Method("retrieve", disfavored: false,
            Arg("with", siblingType), Arg("options", "Swift.Int", "compute()"));
        Host(target, sibling);

        var plan = SwiftOverloadCallPinning.Plan(target, arg => arg.HasDefaultArg, types);

        Assert.Equal(refused, plan.IsRefused);
    }

    private static TypeDatabase TestLibDatabase()
    {
        var types = new TypeDatabase();
        var testLib = new ModuleTypeDatabase("TestLib", "/tmp/TestLib.dylib");
        void Register(ModuleTypeDatabase db, string module, string name, TypeRecordKind kind, params string[]? conformances)
        {
            var swiftName = SwiftTypeName.FromModuleQualifiedName($"{module}.{name}");
            db.RegisterType(swiftName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(module, name),
                SwiftTypeName = swiftName,
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.None,
                Kind = kind,
                ProtocolConformances = conformances?.Select(SwiftTypeName.FromModuleQualifiedName).ToList(),
            });
        }
        Register(testLib, "TestLib", "Resource", TypeRecordKind.Protocol, "Swift.Sendable");
        Register(testLib, "TestLib", "RefinedResource", TypeRecordKind.Protocol, "TestLib.Resource");
        Register(testLib, "TestLib", "Source", TypeRecordKind.Enum, "Swift.Sendable", "Swift.Hashable");
        Register(testLib, "TestLib", "ImageResource", TypeRecordKind.Struct, "TestLib.Resource");
        Register(testLib, "TestLib", "RefinedConformer", TypeRecordKind.Struct, "TestLib.RefinedResource");
        Register(testLib, "TestLib", "Unrecorded", TypeRecordKind.Struct, null);
        Register(testLib, "TestLib", "Box", TypeRecordKind.Class);
        types.AddModuleDatabase(testLib);
        var foundation = new ModuleTypeDatabase("Foundation", "/tmp/Foundation.dylib");
        Register(foundation, "Foundation", "URL", TypeRecordKind.Struct);
        types.AddModuleDatabase(foundation);
        return types;
    }

    [Fact]
    public void SpecializedCopy_IsNotItsOwnRival()
    {
        // A synthesized copy of the target (rewritten symbol, substituted types) is resolved
        // through SpecializedFrom, so the original declaration in the member list never competes
        // with the call it is the target of.
        var original = Method("create", disfavored: false,
            Arg("grid", "Swift.String"), Arg("options", "TestLib.Options", "[]"), Arg("using", "τ_0_0", generic: true));
        Host(original);
        var specialized = original with
        {
            MangledName = "SBW_CSM_specialized",
            SpecializedFrom = original,
            CSSignature = new List<ArgumentDecl>
            {
                original.CSSignature[0], original.CSSignature[1], original.CSSignature[2],
                original.CSSignature[3] with { SwiftTypeSpec = new NamedTypeSpec("TestLib.Column"), IsGeneric = false },
            },
        };

        var plan = SwiftOverloadCallPinning.Plan(specialized, arg => arg.HasDefaultArg, typeDatabase: null);

        Assert.Same(SwiftCallPinPlan.Unchanged, plan);
    }

    private static List<ArgumentDecl> WithResult(string resultType, params ArgumentDecl[] args)
    {
        var signature = new List<ArgumentDecl>
        {
            new() { Name = "", PrivateName = "", SwiftTypeSpec = new NamedTypeSpec(resultType), IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null },
        };
        signature.AddRange(args);
        return signature;
    }

    private static ArgumentDecl ArgOfSpec(string label, string type)
    {
        var arg = Arg(label, "Swift.Int");
        arg.SwiftTypeSpec = TypeSpecParser.Parse(type)!;
        return arg;
    }

    private static ArgumentDecl Arg(
        string label, string type, string? defaultExpr = null, bool generic = false, bool? hasDefault = null) => new()
        {
            Name = label,
            PrivateName = label,
            SwiftTypeSpec = new NamedTypeSpec(type),
            HasDefaultArg = hasDefault ?? defaultExpr != null,
            SwiftDefaultExpression = defaultExpr,
            IsInOut = false,
            IsGeneric = generic,
            ParentDecl = null,
            ModuleDecl = null,
        };

    private static int _symbol;

    private static MethodDecl Method(string name, bool disfavored, params ArgumentDecl[] args)
    {
        var signature = new List<ArgumentDecl>
        {
            new() { Name = "", PrivateName = "", SwiftTypeSpec = TupleTypeSpec.Empty, IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null },
        };
        signature.AddRange(args);
        return new MethodDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s7TestLib4HostC{name}{Interlocked.Increment(ref _symbol)}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            CSSignature = signature,
            AvailabilityAnnotations = null,
            IsDisfavoredOverload = disfavored,
        };
    }

    private static ClassDecl Host(params MethodDecl[] methods)
    {
        var host = new ClassDecl
        {
            Name = "Host",
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestLib.Host"),
            MangledName = "",
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = methods.ToList(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            AvailabilityAnnotations = null,
            IsFinal = true,
        };
        foreach (var method in methods)
            method.ParentDecl = host;
        return host;
    }
}
