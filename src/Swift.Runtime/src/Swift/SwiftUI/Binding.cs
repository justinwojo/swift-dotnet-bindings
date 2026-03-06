// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SwiftUI;

/// <summary>
/// Represents SwiftUI.Binding - a property wrapper for two-way data binding.
/// This is a lightweight borrowed-handle stub for bridge file compilation.
/// Binding is generic in Swift (Binding&lt;Value&gt;) but projected as a non-generic
/// borrowed handle since the C# side cannot know the generic argument at runtime.
/// </summary>
public sealed class Binding : IDisposable
{
    private readonly BorrowedHandle _payload;

    /// <summary>
    /// Gets the internal handle for marshalling.
    /// </summary>
    public SafeHandle Payload => _payload;

    /// <summary>
    /// Creates a new Binding wrapping the given native handle.
    /// The handle is borrowed (not owned) — disposal is a no-op.
    /// </summary>
    public Binding(IntPtr handle)
    {
        _payload = new BorrowedHandle(handle);
    }

    /// <summary>
    /// Disposes the binding handle wrapper.
    /// </summary>
    public void Dispose() => _payload.Dispose();

    private sealed class BorrowedHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public BorrowedHandle(IntPtr h) : base(false) => SetHandle(h);
        protected override bool ReleaseHandle() => true;
    }
}
