// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;
using System.Text;
using Swift.Runtime;
using Swift.Runtime.InteropServices;

namespace Swift;

/// <summary>
/// Represents a Swift string with Foundation.Data payload.
/// </summary>
public class SwiftString : ISwiftObject, IDisposable
{
    private static nuint _payloadSize = SwiftObjectHelper<SwiftString>.GetTypeMetadata().Size;

    public struct Buffer
    {
#pragma warning disable CS0169
        private Data _data;
#pragma warning restore CS0169
    }

    private SwiftSafeHandle<SwiftString> _payload;

    public SwiftSafeHandle<SwiftString> Payload => _payload;

    private static Dictionary<Type, string> _protocolConformanceSymbols;

    /// <summary>
    /// When true, ToString()/Length use the SwiftBindingsRuntime wrapper path
    /// (avoids Mono JIT CallConvSwift assertion). Falls back to false if the
    /// runtime library is not deployed, after which direct P/Invoke is used.
    /// On Mono, fallback to direct CallConvSwift is not allowed (process-fatal).
    /// </summary>
    private static bool _useWrapperPath = true;

    /// <summary>
    /// True when running on the Mono runtime (iOS). On Mono, the direct
    /// CallConvSwift P/Invoke path triggers a process-fatal JIT assertion,
    /// so fallback is not safe — we must throw instead.
    /// </summary>
    private static readonly bool _isMonoRuntime = Type.GetType("Mono.Runtime") != null;

    static SwiftString()
    {
        _protocolConformanceSymbols = new Dictionary<Type, string> { };
    }

    public unsafe PayloadBuffer<SwiftString.Buffer> PayloadBuffer => new PayloadBuffer<SwiftString.Buffer>(_payload);

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        return TypeMetadata.Cache.GetOrAdd(typeof(SwiftString), _ => PInvoke_getMetadata());
    }

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        return new SwiftString(handle);
    }

    int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        var metadata = SwiftObjectHelper<SwiftString>.GetTypeMetadata();
        if ((int)metadata.Size > swiftDestSpan.Length)
        {
            throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
        }
        unsafe
        {
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure the payload is valid before making copy
                bool success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    metadata.ValueWitnessTable->InitializeWithCopy(swiftDest, (void*)_payload.DangerousGetHandle(), metadata);
                    return (int)metadata.Size;
                }
                finally
                {
                    if (success)
                        _payload.DangerousRelease();
                }
            }
        }
    }

    /// <summary>
    /// Gets the protocol conformance descriptor for the given type.
    /// </summary>
    /// <typeparam name="TProtocol"></typeparam>
    /// <returns></returns>
    static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
        where TProtocol : class
    {
        if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
        {
            throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type SwiftString and protocol {typeof(TProtocol).Name}, but no conformance was found.");
        }
        return ProtocolConformanceDescriptor.LoadFromSymbol("/usr/lib/swift/libswiftCore.dylib", symbolName);
    }

    /// <summary>
    /// Constructs a new SwiftString from the given handle.
    /// </summary>
    unsafe SwiftString(IntPtr handle)
    {
        IntPtr bufferPtr = (IntPtr)NativeMemory.Alloc((nuint)sizeof(SwiftString.Buffer));
        *(SwiftString.Buffer*)bufferPtr = *(SwiftString.Buffer*)handle;
        _payload = new SwiftSafeHandle<SwiftString>(bufferPtr);
    }

    /// <summary>
    /// Constructs a new SwiftString from the C# string.
    /// </summary>
    public SwiftString(string str)
    {
        byte[] utf8Bytes = Encoding.UTF8.GetBytes(str);
        unsafe
        {
            IntPtr bufferPtr = (IntPtr)NativeMemory.Alloc((nuint)sizeof(SwiftString.Buffer));

            if (_useWrapperPath)
            {
                try
                {
                    fixed (byte* utf8BytesPtr = utf8Bytes)
                    {
                        RuntimeNativeMethods.SwiftString_Create(
                            (IntPtr)utf8BytesPtr, utf8Bytes.Length, bufferPtr);
                    }
                    _payload = new SwiftSafeHandle<SwiftString>(bufferPtr);
                    return;
                }
                catch (DllNotFoundException) { _useWrapperPath = false; }
                catch (EntryPointNotFoundException) { _useWrapperPath = false; }
            }

            if (_isMonoRuntime)
            {
                NativeMemory.Free((void*)bufferPtr);
                ThrowMissingWrapperOnMono();
            }

            fixed (byte* utf8BytesPtr = utf8Bytes)
            {
                var result = PInvoke_Create(utf8BytesPtr, utf8Bytes.Length, 1);
                *(SwiftString.Buffer*)bufferPtr = result;
                _payload = new SwiftSafeHandle<SwiftString>(bufferPtr);
            }
        }
    }

    /// <summary>
    /// Gets the length of string.
    /// </summary>
    public int Length
    {
        get
        {
            if (_useWrapperPath)
            {
                try
                {
                    return GetLengthViaWrapper();
                }
                catch (DllNotFoundException) { _useWrapperPath = false; }
                catch (EntryPointNotFoundException) { _useWrapperPath = false; }
            }

            if (_isMonoRuntime)
                ThrowMissingWrapperOnMono();

            using PayloadBuffer<SwiftString.Buffer> disposable = PayloadBuffer;
            return (int)PInvoke_GetLength(disposable.Buffer);
        }
    }

    private unsafe int GetLengthViaWrapper()
    {
        bool success = false;
        _payload.DangerousAddRef(ref success);
        try
        {
            return (int)RuntimeNativeMethods.SwiftString_GetCount(_payload.DangerousGetHandle());
        }
        finally
        {
            if (success)
                _payload.DangerousRelease();
        }
    }

    /// <summary>
    /// Converts the SwiftString to a C# string.
    /// </summary>
    public override string ToString()
    {
        if (_useWrapperPath)
        {
            try
            {
                return ToStringViaWrapper();
            }
            catch (DllNotFoundException) { _useWrapperPath = false; }
            catch (EntryPointNotFoundException) { _useWrapperPath = false; }
        }

        if (_isMonoRuntime)
            ThrowMissingWrapperOnMono();

        return ToStringDirect();
    }

    private unsafe string ToStringViaWrapper()
    {
        bool success = false;
        _payload.DangerousAddRef(ref success);
        try
        {
            RuntimeNativeMethods.SwiftString_ToUtf8(
                _payload.DangerousGetHandle(), out var utf8Ptr, out var utf8Len);

            if (utf8Ptr == IntPtr.Zero || utf8Len <= 0)
                return string.Empty;

            try
            {
                return Encoding.UTF8.GetString((byte*)utf8Ptr, (int)utf8Len);
            }
            finally
            {
                RuntimeNativeMethods.SwiftString_FreeUtf8(utf8Ptr);
            }
        }
        finally
        {
            if (success)
                _payload.DangerousRelease();
        }
    }

    private static void ThrowMissingWrapperOnMono()
    {
        throw new SwiftRuntimeException(
            "SwiftString operations require the SwiftBindingsRuntime native library on Mono. " +
            "The direct CallConvSwift P/Invoke path triggers a process-fatal JIT assertion on Mono. " +
            "Ensure libSwiftBindingsRuntime.dylib is included in your application bundle.");
    }

    private string ToStringDirect()
    {
        var elementType = TypeMetadata.GetTypeMetadataOrThrow<byte>();
        var resultType = TypeMetadata.GetTypeMetadataOrThrow<long>();

        using PayloadBuffer<SwiftString.Buffer> disposable = PayloadBuffer;
        var length = Length;
        if (length <= 0)
            return string.Empty;

        var contiguousArray = PInvoke_GetUtf8ContiguousArray(disposable.Buffer);

#pragma warning disable CS8500
        unsafe
        {
            ToStringCallbackContext callbackContext;
            callbackContext._length = length;
            PInvoke_WithUnsafeBytes(&Callback, &callbackContext, contiguousArray, elementType, resultType);
            return callbackContext._returnString!;

            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
            static IntPtr Callback(byte* bytes, SwiftSelf context)
            {
                ToStringCallbackContext* pContext = (ToStringCallbackContext*)context.Value;
                pContext->_returnString = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(bytes, pContext->_length));
                return default;
            }
        }
#pragma warning restore CS8500
    }

    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSSMa")]
    public static extern TypeMetadata PInvoke_getMetadata();

    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSS21_builtinStringLiteral17utf8CodeUnitCount7isASCIISSBp_BwBi1_tcfC")]
    public static extern unsafe SwiftString.Buffer PInvoke_Create(byte* str, long len, byte isASCII);

    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSS5countSivg")]
    public static extern long PInvoke_GetLength(SwiftString.Buffer str);

    // https://developer.apple.com/documentation/swift/string/utf8cstring
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$sSS11utf8CStrings15ContiguousArrayVys4Int8VGvg")]
    public static extern IntPtr PInvoke_GetUtf8ContiguousArray(SwiftString.Buffer str);

    // https://developer.apple.com/documentation/swift/contiguousarray/withunsafebytes(_:)
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "$ss15ContiguousArrayV15withUnsafeBytesyqd__qd__SWKXEKlF")]
    public static extern unsafe IntPtr PInvoke_WithUnsafeBytes(delegate* unmanaged[Swift]<byte*, SwiftSelf, IntPtr> callback, void* context, IntPtr contiguousArray, TypeMetadata elementType, TypeMetadata resultType);

    /// <summary>
    /// Implicitly converts a C# string to a SwiftString.
    /// </summary>
    public static implicit operator SwiftString(string value)
    {
        if (value == null)
            throw new ArgumentNullException(nameof(value));
        return new SwiftString(value);
    }

    /// <summary>
    /// Implicitly converts a SwiftString to a C# string.
    /// </summary>
    public static implicit operator string(SwiftString value)
    {
        if (value == null)
            throw new ArgumentNullException(nameof(value));
        return value.ToString();
    }

    /// <summary>
    /// Releases the resources used by the SwiftString.
    /// </summary>
    public void Dispose()
    {
        _payload?.Dispose();
    }

    /// <summary>
    /// P/Invoke declarations for the SwiftBindingsRuntime library.
    /// Uses CallingConvention.Cdecl to avoid the Mono JIT CallConvSwift assertion.
    /// </summary>
    private static class RuntimeNativeMethods
    {
        private const string LibraryName = "SwiftBindingsRuntime";

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
                   EntryPoint = "SBW_SwiftString_ToUtf8")]
        public static extern void SwiftString_ToUtf8(
            IntPtr bufferPtr, out IntPtr outPtr, out nint outLen);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
                   EntryPoint = "SBW_SwiftString_GetCount")]
        public static extern nint SwiftString_GetCount(IntPtr bufferPtr);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
                   EntryPoint = "SBW_SwiftString_FreeUtf8")]
        public static extern void SwiftString_FreeUtf8(IntPtr ptr);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
                   EntryPoint = "SBW_SwiftString_Create")]
        public static extern void SwiftString_Create(
            IntPtr utf8Ptr, nint utf8Len, IntPtr outBufferPtr);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
                   EntryPoint = "SBW_SwiftString_Destroy")]
        public static extern void SwiftString_Destroy(IntPtr bufferPtr);
    }

    private struct ToStringCallbackContext
    {
        public int _length;
        public string _returnString;
    }
}
