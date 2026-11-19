// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Typed pointers in closure positions (<c>TypedPointerClosureArgs.swift</c>). A typed pointer
/// crosses a callback's C slot as a raw pointer; the wrapper adapter converts an
/// <c>UnsafePointer&lt;T&gt;</c> argument to it explicitly and rebinds a returned raw pointer to
/// the pointee type. Before that conversion every member here failed the wrapper compile, so the
/// whole member was withdrawn (and in an SDK build the whole package failed). Each test moves a
/// value through the pointer in the direction the signature names.
/// </summary>
public class TypedPointerClosureArgTests : TestBase
{
    public TypedPointerClosureArgTests(TestResults results) : base(results) { }

    public void TestEscapingRenderHandlerReadsAndWritesThroughEveryPointer()
    {
        var node = new TypedPointerRenderNode();
        nint observedCount = -1;
        uint observedFrame = 0;
        node.Install((stop, count, frame, buffer) =>
        {
            unsafe
            {
                observedCount = *(nint*)count;
                observedFrame = frame;
                *(byte*)stop = 1; // ObjCBool true
                var ints = (int*)buffer;
                for (int i = 0; i < 4; i++) ints[i] = (int)observedCount + i;
            }
            return 7;
        });

        var result = node.Render(40, 9);

        AssertEqual((nint)40, observedCount, "the read-only count pointer carried Swift's value");
        AssertEqual(9u, observedFrame, "the frame scalar crossed beside the pointers");
        // status 7, flag set, buffer 40+41+42+43
        AssertEqual((nint)(7 * 1_000_000 + 100_000 + 166), result,
            "status, the flag written through UnsafeMutablePointer<ObjCBool> and the raw buffer reached Swift");
    }

    public void TestNonEscapingThrowingRenderOnce()
    {
        var node = new TypedPointerRenderNode();
        var status = node.RenderOnce(5, 2, (stop, count, frame, buffer) =>
        {
            unsafe
            {
                var n = (int)*(nint*)count;
                ((int*)buffer)[0] = n;
                ((int*)buffer)[1] = (int)frame;
                *(byte*)stop = 0;
                return n * 10;
            }
        });

        AssertEqual(50 + 5 + 2, status, "the handler read the count and wrote the buffer; the flag stayed false");
    }

    public void TestInitializerAndStoredPropertyReadThroughUnsafePointer()
    {
        using var host = new TypedPointerClosureHost(21, p => { unsafe { return *(int*)p * 2; } });

        AssertEqual(42, host.SeededTotal, "the initializer's callback read the seed through UnsafePointer<Int32>");
        AssertEqual(-10, host.ReadThroughProperty(-5), "the stored callback reads through the pointer later");

        host.Reader = p => { unsafe { return *(int*)p + 1; } };
        AssertEqual(100, host.ReadThroughProperty(99), "a callback assigned through the property reads the pointer");
    }

    public void TestStructPointeeBehindUnsafePointer()
    {
        using var host = new TypedPointerClosureHost(0, _ => 0);
        var result = host.ReadSample(3, 1.5, p =>
        {
            unsafe
            {
                var sample = *(TypedPointerSample*)p;
                return sample.Id + sample.Weight;
            }
        });

        AssertEqual(4.5, result, "the struct's fields were read through UnsafePointer<TypedPointerSample>");
    }

    public unsafe void TestReturnedUnsafePointerIsReadBySwift()
    {
        using var host = new TypedPointerClosureHost(0, _ => 0);
        var values = (int*)NativeMemory.Alloc(3, sizeof(int));
        try
        {
            values[0] = 10; values[1] = 20; values[2] = 12;
            var total = host.SumReturned(3, () => (System.IntPtr)values);
            AssertEqual(42, total, "Swift summed the Int32s behind the returned UnsafePointer<Int32>");
        }
        finally
        {
            NativeMemory.Free(values);
        }
    }

    public unsafe void TestReturnedUnsafeMutablePointerIsWrittenBySwift()
    {
        using var host = new TypedPointerClosureHost(0, _ => 0);
        var cell = (int*)NativeMemory.Alloc(1, sizeof(int));
        try
        {
            *cell = 0;
            var echoed = host.WriteReturned(-31, () => (System.IntPtr)cell);
            AssertEqual(-31, echoed, "Swift read back what it wrote");
            AssertEqual(-31, *cell, "Swift's write landed in the managed cell behind UnsafeMutablePointer<Int32>");
        }
        finally
        {
            NativeMemory.Free(cell);
        }
    }

    public unsafe void TestThrowingCallbackReturnsStructPointer()
    {
        using var host = new TypedPointerClosureHost(0, _ => 0);
        var sample = (TypedPointerSample*)NativeMemory.Alloc(1, (nuint)sizeof(TypedPointerSample));
        try
        {
            *sample = new TypedPointerSample(4, 2.25);
            var result = host.WeightReturned(() => (System.IntPtr)sample);
            AssertEqual(8.5, result, "Swift read the struct behind the returned UnsafePointer<TypedPointerSample>");
        }
        finally
        {
            NativeMemory.Free(sample);
        }
    }

    public void TestFreeFunctionEchoesThroughArgumentAndResultPointers()
    {
        var echoed = TestLibFunctions.TypedPointerEcho(77, p => p);
        AssertEqual(77, echoed, "the argument pointer came back as the result pointer and Swift read through it");
    }
}
