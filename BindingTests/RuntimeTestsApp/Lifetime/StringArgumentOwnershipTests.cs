// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

public class StringArgumentOwnershipTests : TestBase
{
    public StringArgumentOwnershipTests(TestResults results) : base(results) { }

    public void TestConsumingStringWrapperPreservesValue() => Ownership("consume");
    public void TestBorrowingStringWrapperPreservesValue() => Ownership("borrow");
    public void TestThrowingStringWrapperSuccess() => Ownership("throw-success");
    public void TestThrowingStringWrapperErrorCopiesBack() => Ownership("throw");
    public void TestStringWrapperLaterArgument() => Ownership("later");
    public void TestStringWrapperDisposedLaterNeverEnters() => Ownership("disposed-later");

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(StringOwnershipReceiver))]
    private void AssertOwnershipImports()
    {
        foreach (string name in new[] { "consume", "borrow", "consumeAndThrow", "consumeWithLater", "replace" })
        {
            MethodInfo? import = null;
            foreach (var candidate in typeof(StringOwnershipReceiver).GetMethods(BindingFlags.Static | BindingFlags.NonPublic))
                if (candidate.Name.StartsWith("PInvoke_" + name + "_", StringComparison.Ordinal))
                {
                    AssertTrue(import == null, "unique import " + name);
                    import = candidate;
                }
            AssertTrue(import != null, "native import " + name);
            var method = import ?? throw new AssertionException("native import missing: " + name);
            var library = method.GetCustomAttribute<LibraryImportAttribute>();
            AssertEqual("SwiftBindings", library?.LibraryName, "wrapper library " + name);
            AssertTrue(library?.EntryPoint?.StartsWith("SBW_", StringComparison.Ordinal) == true, "wrapper export " + name);
            var conventions = method.GetCustomAttribute<UnmanagedCallConvAttribute>()?.CallConvs;
            AssertTrue(conventions != null && Array.IndexOf(conventions, typeof(global::System.Runtime.CompilerServices.CallConvCdecl)) >= 0, "Cdecl " + name);
            var parameters = method.GetParameters();
            AssertEqual(name == "replace" ? 5 : name is "consumeAndThrow" or "consumeWithLater" ? 4 : 3,
                parameters.Length, "complete import parameter count " + name);
            AssertEqual(typeof(Swift.SwiftString.Buffer).MakeByRefType(), parameters[1].ParameterType, "one String storage address " + name);
            int selfIndex = name == "consumeAndThrow" ? parameters.Length - 2 : parameters.Length - 1;
            AssertEqual(typeof(IntPtr), parameters[selfIndex].ParameterType, "ordinary receiver address " + name);
            if (name == "consumeAndThrow")
                AssertEqual(typeof(IntPtr).MakeByRefType(), parameters[^1].ParameterType, "ordinary error output");
        }
    }

    private void Ownership(string mode)
    {
        AssertOwnershipImports();
        int allocations = TestLibFunctions.GetStringOwnershipAllocationCount();
        int deinits = TestLibFunctions.GetStringOwnershipDeinitCount();
        int entries = TestLibFunctions.GetStringOwnershipEntryCount();
        int number = mode == "throw" ? -7 : 7;
        int count = mode == "disposed-later" ? 0 : mode == "throw" ? 1 : 64;
        using var receiver = new StringOwnershipReceiver(11);
        var value = new StringOwnershipValue(number);
        string text = new string("start".ToCharArray());
        string original = text;
        try
        {
            AssertEqual(allocations + 1, TestLibFunctions.GetStringOwnershipAllocationCount(), "one native token allocated");
            using var later = new StringOwnershipLater(5);
            if (mode == "disposed-later")
            {
                later.Dispose();
                bool rejected = false;
                try { receiver.ConsumeWithLater(value, ref text, later); }
                catch (ObjectDisposedException) { rejected = true; }
                AssertTrue(rejected, "disposed later fails before entry");
                AssertTrue(ReferenceEquals(original, text), "failed entry preserves managed String identity");
            }
            for (int i = 0; i < count; i++)
            {
                bool threw = false;
                try
                {
                    int result = mode switch
                    {
                        "borrow" => receiver.Borrow(value, ref text),
                        "throw" or "throw-success" => receiver.ConsumeAndThrow(value, ref text),
                        "later" => receiver.ConsumeWithLater(value, ref text, later),
                        _ => receiver.Consume(value, ref text)
                    };
                    AssertEqual(number + 11 + (mode == "later" ? 5 : 0), result, "stored receiver bias contributes to result");
                }
                catch (SwiftException ex)
                {
                    AssertEqual("throw", mode, "only the negative value throws");
                    AssertTrue(ex.Message.Contains("expected", StringComparison.Ordinal), "native expected error");
                    threw = true;
                }
                AssertEqual(mode == "throw", threw, "Swift error outcome");
                original += "|changed";
                AssertEqual(original, text, "native defer reaches public copyback");
                AssertEqual(number, value.Number, "original caller value remains usable");
                AssertEqual(entries + i + 1, TestLibFunctions.GetStringOwnershipEntryCount(), "native entries");
                AssertEqual(deinits, TestLibFunctions.GetStringOwnershipDeinitCount(), "caller token remains live");
            }
            AssertEqual(entries + count, TestLibFunctions.GetStringOwnershipEntryCount(), "total native entries");
            AssertEqual(allocations + 1, TestLibFunctions.GetStringOwnershipAllocationCount(), "no extra token allocations");
            AssertEqual(deinits, TestLibFunctions.GetStringOwnershipDeinitCount(), "no premature token destruction");
            AssertEqual(number, value.Number, "caller remains readable after failure or success");
        }
        finally { value.Dispose(); }
        AssertEqual(deinits + 1, TestLibFunctions.GetStringOwnershipDeinitCount(), "exact final destruction");
        AssertEqual(0, (TestLibFunctions.GetStringOwnershipAllocationCount() - allocations) -
            (TestLibFunctions.GetStringOwnershipDeinitCount() - deinits), "zero remaining native tokens");
        value.Dispose();
        AssertEqual(deinits + 1, TestLibFunctions.GetStringOwnershipDeinitCount(), "repeated disposal is inert");
        TestLogger.Info($"String ownership {mode}: entries={count}, allocations=1, deinits=1, bias=11");
    }

    public void TestStringWrapperReplacementTransitions()
    {
        using var receiver = new StringOwnershipReceiver(11);
        using var value = new StringOwnershipValue(7);
        var pairs = new (string Before, string After)[] {
            (new string('a', 256), "x"), ("x", new string('b', 320)),
            (new string('c', 256), new string('d', 384)), ("", ""),
            ("", "漢字🙂e\u0301"), ("漢字🙂e\u0301", ""), ("old\0tail", "new\0🙂\0tail")
        };
        foreach (var pair in pairs)
        {
            string text = pair.Before;
            AssertEqual(18, receiver.Replace(value, ref text, pair.After), "replacement receiver/value");
            AssertEqual(pair.After, text, "exact String replacement including NUL and Unicode");
            AssertEqual(7, value.Number, "caller survives replacement");
        }
    }

    public void TestStringWrapperSourceShapes()
    {
        using var receiver = new StringOwnershipReceiver(11);
        string text = "middle";
        AssertEqual(11, receiver.StringNeighbors("before", ref text, "after"), "receiver bias with by-value neighbors");
        AssertEqual("before[middle]after", text, "by-value words surround one address");
        string first = "first", second = "second";
        AssertEqual(11, receiver.ReplaceBoth(ref first, ref second), "receiver bias with two addresses");
        AssertEqual("second|first", first, "first independent writeback");
        AssertEqual("first|second", second, "second independent writeback");
        text = "start";
        AssertEqual(39, receiver.AfterScalars(1, 2, 3, 4, 5, 6, 7, ref text), "seven leading scalars plus stored bias");
        AssertEqual("start|scalars", text, "String after seven scalars");
        text = "start";
        AssertEqual(23, StringOwnershipReceiver.StaticText(ref text), "static result");
        AssertEqual("start|static", text, "static copyback");
        AssertEqual(29, TestLibFunctions.StringOwnershipFreeText(ref text), "free function result");
        AssertEqual("start|static|free", text, "free function copyback");
    }

    public void TestStringWrapperDefaultAndDebugShims()
    {
        using var receiver = new StringOwnershipReceiver(11);
        using var value = new StringOwnershipValue(7);
        string text = "start";
        AssertEqual(21, receiver.WithDefaults(value, ref text), "two trimmed defaults");
        AssertEqual("start|default", text, "trimmed by-value String default");
        AssertEqual(21, receiver.WithDefaults(value, ref text, "|chosen"), "one trimmed default");
        AssertEqual("start|default|chosen", text, "inherited wrapper with explicit suffix");
        AssertEqual(22, receiver.WithDefaults(value, ref text, "|full", 4), "full primary method");
        AssertEqual("start|default|chosen|full", text, "full signature writeback");
        AssertEqual(11, receiver.DroppingClosureDefault(ref text), "independently eligible no-callback trim");
        AssertEqual("start|default|chosen|full|trimmed", text, "trim calls real Swift default");
        AssertEqual(11, receiver.DebugDefault(ref text), "debug-default shim receives nonempty file and correct self");
        AssertEqual("start|default|chosen|full|trimmed|debug", text, "debug shim inout writeback");
        AssertEqual(7, value.Number, "caller survives default shims");
    }

    public void TestOptionalCallbackStringFullAndTrim()
    {
        using var receiver = new StringOwnershipReceiver(11);
        int calls = 0;
        foreach (var initial in new[] { "", new string('漢', 4096) })
        {
            string text = initial;
            string expected = initial;
            for (int i = 0; i < 32; i++)
            {
                AssertEqual(11, receiver.DroppingClosureDefault(ref text, null), "full null callback result");
                expected += "|trimmed";
                AssertEqual(expected, text, "null callback mutation");
                AssertEqual(11, receiver.DroppingClosureDefault(ref text, () => calls++), "full callback result");
                expected += "|trimmed";
                AssertEqual(expected, text, "non-null callback mutation");
                AssertEqual(11, receiver.DroppingClosureDefault(ref text), "independent trim result");
                expected += "|trimmed";
                AssertEqual(expected, text, "trim mutation");
            }
        }
        AssertEqual(64, calls, "one invocation per non-null callback");
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(StringOwnershipReceiver))]
    public void TestOptionalCallbackStringImportsPreserveFullAndTrim()
    {
        int count = 0;
        foreach (var import in typeof(StringOwnershipReceiver).GetMethods(BindingFlags.Static | BindingFlags.NonPublic))
        {
            if (!import.Name.StartsWith("PInvoke_droppingClosureDefault_", StringComparison.Ordinal)) continue;
            count++;
            var parameters = import.GetParameters();
            AssertTrue(parameters.Length is 2 or 4, "trim or full pointer-pair signature");
            AssertEqual(typeof(Swift.SwiftString.Buffer).MakeByRefType(), parameters[0].ParameterType);
            AssertEqual(typeof(IntPtr), parameters[^1].ParameterType, "receiver address");
            var library = import.GetCustomAttribute<LibraryImportAttribute>();
            AssertEqual("SwiftBindings", library?.LibraryName);
            AssertEqual(parameters.Length == 2
                ? "SBW_SwiftBindingsTestLib_StringOwnershipReceiver_droppingClosureDefault_7386B826"
                : "SBW_SwiftBindingsTestLib_StringOwnershipReceiver_droppingClosureDefault_C7CC85E1_cdecl", library?.EntryPoint);
            var conventions = import.GetCustomAttribute<UnmanagedCallConvAttribute>()?.CallConvs;
            AssertTrue(conventions != null && Array.IndexOf(conventions,
                typeof(System.Runtime.CompilerServices.CallConvCdecl)) >= 0);
        }
        AssertEqual(2, count, "full overload and independently named trim");
    }

    public void TestOptionalCallbackStringReceiverAndScalarControls()
    {
        int allocations = TestLibFunctions.GetStringOwnershipAllocationCount();
        int deinits = TestLibFunctions.GetStringOwnershipDeinitCount();
        var receiver = new StringOwnershipCallbackReceiver(11);
        var frozen = new StringOwnershipFrozenCallbackReceiver(13);
        int calls = 0;
        string text = "";
        string expected = "";
        for (int i = 0; i < 64; i++)
        {
            Action? callback = (i & 1) == 0 ? null : () => calls++;
            AssertEqual(19, receiver.Update(3, ref text, 5, callback), "non-final self between scalar neighbors");
            AssertEqual(21, frozen.Update(3, ref text, 5, callback), "frozen self between scalar neighbors");
            AssertEqual(8, TestLibFunctions.StringOwnershipFreeCallback(3, ref text, 5, callback), "free function scalar neighbors");
            expected += "|callback|callback|callback";
            AssertEqual(expected, text, "all three routes copy back");
            AssertEqual(deinits, TestLibFunctions.GetStringOwnershipDeinitCount(), "receiver remains live");
        }
        AssertEqual(96, calls, "callback count across receiver shapes");
        AssertEqual(allocations + 1, TestLibFunctions.GetStringOwnershipAllocationCount(), "one receiver token");
        receiver.Dispose();
        AssertEqual(deinits + 1, TestLibFunctions.GetStringOwnershipDeinitCount(), "receiver releases once");
        receiver.Dispose();
        AssertEqual(deinits + 1, TestLibFunctions.GetStringOwnershipDeinitCount(), "no double release");
        AssertEqual(expected, text, "result survives receiver disposal");
    }

    private sealed class CallbackSentinel { public int Calls; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference InvokeUnstoredEscapingCallback()
    {
        var sentinel = new CallbackSentinel();
        using var receiver = new StringOwnershipReceiver(11);
        string text = "";
        AssertEqual(11, receiver.DroppingClosureDefault(ref text, () => sentinel.Calls++));
        AssertEqual(1, sentinel.Calls);
        AssertEqual("|trimmed", text);
        return new WeakReference(sentinel);
    }

    public void TestOptionalCallbackStringReleasesEscapingContext()
    {
        var reference = InvokeUnstoredEscapingCallback();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        AssertFalse(reference.IsAlive, "unstored optional escaping callback releases managed capture");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference InvokeThrowingEscapingCallback()
    {
        var sentinel = new CallbackSentinel();
        using var receiver = new StringOwnershipCallbackReceiver(11);
        string text = "";
        bool threw = false;
        try { receiver.UpdateAndThrow(-3, ref text, 5, () => sentinel.Calls++); }
        catch (SwiftException) { threw = true; }
        AssertTrue(threw);
        AssertEqual(1, sentinel.Calls);
        AssertEqual("|throwing-callback", text);
        return new WeakReference(sentinel);
    }

    public void TestOptionalCallbackStringThrowReleasesEscapingContext()
    {
        var reference = InvokeThrowingEscapingCallback();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        AssertFalse(reference.IsAlive, "throwing invocation releases optional escaping capture");
    }

    public void TestOptionalCallbackStringMutationBeforeThrow()
    {
        int deinits = TestLibFunctions.GetStringOwnershipDeinitCount();
        var receiver = new StringOwnershipCallbackReceiver(11);
        int callbacks = 0;
        string text = new string('x', 4096), expected = text;
        for (int i = 0; i < 32; i++)
        {
            bool shouldThrow = (i & 1) == 0;
            bool threw = false;
            try
            {
                AssertEqual(19, receiver.UpdateAndThrow(shouldThrow ? -3 : 3, ref text, 5, () => callbacks++));
            }
            catch (SwiftException ex)
            {
                AssertTrue(ex.Message.Contains("expected", StringComparison.Ordinal));
                threw = true;
            }
            AssertEqual(shouldThrow, threw);
            expected += "|throwing-callback";
            AssertEqual(expected, text, "mutation before error is copied back");
            AssertEqual(i + 1, callbacks, "callback before error runs once");
            AssertEqual(deinits, TestLibFunctions.GetStringOwnershipDeinitCount(), "receiver survives thrown call");
        }
        receiver.Dispose();
        AssertEqual(deinits + 1, TestLibFunctions.GetStringOwnershipDeinitCount(), "throwing receiver released exactly once");
        AssertEqual(expected, text, "mutated result survives disposal");
    }
}
