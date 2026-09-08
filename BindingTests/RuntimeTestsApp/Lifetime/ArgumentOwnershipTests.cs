// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
using SwiftBindingsTestLib;
using RuntimeTestsApp.Infrastructure;
using System;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace RuntimeTestsApp.Lifetime;

public class ArgumentOwnershipTests : TestBase
{
    public ArgumentOwnershipTests(TestResults results) : base(results) { }

    public void TestExplicitConsumingFreeClass() => RunCase("consume");
    public void TestExplicitBorrowingFreeClass() => RunCase("borrow");
    public void TestExplicitConsumingMemberClass() => RunCase("member");
    public void TestExplicitConsumingClassPair() => RunCase("pair");
    public void TestConsumingDisposedLaterArgumentRollsBack() => RunCase("disposed-later");
    public void TestConsumingSwiftThrowBalances() => RunCase("throw");
    public void TestDirectCallbackPropertyCaptureOwnsValue() => RunCase("property");
    public void TestDirectCallbackDisposePreservesSwiftValue() => RunCase("inside");
    public void TestDirectCallbackPodOwnsIndependentStorage() => RunCase("pod");
    public void TestCallbackClassOwnsIndependentReference() => RunCase("class");
    public void TestOptionalCallbackPropertyOwnsValue() => RunCase("optional");
    public void TestOrdinaryCallbackOwnsValue() => RunCase("ordinary");
    public void TestFailableCallbackSomeOwnsValue() => RunCase("failable-some");
    public void TestFailableCallbackNilOwnsValue() => RunCase("failable-nil");

    public void TestConsumingNonFrozenStructPreservesCallerOwnership() => RunStructCase("consume");
    public void TestBorrowingNonFrozenStructPreservesCallerOwnership() => RunStructCase("borrow");
    public void TestConsumingNonFrozenStructSwiftThrowBalances() => RunStructCase("throw");

    public void TestStaticStringWrapperConsumingNonFrozenStructPreservesCallerOwnership() => RunStructCase("consume", withString: true);
    public void TestStaticStringWrapperBorrowingNonFrozenStructPreservesCallerOwnership() => RunStructCase("borrow", withString: true);
    public void TestStaticStringWrapperConsumingNonFrozenStructSwiftThrowBalances() => RunStructCase("throw", withString: true);
    public void TestStaticStringWrapperConsumingNonFrozenStructDisposedLaterArgumentRollsBack() => RunStructCase("disposed-later", withString: true);

    public void TestStaticStringWrapperConsumingNonFrozenStructWithLiveLaterArgumentPreservesOwnership()
    {
        AssertStaticStringWrapperRoute();
        int deinits = TestLibFunctions.GetOwnershipStructDeinitCount();
        int entries = TestLibFunctions.GetOwnershipStructCallCount();
        using var later = new OwnershipToken(9);
        var value = new OwnershipStructValue(57);
        string text = "start";
        try
        {
            for (int i = 0; i < 3; i++)
                AssertEqual(66, OwnershipStructConsumer.ConsumeDirectWithLater(value, ref text, later),
                    "live later argument contributes to the actual native result");
            AssertEqual(entries + 3, TestLibFunctions.GetOwnershipStructCallCount(), "three successful native entries");
            AssertEqual("start!!!", text, "each successful native call copies back String mutation");
            AssertEqual(57, value.Number, "consuming wrapper preserves the caller's value");
            AssertEqual(9, later.Number, "borrowing later argument remains readable");
            AssertEqual(deinits, TestLibFunctions.GetOwnershipStructDeinitCount(), "caller still owns the value's class field");
        }
        finally { value.Dispose(); }
        AssertEqual(deinits + 1, TestLibFunctions.GetOwnershipStructDeinitCount(), "caller disposal destroys the value's field once");
        value.Dispose();
        AssertEqual(deinits + 1, TestLibFunctions.GetOwnershipStructDeinitCount(), "repeated disposal is inert");
    }

    // Scalar String support deliberately retargets these historically Direct-named fixtures.
    // Assert their actual Cdecl route; direct ValueTransfer coverage remains independently
    // qualified in the retained direct native specimen and non-XCFramework emission test.
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(OwnershipStructConsumer))]
    private void AssertStaticStringWrapperRoute()
    {
        foreach (string name in new[] { "consumeDirect", "borrowDirect", "consumeDirectAndThrow", "consumeDirectWithLater" })
        {
            MethodInfo? import = null;
            foreach (var method in typeof(OwnershipStructConsumer).GetMethods(BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (!method.Name.StartsWith("PInvoke_" + name + "_", StringComparison.Ordinal)) continue;
                AssertTrue(import == null, "exactly one native import for " + name);
                import = method;
            }
            AssertTrue(import != null, "String wrapper native import exists for " + name);
            var nativeImport = import ?? throw new AssertionException("String wrapper native import missing for " + name);
            var library = nativeImport.GetCustomAttribute<LibraryImportAttribute>();
            AssertTrue(library != null, "LibraryImport metadata survives for " + name);
            AssertEqual("SwiftBindings", library!.LibraryName, "wrapper library for " + name);
            AssertTrue(library.EntryPoint?.StartsWith("SBW_", StringComparison.Ordinal) == true,
                "C wrapper export for " + name);
            var convention = nativeImport.GetCustomAttribute<UnmanagedCallConvAttribute>();
            AssertTrue(convention?.CallConvs != null &&
                Array.IndexOf(convention.CallConvs, typeof(global::System.Runtime.CompilerServices.CallConvCdecl)) >= 0,
                "C calling convention for " + name);
            var parameters = nativeImport.GetParameters();
            AssertEqual(name is "consumeDirectAndThrow" or "consumeDirectWithLater" ? 3 : 2,
                parameters.Length, "static import has no receiver slot for " + name);
            AssertEqual(typeof(Swift.SwiftString.Buffer).MakeByRefType(), parameters[1].ParameterType,
                "inout String is one mutable value-storage pointer for " + name);
            if (name == "consumeDirectAndThrow")
                AssertEqual(typeof(IntPtr).MakeByRefType(), parameters[^1].ParameterType, "ordinary error output");
            foreach (var parameter in parameters)
                AssertTrue(parameter.ParameterType != typeof(SwiftSelf), "no instance SwiftSelf for " + name);
        }
    }

    private void RunStructCase(string mode, bool withString = false)
    {
        if (withString) AssertStaticStringWrapperRoute();
        int start = TestLibFunctions.GetOwnershipStructDeinitCount();
        int entries = TestLibFunctions.GetOwnershipStructCallCount();
        int expected = mode == "throw" ? -57 : 57;
        string text = "start";
        using var consumer = new OwnershipStructConsumer();
        var value = new OwnershipStructValue(expected);
        try
        {
            AssertEqual(start, TestLibFunctions.GetOwnershipStructDeinitCount(), "class field is live before call");
            if (mode == "disposed-later")
            {
                using var later = new OwnershipToken(9);
                later.Dispose();
                bool rejected = false;
                try
                {
                    if (withString) OwnershipStructConsumer.ConsumeDirectWithLater(value, ref text, later);
                    else consumer.ConsumeWithLater(value, later);
                }
                catch (ObjectDisposedException) { rejected = true; }
                AssertTrue(rejected, "disposed later argument rejects before native entry");
            }
            else
            {
                for (int i = 0; i < 3; i++)
                {
                    if (mode == "consume") AssertEqual(expected,
                        withString ? OwnershipStructConsumer.ConsumeDirect(value, ref text) : consumer.Consume(value), "consuming struct result");
                    if (mode == "borrow") AssertEqual(expected,
                        withString ? OwnershipStructConsumer.BorrowDirect(value, ref text) : consumer.Borrow(value), "borrowing struct result");
                    if (mode == "throw")
                    {
                        bool threw = false;
                        try
                        {
                            if (withString) OwnershipStructConsumer.ConsumeDirectAndThrow(value, ref text);
                            else consumer.ConsumeAndThrow(value);
                        }
                        catch (SwiftException) { threw = true; }
                        AssertTrue(threw, "consuming struct callee throws");
                    }
                }
            }
            if (withString) AssertEqual(mode == "disposed-later" ? "start" : "start!!!", text,
                "native entry controls String wrapper writeback");
            TestLogger.Info($"Struct ownership withString={withString} {mode}: after call deinits={TestLibFunctions.GetOwnershipStructDeinitCount() - start}, entries={TestLibFunctions.GetOwnershipStructCallCount() - entries}");
            AssertEqual(start, TestLibFunctions.GetOwnershipStructDeinitCount(), "callee must not destroy the caller's class field");
            AssertEqual(expected, value.Number, "caller struct remains readable after consume/throw/entry rejection");
            AssertEqual(entries + (mode == "disposed-later" ? 0 : 3), TestLibFunctions.GetOwnershipStructCallCount(), "native entry count");
        }
        finally { value.Dispose(); }
        TestLogger.Info($"Struct ownership {mode}: after caller Dispose deinits={TestLibFunctions.GetOwnershipStructDeinitCount() - start}");
        AssertEqual(start + 1, TestLibFunctions.GetOwnershipStructDeinitCount(), "struct class field deinitializes exactly once at caller Dispose");
        value.Dispose();
        AssertEqual(start + 1, TestLibFunctions.GetOwnershipStructDeinitCount(), "repeated Dispose is inert");
    }

    public void TestBorrowedMoveSurvivesSourceDispose()
    {
        string expected = new string('x', 32768) + "independent";
        using var source = new Swift.SwiftString(expected);
        using var captured = SwiftMarshal.MarshalCallbackArg<Swift.SwiftString>(source.Payload.DangerousGetHandle());
        source.Dispose();
        AssertEqual(expected, captured.ToString(), "copied Move value survives borrowed source disposal");
    }

    private void RunCase(string mode)
    {
        int start = TestLibFunctions.GetOwnershipDeinitCount();
        if (mode == "consume" || mode == "borrow" || mode == "member" || mode == "throw")
        {
            var token = new OwnershipToken(mode == "throw" ? -5 : 51);
            try
            {
                if (mode == "consume") AssertEqual(51, TestLibFunctions.ConsumeOwnershipToken(token), mode);
                if (mode == "borrow") AssertEqual(51, TestLibFunctions.BorrowOwnershipToken(token), mode);
                if (mode == "member") { using var consumer = new OwnershipConsumer(); AssertEqual(51, consumer.Consume(token), mode); }
                if (mode == "throw")
                {
                    try
                    {
                        TestLibFunctions.ConsumeOwnershipThenThrow(token);
                        throw new Exception("expected failure");
                    }
                    catch (SwiftException) when (mode == "throw") { }
                }
                AssertEqual(start, TestLibFunctions.GetOwnershipDeinitCount(), "caller still owns argument");
                AssertEqual(mode == "throw" ? -5 : 51, token.Number, "argument remains readable");
            }
            finally { token.Dispose(); }
            AssertEqual(start + 1, TestLibFunctions.GetOwnershipDeinitCount(), "argument exactly one deinit");
        }
        else if (mode == "pair" || mode == "disposed-later")
        {
            var first = new OwnershipToken(21);
            var second = new OwnershipToken(22);
            try
            {
                if (mode == "disposed-later")
                {
                    second.Dispose();
                    try { TestLibFunctions.ConsumeOwnershipPair(first, second); throw new Exception("expected disposed failure"); }
                    catch (ObjectDisposedException) { }
                    AssertEqual(start + 1, TestLibFunctions.GetOwnershipDeinitCount(), "only disposed second deinitialized");
                }
                else AssertEqual(43, TestLibFunctions.ConsumeOwnershipPair(first, second), "consuming pair");
                AssertEqual(21, first.Number, "first remains readable");
            }
            finally { first.Dispose(); second.Dispose(); }
            AssertEqual(start + 2, TestLibFunctions.GetOwnershipDeinitCount(), "pair exactly two deinits");
        }
        else if (mode == "pod")
        {
            using var owner = new OwnershipCallbackOwner();
            CallbackPodValue? captured = null;
            owner.PodCallback = v => captured = v;
            owner.InvokePod();
            AssertEqual(72, captured!.Number, "POD independent buffer after callback");
            captured.Dispose();
        }
        else if (mode == "class")
        {
            using var owner = new OwnershipCallbackOwner();
            OwnershipToken? captured = null;
            owner.ClassCallback = v => captured = v;
            owner.InvokeClass();
            AssertEqual(start, TestLibFunctions.GetOwnershipDeinitCount(), "captured class stays live");
            AssertEqual(73, captured!.Number, "class object reader identity");
            captured.Dispose();
            AssertEqual(start + 1, TestLibFunctions.GetOwnershipDeinitCount(), "captured class exactly one deinit");
        }
        else
        {
            using var owner = new OwnershipCallbackOwner();
            CallbackOwnedValue? captured = null;
            Action<CallbackOwnedValue> body = v =>
            {
                AssertEqual(start, TestLibFunctions.GetOwnershipDeinitCount(), "source alive inside callback");
                if (mode == "inside")
                {
                    v.Dispose();
                    AssertEqual(start, TestLibFunctions.GetOwnershipDeinitCount(), "callback Dispose preserves native source");
                }
                else captured = v;
            };
            int expected = mode switch { "optional" => 74, "ordinary" => 75, "failable-some" or "failable-nil" => 76, _ => 71 };
            if (mode.StartsWith("failable"))
            {
                bool expectedSome = mode == "failable-some";
                bool some = OwnershipCallbackFactory.TryCreate(body, expectedSome, out var factory);
                if (some != expectedSome) throw new Exception("failable result");
                factory?.Dispose();
            }
            else if (mode == "optional") { owner.OptionalCallback = body; owner.InvokeOptional(); }
            else if (mode == "ordinary") owner.Ordinary(body);
            else { owner.Callback = body; owner.Invoke(); }
            if (mode != "inside")
            {
                AssertEqual(start, TestLibFunctions.GetOwnershipDeinitCount(), "captured value keeps native payload alive");
                AssertEqual(expected, captured!.Number, "capture reads after callback");
                captured.Dispose();
            }
            AssertEqual(start + 1, TestLibFunctions.GetOwnershipDeinitCount(), "callback payload exactly one deinit");
        }
        TestLogger.Info("Ownership probe passed: " + mode);
    }
}
