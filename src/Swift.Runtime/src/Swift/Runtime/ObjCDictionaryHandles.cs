// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Reads and builds an Objective-C dictionary by the raw handles of its keys and values.
/// </summary>
/// <remarks>
/// A Swift dictionary whose value bridges to Objective-C (<c>[Node: URL]</c>) crosses as the
/// <c>NSDictionary</c> it bridges to. When its key is a Swift class that does not derive from
/// <c>NSObject</c>, each key inside that dictionary is the Swift object itself, which has no
/// <c>NSObject</c> peer to hand a .NET <c>NSDictionary</c>. A binding reads such a dictionary's
/// entries by handle and converts each key through the class's own marshalling, and builds one
/// from the keys' handles. A Swift class value (<c>[URL: Node]</c>) is likewise the object itself
/// and crosses the same way. The dictionary it builds retains its keys and values and does not copy
/// its keys (a Swift object is not <c>NSCopying</c>); Swift compares the keys of the dictionary it
/// bridges back by the class's own <c>Hashable</c> conformance.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ObjCDictionaryHandles
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(CoreFoundation)]
    private static extern nint CFDictionaryGetCount(IntPtr dictionary);

    [DllImport(CoreFoundation)]
    private static extern unsafe void CFDictionaryGetKeysAndValues(IntPtr dictionary, IntPtr* keys, IntPtr* values);

    [DllImport(CoreFoundation)]
    private static extern unsafe IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr* keys, IntPtr* values, nint count, IntPtr keyCallBacks, IntPtr valueCallBacks);

    private static readonly Lazy<(IntPtr Key, IntPtr Value)> s_typeCallBacks = new(() =>
    {
        var library = NativeLibrary.Load(CoreFoundation);
        return (NativeLibrary.GetExport(library, "kCFTypeDictionaryKeyCallBacks"),
                NativeLibrary.GetExport(library, "kCFTypeDictionaryValueCallBacks"));
    });

    /// <summary>
    /// The key and value handles of every entry of the Objective-C dictionary
    /// <paramref name="dictionary"/>, borrowed from it: they stay valid while the dictionary lives.
    /// </summary>
    public static unsafe KeyValuePair<IntPtr, IntPtr>[] Entries(IntPtr dictionary)
    {
        if (dictionary == IntPtr.Zero)
            throw new ArgumentNullException(nameof(dictionary));
        int count = checked((int)CFDictionaryGetCount(dictionary));
        if (count == 0)
            return Array.Empty<KeyValuePair<IntPtr, IntPtr>>();
        var keys = new IntPtr[count];
        var values = new IntPtr[count];
        fixed (IntPtr* keysPointer = keys)
        fixed (IntPtr* valuesPointer = values)
            CFDictionaryGetKeysAndValues(dictionary, keysPointer, valuesPointer);
        var entries = new KeyValuePair<IntPtr, IntPtr>[count];
        for (int i = 0; i < count; i++)
            entries[i] = new KeyValuePair<IntPtr, IntPtr>(keys[i], values[i]);
        return entries;
    }

    /// <summary>
    /// A new Objective-C dictionary, returned at +1, holding every entry of
    /// <paramref name="entries"/>: each key's object, from <paramref name="keyHandle"/>, maps to the
    /// object <paramref name="valueObject"/> makes of its value, whose handle
    /// <paramref name="valueHandle"/> reads. The dictionary retains every key and value; it copies none.
    /// </summary>
    /// <param name="entries">The entries, enumerated once. Their keys stay alive for the call.</param>
    /// <param name="keyHandle">The Objective-C object of a key (an <c>NSNull</c> for a nil key).</param>
    /// <param name="valueObject">The Objective-C object a value crosses as, kept alive for the call.</param>
    /// <param name="valueHandle">The handle of a value's object.</param>
    public static IntPtr Create<TKey, TValue, TObject>(
        IEnumerable<KeyValuePair<TKey, TValue>> entries,
        Func<TKey, IntPtr> keyHandle,
        Func<TValue, TObject> valueObject,
        Func<TObject, IntPtr> valueHandle)
    {
        ArgumentNullException.ThrowIfNull(keyHandle);
        return Create(entries, static key => key, keyHandle, valueObject, valueHandle);
    }

    /// <summary>
    /// A new Objective-C dictionary, returned at +1, holding every entry of
    /// <paramref name="entries"/>: the object <paramref name="keyObject"/> makes of each key, whose
    /// handle <paramref name="keyHandle"/> reads, maps to the object <paramref name="valueObject"/>
    /// makes of its value, whose handle <paramref name="valueHandle"/> reads. A Swift object key or
    /// value is its own object. The dictionary retains every key and value; it copies none.
    /// </summary>
    /// <param name="entries">The entries, enumerated once. Their keys and values stay alive for the call.</param>
    /// <param name="keyObject">The object a key crosses as, kept alive for the call.</param>
    /// <param name="keyHandle">The handle of a key's object (an <c>NSNull</c> for a nil key).</param>
    /// <param name="valueObject">The object a value crosses as, kept alive for the call.</param>
    /// <param name="valueHandle">The handle of a value's object (an <c>NSNull</c> for a nil value).</param>
    public static unsafe IntPtr Create<TKey, TValue, TKeyObject, TValueObject>(
        IEnumerable<KeyValuePair<TKey, TValue>> entries,
        Func<TKey, TKeyObject> keyObject,
        Func<TKeyObject, IntPtr> keyHandle,
        Func<TValue, TValueObject> valueObject,
        Func<TValueObject, IntPtr> valueHandle)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(keyObject);
        ArgumentNullException.ThrowIfNull(keyHandle);
        ArgumentNullException.ThrowIfNull(valueObject);
        ArgumentNullException.ThrowIfNull(valueHandle);
        var pairs = entries.ToArray();
        var keys = new TKeyObject[pairs.Length];
        var keyHandles = new IntPtr[pairs.Length];
        var values = new TValueObject[pairs.Length];
        var valueHandles = new IntPtr[pairs.Length];
        for (int i = 0; i < pairs.Length; i++)
        {
            keys[i] = keyObject(pairs[i].Key);
            keyHandles[i] = keyHandle(keys[i]);
            values[i] = valueObject(pairs[i].Value);
            valueHandles[i] = valueHandle(values[i]);
            if (keyHandles[i] == IntPtr.Zero || valueHandles[i] == IntPtr.Zero)
                throw new ArgumentException("An Objective-C dictionary holds no nil key or value.", nameof(entries));
        }
        var (keyCallBacks, valueCallBacks) = s_typeCallBacks.Value;
        IntPtr dictionary;
        fixed (IntPtr* keysPointer = keyHandles)
        fixed (IntPtr* valuesPointer = valueHandles)
            dictionary = CFDictionaryCreate(IntPtr.Zero, keysPointer, valuesPointer, keyHandles.Length, keyCallBacks, valueCallBacks);
        // The handles belong to the key and value objects; they stay reachable until the
        // dictionary has retained them.
        GC.KeepAlive(pairs);
        GC.KeepAlive(keys);
        GC.KeepAlive(values);
        if (dictionary == IntPtr.Zero)
            throw new SwiftRuntimeException("CFDictionaryCreate returned no dictionary.");
        return dictionary;
    }
}
