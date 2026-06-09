// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.IO;
using System.Linq;
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
    // ConstructorHandler routes through WrapperSymbolContractGate.HandleViolation,
    // which is responsible for both the on-disk evidence (// Unsupported …
    // marker) and the structured BindingReport entry. These tests pin that
    // observable contract so a regression in any of the three sites surfaces
    // here rather than in a downstream cogating audit.
    // -----------------------------------------------------------------------

    [Fact]
    public void HandleViolation_Records_MissingWrapperSymbol_Skip_And_Emits_Marker()
    {
        var moduleDecl = TestModelFactory.CreateModuleDecl("StripeReproModule");
        var classDecl = (ClassDecl)moduleDecl.Types[0];
        var method = TestModelFactory.CreateMethod(
            "create",
            parent: classDecl,
            args: new[] { ("intentConfiguration", "Swift.String") },
            mangledName: "SBW_StripeReproModule_FlowController_create_xyz");
        var typeDb = new SimpleTypeDatabase();
        var env = new MethodEnvironment(method, typeDb);
        var sw = new System.IO.StringWriter();
        var csWriter = new CSharpWriter(sw);

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        try
        {
            var ex = new WrapperSymbolContractException(
                "SBW_StripeReproModule_FlowController_create_xyz",
                "PInvoke_create");
            WrapperSymbolContractGate.HandleViolation(env, ex, csWriter, NullLogger.Instance);

            var report = ReportCollector.Complete();
            Assert.NotNull(report);
            var skipped = Assert.Single(report!.SkippedItems);
            Assert.Equal(SkipReason.MissingWrapperSymbol, skipped.Reason);
            Assert.Equal("create", skipped.Name);
            Assert.Contains("SBW_StripeReproModule_FlowController_create_xyz", skipped.Details ?? "");

            var output = sw.ToString();
            Assert.Contains("// Unsupported: method 'create'", output);
            Assert.Contains("SBW_StripeReproModule_FlowController_create_xyz", output);
            Assert.DoesNotContain("LibraryImport", output);
            Assert.DoesNotContain("EntryPoint", output);
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void HandleViolation_OverloadDistinct_RecordsBothSkips()
    {
        // The Stripe FlowController shape: three create overloads all skipped
        // for MissingWrapperSymbol must each appear in SkippedItems — overload
        // collapse here would silently mask 2/3 missing public APIs.
        var moduleDecl = TestModelFactory.CreateModuleDecl("StripeReproModule");
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
            WrapperSymbolContractGate.HandleViolation(
                new MethodEnvironment(createIntent, typeDb),
                new WrapperSymbolContractException("SBW_create_intent", "PInvoke_create"),
                csWriter, NullLogger.Instance);
            WrapperSymbolContractGate.HandleViolation(
                new MethodEnvironment(createSetup, typeDb),
                new WrapperSymbolContractException("SBW_create_setup", "PInvoke_create"),
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

    [Fact]
    public void EmitPInvoke_Inline_Throws_When_Wrapper_Symbol_Missing()
    {
        // Verify FormatDeclarationLines refuses to emit even one P/Invoke
        // declaration line when the wrapper-symbol contract trips. Catching
        // the exception and inspecting csWriter shows nothing was written —
        // the throw fires before any emit, so MethodHandler's catch sees a
        // clean writer (apart from any wrapper-emit output that ran earlier,
        // which the cogater strips).
        var ctx = new ModuleEmissionContext();
        var info = MakeInfo("SBW_unregistered_wrapper", ctx, enforce: true);

        Assert.Throws<WrapperSymbolContractException>(
            () => PInvokeEmitHelper.FormatDeclarationLines(info));
    }

    [Fact]
    public void Contract_Violation_CoGater_Strips_Orphan_Caller_Body()
    {
        // Asymmetric-skip regression: WrapperEmitter writes the public caller body
        // to the C# buffer BEFORE PInvokeEmitter runs the contract check. When the
        // check throws, the P/Invoke decl is never emitted but the caller body has
        // already landed in the file. Without the contract-violation CoGater pass,
        // the file references a P/Invoke that does not exist and fails to compile.
        //
        // This test pins the recovery path: HandleViolation records the rejected
        // P/Invoke method name on ModuleEmissionContext, Program.cs feeds it to
        // CSharpWrapperCoGater via preStrippedPInvokeNames, and the orphan caller
        // is stripped from the final source.
        var ctx = new ModuleEmissionContext();
        ctx.RecordContractViolation(
            entryPoint: "SBW_Mapper_orphan_xyz",
            pInvokeName: "PInvoke_orphan_xyz",
            containingType: "Mapper");

        Assert.Contains("PInvoke_orphan_xyz", ctx.ContractViolatedPInvokeNames);
        Assert.Contains("SBW_Mapper_orphan_xyz", ctx.ContractViolatedEntryPoints);
        Assert.True(ctx.ContractViolatedPInvokeScopes["PInvoke_orphan_xyz"].Contains("Mapper"));

        // Fixture mirrors the post-emit shape that triggered the regression: orphan
        // caller body present, no [LibraryImport] decl in source for the rejected
        // P/Invoke, a sibling kept method + decl as control. The kept method must
        // survive untouched so we know the cogater isn't over-stripping.
        const string source = """
            namespace TestModule
            {
                public class Mapper
                {
                    public static void Orphan(System.IntPtr handle)
                    {
                        Mapper_PInvoke.PInvoke_orphan_xyz(handle);
                    }
                    public static void Kept(System.IntPtr handle)
                    {
                        Mapper_PInvoke.PInvoke_kept_abc(handle);
                    }
                }
                internal static partial class Mapper_PInvoke
                {
                    [LibraryImport("libTest.dylib", EntryPoint = "SBW_Mapper_kept_abc")]
                    internal static partial void PInvoke_kept_abc(System.IntPtr handle);
                }
            }
            """;

        var result = CSharpWrapperCoGater.Process(
            source,
            strippedSymbols: new HashSet<string>(),
            preStrippedPInvokeNamesWithScopes: ctx.ContractViolatedPInvokeScopes);

        Assert.True(result.ContentChanged, $"Expected cogater to strip orphan caller. Output:\n{result.Content}");
        Assert.DoesNotContain("PInvoke_orphan_xyz", result.Content);
        Assert.DoesNotContain("public static void Orphan", result.Content);
        // Control members must survive — the cogater must scope its strip to the
        // rejected name only.
        Assert.Contains("public static void Kept", result.Content);
        Assert.Contains("PInvoke_kept_abc", result.Content);
    }

    [Fact]
    public void Contract_Violation_CoGater_Scoped_Strips_Violated_And_Preserves_Kept_SameName()
    {
        // Scope-aware orphan strip: the violated scope ("Mapper") and the kept scope
        // ("Other") both reference a P/Invoke named "PInvoke_eq" in the same file. The
        // violated reference is the asymmetric-skip orphan (no decl, only the caller);
        // the kept scope has both the emitted partial decl and a valid caller. A
        // file-wide strip would remove BOTH callers and break "Other"'s compilation.
        // RecordContractViolation captures the violated containing type so the cogater
        // restricts the strip to that scope only.
        const string source = """
            namespace TestModule
            {
                public class Mapper
                {
                    public static bool Orphan(System.IntPtr a, System.IntPtr b)
                    {
                        return Mapper_PInvoke.PInvoke_eq(a, b);
                    }
                }
                public class Other
                {
                    public static bool Kept(System.IntPtr a, System.IntPtr b)
                    {
                        return Other_PInvoke.PInvoke_eq(a, b);
                    }
                }
                internal static partial class Other_PInvoke
                {
                    [LibraryImport("libTest.dylib", EntryPoint = "SBW_Other_eq")]
                    internal static partial bool PInvoke_eq(System.IntPtr a, System.IntPtr b);
                }
            }
            """;

        var ctx = new ModuleEmissionContext();
        ctx.RecordContractViolation(
            entryPoint: "SBW_Mapper_eq",
            pInvokeName: "PInvoke_eq",
            containingType: "Mapper");

        var result = CSharpWrapperCoGater.Process(
            source,
            strippedSymbols: new HashSet<string>(),
            preStrippedPInvokeNamesWithScopes: ctx.ContractViolatedPInvokeScopes);

        Assert.True(result.ContentChanged,
            $"Expected cogater to strip orphan caller in violated scope. Output:\n{result.Content}");
        // Violated scope: orphan caller stripped.
        Assert.DoesNotContain("public static bool Orphan", result.Content);
        // Kept scope: decl + caller untouched (its callsite still references PInvoke_eq).
        Assert.Contains("public static bool Kept", result.Content);
        Assert.Contains("EntryPoint = \"SBW_Other_eq\"", result.Content);
        Assert.Contains("internal static partial bool PInvoke_eq", result.Content);
    }

    [Fact]
    public void Contract_Violation_CoGater_Scopeless_Falls_Back_To_Collision_Guard()
    {
        // Defensive fallback path: if a caller of the contract-violation API supplies
        // no scope info (empty containing-type set), the cogater treats the strip as
        // file-wide and defers to the collision guard — same behaviour as a callsite
        // built via the legacy HashSet-only entry point. With the kept-scope decl
        // present in the same file, the strip must be suppressed entirely (preserving
        // pre-scope-aware behavior).
        const string source = """
            namespace TestModule
            {
                public class Mapper
                {
                    public static bool Compare(System.IntPtr a, System.IntPtr b)
                    {
                        return Other_PInvoke.PInvoke_eq(a, b);
                    }
                }
                internal static partial class Other_PInvoke
                {
                    [LibraryImport("libTest.dylib", EntryPoint = "SBW_Other_eq")]
                    internal static partial bool PInvoke_eq(System.IntPtr a, System.IntPtr b);
                }
            }
            """;

        var preStripped = new HashSet<string> { "PInvoke_eq" };
        var result = CSharpWrapperCoGater.Process(
            source,
            strippedSymbols: new HashSet<string>(),
            preStrippedPInvokeNames: preStripped);

        Assert.False(result.ContentChanged,
            $"Expected scope-collision guard to suppress strip. Output:\n{result.Content}");
        Assert.Contains("PInvoke_eq", result.Content);
        Assert.Contains("public static bool Compare", result.Content);
    }

    [Fact]
    public void CoGater_With_No_Contract_Violations_Is_NoOp()
    {
        // Empty preStrippedPInvokeNames must not trigger a rewrite. Guards against a
        // future change that would unconditionally invoke the contract-violation pass
        // and accidentally strip well-formed callers.
        const string source = """
            namespace TestModule
            {
                public class Mapper
                {
                    public static void Kept(System.IntPtr handle)
                    {
                        Mapper_PInvoke.PInvoke_kept_abc(handle);
                    }
                }
                internal static partial class Mapper_PInvoke
                {
                    [LibraryImport("libTest.dylib", EntryPoint = "SBW_Mapper_kept_abc")]
                    internal static partial void PInvoke_kept_abc(System.IntPtr handle);
                }
            }
            """;

        var result = CSharpWrapperCoGater.Process(
            source,
            strippedSymbols: new HashSet<string>(),
            preStrippedPInvokeNames: new HashSet<string>());

        Assert.False(result.ContentChanged);
        Assert.Empty(result.StrippedMembers);
    }

    [Fact]
    public void BuildContainingTypePath_Uses_Emitted_CSharpName_Not_Swift_Name()
    {
        // Scope-string parity: CSharpWrapperCoGater.BuildLineToTypeMap reads emitted
        // C# class names from the generated source. WrapperSymbolContractGate must
        // produce scope paths that match — using TypeDecl.Name directly would diverge
        // whenever NameProvider PascalCases (e.g., "myMapper" → "MyMapper") or a
        // TypeRecord rename overrides (e.g., "Connection" → "ConnectionType") shifts
        // the emitted class name. A mismatch silently drops the orphan caller from
        // the scope-restricted strip, reintroducing the asymmetric-skip compile error.
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");

        // Case 1: TypeRecord rename overrides the Swift name entirely.
        var renamedSwift = SwiftTypeName.FromModuleQualifiedName("TestModule.Connection");
        module.RegisterType(renamedSwift, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ConnectionType"),
            SwiftTypeName = renamedSwift,
            MetadataAccessor = "$s10TestModule10ConnectionVMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct,
        });

        // Case 2: nested type where the record stores a composite path —
        // BuildContainingTypePath must take the last dotted segment.
        var nestedSwift = SwiftTypeName.FromModuleQualifiedName("TestModule.Outer.Inner");
        var outerSwift = SwiftTypeName.FromModuleQualifiedName("TestModule.Outer");
        module.RegisterType(outerSwift, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Outer"),
            SwiftTypeName = outerSwift,
            MetadataAccessor = "$s10TestModule5OuterVMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct,
        });
        module.RegisterType(nestedSwift, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Outer.RenamedInner"),
            SwiftTypeName = nestedSwift,
            MetadataAccessor = "$s10TestModule5OuterV5InnerVMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct,
        });

        typeDatabase.AddModuleDatabase(module);

        var moduleDecl = new ModuleDecl
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

        StructDecl MakeStruct(string swiftName, SwiftTypeName swiftTypeName, BaseDecl? parent) => new()
        {
            Name = swiftName,
            SwiftTypeName = swiftTypeName,
            IsFrozen = true,
            MetadataAccessor = "$sFakeMa",
            MangledName = "$sFake",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Conformances = new List<TypeConformance>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            ParentDecl = parent,
            ModuleDecl = moduleDecl,
        };

        // Case 1: lookup hits the renamed record → "ConnectionType", not "Connection".
        var renamedDecl = MakeStruct("Connection", renamedSwift, moduleDecl);
        var renamedPath = WrapperSymbolContractGate.BuildContainingTypePath(renamedDecl, typeDatabase);
        Assert.Equal("ConnectionType", renamedPath);

        // Case 2: nested type — emitted path uses composite leaf segments.
        var outerDecl = MakeStruct("Outer", outerSwift, moduleDecl);
        var nestedDecl = MakeStruct("Inner", nestedSwift, outerDecl);
        var nestedPath = WrapperSymbolContractGate.BuildContainingTypePath(nestedDecl, typeDatabase);
        Assert.Equal("Outer.RenamedInner", nestedPath);

        // Case 3: no registered record → PascalCase fallback on the Swift name,
        // mirroring ModuleEmitter's default. A bare camelCase Swift type name must
        // not appear verbatim in the scope path.
        var unregisteredSwift = SwiftTypeName.FromModuleQualifiedName("TestModule.myMapper");
        var unregisteredDecl = MakeStruct("myMapper", unregisteredSwift, moduleDecl);
        var pascalPath = WrapperSymbolContractGate.BuildContainingTypePath(unregisteredDecl, typeDatabase);
        Assert.Equal("MyMapper", pascalPath);
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
            Visibility = Visibility.Public
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
