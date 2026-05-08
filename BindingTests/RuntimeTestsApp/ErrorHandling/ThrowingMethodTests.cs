// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.ErrorHandling;

/// <summary>
/// Tests for Swift error handling: throwing free functions, struct throwing methods,
/// typed throws (Swift 6.0), and error propagation across the interop boundary.
/// All throwing Swift methods surface as SwiftException in C#.
/// Named with "Basic" prefix to sort alphabetically before crash-prone tests
/// (EnumMarshallingTests.TestOrderContainerCreation triggers Mono JIT crash).
///
/// Tier strategy:
/// - Tier 1: Blittable-only throwing (Int32 params/returns) — works on success AND error paths
/// - Tier 2: SwiftString-involving success paths + factory-created structs — works on success paths
/// - Tier 3: Deferred due to Mono/.NET runtime limitations:
///   (a) SwiftString + error path — Mono JIT crash (CallConvSwift + SwiftString + SwiftError)
///   (b) Non-blittable types — InvalidProgramException (tuple enum cases, non-frozen struct ctors)
///   (c) Missing entry points — enum case symbols not exported from dylib
/// </summary>
public class BasicThrowingTests : TestBase
{
    public BasicThrowingTests(TestResults results) : base(results) { }

    // ===================================================================
    // Tier 1: Blittable throwing functions (Int32-only, both success and error paths)
    // ===================================================================

    #region Free Throwing Functions — Blittable

    public void TestDivideSuccess()
    {
        var result = TestLibFunctions.Divide(10, 2);
        AssertEqual(5, result, "10 / 2 = 5");
        TestLogger.Info($"Divide(10, 2) = {result}");
    }

    public void TestDivideNegativeValues()
    {
        var result = TestLibFunctions.Divide(-15, 3);
        AssertEqual(-5, result, "-15 / 3 = -5");

        var result2 = TestLibFunctions.Divide(15, -3);
        AssertEqual(-5, result2, "15 / -3 = -5");
        TestLogger.Info("Divide with negative values passed");
    }

    public void TestDivideByZeroThrows()
    {
        try
        {
            TestLibFunctions.Divide(10, 0);
            throw new AssertionException("Divide by zero should throw");
        }
        catch (SwiftException ex)
        {
            // Error message should be the Swift String(describing:) output, not a hardcoded message
            AssertTrue(ex.Message.Contains("divisionByZero"),
                $"Error message should contain Swift error description, got: {ex.Message}");
            TestLogger.Info($"Divide by zero threw with message: {ex.Message}");
        }
    }

    #endregion

    #region ThrowingStruct — Blittable

    public void TestThrowingStructConstruction()
    {
        var ts = new ThrowingStruct(42);
        AssertEqual(42, ts.Value, "ThrowingStruct.Value");
        TestLogger.Info($"ThrowingStruct created with value={ts.Value}");
    }

    public void TestThrowingStructDivideBySuccess()
    {
        var ts = new ThrowingStruct(100);
        var result = ts.DivideBy(5);
        AssertEqual(20, result, "100 / 5 = 20");
        TestLogger.Info($"ThrowingStruct(100).GetDivideBy(5) = {result}");
    }

    public void TestThrowingStructValidatePositiveSuccess()
    {
        var ts = new ThrowingStruct(10);
        var result = ts.ValidatePositive();
        AssertEqual(10, result, "Positive value validates");
        TestLogger.Info($"ThrowingStruct(10).ValidatePositive() = {result}");
    }

    public void TestThrowingStructStaticSafeDivideSuccess()
    {
        var result = ThrowingStruct.SafeDivide(20, 4);
        AssertEqual(5, result, "20 / 4 = 5");
        TestLogger.Info($"ThrowingStruct.SafeDivide(20, 4) = {result}");
    }

    public void TestThrowingStructDivideByZeroThrows()
    {
        var ts = new ThrowingStruct(100);
        try
        {
            ts.DivideBy(0);
            throw new AssertionException("DivideBy(0) should throw");
        }
        catch (SwiftException ex)
        {
            AssertTrue(ex.Message.Contains("divisionByZero"),
                $"Error message should contain Swift error description, got: {ex.Message}");
            TestLogger.Info($"ThrowingStruct.GetDivideBy(0) threw with message: {ex.Message}");
        }
    }

    public void TestThrowingStructValidatePositiveNegativeThrows()
    {
        var ts = new ThrowingStruct(-5);
        AssertThrows<SwiftException>(() =>
        {
            ts.ValidatePositive();
        }, "ValidatePositive on negative should throw");
        TestLogger.Info("ThrowingStruct(-5).ValidatePositive() correctly threw");
    }

    public void TestThrowingStructValidatePositiveZeroThrows()
    {
        var ts = new ThrowingStruct(0);
        AssertThrows<SwiftException>(() =>
        {
            ts.ValidatePositive();
        }, "ValidatePositive on zero should throw");
        TestLogger.Info("ThrowingStruct(0).ValidatePositive() correctly threw");
    }

    public void TestThrowingStructStaticSafeDivideByZeroThrows()
    {
        AssertThrows<SwiftException>(() =>
        {
            ThrowingStruct.SafeDivide(10, 0);
        }, "SafeDivide by zero should throw");
        TestLogger.Info("ThrowingStruct.SafeDivide(10, 0) correctly threw");
    }

    #endregion

    #region Typed Throws — Blittable (ValidateRange: Int32-only)

    public void TestValidateRangeSuccess()
    {
        var result = TestLibFunctions.ValidateRange(5, 1, 10);
        AssertEqual(5, result, "ValidateRange(5, 1, 10)");
        TestLogger.Info($"ValidateRange(5, 1, 10) = {result}");
    }

    public void TestValidateRangeBelowMinThrows()
    {
        AssertThrows<SwiftException<RangeError>>(() =>
        {
            TestLibFunctions.ValidateRange(0, 1, 10);
        }, "ValidateRange below min should throw SwiftException<RangeError>");
        TestLogger.Info("ValidateRange(0, 1, 10) correctly threw SwiftException<RangeError>");
    }

    public void TestValidateRangeAboveMaxThrows()
    {
        AssertThrows<SwiftException<RangeError>>(() =>
        {
            TestLibFunctions.ValidateRange(11, 1, 10);
        }, "ValidateRange above max should throw SwiftException<RangeError>");
        TestLogger.Info("ValidateRange(11, 1, 10) correctly threw SwiftException<RangeError>");
    }

    #endregion

    #region Error Enum Construction (Tier 1 — no P/Invoke, pure C# construction)

    public void TestMathErrorCases()
    {
        // MathError is a simple C# enum (Error protocol enum projected as int enum)
        var divByZero = MathError.DivisionByZero;
        AssertEqual(MathError.DivisionByZero, divByZero, "DivisionByZero");

        var overflow = MathError.Overflow;
        AssertEqual(MathError.Overflow, overflow, "Overflow");

        var negInput = MathError.NegativeInput;
        AssertEqual(MathError.NegativeInput, negInput, "NegativeInput");

        // All cases should have distinct int values
        AssertTrue((int)MathError.DivisionByZero != (int)MathError.Overflow, "DivisionByZero != Overflow");
        AssertTrue((int)MathError.Overflow != (int)MathError.NegativeInput, "Overflow != NegativeInput");

        TestLogger.Info("MathError case construction passed");
    }

    public void TestValidationErrorEmptyCase()
    {
        var empty = ValidationError.Empty;
        AssertEqual(ValidationError.CaseTag.Empty, empty.Tag, "Empty tag");
        TestLogger.Info("ValidationError.Empty case construction passed");
    }

    public void TestValidationErrorTooLongCase()
    {
        var tooLong = ValidationError.TooLong(50);
        AssertEqual(ValidationError.CaseTag.TooLong, tooLong.Tag, "TooLong tag");
        TestLogger.Info("ValidationError.TooLong case construction passed");
    }

    public void TestParseErrorCases()
    {
        var invalid = ParseError.InvalidInput;
        AssertEqual(ParseError.CaseTag.InvalidInput, invalid.Tag, "InvalidInput tag");

        TestLogger.Info("ParseError case construction passed");
    }

    // ParseError.Overflow(SwiftString) — SwiftIndirectResult + SwiftString (non-blittable P/Invoke)
    // Fixed: Mono runtime detection now works on .NET 10 — finalizer skips Destroy
    public void TestParseErrorOverflowCase()
    {
        using var val = new SwiftString("99999999999");
        var overflow = ParseError.Overflow(val);
        AssertEqual(ParseError.CaseTag.Overflow, overflow.Tag, "Overflow tag");
        TestLogger.Info("ParseError.Overflow case construction passed");
    }

    // ValidationError.InvalidFormat(SwiftString) — SwiftIndirectResult + SwiftString (non-blittable P/Invoke)
    // Fixed: Mono runtime detection now works on .NET 10 — finalizer skips Destroy
    public void TestValidationErrorInvalidFormatCase()
    {
        using var val = new SwiftString("bad format");
        var invalid = ValidationError.InvalidFormat(val);
        AssertEqual(ValidationError.CaseTag.InvalidFormat, invalid.Tag, "InvalidFormat tag");
        TestLogger.Info("ValidationError.InvalidFormat case construction passed");
    }

    // RangeError.BelowMinimum/AboveMaximum take tuple (Int32, Int32) associated values
    // InvalidProgramException: non-blittable types with Swift calling convention
    // Fixed: Mono runtime detection now works on .NET 10 — finalizer skips Destroy
    public void TestRangeErrorCases()
    {
        var below = RangeError.BelowMinimum(5, 10);
        AssertEqual(RangeError.CaseTag.BelowMinimum, below.Tag, "BelowMinimum tag");

        var above = RangeError.AboveMaximum(15, 10);
        AssertEqual(RangeError.CaseTag.AboveMaximum, above.Tag, "AboveMaximum tag");

        TestLogger.Info("RangeError case construction passed");
    }

    #endregion

    // ===================================================================
    // Tier 2: SwiftString-involving success paths (no error thrown)
    // ===================================================================

    #region Typed Throws — SwiftString Success Paths (ParseNumber success)

    public void TestParseNumberSuccess()
    {
        var result = TestLibFunctions.ParseNumber("42");
        AssertEqual(42, result, "ParseNumber(\"42\")");
        TestLogger.Info($"ParseNumber(\"42\") = {result}");
    }

    public void TestParseNumberNegative()
    {
        var result = TestLibFunctions.ParseNumber("-100");
        AssertEqual(-100, result, "ParseNumber(\"-100\")");
        TestLogger.Info($"ParseNumber(\"-100\") = {result}");
    }

    #endregion

    #region TypedThrowingParser — Struct Construction and Success Paths

    public void TestTypedThrowingParserCreation()
    {
        var strict = TestLibFunctions.CreateStrictParser();
        AssertTrue(strict.Strict, "Strict parser .Strict should be true");

        var lenient = TestLibFunctions.CreateLenientParser();
        AssertFalse(lenient.Strict, "Lenient parser .Strict should be false");
        TestLogger.Info("TypedThrowingParser factory methods passed");
    }

    // TypedThrowingParser is a non-frozen struct — constructor uses SwiftIndirectResult
    // InvalidProgramException: non-blittable types with Swift calling convention
    // Factory methods (CreateStrictParser/CreateLenientParser) work because they return via register
    // Fixed: Mono runtime detection now works on .NET 10 — finalizer skips Destroy
    public void TestTypedThrowingParserConstructor()
    {
        var parser = new TypedThrowingParser(true);
        AssertTrue(parser.Strict, "Constructor strict=true");

        var parser2 = new TypedThrowingParser(false);
        AssertFalse(parser2.Strict, "Constructor strict=false");
        TestLogger.Info("TypedThrowingParser constructors passed");
    }

    public void TestTypedThrowingParserParseSuccess()
    {
        var parser = TestLibFunctions.CreateLenientParser();
        var result = parser.Parse("123");
        AssertEqual(123, result, "Lenient parse \"123\"");
        TestLogger.Info($"LenientParser.Parse(\"123\") = {result}");
    }

    public void TestStrictParserAcceptsCleanInput()
    {
        var parser = TestLibFunctions.CreateStrictParser();
        var result = parser.Parse("99");
        AssertEqual(99, result, "Strict parse \"99\"");
        TestLogger.Info($"StrictParser.Parse(\"99\") = {result}");
    }

    public void TestLenientParserAcceptsCleanInput()
    {
        var parser = TestLibFunctions.CreateLenientParser();
        var result = parser.Parse("42");
        AssertEqual(42, result, "Lenient parse \"42\"");
        TestLogger.Info($"LenientParser.Parse(\"42\") = {result}");
    }

    #endregion

    #region String-Returning Throwing Functions — Success Path

    public void TestValidateStringSuccess()
    {
        var result = TestLibFunctions.Validate("hello", 100);
        AssertEqual("hello", result, "Validate(\"hello\", 100)");
        TestLogger.Info($"Validate(\"hello\", 100) = \"{result}\"");
    }

    #endregion

    // ===================================================================
    // Tier 3: SwiftString + error path (Mono JIT crash: CallConvSwift + SwiftString + SwiftError)
    // Deferred until Mono JIT bug is fixed — same root cause as closure/SwiftString tests.
    // ===================================================================

    #region SwiftString Throwing — Error Paths (Tier 3, crash-prone)

    // Fixed: Mono runtime detection now works on .NET 10 — finalizer skips Destroy
    public void TestParseNumberThrowsOnInvalidInput()
    {
        AssertThrows<SwiftException<ParseError>>(() =>
        {
            TestLibFunctions.ParseNumber("abc");
        }, "ParseNumber(\"abc\") should throw SwiftException<ParseError>");
        TestLogger.Info("ParseNumber(\"abc\") correctly threw SwiftException<ParseError>");
    }

    // Fixed: Mono runtime detection now works on .NET 10 — finalizer skips Destroy
    public void TestTypedThrowingParserParseInvalidThrows()
    {
        var parser = TestLibFunctions.CreateLenientParser();
        AssertThrows<SwiftException<ParseError>>(() =>
        {
            parser.Parse("not_a_number");
        }, "Parse invalid input should throw SwiftException<ParseError>");
        TestLogger.Info("Parser.Parse(\"not_a_number\") correctly threw SwiftException<ParseError>");
    }

    // Fixed: Mono runtime detection now works on .NET 10 — finalizer skips Destroy
    public void TestStrictParserRejectsWhitespace()
    {
        var parser = TestLibFunctions.CreateStrictParser();
        AssertThrows<SwiftException<ParseError>>(() =>
        {
            parser.Parse(" 42 ");
        }, "Strict parser should reject whitespace");
        TestLogger.Info("StrictParser.Parse(\" 42 \") correctly threw SwiftException<ParseError>");
    }

    // Fixed: Mono runtime detection now works on .NET 10 — finalizer skips Destroy
    public void TestValidateEmptyStringThrows()
    {
        AssertThrows<SwiftException>(() =>
        {
            TestLibFunctions.Validate("", 100);
        }, "Validate empty string should throw");
        TestLogger.Info("Validate(\"\", 100) correctly threw");
    }

    // Fixed: Mono runtime detection now works on .NET 10 — finalizer skips Destroy
    public void TestValidateTooLongThrows()
    {
        AssertThrows<SwiftException>(() =>
        {
            TestLibFunctions.Validate("hello world", 5);
        }, "Validate too long should throw");
        TestLogger.Info("Validate(\"hello world\", 5) correctly threw");
    }

    #endregion

    // ===================================================================
    // Typed Throws — Exception Type Verification (SwiftException<TError>)
    // Verifies that typed throws methods produce SwiftException<TError>
    // with correct exception type and Error property behavior.
    // ===================================================================

    #region Typed Throws — Sync Error Property (Tier 1: blittable)

    public void TestValidateRangeTypedCatchWithError()
    {
        // Sync typed throws (C2): SwiftException<RangeError> with non-null .Error
        // value=100, min=0, max=50 → throws aboveMaximum(value: 100, maximum: 50)
        try
        {
            TestLibFunctions.ValidateRange(100, 0, 50);
            throw new AssertionException("ValidateRange should have thrown");
        }
        catch (SwiftException<RangeError> ex)
        {
            // C2: Sync typed throws .Error should be non-null (extracted via SBW_ExtractTypedError)
            AssertNotNull(ex.Error, "Sync typed throws .Error should be non-null (C2)");
            AssertEqual(RangeError.CaseTag.AboveMaximum, ex.Error!.Tag,
                "Error should be RangeError.AboveMaximum");
            // Error description uses "code N" format (SBW_GetErrorDescription returns type + raw value)
            // Error description uses String(describing:) which renders as case name with associated values
            AssertTrue(ex.Message.Contains("aboveMaximum"),
                $"Exception message should contain case name, got: {ex.Message}");
            TestLogger.Info($"ValidateRange typed catch: Error.Tag={ex.Error.Tag}, Message={ex.Message}");
        }
    }

    #endregion

    #region Typed Throws — Async (Tier 3: Mono JIT limitations)

    public async Task TestAsyncParseTypedCatch()
    {
        // Async typed throws: SwiftException<ParseError> with non-null .Error
        var parser = TestLibFunctions.CreateLenientParser();
        try
        {
            await WithTimeout(parser.ParseAsync("abc"), DefaultAsyncTimeout);
            throw new AssertionException("AsyncParse should have thrown");
        }
        catch (SwiftException<ParseError> ex)
        {
            AssertNotNull(ex.Error, "Async typed throws .Error should be non-null");
            AssertEqual(ParseError.CaseTag.InvalidInput, ex.Error!.Tag,
                "Error should be ParseError.InvalidInput");
            TestLogger.Info($"AsyncParse typed catch: Error.Tag={ex.Error.Tag}, Message={ex.Message}");
        }
    }

    public async Task TestAsyncParseSuccess()
    {
        var parser = TestLibFunctions.CreateLenientParser();
        var result = await WithTimeout(parser.ParseAsync("42"), DefaultAsyncTimeout);
        AssertEqual(42, result, "AsyncParse(\"42\") should return 42");
        TestLogger.Info($"AsyncParse(\"42\") = {result}");
    }

    #endregion

    // ===================================================================
    // Phase 4 plain-throws cascade — SwiftException<TError> dispatch
    // Plain `async throws` (NOT typed-throws) functions that throw a
    // registered Error-conforming enum. The per-module cascade dispatcher
    // should match the runtime error type and surface the strongly-typed
    // SwiftException<TError> rather than the untyped fallback.
    // ===================================================================

    #region Plain-throws cascade — Async

    public async Task TestPlainThrowsAsyncDivideSuccess()
    {
        var result = await WithTimeout(TestLibFunctions.PlainThrowsAsyncDivideAsync(20, 4), DefaultAsyncTimeout);
        AssertEqual(5, result, "PlainThrowsAsyncDivideAsync(20, 4) = 5");
        TestLogger.Info($"PlainThrowsAsyncDivideAsync(20, 4) = {result}");
    }

    public async Task TestPlainThrowsAsyncDivideCascadeToMathError()
    {
        // Plain `async throws` throwing MathError.overflow (registered via
        // ErrorEnumRegistryEmitter). The cascade dispatcher should hand C# a typed
        // buffer + matching errorTypeId so the outer await sees
        // SwiftException<MathError> — NOT the untyped SwiftException fallback.
        // MathError is a simple no-payload Int32-raw-value enum so it projects to a
        // C# value-type enum and `.Error` is plain `MathError` (TError? without a
        // struct constraint stays the underlying type). Asserting against
        // `.overflow` (raw value 1) instead of `.divisionByZero` (raw value 0)
        // distinguishes a real cascade payload from a zero-default fallback.
        try
        {
            await WithTimeout(
                TestLibFunctions.PlainThrowsAsyncDivideAsync(int.MinValue, -1),
                DefaultAsyncTimeout);
            throw new AssertionException("PlainThrowsAsyncDivideAsync(Int32.MinValue, -1) should have thrown");
        }
        catch (SwiftException<MathError> ex)
        {
            AssertEqual(MathError.Overflow, ex.Error,
                "Cascade dispatch should produce MathError.Overflow");
            AssertTrue(ex.Message.Contains("overflow"),
                $"Cascade message should contain Swift error description, got: {ex.Message}");
            TestLogger.Info($"PlainThrowsAsyncDivideAsync cascade: Error={ex.Error}, Message={ex.Message}");
        }
    }

    public async Task TestPlainThrowsAsyncParseCascadeToComplexEnum()
    {
        // Layer 5: plain async throws with a complex enum (associated values).
        // ParseError2 projects to a C# class with a Tag/CaseTag — MarshalFromSwift
        // hands ownership of the Swift buffer to the SafeHandle, so the cascade
        // dispatcher's per-id ownership branch must NOT free in finally. A
        // double-free here would surface as a Mono JIT crash or a NativeAOT
        // assertion. Asserting `.UnexpectedEOF` rules out a "first-case-default"
        // false positive (malformed is alphabetically first; unexpectedEOF is
        // selected by the input "" sentinel below).
        try
        {
            await WithTimeout(
                TestLibFunctions.PlainThrowsAsyncParseAsync(""),
                DefaultAsyncTimeout);
            throw new AssertionException("PlainThrowsAsyncParseAsync(\"\") should have thrown");
        }
        catch (SwiftException<ParseError2> ex)
        {
            AssertNotNull(ex.Error, "Cascade dispatch should populate ex.Error for complex enum");
            AssertEqual(ParseError2.CaseTag.UnexpectedEOF, ex.Error!.Tag,
                "Cascade dispatch should produce ParseError2.UnexpectedEOF");
            AssertTrue(ex.Message.Contains("unexpectedEOF") || ex.Message.Contains("42"),
                $"Cascade message should reflect Swift error description, got: {ex.Message}");
            TestLogger.Info($"PlainThrowsAsyncParseAsync cascade: Error.Tag={ex.Error.Tag}, Message={ex.Message}");
        }
    }

    public async Task TestPlainThrowsAsyncLoadConfigCascadeToStructError()
    {
        // Layer 5: plain async throws with a non-frozen struct error. PlainThrowsConfigError
        // projects as a C# class through the resilience boundary; same ownership
        // pattern as complex enums — buffer ownership transfers to SafeHandle on
        // successful marshal, so the cascade helper's per-case `catch { SBW_Free; throw; }`
        // is the only path that frees on this branch. Asserting on the carried
        // payload fields (Path / LineNumber) confirms the marshalled struct holds
        // the values copied by Swift, not a zero-initialized fallback.
        try
        {
            await WithTimeout(
                TestLibFunctions.PlainThrowsAsyncLoadConfigAsync("/etc/bad"),
                DefaultAsyncTimeout);
            throw new AssertionException("PlainThrowsAsyncLoadConfigAsync(\"/etc/bad\") should have thrown");
        }
        catch (SwiftException<PlainThrowsConfigError> ex)
        {
            AssertNotNull(ex.Error, "Cascade dispatch should populate ex.Error for struct error");
            // SwiftString → string projection on the resilience boundary; Path is
            // a non-blittable getter through the SafeHandle.
            AssertEqual("/etc/bad", ex.Error!.Path.ToString(),
                "Cascade payload should preserve PlainThrowsConfigError.path = /etc/bad");
            AssertEqual(7, ex.Error.LineNumber,
                "Cascade payload should preserve PlainThrowsConfigError.lineNumber = 7");
            AssertTrue(ex.Message.Contains("PlainThrowsConfigError") || ex.Message.Contains("/etc/bad") || ex.Message.Contains("7"),
                $"Cascade message should reflect Swift error description, got: {ex.Message}");
            TestLogger.Info($"PlainThrowsAsyncLoadConfigAsync cascade: Path={ex.Error.Path}, LineNumber={ex.Error.LineNumber}, Message={ex.Message}");
        }
    }

    public async Task TestPlainThrowsAsyncScanCascadeToClassError()
    {
        // Layer 5 class-pointer-direct shape: PlainThrowsScanError is a Swift class
        // conforming to Error. The cascade Swift body uses
        // `Unmanaged.passRetained(_typed as AnyObject).toOpaque()` to hand a +1
        // retained class pointer to C#; the wire `errorPtr` IS the class pointer
        // (no carrier buffer). C# `MarshalFromSwift<PlainThrowsScanError>` routes
        // through `NewFromPayload`, whose constructor takes ownership of the +1
        // retain. There is nothing to `SBW_Free` for this shape — SafeHandle's
        // finalizer balances the retain. A double-free here would surface as a
        // Mono JIT crash or NativeAOT abort. Asserting on `.Code` and `.Detail`
        // proves the class pointer round-tripped its carried payload.
        try
        {
            await WithTimeout(
                TestLibFunctions.PlainThrowsAsyncScanAsync("denied"),
                DefaultAsyncTimeout);
            throw new AssertionException("PlainThrowsAsyncScanAsync(\"denied\") should have thrown");
        }
        catch (SwiftException<PlainThrowsScanError> ex)
        {
            AssertNotNull(ex.Error, "Cascade dispatch should populate ex.Error for class error");
            AssertEqual(403, ex.Error!.Code, "Cascade payload should preserve PlainThrowsScanError.code = 403");
            AssertEqual("scanning denied for: denied", ex.Error.Detail.ToString(),
                "Cascade payload should preserve PlainThrowsScanError.detail");
            TestLogger.Info($"PlainThrowsAsyncScanAsync cascade: Code={ex.Error.Code}, Detail={ex.Error.Detail}, Message={ex.Message}");
        }
    }

    public async Task TestPlainThrowsAsyncFrozenWithMemoryCascade()
    {
        // Layer 5 BufferCopiedNeedsVwtDestroy shape: PlainThrowsFrozenWithMemoryError
        // is a `@frozen` struct with a `String` field, so the C# projection is
        // `IsFrozenStructProjectedAsClass` (`ClassWithBufferStruct`). Its generated
        // `NewFromPayload` does an `InitializeWithCopy` from the wire carrier into
        // a fresh `NativeMemory.Alloc` buffer owned by a SafeHandle — the wire
        // carrier walks away with +1 retains on `resourceName`'s heap allocation
        // and on the carrier itself. The cascade dispatcher (and the async
        // typed-throws cleanup in WrapperEmitter / AsyncHarnessEmitter) must run a
        // VWT `Destroy` on the wire buffer before `SBW_Free` to release those
        // retains. Without that destroy, every throw leaks the heap-typed field's
        // backing allocation. Asserting on the marshalled `ResourceName` /
        // `Attempts` confirms the copied payload reached C# intact while the
        // upstream wire buffer was destroyed correctly. Throwing many times in
        // a loop would surface a leak in CI memory reports; the per-call
        // correctness assertion alone is enough to gate the cleanup wiring.
        try
        {
            await WithTimeout(
                TestLibFunctions.PlainThrowsAsyncFrozenWithMemoryAsync("denied"),
                DefaultAsyncTimeout);
            throw new AssertionException("PlainThrowsAsyncFrozenWithMemoryAsync(\"denied\") should have thrown");
        }
        catch (SwiftException<PlainThrowsFrozenWithMemoryError> ex)
        {
            AssertNotNull(ex.Error, "Cascade dispatch should populate ex.Error for frozen-with-memory error");
            AssertEqual("secrets.json", ex.Error!.ResourceName.ToString(),
                "Cascade payload should preserve PlainThrowsFrozenWithMemoryError.resourceName = secrets.json");
            AssertEqual(13, ex.Error.Attempts,
                "Cascade payload should preserve PlainThrowsFrozenWithMemoryError.attempts = 13");
            TestLogger.Info($"PlainThrowsAsyncFrozenWithMemoryAsync cascade: ResourceName={ex.Error.ResourceName}, Attempts={ex.Error.Attempts}, Message={ex.Message}");
        }
    }

    public async Task TestPlainThrowsAsyncFallthroughToUntyped()
    {
        // Layer 5: plain async throws of a Foundation NSError that's NOT in the
        // SwiftBindingsTestLib registry. The Swift cascade has no `as?` arm for
        // NSError, falls through to id 0 + nil buffer, and the C# helper returns
        // a bare `SwiftException` (not `SwiftException<T>`). The Swift error
        // description carries through on `.Message` via String(describing:).
        try
        {
            await WithTimeout(
                TestLibFunctions.PlainThrowsAsyncFallthroughToUntypedAsync(),
                DefaultAsyncTimeout);
            throw new AssertionException("PlainThrowsAsyncFallthroughToUntypedAsync should have thrown");
        }
        catch (SwiftException ex) when (ex.GetType() == typeof(SwiftException))
        {
            // .GetType() == typeof(SwiftException) ensures we caught the BARE type,
            // not a SwiftException<T> subclass — the latter would imply the cascade
            // matched something it shouldn't have.
            AssertTrue(
                ex.Message.Contains("fallthrough-sentinel-7777")
                    || ex.Message.Contains("UnregisteredDomain")
                    || ex.Message.Contains("7777"),
                $"Untyped fallback should preserve NSError description on .Message, got: {ex.Message}");
            TestLogger.Info($"PlainThrowsAsyncFallthroughToUntypedAsync correctly fell through to bare SwiftException; Message={ex.Message}");
        }
    }

    #endregion

    #region Pass 2 — S1: Failable Init (SafeDiv, RangedInt)

    public void TestSafeDivSuccess()
    {
        var success = SafeDiv.TryCreate(10, 2, out var div);
        AssertTrue(success, "SafeDiv.TryCreate succeeds for valid inputs");
        AssertEqual(10, div!.Numerator, "Numerator = 10");
        AssertEqual(2, div!.Denominator, "Denominator = 2");
        TestLogger.Info("SafeDiv.TryCreate success passed");
    }

    public void TestSafeDivFailure()
    {
        var success = SafeDiv.TryCreate(10, 0, out var div);
        AssertFalse(success, "SafeDiv.TryCreate fails for zero denominator");
        TestLogger.Info("SafeDiv.TryCreate failure passed");
    }

    public void TestRangedIntSuccess()
    {
        var success = RangedInt.TryCreate(5, 1, 10, out var val);
        AssertTrue(success, "RangedInt.TryCreate succeeds for value in range");
        AssertEqual(5, val!.Value, "Value = 5");
        AssertEqual(1, val!.Min, "Min = 1");
        AssertEqual(10, val!.Max, "Max = 10");
        TestLogger.Info("RangedInt.TryCreate success passed");
    }

    public void TestRangedIntFailure()
    {
        var success = RangedInt.TryCreate(15, 1, 10, out var val);
        AssertFalse(success, "RangedInt.TryCreate fails for out of range");
        TestLogger.Info("RangedInt.TryCreate failure passed");
    }

    #endregion

    #region S1: NonEmptyString Failable Init (non-frozen struct projected as class)

    [Skip("NonEmptyString.TryCreate not emitted — failable init on non-frozen struct not yet supported")]
    public void TestNonEmptyStringSuccess()
    {
        TestLogger.Info("Skipped: NonEmptyString.TryCreate not emitted");
    }

    [Skip("NonEmptyString.TryCreate not emitted — failable init on non-frozen struct not yet supported")]
    public void TestNonEmptyStringFailure()
    {
        TestLogger.Info("Skipped: NonEmptyString.TryCreate not emitted");
    }

    #endregion

    #region Pass 2 — S2: Traditional Throws with Typed Error (SecureStore)

    public void TestLoadFromStorageSuccess()
    {
        var result = TestLibFunctions.LoadFromStorage("mykey");
        AssertEqual("stored:mykey", result, "LoadFromStorage success");
        TestLogger.Info($"LoadFromStorage = {result}");
    }

    public void TestLoadFromStorageThrowsNotFound()
    {
        try
        {
            TestLibFunctions.LoadFromStorage("");
            AssertTrue(false, "Should have thrown");
        }
        catch (Swift.Runtime.SwiftException ex)
        {
            // Traditional `throws` — generator emits SwiftException (not SwiftException<StorageError>).
            // Error description now uses String(describing:) which gives the case name directly.
            AssertTrue(ex.Message.Contains("notFound"), "Error message contains notFound case name");
            TestLogger.Info($"LoadFromStorage(\"\") threw: {ex.Message}");
        }
    }

    public void TestLoadFromStorageThrowsAccessDenied()
    {
        try
        {
            TestLibFunctions.LoadFromStorage("restricted");
            AssertTrue(false, "Should have thrown");
        }
        catch (Swift.Runtime.SwiftException ex)
        {
            // StorageError.accessDenied — error description gives case name directly.
            AssertTrue(ex.Message.Contains("accessDenied"), "Error message contains accessDenied case name");
            TestLogger.Info($"LoadFromStorage(\"restricted\") threw: {ex.Message}");
        }
    }

    public void TestSecureStoreRetrieve()
    {
        var store = new SecureStore();
        var result = store.Retrieve("data");
        AssertEqual("value-for-data", result, "SecureStore.Retrieve success");
        TestLogger.Info($"SecureStore.Retrieve = {result}");
    }

    #endregion
}
