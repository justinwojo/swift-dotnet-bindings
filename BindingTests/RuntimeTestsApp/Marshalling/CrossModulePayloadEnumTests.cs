// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using SwiftBindingsTestLibDependency;
// Pin unqualified DependencyService to the dep-module original; the cross-module
// emitter produces a same-named partial-class wrapper in SwiftBindingsTestLib to host
// nested extension types.
using DependencyService = SwiftBindingsTestLibDependency.DependencyService;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Regression coverage for the cross-module variant of the enum-case
/// payload-extractor-missing bug: an enum declared in one module whose
/// `.completed(payload:)` case carries a type owned by a *different* module.
/// <see cref="ClassPayloadEnumTests"/> already locks the
/// same-module variant; these tests lock the cross-module path so any
/// TypeDatabase / projection regression that silently drops the factory or
/// extractor surfaces here rather than in downstream binding output.
/// </summary>
public class CrossModulePayloadEnumTests : TestBase
{
    public CrossModulePayloadEnumTests(TestResults results) : base(results) { }

    public void TestCrossModuleClassResult_Completed_ExtractsForeignClassPayload()
    {
        using var result = TestLibFunctions.MakeCrossModuleClassCompleted("svc-1");
        AssertEqual(CrossModuleClassResult.CaseTag.Completed, result.Tag, "Tag == Completed");

        AssertTrue(result.TryGetCompleted(out var session), "TryGetCompleted returns true");
        using (session)
        {
            AssertEqual("svc-1", session!.Name, "Cross-module class payload round-trips Name");
            AssertTrue(session.IsActive, "Cross-module class payload round-trips IsActive");
        }
    }

    public void TestCrossModuleClassResult_Failed_ExtractsAnyErrorOnly()
    {
        using var result = TestLibFunctions.MakeCrossModuleClassFailed("boom");
        AssertEqual(CrossModuleClassResult.CaseTag.Failed, result.Tag, "Tag == Failed");

        AssertTrue(result.TryGetFailed(out _), "TryGetFailed returns true on Failed");
        AssertFalse(result.TryGetCompleted(out var bogus), "TryGetCompleted returns false on Failed");
        bogus?.Dispose();
    }

    public void TestCrossModuleClassResult_Canceled_TryGetsFail()
    {
        using var result = TestLibFunctions.MakeCrossModuleClassCanceled();
        AssertEqual(CrossModuleClassResult.CaseTag.Canceled, result.Tag, "Tag == Canceled");

        AssertFalse(result.TryGetCompleted(out var bogusSession),
            "TryGetCompleted returns false on Canceled");
        bogusSession?.Dispose();
        AssertFalse(result.TryGetFailed(out _), "TryGetFailed returns false on Canceled");
    }

    /// Locks the C#-side factory path for the cross-module class payload. A C#-built
    /// .completed(DependencyService) must round-trip through Tag + TryGet so the
    /// factory's PInvoke shape is verified across the module boundary.
    public void TestCrossModuleClassResult_Completed_FactoryRoundTrip()
    {
        using var svc = new DependencyService("c#-built");
        using var result = CrossModuleClassResult.Completed(svc);
        AssertEqual(CrossModuleClassResult.CaseTag.Completed, result.Tag,
            "C#-built Completed has correct tag");

        AssertTrue(result.TryGetCompleted(out var roundTrip),
            "TryGetCompleted returns true on C#-built Completed");
        using (roundTrip)
        {
            AssertEqual("c#-built", roundTrip!.Name,
                "C#-side factory round-trips foreign class payload Name");
        }
    }

    public void TestCrossModuleFrozenStructResult_Completed_ExtractsForeignFrozenStructPayload()
    {
        using var result = TestLibFunctions.MakeCrossModuleFrozenStructCompleted(3.5, 4.25);
        AssertEqual(CrossModuleFrozenStructResult.CaseTag.Completed, result.Tag,
            "Tag == Completed");

        AssertTrue(result.TryGetCompleted(out var point), "TryGetCompleted returns true");
        AssertEqual(3.5, point.X, "Frozen struct payload round-trips X");
        AssertEqual(4.25, point.Y, "Frozen struct payload round-trips Y");
    }

    public void TestCrossModuleFrozenStructResult_Failed_TryGetCompletedFails()
    {
        using var result = TestLibFunctions.MakeCrossModuleFrozenStructFailed("err");
        AssertEqual(CrossModuleFrozenStructResult.CaseTag.Failed, result.Tag, "Tag == Failed");
        AssertFalse(result.TryGetCompleted(out _), "TryGetCompleted returns false on Failed");
    }

    public void TestCrossModuleNonFrozenStructResult_Completed_ExtractsForeignNonFrozenPayload()
    {
        using var result = TestLibFunctions.MakeCrossModuleNonFrozenStructCompleted("cfg", 7);
        AssertEqual(CrossModuleNonFrozenStructResult.CaseTag.Completed, result.Tag,
            "Tag == Completed");

        AssertTrue(result.TryGetCompleted(out var cfg), "TryGetCompleted returns true");
        using (cfg)
        {
            AssertEqual("cfg", cfg!.Name, "Non-frozen struct payload round-trips Name");
            AssertEqual(7, cfg.Version, "Non-frozen struct payload round-trips Version");

            // Round-trip the extracted payload through a Swift function in the
            // *current* module that consumes a foreign-module DependencyConfig.
            // This proves the extractor produced a real, dispatchable foreign
            // SafeHandle — not a placeholder that happens to read correct fields
            // but can't be passed back across the C ABI boundary.
            using var upgraded = TestLibFunctions.UpgradeDependencyConfig(cfg);
            AssertEqual("cfg", upgraded.Name, "Upgraded payload preserves Name");
            AssertEqual(8, upgraded.Version, "Upgraded payload increments Version (7 -> 8)");

            // The original enum payload must still be readable after the
            // extracted SafeHandle has been used (no aliasing / move corruption).
            AssertEqual("cfg", cfg.Name, "Original payload Name still readable after re-use");
            AssertEqual(7, cfg.Version, "Original payload Version unchanged after re-use");
        }

        // After extracting + disposing the payload, the enum SafeHandle itself
        // must remain valid: TryGetCompleted is allowed to be called twice and
        // produce a *fresh* foreign payload each time, so the Tag/payload buffer
        // inside the enum is not consumed by extraction.
        AssertTrue(result.TryGetCompleted(out var cfgAgain),
            "TryGetCompleted returns true on second extraction");
        using (cfgAgain)
        {
            AssertEqual("cfg", cfgAgain!.Name,
                "Second extraction yields a fresh, equally-valued payload");
            AssertEqual(7, cfgAgain.Version,
                "Second extraction yields a fresh, equally-valued payload");
        }
    }

    public void TestCrossModuleNonFrozenStructResult_Canceled_TryGetsFail()
    {
        using var result = TestLibFunctions.MakeCrossModuleNonFrozenStructCanceled();
        AssertEqual(CrossModuleNonFrozenStructResult.CaseTag.Canceled, result.Tag,
            "Tag == Canceled");

        AssertFalse(result.TryGetCompleted(out var bogusCfg),
            "TryGetCompleted returns false on Canceled");
        bogusCfg?.Dispose();
        AssertFalse(result.TryGetFailed(out _),
            "TryGetFailed returns false on Canceled");
    }

    public void TestCrossModuleNonFrozenStructResult_Failed_TryGetCompletedFails()
    {
        using var result = TestLibFunctions.MakeCrossModuleNonFrozenStructFailed("err");
        AssertEqual(CrossModuleNonFrozenStructResult.CaseTag.Failed, result.Tag, "Tag == Failed");
        AssertFalse(result.TryGetCompleted(out var bogus), "TryGetCompleted returns false on Failed");
        bogus?.Dispose();
        AssertTrue(result.TryGetFailed(out _), "TryGetFailed returns true on Failed");
    }

    /// Locks the C#-side factory path for the cross-module non-frozen struct
    /// payload. A C#-built `.completed(DependencyConfig)` must round-trip through
    /// Tag + TryGet, and the extracted SafeHandle must be independent enough to
    /// dispose without invalidating the enum (verifying the factory + extractor
    /// agree on ownership semantics, not aliasing the same buffer).
    public void TestCrossModuleNonFrozenStructResult_Completed_FactoryRoundTrip()
    {
        using var cfgIn = SwiftBindingsTestLibDependency.Functions.MakeDependencyConfig("c#-built", 42);
        using var result = CrossModuleNonFrozenStructResult.Completed(cfgIn);
        AssertEqual(CrossModuleNonFrozenStructResult.CaseTag.Completed, result.Tag,
            "C#-built Completed has correct tag");

        AssertTrue(result.TryGetCompleted(out var roundTrip),
            "TryGetCompleted returns true on C#-built Completed");
        AssertEqual("c#-built", roundTrip!.Name,
            "C#-side factory round-trips non-frozen struct payload Name");
        AssertEqual(42, roundTrip.Version,
            "C#-side factory round-trips non-frozen struct payload Version");
        roundTrip.Dispose();

        // Disposing the extracted payload must not invalidate the enum's own
        // payload buffer — the factory's InitializeWithCopy must have produced
        // an independent foreign value, not aliased the caller's SafeHandle.
        AssertEqual(CrossModuleNonFrozenStructResult.CaseTag.Completed, result.Tag,
            "Enum Tag still readable after extracted payload disposed");
        AssertTrue(result.TryGetCompleted(out var afterDispose),
            "TryGetCompleted still succeeds after extracted payload disposed");
        using (afterDispose)
        {
            AssertEqual("c#-built", afterDispose!.Name,
                "Enum payload still readable after extracted copy disposed");
            AssertEqual(42, afterDispose.Version,
                "Enum payload still readable after extracted copy disposed");
        }
    }
}
