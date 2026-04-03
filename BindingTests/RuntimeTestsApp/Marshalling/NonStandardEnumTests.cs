// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests for enums with non-Int32 backing types: SecurityError (ushort),
/// FeatureFlag (long), Permission (uint).
/// </summary>
public class NonStandardEnumTests : TestBase
{
    public NonStandardEnumTests(TestResults results) : base(results) { }

    #region Tier 1 — Case Values + Backing Type Verification

    public void TestSecurityErrorCaseValues()
    {
        AssertEqual(SecurityError.None, (SecurityError)(ushort)0, "None is 0");
        AssertEqual(SecurityError.BadCertificate, (SecurityError)(ushort)1, "BadCertificate is 1");
        AssertEqual(SecurityError.PinningFailed, (SecurityError)(ushort)2, "PinningFailed is 2");
        AssertEqual(SecurityError.InvalidChain, (SecurityError)(ushort)3, "InvalidChain is 3");
        TestLogger.Info("SecurityError case values passed");
    }

    public void TestSecurityErrorBackingType()
    {
        // SecurityError should be backed by ushort
        var underlyingType = Enum.GetUnderlyingType(typeof(SecurityError));
        AssertEqual(typeof(ushort), underlyingType, "SecurityError backing type is ushort");
        TestLogger.Info($"SecurityError underlying type: {underlyingType.Name}");
    }

    public void TestFeatureFlagCaseValues()
    {
        AssertEqual(FeatureFlag.Disabled, (FeatureFlag)(long)0, "Disabled is 0");
        AssertEqual(FeatureFlag.Enabled, (FeatureFlag)(long)1, "Enabled is 1");
        AssertEqual(FeatureFlag.Experimental, (FeatureFlag)(long)2, "Experimental is 2");
        TestLogger.Info("FeatureFlag case values passed");
    }

    public void TestFeatureFlagBackingType()
    {
        var underlyingType = Enum.GetUnderlyingType(typeof(FeatureFlag));
        AssertEqual(typeof(long), underlyingType, "FeatureFlag backing type is long");
        TestLogger.Info($"FeatureFlag underlying type: {underlyingType.Name}");
    }

    [Skip("Blocked: .swiftinterface strips integer raw values and ABI JSON lacks them — no source of truth for non-sequential values (execute=4 emitted as 3)")]
    public void TestPermissionCaseValues()
    {
        // NOTE: Swift declares none=0, read=1, write=2, execute=4 but the generator
        // currently emits sequential ordinals (0,1,2,3) for non-Int32 enums.
        // These assertions test the INTENDED Swift values — if the generator is fixed
        // to preserve gap values, these will pass; if still ordinal, the execute check
        // will catch the bug.
        AssertEqual(Permission.None, (Permission)(uint)0, "None is 0");
        AssertEqual(Permission.Read, (Permission)(uint)1, "Read is 1");
        AssertEqual(Permission.Write, (Permission)(uint)2, "Write is 2");
        AssertEqual(Permission.Execute, (Permission)(uint)4, "Execute is 4 (Swift raw value)");
        TestLogger.Info("Permission case values passed");
    }

    public void TestPermissionBackingType()
    {
        var underlyingType = Enum.GetUnderlyingType(typeof(Permission));
        AssertEqual(typeof(uint), underlyingType, "Permission backing type is uint");
        TestLogger.Info($"Permission underlying type: {underlyingType.Name}");
    }

    public void TestDistinctValues()
    {
        // Verify all cases within each enum have distinct values
        var securityValues = new HashSet<ushort>
        {
            (ushort)SecurityError.None,
            (ushort)SecurityError.BadCertificate,
            (ushort)SecurityError.PinningFailed,
            (ushort)SecurityError.InvalidChain,
        };
        AssertEqual(4, securityValues.Count, "SecurityError has 4 distinct values");

        var flagValues = new HashSet<long>
        {
            (long)FeatureFlag.Disabled,
            (long)FeatureFlag.Enabled,
            (long)FeatureFlag.Experimental,
        };
        AssertEqual(3, flagValues.Count, "FeatureFlag has 3 distinct values");

        var permValues = new HashSet<uint>
        {
            (uint)Permission.None,
            (uint)Permission.Read,
            (uint)Permission.Write,
            (uint)Permission.Execute,
        };
        AssertEqual(4, permValues.Count, "Permission has 4 distinct values");

        TestLogger.Info("All non-standard enums have distinct values");
    }

    public void TestCastRoundTrips()
    {
        // ushort round-trip
        AssertEqual(SecurityError.PinningFailed, (SecurityError)(ushort)SecurityError.PinningFailed, "SecurityError ushort round-trip");

        // long round-trip
        AssertEqual(FeatureFlag.Experimental, (FeatureFlag)(long)FeatureFlag.Experimental, "FeatureFlag long round-trip");

        // uint round-trip
        AssertEqual(Permission.Execute, (Permission)(uint)Permission.Execute, "Permission uint round-trip");

        TestLogger.Info("Cast round-trips passed");
    }

    #endregion

    #region Single-Case Enum Skip Verification (BUG-2)

    public void TestSingleCaseModeIsSkipped()
    {
        // SingleCaseMode has 1 case and no payload — TypeMetadata.Size == 0.
        // Generator should skip it entirely to avoid marshalling crash.
        var type = typeof(SecurityError).Assembly.GetType("SwiftBindingsTestLib.SingleCaseMode");
        AssertNull(type, "SingleCaseMode should not be emitted (zero runtime size)");
        TestLogger.Info("SingleCaseMode correctly skipped by generator");
    }

    public void TestSingletonFlagIsEmitted()
    {
        // SingletonFlag has 1 case with Int32 raw value. Despite being single-case (Size==0 in Swift),
        // int-backed enums are safe: C# enum uses the raw value as backing (4 bytes), not Swift's
        // zero-size layout. Only String-backed enums (emitted as C# classes with SafeHandle) hit the
        // Size==0 problem.
        var type = typeof(SecurityError).Assembly.GetType("SwiftBindingsTestLib.SingletonFlag");
        AssertNotNull(type, "SingletonFlag should be emitted (int-backed enum is safe)");
        AssertTrue(type!.IsEnum, "SingletonFlag is a C# enum");
        TestLogger.Info("SingletonFlag correctly emitted as C# enum");
    }

    public void TestDualCaseModeIsEmitted()
    {
        // DualCaseMode has 2 cases — should be emitted normally.
        var type = typeof(SecurityError).Assembly.GetType("SwiftBindingsTestLib.DualCaseMode");
        AssertNotNull(type, "DualCaseMode should be emitted (has 2 cases)");
        TestLogger.Info("DualCaseMode correctly emitted by generator");
    }

    #endregion
}
