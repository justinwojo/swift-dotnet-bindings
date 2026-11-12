// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Lane-identity runner. Runs exactly one fixture (`--iif-fixture <name>`) against the generated
// InterpIdentityFixture binding, then prints what it saw:
//
//   IIF START fixture=<name> framework=<description> arch=<arch> configuration=<Debug|Release>
//   IIF BEGIN <name>
//   IIF CAPTURE <serial> <index> 0x<address> <image> <symbol|->   (written by the Swift fixture)
//   IIF OBSERVE <name> <measurement>
//   IIF RESULT <name> PASS|FAIL <detail>
//   IIF END <name>
//
// The capture lines are the return addresses the Swift fixture recorded on its own stack, resolved
// with dladdr and written during the call itself, so a call that crashes afterwards still leaves
// them. Interpretation (which frame is the caller, which lane it proves) belongs to the harness.
// One fixture per process: a negative control can crash, and the
// arm64 x20 defect leaves the thread in a state that poisons every later call.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using InterpIdentityFixture;
using Swift;
using Swift.Runtime;

static unsafe class Program
{
#if DEBUG
    const string Configuration = "Debug";
#else
    const string Configuration = "Release";
#endif

    static string fixture = "";

    static int Main(string[] args)
    {
        // The iOS host does not always hand launch arguments to Main; the process info always has them.
        if (args.Length == 0)
            args = Foundation.NSProcessInfo.ProcessInfo.Arguments.Skip(1).ToArray();
        var at = Array.IndexOf(args, "--iif-fixture");
        fixture = at >= 0 && at + 1 < args.Length ? args[at + 1] : "";
        Emit($"IIF START fixture={fixture} framework={RuntimeInformation.FrameworkDescription.Replace(' ', '_')} " +
             $"arch={RuntimeInformation.ProcessArchitecture} configuration={Configuration}");

        Func<List<string>, bool>? body = fixture switch
        {
            "primitive" => Primitive,
            "string" => StringRoundTrip,
            "class-member" => ClassMember,
            "self-receiver" => SelfReceiver,
            "self-error" => SelfError,
            "indirect-result" => IndirectResult,
            "reverse-error-mid" => ReverseErrorMid,
            "reverse-context-last" => ReverseContextLast,
            _ => null,
        };
        if (body == null)
        {
            Emit($"IIF UNKNOWN {fixture}");
            return Finish(2);
        }

        Emit($"IIF BEGIN {fixture}");
        var issues = new List<string>();
        bool passed;
        try
        {
            passed = body(issues) && issues.Count == 0;
        }
        catch (Exception ex)
        {
            issues.Add($"unexpected {ex.GetType().Name}: {ex.Message}");
            passed = false;
        }
        Emit($"IIF RESULT {fixture} {(passed ? "PASS" : "FAIL")} {string.Join("; ", issues)}".TrimEnd());
        Emit($"IIF END {fixture}");
        return Finish(0);
    }

    static int Finish(int code)
    {
        // The launcher stops polling on these two lines; they say the process completed, not
        // that the fixture passed.
        Emit("RESULTS FLUSHED");
        Emit("TEST SUCCESS");
        Console.Out.Flush();
        Environment.Exit(code);
        return code;
    }

    static void Emit(string line)
    {
        Console.WriteLine(line);
        Console.Out.Flush();
    }

    static void Observe(string text) => Emit($"IIF OBSERVE {fixture} {text}");

    // MARK: - Positive controls

    static bool Primitive(List<string> issues)
    {
        var r = Functions.IifIdentityProbe(13);
        if (r != 40) issues.Add($"IifIdentityProbe(13) returned {r}, expected 40");
        return true;
    }

    static bool StringRoundTrip(List<string> issues)
    {
        var r = Functions.IifGreeting("swift");
        if (r != "hello, swift") issues.Add($"IifGreeting returned '{r}'");
        return true;
    }

    static bool ClassMember(List<string> issues)
    {
        using var counter = new IifCounter(5);
        var r = counter.Increment(3);
        if (r != 8 || counter.Count != 8) issues.Add($"Increment(3) from 5 returned {r}, Count {counter.Count}");
        return true;
    }

    // MARK: - Negative controls

    // The receiver is a non-frozen struct passed by address in the self register, and Swift
    // writes the address it received into point.y. Its first stored property sits at offset 0;
    // the arm64 interpreter's x20 defect writes 1 into the word four bytes in, which reads back
    // as the tag plus 2^32. The tag is read straight from the payload, not through the binding,
    // so the measurement makes no further transition.
    const nint Tag = 5;

    static void StampOnce(IifSlot slot, nint handle, int call, List<string> issues, bool throwing = false, bool fail = false)
    {
        var point = new IifPoint(0, 0);
        try
        {
            Exception? thrown = null;
            try
            {
                if (throwing) slot.StampThrowing(ref point, fail);
                else slot.Stamp(ref point);
            }
            catch (Exception ex) when (throwing)
            {
                thrown = ex;
            }

            var tag = *(nint*)handle;
            if (tag != Tag)
            {
                issues.Add($"call {call}: receiver word 0 changed from 0x{Tag:x} to 0x{tag:x}");
                // Restore it so each later call is measured from the same state.
                *(nint*)handle = Tag;
            }
            if (point.YNative != handle)
                issues.Add($"call {call}: Swift saw self 0x{point.YNative:x}, expected 0x{handle:x}");
            var expectedX = fail ? 0 : 1;
            if (point.XNative != expectedX)
                issues.Add($"call {call}: point.x is {point.XNative}, expected {expectedX}");
            if (throwing)
            {
                Observe($"call {call}: fail={fail} thrown={(thrown == null ? "none" : thrown.GetType().Name)}");
                if (fail && thrown == null) issues.Add($"call {call}: the Swift error did not surface");
                if (!fail && thrown != null) issues.Add($"call {call}: unexpected {thrown.GetType().Name}: {thrown.Message}");
            }
        }
        finally
        {
            point.Dispose();
        }
    }

    static bool SelfReceiver(List<string> issues)
    {
        using var slot = new IifSlot(Tag);
        var handle = ((ISwiftObject)slot).SwiftHandle;
        Observe($"receiver=0x{handle:x} word0=0x{*(nint*)handle:x}");
        for (var call = 0; call < 4; call++)
            StampOnce(slot, handle, call, issues);
        return true;
    }

    static bool SelfError(List<string> issues)
    {
        using var slot = new IifSlot(Tag);
        var handle = ((ISwiftObject)slot).SwiftHandle;
        Observe($"receiver=0x{handle:x} word0=0x{*(nint*)handle:x}");
        StampOnce(slot, handle, 0, issues, throwing: true, fail: false);
        StampOnce(slot, handle, 1, issues, throwing: true, fail: true);
        return true;
    }

    static bool IndirectResult(List<string> issues)
    {
        if (!IifFailable.TryCreate(21, out var made))
            issues.Add("TryCreate(21) returned nil");
        else
        {
            using (made)
                if (made.Value != 42) issues.Add($"TryCreate(21).Value is {made.Value}, expected 42");
        }
        if (IifFailable.TryCreate(-1, out var none))
        {
            issues.Add("TryCreate(-1) returned a value");
            none.Dispose();
        }
        return true;
    }

    static bool ReverseErrorMid(List<string> issues)
    {
        var seen = -1;
        var r = Functions.IifCallThrowing((SwiftSet<SwiftString> set) => { seen = set.Count; });
        if (r != 3 || seen != 3) issues.Add($"non-throwing callback: returned {r}, callback saw {seen} members");
        if (issues.Count > 0) return true;
        var thrown = Functions.IifCallThrowing((Action<SwiftSet<SwiftString>>)(_ => throw new InvalidOperationException("requested")));
        if (thrown != -1) issues.Add($"throwing callback: returned {thrown}, expected -1");
        return true;
    }

    static bool ReverseContextLast(List<string> issues)
    {
        nint x = 0, y = 0;
        var calls = 0;
        if (!IifVisitorFactory.TryCreate(point => { calls++; x = point.XNative; y = point.YNative; }, true, out var made))
        {
            issues.Add("TryCreate(valid: true) returned nil");
            return true;
        }
        using (made)
            if (made.Number != 76) issues.Add($"Number is {made.Number}, expected 76");
        if (calls != 1 || x != 7 || y != 9) issues.Add($"callback ran {calls} time(s) and saw ({x}, {y}), expected once with (7, 9)");
        return true;
    }
}
