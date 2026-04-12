// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Tests that Dispose() is safe on all generated type categories.
/// Verifies VWT Destroy (value types) and Arc.Release (classes) work correctly,
/// proving that `using var` is safe for all ISwiftObject types.
/// </summary>
public class DisposeTests : TestBase
{
    public DisposeTests(TestResults results) : base(results) { }

    #region Non-frozen struct (ClassWithOpaquePayload — VWT Destroy)

    public void TestDisposeNonFrozenStruct()
    {
        var config = TestLibFunctions.MakeDefaultConfig();
        config.Dispose();
        AssertTrue(true, "Non-frozen struct Dispose did not crash");
    }

    public void TestDisposeNonFrozenStructAfterUse()
    {
        var config = TestLibFunctions.MakeDefaultConfig();
        var timeout = TestLibFunctions.GetConfigTimeout(config);
        config.Dispose();
        AssertTrue(timeout >= 0, "Non-frozen struct usable before Dispose");
    }

    public void TestDoubleDisposeNonFrozenStruct()
    {
        var config = TestLibFunctions.MakeDefaultConfig();
        config.Dispose();
        config.Dispose();
        AssertTrue(true, "Non-frozen struct double-Dispose did not crash");
    }

    public void TestDisposeNonFrozenPoint()
    {
        var point = new NonFrozenPoint(3.0, 4.0);
        point.Dispose();
        AssertTrue(true, "NonFrozenPoint Dispose did not crash");
    }

    #endregion

    #region Swift class (Arc.Release)

    public void TestDisposeClass()
    {
        var service = new EvolvingService("dispose-test", true);
        service.Dispose();
        AssertTrue(true, "Class Dispose did not crash");
    }

    public void TestDisposeClassAfterUse()
    {
        var service = new EvolvingService("dispose-test", true);
        var name = service.Name.ToString();
        service.Dispose();
        AssertEqual("dispose-test", name, "Class usable before Dispose");
    }

    public void TestDoubleDisposeClass()
    {
        var service = new EvolvingService("dispose-test", true);
        service.Dispose();
        service.Dispose();
        AssertTrue(true, "Class double-Dispose did not crash");
    }

    #endregion

    #region Frozen C# struct (no-op Dispose)

    public void TestDisposeFrozenStruct()
    {
        var point = new FrozenPoint(1.0, 2.0);
        point.Dispose();
        AssertTrue(true, "Frozen struct Dispose did not crash");
    }

    public void TestDoubleDisposeFrozenStruct()
    {
        var point = new FrozenPoint(1.0, 2.0);
        point.Dispose();
        point.Dispose();
        AssertTrue(true, "Frozen struct double-Dispose did not crash");
    }

    #endregion

    #region Enum singleton (cached — Dispose guarded)

    public void TestDisposeSingletonEnum()
    {
        var ok = StatusCode.Ok;
        ok.Dispose();
        AssertTrue(true, "Singleton enum Dispose did not crash");
    }

    public void TestSingletonEnumUsableAfterDispose()
    {
        var ok = StatusCode.Ok;
        ok.Dispose();
        // Singleton is still usable because Dispose is a no-op
        AssertEqual(StatusCode.CaseTag.Ok, ok.Tag, "Singleton enum still valid after Dispose");
    }

    #endregion

    #region Enum non-singleton (FromRawValue — VWT Destroy)

    public void TestDisposeNonSingletonEnum()
    {
        var code = StatusCode.FromRawValue("OK");
        AssertNotNull(code, "FromRawValue created instance");
        code!.Dispose();
        AssertTrue(true, "Non-singleton enum Dispose did not crash");
    }

    public void TestDoubleDisposeNonSingletonEnum()
    {
        var code = StatusCode.FromRawValue("ERROR");
        AssertNotNull(code, "FromRawValue created instance");
        code!.Dispose();
        code.Dispose();
        AssertTrue(true, "Non-singleton enum double-Dispose did not crash");
    }

    #endregion

    #region Frozen struct with ref fields (ClassWithBufferStruct — VWT Destroy)

    public void TestDisposeFrozenStructWithRef()
    {
        var fs = new FrozenStructWithRef(42);
        fs.Dispose();
        AssertTrue(true, "Frozen struct with ref Dispose did not crash");
    }

    public void TestDisposeFrozenStructWithRefAfterUse()
    {
        var fs = new FrozenStructWithRef(99);
        var value = fs.GetValue();
        fs.Dispose();
        AssertEqual(99, value, "Frozen struct with ref usable before Dispose");
    }

    public void TestDoubleDisposeFrozenStructWithRef()
    {
        var fs = new FrozenStructWithRef(42);
        fs.Dispose();
        fs.Dispose();
        AssertTrue(true, "Frozen struct with ref double-Dispose did not crash");
    }

    #endregion

    #region SafeHandle state after Dispose

    public void TestPayloadClosedAfterDispose()
    {
        var config = TestLibFunctions.MakeDefaultConfig();
        AssertFalse(config.Payload.IsClosed, "Payload open before Dispose");
        config.Dispose();
        AssertTrue(config.Payload.IsClosed, "Payload closed after Dispose");
    }

    public void TestClassHandleClosedAfterDispose()
    {
        var service = new EvolvingService("handle-test", true);
        service.Dispose();
        AssertTrue(service.Payload.IsClosed, "Class handle closed after Dispose");
    }

    #endregion
}
