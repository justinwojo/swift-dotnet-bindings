// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests for string marshalling round-trips across the Swift/C# boundary.
/// Covers Phase 55 regression (string enum round-trip) and general string interop.
/// </summary>
public class StringMarshallingTests : TestBase
{
    public StringMarshallingTests(TestResults results) : base(results) { }

    #region Basic String Round-Trips (via Animal class)

    [TestTier(TestTier.Tier1)]
    public void TestAsciiStringRoundTrip()
    {
        // Create an Animal with ASCII name, verify it round-trips through Speak()
        var animal = SwiftBindingsTestLib.CreateAnimal("TestDog", "Woof");
        var description = animal.Describe();

        AssertNotNull(description, "Description not null");
        AssertTrue(description.Contains("TestDog"), "Description contains name");
        AssertTrue(description.Contains("Woof"), "Description contains sound");
        TestLogger.Info($"ASCII round-trip: \"{description}\"");
    }

    [TestTier(TestTier.Tier1)]
    public void TestStringMethodReturn()
    {
        // Animal.Speak() returns a string - verify marshalling
        var animal = SwiftBindingsTestLib.CreateAnimal("Cat", "Meow");
        var sound = animal.Speak();

        AssertNotNull(sound, "Speak result not null");
        AssertTrue(sound.Contains("Meow"), "Speak contains sound");
        TestLogger.Info($"Speak() = \"{sound}\"");
    }

    [TestTier(TestTier.Tier1)]
    public void TestStringParameterPassing()
    {
        // DescribePoint takes no string params, but CreateAnimal takes two strings
        // Verify strings with special characters pass correctly
        var animal = SwiftBindingsTestLib.CreateAnimal("Name With Spaces", "Sound!");
        var description = animal.Describe();

        AssertNotNull(description, "Description not null");
        AssertTrue(description.Contains("Name With Spaces"), "Spaces preserved");
        TestLogger.Info($"String with spaces: \"{description}\"");
    }

    #endregion

    #region Unicode String Tests

    [TestTier(TestTier.Tier2)]
    public void TestUnicodeJapanese()
    {
        // Test Japanese characters in strings (CJK unified ideographs + hiragana)
        var animal = SwiftBindingsTestLib.CreateAnimal("犬", "ワンワン");
        var description = animal.Describe();

        AssertNotNull(description, "Japanese description not null");
        AssertTrue(description.Contains("犬"), "Japanese name preserved");
        AssertTrue(description.Contains("ワンワン"), "Japanese sound preserved");
        TestLogger.Info($"Japanese: \"{description}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestUnicodeEmoji()
    {
        // Test emoji characters (multi-byte UTF-8 sequences)
        var animal = SwiftBindingsTestLib.CreateAnimal("Dog", "Bark");
        AssertNotNull(animal.Describe(), "Emoji test created");

        // Verify Greeting enum with emoji raw value
        var greeting = Greeting.Emoji;
        var rawValue = greeting.RawValue.ToString();
        AssertEqual("👋", rawValue, "Emoji raw value preserved");
        TestLogger.Info($"Emoji greeting raw value: \"{rawValue}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestUnicodeKorean()
    {
        // Verify Korean characters via Greeting enum
        var greeting = Greeting.Korean;
        var rawValue = greeting.RawValue.ToString();
        AssertEqual("안녕하세요", rawValue, "Korean raw value preserved");
        TestLogger.Info($"Korean greeting: \"{rawValue}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestUnicodeMixed()
    {
        // Verify mixed ASCII + CJK string via Greeting enum
        var greeting = Greeting.Mixed;
        var rawValue = greeting.RawValue.ToString();
        AssertEqual("Hello 世界!", rawValue, "Mixed raw value preserved");
        TestLogger.Info($"Mixed greeting: \"{rawValue}\"");
    }

    #endregion

    #region String Enum Raw Value Round-Trips (Phase 55 regression)

    [TestTier(TestTier.Tier1)]
    public void TestLogLevelRawValueRoundTrip()
    {
        // Phase 55 regression: string enum FromRawValue round-trip
        // Create LogLevel from raw value, extract raw value back, compare
        var result = SwiftBindingsTestLib.ValidateLogLevelRoundTrip("[INFO]");
        AssertTrue(result, "LogLevel [INFO] round-trip");

        result = SwiftBindingsTestLib.ValidateLogLevelRoundTrip("[DEBUG]");
        AssertTrue(result, "LogLevel [DEBUG] round-trip");

        result = SwiftBindingsTestLib.ValidateLogLevelRoundTrip("[ERROR]");
        AssertTrue(result, "LogLevel [ERROR] round-trip");

        TestLogger.Info("LogLevel raw value round-trips passed");
    }

    [TestTier(TestTier.Tier1)]
    public void TestLogLevelInvalidRawValue()
    {
        // Invalid raw value should return false (no matching case)
        var result = SwiftBindingsTestLib.ValidateLogLevelRoundTrip("INVALID");
        AssertFalse(result, "Invalid LogLevel returns false");

        result = SwiftBindingsTestLib.ValidateLogLevelRoundTrip("");
        AssertFalse(result, "Empty LogLevel returns false");

        TestLogger.Info("LogLevel invalid raw value tests passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestLogLevelAllCases()
    {
        // Exhaustive test of all LogLevel cases
        var cases = new (string rawValue, string name)[]
        {
            ("[DEBUG]", "Debug"),
            ("[INFO]", "Info"),
            ("[WARN]", "Warning"),
            ("[ERROR]", "Error"),
            ("[CRITICAL]", "Critical"),
        };

        foreach (var (rawValue, name) in cases)
        {
            var result = SwiftBindingsTestLib.ValidateLogLevelRoundTrip(rawValue);
            AssertTrue(result, $"LogLevel {name} round-trip");
        }
        TestLogger.Info("All LogLevel cases round-trip correctly");
    }

    [TestTier(TestTier.Tier2)]
    public void TestLogLevelFromRawValueFactory()
    {
        // Test FromRawValue factory method directly
        var info = LogLevel.FromRawValue("[INFO]");
        AssertNotNull(info, "LogLevel.FromRawValue([INFO]) not null");

        var rawBack = info!.RawValue.ToString();
        AssertEqual("[INFO]", rawBack, "Round-trip preserves raw value");

        // Invalid raw value should return null
        var invalid = LogLevel.FromRawValue("BOGUS");
        AssertNull(invalid, "LogLevel.FromRawValue(BOGUS) is null");

        TestLogger.Info("LogLevel FromRawValue factory tests passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestGreetingFromRawValue()
    {
        // Test Greeting string enum round-trip with unicode values
        var hello = Greeting.FromRawValue("Hello");
        AssertNotNull(hello, "Greeting.FromRawValue(Hello) not null");
        AssertEqual("Hello", hello!.RawValue.ToString(), "Hello round-trip");

        var japanese = Greeting.FromRawValue("こんにちは");
        AssertNotNull(japanese, "Greeting.FromRawValue(こんにちは) not null");
        AssertEqual("こんにちは", japanese!.RawValue.ToString(), "Japanese round-trip");

        TestLogger.Info("Greeting FromRawValue tests passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestStatusCodeFromRawValue()
    {
        // Test StatusCode string enum
        var ok = StatusCode.FromRawValue("OK");
        AssertNotNull(ok, "StatusCode OK not null");
        AssertEqual("OK", ok!.RawValue.ToString(), "OK round-trip");

        var notFound = StatusCode.FromRawValue("NOT_FOUND");
        AssertNotNull(notFound, "StatusCode NOT_FOUND not null");
        AssertEqual("NOT_FOUND", notFound!.RawValue.ToString(), "NOT_FOUND round-trip");

        var invalid = StatusCode.FromRawValue("INVALID_CODE");
        AssertNull(invalid, "Invalid StatusCode is null");

        TestLogger.Info("StatusCode FromRawValue tests passed");
    }

    #endregion

    #region Edge Case Strings

    [TestTier(TestTier.Tier2)]
    public void TestEmptyString()
    {
        // EdgeCaseStrings.Empty has raw value ""
        var empty = EdgeCaseStrings.Empty;
        var rawValue = empty.RawValue.ToString();
        AssertEqual("", rawValue, "Empty string raw value");
        TestLogger.Info("Empty string edge case passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestWhitespaceStrings()
    {
        // Single space
        var space = EdgeCaseStrings.SingleSpace;
        AssertEqual(" ", space.RawValue.ToString(), "Single space raw value");

        // Multiple spaces
        var multiSpace = EdgeCaseStrings.MultipleSpaces;
        AssertEqual("   ", multiSpace.RawValue.ToString(), "Multiple spaces raw value");

        // Tab
        var tab = EdgeCaseStrings.Tab;
        AssertEqual("\t", tab.RawValue.ToString(), "Tab raw value");

        // Newline
        var newline = EdgeCaseStrings.Newline;
        AssertEqual("\n", newline.RawValue.ToString(), "Newline raw value");

        TestLogger.Info("Whitespace string edge cases passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestEdgeCaseStringsRoundTrip()
    {
        // Round-trip via FromRawValue for edge cases
        var empty = EdgeCaseStrings.FromRawValue("");
        AssertNotNull(empty, "FromRawValue empty string not null");
        AssertEqual("", empty!.RawValue.ToString(), "Empty round-trip");

        var normal = EdgeCaseStrings.FromRawValue("normal");
        AssertNotNull(normal, "FromRawValue normal not null");
        AssertEqual("normal", normal!.RawValue.ToString(), "Normal round-trip");

        TestLogger.Info("EdgeCaseStrings round-trip tests passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestCaseSensitiveStrings()
    {
        // Verify case sensitivity is preserved in raw values
        var lower = CaseSensitiveEnum.Lower;
        AssertEqual("value", lower.RawValue.ToString(), "Lower case preserved");

        var upper = CaseSensitiveEnum.Upper;
        AssertEqual("VALUE", upper.RawValue.ToString(), "Upper case preserved");

        var mixed = CaseSensitiveEnum.Mixed;
        AssertEqual("Value", mixed.RawValue.ToString(), "Mixed case preserved");

        var camel = CaseSensitiveEnum.Camel;
        AssertEqual("valueCase", camel.RawValue.ToString(), "Camel case preserved");

        var pascal = CaseSensitiveEnum.Pascal;
        AssertEqual("ValueCase", pascal.RawValue.ToString(), "Pascal case preserved");

        TestLogger.Info("Case sensitivity tests passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestCaseSensitiveFromRawValue()
    {
        // "value" and "VALUE" must resolve to different enum cases
        var lower = CaseSensitiveEnum.FromRawValue("value");
        AssertNotNull(lower, "lower from raw value not null");

        var upper = CaseSensitiveEnum.FromRawValue("VALUE");
        AssertNotNull(upper, "upper from raw value not null");

        // Cross-check: they should have different raw values
        AssertEqual("value", lower!.RawValue.ToString(), "Lower resolves correctly");
        AssertEqual("VALUE", upper!.RawValue.ToString(), "Upper resolves correctly");

        TestLogger.Info("Case-sensitive FromRawValue tests passed");
    }

    #endregion

    #region String via GetLogLevelRaw / GetOrderStatusRaw

    [TestTier(TestTier.Tier2)]
    public void TestGetLogLevelRaw()
    {
        // Create LogLevel enum cases, extract raw value via free function
        var debug = LogLevel.Debug;
        var raw = SwiftBindingsTestLib.GetLogLevelRaw(debug);
        AssertEqual("[DEBUG]", raw, "GetLogLevelRaw Debug");

        var critical = LogLevel.Critical;
        raw = SwiftBindingsTestLib.GetLogLevelRaw(critical);
        AssertEqual("[CRITICAL]", raw, "GetLogLevelRaw Critical");

        TestLogger.Info("GetLogLevelRaw tests passed");
    }

    [TestTier(TestTier.Tier3)]
    public void TestLongStringViaAnimal()
    {
        // Test a moderately long string (1KB)
        var longName = new string('A', 1024);
        var animal = SwiftBindingsTestLib.CreateAnimal(longName, "Sound");
        var description = animal.Describe();

        AssertNotNull(description, "Long string description not null");
        AssertTrue(description.Contains(longName), "Long string preserved");
        TestLogger.Info($"Long string ({longName.Length} chars) round-trip passed");
    }

    [TestTier(TestTier.Tier3)]
    public void TestVeryLongStringViaAnimal()
    {
        // Stress test: >64KB string to exercise large buffer marshalling
        var veryLongName = new string('B', 65536 + 100);
        var animal = SwiftBindingsTestLib.CreateAnimal(veryLongName, "X");
        var description = animal.Describe();

        AssertNotNull(description, ">64KB string description not null");
        AssertTrue(description.Contains(veryLongName), ">64KB string preserved");
        TestLogger.Info($"Very long string ({veryLongName.Length} chars) round-trip passed");
    }

    #endregion
}
