// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// The negative controls' transitions, written by hand. Plain DllImport, since the app disables
// runtime marshalling: every parameter is blittable, Bool travels as one byte, and the error
// register is a SwiftError*.
//
// The generated binding no longer reaches Swift on a direct Swift-convention transition for any of
// these shapes: it routes them through native bridges, or guards what it cannot route so the
// interpreter refuses before Swift runs. That keeps every binding consumer off the interpreter's
// defects, which leaves the harness without a call that still exercises them. These declarations
// are that call: each mirrors, member for member, the direct transition the generator used to
// emit, against the fixture's own Swift symbols. They are what the controls measure, so they stay
// direct on purpose; the harness scans this assembly to prove it.
//
// Layout facts the generated code read through value witnesses are fixed here instead, because
// an unmanaged calli is a method crossgen2 leaves to the runtime, and a ReadyToRun cell must make
// these calls from precompiled code. Each is the fixture's own: IifSlot and IifPoint are two Ints,
// and an Optional of a single Int32 struct (IifFailable, IifVisitorFactory) is the Int32 followed
// by one tag byte, 0 for a value and 1 for nil.

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;

static unsafe class DirectControls
{
    const string Fixture = "InterpIdentityFixture";
    const string Wrapper = "InterpIdentityFixtureSwiftBindings";

    // The binding's module initializer resolves its own imports; this assembly's need the same
    // frameworks from the app bundle.
    public static void RegisterResolver() =>
        NativeLibrary.SetDllImportResolver(typeof(DirectControls).Assembly, (name, _, _) =>
            name is Fixture or Wrapper
                ? NativeLibrary.Load(Path.Combine(Foundation.NSBundle.MainBundle.PrivateFrameworksPath!, $"{name}.framework", name))
                : IntPtr.Zero);

    // IifSlot.stamp(_: inout IifPoint): the point by address, the receiver by address in the self register.
    [DllImport(Fixture, EntryPoint = "$s21InterpIdentityFixture7IifSlotV5stampyyAA0D5PointVzF")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
    public static extern void StampReceiver(IntPtr point, SwiftSelf self);

    // IifSlot.stampThrowing(_: inout IifPoint, fail: Bool) throws: adds the error register.
    [DllImport(Fixture, EntryPoint = "$s21InterpIdentityFixture7IifSlotV13stampThrowing_4failyAA0D5PointVz_SbtKF")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
    public static extern void StampWithError(IntPtr point, byte fail, SwiftSelf self, SwiftError* error);

    // IifFailable.init?(value: Int32): the Optional comes back through the indirect-result register.
    [DllImport(Fixture, EntryPoint = "$s21InterpIdentityFixture11IifFailableV5valueACSgs5Int32V_tcfC")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
    public static extern void CreateFailable(SwiftIndirectResult result, int value);

    // iifCallThrowing(_: @escaping (Set<String>) throws -> Void) -> Int32: the closure is its
    // function and context. A null context is a valid thick closure, and Swift's retain and
    // release of it do nothing.
    [DllImport(Fixture, EntryPoint = "$s21InterpIdentityFixture15iifCallThrowingys5Int32VyShySSGKcF")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
    public static extern int CallThrowing(IntPtr function, IntPtr context);

    // IifVisitorFactory.init?(_: (IifPoint) -> Void, valid: Bool): the indirect result, the
    // non-escaping closure and the flag.
    [DllImport(Fixture, EntryPoint = "$s21InterpIdentityFixture17IifVisitorFactoryV_5validACSgyAA0D5PointVXE_SbtcfC")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
    public static extern void CreateVisitorFactory(SwiftIndirectResult result, IntPtr body, IntPtr context, byte valid);

    // The wrapper's error factory (C convention), which the generated callbacks use to throw: two
    // NUL-terminated UTF-8 strings in, a retained error out.
    [DllImport(Wrapper, EntryPoint = "SBW_CreateError_InterpIdentityFixture")]
    public static extern IntPtr CreateError(byte* message, byte* managedTypeName);

    // MARK: - Reverse callbacks

    /// <summary>What the last callback received.</summary>
    public static int Calls;
    public static nint SetCount = -1;
    public static nint ContextSeen = -1;
    public static nint PointX, PointY;
    public static bool Throw;

    // The (Set<String>) throws -> Void closure's function: the set's storage, the context in the
    // self register, the error register. The count is the storage's first stored property, the
    // word after the object header.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
    static void ThrowingSetCallback(SwiftError* error, void* set, SwiftSelf context)
    {
        Calls++;
        ContextSeen = (nint)context.Value;
        SetCount = set == null ? -1 : *(nint*)((byte*)set + 2 * sizeof(nint));
        if (!Throw)
        {
            *error = default;
            return;
        }
        fixed (byte* message = "requested"u8)
            *error = new SwiftError((void*)CreateError(message, null));
    }

    public static IntPtr ThrowingSetCallbackFunction =>
        (IntPtr)(delegate* unmanaged[Swift]<SwiftError*, void*, SwiftSelf, void>)&ThrowingSetCallback;

    // The (IifPoint) -> Void closure's function: the point by address, the context in the self register.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
    static void PointVisitorCallback(void* point, SwiftSelf context)
    {
        Calls++;
        ContextSeen = (nint)context.Value;
        PointX = ((nint*)point)[0];
        PointY = ((nint*)point)[1];
    }

    public static IntPtr PointVisitorCallbackFunction =>
        (IntPtr)(delegate* unmanaged[Swift]<void*, SwiftSelf, void>)&PointVisitorCallback;
}
