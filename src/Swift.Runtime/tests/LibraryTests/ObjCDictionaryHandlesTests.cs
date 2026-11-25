// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// An Objective-C dictionary read and built by the handles of its keys and values, as a binding
/// crosses a dictionary keyed by a Swift class beside a bridged value.
/// </summary>
public class ObjCDictionaryHandlesTests
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(CoreFoundation, CharSet = CharSet.Unicode)]
    private static extern IntPtr CFStringCreateWithCharacters(IntPtr allocator, string chars, nint length);

    [DllImport(CoreFoundation)]
    private static extern nint CFGetRetainCount(IntPtr value);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr value);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);

    [DllImport(CoreFoundation)]
    private static extern nint CFDictionaryGetCount(IntPtr dictionary);

    private sealed class Handle(string text) : IDisposable
    {
        public IntPtr Value { get; } = CFStringCreateWithCharacters(IntPtr.Zero, text, text.Length);

        public void Dispose() => CFRelease(Value);
    }

    [Fact]
    public void Create_HoldsEveryEntryAndRetainsWithoutCopying()
    {
        using var alpha = new Handle("alpha-key-long-enough-to-be-heap");
        using var beta = new Handle("beta-key-long-enough-to-be-heap");
        using var one = new Handle("one-value-long-enough-to-be-heap");
        using var two = new Handle("two-value-long-enough-to-be-heap");
        nint keyBaseline = CFGetRetainCount(alpha.Value);
        nint valueBaseline = CFGetRetainCount(one.Value);
        var entries = new Dictionary<Handle, Handle> { [alpha] = one, [beta] = two };

        IntPtr dictionary = ObjCDictionaryHandles.Create(entries, k => k.Value, v => v, o => o.Value);
        try
        {
            Assert.Equal(2, CFDictionaryGetCount(dictionary));
            // The dictionary holds the key objects themselves (a copy would be another object).
            Assert.Equal(one.Value, CFDictionaryGetValue(dictionary, alpha.Value));
            Assert.Equal(two.Value, CFDictionaryGetValue(dictionary, beta.Value));
            Assert.Equal(keyBaseline + 1, CFGetRetainCount(alpha.Value));
            Assert.Equal(valueBaseline + 1, CFGetRetainCount(one.Value));

            var read = ObjCDictionaryHandles.Entries(dictionary);
            Assert.Equal(2, read.Length);
            Assert.Equal(one.Value, read.Single(e => e.Key == alpha.Value).Value);
            Assert.Equal(two.Value, read.Single(e => e.Key == beta.Value).Value);
            // Reading borrows: nothing is retained.
            Assert.Equal(keyBaseline + 1, CFGetRetainCount(alpha.Value));
        }
        finally
        {
            CFRelease(dictionary);
        }
        Assert.Equal(keyBaseline, CFGetRetainCount(alpha.Value));
        Assert.Equal(valueBaseline, CFGetRetainCount(one.Value));
    }

    [Fact]
    public void Create_EmptyEntriesMakeAnEmptyDictionary()
    {
        IntPtr dictionary = ObjCDictionaryHandles.Create(new Dictionary<Handle, Handle>(), k => k.Value, v => v, o => o.Value);
        try
        {
            Assert.Equal(0, CFDictionaryGetCount(dictionary));
            Assert.Empty(ObjCDictionaryHandles.Entries(dictionary));
        }
        finally
        {
            CFRelease(dictionary);
        }
    }

    [Fact]
    public void Create_RefusesANilKeyOrValue()
    {
        using var alpha = new Handle("alpha");
        var entries = new Dictionary<Handle, Handle> { [alpha] = alpha };

        Assert.Throws<ArgumentException>(() => ObjCDictionaryHandles.Create(entries, k => IntPtr.Zero, v => v, o => o.Value));
        Assert.Throws<ArgumentException>(() => ObjCDictionaryHandles.Create(entries, k => k.Value, v => v, o => IntPtr.Zero));
        Assert.Throws<ArgumentNullException>(() => ObjCDictionaryHandles.Entries(IntPtr.Zero));
    }
}
