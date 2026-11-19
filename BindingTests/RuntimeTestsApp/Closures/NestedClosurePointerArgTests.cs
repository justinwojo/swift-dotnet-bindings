// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Pointers as the outer arguments of a closure that also takes an inner closure
/// (<c>NestedClosurePointerArgs.swift</c>). Each test reads or writes the value behind the pointer
/// Swift passed and reports back through the inner closure.
/// </summary>
public class NestedClosurePointerArgTests : TestBase
{
    public NestedClosurePointerArgTests(TestResults results) : base(results) { }

    public unsafe void TestReadThroughTypedPointer()
    {
        using var host = new NestedPointerCallbackHost();
        var result = host.ReadThroughTypedPointer(21, (p, report) => report(*(int*)p * 2));
        AssertEqual(42, result, "the callback read Swift's value through UnsafePointer<Int32>");
    }

    public unsafe void TestWriteThroughMutablePointer()
    {
        using var host = new NestedPointerCallbackHost();
        var result = host.WriteThroughMutablePointer((p, report) =>
        {
            *(int*)p = 7;
            report(3);
        });
        AssertEqual(7003, result, "the write through UnsafeMutablePointer<Int32> reached Swift's cell");
    }

    public unsafe void TestIncrementThroughRawPointer()
    {
        using var host = new NestedPointerCallbackHost();
        var result = host.IncrementThroughRawPointer(1L << 40, (p, report) =>
        {
            *(long*)p += 1;
            report(10);
        });
        AssertEqual((1L << 40) + 11, result, "the raw pointer addressed Swift's Int64 cell");
    }

    public unsafe void TestReadThroughRawPointer()
    {
        using var host = new NestedPointerCallbackHost();
        var result = host.ReadThroughRawPointer(2.5, (p, report) => report(*(double*)p * 4));
        AssertEqual(10.0, result, "the callback read Swift's Double through UnsafeRawPointer");
    }

    public unsafe void TestSendableInnerClosureBesideTypedPointer()
    {
        using var host = new NestedPointerCallbackHost();
        var result = host.ReadThroughTypedPointerReportingSendably(-8, (p, report) => report(*(int*)p * 3));
        AssertEqual(-24, result, "the @Sendable inner closure was called and its report reached Swift");
    }

    public void TestSendableInnerClosureBesideScalar()
    {
        using var host = new NestedPointerCallbackHost();
        var result = host.ReportSendably(12, (value, report) => report(value + 30));
        AssertEqual(42, result, "the @Sendable inner closure was called and its report reached Swift");
    }

    public unsafe void TestInnerClosureReadsThroughTypedPointer()
    {
        using var host = new NestedPointerCallbackHost();
        var result = host.ReadThroughInnerTypedPointer(19, (seed, read) =>
        {
            int cell = seed + 23;
            read((System.IntPtr)(&cell));
        });
        AssertEqual(42, result, "Swift read the callback's Int32 through UnsafePointer<Int32>");
    }

    public unsafe void TestInnerClosureWritesThroughMutablePointer()
    {
        using var host = new NestedPointerCallbackHost();
        long cell = 0;
        host.WriteThroughInnerMutablePointer(write =>
        {
            long local = -1;
            write((System.IntPtr)(&local));
            cell = local;
        });
        AssertEqual((1L << 33) + 5, cell, "Swift's write through UnsafeMutablePointer<Int64> reached the callback's cell");
    }

    public unsafe void TestInnerClosureReadsThroughRawPointer()
    {
        using var host = new NestedPointerCallbackHost();
        var result = host.ReadThroughInnerRawPointer(read =>
        {
            double cell = 6.25;
            read((System.IntPtr)(&cell));
        });
        AssertEqual(6.25, result, "Swift read the callback's Double through UnsafeRawPointer");
    }
}
