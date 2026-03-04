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
/// All throwing Swift methods surface as SwiftRuntimeException in C#.
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

    [TestTier(TestTier.Tier1)]
    public void TestDivideSuccess()
    {
        var result = TestLibFunctions.Divide(10, 2);
        AssertEqual(5, result, "10 / 2 = 5");
        TestLogger.Info($"Divide(10, 2) = {result}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestDivideNegativeValues()
    {
        var result = TestLibFunctions.Divide(-15, 3);
        AssertEqual(-5, result, "-15 / 3 = -5");

        var result2 = TestLibFunctions.Divide(15, -3);
        AssertEqual(-5, result2, "15 / -3 = -5");
        TestLogger.Info("Divide with negative values passed");
    }

    [TestTier(TestTier.Tier1)]
    public void TestDivideByZeroThrows()
    {
        try
        {
            TestLibFunctions.Divide(10, 0);
            throw new AssertionException("Divide by zero should throw");
        }
        catch (SwiftRuntimeException ex)
        {
            // Error message should be the Swift String(describing:) output, not a hardcoded message
            AssertTrue(ex.Message.Contains("divisionByZero"),
                $"Error message should contain Swift error description, got: {ex.Message}");
            TestLogger.Info($"Divide by zero threw with message: {ex.Message}");
        }
    }

    #endregion

    #region ThrowingStruct — Blittable

    [TestTier(TestTier.Tier1)]
    public void TestThrowingStructConstruction()
    {
        var ts = new ThrowingStruct(42);
        AssertEqual(42, ts.Value, "ThrowingStruct.Value");
        TestLogger.Info($"ThrowingStruct created with value={ts.Value}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestThrowingStructDivideBySuccess()
    {
        var ts = new ThrowingStruct(100);
        var result = ts.DivideBy(5);
        AssertEqual(20, result, "100 / 5 = 20");
        TestLogger.Info($"ThrowingStruct(100).GetDivideBy(5) = {result}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestThrowingStructValidatePositiveSuccess()
    {
        var ts = new ThrowingStruct(10);
        var result = ts.ValidatePositive();
        AssertEqual(10, result, "Positive value validates");
        TestLogger.Info($"ThrowingStruct(10).ValidatePositive() = {result}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestThrowingStructStaticSafeDivideSuccess()
    {
        var result = ThrowingStruct.SafeDivide(20, 4);
        AssertEqual(5, result, "20 / 4 = 5");
        TestLogger.Info($"ThrowingStruct.SafeDivide(20, 4) = {result}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestThrowingStructDivideByZeroThrows()
    {
        var ts = new ThrowingStruct(100);
        try
        {
            ts.DivideBy(0);
            throw new AssertionException("DivideBy(0) should throw");
        }
        catch (SwiftRuntimeException ex)
        {
            AssertTrue(ex.Message.Contains("divisionByZero"),
                $"Error message should contain Swift error description, got: {ex.Message}");
            TestLogger.Info($"ThrowingStruct.GetDivideBy(0) threw with message: {ex.Message}");
        }
    }

    [TestTier(TestTier.Tier1)]
    public void TestThrowingStructValidatePositiveNegativeThrows()
    {
        var ts = new ThrowingStruct(-5);
        AssertThrows<SwiftRuntimeException>(() =>
        {
            ts.ValidatePositive();
        }, "ValidatePositive on negative should throw");
        TestLogger.Info("ThrowingStruct(-5).ValidatePositive() correctly threw");
    }

    [TestTier(TestTier.Tier1)]
    public void TestThrowingStructValidatePositiveZeroThrows()
    {
        var ts = new ThrowingStruct(0);
        AssertThrows<SwiftRuntimeException>(() =>
        {
            ts.ValidatePositive();
        }, "ValidatePositive on zero should throw");
        TestLogger.Info("ThrowingStruct(0).ValidatePositive() correctly threw");
    }

    [TestTier(TestTier.Tier1)]
    public void TestThrowingStructStaticSafeDivideByZeroThrows()
    {
        AssertThrows<SwiftRuntimeException>(() =>
        {
            ThrowingStruct.SafeDivide(10, 0);
        }, "SafeDivide by zero should throw");
        TestLogger.Info("ThrowingStruct.SafeDivide(10, 0) correctly threw");
    }

    #endregion

    #region Typed Throws — Blittable (ValidateRange: Int32-only)

    [TestTier(TestTier.Tier1)]
    public void TestValidateRangeSuccess()
    {
        var result = TestLibFunctions.ValidateRange(5, 1, 10);
        AssertEqual(5, result, "ValidateRange(5, 1, 10)");
        TestLogger.Info($"ValidateRange(5, 1, 10) = {result}");
    }

    [TestTier(TestTier.Tier3)] // Typed throws via direct P/Invoke: swifterror may not contain AnyObject box
    public void TestValidateRangeBelowMinThrows()
    {
        AssertThrows<SwiftException<RangeError>>(() =>
        {
            TestLibFunctions.ValidateRange(0, 1, 10);
        }, "ValidateRange below min should throw SwiftException<RangeError>");
        TestLogger.Info("ValidateRange(0, 1, 10) correctly threw SwiftException<RangeError>");
    }

    [TestTier(TestTier.Tier3)] // Typed throws via direct P/Invoke: swifterror may not contain AnyObject box
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

    [TestTier(TestTier.Tier1)]
    public void TestMathErrorCases()
    {
        var divByZero = MathError.DivisionByZero;
        AssertEqual(MathError.CaseTag.DivisionByZero, divByZero.Tag, "DivisionByZero tag");

        var overflow = MathError.Overflow;
        AssertEqual(MathError.CaseTag.Overflow, overflow.Tag, "Overflow tag");

        var negInput = MathError.NegativeInput;
        AssertEqual(MathError.CaseTag.NegativeInput, negInput.Tag, "NegativeInput tag");

        TestLogger.Info("MathError case construction passed");
    }

    [TestTier(TestTier.Tier1)]
    public void TestValidationErrorEmptyCase()
    {
        var empty = ValidationError.Empty;
        AssertEqual(ValidationError.CaseTag.Empty, empty.Tag, "Empty tag");
        TestLogger.Info("ValidationError.Empty case construction passed");
    }

    // ValidationError.TooLong(Int32) — EntryPointNotFoundException at runtime
    // The symbol $s...ValidationErrorO7tooLong... is not exported from the dylib
    [TestTier(TestTier.Tier3)]
    public void TestValidationErrorTooLongCase()
    {
        var tooLong = ValidationError.TooLong(50);
        AssertEqual(ValidationError.CaseTag.TooLong, tooLong.Tag, "TooLong tag");
        TestLogger.Info("ValidationError.TooLong case construction passed");
    }

    [TestTier(TestTier.Tier1)]
    public void TestParseErrorCases()
    {
        var invalid = ParseError.InvalidInput;
        AssertEqual(ParseError.CaseTag.InvalidInput, invalid.Tag, "InvalidInput tag");

        TestLogger.Info("ParseError case construction passed");
    }

    // ParseError.Overflow(SwiftString) — SwiftIndirectResult + SwiftString (non-blittable P/Invoke)
    [TestTier(TestTier.Tier3)]
    public void TestParseErrorOverflowCase()
    {
        using var val = new SwiftString("99999999999");
        var overflow = ParseError.Overflow(val);
        AssertEqual(ParseError.CaseTag.Overflow, overflow.Tag, "Overflow tag");
        TestLogger.Info("ParseError.Overflow case construction passed");
    }

    // ValidationError.InvalidFormat(SwiftString) — SwiftIndirectResult + SwiftString (non-blittable P/Invoke)
    [TestTier(TestTier.Tier3)]
    public void TestValidationErrorInvalidFormatCase()
    {
        using var val = new SwiftString("bad format");
        var invalid = ValidationError.InvalidFormat(val);
        AssertEqual(ValidationError.CaseTag.InvalidFormat, invalid.Tag, "InvalidFormat tag");
        TestLogger.Info("ValidationError.InvalidFormat case construction passed");
    }

    // RangeError.BelowMinimum/AboveMaximum take tuple (Int32, Int32) associated values
    // InvalidProgramException: non-blittable types with Swift calling convention
    [TestTier(TestTier.Tier3)]
    public void TestRangeErrorCases()
    {
        var below = RangeError.BelowMinimum((5, 10));
        AssertEqual(RangeError.CaseTag.BelowMinimum, below.Tag, "BelowMinimum tag");

        var above = RangeError.AboveMaximum((15, 10));
        AssertEqual(RangeError.CaseTag.AboveMaximum, above.Tag, "AboveMaximum tag");

        TestLogger.Info("RangeError case construction passed");
    }

    #endregion

    // ===================================================================
    // Tier 2: SwiftString-involving success paths (no error thrown)
    // ===================================================================

    #region Typed Throws — SwiftString Success Paths (ParseNumber success)

    [TestTier(TestTier.Tier2)]
    public void TestParseNumberSuccess()
    {
        var result = TestLibFunctions.ParseNumber("42");
        AssertEqual(42, result, "ParseNumber(\"42\")");
        TestLogger.Info($"ParseNumber(\"42\") = {result}");
    }

    [TestTier(TestTier.Tier2)]
    public void TestParseNumberNegative()
    {
        var result = TestLibFunctions.ParseNumber("-100");
        AssertEqual(-100, result, "ParseNumber(\"-100\")");
        TestLogger.Info($"ParseNumber(\"-100\") = {result}");
    }

    #endregion

    #region TypedThrowingParser — Struct Construction and Success Paths

    [TestTier(TestTier.Tier2)]
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
    [TestTier(TestTier.Tier3)]
    public void TestTypedThrowingParserConstructor()
    {
        var parser = new TypedThrowingParser(true);
        AssertTrue(parser.Strict, "Constructor strict=true");

        var parser2 = new TypedThrowingParser(false);
        AssertFalse(parser2.Strict, "Constructor strict=false");
        TestLogger.Info("TypedThrowingParser constructors passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestTypedThrowingParserParseSuccess()
    {
        var parser = TestLibFunctions.CreateLenientParser();
        var result = parser.Parse("123");
        AssertEqual(123, result, "Lenient parse \"123\"");
        TestLogger.Info($"LenientParser.Parse(\"123\") = {result}");
    }

    [TestTier(TestTier.Tier2)]
    public void TestStrictParserAcceptsCleanInput()
    {
        var parser = TestLibFunctions.CreateStrictParser();
        var result = parser.Parse("99");
        AssertEqual(99, result, "Strict parse \"99\"");
        TestLogger.Info($"StrictParser.Parse(\"99\") = {result}");
    }

    [TestTier(TestTier.Tier2)]
    public void TestLenientParserAcceptsCleanInput()
    {
        var parser = TestLibFunctions.CreateLenientParser();
        var result = parser.Parse("42");
        AssertEqual(42, result, "Lenient parse \"42\"");
        TestLogger.Info($"LenientParser.Parse(\"42\") = {result}");
    }

    #endregion

    #region String-Returning Throwing Functions — Success Path

    [TestTier(TestTier.Tier2)]
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

    [TestTier(TestTier.Tier3)]
    public void TestParseNumberThrowsOnInvalidInput()
    {
        AssertThrows<SwiftException<ParseError>>(() =>
        {
            TestLibFunctions.ParseNumber("abc");
        }, "ParseNumber(\"abc\") should throw SwiftException<ParseError>");
        TestLogger.Info("ParseNumber(\"abc\") correctly threw SwiftException<ParseError>");
    }

    [TestTier(TestTier.Tier3)]
    public void TestTypedThrowingParserParseInvalidThrows()
    {
        var parser = TestLibFunctions.CreateLenientParser();
        AssertThrows<SwiftException<ParseError>>(() =>
        {
            parser.Parse("not_a_number");
        }, "Parse invalid input should throw SwiftException<ParseError>");
        TestLogger.Info("Parser.Parse(\"not_a_number\") correctly threw SwiftException<ParseError>");
    }

    [TestTier(TestTier.Tier3)]
    public void TestStrictParserRejectsWhitespace()
    {
        var parser = TestLibFunctions.CreateStrictParser();
        AssertThrows<SwiftException<ParseError>>(() =>
        {
            parser.Parse(" 42 ");
        }, "Strict parser should reject whitespace");
        TestLogger.Info("StrictParser.Parse(\" 42 \") correctly threw SwiftException<ParseError>");
    }

    [TestTier(TestTier.Tier3)]
    public void TestValidateEmptyStringThrows()
    {
        AssertThrows<SwiftRuntimeException>(() =>
        {
            TestLibFunctions.Validate("", 100);
        }, "Validate empty string should throw");
        TestLogger.Info("Validate(\"\", 100) correctly threw");
    }

    [TestTier(TestTier.Tier3)]
    public void TestValidateTooLongThrows()
    {
        AssertThrows<SwiftRuntimeException>(() =>
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

    [TestTier(TestTier.Tier3)] // Typed throws via direct P/Invoke: swifterror ABI mismatch on Mono
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
            // Message should be the real Swift error description
            AssertTrue(ex.Message.Contains("aboveMaximum") || ex.Message.Contains("Above"),
                $"Exception message should contain Swift error description, got: {ex.Message}");
            TestLogger.Info($"ValidateRange typed catch: Error.Tag={ex.Error.Tag}, Message={ex.Message}");
        }
    }

    #endregion

    #region Typed Throws — Async (Tier 3: Mono JIT limitations)

    [TestTier(TestTier.Tier3)]
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

    [TestTier(TestTier.Tier3)]
    public async Task TestAsyncParseSuccess()
    {
        var parser = TestLibFunctions.CreateLenientParser();
        var result = await WithTimeout(parser.ParseAsync("42"), DefaultAsyncTimeout);
        AssertEqual(42, result, "AsyncParse(\"42\") should return 42");
        TestLogger.Info($"AsyncParse(\"42\") = {result}");
    }

    #endregion
}
