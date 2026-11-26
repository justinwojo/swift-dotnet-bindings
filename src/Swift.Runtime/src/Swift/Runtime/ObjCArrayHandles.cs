// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Reads and builds an Objective-C array by the raw handles of its elements.
/// </summary>
/// <remarks>
/// A Swift array of Swift class instances (<c>[Node]</c>) held inside a collection that bridges to
/// Objective-C (the value of a <c>[URL: [Node]]</c>) crosses as an <c>NSArray</c> whose elements
/// are the Swift objects themselves. A class that does not derive from <c>NSObject</c> has no
/// <c>NSObject</c> peer to hand a .NET <c>NSArray</c>, so a binding reads such an array's elements by
/// handle and converts each through the class's own marshalling, and builds one from the elements'
/// handles.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ObjCArrayHandles
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(CoreFoundation)]
    private static extern nint CFArrayGetCount(IntPtr array);

    [DllImport(CoreFoundation)]
    private static extern unsafe void CFArrayGetValues(IntPtr array, CFRange range, IntPtr* values);

    [DllImport(CoreFoundation)]
    private static extern unsafe IntPtr CFArrayCreate(IntPtr allocator, IntPtr* values, nint count, IntPtr callBacks);

    [StructLayout(LayoutKind.Sequential)]
    private struct CFRange
    {
        public nint Location;
        public nint Length;
    }

    private static readonly Lazy<IntPtr> s_typeCallBacks = new(() =>
        NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFTypeArrayCallBacks"));

    /// <summary>
    /// The handle of every element of the Objective-C array <paramref name="array"/>, in order and
    /// borrowed from it: they stay valid while the array lives.
    /// </summary>
    public static unsafe IntPtr[] Elements(IntPtr array)
    {
        if (array == IntPtr.Zero)
            throw new ArgumentNullException(nameof(array));
        int count = checked((int)CFArrayGetCount(array));
        if (count == 0)
            return Array.Empty<IntPtr>();
        var elements = new IntPtr[count];
        fixed (IntPtr* pointer = elements)
            CFArrayGetValues(array, new CFRange { Location = 0, Length = count }, pointer);
        return elements;
    }

    /// <summary>
    /// A new Objective-C array, returned at +1, holding the object of every element of
    /// <paramref name="elements"/> in order, each read by <paramref name="handle"/>. The array
    /// retains every element.
    /// </summary>
    /// <param name="elements">The elements, enumerated once. They stay alive for the call.</param>
    /// <param name="handle">The handle of an element's object (an <c>NSNull</c> for a nil element).</param>
    public static unsafe IntPtr Create<T>(IEnumerable<T> elements, Func<T, IntPtr> handle)
    {
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(handle);
        var items = elements.ToArray();
        var handles = new IntPtr[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            handles[i] = handle(items[i]);
            if (handles[i] == IntPtr.Zero)
                throw new ArgumentException("An Objective-C array holds no nil element.", nameof(elements));
        }
        IntPtr array;
        fixed (IntPtr* pointer = handles)
            array = CFArrayCreate(IntPtr.Zero, pointer, handles.Length, s_typeCallBacks.Value);
        // The handles belong to the elements; they stay reachable until the array has retained them.
        GC.KeepAlive(items);
        if (array == IntPtr.Zero)
            throw new SwiftRuntimeException("CFArrayCreate returned no array.");
        return array;
    }
}
