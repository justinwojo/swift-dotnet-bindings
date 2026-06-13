// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Swift.Runtime;
using Swift.Runtime.InteropServices;

namespace Swift;

/// <summary>
/// Represents a Swift string with Foundation.Data payload.
/// </summary>
public class SwiftString : ISwiftObject, ISwiftStruct, ISwiftMovesPayloadOnConstruction, IDisposable
{
    private static nuint _payloadSize = SwiftObjectHelper<SwiftString>.GetTypeMetadata().Size;

    public struct Buffer
    {
        // Placeholder to match the 16-byte layout of Swift.String's storage (two-word
        // Foundation.Data representation). The fields are never accessed by managed code —
        // layout is the only thing that matters for PayloadBuffer<Buffer> size/alignment.
        // Runtime cannot reference Swift.Foundation.Data (that type moved to
        // SwiftBindings.Apple), so the layout is expressed with primitives here.
#pragma warning disable CS0169
        private long _word0;
        private IntPtr _word1;
#pragma warning restore CS0169
    }

    private SwiftSafeHandle<SwiftString> _payload;
    private bool _disposed;

    public SwiftSafeHandle<SwiftString> Payload
    {
        get { ThrowIfDisposed(); return _payload; }
    }

    private static Dictionary<Type, string> _protocolConformanceSymbols;

    static SwiftString()
    {
        _protocolConformanceSymbols = new Dictionary<Type, string>
        {
            { typeof(ISwiftHashable), "$sSSSHsMc" }, // Swift.String : Swift.Hashable
        };

        // On NativeAOT, pre-register protocol conformances in ConformanceDispatcher.
        // MakeGenericMethod on GetProtocolConformanceDescriptor<TProtocol> may fail for
        // generic instantiations not statically referenced at compile time.
        if (SwiftRuntimeInfo.IsNativeAotRuntime)
        {
            NativeAotRegisterConformances();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void NativeAotRegisterConformances()
    {
        foreach (var (protocolType, symbol) in _protocolConformanceSymbols)
        {
            var symbolName = symbol;
            ConformanceDispatcher.Register(typeof(SwiftString), protocolType,
                () => ProtocolConformanceDescriptor.LoadFromSymbol("/usr/lib/swift/libswiftCore.dylib", symbolName));
        }
    }

    public unsafe PayloadBuffer<SwiftString.Buffer> PayloadBuffer
    {
        get { ThrowIfDisposed(); return new PayloadBuffer<SwiftString.Buffer>(_payload); }
    }

    IntPtr ISwiftObject.SwiftHandle
    {
        get { ThrowIfDisposed(); return _payload.DangerousGetHandle(); }
    }

    // Non-reflective borrowed-marshal finalizer suppression (Finding 56a). See ISwiftObject.SuppressPayloadFinalizer.
    void ISwiftObject.SuppressPayloadFinalizer() => global::System.GC.SuppressFinalize(_payload);

    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        return TypeMetadata.Cache.GetOrAdd(typeof(SwiftString), _ =>
        {
            // Prefer SwiftBindingsRuntime wrapper (Cdecl, no CallConvSwift)
            try
            {
                return TypeMetadata.FromHandle(RuntimeNativeMethods.SwiftString_GetMetadata());
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }

            // Fallback: resolve the direct metadata symbol from libswiftCore (no function call needed).
            // $sSSN is Swift.String's metadata pointer — NativeLibrary.TryGetExport returns it directly.
            if (NativeLibrary.TryLoad(KnownLibraries.SwiftCore, out var coreHandle) &&
                NativeLibrary.TryGetExport(coreHandle, "$sSSN", out var metadataPtr))
            {
                return TypeMetadata.FromHandle(metadataPtr);
            }

            throw new SwiftRuntimeException(
                "Unable to get type metadata for SwiftString. " +
                "Ensure either libSwiftBindingsRuntime.dylib or libswiftCore.dylib is available.");
        });
    }

    static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
    {
        return new SwiftString(handle);
    }

    /// <summary>
    /// Creates a SwiftString from a raw Swift string payload buffer.
    /// Used for unboxing strings from existential containers.
    /// </summary>
    /// <param name="payloadPtr">Pointer to a buffer containing the raw SwiftString payload.</param>
    /// <returns>A new SwiftString owning the payload data.</returns>
    public static SwiftString FromPayload(IntPtr payloadPtr) => new SwiftString(payloadPtr);

    int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
    {
        ThrowIfDisposed();
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
    /// Constructs a new SwiftString from a pointer to raw Swift string payload.
    /// The caller must ensure the pointer points to a valid SwiftString.Buffer.
    /// <para>
    /// This is a <b>bitwise move</b>, not a value-witness copy: it duplicates the two-word
    /// <see cref="Buffer"/> without a bridge-object retain, so the source's <c>+1</c> transfers into
    /// this instance rather than producing an independent one. This is why <see cref="SwiftString"/>
    /// is flagged <see cref="ISwiftMovesPayloadOnConstruction"/> — payload extraction must NOT
    /// value-witness-destroy the temporary it hands here (the retain has already moved).
    /// </para>
    /// </summary>
    internal unsafe SwiftString(IntPtr handle)
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
            try
            {
                fixed (byte* utf8BytesPtr = utf8Bytes)
                {
                    RuntimeNativeMethods.SwiftString_Create(
                        (IntPtr)utf8BytesPtr, utf8Bytes.Length, bufferPtr);
                }
            }
            catch (DllNotFoundException ex) { NativeMemory.Free((void*)bufferPtr); ThrowMissingRuntime(ex); }
            catch (EntryPointNotFoundException ex) { NativeMemory.Free((void*)bufferPtr); ThrowMissingRuntime(ex); }
            _payload = new SwiftSafeHandle<SwiftString>(bufferPtr);
        }
    }

    /// <summary>
    /// Gets the length of string.
    /// </summary>
    public int Length
    {
        get
        {
            ThrowIfDisposed();
            try
            {
                return GetLengthViaWrapper();
            }
            catch (DllNotFoundException ex) { ThrowMissingRuntime(ex); return 0; }
            catch (EntryPointNotFoundException ex) { ThrowMissingRuntime(ex); return 0; }
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
        ThrowIfDisposed();
        try
        {
            return ToStringViaWrapper();
        }
        catch (DllNotFoundException ex) { ThrowMissingRuntime(ex); return ""; }
        catch (EntryPointNotFoundException ex) { ThrowMissingRuntime(ex); return ""; }
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

    /// <summary>
    /// Translates raw interop exceptions into clear SwiftRuntimeException.
    /// No fallback to CallConvSwift — the wrapper library is required.
    /// </summary>
    [DoesNotReturn]
    private static void ThrowMissingRuntime(Exception inner)
    {
        throw new SwiftRuntimeException(
            "SwiftString operations require the SwiftBindingsRuntime native library. " +
            "Ensure libSwiftBindingsRuntime.dylib is included in your application bundle.", inner);
    }

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
        if (!_disposed)
        {
            _disposed = true;
            _payload?.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

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

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
                   EntryPoint = "SBW_SwiftString_GetMetadata")]
        public static extern IntPtr SwiftString_GetMetadata();
    }
}
