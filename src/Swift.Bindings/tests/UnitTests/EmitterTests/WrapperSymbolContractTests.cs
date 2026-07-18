// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Layer A unit tests for the wrapper-symbol contract enforced inside
/// <see cref="PInvokeEmitHelper.FormatDeclarationLines"/>.
/// </summary>
/// <remarks>
/// The contract has three orthogonal dimensions and these tests pin each:
/// <list type="bullet">
///   <item>Entry-point shape — <c>SBW_…</c> (Cdecl) and <c>SBSW_…</c> (Swift CC)
///   wrapper symbols participate; direct Swift mangled names (<c>$s…</c>) and
///   ObjC selectors are out of scope.</item>
///   <item>Calling convention — enforced for both shapes via the resolved
///   pairing: <see cref="PInvokeCallingConvention.Cdecl"/> + SBW_ and
///   <see cref="PInvokeCallingConvention.Swift"/> + SBSW_. Swift CC P/Invokes
///   that target raw <c>$s…</c> symbols (not wrappers) remain out of scope.</item>
///   <item>Opt-in via <see cref="PInvokeEmissionInfo.EnforceWrapperContract"/> +
///   <see cref="PInvokeEmissionInfo.EmissionContext"/> — both must be set for
///   the check to fire, so existing call sites stay non-breaking until they
///   migrate.</item>
/// </list>
/// </remarks>
public class WrapperSymbolContractTests
{
    private static PInvokeEmissionInfo MakeInfo(
        string entryPoint,
        ModuleEmissionContext? ctx = null,
        bool enforce = false,
        PInvokeCallingConvention cc = PInvokeCallingConvention.Cdecl) =>
        new()
        {
            LibraryPath = "libTest.dylib",
            EntryPoint = entryPoint,
            MethodName = "DoWork",
            ReturnType = "void",
            ParametersString = "",
            CallingConvention = cc,
            EmissionContext = ctx,
            EnforceWrapperContract = enforce
        };

    [Fact]
    public void Throws_When_WrapperSymbol_Missing_From_Context()
    {
        var ctx = new ModuleEmissionContext();
        var info = MakeInfo("SBW_Foo_doWork_xyz", ctx, enforce: true);

        var ex = Assert.Throws<WrapperSymbolContractException>(
            () => PInvokeEmitHelper.FormatDeclarationLines(info));

        Assert.Equal("SBW_Foo_doWork_xyz", ex.EntryPoint);
        Assert.Equal("DoWork", ex.MethodName);
    }

    [Fact]
    public void Passes_When_WrapperSymbol_Registered()
    {
        var ctx = new ModuleEmissionContext();
        Assert.True(ctx.TryAddMethodWrapperSymbol("SBW_Foo_doWork_xyz"));

        var info = MakeInfo("SBW_Foo_doWork_xyz", ctx, enforce: true);

        var lines = PInvokeEmitHelper.FormatDeclarationLines(info);

        Assert.Contains(lines, l => l.Contains("EntryPoint = \"SBW_Foo_doWork_xyz\""));
    }

    [Fact]
    public void Registry_Visible_Via_IsWrapperSymbolRegistered_Across_All_Wrapper_Kinds()
    {
        // The unified registry tracks symbols regardless of which kind-specific
        // TryAdd registered them — the contract check shouldn't depend on the
        // caller knowing which sub-set the symbol came from.
        var ctx = new ModuleEmissionContext();
        Assert.True(ctx.TryAddMethodWrapperSymbol("SBW_Method_a"));
        Assert.True(ctx.TryAddPropertyWrapperSymbol("SBW_Property_b"));
        Assert.True(ctx.TryAddConstructorWrapperSymbol("SBW_Ctor_c"));
        Assert.True(ctx.TryAddMetadataAccessorHelper("SBW_Metadata_d"));

        Assert.True(ctx.IsWrapperSymbolRegistered("SBW_Method_a"));
        Assert.True(ctx.IsWrapperSymbolRegistered("SBW_Property_b"));
        Assert.True(ctx.IsWrapperSymbolRegistered("SBW_Ctor_c"));
        Assert.True(ctx.IsWrapperSymbolRegistered("SBW_Metadata_d"));
        Assert.False(ctx.IsWrapperSymbolRegistered("SBW_Never_Registered"));
    }

    [Fact]
    public void Skips_NonWrapper_EntryPoint_Even_When_Enforced()
    {
        // Direct Swift mangled symbols ($s…) bypass wrapper-emit entirely; the
        // contract check must not fire for them, otherwise we'd reject every
        // legitimate WrapperStrategy.None call. The $s prefix pairs exclusively
        // with CallConvSwift — SelectCallingConvention throws on $s + Cdecl by design.
        var ctx = new ModuleEmissionContext();
        var info = MakeInfo("$s4Test6doWorkyyF", ctx, enforce: true,
            cc: PInvokeCallingConvention.Swift);

        var lines = PInvokeEmitHelper.FormatDeclarationLines(info);

        Assert.Contains(lines, l => l.Contains("EntryPoint = \"$s4Test6doWorkyyF\""));
    }

    [Fact]
    public void Coerces_DollarS_With_CallingConvention_Cdecl_To_Swift()
    {
        // $s… is reserved for Swift CC. Pairing it with CallConvCdecl reads register
        // state under the wrong ABI for the implicit self / metadata / error registers
        // Swift relies on, which produced the 0.10.0 mangled-symbol desync bug.
        // The pairing helper silently coerces to CallConvSwift — combined with the
        // post-emit EntryPointCallConvPairingTests reflection audit, this makes the
        // desync impossible to ship even when an upstream gate leaves the convention
        // at its Cdecl default.
        var info = MakeInfo("$s4Test6doWorkyyF", ctx: null, enforce: false,
            cc: PInvokeCallingConvention.Cdecl);

        var lines = PInvokeEmitHelper.FormatDeclarationLines(info);

        Assert.Contains(lines, l => l.Contains("CallConvSwift"));
        Assert.DoesNotContain(lines, l => l.Contains("CallConvCdecl"));
    }

    [Fact]
    public void Throws_When_SBW_Paired_With_CallingConvention_Swift()
    {
        // SBW_ is reserved for the @_cdecl wrapper convention. Pairing it with
        // CallConvSwift contradicts the (entry-point prefix → calling-convention)
        // invariant enforced by PInvokeEmitHelper.SelectCallingConvention, so the
        // helper must throw at construction time rather than emit a mismatched
        // declaration that reads register state under the wrong ABI at runtime.
        // The legal pairings are SBW_ ↔ Cdecl and SBSW_ (or any non-SBW_ prefix) ↔ Swift.
        var info = MakeInfo("SBW_Looks_Like_Wrapper", ctx: null, enforce: false,
            cc: PInvokeCallingConvention.Swift);

        var ex = Assert.Throws<InvalidOperationException>(
            () => PInvokeEmitHelper.FormatDeclarationLines(info));

        Assert.Contains("SBW_Looks_Like_Wrapper", ex.Message);
        Assert.Contains("SBSW_", ex.Message);
    }

    [Fact]
    public void Does_Not_Enforce_When_Contract_Not_Enabled()
    {
        // Existing call sites that haven't migrated yet must keep emitting
        // — the contract is opt-in until every chokepoint flips.
        var ctx = new ModuleEmissionContext();
        var info = MakeInfo("SBW_Unregistered", ctx, enforce: false);

        var lines = PInvokeEmitHelper.FormatDeclarationLines(info);

        Assert.Contains(lines, l => l.Contains("EntryPoint = \"SBW_Unregistered\""));
    }

    [Fact]
    public void Does_Not_Enforce_When_EmissionContext_Null()
    {
        // EnforceWrapperContract is only meaningful with a context to consult.
        // Without one, the helper must pass through (no false positives).
        var info = MakeInfo("SBW_Unregistered", ctx: null, enforce: true);

        var lines = PInvokeEmitHelper.FormatDeclarationLines(info);

        Assert.Contains(lines, l => l.Contains("EntryPoint = \"SBW_Unregistered\""));
    }

    [Fact]
    public void IsWrapperEntryPoint_Recognizes_SBW_Prefix_Only()
    {
        Assert.True(PInvokeEmitHelper.IsWrapperEntryPoint("SBW_anything"));
        Assert.False(PInvokeEmitHelper.IsWrapperEntryPoint("$s4Test6doWorkyyF"));
        Assert.False(PInvokeEmitHelper.IsWrapperEntryPoint("doWork:"));
        Assert.False(PInvokeEmitHelper.IsWrapperEntryPoint(""));
    }

    [Fact]
    public void Same_Symbol_Across_Multiple_Wrapper_Kinds_Rejects_Second()
    {
        // Two emitters racing to claim the same C @_cdecl symbol (e.g. MethodWrapperEmitter
        // running over a synthetic protocol-extension MethodDecl AND ProtocolExtensionEmitter
        // emitting its own wrapper for the same conformance) would produce two `@_cdecl`
        // annotations pointing at one C name — swiftc rejects with "multiple definitions
        // of symbol" at link time. The unified registry catches this cross-kind collision
        // so the second caller skips its emission.
        var ctx = new ModuleEmissionContext();
        Assert.True(ctx.TryAddMethodWrapperSymbol("SBW_shared"));
        Assert.False(ctx.TryAddPropertyWrapperSymbol("SBW_shared"));

        Assert.True(ctx.IsWrapperSymbolRegistered("SBW_shared"));
        Assert.Single(ctx.RegisteredWrapperSymbols, s => s == "SBW_shared");
    }

    // -----------------------------------------------------------------------
    // Layer B — integration shape: when wrapper-emit silently fails to
    // register an SBW_… symbol, the in-band check throws
    // WrapperSymbolContractException; the catch path in MethodHandler /
    // ConstructorHandler routes through WrapperSymbolContractGate.HandleSkip,
    // which is responsible for both the on-disk evidence (// Unsupported …
    // marker) and the structured BindingReport entry. These tests pin that
    // observable contract so a regression in any of the three sites surfaces
    // here rather than in a downstream audit.
    // -----------------------------------------------------------------------

    [Fact]
    public void HandleSkip_Records_MissingWrapperSymbol_Skip_And_Emits_Marker()
    {
        var moduleDecl = TestModelFactory.CreateModuleDecl("PaymentSdkReproModule");
        var classDecl = (ClassDecl)moduleDecl.Types[0];
        var method = TestModelFactory.CreateMethod(
            "create",
            parent: classDecl,
            args: new[] { ("intentConfiguration", "Swift.String") },
            mangledName: "SBW_PaymentSdkReproModule_FlowController_create_xyz");
        var typeDb = new SimpleTypeDatabase();
        var env = new MethodEnvironment(method, typeDb);
        var sw = new System.IO.StringWriter();
        var csWriter = new CSharpWriter(sw);

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        try
        {
            WrapperSymbolContractGate.HandleSkip(
                env, "SBW_PaymentSdkReproModule_FlowController_create_xyz", csWriter, NullLogger.Instance);

            var report = ReportCollector.Complete();
            Assert.NotNull(report);
            var skipped = Assert.Single(report!.SkippedItems);
            Assert.Equal(SkipReason.MissingWrapperSymbol, skipped.Reason);
            Assert.Equal("create", skipped.Name);
            Assert.Contains("SBW_PaymentSdkReproModule_FlowController_create_xyz", skipped.Details ?? "");

            var output = sw.ToString();
            // Finding 53: the comment qualifies the member by its declaring type (Loader.create).
            Assert.Contains("// Unsupported: method 'Loader.create'", output);
            Assert.Contains("SBW_PaymentSdkReproModule_FlowController_create_xyz", output);
            Assert.DoesNotContain("LibraryImport", output);
            Assert.DoesNotContain("EntryPoint", output);
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void HandleSkip_OverloadDistinct_RecordsBothSkips()
    {
        // Three `create` overloads all skipped for MissingWrapperSymbol must each
        // appear in SkippedItems — overload collapse here would silently mask
        // 2/3 missing public APIs.
        var moduleDecl = TestModelFactory.CreateModuleDecl("PaymentSdkReproModule");
        var classDecl = (ClassDecl)moduleDecl.Types[0];
        var createIntent = TestModelFactory.CreateMethod(
            "create",
            parent: classDecl,
            args: new[] { ("intentConfiguration", "Swift.String") },
            mangledName: "SBW_create_intent");
        var createSetup = TestModelFactory.CreateMethod(
            "create",
            parent: classDecl,
            args: new[] { ("setupIntentClientSecret", "Swift.String") },
            mangledName: "SBW_create_setup");
        var typeDb = new SimpleTypeDatabase();
        var sw = new System.IO.StringWriter();
        var csWriter = new CSharpWriter(sw);

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        try
        {
            WrapperSymbolContractGate.HandleSkip(
                new MethodEnvironment(createIntent, typeDb),
                "SBW_create_intent",
                csWriter, NullLogger.Instance);
            WrapperSymbolContractGate.HandleSkip(
                new MethodEnvironment(createSetup, typeDb),
                "SBW_create_setup",
                csWriter, NullLogger.Instance);

            var report = ReportCollector.Complete();
            Assert.NotNull(report);
            Assert.Equal(2, report!.SkippedItems.Count);
            Assert.All(report.SkippedItems, item =>
                Assert.Equal(SkipReason.MissingWrapperSymbol, item.Reason));
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    // -----------------------------------------------------------------------
    // Transactional rollback (in-emission orphan removal).
    //
    // The method/bridge sites cannot predict a contract violation before writing
    // the public body (async @_cdecl wrappers register their symbol *inside*
    // EmitMethod), so they checkpoint the C# writer, emit, and roll the orphan
    // back out on the eager throw. These tests pin the CSharpWriter rollback
    // primitive and the rollback+HandleSkip composition that replaces the old
    // generate-then-regex-strip recovery.
    // -----------------------------------------------------------------------

    [Fact]
    public void Checkpoint_RollbackTo_Truncates_Buffer_And_Restores_Indent()
    {
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);
        csWriter.Indent = 1;
        csWriter.WriteLine("kept();");
        var checkpoint = csWriter.Checkpoint();

        csWriter.Indent = 3;
        csWriter.WriteLine("discarded_a();");
        csWriter.WriteLine("discarded_b();");

        csWriter.RollbackTo(checkpoint);

        var afterRollback = sw.ToString();
        Assert.Contains("kept();", afterRollback);
        Assert.DoesNotContain("discarded_a();", afterRollback);
        Assert.DoesNotContain("discarded_b();", afterRollback);
        Assert.Equal(1, csWriter.Indent);

        // The writer stays usable after rollback: subsequent content appends at the
        // checkpoint, not after the discarded text.
        csWriter.WriteLine("after();");
        var afterAppend = sw.ToString();
        Assert.Contains("after();", afterAppend);
        Assert.DoesNotContain("discarded", afterAppend);
    }

    [Fact]
    public void RollbackTo_Checkpoint_At_Current_Position_Is_NoOp()
    {
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);
        csWriter.WriteLine("content();");
        var checkpoint = csWriter.Checkpoint();

        csWriter.RollbackTo(checkpoint);

        Assert.Contains("content();", sw.ToString());
    }

    [Fact]
    public void ContractSkip_RollbackThenHandleSkip_Removes_Orphan_Body_Leaves_Marker()
    {
        // Mirrors the MethodHandler method-site sequence: checkpoint before EmitMethod,
        // write the public member body, then on the contract throw roll the orphan back
        // out and emit the skip marker. The orphan caller (which references an unresolved
        // P/Invoke) must NOT survive; only the // Unsupported marker remains. Before the
        // in-emission rollback this body was left in the buffer for a downstream text
        // strip — this test pins that the recovery is now transactional.
        var moduleDecl = TestModelFactory.CreateModuleDecl("ReproModule");
        var classDecl = (ClassDecl)moduleDecl.Types[0];
        var method = TestModelFactory.CreateMethod(
            "doWork",
            parent: classDecl,
            args: new[] { ("value", "Swift.String") },
            mangledName: "SBW_ReproModule_Loader_doWork_xyz");
        var typeDb = new SimpleTypeDatabase();
        var env = new MethodEnvironment(method, typeDb);

        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        try
        {
            var checkpoint = csWriter.Checkpoint();
            // Simulate the orphan public member EmitMethod would have written before the
            // eager contract throw fired inside EmitPInvoke.
            csWriter.WriteLine("public void DoWork(string value)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("NativeMethods.PInvoke_doWork(value);");
            csWriter.Indent--;
            csWriter.WriteLine("}");

            // Contract throw → catch: roll the orphan out, then record the skip.
            csWriter.RollbackTo(checkpoint);
            WrapperSymbolContractGate.HandleSkip(
                env, "SBW_ReproModule_Loader_doWork_xyz", csWriter, NullLogger.Instance);

            var output = sw.ToString();
            Assert.DoesNotContain("PInvoke_doWork", output);
            Assert.DoesNotContain("public void DoWork", output);
            Assert.Contains("// Unsupported", output);
            Assert.Contains("SBW_ReproModule_Loader_doWork_xyz", output);

            var report = ReportCollector.Complete();
            Assert.NotNull(report);
            var skipped = Assert.Single(report!.SkippedItems);
            Assert.Equal(SkipReason.MissingWrapperSymbol, skipped.Reason);
            Assert.Equal("doWork", skipped.Name);
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void EmitPInvoke_Inline_Throws_When_Wrapper_Symbol_Missing()
    {
        // Verify FormatDeclarationLines refuses to emit even one P/Invoke
        // declaration line when the wrapper-symbol contract trips. Catching
        // the exception and inspecting csWriter shows nothing was written —
        // the throw fires before any emit, so MethodHandler's catch sees a
        // clean writer (apart from any wrapper-emit output that ran earlier,
        // which the member-body rollback discards).
        var ctx = new ModuleEmissionContext();
        var info = MakeInfo("SBW_unregistered_wrapper", ctx, enforce: true);

        Assert.Throws<WrapperSymbolContractException>(
            () => PInvokeEmitHelper.FormatDeclarationLines(info));
    }

    [Fact]
    public void Async_Cdecl_Wrapper_Production_Path_Registers_Symbol()
    {
        // Drives a real async @_cdecl method emission through MethodHandler.Emit
        // with a custom ModuleEmissionContext, then asserts the production
        // registration call inside the WrapperEmitter.Async template ran. The
        // async wrapper template bypasses MethodWrapperEmitter, so this is the
        // only path that registers async SBW_… symbols — without it, every
        // async cdecl method falsely trips the wrapper-symbol contract during
        // PInvoke emission. A pre-registered ctx (the prior shape of this test)
        // would mask a regression where the production code stopped registering.
        var (ctx, registeredSymbols, methodDecl, csOutput, swiftOutput) =
            EmitAsyncMethodAndCaptureRegistry();

        // Confirm the production path actually fired (no silent rewrite to a
        // sync surface, no skip via SignatureHandler placeholder gate).
        Assert.True(methodDecl.UsesCdeclMethodWrapper,
            $"Async cdecl-eligibility didn't promote the method. CS:\n{csOutput}\n\nSwift:\n{swiftOutput}");
        Assert.True(methodDecl.WasEmitted,
            $"Method emission short-circuited before reaching the wrapper template. CS:\n{csOutput}");
        Assert.Contains("@_cdecl(", swiftOutput);

        Assert.NotEmpty(registeredSymbols);
        // Filter to the async method symbol shape rather than asserting against every
        // registered symbol: the emission path may legitimately register auxiliary
        // helpers (invoke thunks, free helpers) and asserting on those would tie this
        // test to incidental emitter detail. The async cdecl symbol is what this test
        // pins down — it must appear both as a Swift @_cdecl and a C# EntryPoint.
        var asyncSymbols = registeredSymbols
            .Where(s => s.StartsWith("SBW_", StringComparison.Ordinal)
                     && s.EndsWith("_async", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(asyncSymbols);
        foreach (var sym in asyncSymbols)
        {
            Assert.True(ctx.IsWrapperSymbolRegistered(sym),
                $"Expected '{sym}' to be visible via IsWrapperSymbolRegistered.");
            Assert.Contains($"@_cdecl(\"{sym}\")", swiftOutput);
            Assert.Contains($"EntryPoint = \"{sym}\"", csOutput);
        }
    }

    [Fact]
    public void Production_Path_Leaves_No_Wrapper_Block_The_Contract_Cannot_See()
    {
        // The orphan shape the paired rollback exists to prevent: a @_cdecl block sitting in the
        // wrapper source with no counterpart on the managed side. A C#-only assertion cannot see
        // it, so pin the cross-buffer invariant directly — every wrapper block emitted for this
        // member is registered, which is exactly what makes it visible to the contract and to any
        // later rollback decision.
        var (ctx, _, _, csOutput, swiftOutput) = EmitAsyncMethodAndCaptureRegistry();

        var emittedCdeclSymbols = System.Text.RegularExpressions.Regex
            .Matches(swiftOutput, "@_cdecl\\(\"([^\"]+)\"\\)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(emittedCdeclSymbols);
        foreach (var symbol in emittedCdeclSymbols)
        {
            Assert.True(
                ctx.IsWrapperSymbolRegistered(symbol),
                $"Wrapper block '{symbol}' was written into the Swift buffer but never registered, so " +
                $"nothing downstream can tell it apart from an orphan.\n\nSwift:\n{swiftOutput}\n\nCS:\n{csOutput}");
        }
    }

    [Fact]
    public void ContractTrip_MidMember_LeavesNoWrapperBlockForTheSkippedMember()
    {
        // The rollback pairing, driven through the real emission path rather than a writer
        // fixture. Emission normally promotes the entry point to an SBW_ symbol and marks the
        // method cdecl-wrapped in the same step, which is what makes the symbol get registered;
        // promoting the symbol alone reproduces the state the contract exists to catch — a
        // P/Invoke naming a wrapper symbol that wrapper-emit never registered — so EmitPInvoke
        // throws after the Swift side has already been written.
        var (csOutput, swiftOutput, tripped) = EmitAsyncMethodWithUnregisteredWrapperSymbol();

        // Assert the mechanism, not just the outcome. Without this the test could pass vacuously:
        // a member skipped for any OTHER reason before reaching the transaction never writes Swift,
        // so the buffer assertion below would hold while proving nothing about rollback. `tripped`
        // is taken from the contract gate's own skip diagnostic, so only that path satisfies it.
        Assert.True(tripped,
            $"The wrapper-symbol contract did not trip, so this test proves nothing about rollback.\n\nCS:\n{csOutput}");

        // Semantic assertion: whatever survives in the wrapper source, none of it may be a block
        // for the member whose managed side was just rolled back.
        Assert.DoesNotContain(OrphanProbeSymbol, swiftOutput, StringComparison.Ordinal);
    }

    private const string OrphanProbeSymbol = "SBSW_orphanProbe";

    /// <summary>Captures log messages so a test can assert which emission path ran.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private static (string csOutput, string swiftOutput, bool tripped)
        EmitAsyncMethodWithUnregisteredWrapperSymbol()
    {
        var (moduleDecl, parentDecl, methodDecl, typeDatabase) = BuildAsyncFixture(setAsyncLibraryName: false);
        _ = moduleDecl;
        _ = parentDecl;

        var ctx = new ModuleEmissionContext();
        var context = new TypeHandlerContext(null, new(), null, EmissionContext: ctx);

        var csSw = new StringWriter();
        var swiftSw = new StringWriter();
        var csWriter = new CSharpWriter(csSw);
        var swiftWriter = new SwiftWriter(swiftSw);
        var conductor = new Conductor(new NullLoggerFactory());
        var logger = new CapturingLogger<MethodHandler>();
        var handler = new MethodHandler(logger);
        var env = (MethodEnvironment)handler.Marshal(methodDecl, typeDatabase);

        env.PromoteSymbol(OrphanProbeSymbol);
        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        // The contract gate's skip diagnostic is the only thing that names both the contract and
        // the probe symbol, so it pins the member to THIS path rather than any other skip.
        var trippedTheContract = logger.Messages.Any(m =>
            m.Contains("Wrapper-symbol contract", StringComparison.Ordinal)
            && m.Contains(OrphanProbeSymbol, StringComparison.Ordinal));

        return (csSw.ToString(), swiftSw.ToString(),
            trippedTheContract && !ctx.IsWrapperSymbolRegistered($"{OrphanProbeSymbol}_async"));
    }

    private static (ModuleDecl, StructDecl, MethodDecl, TypeDatabase) BuildAsyncFixture(bool setAsyncLibraryName)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "WSCAsyncTest",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
        var parentDecl = new StructDecl
        {
            Name = "Fetcher",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("WSCAsyncTest.Fetcher"),
            IsFrozen = true,
            MetadataAccessor = "$s12WSCAsyncTest7FetcherVMa",
            MangledName = "$s12WSCAsyncTest7FetcherV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Conformances = new List<TypeConformance>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(parentDecl);

        var methodDecl = new MethodDecl
        {
            Name = "fetchValue",
            MangledName = "$s12WSCAsyncTest7FetcherV10fetchValueSiyYaF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = parentDecl,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = true,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(methodDecl);

        var typeDatabase = new TypeDatabase();
        if (setAsyncLibraryName)
        {
            typeDatabase.AsyncLibraryName = "WSCAsyncTestSwiftBindings";
        }
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        var intName = SwiftTypeName.FromModuleQualifiedName("Swift.Int");
        swiftModule.RegisterType(intName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "nint"),
            SwiftTypeName = intName,
            MetadataAccessor = "$sSiMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        typeDatabase.AddModuleDatabase(swiftModule);
        var module = new ModuleTypeDatabase("WSCAsyncTest", "/fake/path");
        module.RegisterType(parentDecl.SwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("WSCAsyncTest", "Fetcher"),
            SwiftTypeName = parentDecl.SwiftTypeName,
            MetadataAccessor = parentDecl.MetadataAccessor,
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        typeDatabase.AddModuleDatabase(module);

        return (moduleDecl, parentDecl, methodDecl, typeDatabase);
    }

    private static (ModuleEmissionContext ctx, IReadOnlyCollection<string> symbols,
        MethodDecl methodDecl, string csOutput, string swiftOutput)
        EmitAsyncMethodAndCaptureRegistry()
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "WSCAsyncTest",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
        var parentDecl = new StructDecl
        {
            Name = "Fetcher",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("WSCAsyncTest.Fetcher"),
            IsFrozen = true,
            MetadataAccessor = "$s12WSCAsyncTest7FetcherVMa",
            MangledName = "$s12WSCAsyncTest7FetcherV",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Conformances = new List<TypeConformance>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(parentDecl);

        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = parentDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "fetchValue",
            MangledName = "$s12WSCAsyncTest7FetcherV10fetchValueSiyYaF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = true,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(methodDecl);

        var typeDatabase = new TypeDatabase();
        // The async @_cdecl wrapper path is gated on xcframework mode, which is
        // detected by AsyncLibraryName being set. Without it,
        // WrapperValidation.DetermineMethodWrapperDecision returns CannotWrap
        // and the production registration code never runs.
        typeDatabase.AsyncLibraryName = "WSCAsyncTestSwiftBindings";
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        var intName = SwiftTypeName.FromModuleQualifiedName("Swift.Int");
        swiftModule.RegisterType(intName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "nint"),
            SwiftTypeName = intName,
            MetadataAccessor = "$sSiMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        typeDatabase.AddModuleDatabase(swiftModule);
        var module = new ModuleTypeDatabase("WSCAsyncTest", "/fake/path");
        module.RegisterType(parentDecl.SwiftTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("WSCAsyncTest", "Fetcher"),
            SwiftTypeName = parentDecl.SwiftTypeName,
            MetadataAccessor = parentDecl.MetadataAccessor,
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        typeDatabase.AddModuleDatabase(module);

        var ctx = new ModuleEmissionContext();
        var context = new TypeHandlerContext(null, new(), null, EmissionContext: ctx);

        var csSw = new StringWriter();
        var swiftSw = new StringWriter();
        var csWriter = new CSharpWriter(csSw);
        var swiftWriter = new SwiftWriter(swiftSw);
        var conductor = new Conductor(new NullLoggerFactory());
        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = handler.Marshal(methodDecl, typeDatabase);

        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        return (ctx, ctx.RegisteredWrapperSymbols.ToList(), methodDecl, csSw.ToString(), swiftSw.ToString());
    }

    // -----------------------------------------------------------------------
    // Direct-path enforcement: bridge / helper emitters call
    // PInvokeEmitHelper.FormatDeclarationLines outside the canonical
    // PInvokeEmitter chokepoint. These tests pin that the contract fires
    // identically on those paths — an SBW_ symbol registered via
    // TryAddDirectHelperWrapperSymbol must pass, an unregistered one must
    // throw, and the unified registry must be visible to both the direct-
    // path register and the contract check.
    // -----------------------------------------------------------------------

    [Fact]
    public void DirectHelper_Registered_Symbol_Passes_Contract()
    {
        // ThemeBridgeEmitter / SwiftUIBridgeEmitter / GenericClosureBridgeEmitter all
        // register through TryAddDirectHelperWrapperSymbol. The contract check shares
        // the unified registry — registration via the direct-helper kind must satisfy
        // the contract just like TryAddMethodWrapperSymbol does on the canonical path.
        var ctx = new ModuleEmissionContext();
        Assert.True(ctx.TryAddDirectHelperWrapperSymbol("SBW_TestModule_View_Create"));

        var info = MakeInfo("SBW_TestModule_View_Create", ctx, enforce: true);

        var lines = PInvokeEmitHelper.FormatDeclarationLines(info);

        Assert.Contains(lines, l => l.Contains("EntryPoint = \"SBW_TestModule_View_Create\""));
    }

    [Fact]
    public void DirectHelper_Unregistered_Symbol_Trips_Contract()
    {
        // If a direct-helper emitter forgets to call TryAddDirectHelperWrapperSymbol
        // before emitting its P/Invoke (refactor regression), the contract must catch
        // it at compile time rather than letting the build link an unresolved symbol.
        var ctx = new ModuleEmissionContext();
        // Register a DIFFERENT direct-helper symbol so the registry isn't empty —
        // the check must scope to the specific entry point, not "is anything
        // direct-helper registered?".
        Assert.True(ctx.TryAddDirectHelperWrapperSymbol("SBW_TestModule_OtherView_Create"));

        var info = MakeInfo("SBW_TestModule_View_GetViewController", ctx, enforce: true);

        var ex = Assert.Throws<WrapperSymbolContractException>(
            () => PInvokeEmitHelper.FormatDeclarationLines(info));
        Assert.Equal("SBW_TestModule_View_GetViewController", ex.EntryPoint);
    }

    [Fact]
    public void DirectHelper_Theme_Getter_Setter_Pair_Registers_Visibly()
    {
        // Theme bridge emits paired setter/getter SBW_ symbols (e.g.
        // SBW_Theme_set_primaryColor / SBW_Theme_get_primaryColor). Both must be
        // visible to IsWrapperSymbolRegistered without one masking the other.
        var ctx = new ModuleEmissionContext();
        Assert.True(ctx.TryAddDirectHelperWrapperSymbol("SBW_Theme_set_primaryColor"));
        Assert.True(ctx.TryAddDirectHelperWrapperSymbol("SBW_Theme_get_primaryColor"));

        var setterInfo = MakeInfo("SBW_Theme_set_primaryColor", ctx, enforce: true);
        var getterInfo = MakeInfo("SBW_Theme_get_primaryColor", ctx, enforce: true);

        // No throw + entry point line emitted = both visible.
        var setterLines = PInvokeEmitHelper.FormatDeclarationLines(setterInfo);
        var getterLines = PInvokeEmitHelper.FormatDeclarationLines(getterInfo);
        Assert.Contains(setterLines, l => l.Contains("EntryPoint = \"SBW_Theme_set_primaryColor\""));
        Assert.Contains(getterLines, l => l.Contains("EntryPoint = \"SBW_Theme_get_primaryColor\""));
    }

    [Fact]
    public void DirectHelper_SwiftUI_Lifecycle_Family_Unregistered_All_Trip()
    {
        // SwiftUI bridge emits a Create/GetViewController/Free triple per view plus
        // SetLifecycle / SetFrame / PresentAsSheet / etc. Each is registered
        // independently — registering one must NOT make a sibling pass.
        var ctx = new ModuleEmissionContext();
        Assert.True(ctx.TryAddDirectHelperWrapperSymbol("SBW_MyModule_MyView_Create"));

        // Free is a separate registration — must still trip.
        var freeInfo = MakeInfo("SBW_MyModule_MyView_Free", ctx, enforce: true);
        Assert.Throws<WrapperSymbolContractException>(
            () => PInvokeEmitHelper.FormatDeclarationLines(freeInfo));

        // SetLifecycle likewise.
        var setLifecycleInfo = MakeInfo("SBW_MyModule_MyView_SetLifecycle", ctx, enforce: true);
        Assert.Throws<WrapperSymbolContractException>(
            () => PInvokeEmitHelper.FormatDeclarationLines(setLifecycleInfo));
    }

    [Fact]
    public void DirectHelper_GenericClosure_CreateError_Symbol_Visible_To_Contract()
    {
        // SwiftErrorMintEmitter registers exactly one SBW_CreateError_{module}
        // direct helper per module (deduped via TryAddSwiftErrorMintPInvoke).
        // The contract check must see it through the unified registry.
        var ctx = new ModuleEmissionContext();
        Assert.True(ctx.TryAddDirectHelperWrapperSymbol("SBW_CreateError_TestModule"));

        var info = MakeInfo("SBW_CreateError_TestModule", ctx, enforce: true);
        var lines = PInvokeEmitHelper.FormatDeclarationLines(info);

        Assert.Contains(lines, l => l.Contains("EntryPoint = \"SBW_CreateError_TestModule\""));
    }

    [Fact]
    public void ThemeBridge_CSharp_Emission_Trips_Contract_When_Symbol_Unregistered()
    {
        // End-to-end direct-path coverage: drive ThemeBridgeEmitter.GenerateCSharpThemeBridge
        // with a ThemeBridgeInfo whose setter/getter symbols are NOT registered in the
        // ModuleEmissionContext. The Swift-side EmitSwiftThemeSetters/Getters path
        // normally registers them; running only the C# side proves the contract gate is
        // wired correctly through the emission code rather than just through MakeInfo.
        // A regression that drops `EnforceWrapperContract = true` or `EmissionContext`
        // from one of the two EmitDeclaration sites in ThemeBridgeEmitter would make
        // this test return generated source instead of throwing.
        var emptyCtx = new ModuleEmissionContext();
        var info = new ThemeBridgeEmitter.ThemeBridgeInfo(
            ClassName: "MyTheme",
            ModuleName: "TestModule",
            SingletonName: "shared",
            Properties: new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("primaryColor", ThemeBridgeEmitter.ThemePropertyKind.Color),
            });

        Assert.Throws<WrapperSymbolContractException>(
            () => ThemeBridgeEmitter.GenerateCSharpThemeBridge(
                "TestNs", "TestModule",
                new List<ThemeBridgeEmitter.ThemeBridgeInfo> { info },
                emptyCtx));
    }

    [Fact]
    public void ThemeBridge_CSharp_Getter_Site_Independently_Enforces_Contract()
    {
        // The companion ThemeBridge tests above prove the setter EmitDeclaration site
        // is wired — but with an empty context the setter throws first and the getter
        // is never reached. A regression that drops `EmissionContext` or
        // `EnforceWrapperContract` from the getter EmitDeclaration in
        // ThemeBridgeEmitter.cs:633 would slip past both companion tests.
        //
        // Register ONLY the setter so the setter site is satisfied; the getter site is
        // then the first unregistered SBW_ symbol the emit hits. The expected throw's
        // EntryPoint must point at the getter, proving the getter site actually
        // consulted the registry rather than emitting blind.
        var ctx = new ModuleEmissionContext();
        Assert.True(ctx.TryAddDirectHelperWrapperSymbol("SBW_MyTheme_set_primaryColor"));

        var info = new ThemeBridgeEmitter.ThemeBridgeInfo(
            ClassName: "MyTheme",
            ModuleName: "TestModule",
            SingletonName: "shared",
            Properties: new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("primaryColor", ThemeBridgeEmitter.ThemePropertyKind.Color),
            });

        var ex = Assert.Throws<WrapperSymbolContractException>(
            () => ThemeBridgeEmitter.GenerateCSharpThemeBridge(
                "TestNs", "TestModule",
                new List<ThemeBridgeEmitter.ThemeBridgeInfo> { info },
                ctx));
        Assert.Equal("SBW_MyTheme_get_primaryColor", ex.EntryPoint);
    }

    [Fact]
    public void ThemeBridge_CSharp_Emission_Passes_When_Symbols_Registered()
    {
        // Companion to the previous test: with both setter and getter SBW_ symbols
        // pre-registered (mirroring what the Swift-side path does), the C# emit must
        // complete cleanly. Catches the inverse regression — a stray contract gate
        // that rejects properly-registered symbols.
        var ctx = new ModuleEmissionContext();
        Assert.True(ctx.TryAddDirectHelperWrapperSymbol("SBW_MyTheme_set_primaryColor"));
        Assert.True(ctx.TryAddDirectHelperWrapperSymbol("SBW_MyTheme_get_primaryColor"));

        var info = new ThemeBridgeEmitter.ThemeBridgeInfo(
            ClassName: "MyTheme",
            ModuleName: "TestModule",
            SingletonName: "shared",
            Properties: new List<ThemeBridgeEmitter.ThemeProperty>
            {
                new("primaryColor", ThemeBridgeEmitter.ThemePropertyKind.Color),
            });

        var content = ThemeBridgeEmitter.GenerateCSharpThemeBridge(
            "TestNs", "TestModule",
            new List<ThemeBridgeEmitter.ThemeBridgeInfo> { info },
            ctx);

        Assert.Contains("SBW_MyTheme_set_primaryColor", content);
        Assert.Contains("SBW_MyTheme_get_primaryColor", content);
    }

    private sealed class SimpleTypeDatabase : ITypeDatabase
    {
        public string? AsyncLibraryName => null;
        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => false;
        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TypeRecord? record)
        { record = null; return false; }
        public string GetLibraryPath(string moduleName) => $"lib{moduleName}.dylib";
        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }
}
