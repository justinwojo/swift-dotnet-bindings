// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Lane-identity runner. Runs exactly one fixture (`--iif-fixture <name>`) against the
// InterpIdentityFixture module, then prints what it saw. The positive controls call the generated
// binding; the negative controls make their direct Swift-convention transitions through
// DirectControls (see that file for why):
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
using System.Runtime.InteropServices.Swift;
using InterpIdentityFixture;

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
            "reverse-error-first" => ReverseErrorFirst,
            "reverse-context-last" => ReverseContextLast,
            _ => null,
        };
        if (body == null)
        {
            Emit($"IIF UNKNOWN {fixture}");
            return Finish(2);
        }

        DirectControls.RegisterResolver();
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
    //
    // Each makes its fixture's direct Swift-convention transition through DirectControls, never
    // through the binding, which no longer reaches Swift directly for these shapes.

    // The receiver is a non-frozen struct passed by address in the self register, and Swift
    // writes the address it received into point.y. Its first stored property sits at offset 0;
    // the arm64 interpreter's x20 defect writes 1 into the word four bytes in, which reads back
    // as the tag plus 2^32. Receiver and point are native memory the app reads directly, so the
    // measurement makes no further transition.
    const nint Tag = 5;

    static void StampOnce(nint* slot, int call, List<string> issues, bool throwing = false, bool fail = false)
    {
        var point = stackalloc nint[2];
        point[0] = 0;
        point[1] = 0;
        var handle = (nint)slot;
        var error = default(SwiftError);
        if (throwing) DirectControls.StampWithError((IntPtr)point, fail ? (byte)1 : (byte)0, new SwiftSelf(slot), &error);
        else DirectControls.StampReceiver((IntPtr)point, new SwiftSelf(slot));

        if (slot[0] != Tag)
        {
            issues.Add($"call {call}: receiver word 0 changed from 0x{Tag:x} to 0x{slot[0]:x}");
            // Restore it so each later call is measured from the same state.
            slot[0] = Tag;
        }
        if (point[1] != handle)
            issues.Add($"call {call}: Swift saw self 0x{point[1]:x}, expected 0x{handle:x}");
        var expectedX = fail ? 0 : 1;
        if (point[0] != expectedX)
            issues.Add($"call {call}: point.x is {point[0]}, expected {expectedX}");
        if (throwing)
        {
            // A thrown error is the app's to release; one leaked object in a one-fixture process is harmless.
            var thrown = error.Value != null;
            Observe($"call {call}: fail={fail} thrown={(thrown ? $"0x{(nint)error.Value:x}" : "none")}");
            if (fail && !thrown) issues.Add($"call {call}: the Swift error did not surface");
            if (!fail && thrown) issues.Add($"call {call}: an error surfaced from a call that does not throw");
        }
    }

    static nint* NewSlot()
    {
        // IifSlot is two Ints: tag, then spare.
        var slot = (nint*)NativeMemory.AllocZeroed(2, (nuint)sizeof(nint));
        slot[0] = Tag;
        return slot;
    }

    static bool SelfReceiver(List<string> issues)
    {
        var slot = NewSlot();
        Observe($"receiver=0x{(nint)slot:x} word0=0x{slot[0]:x}");
        for (var call = 0; call < 4; call++)
            StampOnce(slot, call, issues);
        NativeMemory.Free(slot);
        return true;
    }

    static bool SelfError(List<string> issues)
    {
        var slot = NewSlot();
        Observe($"receiver=0x{(nint)slot:x} word0=0x{slot[0]:x}");
        StampOnce(slot, 0, issues, throwing: true, fail: false);
        StampOnce(slot, 1, issues, throwing: true, fail: true);
        NativeMemory.Free(slot);
        return true;
    }

    // An Optional of a one-Int32 struct: the Int32, then a tag byte (0 a value, 1 nil). The buffer
    // is filled with a pattern first, so a result Swift never wrote reads back as neither.
    const byte Unwritten = 0xA5;

    static (bool Written, bool HasValue, int Value) ReadOptionalInt32(byte* buffer)
    {
        var tag = buffer[4];
        return (tag is 0 or 1, tag == 0, *(int*)buffer);
    }

    static bool IndirectResult(List<string> issues)
    {
        var buffer = stackalloc byte[16];
        new Span<byte>(buffer, 16).Fill(Unwritten);
        DirectControls.CreateFailable(new SwiftIndirectResult(buffer), 21);
        var made = ReadOptionalInt32(buffer);
        Observe($"init?(value: 21) tag=0x{buffer[4]:x2} payload={made.Value}");
        if (!made.Written) issues.Add($"init?(value: 21) left the result unwritten (tag 0x{buffer[4]:x2})");
        else if (!made.HasValue) issues.Add("init?(value: 21) returned nil");
        else if (made.Value != 42) issues.Add($"init?(value: 21).value is {made.Value}, expected 42");

        new Span<byte>(buffer, 16).Fill(Unwritten);
        DirectControls.CreateFailable(new SwiftIndirectResult(buffer), -1);
        var none = ReadOptionalInt32(buffer);
        Observe($"init?(value: -1) tag=0x{buffer[4]:x2}");
        if (!none.Written) issues.Add($"init?(value: -1) left the result unwritten (tag 0x{buffer[4]:x2})");
        else if (none.HasValue) issues.Add("init?(value: -1) returned a value");
        return true;
    }

    static bool ReverseErrorFirst(List<string> issues)
    {
        DirectControls.Throw = false;
        var r = DirectControls.CallThrowing(DirectControls.ThrowingSetCallbackFunction, IntPtr.Zero);
        Observe($"non-throwing: returned {r}, calls {DirectControls.Calls}, set count {DirectControls.SetCount}, context 0x{DirectControls.ContextSeen:x}");
        if (r != 3 || DirectControls.Calls != 1 || DirectControls.SetCount != 3 || DirectControls.ContextSeen != 0)
            issues.Add($"non-throwing callback: returned {r}, callback ran {DirectControls.Calls} time(s), saw {DirectControls.SetCount} members and context 0x{DirectControls.ContextSeen:x}");
        if (issues.Count > 0) return true;
        DirectControls.Throw = true;
        var thrown = DirectControls.CallThrowing(DirectControls.ThrowingSetCallbackFunction, IntPtr.Zero);
        Observe($"throwing: returned {thrown}, calls {DirectControls.Calls}");
        if (thrown != -1 || DirectControls.Calls != 2) issues.Add($"throwing callback: returned {thrown} after {DirectControls.Calls} call(s), expected -1 after 2");
        return true;
    }

    static bool ReverseContextLast(List<string> issues)
    {
        var buffer = stackalloc byte[16];
        new Span<byte>(buffer, 16).Fill(Unwritten);
        DirectControls.CreateVisitorFactory(new SwiftIndirectResult(buffer), DirectControls.PointVisitorCallbackFunction, IntPtr.Zero, 1);
        var made = ReadOptionalInt32(buffer);
        Observe($"init?(valid: true) tag=0x{buffer[4]:x2} number={made.Value} calls={DirectControls.Calls} point=({DirectControls.PointX}, {DirectControls.PointY}) context=0x{DirectControls.ContextSeen:x}");
        if (!made.Written) issues.Add($"init?(valid: true) left the result unwritten (tag 0x{buffer[4]:x2})");
        else if (!made.HasValue) issues.Add("init?(valid: true) returned nil");
        else if (made.Value != 76) issues.Add($"number is {made.Value}, expected 76");
        if (DirectControls.Calls != 1 || DirectControls.PointX != 7 || DirectControls.PointY != 9 || DirectControls.ContextSeen != 0)
            issues.Add($"callback ran {DirectControls.Calls} time(s) and saw ({DirectControls.PointX}, {DirectControls.PointY}) with context 0x{DirectControls.ContextSeen:x}, expected once with (7, 9) and a null context");
        return true;
    }
}
