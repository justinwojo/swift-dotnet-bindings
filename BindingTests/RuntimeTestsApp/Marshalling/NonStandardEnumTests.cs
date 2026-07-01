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

    [Skip("Non-@objc enums export no raw values: the Swift compiler strips explicit raw values (execute=4) from the .swiftinterface for plain `enum: UInt32`, and the ABI JSON lacks them too, so there is no source of truth — execute necessarily falls back to the declaration-order ordinal 3. @objc enums DO preserve raw values; that path is gated by the AuthErrorCodeLike tests below.")]
    public void TestPermissionCaseValues()
    {
        // Swift declares none=0, read=1, write=2, execute=4. For a non-@objc enum those raw
        // values are absent from every generator input, so execute emits as the ordinal 3.
        // These assertions encode the INTENDED Swift values; if a future toolchain preserves
        // non-@objc raw values, un-skipping this will start passing.
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

    public void TestAuthErrorCodeLikeCaseValues()
    {
        // An Int-backed enum with large, non-sequential raw values (a common SDK error-code
        // shape). The C# member must carry the actual Swift raw value, NOT the
        // declaration-order ordinal (which would make WrongPassword == 0).
        AssertEqual((long)17009, (long)AuthErrorCodeLike.WrongPassword, "WrongPassword raw value is 17009");
        AssertEqual((long)17011, (long)AuthErrorCodeLike.UserNotFound, "UserNotFound raw value is 17011");
        AssertEqual((long)17020, (long)AuthErrorCodeLike.NetworkError, "NetworkError raw value is 17020");
        TestLogger.Info("AuthErrorCodeLike case values passed");
    }

    public void TestAuthErrorCodeLikeBackingType()
    {
        // Swift `Int` → C# `long` underlying type (64-bit ABI).
        var underlyingType = Enum.GetUnderlyingType(typeof(AuthErrorCodeLike));
        AssertEqual(typeof(long), underlyingType, "AuthErrorCodeLike backing type is long");
    }

    public void TestAuthErrorCodeRoundTripsThroughCdecl()
    {
        // Exercises the @_cdecl param-conversion switch (C# scalar 17009 -> Swift
        // .wrongPassword) AND the return switch (Swift .wrongPassword -> C# scalar 17009) at
        // runtime. A correct fix returns the same case the caller passed.
        AssertEqual(AuthErrorCodeLike.WrongPassword,
            SwiftBindingsTestLib.Functions.EchoAuthErrorCode(AuthErrorCodeLike.WrongPassword),
            "echoAuthErrorCode round-trips wrongPassword");
        AssertEqual(AuthErrorCodeLike.UserNotFound,
            SwiftBindingsTestLib.Functions.EchoAuthErrorCode(AuthErrorCodeLike.UserNotFound),
            "echoAuthErrorCode round-trips userNotFound");
        // Return-only path: the scalar Swift hands back must equal the Swift raw value.
        // (The parameterless `wrongPasswordCode()` projects to `GetWrongPasswordCode` under
        // the noun→Get name-shaping rule.)
        AssertEqual(AuthErrorCodeLike.WrongPassword,
            SwiftBindingsTestLib.Functions.GetWrongPasswordCode(),
            "wrongPasswordCode() returns wrongPassword");
        AssertEqual((long)17009,
            (long)SwiftBindingsTestLib.Functions.GetWrongPasswordCode(),
            "wrongPasswordCode() scalar equals 17009");
        TestLogger.Info("AuthErrorCodeLike @_cdecl round-trips passed");
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
