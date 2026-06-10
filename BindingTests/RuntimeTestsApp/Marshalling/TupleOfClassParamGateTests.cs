// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Linq;
using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Pins the MemberValidationPipeline gate: a constructor or method whose tuple
/// parameter has elements projected as classes/structs (P/Invoke type IntPtr
/// but C# type is not IntPtr) must be skipped — emitting it produces
/// uncompilable code (CS1503 at the call site, raw class tuple passed where
/// ValueTuple of IntPtrs is expected).
///
/// Originally surfaced by a rich-text-editor library's RichTextImageConfiguration ctor.
/// </summary>
public class TupleOfClassParamGateTests : TestBase
{
    public TupleOfClassParamGateTests(TestResults results) : base(results) { }

    public void TestDefaultConstructorWorks()
    {
        var host = new TupleOfClassParamHost();
        AssertEqual("default", host.Label.ToString(), "Default ctor label");
        AssertEqual(0, host.Width, "Default ctor width");
        AssertEqual(0, host.Height, "Default ctor height");
    }

    public void TestTupleOfClassConstructorIsSkipped()
    {
        // The (string, (TupleClassElementSize, TupleClassElementSize)) constructor
        // must NOT exist — the gate skips it. Reject ANY two-parameter ctor so a
        // regression that emits the constructor with AnyType, object, or another
        // placeholder still fails this test.
        var ctors = typeof(TupleOfClassParamHost).GetConstructors();
        var twoParamCtor = ctors.FirstOrDefault(c => c.GetParameters().Length == 2);
        AssertTrue(twoParamCtor is null,
            "Tuple-of-class constructor must be skipped by the validation gate");
    }

    public void TestElementSizeStillUsable()
    {
        // The element type itself remains usable — the gate only skips the
        // member that uses the type in an unsupported tuple position.
        var size = new TupleClassElementSize(width: 100, height: 50);
        AssertEqual(100, size.Width, "Element size width");
        AssertEqual(50, size.Height, "Element size height");
    }
}
