// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for MarkerProtocolOverloadEmitter — Sendable/Copyable overload generation
/// for marker protocols with primitive conformers.
/// </summary>
[Collection("ReportCollector")]
public class MarkerProtocolOverloadEmitterTests
{
    #region GetPrimitiveConformers

    [Fact]
    public void GetPrimitiveConformers_NullMap_ReturnsEmpty()
    {
        var result = MarkerProtocolOverloadEmitter.GetPrimitiveConformers("Foo", null);
        Assert.Empty(result);
    }

    [Fact]
    public void GetPrimitiveConformers_MissingProtocol_ReturnsEmpty()
    {
        var map = new Dictionary<string, List<string>>
        {
            ["Other"] = new() { "Swift.Int" }
        };
        var result = MarkerProtocolOverloadEmitter.GetPrimitiveConformers("Foo", map);
        Assert.Empty(result);
    }

    [Fact]
    public void GetPrimitiveConformers_KnownPrimitives_ReturnsMapped()
    {
        var map = new Dictionary<string, List<string>>
        {
            ["ConstraintOffsetTarget"] = new() { "Swift.Double", "Swift.Float", "Swift.Int" }
        };
        var result = MarkerProtocolOverloadEmitter.GetPrimitiveConformers("ConstraintOffsetTarget", map);
        Assert.Equal(3, result.Count);
        Assert.Contains(result, c => c.CSharpType == "double" && c.SwiftType == "Swift.Double");
        Assert.Contains(result, c => c.CSharpType == "float" && c.SwiftType == "Swift.Float");
        Assert.Contains(result, c => c.CSharpType == "nint" && c.SwiftType == "Swift.Int");
    }

    [Theory]
    [InlineData("Swift.Double", "double")]
    [InlineData("Swift.Float", "float")]
    [InlineData("Swift.Int", "nint")]
    [InlineData("Swift.UInt", "nuint")]
    [InlineData("CoreFoundation.CGFloat", "nfloat")]
    [InlineData("Swift.Int32", "int")]
    [InlineData("Swift.Int64", "long")]
    [InlineData("Swift.Bool", "bool")]
    public void GetPrimitiveConformers_AllMappedTypes(string swiftType, string expectedCSharp)
    {
        var map = new Dictionary<string, List<string>>
        {
            ["Proto"] = new() { swiftType }
        };
        var result = MarkerProtocolOverloadEmitter.GetPrimitiveConformers("Proto", map);
        Assert.Single(result);
        Assert.Equal(expectedCSharp, result[0].CSharpType);
        Assert.Equal(swiftType, result[0].SwiftType);
    }

    [Fact]
    public void GetPrimitiveConformers_UnknownType_FiltersOut()
    {
        var map = new Dictionary<string, List<string>>
        {
            ["Proto"] = new() { "MyModule.CustomStruct", "Swift.Int" }
        };
        var result = MarkerProtocolOverloadEmitter.GetPrimitiveConformers("Proto", map);
        Assert.Single(result);
        Assert.Equal("nint", result[0].CSharpType);
    }

    #endregion

    #region EmitOverloads gates

    [Fact]
    public void EmitOverloads_NullMap_NoOutput()
    {
        var (csWriter, swiftWriter, output) = CreateWriters();
        var method = CreateBasicMethodDecl("doSomething");

        MarkerProtocolOverloadEmitter.EmitOverloads(
            csWriter, swiftWriter, method,
            CreateMethodEnvironment(method), null, null);

        Assert.Empty(output.ToString());
    }

    [Fact]
    public void EmitOverloads_EmptyMap_NoOutput()
    {
        var (csWriter, swiftWriter, output) = CreateWriters();
        var method = CreateBasicMethodDecl("doSomething");

        MarkerProtocolOverloadEmitter.EmitOverloads(
            csWriter, swiftWriter, method,
            CreateMethodEnvironment(method), null,
            new Dictionary<string, List<string>>());

        Assert.Empty(output.ToString());
    }

    [Fact]
    public void EmitOverloads_Constructor_Skipped()
    {
        var (csWriter, swiftWriter, output) = CreateWriters();
        var method = CreateBasicMethodDecl("init");
        method.IsConstructor = true;

        MarkerProtocolOverloadEmitter.EmitOverloads(
            csWriter, swiftWriter, method,
            CreateMethodEnvironment(method), null,
            new Dictionary<string, List<string>> { ["Proto"] = new() { "Swift.Int" } });

        Assert.Empty(output.ToString());
    }

    [Fact]
    public void EmitOverloads_AsyncMethod_Skipped()
    {
        var (csWriter, swiftWriter, output) = CreateWriters();
        var method = CreateBasicMethodDecl("doAsync");
        method.IsAsync = true;

        MarkerProtocolOverloadEmitter.EmitOverloads(
            csWriter, swiftWriter, method,
            CreateMethodEnvironment(method), null,
            new Dictionary<string, List<string>> { ["Proto"] = new() { "Swift.Int" } });

        Assert.Empty(output.ToString());
    }

    [Fact]
    public void EmitOverloads_ThrowingMethod_Skipped()
    {
        var (csWriter, swiftWriter, output) = CreateWriters();
        var method = CreateBasicMethodDecl("doThrow");
        method.Throws = true;

        MarkerProtocolOverloadEmitter.EmitOverloads(
            csWriter, swiftWriter, method,
            CreateMethodEnvironment(method), null,
            new Dictionary<string, List<string>> { ["Proto"] = new() { "Swift.Int" } });

        Assert.Empty(output.ToString());
    }

    [Fact]
    public void EmitOverloads_GenericParent_Skipped()
    {
        var (csWriter, swiftWriter, output) = CreateWriters();
        var method = CreateBasicMethodDecl("doWork");
        var parentDecl = CreateClassDecl("Container");
        parentDecl.GenericParameters.Add(new GenericArgumentDecl("τ_0_0", "Element", new(), new()));

        MarkerProtocolOverloadEmitter.EmitOverloads(
            csWriter, swiftWriter, method,
            CreateMethodEnvironment(method), parentDecl,
            new Dictionary<string, List<string>> { ["Proto"] = new() { "Swift.Int" } });

        Assert.Empty(output.ToString());
    }

    [Fact]
    public void EmitOverloads_NoMarkerProtocolParam_NoOutput()
    {
        var (csWriter, swiftWriter, output) = CreateWriters();
        var moduleDecl = CreateModuleDecl();
        var method = new MethodDecl
        {
            Name = "doWork",
            MangledName = "$s10TestModule6doWorkyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg("value", new NamedTypeSpec("Swift.Int"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = CreateClassDecl("Widget"),
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };

        MarkerProtocolOverloadEmitter.EmitOverloads(
            csWriter, swiftWriter, method,
            CreateMethodEnvironment(method), null,
            new Dictionary<string, List<string>> { ["OffsetTarget"] = new() { "Swift.Double" } });

        Assert.Empty(output.ToString());
    }

    [Fact]
    public void EmitOverloads_WithMarkerProtocolParam_EmitsOverload()
    {
        var (csWriter, swiftWriter, output) = CreateWriters();
        var swiftOutput = new StringWriter();
        var swWriter = new SwiftWriter(swiftOutput);

        var moduleDecl = CreateModuleDecl();
        var parentDecl = CreateClassDecl("Widget");
        parentDecl.SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Widget");

        var markerProtoSpec = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.OffsetTarget") });
        var method = new MethodDecl
        {
            Name = "offset",
            MangledName = "$s10TestModule6Widget6offsetyySiF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg("amount", markerProtoSpec, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };

        var map = new Dictionary<string, List<string>>
        {
            ["OffsetTarget"] = new() { "Swift.Double", "Swift.Int" }
        };

        MarkerProtocolOverloadEmitter.EmitOverloads(
            csWriter, swWriter, method,
            CreateMethodEnvironment(method), parentDecl, map);

        var csResult = output.ToString();
        var swiftResult = swiftOutput.ToString();

        // Should emit C# overloads for double and nint
        Assert.Contains("public void Offset(double amount)", csResult);
        Assert.Contains("public void Offset(nint amount)", csResult);
        Assert.Contains("Convenience overload", csResult);

        // Should emit P/Invoke declarations
        Assert.Contains("LibraryImport", csResult);
        Assert.Contains("_MP_Double", csResult);
        Assert.Contains("_MP_Int", csResult);

        // Should emit Swift wrappers
        Assert.Contains("@_silgen_name", swiftResult);
        Assert.Contains("Swift.Double", swiftResult);
        Assert.Contains("Swift.Int", swiftResult);
    }

    [Fact]
    public void EmitOverloads_DescribesEachConformerWrapperItBindsWithTheSwiftConvention()
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var moduleDecl = CreateModuleDecl();
        var parentDecl = CreateClassDecl("Widget");
        var method = new MethodDecl
        {
            Name = "offset",
            MangledName = "$s10TestModule6Widget6offsetyySiF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg("amount", new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.OffsetTarget") }), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };
        var ctx = new ModuleEmissionContext();
        var env = CreateMethodEnvironment(method);
        env.EmissionContext = ctx;

        MarkerProtocolOverloadEmitter.EmitOverloads(
            new CSharpWriter(csOutput), new SwiftWriter(swiftOutput), method, env, parentDecl,
            new Dictionary<string, List<string>> { ["OffsetTarget"] = new() { "Swift.Double", "Swift.Int" } });

        var swift = swiftOutput.ToString();
        var wrappers = SyntheticRegistration.AssertRegistered(ctx, swift, SwiftWrapperSynthesizer.MarkerProtocolOverload);
        // One wrapper per primitive conformer, each taking that conformer where the marker existential was.
        Assert.Equal(2, wrappers.Count);
        Assert.Contains(wrappers, w => w.Parameters.Any(p => p.SwiftType == "Swift.Double"));
        Assert.Contains(wrappers, w => w.Parameters.Any(p => p.SwiftType == "Swift.Int"));
        Assert.All(wrappers, w => Assert.Equal(method.MangledName, w.Forwards));
        SyntheticRegistration.AssertBoundWithFacts(ctx, csOutput.ToString(), SwiftWrapperSynthesizer.MarkerProtocolOverload);
    }

    [Fact]
    public void EmitOverloads_RecordsEachOverloadAsTheConformerOverloadItIs()
    {
        // `Offset(double)` reaches `offset(amount:)` through its own wrapper. A newer SDK's
        // `offset(amount: Double)` declares that C# signature itself, so the Swift declaration record
        // must name the overload as one, or the package merge reads the moved signature as unchanged.
        var moduleDecl = CreateModuleDecl();
        var parentDecl = CreateClassDecl("Widget");
        var method = new MethodDecl
        {
            Name = "offset",
            MangledName = "$s10TestModule6Widget6offsetyySiF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg("amount", new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.OffsetTarget") }), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };
        var ctx = new ModuleEmissionContext();
        var env = CreateMethodEnvironment(method);
        env.EmissionContext = ctx;

        MarkerProtocolOverloadEmitter.EmitOverloads(
            new CSharpWriter(new StringWriter()), new SwiftWriter(new StringWriter()), method, env, parentDecl,
            new Dictionary<string, List<string>> { ["OffsetTarget"] = new() { "Swift.Double" } });

        var (key, swift) = Assert.Single(ctx.SwiftMemberDeclarations);
        Assert.StartsWith("Widget.Offset(", key);
        Assert.Equal(SwiftDeclarationMapEmitter.DescribeForwarder(SwiftDeclarationMapEmitter.Describe(method)!,
            "Swift.Double", SwiftDeclarationMapEmitter.MarkerConformerOverload), swift);
    }

    [Fact]
    public void EmitOverloads_NonVoidNonSelfReturn_Skipped()
    {
        // Methods returning anything other than void or Self are not supported
        var (csWriter, swiftWriter, output) = CreateWriters();
        var moduleDecl = CreateModuleDecl();

        var markerProtoSpec = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.OffsetTarget") });
        var method = new MethodDecl
        {
            Name = "offset",
            MangledName = "$s10TestModule6offsetyySiF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", new NamedTypeSpec("Swift.Int"), moduleDecl),
                CreateArg("amount", markerProtoSpec, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = CreateClassDecl("Widget"),
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };

        var map = new Dictionary<string, List<string>>
        {
            ["OffsetTarget"] = new() { "Swift.Double" }
        };

        MarkerProtocolOverloadEmitter.EmitOverloads(
            csWriter, swiftWriter, method,
            CreateMethodEnvironment(method), null, map);

        Assert.Empty(output.ToString());
    }

    #endregion

    #region Parameters and returns

    [Fact]
    public void EmitOverloads_PassesEveryParameterThroughToItsWrapper()
    {
        // The wrapper declares every Swift parameter, so the P/Invoke and the C# overload must
        // carry every one too: a P/Invoke that stopped at the marker would leave the wrapper
        // reading the remaining arguments from whatever the registers held.
        var parent = CreateClassDecl("Widget");
        var method = CreateMarkerMethod("recordScaled", parent, TupleTypeSpec.Empty,
            CreateArg("scale", new NamedTypeSpec("Swift.Int32"), CreateModuleDecl()));
        var (cs, swift) = Emit(method, parent, "Swift.Double");

        Assert.Contains("public void RecordScaled(double amount, int scale)", cs);
        Assert.Matches(@"_MP_Double\(global::System\.IntPtr self, double amount, int scale\)", cs);
        Assert.Matches(@"_MP_Double\(.*, amount, scale\);", cs);
        // The marker goes back in as the existential the method declares, so Swift cannot resolve
        // the call to a sibling overload the concrete value also fits.
        Assert.Contains("amount as any TestModule.OffsetTarget", swift);
        Assert.Contains("Swift.Double", swift);
        Assert.Contains("Swift.Int32", swift);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmitOverloads_AnySpelledMarker_EmitsTheSameOverloads(bool inOut)
    {
        // The parser spells a single `any P` as a named type marked `any`, not as a one-protocol
        // list, so detection must accept both spellings. An inout marker is a different shape and
        // stays declined.
        var parent = CreateClassDecl("Widget");
        var method = CreateMarkerMethod("offset", parent, TupleTypeSpec.Empty);
        method.CSSignature[1] = CreateArg("amount", new NamedTypeSpec("TestModule.OffsetTarget") { IsAny = true }, CreateModuleDecl());
        method.CSSignature[1].IsInOut = inOut;
        var (cs, _) = Emit(method, parent, "Swift.Double", "Swift.Int");

        if (inOut)
        {
            Assert.DoesNotContain("public void Offset(", cs);
            return;
        }
        Assert.Contains("public void Offset(double amount)", cs);
        Assert.Contains("public void Offset(nint amount)", cs);
    }

    [Theory]
    [InlineData("Swift.UInt16", "ushort")]
    [InlineData("Swift.Int8", "sbyte")]
    [InlineData("Swift.UInt64", "ulong")]
    public void EmitOverloads_EveryScalarPassesThrough(string swiftType, string csType)
    {
        var parent = CreateClassDecl("Widget");
        var method = CreateMarkerMethod("recordScaled", parent, TupleTypeSpec.Empty,
            CreateArg("scale", new NamedTypeSpec(swiftType), CreateModuleDecl()));
        var (cs, _) = Emit(method, parent, "Swift.Double");

        Assert.Contains($"public void RecordScaled(double amount, {csType} scale)", cs);
    }

    [Fact]
    public void EmitOverloads_BoolPassThroughMarshalsAsOneByte()
    {
        var parent = CreateClassDecl("Widget");
        var method = CreateMarkerMethod("recordFlagged", parent, TupleTypeSpec.Empty,
            CreateArg("flag", new NamedTypeSpec("Swift.Bool"), CreateModuleDecl()));
        var (cs, _) = Emit(method, parent, "Swift.Double");

        Assert.Contains("public void RecordFlagged(double amount, bool flag)", cs);
        Assert.Contains("UnmanagedType.U1)] bool flag", cs);
    }

    [Fact]
    public void EmitOverloads_NonPrimitiveExtraParameter_Declined()
    {
        // A String would need marshalling this emitter does not do: no overload rather than one
        // whose parameter list disagrees with its wrapper's.
        var parent = CreateClassDecl("Widget");
        var method = CreateMarkerMethod("recordNamed", parent, TupleTypeSpec.Empty,
            CreateArg("name", new NamedTypeSpec("Swift.String"), CreateModuleDecl()));
        var (cs, swift) = Emit(method, parent, "Swift.Double");

        Assert.Empty(cs);
        Assert.Empty(swift);
    }

    [Fact]
    public void EmitOverloads_SelfReturn_AdoptsTheReturnedReference()
    {
        var parent = CreateClassDecl("Widget");
        var method = CreateMarkerMethod("adding", parent, new NamedTypeSpec("TestModule.Widget"));
        var (cs, _) = Emit(method, parent, "Swift.Double");

        Assert.Contains("public Widget Adding(double amount)", cs);
        Assert.Contains("return global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwiftObject<Widget>(__result);", cs);
    }

    /// <summary>
    /// An instance overload reads its receiver off this wrapper's payload, which is then the
    /// wrapper's last use: it is held until the call has returned, whether the overload returns.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmitOverloads_InstanceOverload_HoldsTheReceiverUntilTheCallReturns(bool selfReturn)
    {
        var parent = CreateClassDecl("Widget");
        var method = CreateMarkerMethod(selfReturn ? "adding" : "offset", parent,
            selfReturn ? new NamedTypeSpec("TestModule.Widget") : TupleTypeSpec.Empty);
        var (cs, _) = Emit(method, parent, "Swift.Double");

        BorrowedOwnerAssert.HeldAfter(cs, "_MP_Double(", "this", from: selfReturn ? " Adding(double amount)" : " Offset(double amount)");
    }

    [Fact]
    public void EmitOverloads_ReturnWhoseNameOnlyEndsWithTheParentName_Declined()
    {
        // `BigWidget` is not `Widget`: adopting its reference as a Widget would wrap the wrong type.
        var parent = CreateClassDecl("Widget");
        var method = CreateMarkerMethod("adding", parent, new NamedTypeSpec("TestModule.BigWidget"));
        var (cs, swift) = Emit(method, parent, "Swift.Double");

        Assert.Empty(cs);
        Assert.Empty(swift);
    }

    #endregion

    #region Recovery units

    [Fact]
    public void EmitOverloads_EachOverloadOwnsItsOwnArtifacts()
    {
        var parent = CreateClassDecl("Widget");
        var method = CreateMarkerMethod("offset", parent, TupleTypeSpec.Empty);
        var csOut = new StringWriter();
        var swiftOut = new StringWriter();
        var csWriter = new CSharpWriter(csOut);
        var swiftWriter = new SwiftWriter(swiftOut);
        var env = CreateMethodEnvironment(method);
        env.EmissionContext = new ModuleEmissionContext();

        MarkerProtocolOverloadEmitter.EmitOverloads(csWriter, swiftWriter, method, env, parent,
            new Dictionary<string, List<string>> { ["OffsetTarget"] = new() { "Swift.Double", "Swift.Int" } });

        var cs = csOut.ToString();
        var swift = swiftOut.ToString();
        var csTiling = csWriter.Fragments.BuildTiling(cs.Length, FragmentOwners.ForDecl(method));
        var swiftTiling = swiftWriter.Fragments.BuildTiling(swift.Length, FragmentOwners.ForDeclWrapper(method));
        foreach (var conformer in new[] { "Swift.Double", "Swift.Int" })
        {
            var id = MarkerProtocolOverloadEmitter.OverloadId(env.SourceDeclId, "TestModule.OffsetTarget", conformer);
            Assert.NotEqual(env.SourceDeclId, id);
            var shortName = conformer.Substring(conformer.LastIndexOf('.') + 1);
            // The overload's C# (P/Invoke and method) and its Swift wrapper sit in its own fragments.
            Assert.Contains(csTiling, f => f.Owner.Artifact.Decl == id
                && cs.Substring(f.Start, f.End - f.Start).Contains($"_MP_{shortName}("));
            Assert.Contains(swiftTiling, f => f.Owner.Artifact.Decl == id
                && swift.Substring(f.Start, f.End - f.Start).Contains($"_MP_{shortName}"));
        }
        // Nothing of either overload is left on the method's own unit.
        Assert.DoesNotContain(csTiling, f => f.Owner.Artifact.Decl == env.SourceDeclId
            && cs.Substring(f.Start, f.End - f.Start).Contains("_MP_"));
        Assert.DoesNotContain(swiftTiling, f => f.Owner.Artifact.Decl == env.SourceDeclId
            && swift.Substring(f.Start, f.End - f.Start).Contains("_MP_"));
    }

    [Fact]
    public void EmitOverloads_DeniedOverload_LeavesATombstoneAndKeepsItsSiblings()
    {
        var parent = CreateClassDecl("Widget");
        var method = CreateMarkerMethod("offset", parent, TupleTypeSpec.Empty);
        var denied = MarkerProtocolOverloadEmitter.OverloadId(
            DeclIdFactory.ForMethod(method), "TestModule.OffsetTarget", "Swift.Double");
        using var attempt = EmissionAttempt.Begin(WrapperDenylistSeed.Build(
            new HashSet<RecoveryUnitId> { RecoveryUnitId.Create(denied, RecoveryScope.LeafApi) }));

        var (cs, swift) = Emit(method, parent, "Swift.Double", "Swift.Int");

        Assert.Contains("Unsupported:", cs);
        Assert.Contains("Swift.Double as TestModule.OffsetTarget", cs);
        Assert.DoesNotContain("_MP_Double", cs);
        Assert.DoesNotContain("_MP_Double", swift);
        Assert.Contains("public void Offset(nint amount)", cs);
        Assert.Contains("_MP_Int", swift);
        Assert.False(attempt.Abandoned);
    }

    [Fact]
    public void TryEmitForWithdrawnMethod_RecoveryWithdrawal_ReplaysEveryOverload()
    {
        // The overloads call wrappers of their own, so withdrawing the method they convenience
        // must not take them with it.
        var parent = CreateClassDecl("Widget");
        var method = CreateMarkerMethod("offset", parent, TupleTypeSpec.Empty);
        using var attempt = EmissionAttempt.Begin(WrapperDenylistSeed.Build(new HashSet<RecoveryUnitId>
            { RecoveryUnitId.Create(DeclIdFactory.ForMethod(method), RecoveryScope.LeafApi) }));

        var (cs, swift) = Replay(method);

        Assert.Contains("public void Offset(double amount)", cs);
        Assert.Contains("public void Offset(nint amount)", cs);
        Assert.Contains("_MP_Double", swift);
        Assert.Contains("_MP_Int", swift);
    }

    [Fact]
    public void TryEmitForWithdrawnMethod_ReplaysUnderTheNameTheMethodsEmissionGaveThem()
    {
        // The overload resolver renamed the method (a sibling took the bare name), so its overloads
        // were emitted under the new name. Replayed under a recomputed bare name, they would collide
        // with the sibling's own overloads.
        var parent = CreateClassDecl("Widget");
        var method = CreateMarkerMethod("offset", parent, TupleTypeSpec.Empty);
        using var attempt = EmissionAttempt.Begin(WrapperDenylistSeed.Build(new HashSet<RecoveryUnitId>
            { RecoveryUnitId.Create(DeclIdFactory.ForMethod(method), RecoveryScope.LeafApi) }));

        var (cs, _) = Replay(method, disambiguatedName: "offsetSecond");

        Assert.Contains("public void OffsetSecond(double amount)", cs);
        Assert.DoesNotContain("public void Offset(", cs);
    }

    [Fact]
    public void TryEmitForWithdrawnMethod_NothingEmittedEarlier_ReplaysNothing()
    {
        var parent = CreateClassDecl("Widget");
        var method = CreateMarkerMethod("offset", parent, TupleTypeSpec.Empty);
        using var attempt = EmissionAttempt.Begin(WrapperDenylistSeed.Build(new HashSet<RecoveryUnitId>
            { RecoveryUnitId.Create(DeclIdFactory.ForMethod(method), RecoveryScope.LeafApi) }));

        var (cs, swift) = Replay(method, earlierAttemptEmitted: false);

        Assert.Empty(cs);
        Assert.Empty(swift);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryEmitForWithdrawnMethod_NotARecoveryWithdrawal_EmitsNothing(bool emitterThrew)
    {
        // An emitter that threw on the method is not re-entered for it; a method nothing withdrew
        // already ran its overloads as post-processors.
        var parent = CreateClassDecl("Widget");
        var method = CreateMarkerMethod("offset", parent, TupleTypeSpec.Empty);
        var units = emitterThrew
            ? new HashSet<RecoveryUnitId> { RecoveryUnitId.Create(DeclIdFactory.ForMethod(method), RecoveryScope.LeafApi) }
            : new HashSet<RecoveryUnitId>();
        using var attempt = EmissionAttempt.Begin(WrapperDenylistSeed.Build(units, _ => EmitterFaultOrigin.EmitterException));

        var (cs, swift) = Replay(method);

        Assert.Empty(cs);
        Assert.Empty(swift);
    }

    /// <summary>
    /// Replays the method's overloads as the withdrawn-method loop does, after an earlier attempt
    /// (unless <paramref name="earlierAttemptEmitted"/> is false) emitted them under
    /// <paramref name="disambiguatedName"/> and the driver carried that attempt's recipes forward.
    /// </summary>
    private static (string Cs, string Swift) Replay(MethodDecl method, string disambiguatedName = null, bool earlierAttemptEmitted = true)
    {
        var conformances = new Dictionary<string, List<string>> { ["OffsetTarget"] = new() { "Swift.Double", "Swift.Int" } };
        var emissionContext = new ModuleEmissionContext();
        if (earlierAttemptEmitted)
        {
            var earlier = CreateMethodEnvironment(method);
            earlier.EmissionContext = emissionContext;
            earlier.DisambiguatedNameInput = disambiguatedName;
            MarkerProtocolOverloadEmitter.EmitOverloads(new CSharpWriter(new StringWriter()), new SwiftWriter(new StringWriter()),
                method, earlier, method.ParentDecl as TypeDecl, conformances);
            emissionContext.SeedDefaultOverloadRecipes(new Dictionary<DeclId, DefaultOverloadReplayRecipe>(emissionContext.CompletedDefaultOverloadRecipes));
        }

        var csOut = new StringWriter();
        var swiftOut = new StringWriter();
        var context = new TypeHandlerContext(null, new(), null,
            MarkerProtocolConformances: conformances, EmissionContext: emissionContext);
        MarkerProtocolOverloadEmitter.TryEmitForWithdrawnMethod(method, new CSharpWriter(csOut), new SwiftWriter(swiftOut),
            CreateTypeDatabase(), context, siblingPropertyNames: null, emittedProjectedSignatures: new(), reservedOverloadShapes: new());
        return (csOut.ToString(), swiftOut.ToString());
    }

    private static (string Cs, string Swift) Emit(MethodDecl method, TypeDecl parent, params string[] conformers)
    {
        var csOut = new StringWriter();
        var swiftOut = new StringWriter();
        var env = CreateMethodEnvironment(method);
        env.EmissionContext = new ModuleEmissionContext();
        MarkerProtocolOverloadEmitter.EmitOverloads(new CSharpWriter(csOut), new SwiftWriter(swiftOut), method, env, parent,
            new Dictionary<string, List<string>> { ["OffsetTarget"] = conformers.ToList() });
        return (csOut.ToString(), swiftOut.ToString());
    }

    private static MethodDecl CreateMarkerMethod(string name, ClassDecl parent, TypeSpec returnSpec, params ArgumentDecl[] extra)
    {
        var moduleDecl = CreateModuleDecl();
        var signature = new List<ArgumentDecl>
        {
            CreateArg("", returnSpec, moduleDecl),
            CreateArg("amount", new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.OffsetTarget") }), moduleDecl),
        };
        signature.AddRange(extra);
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule6WidgetC{name.Length}{name}yyAA12OffsetTarget_pF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = signature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parent,
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };
    }

    #endregion

    #region Helpers

    private static (CSharpWriter csWriter, SwiftWriter swiftWriter, StringWriter output) CreateWriters()
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);
        return (csWriter, swiftWriter, output);
    }

    private static ModuleDecl CreateModuleDecl()
    {
        return new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ClassDecl CreateClassDecl(string name)
    {
        var moduleDecl = CreateModuleDecl();
        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
    }

    private static MethodDecl CreateBasicMethodDecl(string name)
    {
        var moduleDecl = CreateModuleDecl();
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name.Length}{name}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = CreateClassDecl("TestType"),
            ModuleDecl = moduleDecl,
            IsSynthesizedAccessor = false
        };
    }

    private static ArgumentDecl CreateArg(string name, TypeSpec typeSpec, ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            Name = name,
            PrivateName = name,
            SwiftTypeSpec = typeSpec,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "nint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static MethodEnvironment CreateMethodEnvironment(MethodDecl method)
    {
        return new MethodEnvironment(method, CreateTypeDatabase());
    }

    #endregion
}
