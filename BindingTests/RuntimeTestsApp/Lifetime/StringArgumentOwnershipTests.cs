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
}
