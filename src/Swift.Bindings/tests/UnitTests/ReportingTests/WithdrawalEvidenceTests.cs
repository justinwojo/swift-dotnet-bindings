// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using BindingsGeneration.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

public class WithdrawalEvidenceTests
{
    private static WrapperRecoveryResult Result(params RecoveryUnitId[] units) => new()
    {
        Converged = true, Cause = WrapperRecoveryFailureCause.None, Rounds = 2,
        Denylist = units.ToImmutableArray(),
    };

    [Fact]
    public void CanonicalBytes_PreserveEscapesReturnSlotAndDisplayMultiplicity()
    {
        var first = DeclId.Create("M", "T", BindingItemKind.Method, "call!",
            ImmutableArray.Create("", "arg"), ImmutableArray.Create("Swift.Int", "(A, B):C"),
            genericContext: "<T : P>", symbol: "sym\\bol").Unit(RecoveryScope.LeafApi);
        const string expected = @"M|T|Method|call!|:Swift.Int,arg:(A\, B)\:C|None|<T \: P>|sym\\bol|!leaf-api";
        Assert.Equal(expected, first.Canonical);
        var second = first with { Decl = first.Decl with { Symbol = "other" } };
        var evidence = WithdrawalEvidence.FromController(Result(first, second), true, true);
        var report = EmissionReportEmitter.BuildReport(new ModuleEmissionContext(), "M", evidence);

        Assert.Equal(new[] { "csharp", "swift" }, evidence.ConfiguredPlanes);
        Assert.Equal(new[] { first.Canonical, second.Canonical }.Order(StringComparer.Ordinal), evidence.UnitIds);
        Assert.Equal(new[] { "M.T.call! (leaf-api)", "M.T.call! (leaf-api)" }, report.WithdrawnUnits);
        Assert.All(evidence.UnitIds, id =>
        {
            Assert.True(CanonicalIdentityCodec.TryParseUnit(id, out var parsed));
            Assert.Equal(id, parsed.Canonical);
            Assert.Equal(id, RecoveryUnitId.Parse(id).Canonical);
            Assert.Equal("Swift.Int", parsed.Decl.ParameterTypes[0]);
        });
    }

    [Fact]
    public void FactoryMethodReturnSlot_ReachesCodecUnchanged()
    {
        var method = TestModelFactory.CreateMethod("fetch", TestModelFactory.CreateModuleDecl(),
            new[] { ("from", "Swift.String") });
        var id = DeclIdFactory.ForMethod(method);
        Assert.True(CanonicalIdentityCodec.TryParseDeclaration(id.Canonical, out var parsed));
        Assert.Equal(method.CSSignature.Count, parsed.ParameterTypes.Length);
        Assert.Equal(method.CSSignature[0].SwiftTypeSpec.ToString(), parsed.ParameterTypes[0]);
        Assert.Equal(id.Canonical, parsed.Canonical);
    }

    [Fact]
    public void DuplicateIdentity_IsRejectedInsteadOfSilentlyDeduplicated()
    {
        var unit = DeclId.Create("M", "T", BindingItemKind.Method, "f").Unit(RecoveryScope.LeafApi);
        Assert.Throws<ArgumentException>(() => WithdrawalEvidence.FromController(Result(unit, unit), true, false));
    }

    [Fact]
    public void NoWrapperOrCSharpOnly_DoesNotLoseSettledDenylist()
    {
        var unit = DeclId.Create("M", "T", BindingItemKind.Property, "p").Unit(RecoveryScope.AccessorGroup);
        var evidence = WithdrawalEvidence.FromController(Result(unit), false, true);
        Assert.Equal("converged", evidence.LoopStatus);
        Assert.Equal(new[] { "csharp" }, evidence.ConfiguredPlanes);
        Assert.Equal(new[] { unit.Canonical }, evidence.UnitIds);
        Assert.Null(evidence.NotRunReason);
    }

    [Fact]
    public void FailedResult_CannotClaimConvergence()
    {
        var failed = Result() with { Converged = false, Cause = WrapperRecoveryFailureCause.NoProgress };
        Assert.Equal("failed", WithdrawalEvidence.FromController(failed, true, false).LoopStatus);
        Assert.Throws<ArgumentException>(() => WithdrawalEvidence.FromController(failed with { Converged = true }, true, false));
        Assert.Throws<ArgumentException>(() => WithdrawalEvidence.FromController(Result(), false, false));
    }

    [Fact]
    public void WrongModule_IsRejected()
    {
        var unit = DeclId.Create("Other", "T", BindingItemKind.Method, "f").Unit(RecoveryScope.LeafApi);
        var evidence = WithdrawalEvidence.FromController(Result(unit), true, false);
        Assert.Throws<ArgumentException>(() => EmissionReportEmitter.BuildReport(new ModuleEmissionContext(), "M", evidence));
    }

    [Fact]
    public void NotRun_HasSupportedReasonAndNoInventedUnits()
    {
        var report = EmissionReportEmitter.BuildReport(new ModuleEmissionContext(), "M");
        Assert.Equal("not-run", report.WithdrawalEvidence.LoopStatus);
        Assert.Equal("no-verification-planes", report.WithdrawalEvidence.NotRunReason);
        Assert.Empty(report.WithdrawalEvidence.ConfiguredPlanes);
        Assert.Empty(report.WithdrawalEvidence.UnitIds);
        Assert.Empty(report.WithdrawnUnits);
    }

    [Fact]
    public void EmitAndBuildReport_UseSameSnapshot()
    {
        var unit = DeclId.Create("M", "T", BindingItemKind.Method, "f").Unit(RecoveryScope.LeafApi);
        var evidence = WithdrawalEvidence.FromController(Result(unit), true, false);
        var directory = Path.Combine(Path.GetTempPath(), "withdrawal-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            EmissionReportEmitter.Emit(new ModuleEmissionContext(), "M", directory, NullLogger.Instance, evidence);
            var json = JObject.Parse(File.ReadAllText(Path.Combine(directory, "binding-emission-report.json")));
            Assert.Equal(1, (int)json["withdrawalEvidence"]["schemaVersion"]);
            Assert.Equal("RecoveryUnitId.Canonical/v1", (string)json["withdrawalEvidence"]["identityFormat"]);
            Assert.Equal(evidence.UnitIds, json["withdrawalEvidence"]["unitIds"].Values<string>());
            Assert.Equal(evidence.Descriptions, json["withdrawnUnits"].Values<string>());
            Assert.Null(json["withdrawalEvidence"]["notRunReason"]);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
