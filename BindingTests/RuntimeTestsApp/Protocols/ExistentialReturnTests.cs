// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using SwiftBindingsTestLib.SwiftInterop;
using Swift;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Tests for protocol existential RETURN types — methods, properties, and statics
/// that return `any Protocol`. Covers the R3 regression where the emitter generates
/// SwiftMarshal.MarshalFromSwift&lt;IProtocol&gt;() instead of wrapping in a proxy class.
///
/// Real-world patterns: Swinject (Assembler.Resolver), SwiftyBeaver (FilterFactory.Custom).
///
/// Tier structure:
/// - Tier 1: Factory construction
/// - Tier 2: Property/method/static returning existential
/// - Tier 3: Existential round-trip through constructor, closure variant
/// </summary>
public class ExistentialReturnTests : TestBase
{
    public ExistentialReturnTests(TestResults results) : base(results) { }

    #region Factory Construction (Tier 1)

    public void TestERTestFactoryConstruction()
    {
        var factory = new ERTestFactory();
        AssertNotNull(factory, "ERTestFactory constructed");
        TestLogger.Info("ERTestFactory() construction passed");
    }

    #endregion

    #region Property Returning Existential (Tier 2 — R3 regression)

    public void TestDefaultItemPropertyReturnsExistential()
    {
        var factory = new ERTestFactory();
        var item = factory.DefaultItem;
        AssertNotNull(item, "DefaultItem property returned non-null IERTestProtocol");
        TestLogger.Info("ERTestFactory.DefaultItem returned IERTestProtocol successfully");
    }

    public void TestDefaultItemLabel()
    {
        var factory = new ERTestFactory();
        var item = factory.DefaultItem;
        AssertEqual("default", item.Label, "DefaultItem.Label");
        TestLogger.Info($"DefaultItem.Label = \"{item.Label}\"");
    }

    public void TestDefaultItemDescribe()
    {
        var factory = new ERTestFactory();
        var item = factory.DefaultItem;
        var desc = item.GetDescribe();
        AssertEqual("Item: default", desc, "DefaultItem.GetDescribe()");
        TestLogger.Info($"DefaultItem.GetDescribe() = \"{desc}\"");
    }

    #endregion

    #region Method Returning Existential (Tier 2 — R3 regression)

    public void TestCreateItemReturnsExistential()
    {
        var factory = new ERTestFactory();
        var item = factory.CreateItem("test");
        AssertNotNull(item, "CreateItem returned non-null IERTestProtocol");
        TestLogger.Info("ERTestFactory.CreateItem(\"test\") returned IERTestProtocol successfully");
    }

    public void TestCreateItemLabel()
    {
        var factory = new ERTestFactory();
        var item = factory.CreateItem("hello");
        AssertEqual("hello", item.Label, "CreateItem(\"hello\").Label");
        TestLogger.Info($"CreateItem.Label = \"{item.Label}\"");
    }

    public void TestCreateItemDescribe()
    {
        var factory = new ERTestFactory();
        var item = factory.CreateItem("world");
        var desc = item.GetDescribe();
        AssertEqual("Item: world", desc, "CreateItem(\"world\").GetDescribe()");
        TestLogger.Info($"CreateItem.GetDescribe() = \"{desc}\"");
    }

    #endregion

    #region Static Method Returning Existential (Tier 2 — R3 regression)

    public void TestSharedReturnsExistential()
    {
        var item = ERTestFactory.GetShared();
        AssertNotNull(item, "Shared() returned non-null IERTestProtocol");
        TestLogger.Info("ERTestFactory.GetShared() returned IERTestProtocol successfully");
    }

    public void TestSharedLabel()
    {
        var item = ERTestFactory.GetShared();
        AssertEqual("shared", item.Label, "Shared().Label");
        TestLogger.Info($"Shared().Label = \"{item.Label}\"");
    }

    #endregion

    #region Existential Round-Trip via Constructor (Tier 3 — RxSwift pattern)

    public void TestERTestHolderConstruction()
    {
        var factory = new ERTestFactory();
        var item = factory.CreateItem("held");
        var holder = new ERTestHolder(item);
        AssertNotNull(holder, "ERTestHolder constructed with existential");
        TestLogger.Info("ERTestHolder(IERTestProtocol) construction passed");
    }

    public void TestERTestHolderHeldLabel()
    {
        var factory = new ERTestFactory();
        var item = factory.CreateItem("roundtrip");
        var holder = new ERTestHolder(item);
        var label = holder.HeldLabel;
        AssertEqual("roundtrip", label, "HeldLabel after existential round-trip");
        TestLogger.Info($"ERTestHolder.HeldLabel = \"{label}\"");
    }

    #endregion

    #region Closure + Existential Return (Tier 3 — SwiftyBeaver FilterFactory pattern)

    public void TestERTestFilterFactoryCustomReturnsExistential()
    {
        var item = ERTestFilterFactory.Custom(s => s.Length > 3);
        AssertNotNull(item, "ERTestFilterFactory.Custom returned non-null IERTestProtocol");
        TestLogger.Info("ERTestFilterFactory.Custom(closure) returned IERTestProtocol successfully");
    }

    public void TestERTestFilterFactoryCustomLabel()
    {
        var item = ERTestFilterFactory.Custom(s => true);
        AssertEqual("custom", item.Label, "ERTestFilterFactory.Custom().Label");
        TestLogger.Info($"ERTestFilterFactory.Custom().Label = \"{item.Label}\"");
    }

    #endregion
}
