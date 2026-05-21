// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Hand-rolled partial that layers a user-facing public surface on top of
// the generated Foundation.AttributedString shell. The generator emits
// the storage + ISwiftObject plumbing (NewFromPayload, MarshalToSwift,
// the value-witness-based heap copy) but cannot synthesise:
//
//   * any public initializer — AttributedString's Swift ctors take a
//     Swift String or Sequence<Character>, neither of which has a
//     blittable @_cdecl signature the generator can name;
//   * the @dynamicMemberLookup subscript that exposes attribute keys
//     (languageIdentifier, link, foregroundColor, ...) — that subscript
//     is a generic key-path overload with no flat symbol.
//
// The Apple Supplement framework (SwiftBindingsAppleSupplement.xcframework,
// built by `nuke build-apple-supplement-xcframework`) carries the
// SBW_AttributedString_* @_cdecl shims that translate those operations
// into a calling-convention this partial can speak via [LibraryImport].
// LanguageIdentifier is the canonical example here; subsequent attribute
// properties (link, foregroundColor, font, ...) follow the same shape.
//
// Storage ownership: the C# SwiftSafeHandle is the sole owner of the
// AttributedString's heap slot, so AttributedString's internal COW
// counters see a refcount of 1 and mutations apply in place. Mutating
// shims (Set*) are therefore safe to call against the live payload
// without copy-then-replace dance.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Swift.Runtime;

namespace Swift.Foundation;

/// <summary>
/// User-facing surface for <see cref="Swift.Foundation.AttributedString"/>.
/// The companion auto-generated partial provides the storage and the
/// ISwiftObject plumbing; this hand-rolled partial adds the public string
/// constructor and the @dynamicMemberLookup attribute properties that
/// the binding generator cannot synthesise on its own.
/// </summary>
public sealed partial class AttributedString
{
    /// <summary>
    /// Constructs a Foundation.AttributedString from a managed UTF-16
    /// string. The text is encoded to UTF-8, written into a freshly
    /// allocated heap slot sized by the AttributedString value-witness
    /// table, and the SBW_AttributedString_InitFromUtf8 shim runs
    /// AttributedString(_ string: String) into that slot. The resulting
    /// payload is wrapped in a SwiftSafeHandle that frees the heap copy
    /// on Dispose.
    /// </summary>
    /// <param name="text">Plain-text contents of the AttributedString.
    /// A null reference is treated as the empty string — Foundation has
    /// no concept of a nil-text AttributedString.</param>
    public unsafe AttributedString(string text)
    {
        var metadata = _cachedMetadata ??= PInvoke_GetMetadata();
        var size = (nuint)metadata.Size;
        var heapCopy = NativeMemory.Alloc(size);
        bool initialized = false;
        try
        {
            var utf8 = text is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(text);
            fixed (byte* utf8Ptr = utf8)
            {
                SupplementNative.InitFromUtf8(utf8Ptr, utf8.Length, heapCopy);
            }
            initialized = true;
        }
        catch
        {
            if (initialized)
                metadata.ValueWitnessTable->Destroy(heapCopy, metadata);
            NativeMemory.Free(heapCopy);
            throw;
        }
        _payload = new SwiftSafeHandle<AttributedString>((IntPtr)heapCopy);
    }

    /// <summary>
    /// Returns the plain-text characters of this AttributedString,
    /// dropping all attribute runs. Equivalent to
    /// <c>String(attrStr.characters)</c> on the Swift side.
    /// </summary>
    public override string ToString()
    {
        ThrowIfDisposed();
        bool addedRef = false;
        _payload.DangerousAddRef(ref addedRef);
        try
        {
            unsafe
            {
                byte* utf8Ptr = null;
                nint utf8Len = 0;
                SupplementNative.GetCharacters(
                    (void*)_payload.DangerousGetHandle(), &utf8Ptr, &utf8Len);
                if (utf8Ptr == null || utf8Len <= 0)
                    return string.Empty;
                try
                {
                    return Encoding.UTF8.GetString(utf8Ptr, (int)utf8Len);
                }
                finally
                {
                    SupplementNative.FreeBuffer(utf8Ptr);
                }
            }
        }
        finally
        {
            if (addedRef) _payload.DangerousRelease();
        }
    }

    /// <summary>
    /// The value of the <c>languageIdentifier</c> attribute applied
    /// uniformly across this entire AttributedString, or <c>null</c> if
    /// the attribute is absent or applied non-uniformly. Setting to
    /// <c>null</c> removes the attribute; setting to a non-null string
    /// applies that identifier across the whole range. Backed by Swift
    /// AttributedString's @dynamicMemberLookup keyed on
    /// <c>AttributeScopes.FoundationAttributes.LanguageIdentifierAttribute</c>.
    /// </summary>
    public string? LanguageIdentifier
    {
        get
        {
            ThrowIfDisposed();
            bool addedRef = false;
            _payload.DangerousAddRef(ref addedRef);
            try
            {
                unsafe
                {
                    byte* utf8Ptr = null;
                    nint utf8Len = 0;
                    var present = SupplementNative.GetLanguageIdentifier(
                        (void*)_payload.DangerousGetHandle(), &utf8Ptr, &utf8Len);
                    if (present == 0) return null;
                    try
                    {
                        if (utf8Ptr == null || utf8Len <= 0) return string.Empty;
                        return Encoding.UTF8.GetString(utf8Ptr, (int)utf8Len);
                    }
                    finally
                    {
                        if (utf8Ptr != null) SupplementNative.FreeBuffer(utf8Ptr);
                    }
                }
            }
            finally
            {
                if (addedRef) _payload.DangerousRelease();
            }
        }
        set
        {
            ThrowIfDisposed();
            bool addedRef = false;
            _payload.DangerousAddRef(ref addedRef);
            try
            {
                unsafe
                {
                    if (value is null)
                    {
                        SupplementNative.SetLanguageIdentifier(
                            (void*)_payload.DangerousGetHandle(), null, 0, hasValue: 0);
                        return;
                    }
                    var utf8 = Encoding.UTF8.GetBytes(value);
                    fixed (byte* utf8Ptr = utf8)
                    {
                        SupplementNative.SetLanguageIdentifier(
                            (void*)_payload.DangerousGetHandle(),
                            utf8Ptr, utf8.Length, hasValue: 1);
                    }
                }
            }
            finally
            {
                if (addedRef) _payload.DangerousRelease();
            }
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>
    /// P/Invoke surface for the Apple Supplement shim framework.
    /// Cdecl + raw void* — none of these symbols traffic in
    /// CallConvSwift, so Mono and NativeAOT take identical fast paths
    /// through them. The framework ships in this NuGet at
    /// <c>runtimes/native/SwiftBindingsAppleSupplement.xcframework/</c>
    /// and is resolved at LoadLibrary time by SwiftFrameworkResolver's
    /// <c>@rpath/{name}.framework/{name}</c> rule.
    /// </summary>
    private static partial class SupplementNative
    {
        private const string Library = "SwiftBindingsAppleSupplement";

        [LibraryImport(Library, EntryPoint = "SBW_AttributedString_InitFromUtf8")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static unsafe partial void InitFromUtf8(byte* utf8Ptr, nint utf8Len, void* outBuffer);

        [LibraryImport(Library, EntryPoint = "SBW_AttributedString_GetCharacters")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static unsafe partial void GetCharacters(void* astrPtr, byte** outUtf8Ptr, nint* outUtf8Len);

        [LibraryImport(Library, EntryPoint = "SBW_AttributedString_FreeBuffer")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static unsafe partial void FreeBuffer(byte* ptr);

        [LibraryImport(Library, EntryPoint = "SBW_AttributedString_GetLanguageIdentifier")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static unsafe partial nint GetLanguageIdentifier(void* astrPtr, byte** outUtf8Ptr, nint* outUtf8Len);

        [LibraryImport(Library, EntryPoint = "SBW_AttributedString_SetLanguageIdentifier")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static unsafe partial void SetLanguageIdentifier(void* astrPtr, byte* utf8Ptr, nint utf8Len, nint hasValue);
    }
}
