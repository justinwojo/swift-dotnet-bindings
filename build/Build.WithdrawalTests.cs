// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nuke.Common;
using Nuke.Common.Tooling;

partial class Build
{
    [Parameter("Inject a downstream failure into the withdrawal promotion invocation test")]
    readonly bool WithdrawalOrderFailure;
    const string WithdrawalOrderBaselineEnvironment = "SWIFT_BINDINGS_WITHDRAWAL_ORDER_BASELINE";
    ValidationPromotion? orderTestCandidate;
    string? orderTestBaseline;
    const string OrderTestOriginal = "{\"compile_gate\":{\"libraries\":{\"Fixture\":{\"compile\":\"fail\"}}}}";

    // This real Nuke graph reproduces Validate -> triggered gate -> final promotion, including
    // the engine's failure/skip behavior. Run with and without --withdrawal-order-failure.
    Target WithdrawalInvocationTest => _ => _
        .After(PromoteValidationBaseline)
        .Triggers(WithdrawalInvocationGate)
        .Executes(() =>
        {
            var requestedBaseline = Environment.GetEnvironmentVariable(WithdrawalOrderBaselineEnvironment);
            orderTestBaseline = string.IsNullOrWhiteSpace(requestedBaseline)
                ? Path.Combine(Path.GetTempPath(), "withdrawal-invocation-" + Guid.NewGuid().ToString("N"), "baseline.json")
                : Path.GetFullPath(requestedBaseline);
            Directory.CreateDirectory(Path.GetDirectoryName(orderTestBaseline)!);
            File.WriteAllText(orderTestBaseline, OrderTestOriginal);
            orderTestCandidate = new ValidationPromotion(orderTestBaseline, true,
                new Dictionary<string, ValidationBaseline.LibraryResult> { ["Fixture"] = new() { Compile = "ok" } },
                "Validate", "Gate");
            orderTestCandidate.Record("Validate");
            if (File.ReadAllText(orderTestBaseline) != OrderTestOriginal)
                throw new Exception("Validation body changed baseline before triggered gate");
            Serilog.Log.Information("Invocation test baseline: {Path}", orderTestBaseline);
        });

    Target WithdrawalInvocationGate => _ => _.Triggers(WithdrawalInvocationPromotion).Executes(() =>
    {
        if (File.ReadAllText(orderTestBaseline!) != OrderTestOriginal)
            throw new Exception("Baseline promoted before downstream gate");
        if (WithdrawalOrderFailure) throw new Exception("Expected downstream gate failure; baseline remains unchanged");
        orderTestCandidate!.Record("Gate");
    });

    Target WithdrawalInvocationPromotion => _ => _.Executes(() =>
    {
        if (!orderTestCandidate!.Promote()) throw new Exception("Successful invocation did not promote");
        using var result = JsonDocument.Parse(File.ReadAllText(orderTestBaseline!));
        if (result.RootElement.GetProperty("compile_gate").GetProperty("libraries").GetProperty("Fixture")
            .GetProperty("compile").GetString() != "ok") throw new Exception("Promotion did not apply candidate");
    });

    Target WithdrawalGateTests => _ => _
        .DependsOn(Compile)
        .Executes(() =>
    {
        var scratch = Path.Combine(Path.GetTempPath(), "withdrawal-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var path = Path.Combine(scratch, "report.json");
        const string first = "Fixture||Type|First||None|||!type-surface";
        const string second = "Fixture||Type|Second||None|||!type-surface";
        int assertions = 0;
        void Check(bool condition) { assertions++; if (!condition) throw new Exception("Withdrawal test failed"); }
        void Reject(Action action)
        {
            assertions++;
            try { action(); } catch (InvalidDataException) { return; }
            throw new Exception("Expected withdrawal rejection");
        }
        void Report(string[] ids, string[] descriptions, string status = "converged", string[]? planes = null,
            string module = "Fixture", int schema = 1) => File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                module, withdrawnUnits = descriptions,
                withdrawalEvidence = new { schemaVersion = schema, identityFormat = "RecoveryUnitId.Canonical/v1",
                    loopStatus = status, configuredPlanes = planes ?? new[] { "swift" }, unitIds = ids }
            }));
        WithdrawalGate.Evidence Read() => WithdrawalGate.Read(path, "Fixture", new[] { "swift" }, WithdrawalGate.ParseIdentity);
        try
        {
            Reject(() => Read());
            Report([], []); Check(Read().UnitIds.Length == 0);
            Report([], [], module: "Other"); Reject(() => Read());
            Report([], [], schema: 99); Reject(() => Read());
            Report([], [], status: "not-run"); Reject(() => Read());
            Report([], [], planes: ["swift", "csharp"]); Reject(() => Read());
            Report([], [], planes: ["swift", "swift"]); Reject(() => Read());
            File.WriteAllText(path, "{\"module\":\"Fixture\",\"module\":\"Fixture\"}"); Reject(() => Read());
            File.WriteAllText(path, "{\"module\":\"Fixture\",\"withdrawalEvidence\":null}"); Reject(() => Read());
            Report([first], ["Fixture.First (type-surface)"]); Check(Read().UnitIds.Single() == first);
            Reject(() => WithdrawalGate.RequireZero(Read()));
            Report([first, first], ["Fixture.First (type-surface)", "Fixture.First (type-surface)"]); Reject(() => Read());
            Report([first], []); Reject(() => Read());
            Report(["invalid"], ["invalid"]); Reject(() => Read());
            const string otherScope = "Fixture||Type|First||None|||!type-representation";
            Report([otherScope, first], ["Fixture.First (type-representation)", "Fixture.First (type-surface)"]);
            Check(Read().UnitIds.Length == 2);
            File.WriteAllText(path, JsonSerializer.Serialize(new { module = "Fixture", withdrawnUnits = Array.Empty<string>(),
                withdrawalEvidence = new { schemaVersion = 1, identityFormat = "RecoveryUnitId.Canonical/v1",
                    loopStatus = "not-run", configuredPlanes = Array.Empty<string>(), unitIds = Array.Empty<string>(),
                    notRunReason = "no-verification-planes" } }));
            Reject(() => WithdrawalGate.Read(path, "Fixture", [], WithdrawalGate.ParseIdentity));
            Check(WithdrawalGate.Read(path, "Fixture", [], WithdrawalGate.ParseIdentity, true).UnitIds.Length == 0);
            var accepted = new WithdrawalPolicy(1, "Fixture/ios", [new(first, "reason", "fixture", "sha256:diagnostic",
                "active", "owner-reference", "trigger", "sdk")]);
            Check(!WithdrawalGate.Compare(accepted, "Fixture/ios", new([first], path)));
            Check(WithdrawalGate.Compare(accepted, "Fixture/ios", new([], path)));
            Reject(() => WithdrawalGate.Compare(accepted, "Fixture/ios", new([second], path)));
            Reject(() => WithdrawalGate.Compare(accepted, "Fixture/macos", new([], path)));
            Reject(() => WithdrawalGate.Compare(null, "Fixture/ios", new([], path)));
            Check(!JsonSerializer.Serialize(new ValidationBaseline.LibraryResult()).Contains("withdrawal_policy", StringComparison.Ordinal));
            Check(!JsonSerializer.Serialize(JsonSerializer.Deserialize<ValidationBaseline.LibraryResult>("{\"withdrawal_policy\":null}"))
                .Contains("withdrawal_policy", StringComparison.Ordinal));
            var provenanceInput = Path.Combine(scratch, "input.swift");
            File.WriteAllText(provenanceInput, "fixture");
            var provenance = new WithdrawalProvenance("source", "generator", "selection", "ios", "arm64", "arm64-apple-ios-simulator", "sdk",
                new Dictionary<string, string> { [provenanceInput] = WithdrawalProvenance.HashFile(provenanceInput) });
            var toolchain = new WithdrawalToolchain("ios", "arm64", "arm64-apple-ios-simulator", "sdk");
            provenance.Verify("source", "generator", "selection", toolchain);
            foreach (var changedToolchain in new[] { toolchain with { Platform = "macos" }, toolchain with { Architecture = "x86_64" },
                toolchain with { TargetTriple = "arm64-apple-ios-device" }, toolchain with { Sdk = "new-sdk" } })
                Reject(() => provenance.Verify("source", "generator", "selection", changedToolchain));
            Reject(() => (provenance with { Architecture = "x86_64" }).Verify("source", "generator", "selection", toolchain));
            Reject(() => provenance.Verify("changed", "generator", "selection", toolchain));
            File.AppendAllText(provenanceInput, "changed");
            Reject(() => provenance.Verify("source", "generator", "selection", toolchain));
            Report([], []);
            var receipt = new WithdrawalReceipt(provenance, WithdrawalProvenance.HashFile(path), 0, "ok", "ok", "none");
            receipt.RequireSuccess(path);
            Reject(() => (receipt with { GeneratorExit = 1 }).RequireSuccess(path));
            Reject(() => (receipt with { CSharpVerdict = "known_errors" }).RequireSuccess(path));
            Reject(() => (receipt with { SwiftVerdict = "unknown" }).RequireSuccess(path));
            File.WriteAllText(path, JsonSerializer.Serialize(new { module = "ObjCUmbrella", withdrawnUnits = Array.Empty<string>(),
                withdrawalEvidence = new { schemaVersion = 1, identityFormat = "RecoveryUnitId.Canonical/v1", loopStatus = "not-run",
                    notRunReason = "no-verification-planes", configuredPlanes = Array.Empty<string>(), unitIds = Array.Empty<string>() } }));
            var objcReceipt = receipt with { ReportHash = WithdrawalProvenance.HashFile(path), SwiftVerdict = "no_wrapper" };
            objcReceipt.RequireSuccess(path, "ObjCUmbrella", "ObjCUmbrella");
            Reject(() => objcReceipt.RequireSuccess(path, "AnotherSwiftModule", "ObjCUmbrella"));
            Reject(() => objcReceipt.RequireSuccess(path, "ObjCUmbrella", "AnotherSwiftModule"));
            File.WriteAllText(path, File.ReadAllText(path).Replace("no-verification-planes", "unknown-reason"));
            Reject(() => (objcReceipt with { ReportHash = WithdrawalProvenance.HashFile(path) }).RequireSuccess(path, "ObjCUmbrella", "ObjCUmbrella"));

            var pureObjCManifest = Path.Combine(scratch, "pure-objc-manifest.json");
            const string pureObjCReason = "Pure-ObjC binding: no Swift generation phase runs, so the manifest carries only the ObjC skip section.";
            void PureObjCManifest(string module = "PureObjC", string status = "Partial") =>
                File.WriteAllText(pureObjCManifest, JsonSerializer.Serialize(new
                {
                    SchemaVersion = 3, Module = module, Status = status, PartialReason = pureObjCReason,
                    Generation = (object?)null, Emission = (object?)null, Wrapper = (object?)null, ObjC = new { Status = "Success" }
                }));
            PureObjCManifest();
            var pureObjCReceipt = receipt with
            {
                ReportHash = WithdrawalProvenance.HashFile(pureObjCManifest),
                SwiftVerdict = "no_wrapper"
            };
            pureObjCReceipt.RequirePureObjCSuccess(pureObjCManifest, "PureObjC");
            Reject(() => (pureObjCReceipt with { SwiftVerdict = "ok" }).RequirePureObjCSuccess(pureObjCManifest, "PureObjC"));
            Reject(() => (pureObjCReceipt with { ReportHash = "stale" }).RequirePureObjCSuccess(pureObjCManifest, "PureObjC"));
            Reject(() => pureObjCReceipt.RequirePureObjCSuccess(pureObjCManifest, "Other"));
            PureObjCManifest(status: "Complete");
            Reject(() => (pureObjCReceipt with { ReportHash = WithdrawalProvenance.HashFile(pureObjCManifest) })
                .RequirePureObjCSuccess(pureObjCManifest, "PureObjC"));

            var priorOutput = Path.Combine(scratch, "prior-output");
            Directory.CreateDirectory(priorOutput);
            File.WriteAllText(Path.Combine(priorOutput, "binding-emission-report.json"), "prior-report");
            File.WriteAllText(Path.Combine(priorOutput, "withdrawal-receipt.json"), "prior-receipt");
            File.WriteAllText(Path.Combine(priorOutput, "withdrawal-provenance.json"), "prior-provenance");
            File.WriteAllText(Path.Combine(priorOutput, "unrelated.txt"), "unrelated");
            var archive = WithdrawalEvidenceArchive.Rotate(priorOutput, Path.Combine(scratch, "archive", "Fixture"))!;
            Check(File.ReadAllText(Path.Combine(archive, "binding-emission-report.json")) == "prior-report");
            Check(File.ReadAllText(Path.Combine(archive, "withdrawal-receipt.json")) == "prior-receipt");
            Check(File.ReadAllText(Path.Combine(archive, "withdrawal-provenance.json")) == "prior-provenance");
            Check(!File.Exists(Path.Combine(archive, "unrelated.txt")));
            Check(File.ReadAllText(Path.Combine(priorOutput, "binding-emission-report.json")) == "prior-report");
            Check(WithdrawalEvidenceArchive.Rotate(Path.Combine(scratch, "missing"), Path.Combine(scratch, "archive", "Other")) == null);

            var baseline = Path.Combine(scratch, "baseline.json");
            const string original = "{\"git_sha\":\"old\",\"legacy\":{\"x\":1},\"compile_gate\":{\"libraries\":{\"Fixture\":{\"compile\":\"fail\",\"withdrawal_policy\":{\"sentinel\":1}}}}}";
            File.WriteAllText(baseline, original);
            var results = new Dictionary<string, ValidationBaseline.LibraryResult> { ["Fixture"] = new() { Compile = "ok" } };
            var candidate = new ValidationPromotion(baseline, true, results, "Validate", "PackGate", "BehaviorTier");
            candidate.Record("Validate"); Check(!candidate.Promote()); Check(File.ReadAllText(baseline) == original);
            candidate.Record("PackGate"); Check(!candidate.Promote()); Check(File.ReadAllText(baseline) == original);
            candidate.Record("BehaviorTier"); Check(candidate.Promote());
            using (var result = JsonDocument.Parse(File.ReadAllText(baseline)))
            {
                Check(result.RootElement.GetProperty("git_sha").GetString() == "old");
                Check(result.RootElement.GetProperty("legacy").GetProperty("x").GetInt32() == 1);
                Check(result.RootElement.GetProperty("compile_gate").GetProperty("libraries").GetProperty("Fixture")
                    .GetProperty("withdrawal_policy").GetProperty("sentinel").GetInt32() == 1);
            }
            candidate = new ValidationPromotion(baseline, false, results); Check(!candidate.Promote());
            var inspection = Path.Combine(scratch, "inspection.json");
            results["Fixture"] = results["Fixture"] with { WithdrawalPolicy = new WithdrawalPolicy(1, "Fixture/ios", []) };
            candidate.UpdateResults(results); candidate.WriteInspection(inspection);
            using (var inspected = JsonDocument.Parse(File.ReadAllText(inspection)))
            {
                Check(!inspected.RootElement.GetProperty("eligible").GetBoolean());
                Check(inspected.RootElement.GetProperty("results").GetProperty("Fixture").GetProperty("withdrawal_policy")
                    .GetProperty("exceptions").GetArrayLength() == 0);
            }
            Check(!candidate.Promote());
            candidate = new ValidationPromotion(baseline, true, results);
            File.AppendAllText(baseline, " "); var changed = File.ReadAllText(baseline);
            Reject(() => candidate.Promote()); Check(File.ReadAllText(baseline) == changed);

            // Exercise the real Nuke trigger graph in child processes. The injected-failure arm
            // must return non-zero while leaving the baseline byte-for-byte unchanged; the green
            // arm must reach the final promotion target. Invoke the already-built assembly rather
            // than `dotnet run`, which would rebuild the live build project recursively.
            RunWithdrawalInvocationProbe(injectFailure: false); assertions++;
            RunWithdrawalInvocationProbe(injectFailure: true); assertions++;
            Serilog.Log.Information("Withdrawal gate model tests passed: {Count} assertions", assertions);
        }
        finally { Directory.Delete(scratch, recursive: true); }
    });

    void RunWithdrawalInvocationProbe(bool injectFailure)
    {
        var buildDll = typeof(Build).Assembly.Location;
        var scratch = Path.Combine(Path.GetTempPath(), "withdrawal-invocation-probe-" + Guid.NewGuid().ToString("N"));
        var baselinePath = Path.Combine(scratch, "baseline.json");
        var arguments = new List<string>
        {
            $"{WithdrawalOrderBaselineEnvironment}={baselinePath}",
            "dotnet",
            buildDll,
            nameof(WithdrawalInvocationTest),
            "--root",
            scratch
        };
        if (injectFailure) arguments.Add("--withdrawal-order-failure");

        try
        {
            var process = ProcessTasks.StartProcess("/usr/bin/env", ArgumentEscaper.Join(arguments),
                workingDirectory: RootDirectory, logOutput: false).AssertWaitForExit();
            if (injectFailure)
            {
                if (process.ExitCode == 0)
                    throw new Exception("Withdrawal invocation failure probe unexpectedly succeeded");
                if (File.ReadAllText(baselinePath) != OrderTestOriginal)
                    throw new Exception("Withdrawal invocation failure probe changed the baseline");
            }
            else
            {
                if (process.ExitCode != 0)
                    throw new Exception($"Withdrawal invocation success probe exited {process.ExitCode}: " +
                        string.Join(Environment.NewLine, process.Output.Select(line => line.Text)));
                using var result = JsonDocument.Parse(File.ReadAllText(baselinePath));
                if (result.RootElement.GetProperty("compile_gate").GetProperty("libraries").GetProperty("Fixture")
                    .GetProperty("compile").GetString() != "ok")
                    throw new Exception("Withdrawal invocation success probe did not promote the candidate");
            }
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }
}
