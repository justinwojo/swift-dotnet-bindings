// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// End-to-end coverage for <c>Foundation.URLError.Code</c>, projected as <c>nint</c>, on every call
/// route the generator emits for it.
///
/// The SDK does not declare <c>URLError.Code</c> <c>@frozen</c>, so although it holds one
/// <c>Int</c>, Swift passes it to clients by address and returns it through an indirect-result
/// buffer. A Swift-convention P/Invoke carrying the <c>nint</c> in a register would hand Swift an
/// integer to dereference. The binding must instead route each member through a wrapper that takes
/// a plain <c>Int</c> and rebuilds the value, or carry it in memory. The raw values are chosen so a
/// value read from the wrong register or address cannot match.
/// </summary>
public class UrlErrorCodeAbiTests : TestBase
{
    public UrlErrorCodeAbiTests(TestResults results) : base(results) { }

    private static readonly nint RawA = unchecked((nint)0x1234_5678_9ABCL);
    private static readonly nint RawB = -1001;

    private sealed class FixedSource : IUrlErrorCodeSource
    {
        private readonly nint _value;
        public FixedSource(nint value) => _value = value;
        public nint GetCurrentCode() => _value;
    }

    public void TestFreeFunction_EchoAndRawValue()
    {
        AssertEqual((nint)RawA, TestLibFunctions.UrlErrorCodeEcho((nint)RawA),
            "URLError.Code round-trips through a free function parameter and result");
        AssertEqual((nint)RawB, TestLibFunctions.UrlErrorCodeRawValue((nint)RawB),
            "Swift reads the rawValue of a URLError.Code parameter");
    }

    public void TestFreeFunction_PastIntegerRegisterBoundary()
    {
        AssertEqual((nint)(28 + RawA),
            TestLibFunctions.UrlErrorCodePastRegisterBoundary(1, 2, 3, 4, 5, 6, 7, (nint)RawA),
            "A URLError.Code after seven Int64 arguments keeps its value");
    }

    public void TestStruct_InitializerPropertyAndMethods()
    {
        using var holder = new UrlErrorCodeHolder((nint)RawA);
        AssertEqual((nint)RawA, holder.CodeNative, "Initializer argument is stored and read back");
        AssertEqual((nint)RawB, holder.Echo((nint)RawB), "Instance method echoes its argument");
        AssertEqual((nint)RawB, holder.RawValue((nint)RawB), "Instance method reads the argument's rawValue");
        AssertEqual((nint)(7 * 1000 + RawB), holder.Combined(7, (nint)RawB),
            "Two adjacent URLError.Code arguments each arrive in their own slot");
        AssertEqual((nint)(RawA + RawB), holder[(nint)RawB], "Subscript index and stored value both arrive intact");

        holder.CodeNative = (nint)RawB;
        AssertEqual((nint)RawB, holder.CodeNative, "Property setter stores the new value");
    }

    public async Task TestStruct_AsyncEcho()
    {
        using var holder = new UrlErrorCodeHolder(1);
        AssertEqual((nint)RawA, await holder.EchoLaterAsync((nint)RawA),
            "An async member carries URLError.Code in and out");
    }

    public void TestClass_InitializerPropertyMethodAndStaticFactory()
    {
        using var box = new UrlErrorCodeBox((nint)RawB);
        AssertEqual((nint)RawB, box.CodeNative, "Class initializer stores the argument");
        AssertEqual((nint)RawB, box.GetStoredRawValue(), "Swift sees the stored value");
        AssertEqual((nint)RawA, box.Echo((nint)RawA), "Class method echoes its argument");
        box.CodeNative = (nint)RawA;
        AssertEqual((nint)RawA, box.GetStoredRawValue(), "Class property setter reaches Swift");
        AssertEqual((nint)RawA, UrlErrorCodeBox.Make((nint)RawA), "Static method returns a URLError.Code");
    }

    public void TestGeneric_EchoOnBoundGenericReceiver()
    {
        using var box = new UrlErrorCodeGenericBox<long>(5);
        AssertEqual((nint)RawB, box.Echo((nint)RawB), "A method on a generic receiver echoes its argument");
    }

    public void TestEnum_PayloadCaseConstructionAndExtraction()
    {
        using var outcome = UrlErrorCodeOutcome.Failed((nint)RawA);
        AssertEqual((nint)RawA, outcome.FailureRawValueNative, "Swift sees the case payload");
        AssertTrue(outcome.TryGetFailed(out var value), "The payload case is recognized");
        AssertEqual((nint)RawA, value, "The payload reads back intact");
    }

    public void TestClosure_SwiftPassesCodeToManagedCallback()
    {
        nint seen = 0;
        var result = TestLibFunctions.UrlErrorCodeThroughCallback((nint)RawA, code => { seen = code; return code + 1; });
        AssertEqual((nint)RawA, seen, "The callback receives the URLError.Code Swift built");
        AssertEqual((nint)(RawA + 1), result, "Swift receives the callback's answer");
    }

    public void TestProtocol_ManagedConformerReturnsCode()
    {
        AssertEqual((nint)RawB, TestLibFunctions.UrlErrorCodeFromSource(new FixedSource((nint)RawB)),
            "Swift reads a URLError.Code returned by a managed protocol conformer");
    }

    public unsafe void TestSpecializedWrappers_CarryCodeBesideClosureSliceAndPointer()
    {
        AssertEqual((nint)(RawA + 1), TestLibFunctions.UrlErrorCodeWithCallback((nint)RawA, raw => raw + 1),
            "beside a closure, Swift reads the code the caller passed");
        AssertEqual((nint)(RawB + 6), TestLibFunctions.UrlErrorCodeWithSlice((nint)RawB, new nint[] { 1, 2, 3 }),
            "beside an ArraySlice, Swift reads the code the caller passed");
        nint addend = 5;
        AssertEqual((nint)(RawA + 5), TestLibFunctions.UrlErrorCodeWithOptionalPointer((nint)RawA, (System.IntPtr)(&addend)),
            "beside an optional pointer, Swift reads the code the caller passed");
        AssertEqual((nint)RawA, TestLibFunctions.UrlErrorCodeWithOptionalPointer((nint)RawA, null),
            "beside a nil pointer, Swift reads the code the caller passed");

        var wrappers = new UrlErrorCodeWrappers();
        AssertEqual((nint)(RawB - 1), wrappers.WithCallback((nint)RawB, raw => raw - 1),
            "a class member beside a closure reads the code the caller passed");
        AssertEqual((nint)(RawA + 10), wrappers.WithSlice((nint)RawA, new nint[] { 4, 6 }),
            "a class member beside an ArraySlice reads the code the caller passed");
    }
}
