#nullable enable

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;
using Swift;
using Swift.Runtime;
using Swift.Runtime.InteropServices;

namespace Swift.Nuke
{
    /// <summary>
    /// Wraps a retained Swift class pointer for async operations.
    /// Used to track self pointers that were explicitly retained via Arc.Retain()
    /// before calling async Swift methods. Must be released via Arc.Release() after callback.
    /// </summary>
    internal readonly struct RetainedSelfPtr
    {
        public readonly IntPtr Ptr;
        public RetainedSelfPtr(IntPtr ptr) => Ptr = ptr;
    }
    /// <summary>
    /// Wraps a SafeHandle that needs DangerousRelease() called after async completion.
    /// Used for async instance methods on structs where the SafeHandle must stay alive
    /// until the Swift async operation completes.
    /// </summary>
    internal readonly struct DeferredSafeHandleRelease
    {
        public readonly SafeHandle Handle;
        public DeferredSafeHandleRelease(SafeHandle handle) => Handle = handle;
    }
    /// <summary>
    /// Wraps a copy buffer pointer with its TypeMetadata for proper cleanup.
    /// Used for non-frozen struct parameters in async operations.
    /// Destroy must be called before freeing the buffer to release Swift references.
    /// </summary>
    internal readonly struct CopyBufferWithType
    {
        public readonly IntPtr Buffer;
        public readonly TypeMetadata Metadata;
        public CopyBufferWithType(IntPtr buffer, TypeMetadata metadata)
        {
            Buffer = buffer;
            Metadata = metadata;
        }
    }
    public interface IImageProcessing
    {
        string Identifier { get; }
        [global::Swift.UnsupportedSwiftType("Type is missing from the type database", "Swift.AnyHashable")]
        Swift.AnyType HashableIdentifier { get; }
        UIKit.UIImage? Process(UIKit.UIImage arg0);
        Swift.Nuke.ImageContainer Process(Swift.Nuke.ImageContainer arg0, Swift.Nuke.ImageProcessingContext context);
    }
    
    /// <summary>
    /// Proxy class that enables C# implementations of the ImageProcessing protocol.
    /// Can wrap either a C# implementation or receive Swift existential containers.
    /// </summary>
    public unsafe class ImageProcessingProxy : IImageProcessing, ISwiftObject, Swift.Runtime.ISwiftExistentialConvertible<ExistentialContainer1>
    {
        /// <summary>Matches Swift ImageProcessing_vtable layout</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct ImageProcessingSwiftVTable
        {
            public IntPtr csVTHandle;
            public IntPtr func_identifier_get;
            public IntPtr func_hashableIdentifier_get;
            public IntPtr func_process_0;
            public IntPtr func_process_1;
        }
        
        /// <summary>Local vtable holding managed delegates</summary>
        private struct ImageProcessingLocalVTable
        {
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr> Func_identifier_get;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr> Func_hashableIdentifier_get;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr> Func_process_0;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> Func_process_1;
        }
        
        private static IntPtr _protocolWitnessTable;
        private static ImageProcessingSwiftVTable _swiftVTable;
        private static ImageProcessingLocalVTable _localVTable;
        private static GCHandle _localVTableHandle;
        private static bool _vtableInitialized;
        private static readonly object _vtableLock = new object();
        private readonly IImageProcessing? _csharpImpl;
        private readonly EveryProtocol? _everyProtocol;
        private ExistentialContainer1 _swiftContainer;
        static ImageProcessingProxy()
        {
            InitializeVtable();
        }
        
        private static void InitializeVtable()
        {
            lock (_vtableLock)
            {
                if (_vtableInitialized) return;
                
                _localVTable = new ImageProcessingLocalVTable
                {
                    Func_identifier_get = &Receive_identifier_get,
                    Func_hashableIdentifier_get = &Receive_hashableIdentifier_get,
                    Func_process_0 = &Receive_process_0,
                    Func_process_1 = &Receive_process_1,
                };
                
                _localVTableHandle = GCHandle.Alloc(_localVTable, GCHandleType.Pinned);
                
                _swiftVTable = new ImageProcessingSwiftVTable
                {
                    csVTHandle = GCHandle.ToIntPtr(_localVTableHandle),
                    func_identifier_get = (IntPtr)_localVTable.Func_identifier_get,
                    func_hashableIdentifier_get = (IntPtr)_localVTable.Func_hashableIdentifier_get,
                    func_process_0 = (IntPtr)_localVTable.Func_process_0,
                    func_process_1 = (IntPtr)_localVTable.Func_process_1,
                };
                
                fixed (ImageProcessingSwiftVTable* vtPtr = &_swiftVTable)
                {
                    NativeMethods.SetImageProcessing_vtable((IntPtr)vtPtr);
                }
                _vtableInitialized = true;
            }
        }
        
        #region Swift Callback Receivers
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_identifier_get(IntPtr vtHandle, IntPtr selfContainer)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImageProcessingProxy>(container);
            var result = proxy._csharpImpl!.Identifier;
            return MarshalToSwiftBuffer(result);
        }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_hashableIdentifier_get(IntPtr vtHandle, IntPtr selfContainer)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImageProcessingProxy>(container);
            var result = proxy._csharpImpl!.HashableIdentifier;
            return MarshalToSwiftBuffer(result);
        }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_process_0(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImageProcessingProxy>(container);
            var param0 = MarshalFromSwift<UIKit.UIImage>(rawArg0);
            var result = proxy._csharpImpl!.Process(param0);
            return MarshalToSwiftBuffer(result);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_process_1(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImageProcessingProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageContainer>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImageProcessingContext>(rawArg1);
            var result = proxy._csharpImpl!.Process(param0, param1);
            return MarshalToSwiftBuffer(result);
        }
        
        #endregion
        
        /// <summary>
        /// Creates a proxy wrapping a C# implementation of IImageProcessing.
        /// </summary>
        /// <param name="implementation">The C# implementation of the protocol.</param>
        public ImageProcessingProxy(IImageProcessing implementation)
        {
            _csharpImpl = implementation ?? throw new ArgumentNullException(nameof(implementation));
            _everyProtocol = new EveryProtocol();
            // Create existential container manually
            // The container holds: payload (EveryProtocol pointer), metadata, and witness table
            _swiftContainer = new ExistentialContainer1();
            _swiftContainer.Payload0 = _everyProtocol.Handle;
            _swiftContainer.ObjectMetadata = EveryProtocol.GetTypeMetadata();
            _swiftContainer[0] = ProtocolWitnessTableHandle;
            // Register this proxy so Swift callbacks can find us
            SwiftObjectRegistry.RegisterStrong(_everyProtocol.Handle, this);
        }
        /// <summary>
        /// Creates a proxy from an existing Swift existential container.
        /// Use this when receiving protocol values from Swift code.
        /// </summary>
        /// <remarks>
        /// Swift-backed proxies created with this constructor dispatch blittable and String
        /// protocol members through witness table accessors. Non-dispatchable members
        /// (non-blittable non-String types, throwing, async) throw <see cref="NotSupportedException"/>.
        /// </remarks>
        /// <param name="container">The Swift existential container.</param>
        public ImageProcessingProxy(ExistentialContainer1 container)
        {
            _swiftContainer = container;
            _csharpImpl = null;
            _everyProtocol = null;
        }
        #region Interface Implementation
        
        public string Identifier
        {
            get
            {
                if (_csharpImpl != null)
                    return _csharpImpl.Identifier;
                fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                {
                    IntPtr resultPtr = NativeMethods.SBW_ImageProcessing_get_identifier_0((IntPtr)containerPtr);
                    try
                    {
                        var slice = *(Utf8Slice*)resultPtr;
                        var str = slice.Len > 0
                            ? System.Text.Encoding.UTF8.GetString((byte*)slice.Ptr, (int)slice.Len)
                            : string.Empty;
                        return str;
                    }
                    finally { NativeMethods.SBW_ImageProcessing_free_get_identifier_0(resultPtr); }
                }
            }
        }
        
        public Swift.AnyType HashableIdentifier
        {
            get
            {
                if (_csharpImpl != null)
                    return _csharpImpl.HashableIdentifier;
                throw new NotSupportedException(
                    "Cannot get property 'HashableIdentifier' on a Swift-backed existential container. " +
                    "Protocol member access is only supported when wrapping a C# implementation.");
            }
        }
        
        public UIKit.UIImage? Process(UIKit.UIImage arg0)
        {
            if (_csharpImpl != null)
                return _csharpImpl.Process(arg0);
            throw new NotSupportedException(
                "Cannot call method 'Process' on a Swift-backed existential container. " +
                "Protocol member access is only supported when wrapping a C# implementation.");
        }
        
        public Swift.Nuke.ImageContainer Process(Swift.Nuke.ImageContainer arg0, Swift.Nuke.ImageProcessingContext context)
        {
            if (_csharpImpl != null)
                return _csharpImpl.Process(arg0, context);
            throw new NotSupportedException(
                "Cannot call method 'Process' on a Swift-backed existential container. " +
                "Protocol member access is only supported when wrapping a C# implementation.");
        }
        
        #endregion
        
        #region ISwiftObject Implementation
        /// <summary>
        /// Gets the protocol witness table handle for EveryProtocol conforming to ImageProcessing.
        /// </summary>
        public static IntPtr ProtocolWitnessTableHandle
        {
            get
            {
                if (_protocolWitnessTable == IntPtr.Zero)
                {
                    // The witness table is generated by the Swift compiler
                    // It will be available after the Swift wrapper is loaded
                    // For now, we look it up dynamically
                    _protocolWitnessTable = GetWitnessTableFromSwift();
                }
                return _protocolWitnessTable;
            }
        }
        private static IntPtr GetWitnessTableFromSwift()
        {
            // Call the Swift-exported function that returns the witness table pointer
            // This function is generated by EveryProtocolEmitter.EmitWitnessTableGetter()
            return NativeMethods.GetWitnessTable();
        }
        /// <summary>
        /// Gets the existential container that can be passed to Swift code.
        /// </summary>
        public ExistentialContainer1 GetExistentialContainer() => _swiftContainer;
        public static TypeMetadata GetTypeMetadata()
        {
            // Proxy classes don't have their own Swift metadata
            // They use the EveryProtocol metadata
            return EveryProtocol.GetTypeMetadata();
        }
        public static ISwiftObject NewFromPayload(IntPtr payload)
        {
            // Create from existential container
            var container = *(ExistentialContainer1*)payload;
            return new ImageProcessingProxy(container);
        }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            // Marshal the existential container
            var size = _swiftContainer.SizeOf;
            if (swiftDestSpan.Length < size)
                throw new ArgumentException("Destination span too small", nameof(swiftDestSpan));
            fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
            {
                new Span<byte>(containerPtr, size).CopyTo(swiftDestSpan);
            }
            return size;
        }
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
        {
            throw new NotSupportedException(
                "Protocol conformance descriptor is not available for proxy types. " +
                "Proxy classes use EveryProtocol's witness table, not native conformance descriptors.");
        }
        public void Dispose() { }
        #endregion
        #region Marshalling Helpers
        [StructLayout(LayoutKind.Sequential)]
        private struct Utf8Slice
        {
            public IntPtr Ptr;
            public nint Len;
        }
        private static IntPtr MarshalToSwiftBuffer<T>(T value)
        {
            // Use direct memory operations for all types
            // This works for blittable types (value types, structs with blittable fields)
            var size = Unsafe.SizeOf<T>();
            var ptr = (IntPtr)NativeMemory.Alloc((nuint)size);
            Unsafe.Write((void*)ptr, value);
            return ptr;
        }
        private static T MarshalFromSwift<T>(IntPtr ptr)
        {
            // Use direct memory operations for all types
            return Unsafe.Read<T>((void*)ptr);
        }
        #endregion
        private static class NativeMethods
        {
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SetImageProcessing_vtable")]
            public static extern void SetImageProcessing_vtable(IntPtr vtable);
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "Get_EveryProtocol_ImageProcessing_WitnessTable")]
            public static extern IntPtr GetWitnessTable();
            
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SBW_ImageProcessing_get_identifier_0")]
            public static extern IntPtr SBW_ImageProcessing_get_identifier_0(IntPtr containerPtr);
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SBW_ImageProcessing_free_get_identifier_0")]
            public static extern void SBW_ImageProcessing_free_get_identifier_0(IntPtr ptr);
        }
    }
    
    
    public unsafe class ImageProcessingContext : ISwiftObject
    {
        private unsafe Swift.Nuke.ImageRequest Request_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_request_Get_5D6DB1F8(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingContextV7requestAA0B7RequestVvg")]
        private static extern void PInvoke_request_Get_5D6DB1F8( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Request_Set( Swift.Nuke.ImageRequest value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_request_Set_62CE9651(value.Payload, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingContextV7requestAA0B7RequestVvs")]
        private static extern void PInvoke_request_Set_62CE9651( SafeHandle value,  SwiftSelf self);
        
        public Swift.Nuke.ImageRequest Request
        {
            get => Request_Get();
            set => Request_Set(value);
        }
        
        private unsafe Swift.Nuke.ImageResponse Response_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageResponse>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_response_Get_34F522E6(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageResponse>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingContextV8responseAA0B8ResponseVvg")]
        private static extern void PInvoke_response_Get_34F522E6( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Response_Set( Swift.Nuke.ImageResponse value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_response_Set_17B2D048(value.Payload, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingContextV8responseAA0B8ResponseVvs")]
        private static extern void PInvoke_response_Set_17B2D048( SafeHandle value,  SwiftSelf self);
        
        public Swift.Nuke.ImageResponse Response
        {
            get => Response_Get();
            set => Response_Set(value);
        }
        
        private unsafe System.Boolean IsCompleted_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_isCompleted_Get_5734A31C(self);
                
                return result;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingContextV11isCompletedSbvg")]
        private static extern System.Boolean PInvoke_isCompleted_Get_5734A31C( SwiftSelf self);
        
        private unsafe void IsCompleted_Set( System.Boolean value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_isCompleted_Set_16E0A398(value, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingContextV11isCompletedSbvs")]
        private static extern void PInvoke_isCompleted_Set_16E0A398( System.Boolean value,  SwiftSelf self);
        
        public System.Boolean IsCompleted
        {
            get => IsCompleted_Get();
            set => IsCompleted_Set(value);
        }
        
        static nuint _payloadSize = SwiftObjectHelper<ImageProcessingContext>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageProcessingContext> _payload = SwiftSafeHandle<ImageProcessingContext>.Zero;
        
        internal SwiftSafeHandle<ImageProcessingContext> Payload => _payload;
        
        public void Dispose() => _payload.Dispose();
        
        static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingContextVMa")]
        internal static extern TypeMetadata PInvoke_getMetadata();
        
        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
        {
            return new ImageProcessingContext(handle);
        }
        
        ImageProcessingContext(SwiftHandle handle)
        {
            _payload = new SwiftSafeHandle<ImageProcessingContext>(handle);
        }
        
        unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            var metadata = SwiftObjectHelper<ImageProcessingContext>.GetTypeMetadata();
            if ((int)metadata.Size > swiftDestSpan.Length)
            {
                throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
            }
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure that the instance is valid before making copy
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
        
        private static Dictionary<Type, string> _protocolConformanceSymbols;
        static ImageProcessingContext()
        {
            _protocolConformanceSymbols = new Dictionary<Type, string>
            {
                
            };
        }
        
        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
            where TProtocol : class
        {
            if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
            {
                throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type ImageProcessingContext and protocol {typeof(TProtocol).Name}, but no conformance was found.");
            }
            return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
        }
        
        
        public unsafe ImageProcessingContext( Swift.Nuke.ImageRequest request,  Swift.Nuke.ImageResponse response,  System.Boolean isCompleted)
        {
            _payload = new SwiftSafeHandle<ImageProcessingContext>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            PInvoke_init_4E7F4C9E(swiftIndirectResult, request.Payload, response.Payload, isCompleted);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingContextV7request8response11isCompletedAcA0B7RequestV_AA0B8ResponseVSbtcfC")]
        private static extern void PInvoke_init_4E7F4C9E( SwiftIndirectResult swiftIndirectResult,  SafeHandle request,  SafeHandle response,  System.Boolean isCompleted);
        
        
    }
    
    
    public unsafe class ImageProcessingError : ISwiftObject
    {
        static nuint _payloadSize = SwiftObjectHelper<ImageProcessingError>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageProcessingError> _payload = SwiftSafeHandle<ImageProcessingError>.Zero;
        internal SwiftSafeHandle<ImageProcessingError> Payload => _payload;
        
        public void Dispose() => _payload.Dispose();
        
        /// <summary>
        /// Gets the 'unknown' case of ImageProcessingError.
        /// </summary>
        public static ImageProcessingError Unknown
        {
            get
            {
                var result = new ImageProcessingError();
                var metadata = SwiftObjectHelper<ImageProcessingError>.GetTypeMetadata();
                IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                metadata.ValueWitnessTable->DestructiveInjectEnumTag((void*)buffer, (uint)0, metadata);
                result._payload = new SwiftSafeHandle<ImageProcessingError>(buffer);
                return result;
            }
        }
        
        /// <summary>
        /// Enum representing the possible cases of ImageProcessingError.
        /// Tag values follow Swift's ordering: payload cases first, then no-payload cases.
        /// </summary>
        public enum CaseTag : uint
        {
            Unknown = 0,
        }
        
        /// <summary>
        /// Gets the current case of this enum instance.
        /// </summary>
        public unsafe CaseTag Tag
        {
            get
            {
                bool success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var metadata = SwiftObjectHelper<ImageProcessingError>.GetTypeMetadata();
                    byte* payload = (byte*)_payload.DangerousGetHandle();
                    return (CaseTag)metadata.ValueWitnessTable->GetEnumTag(payload, metadata);
                }
                finally
                {
                    if (success)
                        _payload.DangerousRelease();
                }
            }
        }
        
        
        private unsafe Swift.SwiftString Description_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_description_Get_65E0C537(self);
                
                unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageProcessingErrorO11descriptionSSvg")]
        private static extern Swift.SwiftString.Buffer PInvoke_description_Get_65E0C537( SwiftSelf self);
        
        public string Description
        {
            get => Description_Get().ToString();
        }
        
        private unsafe nint HashValue_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_hashValue_Get_0AD9CD7C(self);
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageProcessingErrorO9hashValueSivg")]
        private static extern nint PInvoke_hashValue_Get_0AD9CD7C( SwiftSelf self);
        
        public nint HashValue
        {
            get => HashValue_Get();
        }
        
        static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageProcessingErrorOMa")]
        internal static extern TypeMetadata PInvoke_getMetadata();
        
        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
        {
            return new ImageProcessingError(handle);
        }
        
        ImageProcessingError()
        {
        }
        
        ImageProcessingError(SwiftHandle handle)
        {
            _payload = new SwiftSafeHandle<ImageProcessingError>(handle);
        }
        
        unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            var metadata = SwiftObjectHelper<ImageProcessingError>.GetTypeMetadata();
            if ((int)metadata.Size > swiftDestSpan.Length)
            {
                throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
            }
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure that the instance is valid before making copy
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
        
        private static Dictionary<Type, string> _protocolConformanceSymbols;
        static ImageProcessingError()
        {
            _protocolConformanceSymbols = new Dictionary<Type, string>
            {
                {typeof(IEquatable<ImageProcessingError>), "$s4Nuke20ImageProcessingErrorOSQAAMc"}
            };
        }
        
        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
            where TProtocol : class
        {
            if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
            {
                throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type ImageProcessingError and protocol {typeof(TProtocol).Name}, but no conformance was found.");
            }
            return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
        }
        
        public unsafe void Hash(ref Swift.Hasher into)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_hash_06B2375A(into.Payload, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageProcessingErrorO4hash4intoys6HasherVz_tF")]
        private static extern void PInvoke_hash_06B2375A( SafeHandle into,  SwiftSelf self);
        
        
    }
    
    
    public unsafe class ImageResponse : ISwiftObject
    {
        private unsafe Swift.Nuke.ImageContainer Container_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageContainer>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_container_Get_3E6A2C0F(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageContainer>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV9containerAA0B9ContainerVvg")]
        private static extern void PInvoke_container_Get_3E6A2C0F( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Container_Set( Swift.Nuke.ImageContainer value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_container_Set_5FE6AE64(value.Payload, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV9containerAA0B9ContainerVvs")]
        private static extern void PInvoke_container_Set_5FE6AE64( SafeHandle value,  SwiftSelf self);
        
        public Swift.Nuke.ImageContainer Container
        {
            get => Container_Get();
            set => Container_Set(value);
        }
        
        private unsafe UIKit.UIImage Image_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<UIKit.UIImage>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_image_Get_524C46CE(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<UIKit.UIImage>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV5imageSo7UIImageCvg")]
        private static extern void PInvoke_image_Get_524C46CE( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public UIKit.UIImage Image
        {
            get => Image_Get();
        }
        
        private unsafe System.Boolean IsPreview_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_isPreview_Get_18557C50(self);
                
                return result;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV9isPreviewSbvg")]
        private static extern System.Boolean PInvoke_isPreview_Get_18557C50( SwiftSelf self);
        
        public System.Boolean IsPreview
        {
            get => IsPreview_Get();
        }
        
        private unsafe Swift.Nuke.ImageRequest Request_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_request_Get_12B3C69B(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV7requestAA0B7RequestVvg")]
        private static extern void PInvoke_request_Get_12B3C69B( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Request_Set( Swift.Nuke.ImageRequest value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_request_Set_39E5FD95(value.Payload, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV7requestAA0B7RequestVvs")]
        private static extern void PInvoke_request_Set_39E5FD95( SafeHandle value,  SwiftSelf self);
        
        public Swift.Nuke.ImageRequest Request
        {
            get => Request_Get();
            set => Request_Set(value);
        }
        
        private unsafe Swift.SwiftOptional<Foundation.NSUrlResponse> UrlResponse_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_urlResponse_Get_2CB2C9BA(self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Foundation.NSUrlResponse>>(new IntPtr(&result));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV03urlC0So13NSURLResponseCSgvg")]
        private static extern IntPtr PInvoke_urlResponse_Get_2CB2C9BA( SwiftSelf self);
        
        private unsafe void UrlResponse_Set( Swift.SwiftOptional<Foundation.NSUrlResponse> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_urlResponse_Set_4BD894EA(valueBuffer, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV03urlC0So13NSURLResponseCSgvs")]
        private static extern void PInvoke_urlResponse_Set_4BD894EA( IntPtr valueBuffer,  SwiftSelf self);
        
        public Foundation.NSUrlResponse? UrlResponse
        {
            get => ((Foundation.NSUrlResponse?)UrlResponse_Get());
            set => UrlResponse_Set((value is {} valueVal ? SwiftOptional<Foundation.NSUrlResponse>.NewSome(valueVal) : SwiftOptional<Foundation.NSUrlResponse>.NewNone()));
        }
        
        private unsafe Swift.SwiftOptional<Swift.Nuke.ImageResponse.CacheTypeInfo> CacheType_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_cacheType_Get_3C8A493F(self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Nuke.ImageResponse.CacheTypeInfo>>(new IntPtr(&result));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV9cacheTypeAC05CacheE0OSgvg")]
        private static extern IntPtr PInvoke_cacheType_Get_3C8A493F( SwiftSelf self);
        
        private unsafe void CacheType_Set( Swift.SwiftOptional<Swift.Nuke.ImageResponse.CacheTypeInfo> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_cacheType_Set_0FE29AD1(valueBuffer, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV9cacheTypeAC05CacheE0OSgvs")]
        private static extern void PInvoke_cacheType_Set_0FE29AD1( IntPtr valueBuffer,  SwiftSelf self);
        
        public Swift.Nuke.ImageResponse.CacheTypeInfo? CacheType
        {
            get => ((Swift.Nuke.ImageResponse.CacheTypeInfo?)CacheType_Get());
            set => CacheType_Set((value is {} valueVal ? SwiftOptional<Swift.Nuke.ImageResponse.CacheTypeInfo>.NewSome(valueVal) : SwiftOptional<Swift.Nuke.ImageResponse.CacheTypeInfo>.NewNone()));
        }
        
        static nuint _payloadSize = SwiftObjectHelper<ImageResponse>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageResponse> _payload = SwiftSafeHandle<ImageResponse>.Zero;
        
        internal SwiftSafeHandle<ImageResponse> Payload => _payload;
        
        public void Dispose() => _payload.Dispose();
        
        static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseVMa")]
        internal static extern TypeMetadata PInvoke_getMetadata();
        
        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
        {
            return new ImageResponse(handle);
        }
        
        ImageResponse(SwiftHandle handle)
        {
            _payload = new SwiftSafeHandle<ImageResponse>(handle);
        }
        
        unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            var metadata = SwiftObjectHelper<ImageResponse>.GetTypeMetadata();
            if ((int)metadata.Size > swiftDestSpan.Length)
            {
                throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
            }
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure that the instance is valid before making copy
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
        
        private static Dictionary<Type, string> _protocolConformanceSymbols;
        static ImageResponse()
        {
            _protocolConformanceSymbols = new Dictionary<Type, string>
            {
                
            };
        }
        
        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
            where TProtocol : class
        {
            if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
            {
                throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type ImageResponse and protocol {typeof(TProtocol).Name}, but no conformance was found.");
            }
            return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
        }
        
        
        public unsafe class CacheTypeInfo : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<CacheTypeInfo>.GetTypeMetadata().Size;
            SwiftSafeHandle<CacheTypeInfo> _payload = SwiftSafeHandle<CacheTypeInfo>.Zero;
            internal SwiftSafeHandle<CacheTypeInfo> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            /// <summary>
            /// Gets the 'memory' case of CacheTypeInfo.
            /// </summary>
            public static CacheTypeInfo Memory
            {
                get
                {
                    var result = new CacheTypeInfo();
                    var metadata = SwiftObjectHelper<CacheTypeInfo>.GetTypeMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    metadata.ValueWitnessTable->DestructiveInjectEnumTag((void*)buffer, (uint)0, metadata);
                    result._payload = new SwiftSafeHandle<CacheTypeInfo>(buffer);
                    return result;
                }
            }
            
            /// <summary>
            /// Gets the 'disk' case of CacheTypeInfo.
            /// </summary>
            public static CacheTypeInfo Disk
            {
                get
                {
                    var result = new CacheTypeInfo();
                    var metadata = SwiftObjectHelper<CacheTypeInfo>.GetTypeMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    metadata.ValueWitnessTable->DestructiveInjectEnumTag((void*)buffer, (uint)1, metadata);
                    result._payload = new SwiftSafeHandle<CacheTypeInfo>(buffer);
                    return result;
                }
            }
            
            /// <summary>
            /// Enum representing the possible cases of CacheType.
            /// Tag values follow Swift's ordering: payload cases first, then no-payload cases.
            /// </summary>
            public enum CaseTag : uint
            {
                Memory = 0,
                Disk = 1,
            }
            
            /// <summary>
            /// Gets the current case of this enum instance.
            /// </summary>
            public unsafe CaseTag Tag
            {
                get
                {
                    bool success = false;
                    _payload.DangerousAddRef(ref success);
                    try
                    {
                        var metadata = SwiftObjectHelper<CacheTypeInfo>.GetTypeMetadata();
                        byte* payload = (byte*)_payload.DangerousGetHandle();
                        return (CaseTag)metadata.ValueWitnessTable->GetEnumTag(payload, metadata);
                    }
                    finally
                    {
                        if (success)
                            _payload.DangerousRelease();
                    }
                }
            }
            
            
            private unsafe nint HashValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_5757ECED(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV9CacheTypeO9hashValueSivg")]
            private static extern nint PInvoke_hashValue_Get_5757ECED( SwiftSelf self);
            
            public nint HashValue
            {
                get => HashValue_Get();
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV9CacheTypeOMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new CacheTypeInfo(handle);
            }
            
            CacheTypeInfo()
            {
            }
            
            CacheTypeInfo(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<CacheTypeInfo>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<CacheTypeInfo>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static CacheTypeInfo()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IEquatable<CacheTypeInfo>), "$s4Nuke13ImageResponseV9CacheTypeOSQAAMc"}
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type CacheType and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
            public unsafe void Hash(ref Swift.Hasher into)
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_384B2444(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV9CacheTypeO4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_384B2444( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        public unsafe ImageResponse( Swift.Nuke.ImageContainer container,  Swift.Nuke.ImageRequest request,  Foundation.NSUrlResponse? urlResponse,  Swift.Nuke.ImageResponse.CacheTypeInfo? cacheType)
        {
            _payload = new SwiftSafeHandle<ImageResponse>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            using var urlResponseSwift = urlResponse is {} urlResponseValue ? SwiftOptional<Foundation.NSUrlResponse>.NewSome(urlResponseValue) : SwiftOptional<Foundation.NSUrlResponse>.NewNone();
            using PayloadBuffer<IntPtr> urlResponseDisposable = urlResponseSwift.PayloadBuffer;
            IntPtr urlResponseBuffer = urlResponseDisposable.Buffer;
            using var cacheTypeSwift = cacheType is {} cacheTypeValue ? SwiftOptional<Swift.Nuke.ImageResponse.CacheTypeInfo>.NewSome(cacheTypeValue) : SwiftOptional<Swift.Nuke.ImageResponse.CacheTypeInfo>.NewNone();
            using PayloadBuffer<IntPtr> cacheTypeDisposable = cacheTypeSwift.PayloadBuffer;
            IntPtr cacheTypeBuffer = cacheTypeDisposable.Buffer;
            PInvoke_init_685BA25C(swiftIndirectResult, container.Payload, request.Payload, urlResponseBuffer, cacheTypeBuffer);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV9container7request03urlC09cacheTypeAcA0B9ContainerV_AA0B7RequestVSo13NSURLResponseCSgAC05CacheH0OSgtcfC")]
        private static extern void PInvoke_init_685BA25C( SwiftIndirectResult swiftIndirectResult,  SafeHandle container,  SafeHandle request,  IntPtr urlResponseBuffer,  IntPtr cacheTypeBuffer);
        public unsafe ImageResponse( Swift.Nuke.ImageContainer container,  Swift.Nuke.ImageRequest request)
        {
            _payload = new SwiftSafeHandle<ImageResponse>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            PInvoke_init_60EB73F8(swiftIndirectResult, container.Payload, request.Payload);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("SwiftBindings", EntryPoint = "DBW_ImageResponse_init_F8ACDC69_2")]
        private static extern void PInvoke_init_60EB73F8( SwiftIndirectResult swiftIndirectResult,  SafeHandle container,  SafeHandle request);
        public unsafe ImageResponse( Swift.Nuke.ImageContainer container,  Swift.Nuke.ImageRequest request,  Foundation.NSUrlResponse? urlResponse)
        {
            _payload = new SwiftSafeHandle<ImageResponse>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            using var urlResponseSwift = urlResponse is {} urlResponseValue ? SwiftOptional<Foundation.NSUrlResponse>.NewSome(urlResponseValue) : SwiftOptional<Foundation.NSUrlResponse>.NewNone();
            using PayloadBuffer<IntPtr> urlResponseDisposable = urlResponseSwift.PayloadBuffer;
            IntPtr urlResponseBuffer = urlResponseDisposable.Buffer;
            PInvoke_init_16202F0A(swiftIndirectResult, container.Payload, request.Payload, urlResponseBuffer);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("SwiftBindings", EntryPoint = "DBW_ImageResponse_init_F8ACDC69_1")]
        private static extern void PInvoke_init_16202F0A( SwiftIndirectResult swiftIndirectResult,  SafeHandle container,  SafeHandle request,  IntPtr urlResponseBuffer);
        
        
    }
    
    
    public unsafe class ImageCache : ISwiftObject
    {
        private unsafe nint CostLimit_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_costLimit_Get_72C2CC9D(self);
                
                return result;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC9costLimitSivg")]
        private static extern nint PInvoke_costLimit_Get_72C2CC9D( SwiftSelf self);
        
        private unsafe void CostLimit_Set( nint value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_costLimit_Set_1F470CE4(value, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC9costLimitSivs")]
        private static extern void PInvoke_costLimit_Set_1F470CE4( nint value,  SwiftSelf self);
        
        public nint CostLimit
        {
            get => CostLimit_Get();
            set => CostLimit_Set(value);
        }
        
        private unsafe nint CountLimit_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_countLimit_Get_17362758(self);
                
                return result;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC10countLimitSivg")]
        private static extern nint PInvoke_countLimit_Get_17362758( SwiftSelf self);
        
        private unsafe void CountLimit_Set( nint value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_countLimit_Set_47DE0BB6(value, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC10countLimitSivs")]
        private static extern void PInvoke_countLimit_Set_47DE0BB6( nint value,  SwiftSelf self);
        
        public nint CountLimit
        {
            get => CountLimit_Get();
            set => CountLimit_Set(value);
        }
        
        private unsafe Swift.SwiftOptional<System.Double> Ttl_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_ttl_Get_0DD4133D(self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<System.Double>>(new IntPtr(&result));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC3ttlSdSgvg")]
        private static extern IntPtr PInvoke_ttl_Get_0DD4133D( SwiftSelf self);
        
        private unsafe void Ttl_Set( Swift.SwiftOptional<System.Double> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_ttl_Set_098483CD(valueBuffer, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC3ttlSdSgvs")]
        private static extern void PInvoke_ttl_Set_098483CD( IntPtr valueBuffer,  SwiftSelf self);
        
        public System.Double? Ttl
        {
            get => ((System.Double?)Ttl_Get());
            set => Ttl_Set((value is {} valueVal ? SwiftOptional<System.Double>.NewSome(valueVal) : SwiftOptional<System.Double>.NewNone()));
        }
        
        private unsafe System.Double EntryCostLimit_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_entryCostLimit_Get_16D56ABF(self);
                
                return result;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC14entryCostLimitSdvg")]
        private static extern System.Double PInvoke_entryCostLimit_Get_16D56ABF( SwiftSelf self);
        
        private unsafe void EntryCostLimit_Set( System.Double value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_entryCostLimit_Set_61F5EFB3(value, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC14entryCostLimitSdvs")]
        private static extern void PInvoke_entryCostLimit_Set_61F5EFB3( System.Double value,  SwiftSelf self);
        
        public System.Double EntryCostLimit
        {
            get => EntryCostLimit_Get();
            set => EntryCostLimit_Set(value);
        }
        
        private unsafe nint TotalCount_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_totalCount_Get_6B907494(self);
                
                return result;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC10totalCountSivg")]
        private static extern nint PInvoke_totalCount_Get_6B907494( SwiftSelf self);
        
        public nint TotalCount
        {
            get => TotalCount_Get();
        }
        
        private unsafe nint TotalCost_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_totalCost_Get_4AFA4E6A(self);
                
                return result;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC9totalCostSivg")]
        private static extern nint PInvoke_totalCost_Get_4AFA4E6A( SwiftSelf self);
        
        public nint TotalCost
        {
            get => TotalCost_Get();
        }
        
        private static Swift.Nuke.ImageCache Shared_Get()
        {
            try
            {
                
                
                var result = PInvoke_shared_Get_6A25B412();
                
                var classPayload = NativeMemory.Alloc((nuint)sizeof(IntPtr));
                *(IntPtr*)classPayload = result;
                return (Swift.Nuke.ImageCache)SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageCache>(new IntPtr(classPayload));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC6sharedACvgZ")]
        private static extern IntPtr PInvoke_shared_Get_6A25B412();
        
        public static Swift.Nuke.ImageCache Shared
        {
            get => Shared_Get();
        }
        
        static nuint _payloadSize = SwiftObjectHelper<ImageCache>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageCache> _payload = SwiftSafeHandle<ImageCache>.Zero;
        
        internal SwiftSafeHandle<ImageCache> Payload => _payload;
        
        public void Dispose() => _payload.Dispose();
        
        static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheCMa")]
        internal static extern TypeMetadata PInvoke_getMetadata();
        
        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
        {
            return new ImageCache(handle);
        }
        
        ImageCache(SwiftHandle handle)
        {
            _payload = new SwiftSafeHandle<ImageCache>(handle);
        }
        
        unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            var metadata = SwiftObjectHelper<ImageCache>.GetTypeMetadata();
            if ((int)metadata.Size > swiftDestSpan.Length)
            {
                throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
            }
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure that the instance is valid before making copy
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
        
        private static Dictionary<Type, string> _protocolConformanceSymbols;
        static ImageCache()
        {
            _protocolConformanceSymbols = new Dictionary<Type, string>
            {
                {typeof(IImageCaching), "$s4Nuke10ImageCacheCAA0B7CachingAAMc"}
            };
        }
        
        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
            where TProtocol : class
        {
            if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
            {
                throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type ImageCache and protocol {typeof(TProtocol).Name}, but no conformance was found.");
            }
            return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
        }
        
        public unsafe ImageCache( nint costLimit,  nint countLimit)
        {
            _payload = new SwiftSafeHandle<ImageCache>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            PInvoke_init_7B275410(swiftIndirectResult, costLimit, countLimit);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC9costLimit05countE0ACSi_SitcfC")]
        private static extern void PInvoke_init_7B275410( SwiftIndirectResult swiftIndirectResult,  nint costLimit,  nint countLimit);
        public unsafe ImageCache()
        {
            _payload = new SwiftSafeHandle<ImageCache>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            PInvoke_init_28068D51(swiftIndirectResult);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("SwiftBindings", EntryPoint = "DBW_ImageCache_init_F7E1ED79_2")]
        private static extern void PInvoke_init_28068D51( SwiftIndirectResult swiftIndirectResult);
        public unsafe ImageCache( nint costLimit)
        {
            _payload = new SwiftSafeHandle<ImageCache>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            PInvoke_init_2BAB9C5C(swiftIndirectResult, costLimit);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("SwiftBindings", EntryPoint = "DBW_ImageCache_init_F7E1ED79_1")]
        private static extern void PInvoke_init_2BAB9C5C( SwiftIndirectResult swiftIndirectResult,  nint costLimit);
        
        
        public static nint DefaultCostLimit()
        {
            try
            {
                
                
                var result = PInvoke_defaultCostLimit_75C3568D();
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC16defaultCostLimitSiyFZ")]
        private static extern nint PInvoke_defaultCostLimit_75C3568D();
        
        
        public unsafe void RemoveAll()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_removeAll_62BEF9CE(self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC9removeAllyyF")]
        private static extern void PInvoke_removeAll_62BEF9CE( SwiftSelf self);
        
        
        public unsafe void Trim( nint toCost)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_trim_10A28ED5(toCost, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC4trim6toCostySi_tF")]
        private static extern void PInvoke_trim_10A28ED5( nint toCost,  SwiftSelf self);
        
        
    }
    
    
    public unsafe class ImagePipeline : ISwiftObject
    {
        private static Swift.Nuke.ImagePipeline Shared_Get()
        {
            try
            {
                
                
                var result = PInvoke_shared_Get_39AAE043();
                
                var classPayload = NativeMemory.Alloc((nuint)sizeof(IntPtr));
                *(IntPtr*)classPayload = result;
                return (Swift.Nuke.ImagePipeline)SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline>(new IntPtr(classPayload));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC6sharedACvgZ")]
        private static extern IntPtr PInvoke_shared_Get_39AAE043();
        
        private static void Shared_Set( Swift.Nuke.ImagePipeline value)
        {
            try
            {
                
                
                PInvoke_shared_Set_0EA3FC9B(value.Payload);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC6sharedACvsZ")]
        private static extern void PInvoke_shared_Set_0EA3FC9B( SafeHandle value);
        
        public static Swift.Nuke.ImagePipeline Shared
        {
            get => Shared_Get();
            set => Shared_Set(value);
        }
        
        private unsafe Swift.Nuke.ImagePipeline.ConfigurationInfo Configuration_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.ConfigurationInfo>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_configuration_Get_0143FCD3(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.ConfigurationInfo>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13configurationAC13ConfigurationVvg")]
        private static extern void PInvoke_configuration_Get_0143FCD3( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Swift.Nuke.ImagePipeline.ConfigurationInfo Configuration
        {
            get => Configuration_Get();
        }
        
        private unsafe Swift.Nuke.ImagePipeline.CacheInfo Cache_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.CacheInfo>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_cache_Get_7BE3D376(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.CacheInfo>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5cacheAC5CacheVvg")]
        private static extern void PInvoke_cache_Get_7BE3D376( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Swift.Nuke.ImagePipeline.CacheInfo Cache
        {
            get => Cache_Get();
        }
        
        static nuint _payloadSize = SwiftObjectHelper<ImagePipeline>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImagePipeline> _payload = SwiftSafeHandle<ImagePipeline>.Zero;
        
        internal SwiftSafeHandle<ImagePipeline> Payload => _payload;
        
        public void Dispose() => _payload.Dispose();
        
        static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineCMa")]
        internal static extern TypeMetadata PInvoke_getMetadata();
        
        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
        {
            return new ImagePipeline(handle);
        }
        
        ImagePipeline(SwiftHandle handle)
        {
            _payload = new SwiftSafeHandle<ImagePipeline>(handle);
        }
        
        unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            var metadata = SwiftObjectHelper<ImagePipeline>.GetTypeMetadata();
            if ((int)metadata.Size > swiftDestSpan.Length)
            {
                throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
            }
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure that the instance is valid before making copy
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
        
        private static Dictionary<Type, string> _protocolConformanceSymbols;
        static ImagePipeline()
        {
            _protocolConformanceSymbols = new Dictionary<Type, string>
            {
                
            };
        }
        
        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
            where TProtocol : class
        {
            if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
            {
                throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type ImagePipeline and protocol {typeof(TProtocol).Name}, but no conformance was found.");
            }
            return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
        }
        
        public unsafe class Error : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<Error>.GetTypeMetadata().Size;
            SwiftSafeHandle<Error> _payload = SwiftSafeHandle<Error>.Zero;
            internal SwiftSafeHandle<Error> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            /// <summary>
            /// Creates the 'dataLoadingFailed' case of Error.
            /// </summary>
            public static Error DataLoadingFailed(Swift.Runtime.ExistentialContainer1 error)
            {
                var result = new Error();
                var metadata = PInvoke_getMetadata();
                IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                var indirectResult = new SwiftIndirectResult((void*)buffer);
                PInvoke_DataLoadingFailed(indirectResult, error);
                result._payload = new SwiftSafeHandle<Error>(buffer);
                return result;
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5ErrorO17dataLoadingFailedyAEsAD_p_tcAEmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_DataLoadingFailed(SwiftIndirectResult result, Swift.Runtime.ExistentialContainer1 error);
            
            /// <summary>
            /// Creates the 'decoderNotRegistered' case of Error.
            /// </summary>
            public static Error DecoderNotRegistered(Swift.Nuke.ImageDecodingContext context)
            {
                var result = new Error();
                var metadata = PInvoke_getMetadata();
                IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                var indirectResult = new SwiftIndirectResult((void*)buffer);
                PInvoke_DecoderNotRegistered(indirectResult, context);
                result._payload = new SwiftSafeHandle<Error>(buffer);
                return result;
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5ErrorO20decoderNotRegisteredyAeA0B15DecodingContextV_tcAEmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_DecoderNotRegistered(SwiftIndirectResult result, Swift.Nuke.ImageDecodingContext context);
            
            /// <summary>
            /// Creates the 'decodingFailed' case of Error.
            /// </summary>
            public static Error DecodingFailed((Swift.Runtime.ExistentialContainer1 decoder, Swift.Nuke.ImageDecodingContext context, Swift.Runtime.ExistentialContainer1 error) value0)
            {
                var result = new Error();
                var metadata = PInvoke_getMetadata();
                IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                var indirectResult = new SwiftIndirectResult((void*)buffer);
                PInvoke_DecodingFailed(indirectResult, (value0.decoder, value0.context, value0.error));
                result._payload = new SwiftSafeHandle<Error>(buffer);
                return result;
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5ErrorO14decodingFailedyAeA0B8Decoding_p_AA0bG7ContextVsAD_ptcAEmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_DecodingFailed(SwiftIndirectResult result, ValueTuple<Swift.Runtime.ExistentialContainer1, Swift.Nuke.ImageDecodingContext, Swift.Runtime.ExistentialContainer1> value0);
            
            /// <summary>
            /// Creates the 'processingFailed' case of Error.
            /// </summary>
            public static Error ProcessingFailed((Swift.Runtime.ExistentialContainer1 processor, Swift.Nuke.ImageProcessingContext context, Swift.Runtime.ExistentialContainer1 error) value0)
            {
                var result = new Error();
                var metadata = PInvoke_getMetadata();
                IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                var indirectResult = new SwiftIndirectResult((void*)buffer);
                PInvoke_ProcessingFailed(indirectResult, (value0.processor, value0.context, value0.error));
                result._payload = new SwiftSafeHandle<Error>(buffer);
                return result;
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5ErrorO16processingFailedyAeA0B10Processing_p_AA0bG7ContextVsAD_ptcAEmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_ProcessingFailed(SwiftIndirectResult result, ValueTuple<Swift.Runtime.ExistentialContainer1, Swift.Nuke.ImageProcessingContext, Swift.Runtime.ExistentialContainer1> value0);
            
            /// <summary>
            /// Gets the 'dataMissingInCache' case of Error.
            /// </summary>
            public static Error DataMissingInCache
            {
                get
                {
                    var result = new Error();
                    var metadata = SwiftObjectHelper<Error>.GetTypeMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    metadata.ValueWitnessTable->DestructiveInjectEnumTag((void*)buffer, (uint)4, metadata);
                    result._payload = new SwiftSafeHandle<Error>(buffer);
                    return result;
                }
            }
            
            /// <summary>
            /// Gets the 'dataIsEmpty' case of Error.
            /// </summary>
            public static Error DataIsEmpty
            {
                get
                {
                    var result = new Error();
                    var metadata = SwiftObjectHelper<Error>.GetTypeMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    metadata.ValueWitnessTable->DestructiveInjectEnumTag((void*)buffer, (uint)5, metadata);
                    result._payload = new SwiftSafeHandle<Error>(buffer);
                    return result;
                }
            }
            
            /// <summary>
            /// Gets the 'imageRequestMissing' case of Error.
            /// </summary>
            public static Error ImageRequestMissing
            {
                get
                {
                    var result = new Error();
                    var metadata = SwiftObjectHelper<Error>.GetTypeMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    metadata.ValueWitnessTable->DestructiveInjectEnumTag((void*)buffer, (uint)6, metadata);
                    result._payload = new SwiftSafeHandle<Error>(buffer);
                    return result;
                }
            }
            
            /// <summary>
            /// Gets the 'pipelineInvalidated' case of Error.
            /// </summary>
            public static Error PipelineInvalidated
            {
                get
                {
                    var result = new Error();
                    var metadata = SwiftObjectHelper<Error>.GetTypeMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    metadata.ValueWitnessTable->DestructiveInjectEnumTag((void*)buffer, (uint)7, metadata);
                    result._payload = new SwiftSafeHandle<Error>(buffer);
                    return result;
                }
            }
            
            /// <summary>
            /// Enum representing the possible cases of Error.
            /// Tag values follow Swift's ordering: payload cases first, then no-payload cases.
            /// </summary>
            public enum CaseTag : uint
            {
                DataLoadingFailed = 0,
                DecoderNotRegistered = 1,
                DecodingFailed = 2,
                ProcessingFailed = 3,
                DataMissingInCache = 4,
                DataIsEmpty = 5,
                ImageRequestMissing = 6,
                PipelineInvalidated = 7,
            }
            
            /// <summary>
            /// Gets the current case of this enum instance.
            /// </summary>
            public unsafe CaseTag Tag
            {
                get
                {
                    bool success = false;
                    _payload.DangerousAddRef(ref success);
                    try
                    {
                        var metadata = SwiftObjectHelper<Error>.GetTypeMetadata();
                        byte* payload = (byte*)_payload.DangerousGetHandle();
                        return (CaseTag)metadata.ValueWitnessTable->GetEnumTag(payload, metadata);
                    }
                    finally
                    {
                        if (success)
                            _payload.DangerousRelease();
                    }
                }
            }
            
            /// <summary>
            /// Attempts to extract the associated value(s) for the 'dataLoadingFailed' case.
            /// </summary>
            /// <param name="value">When this method returns true, contains the associated value(s).</param>
            /// <returns>True if this enum is the 'dataLoadingFailed' case; otherwise, false.</returns>
            public unsafe bool TryGetDataLoadingFailed([MaybeNullWhen(false)] out Swift.Runtime.ExistentialContainer1 value)
            {
                if (Tag != CaseTag.DataLoadingFailed)
                {
                    value = default;
                    return false;
                }
                
                var metadata = SwiftObjectHelper<Error>.GetTypeMetadata();
                
                // Create a non-destructive copy of the enum
                byte* enumCopy = stackalloc byte[(int)metadata.Size];
                bool success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    metadata.ValueWitnessTable->InitializeWithCopy(enumCopy, (void*)_payload.DangerousGetHandle(), metadata);
                }
                finally
                {
                    if (success)
                        _payload.DangerousRelease();
                }
                
                // Strip the tag to get the raw payload
                metadata.ValueWitnessTable->DestructiveProjectEnumData(enumCopy, metadata);
                
                // Marshal the payload to C# type(s)
                value = SwiftMarshal.MarshalFromSwift<Swift.Runtime.ExistentialContainer1>(new IntPtr(enumCopy));
                return true;
            }
            
            /// <summary>
            /// Attempts to extract the associated value(s) for the 'decoderNotRegistered' case.
            /// </summary>
            /// <param name="value">When this method returns true, contains the associated value(s).</param>
            /// <returns>True if this enum is the 'decoderNotRegistered' case; otherwise, false.</returns>
            public unsafe bool TryGetDecoderNotRegistered([MaybeNullWhen(false)] out Swift.Nuke.ImageDecodingContext value)
            {
                if (Tag != CaseTag.DecoderNotRegistered)
                {
                    value = default;
                    return false;
                }
                
                var metadata = SwiftObjectHelper<Error>.GetTypeMetadata();
                
                // Create a non-destructive copy of the enum
                byte* enumCopy = stackalloc byte[(int)metadata.Size];
                bool success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    metadata.ValueWitnessTable->InitializeWithCopy(enumCopy, (void*)_payload.DangerousGetHandle(), metadata);
                }
                finally
                {
                    if (success)
                        _payload.DangerousRelease();
                }
                
                // Strip the tag to get the raw payload
                metadata.ValueWitnessTable->DestructiveProjectEnumData(enumCopy, metadata);
                
                // Marshal the payload to C# type(s)
                value = SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageDecodingContext>(new IntPtr(enumCopy));
                return true;
            }
            
            /// <summary>
            /// Attempts to extract the associated value(s) for the 'decodingFailed' case.
            /// </summary>
            /// <param name="decoder">When this method returns true, contains the associated value.</param>
            /// <param name="context">When this method returns true, contains the associated value.</param>
            /// <param name="error">When this method returns true, contains the associated value.</param>
            /// <returns>True if this enum is the 'decodingFailed' case; otherwise, false.</returns>
            public unsafe bool TryGetDecodingFailed([MaybeNullWhen(false)] out Swift.Runtime.ExistentialContainer1 decoder, [MaybeNullWhen(false)] out Swift.Nuke.ImageDecodingContext context, [MaybeNullWhen(false)] out Swift.Runtime.ExistentialContainer1 error)
            {
                if (Tag != CaseTag.DecodingFailed)
                {
                    decoder = default;
                    context = default;
                    error = default;
                    return false;
                }
                
                var metadata = SwiftObjectHelper<Error>.GetTypeMetadata();
                
                // Create a non-destructive copy of the enum
                byte* enumCopy = stackalloc byte[(int)metadata.Size];
                bool success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    metadata.ValueWitnessTable->InitializeWithCopy(enumCopy, (void*)_payload.DangerousGetHandle(), metadata);
                }
                finally
                {
                    if (success)
                        _payload.DangerousRelease();
                }
                
                // Strip the tag to get the raw payload (which is the tuple)
                metadata.ValueWitnessTable->DestructiveProjectEnumData(enumCopy, metadata);
                
                // Get tuple metadata to determine element offsets
                var tupleMetadata = GetTupleMetadata_DecodingFailed();
                
                // Marshal each tuple element from its computed offset
                var offset0 = tupleMetadata->GetElementOffset(0);
                decoder = SwiftMarshal.MarshalFromSwift<Swift.Runtime.ExistentialContainer1>(new IntPtr(enumCopy + (int)offset0));
                var offset1 = tupleMetadata->GetElementOffset(1);
                context = SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageDecodingContext>(new IntPtr(enumCopy + (int)offset1));
                var offset2 = tupleMetadata->GetElementOffset(2);
                error = SwiftMarshal.MarshalFromSwift<Swift.Runtime.ExistentialContainer1>(new IntPtr(enumCopy + (int)offset2));
                
                return true;
            }
            
            private static TupleTypeMetadata* _tupleMetadata_DecodingFailed;
            
            private static unsafe TupleTypeMetadata* GetTupleMetadata_DecodingFailed()
            {
                if (_tupleMetadata_DecodingFailed != null)
                    return _tupleMetadata_DecodingFailed;
                
                // Build tuple metadata from element types
                var elementMetadataArray = new TypeMetadata[3];
                elementMetadataArray[0] = TypeMetadata.GetExistentialTypeMetadata(1);
                elementMetadataArray[1] = SwiftObjectHelper<Swift.Nuke.ImageDecodingContext>.GetTypeMetadata();
                elementMetadataArray[2] = TypeMetadata.GetExistentialTypeMetadata(1);
                
                // Get tuple type metadata from Swift runtime
                var tupleMetadata = TypeMetadata.GetTupleTypeMetadataFromElements(elementMetadataArray);
                
                _tupleMetadata_DecodingFailed = tupleMetadata.AsTupleMetadata();
                return _tupleMetadata_DecodingFailed;
            }
            
            /// <summary>
            /// Attempts to extract the associated value(s) for the 'processingFailed' case.
            /// </summary>
            /// <param name="processor">When this method returns true, contains the associated value.</param>
            /// <param name="context">When this method returns true, contains the associated value.</param>
            /// <param name="error">When this method returns true, contains the associated value.</param>
            /// <returns>True if this enum is the 'processingFailed' case; otherwise, false.</returns>
            public unsafe bool TryGetProcessingFailed([MaybeNullWhen(false)] out Swift.Runtime.ExistentialContainer1 processor, [MaybeNullWhen(false)] out Swift.Nuke.ImageProcessingContext context, [MaybeNullWhen(false)] out Swift.Runtime.ExistentialContainer1 error)
            {
                if (Tag != CaseTag.ProcessingFailed)
                {
                    processor = default;
                    context = default;
                    error = default;
                    return false;
                }
                
                var metadata = SwiftObjectHelper<Error>.GetTypeMetadata();
                
                // Create a non-destructive copy of the enum
                byte* enumCopy = stackalloc byte[(int)metadata.Size];
                bool success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    metadata.ValueWitnessTable->InitializeWithCopy(enumCopy, (void*)_payload.DangerousGetHandle(), metadata);
                }
                finally
                {
                    if (success)
                        _payload.DangerousRelease();
                }
                
                // Strip the tag to get the raw payload (which is the tuple)
                metadata.ValueWitnessTable->DestructiveProjectEnumData(enumCopy, metadata);
                
                // Get tuple metadata to determine element offsets
                var tupleMetadata = GetTupleMetadata_ProcessingFailed();
                
                // Marshal each tuple element from its computed offset
                var offset0 = tupleMetadata->GetElementOffset(0);
                processor = SwiftMarshal.MarshalFromSwift<Swift.Runtime.ExistentialContainer1>(new IntPtr(enumCopy + (int)offset0));
                var offset1 = tupleMetadata->GetElementOffset(1);
                context = SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageProcessingContext>(new IntPtr(enumCopy + (int)offset1));
                var offset2 = tupleMetadata->GetElementOffset(2);
                error = SwiftMarshal.MarshalFromSwift<Swift.Runtime.ExistentialContainer1>(new IntPtr(enumCopy + (int)offset2));
                
                return true;
            }
            
            private static TupleTypeMetadata* _tupleMetadata_ProcessingFailed;
            
            private static unsafe TupleTypeMetadata* GetTupleMetadata_ProcessingFailed()
            {
                if (_tupleMetadata_ProcessingFailed != null)
                    return _tupleMetadata_ProcessingFailed;
                
                // Build tuple metadata from element types
                var elementMetadataArray = new TypeMetadata[3];
                elementMetadataArray[0] = TypeMetadata.GetExistentialTypeMetadata(1);
                elementMetadataArray[1] = SwiftObjectHelper<Swift.Nuke.ImageProcessingContext>.GetTypeMetadata();
                elementMetadataArray[2] = TypeMetadata.GetExistentialTypeMetadata(1);
                
                // Get tuple type metadata from Swift runtime
                var tupleMetadata = TypeMetadata.GetTupleTypeMetadataFromElements(elementMetadataArray);
                
                _tupleMetadata_ProcessingFailed = tupleMetadata.AsTupleMetadata();
                return _tupleMetadata_ProcessingFailed;
            }
            
            
            private unsafe Swift.SwiftString Description_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_08E29031(self);
                    
                    unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5ErrorO11descriptionSSvg")]
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_08E29031( SwiftSelf self);
            
            public string Description
            {
                get => Description_Get().ToString();
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5ErrorOMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new Error(handle);
            }
            
            Error()
            {
            }
            
            Error(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<Error>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<Error>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static Error()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type Error and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
        }
        
        
        public unsafe class CacheInfo : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<CacheInfo>.GetTypeMetadata().Size;
            SwiftSafeHandle<CacheInfo> _payload = SwiftSafeHandle<CacheInfo>.Zero;
            
            internal SwiftSafeHandle<CacheInfo> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheVMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new CacheInfo(handle);
            }
            
            CacheInfo(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<CacheInfo>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<CacheInfo>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static CacheInfo()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type Cache and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
            
            public unsafe class Caches : ISwiftObject, IEquatable<Caches>
            {
                private unsafe nint RawValue_Get()
                {
                    var success = false;
                    _payload.DangerousAddRef(ref success);
                    try
                    {
                        var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                        
                        
                        
                        var result = PInvoke_rawValue_Get_5DA69036(self);
                        
                        return result;
                    }
                    
                    finally
                    {
                        if (success)
                           _payload.DangerousRelease();
                    }
                    
                }
                
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV6CachesV8rawValueSivg")]
                private static extern nint PInvoke_rawValue_Get_5DA69036( SwiftSelf self);
                
                public nint RawValue
                {
                    get => RawValue_Get();
                }
                
                private static unsafe Swift.Nuke.ImagePipeline.CacheInfo.Caches Memory_Get()
                {
                    try
                    {
                        var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.CacheInfo.Caches>();
                        var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                        var swiftIndirectResult = new SwiftIndirectResult(payload);
                        
                        
                        
                        PInvoke_memory_Get_7CA33BF4(swiftIndirectResult);
                        
                        return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.CacheInfo.Caches>(new IntPtr(swiftIndirectResult.Value));
                    }
                    
                    finally
                    {
                    }
                    
                }
                
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV6CachesV6memoryAGvgZ")]
                private static extern void PInvoke_memory_Get_7CA33BF4( SwiftIndirectResult swiftIndirectResult);
                
                public static Swift.Nuke.ImagePipeline.CacheInfo.Caches Memory
                {
                    get => Memory_Get();
                }
                
                private static unsafe Swift.Nuke.ImagePipeline.CacheInfo.Caches Disk_Get()
                {
                    try
                    {
                        var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.CacheInfo.Caches>();
                        var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                        var swiftIndirectResult = new SwiftIndirectResult(payload);
                        
                        
                        
                        PInvoke_disk_Get_64131BEA(swiftIndirectResult);
                        
                        return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.CacheInfo.Caches>(new IntPtr(swiftIndirectResult.Value));
                    }
                    
                    finally
                    {
                    }
                    
                }
                
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV6CachesV4diskAGvgZ")]
                private static extern void PInvoke_disk_Get_64131BEA( SwiftIndirectResult swiftIndirectResult);
                
                public static Swift.Nuke.ImagePipeline.CacheInfo.Caches Disk
                {
                    get => Disk_Get();
                }
                
                private static unsafe Swift.Nuke.ImagePipeline.CacheInfo.Caches All_Get()
                {
                    try
                    {
                        var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.CacheInfo.Caches>();
                        var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                        var swiftIndirectResult = new SwiftIndirectResult(payload);
                        
                        
                        
                        PInvoke_all_Get_1035551C(swiftIndirectResult);
                        
                        return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.CacheInfo.Caches>(new IntPtr(swiftIndirectResult.Value));
                    }
                    
                    finally
                    {
                    }
                    
                }
                
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV6CachesV3allAGvgZ")]
                private static extern void PInvoke_all_Get_1035551C( SwiftIndirectResult swiftIndirectResult);
                
                public static Swift.Nuke.ImagePipeline.CacheInfo.Caches All
                {
                    get => All_Get();
                }
                
                static nuint _payloadSize = SwiftObjectHelper<Caches>.GetTypeMetadata().Size;
                SwiftSafeHandle<Caches> _payload = SwiftSafeHandle<Caches>.Zero;
                
                internal SwiftSafeHandle<Caches> Payload => _payload;
                
                public void Dispose() => _payload.Dispose();
                
                public override bool Equals(object? obj)
                {
                    return obj is Caches other && Swift.Runtime.SwiftEquatable.Equals(this, other);
                }
                public override int GetHashCode()
                {
                    // TODO: Implement when Swift Hashable protocol binding is supported.
                    // Returning constant 0 satisfies the Equals/GetHashCode contract
                    // (equal objects must have equal hashes). This is correct but makes
                    // hash-based collections O(n) until Hashable is supported.
                    return 0;
                }
                
                public static bool operator ==(Caches left, Caches right)
                {
                    return Swift.Runtime.SwiftEquatable.Equals(left, right);
                }
                
                public static bool operator !=(Caches left, Caches right)
                {
                    return !Swift.Runtime.SwiftEquatable.Equals(left, right);
                }
                
                public bool Equals(Caches? other)
                {
                    return Swift.Runtime.SwiftEquatable.Equals(this, other);
                }
                
                static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
                
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV6CachesVMa")]
                internal static extern TypeMetadata PInvoke_getMetadata();
                
                static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                {
                    return new Caches(handle);
                }
                
                Caches(SwiftHandle handle)
                {
                    _payload = new SwiftSafeHandle<Caches>(handle);
                }
                
                unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                {
                    var metadata = SwiftObjectHelper<Caches>.GetTypeMetadata();
                    if ((int)metadata.Size > swiftDestSpan.Length)
                    {
                        throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                    }
                    fixed (void* swiftDest = swiftDestSpan)
                    {
                        // Ensure that the instance is valid before making copy
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
                
                private static Dictionary<Type, string> _protocolConformanceSymbols;
                static Caches()
                {
                    _protocolConformanceSymbols = new Dictionary<Type, string>
                    {
                        {typeof(IEquatable<Caches>), "$s4Nuke13ImagePipelineC5CacheV6CachesVSQAAMc"}
                    };
                }
                
                static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                    where TProtocol : class
                {
                    if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                    {
                        throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type Caches and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                    }
                    return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
                }
                
                
                public unsafe Caches( nint rawValue)
                {
                    _payload = new SwiftSafeHandle<Caches>((IntPtr)NativeMemory.Alloc(_payloadSize));
                    var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                    
                    PInvoke_init_7CE1F9B2(swiftIndirectResult, rawValue);
                    
                }
                
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV6CachesV8rawValueAGSi_tcfC")]
                private static extern void PInvoke_init_7CE1F9B2( SwiftIndirectResult swiftIndirectResult,  nint rawValue);
                
                
            }
            
            
            public unsafe Swift.Nuke.ImageContainer? CachedImage( Swift.Nuke.ImageRequest _for,  Swift.Nuke.ImagePipeline.CacheInfo.Caches caches)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_cachedImage_3D8CF64C(_for.Payload, caches.Payload, self);
                    
                    var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Nuke.ImageContainer>>(new IntPtr(&result));
                    return swiftResult.ToNullable();
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV06cachedB03for6cachesAA0B9ContainerVSgAA0B7RequestV_AE6CachesVtF")]
            private static extern IntPtr PInvoke_cachedImage_3D8CF64C( SafeHandle _for,  SafeHandle caches,  SwiftSelf self);
            public unsafe Swift.Nuke.ImageContainer? CachedImage( Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_cachedImage_5B094894(_for.Payload, self);
                    
                    var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Nuke.ImageContainer>>(new IntPtr(&result));
                    return swiftResult.ToNullable();
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("SwiftBindings", EntryPoint = "DBW_Cache_cachedImage_FF81D467_1")]
            private static extern IntPtr PInvoke_cachedImage_5B094894( SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe void StoreCachedImage( Swift.Nuke.ImageContainer arg0,  Swift.Nuke.ImageRequest _for,  Swift.Nuke.ImagePipeline.CacheInfo.Caches caches)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_storeCachedImage_48907A65(arg0.Payload, _for.Payload, caches.Payload, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV011storeCachedB0_3for6cachesyAA0B9ContainerV_AA0B7RequestVAE6CachesVtF")]
            private static extern void PInvoke_storeCachedImage_48907A65( SafeHandle arg0,  SafeHandle _for,  SafeHandle caches,  SwiftSelf self);
            public unsafe void StoreCachedImage( Swift.Nuke.ImageContainer arg0,  Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_storeCachedImage_48583EE8(arg0.Payload, _for.Payload, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("SwiftBindings", EntryPoint = "DBW_Cache_storeCachedImage_B14B9B8A_1")]
            private static extern void PInvoke_storeCachedImage_48583EE8( SafeHandle arg0,  SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe void RemoveCachedImage( Swift.Nuke.ImageRequest _for,  Swift.Nuke.ImagePipeline.CacheInfo.Caches caches)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_removeCachedImage_36A45D0B(_for.Payload, caches.Payload, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV012removeCachedB03for6cachesyAA0B7RequestV_AE6CachesVtF")]
            private static extern void PInvoke_removeCachedImage_36A45D0B( SafeHandle _for,  SafeHandle caches,  SwiftSelf self);
            public unsafe void RemoveCachedImage( Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_removeCachedImage_43D5F6D8(_for.Payload, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("SwiftBindings", EntryPoint = "DBW_Cache_removeCachedImage_6F11C1F7_1")]
            private static extern void PInvoke_removeCachedImage_43D5F6D8( SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe System.Boolean ContainsCachedImage( Swift.Nuke.ImageRequest _for,  Swift.Nuke.ImagePipeline.CacheInfo.Caches caches)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_containsCachedImage_61B2A81B(_for.Payload, caches.Payload, self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV014containsCachedB03for6cachesSbAA0B7RequestV_AE6CachesVtF")]
            private static extern System.Boolean PInvoke_containsCachedImage_61B2A81B( SafeHandle _for,  SafeHandle caches,  SwiftSelf self);
            public unsafe System.Boolean ContainsCachedImage( Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_containsCachedImage_0038C30C(_for.Payload, self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("SwiftBindings", EntryPoint = "DBW_Cache_containsCachedImage_5B644858_1")]
            private static extern System.Boolean PInvoke_containsCachedImage_0038C30C( SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe Swift.Data? CachedData( Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_cachedData_16B12179(_for.Payload, self);
                    
                    var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Data>>(new IntPtr(&result));
                    return swiftResult.ToNullable();
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV10cachedData3for10Foundation0F0VSgAA0B7RequestV_tF")]
            private static extern IntPtr PInvoke_cachedData_16B12179( SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe void StoreCachedData( Foundation.NSData arg0,  Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    var arg0Swift = Swift.Data.FromNSData(arg0);
                    
                    PInvoke_storeCachedData_63130A01(arg0Swift, _for.Payload, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV15storeCachedData_3fory10Foundation0G0V_AA0B7RequestVtF")]
            private static extern void PInvoke_storeCachedData_63130A01( Swift.Data arg0,  SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe System.Boolean ContainsData( Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_containsData_06D3B3A7(_for.Payload, self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV12containsData3forSbAA0B7RequestV_tF")]
            private static extern System.Boolean PInvoke_containsData_06D3B3A7( SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe void RemoveCachedData( Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_removeCachedData_52FFF490(_for.Payload, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV16removeCachedData3foryAA0B7RequestV_tF")]
            private static extern void PInvoke_removeCachedData_52FFF490( SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe Swift.Nuke.ImageCacheKey MakeImageCacheKey( Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageCacheKey>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_makeImageCacheKey_4C23CC08(swiftIndirectResult, _for.Payload, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageCacheKey>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV04makebD3Key3forAA0bdF0VAA0B7RequestV_tF")]
            private static extern void PInvoke_makeImageCacheKey_4C23CC08( SwiftIndirectResult swiftIndirectResult,  SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe string MakeDataCacheKey( Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_makeDataCacheKey_6D35BEAF(_for.Payload, self);
                    
                    unsafe {
                        var swiftResult = SwiftMarshal.MarshalFromSwift<SwiftString>(new IntPtr(&result));
                        return swiftResult.ToString();
                    }
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV08makeDataD3Key3forSSAA0B7RequestV_tF")]
            private static extern Swift.SwiftString.Buffer PInvoke_makeDataCacheKey_6D35BEAF( SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe void RemoveAll( Swift.Nuke.ImagePipeline.CacheInfo.Caches caches)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_removeAll_41C92FE6(caches.Payload, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV9removeAll6cachesyAE6CachesV_tF")]
            private static extern void PInvoke_removeAll_41C92FE6( SafeHandle caches,  SwiftSelf self);
            public unsafe void RemoveAll()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_removeAll_7E0D55C6(self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("SwiftBindings", EntryPoint = "DBW_Cache_removeAll_00813104_1")]
            private static extern void PInvoke_removeAll_7E0D55C6( SwiftSelf self);
            
            
        }
        
        
        public unsafe class ConfigurationInfo : ISwiftObject
        {
            private unsafe IDataLoading DataLoader_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_dataLoader_Get_3CE3F073(self);
                    
                    return new DataLoadingProxy(result);
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV10dataLoaderAA11DataLoading_pvg")]
            private static extern Swift.Runtime.ExistentialContainer1 PInvoke_dataLoader_Get_3CE3F073( SwiftSelf self);
            
            private unsafe void DataLoader_Set( IDataLoading value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_dataLoader_Set_2D29EEA0(((Swift.Runtime.ISwiftExistentialConvertible<Swift.Runtime.ExistentialContainer1>)value).GetExistentialContainer(), self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV10dataLoaderAA11DataLoading_pvs")]
            private static extern void PInvoke_dataLoader_Set_2D29EEA0( Swift.Runtime.ExistentialContainer1 value,  SwiftSelf self);
            
            [global::Swift.UnsupportedSwiftType("Existential type fallback", "any Nuke.DataLoading")]
            public IDataLoading DataLoader
            {
                get => DataLoader_Get();
                set => DataLoader_Set(value);
            }
            
            private unsafe Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1> DataCache_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_dataCache_Get_37B95912(self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1>>(new IntPtr(&result));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV9dataCacheAA11DataCaching_pSgvg")]
            private static extern IntPtr PInvoke_dataCache_Get_37B95912( SwiftSelf self);
            
            private unsafe void DataCache_Set( Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1> value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                    IntPtr valueBuffer = valueDisposable.Buffer;
                    
                    PInvoke_dataCache_Set_1AF17499(valueBuffer, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV9dataCacheAA11DataCaching_pSgvs")]
            private static extern void PInvoke_dataCache_Set_1AF17499( IntPtr valueBuffer,  SwiftSelf self);
            
            [global::Swift.UnsupportedSwiftType("Existential type fallback", "any Nuke.DataCaching")]
            public IDataCaching? DataCache
            {
                get => ((Swift.AnyType?)DataCache_Get());
                set => DataCache_Set((value is {} valueVal ? SwiftOptional<Swift.AnyType>.NewSome(valueVal) : SwiftOptional<Swift.AnyType>.NewNone()));
            }
            
            private unsafe Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1> ImageCache_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_imageCache_Get_5602987F(self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1>>(new IntPtr(&result));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV10imageCacheAA0B7Caching_pSgvg")]
            private static extern IntPtr PInvoke_imageCache_Get_5602987F( SwiftSelf self);
            
            private unsafe void ImageCache_Set( Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1> value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                    IntPtr valueBuffer = valueDisposable.Buffer;
                    
                    PInvoke_imageCache_Set_5793886F(valueBuffer, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV10imageCacheAA0B7Caching_pSgvs")]
            private static extern void PInvoke_imageCache_Set_5793886F( IntPtr valueBuffer,  SwiftSelf self);
            
            [global::Swift.UnsupportedSwiftType("Existential type fallback", "any Nuke.ImageCaching")]
            public IImageCaching? ImageCache
            {
                get => ((Swift.AnyType?)ImageCache_Get());
                set => ImageCache_Set((value is {} valueVal ? SwiftOptional<Swift.AnyType>.NewSome(valueVal) : SwiftOptional<Swift.AnyType>.NewNone()));
            }
            
            private unsafe Func<Swift.Nuke.ImageEncodingContext, Swift.Runtime.ExistentialContainer1> MakeImageEncoder_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_makeImageEncoder_Get_333C81FD(self);
                    
                    // Wrap Swift closure in SwiftEscapingClosure for ARC management
                    var _closureWrapper = SwiftEscapingClosure<Func<Swift.Nuke.ImageEncodingContext, Swift.Runtime.ExistentialContainer1>>.FromSwift(result.FunctionPointer, result.Context);
                    // Create invoker delegate that captures wrapper (keeps it alive for proper ARC)
                    Func<Swift.Nuke.ImageEncodingContext, Swift.Runtime.ExistentialContainer1> _invoker = _arg0 =>
                    {
                        unsafe
                        {
                            var _fp = (delegate* unmanaged[Swift]<void*, SwiftSelf, Swift.Runtime.ExistentialContainer1>)_closureWrapper.FunctionPointer;
                            var _swiftSelf = new SwiftSelf((void*)_closureWrapper.Context.ToPointer());
                                // Non-frozen struct: allocate on heap, initialize, and clean up after call
                                var _arg0Metadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageEncodingContext>();
                                byte* _arg0Buffer = (byte*)NativeMemory.Alloc((nuint)_arg0Metadata.Size, (nuint)_arg0Metadata.Stride);
                                _arg0Metadata.ValueWitnessTable->InitializeWithCopy(
                                    (void*)_arg0Buffer,
                                    (void*)_arg0.Payload.DangerousGetHandle(),
                                    _arg0Metadata);
                                try
                                {
                                    return _fp(_arg0Buffer, _swiftSelf);
                                }
                                finally
                                {
                                    _arg0Metadata.ValueWitnessTable->Destroy((void*)_arg0Buffer, _arg0Metadata);
                                    NativeMemory.Free(_arg0Buffer);
                                }
                        }
                    };
                    return _invoker;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV04makeB7EncoderyAA0B8Encoding_pAA0bG7ContextVYbcvg")]
            private static extern SwiftClosureData PInvoke_makeImageEncoder_Get_333C81FD( SwiftSelf self);
            
            private static unsafe readonly delegate* unmanaged[Swift]<void*, SwiftSelf, Swift.Runtime.ExistentialContainer1> s_makeImageEncoder_Set_value_0C5AFB01_Callback = &makeImageEncoder_Set_value_0C5AFB01_Callback;
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
            private static Swift.Runtime.ExistentialContainer1 makeImageEncoder_Set_value_0C5AFB01_Callback(void* arg0, SwiftSelf context)
            {
                var del = SwiftClosureMarshaller.GetDelegateFromContext<Func<Swift.Nuke.ImageEncodingContext, Swift.Runtime.ExistentialContainer1>>(new IntPtr(context.Value));
                return del(SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageEncodingContext>(new IntPtr(arg0)));
            }
            
            private unsafe void MakeImageEncoder_Set( Func<Swift.Nuke.ImageEncodingContext, Swift.Runtime.ExistentialContainer1> value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                GCHandle valueHandle = default;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    valueHandle = GCHandle.Alloc(value);
                    var valueClosure = new SwiftClosureData((IntPtr)s_makeImageEncoder_Set_value_0C5AFB01_Callback, GCHandle.ToIntPtr(valueHandle));
                    
                    PInvoke_makeImageEncoder_Set_0C5AFB01(valueClosure, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                    if (valueHandle.IsAllocated) valueHandle.Free();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV04makeB7EncoderyAA0B8Encoding_pAA0bG7ContextVYbcvs")]
            private static extern void PInvoke_makeImageEncoder_Set_0C5AFB01( SwiftClosureData value,  SwiftSelf self);
            
            [global::Swift.UnsupportedSwiftType("Existential type fallback", "any Nuke.ImageEncoding")]
            public Func<Swift.Nuke.ImageEncodingContext, Swift.Runtime.ExistentialContainer1> MakeImageEncoder
            {
                get => MakeImageEncoder_Get();
                set => MakeImageEncoder_Set(value);
            }
            
            private unsafe System.Boolean IsDecompressionEnabled_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isDecompressionEnabled_Get_51DA4B98(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV22isDecompressionEnabledSbvg")]
            private static extern System.Boolean PInvoke_isDecompressionEnabled_Get_51DA4B98( SwiftSelf self);
            
            private unsafe void IsDecompressionEnabled_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isDecompressionEnabled_Set_21BDF081(value, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV22isDecompressionEnabledSbvs")]
            private static extern void PInvoke_isDecompressionEnabled_Set_21BDF081( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean IsDecompressionEnabled
            {
                get => IsDecompressionEnabled_Get();
                set => IsDecompressionEnabled_Set(value);
            }
            
            private unsafe System.Boolean IsUsingPrepareForDisplay_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isUsingPrepareForDisplay_Get_5729245A(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV24isUsingPrepareForDisplaySbvg")]
            private static extern System.Boolean PInvoke_isUsingPrepareForDisplay_Get_5729245A( SwiftSelf self);
            
            private unsafe void IsUsingPrepareForDisplay_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isUsingPrepareForDisplay_Set_5E4B5AA4(value, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV24isUsingPrepareForDisplaySbvs")]
            private static extern void PInvoke_isUsingPrepareForDisplay_Set_5E4B5AA4( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean IsUsingPrepareForDisplay
            {
                get => IsUsingPrepareForDisplay_Get();
                set => IsUsingPrepareForDisplay_Set(value);
            }
            
            private unsafe Swift.Nuke.ImagePipeline.DataCachePolicy DataCachePolicy_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.DataCachePolicy>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_dataCachePolicy_Get_58280C39(swiftIndirectResult, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.DataCachePolicy>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV15dataCachePolicyAC04DatafG0Ovg")]
            private static extern void PInvoke_dataCachePolicy_Get_58280C39( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void DataCachePolicy_Set( Swift.Nuke.ImagePipeline.DataCachePolicy value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_dataCachePolicy_Set_1ED4ECE2(value.Payload.DangerousGetHandle(), self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV15dataCachePolicyAC04DatafG0Ovs")]
            private static extern void PInvoke_dataCachePolicy_Set_1ED4ECE2( IntPtr value,  SwiftSelf self);
            
            public Swift.Nuke.ImagePipeline.DataCachePolicy DataCachePolicy
            {
                get => DataCachePolicy_Get();
                set => DataCachePolicy_Set(value);
            }
            
            private unsafe System.Boolean IsTaskCoalescingEnabled_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isTaskCoalescingEnabled_Get_23C12C66(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV23isTaskCoalescingEnabledSbvg")]
            private static extern System.Boolean PInvoke_isTaskCoalescingEnabled_Get_23C12C66( SwiftSelf self);
            
            private unsafe void IsTaskCoalescingEnabled_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isTaskCoalescingEnabled_Set_182784EE(value, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV23isTaskCoalescingEnabledSbvs")]
            private static extern void PInvoke_isTaskCoalescingEnabled_Set_182784EE( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean IsTaskCoalescingEnabled
            {
                get => IsTaskCoalescingEnabled_Get();
                set => IsTaskCoalescingEnabled_Set(value);
            }
            
            private unsafe System.Boolean IsRateLimiterEnabled_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isRateLimiterEnabled_Get_3BCBEFC6(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV20isRateLimiterEnabledSbvg")]
            private static extern System.Boolean PInvoke_isRateLimiterEnabled_Get_3BCBEFC6( SwiftSelf self);
            
            private unsafe void IsRateLimiterEnabled_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isRateLimiterEnabled_Set_22237F3E(value, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV20isRateLimiterEnabledSbvs")]
            private static extern void PInvoke_isRateLimiterEnabled_Set_22237F3E( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean IsRateLimiterEnabled
            {
                get => IsRateLimiterEnabled_Get();
                set => IsRateLimiterEnabled_Set(value);
            }
            
            private unsafe System.Boolean IsProgressiveDecodingEnabled_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isProgressiveDecodingEnabled_Get_75DE74E6(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV28isProgressiveDecodingEnabledSbvg")]
            private static extern System.Boolean PInvoke_isProgressiveDecodingEnabled_Get_75DE74E6( SwiftSelf self);
            
            private unsafe void IsProgressiveDecodingEnabled_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isProgressiveDecodingEnabled_Set_2A0E7FA8(value, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV28isProgressiveDecodingEnabledSbvs")]
            private static extern void PInvoke_isProgressiveDecodingEnabled_Set_2A0E7FA8( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean IsProgressiveDecodingEnabled
            {
                get => IsProgressiveDecodingEnabled_Get();
                set => IsProgressiveDecodingEnabled_Set(value);
            }
            
            private unsafe System.Boolean IsStoringPreviewsInMemoryCache_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isStoringPreviewsInMemoryCache_Get_0B571575(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV30isStoringPreviewsInMemoryCacheSbvg")]
            private static extern System.Boolean PInvoke_isStoringPreviewsInMemoryCache_Get_0B571575( SwiftSelf self);
            
            private unsafe void IsStoringPreviewsInMemoryCache_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isStoringPreviewsInMemoryCache_Set_3D456A0A(value, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV30isStoringPreviewsInMemoryCacheSbvs")]
            private static extern void PInvoke_isStoringPreviewsInMemoryCache_Set_3D456A0A( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean IsStoringPreviewsInMemoryCache
            {
                get => IsStoringPreviewsInMemoryCache_Get();
                set => IsStoringPreviewsInMemoryCache_Set(value);
            }
            
            private unsafe System.Boolean IsResumableDataEnabled_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isResumableDataEnabled_Get_5AB47380(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV22isResumableDataEnabledSbvg")]
            private static extern System.Boolean PInvoke_isResumableDataEnabled_Get_5AB47380( SwiftSelf self);
            
            private unsafe void IsResumableDataEnabled_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isResumableDataEnabled_Set_0A74A9DC(value, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV22isResumableDataEnabledSbvs")]
            private static extern void PInvoke_isResumableDataEnabled_Set_0A74A9DC( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean IsResumableDataEnabled
            {
                get => IsResumableDataEnabled_Get();
                set => IsResumableDataEnabled_Set(value);
            }
            
            private unsafe System.Boolean IsLocalResourcesSupportEnabled_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isLocalResourcesSupportEnabled_Get_15A51F32(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV30isLocalResourcesSupportEnabledSbvg")]
            private static extern System.Boolean PInvoke_isLocalResourcesSupportEnabled_Get_15A51F32( SwiftSelf self);
            
            private unsafe void IsLocalResourcesSupportEnabled_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isLocalResourcesSupportEnabled_Set_7DE1038F(value, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV30isLocalResourcesSupportEnabledSbvs")]
            private static extern void PInvoke_isLocalResourcesSupportEnabled_Set_7DE1038F( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean IsLocalResourcesSupportEnabled
            {
                get => IsLocalResourcesSupportEnabled_Get();
                set => IsLocalResourcesSupportEnabled_Set(value);
            }
            
            private unsafe Swift.DispatchQueue CallbackQueue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.DispatchQueue>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_callbackQueue_Get_7A200945(swiftIndirectResult, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.DispatchQueue>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV13callbackQueueSo17OS_dispatch_queueCvg")]
            private static extern void PInvoke_callbackQueue_Get_7A200945( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void CallbackQueue_Set( Swift.DispatchQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_callbackQueue_Set_0F13F650(value.Payload, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV13callbackQueueSo17OS_dispatch_queueCvs")]
            private static extern void PInvoke_callbackQueue_Set_0F13F650( SafeHandle value,  SwiftSelf self);
            
            public Swift.DispatchQueue CallbackQueue
            {
                get => CallbackQueue_Get();
                set => CallbackQueue_Set(value);
            }
            
            private static System.Boolean IsSignpostLoggingEnabled_Get()
            {
                try
                {
                    
                    
                    var result = PInvoke_isSignpostLoggingEnabled_Get_5C452987();
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV24isSignpostLoggingEnabledSbvgZ")]
            private static extern System.Boolean PInvoke_isSignpostLoggingEnabled_Get_5C452987();
            
            private static void IsSignpostLoggingEnabled_Set( System.Boolean value)
            {
                try
                {
                    
                    
                    PInvoke_isSignpostLoggingEnabled_Set_0968DBD9(value);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV24isSignpostLoggingEnabledSbvsZ")]
            private static extern void PInvoke_isSignpostLoggingEnabled_Set_0968DBD9( System.Boolean value);
            
            public static System.Boolean IsSignpostLoggingEnabled
            {
                get => IsSignpostLoggingEnabled_Get();
                set => IsSignpostLoggingEnabled_Set(value);
            }
            
            private unsafe Foundation.NSOperationQueue DataLoadingQueue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Foundation.NSOperationQueue>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_dataLoadingQueue_Get_55258BEF(swiftIndirectResult, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Foundation.NSOperationQueue>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV16dataLoadingQueueSo011NSOperationG0Cvg")]
            private static extern void PInvoke_dataLoadingQueue_Get_55258BEF( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void DataLoadingQueue_Set( Foundation.NSOperationQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr valueHandle = value?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_dataLoadingQueue_Set_47C5E236(valueHandle, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV16dataLoadingQueueSo011NSOperationG0Cvs")]
            private static extern void PInvoke_dataLoadingQueue_Set_47C5E236( IntPtr value,  SwiftSelf self);
            
            public Foundation.NSOperationQueue DataLoadingQueue
            {
                get => DataLoadingQueue_Get();
                set => DataLoadingQueue_Set(value);
            }
            
            private unsafe Foundation.NSOperationQueue DataCachingQueue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Foundation.NSOperationQueue>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_dataCachingQueue_Get_2785E8C9(swiftIndirectResult, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Foundation.NSOperationQueue>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV16dataCachingQueueSo011NSOperationG0Cvg")]
            private static extern void PInvoke_dataCachingQueue_Get_2785E8C9( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void DataCachingQueue_Set( Foundation.NSOperationQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr valueHandle = value?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_dataCachingQueue_Set_65B9E446(valueHandle, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV16dataCachingQueueSo011NSOperationG0Cvs")]
            private static extern void PInvoke_dataCachingQueue_Set_65B9E446( IntPtr value,  SwiftSelf self);
            
            public Foundation.NSOperationQueue DataCachingQueue
            {
                get => DataCachingQueue_Get();
                set => DataCachingQueue_Set(value);
            }
            
            private unsafe Foundation.NSOperationQueue ImageDecodingQueue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Foundation.NSOperationQueue>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_imageDecodingQueue_Get_7E974101(swiftIndirectResult, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Foundation.NSOperationQueue>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV18imageDecodingQueueSo011NSOperationG0Cvg")]
            private static extern void PInvoke_imageDecodingQueue_Get_7E974101( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void ImageDecodingQueue_Set( Foundation.NSOperationQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr valueHandle = value?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_imageDecodingQueue_Set_4D184674(valueHandle, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV18imageDecodingQueueSo011NSOperationG0Cvs")]
            private static extern void PInvoke_imageDecodingQueue_Set_4D184674( IntPtr value,  SwiftSelf self);
            
            public Foundation.NSOperationQueue ImageDecodingQueue
            {
                get => ImageDecodingQueue_Get();
                set => ImageDecodingQueue_Set(value);
            }
            
            private unsafe Foundation.NSOperationQueue ImageEncodingQueue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Foundation.NSOperationQueue>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_imageEncodingQueue_Get_13BE95F5(swiftIndirectResult, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Foundation.NSOperationQueue>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV18imageEncodingQueueSo011NSOperationG0Cvg")]
            private static extern void PInvoke_imageEncodingQueue_Get_13BE95F5( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void ImageEncodingQueue_Set( Foundation.NSOperationQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr valueHandle = value?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_imageEncodingQueue_Set_7D73F639(valueHandle, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV18imageEncodingQueueSo011NSOperationG0Cvs")]
            private static extern void PInvoke_imageEncodingQueue_Set_7D73F639( IntPtr value,  SwiftSelf self);
            
            public Foundation.NSOperationQueue ImageEncodingQueue
            {
                get => ImageEncodingQueue_Get();
                set => ImageEncodingQueue_Set(value);
            }
            
            private unsafe Foundation.NSOperationQueue ImageProcessingQueue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Foundation.NSOperationQueue>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_imageProcessingQueue_Get_42FA4505(swiftIndirectResult, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Foundation.NSOperationQueue>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV20imageProcessingQueueSo011NSOperationG0Cvg")]
            private static extern void PInvoke_imageProcessingQueue_Get_42FA4505( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void ImageProcessingQueue_Set( Foundation.NSOperationQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr valueHandle = value?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_imageProcessingQueue_Set_186D4A83(valueHandle, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV20imageProcessingQueueSo011NSOperationG0Cvs")]
            private static extern void PInvoke_imageProcessingQueue_Set_186D4A83( IntPtr value,  SwiftSelf self);
            
            public Foundation.NSOperationQueue ImageProcessingQueue
            {
                get => ImageProcessingQueue_Get();
                set => ImageProcessingQueue_Set(value);
            }
            
            private unsafe Foundation.NSOperationQueue ImageDecompressingQueue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Foundation.NSOperationQueue>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_imageDecompressingQueue_Get_4249BCD1(swiftIndirectResult, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Foundation.NSOperationQueue>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV23imageDecompressingQueueSo011NSOperationG0Cvg")]
            private static extern void PInvoke_imageDecompressingQueue_Get_4249BCD1( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void ImageDecompressingQueue_Set( Foundation.NSOperationQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr valueHandle = value?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_imageDecompressingQueue_Set_78797DC4(valueHandle, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV23imageDecompressingQueueSo011NSOperationG0Cvs")]
            private static extern void PInvoke_imageDecompressingQueue_Set_78797DC4( IntPtr value,  SwiftSelf self);
            
            public Foundation.NSOperationQueue ImageDecompressingQueue
            {
                get => ImageDecompressingQueue_Get();
                set => ImageDecompressingQueue_Set(value);
            }
            
            private static unsafe Swift.Nuke.ImagePipeline.ConfigurationInfo WithURLCache_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.ConfigurationInfo>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_withURLCache_Get_46CF31D7(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.ConfigurationInfo>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV12withURLCacheAEvgZ")]
            private static extern void PInvoke_withURLCache_Get_46CF31D7( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImagePipeline.ConfigurationInfo WithURLCache
            {
                get => WithURLCache_Get();
            }
            
            private static unsafe Swift.Nuke.ImagePipeline.ConfigurationInfo WithDataCache_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.ConfigurationInfo>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_withDataCache_Get_3D0B03E8(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.ConfigurationInfo>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV13withDataCacheAEvgZ")]
            private static extern void PInvoke_withDataCache_Get_3D0B03E8( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImagePipeline.ConfigurationInfo WithDataCache
            {
                get => WithDataCache_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<ConfigurationInfo>.GetTypeMetadata().Size;
            SwiftSafeHandle<ConfigurationInfo> _payload = SwiftSafeHandle<ConfigurationInfo>.Zero;
            
            internal SwiftSafeHandle<ConfigurationInfo> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationVMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new ConfigurationInfo(handle);
            }
            
            ConfigurationInfo(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<ConfigurationInfo>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<ConfigurationInfo>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static ConfigurationInfo()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type Configuration and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
            
            public unsafe ConfigurationInfo( IDataLoading dataLoader)
            {
                _payload = new SwiftSafeHandle<ConfigurationInfo>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_473092CD(swiftIndirectResult, ((Swift.Runtime.ISwiftExistentialConvertible<Swift.Runtime.ExistentialContainer1>)dataLoader).GetExistentialContainer());
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV10dataLoaderAeA11DataLoading_p_tcfC")]
            private static extern void PInvoke_init_473092CD( SwiftIndirectResult swiftIndirectResult,  Swift.Runtime.ExistentialContainer1 dataLoader);
            public unsafe ConfigurationInfo()
            {
                _payload = new SwiftSafeHandle<ConfigurationInfo>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_15532545(swiftIndirectResult);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("SwiftBindings", EntryPoint = "DBW_Configuration_init_6F4D1A10_1")]
            private static extern void PInvoke_init_15532545( SwiftIndirectResult swiftIndirectResult);
            
            
            public static unsafe Swift.Nuke.ImagePipeline.ConfigurationInfo WithDataCacheMethod( string name,  nint? sizeLimit)
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.ConfigurationInfo>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    using var nameSwift = new SwiftString(name);
                    using PayloadBuffer<SwiftString.Buffer> nameDisposable = nameSwift.PayloadBuffer;
                    using var sizeLimitSwift = sizeLimit is {} sizeLimitValue ? SwiftOptional<nint>.NewSome(sizeLimitValue) : SwiftOptional<nint>.NewNone();
                    using PayloadBuffer<IntPtr> sizeLimitDisposable = sizeLimitSwift.PayloadBuffer;
                    IntPtr sizeLimitBuffer = sizeLimitDisposable.Buffer;
                    
                    PInvoke_withDataCache_53810356(swiftIndirectResult, nameDisposable.Buffer, sizeLimitBuffer);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.ConfigurationInfo>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV13withDataCache4name9sizeLimitAESS_SiSgtFZ")]
            private static extern void PInvoke_withDataCache_53810356( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer name,  IntPtr sizeLimitBuffer);
            public static unsafe Swift.Nuke.ImagePipeline.ConfigurationInfo WithDataCacheMethod()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.ConfigurationInfo>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_withDataCache_69490A47(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.ConfigurationInfo>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("SwiftBindings", EntryPoint = "DBW_Configuration_withDataCache_75148080_2")]
            private static extern void PInvoke_withDataCache_69490A47( SwiftIndirectResult swiftIndirectResult);
            public static unsafe Swift.Nuke.ImagePipeline.ConfigurationInfo WithDataCacheMethod( string name)
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.ConfigurationInfo>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    using var nameSwift = new SwiftString(name);
                    using PayloadBuffer<SwiftString.Buffer> nameDisposable = nameSwift.PayloadBuffer;
                    
                    PInvoke_withDataCache_2F523812(swiftIndirectResult, nameDisposable.Buffer);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.ConfigurationInfo>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("SwiftBindings", EntryPoint = "DBW_Configuration_withDataCache_75148080_1")]
            private static extern void PInvoke_withDataCache_2F523812( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer name);
            
            
        }
        
        
        public unsafe class DataCachePolicy : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<DataCachePolicy>.GetTypeMetadata().Size;
            SwiftSafeHandle<DataCachePolicy> _payload = SwiftSafeHandle<DataCachePolicy>.Zero;
            internal SwiftSafeHandle<DataCachePolicy> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            /// <summary>
            /// Gets the 'automatic' case of DataCachePolicy.
            /// </summary>
            public static DataCachePolicy Automatic
            {
                get
                {
                    var result = new DataCachePolicy();
                    var metadata = SwiftObjectHelper<DataCachePolicy>.GetTypeMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    metadata.ValueWitnessTable->DestructiveInjectEnumTag((void*)buffer, (uint)0, metadata);
                    result._payload = new SwiftSafeHandle<DataCachePolicy>(buffer);
                    return result;
                }
            }
            
            /// <summary>
            /// Gets the 'storeOriginalData' case of DataCachePolicy.
            /// </summary>
            public static DataCachePolicy StoreOriginalData
            {
                get
                {
                    var result = new DataCachePolicy();
                    var metadata = SwiftObjectHelper<DataCachePolicy>.GetTypeMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    metadata.ValueWitnessTable->DestructiveInjectEnumTag((void*)buffer, (uint)1, metadata);
                    result._payload = new SwiftSafeHandle<DataCachePolicy>(buffer);
                    return result;
                }
            }
            
            /// <summary>
            /// Gets the 'storeEncodedImages' case of DataCachePolicy.
            /// </summary>
            public static DataCachePolicy StoreEncodedImages
            {
                get
                {
                    var result = new DataCachePolicy();
                    var metadata = SwiftObjectHelper<DataCachePolicy>.GetTypeMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    metadata.ValueWitnessTable->DestructiveInjectEnumTag((void*)buffer, (uint)2, metadata);
                    result._payload = new SwiftSafeHandle<DataCachePolicy>(buffer);
                    return result;
                }
            }
            
            /// <summary>
            /// Gets the 'storeAll' case of DataCachePolicy.
            /// </summary>
            public static DataCachePolicy StoreAll
            {
                get
                {
                    var result = new DataCachePolicy();
                    var metadata = SwiftObjectHelper<DataCachePolicy>.GetTypeMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    metadata.ValueWitnessTable->DestructiveInjectEnumTag((void*)buffer, (uint)3, metadata);
                    result._payload = new SwiftSafeHandle<DataCachePolicy>(buffer);
                    return result;
                }
            }
            
            /// <summary>
            /// Enum representing the possible cases of DataCachePolicy.
            /// Tag values follow Swift's ordering: payload cases first, then no-payload cases.
            /// </summary>
            public enum CaseTag : uint
            {
                Automatic = 0,
                StoreOriginalData = 1,
                StoreEncodedImages = 2,
                StoreAll = 3,
            }
            
            /// <summary>
            /// Gets the current case of this enum instance.
            /// </summary>
            public unsafe CaseTag Tag
            {
                get
                {
                    bool success = false;
                    _payload.DangerousAddRef(ref success);
                    try
                    {
                        var metadata = SwiftObjectHelper<DataCachePolicy>.GetTypeMetadata();
                        byte* payload = (byte*)_payload.DangerousGetHandle();
                        return (CaseTag)metadata.ValueWitnessTable->GetEnumTag(payload, metadata);
                    }
                    finally
                    {
                        if (success)
                            _payload.DangerousRelease();
                    }
                }
            }
            
            
            private unsafe nint HashValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_76B899AC(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC15DataCachePolicyO9hashValueSivg")]
            private static extern nint PInvoke_hashValue_Get_76B899AC( SwiftSelf self);
            
            public nint HashValue
            {
                get => HashValue_Get();
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC15DataCachePolicyOMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new DataCachePolicy(handle);
            }
            
            DataCachePolicy()
            {
            }
            
            DataCachePolicy(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<DataCachePolicy>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<DataCachePolicy>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static DataCachePolicy()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IEquatable<DataCachePolicy>), "$s4Nuke13ImagePipelineC15DataCachePolicyOSQAAMc"}
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type DataCachePolicy and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
            public unsafe void Hash(ref Swift.Hasher into)
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_41B88456(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC15DataCachePolicyO4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_41B88456( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        
        
        public unsafe void Invalidate()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_invalidate_6577AA35(self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC10invalidateyyF")]
        private static extern void PInvoke_invalidate_6577AA35( SwiftSelf self);
        
        
        public unsafe Swift.Nuke.ImageTask ImageTask( Foundation.NSUrl with)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                using var withSwift = Swift.URL.FromNSUrl(with);
                
                var result = PInvoke_imageTask_0287878E(withSwift.Payload, self);
                
                var classPayload = NativeMemory.Alloc((nuint)sizeof(IntPtr));
                *(IntPtr*)classPayload = result;
                return (Swift.Nuke.ImageTask)SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageTask>(new IntPtr(classPayload));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC9imageTask4withAA0bE0C10Foundation3URLV_tF")]
        private static extern IntPtr PInvoke_imageTask_0287878E( SafeHandle with,  SwiftSelf self);
        
        
        public unsafe Swift.Nuke.ImageTask ImageTask( Swift.Nuke.ImageRequest with)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_imageTask_5E9E5777(with.Payload, self);
                
                var classPayload = NativeMemory.Alloc((nuint)sizeof(IntPtr));
                *(IntPtr*)classPayload = result;
                return (Swift.Nuke.ImageTask)SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageTask>(new IntPtr(classPayload));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC9imageTask4withAA0bE0CAA0B7RequestV_tF")]
        private static extern IntPtr PInvoke_imageTask_5E9E5777( SafeHandle with,  SwiftSelf self);
        
        
                        [System.Runtime.InteropServices.DllImport("SwiftBindings", EntryPoint = "SBW_Free_Nuke")]
        private static extern void SBW_Free(IntPtr ptr);
private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> s_imageCallback_6B90EC1B = &imageOnComplete_6B90EC1B;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void imageOnComplete_6B90EC1B(IntPtr resultPtr, IntPtr task)
        {
            GCHandle handle = GCHandle.FromIntPtr(task);
            try
            {
                // Read result from pointer (Swift allocated memory and stored the value)
                var result = SwiftMarshal.MarshalFromSwift<UIKit.UIImage>(resultPtr);

                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<UIKit.UIImage> holderTcs)
                {
                    // Free copy buffer memory for non-frozen params and release retained self
                    for (int i = 1; i < holder.Length; i++)
                    {
                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                        {
                            Arc.Release(retained.Ptr);
                        }
                        else if (holder[i] is DeferredSafeHandleRelease deferred)
                        {
                            deferred.Handle.DangerousRelease();
                        }
                        else if (holder[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                        {
                            copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                            NativeMemory.Free((void*)copyBuffer.Buffer);
                        }
                    }
                    holderTcs.TrySetResult(result);
                }
                else if (handle.Target is TaskCompletionSource<UIKit.UIImage> directTcs)
                {
                    directTcs.TrySetResult(result);
                }
            }
            finally
            {
                // Free Swift-allocated memory
                SBW_Free(resultPtr);
                handle.Free();
            }
        }

        private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> s_imageErrorCallback_6B90EC1B = &imageOnError_6B90EC1B;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void imageOnError_6B90EC1B(IntPtr errorMessagePtr, IntPtr task)
        {
            GCHandle handle = GCHandle.FromIntPtr(task);
            try
            {
                var errorMessage = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(errorMessagePtr) ?? "Unknown Swift error";
                var exception = new SwiftException(errorMessage);

                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<UIKit.UIImage> holderTcs)
                {
                    // Free copy buffer memory for non-frozen params and release retained self
                    for (int i = 1; i < holder.Length; i++)
                    {
                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                        {
                            Arc.Release(retained.Ptr);
                        }
                        else if (holder[i] is DeferredSafeHandleRelease deferred)
                        {
                            deferred.Handle.DangerousRelease();
                        }
                        else if (holder[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                        {
                            copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                            NativeMemory.Free((void*)copyBuffer.Buffer);
                        }
                    }
                    holderTcs.TrySetException(exception);
                }
                else if (handle.Target is TaskCompletionSource<UIKit.UIImage> directTcs)
                {
                    directTcs.TrySetException(exception);
                }
            }
            finally
            {
                handle.Free();
            }
        }
        public unsafe Task<UIKit.UIImage> ImageAsync( Foundation.NSUrl _for)
        {
            var _forMetadata = SwiftObjectHelper<Swift.URL>.GetTypeMetadata();
            IntPtr _forCopyBuffer = (IntPtr)NativeMemory.Alloc(_forMetadata.Size);
            using var _forSwiftTemp = Swift.URL.FromNSUrl(_for);
            _forMetadata.ValueWitnessTable->InitializeWithCopy(
                (void*)_forCopyBuffer,
                (void*)_forSwiftTemp.Payload.DangerousGetHandle(),
                _forMetadata);
            IntPtr _forHandle = _forCopyBuffer;
            var _forCopyBufferWrapper = new CopyBufferWithType(_forCopyBuffer, _forMetadata);
            IntPtr _selfPtr = *(IntPtr*)_payload.DangerousGetHandle();
            Arc.Retain(_selfPtr);
            TaskCompletionSource<UIKit.UIImage> task = new TaskCompletionSource<UIKit.UIImage>();
            object[] _asyncCallHolder = new object[] { task, _forCopyBufferWrapper, (object)_for, new RetainedSelfPtr(_selfPtr), (object)this };
            GCHandle handle = GCHandle.Alloc(_asyncCallHolder, GCHandleType.Normal);
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                
                using var _forSwift = Swift.URL.FromNSUrl(_for);
                
                PInvoke_image_6B90EC1B(s_imageCallback_6B90EC1B, s_imageErrorCallback_6B90EC1B, GCHandle.ToIntPtr(handle), _forSwift.Payload);
                
                return task.Task;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("SwiftBindings", EntryPoint = "$s4Nuke13ImagePipelineC5image3forSo7UIImageC10Foundation3URLV_tYaKF_async")]
        private static extern void PInvoke_image_6B90EC1B( void* s_imageCallback_6B90EC1B,  void* s_imageErrorCallback_6B90EC1B,  IntPtr handle,  SafeHandle _for);
        
        
                private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> s_imageCallback_734A8A76 = &imageOnComplete_734A8A76;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void imageOnComplete_734A8A76(IntPtr resultPtr, IntPtr task)
        {
            GCHandle handle = GCHandle.FromIntPtr(task);
            try
            {
                // Read result from pointer (Swift allocated memory and stored the value)
                var result = SwiftMarshal.MarshalFromSwift<UIKit.UIImage>(resultPtr);

                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<UIKit.UIImage> holderTcs)
                {
                    // Free copy buffer memory for non-frozen params and release retained self
                    for (int i = 1; i < holder.Length; i++)
                    {
                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                        {
                            Arc.Release(retained.Ptr);
                        }
                        else if (holder[i] is DeferredSafeHandleRelease deferred)
                        {
                            deferred.Handle.DangerousRelease();
                        }
                        else if (holder[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                        {
                            copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                            NativeMemory.Free((void*)copyBuffer.Buffer);
                        }
                    }
                    holderTcs.TrySetResult(result);
                }
                else if (handle.Target is TaskCompletionSource<UIKit.UIImage> directTcs)
                {
                    directTcs.TrySetResult(result);
                }
            }
            finally
            {
                // Free Swift-allocated memory
                SBW_Free(resultPtr);
                handle.Free();
            }
        }

        private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> s_imageErrorCallback_734A8A76 = &imageOnError_734A8A76;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void imageOnError_734A8A76(IntPtr errorMessagePtr, IntPtr task)
        {
            GCHandle handle = GCHandle.FromIntPtr(task);
            try
            {
                var errorMessage = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(errorMessagePtr) ?? "Unknown Swift error";
                var exception = new SwiftException(errorMessage);

                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<UIKit.UIImage> holderTcs)
                {
                    // Free copy buffer memory for non-frozen params and release retained self
                    for (int i = 1; i < holder.Length; i++)
                    {
                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                        {
                            Arc.Release(retained.Ptr);
                        }
                        else if (holder[i] is DeferredSafeHandleRelease deferred)
                        {
                            deferred.Handle.DangerousRelease();
                        }
                        else if (holder[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                        {
                            copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                            NativeMemory.Free((void*)copyBuffer.Buffer);
                        }
                    }
                    holderTcs.TrySetException(exception);
                }
                else if (handle.Target is TaskCompletionSource<UIKit.UIImage> directTcs)
                {
                    directTcs.TrySetException(exception);
                }
            }
            finally
            {
                handle.Free();
            }
        }
        public unsafe Task<UIKit.UIImage> ImageAsync( Swift.Nuke.ImageRequest _for)
        {
            var _forMetadata = SwiftObjectHelper<Swift.Nuke.ImageRequest>.GetTypeMetadata();
            IntPtr _forCopyBuffer = (IntPtr)NativeMemory.Alloc(_forMetadata.Size);
            _forMetadata.ValueWitnessTable->InitializeWithCopy(
                (void*)_forCopyBuffer,
                (void*)_for.Payload.DangerousGetHandle(),
                _forMetadata);
            IntPtr _forHandle = _forCopyBuffer;
            var _forCopyBufferWrapper = new CopyBufferWithType(_forCopyBuffer, _forMetadata);
            IntPtr _selfPtr = *(IntPtr*)_payload.DangerousGetHandle();
            Arc.Retain(_selfPtr);
            TaskCompletionSource<UIKit.UIImage> task = new TaskCompletionSource<UIKit.UIImage>();
            object[] _asyncCallHolder = new object[] { task, _forCopyBufferWrapper, (object)_for, new RetainedSelfPtr(_selfPtr), (object)this };
            GCHandle handle = GCHandle.Alloc(_asyncCallHolder, GCHandleType.Normal);
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                
                
                PInvoke_image_734A8A76(s_imageCallback_734A8A76, s_imageErrorCallback_734A8A76, GCHandle.ToIntPtr(handle), _forHandle);
                
                return task.Task;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("SwiftBindings", EntryPoint = "$s4Nuke13ImagePipelineC5image3forSo7UIImageCAA0B7RequestV_tYaKF_async")]
        private static extern void PInvoke_image_734A8A76( void* s_imageCallback_734A8A76,  void* s_imageErrorCallback_734A8A76,  IntPtr handle,  IntPtr _for);
        
        
                private static unsafe delegate* unmanaged[Cdecl]<Swift.Data, IntPtr, IntPtr, void> s_dataCallback_20CF2ACE = &dataOnComplete_20CF2ACE;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void dataOnComplete_20CF2ACE(Swift.Data rawItem0, IntPtr rawItem1, IntPtr task)
        {
            GCHandle handle = GCHandle.FromIntPtr(task);
            try
            {
                var item0 = rawItem0;
                    var item1 = rawItem1 == IntPtr.Zero ? Swift.SwiftOptional<Foundation.NSUrlResponse>.NewNone() : Swift.SwiftOptional<Foundation.NSUrlResponse>.NewSome(ObjCRuntime.Runtime.GetNSObject<Foundation.NSUrlResponse>(rawItem1));
                var result = (item0, item1);
                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<(Swift.Data, Swift.SwiftOptional<Foundation.NSUrlResponse>)> holderTcs)
                {
                    // Free copy buffer memory for non-frozen params and release retained self
                    for (int i = 1; i < holder.Length; i++)
                    {
                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                        {
                            Arc.Release(retained.Ptr);
                        }
                        else if (holder[i] is DeferredSafeHandleRelease deferred)
                        {
                            // Release the SafeHandle that was kept alive for async safety
                            deferred.Handle.DangerousRelease();
                        }
                        else if (holder[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                        {
                            // Call Destroy to release Swift references, then free buffer
                            copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                            NativeMemory.Free((void*)copyBuffer.Buffer);
                        }
                    }
                    holderTcs.TrySetResult(result);
                }
                else if (handle.Target is TaskCompletionSource<(Swift.Data, Swift.SwiftOptional<Foundation.NSUrlResponse>)> directTcs)
                {
                    directTcs.TrySetResult(result);
                }
            }
            finally
            {
                handle.Free();
            }
        }

        private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> s_dataErrorCallback_20CF2ACE = &dataOnError_20CF2ACE;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void dataOnError_20CF2ACE(IntPtr errorMessagePtr, IntPtr task)
        {
            GCHandle handle = GCHandle.FromIntPtr(task);
            try
            {
                var errorMessage = Marshal.PtrToStringUTF8(errorMessagePtr) ?? "Unknown Swift error";
                var exception = new SwiftException(errorMessage);

                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<(Swift.Data, Swift.SwiftOptional<Foundation.NSUrlResponse>)> holderTcs)
                {
                    // Free copy buffer memory for non-frozen params and release retained self
                    for (int i = 1; i < holder.Length; i++)
                    {
                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                        {
                            Arc.Release(retained.Ptr);
                        }
                        else if (holder[i] is DeferredSafeHandleRelease deferred)
                        {
                            // Release the SafeHandle that was kept alive for async safety
                            deferred.Handle.DangerousRelease();
                        }
                        else if (holder[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                        {
                            // Call Destroy to release Swift references, then free buffer
                            copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                            NativeMemory.Free((void*)copyBuffer.Buffer);
                        }
                    }
                    holderTcs.TrySetException(exception);
                }
                else if (handle.Target is TaskCompletionSource<(Swift.Data, Swift.SwiftOptional<Foundation.NSUrlResponse>)> directTcs)
                {
                    directTcs.TrySetException(exception);
                }
            }
            finally
            {
                handle.Free();
            }
        }
        public unsafe Task<(Swift.Data, Swift.SwiftOptional<Foundation.NSUrlResponse>)> DataAsync( Swift.Nuke.ImageRequest _for)
        {
            var _forMetadata = SwiftObjectHelper<Swift.Nuke.ImageRequest>.GetTypeMetadata();
            IntPtr _forCopyBuffer = (IntPtr)NativeMemory.Alloc(_forMetadata.Size);
            _forMetadata.ValueWitnessTable->InitializeWithCopy(
                (void*)_forCopyBuffer,
                (void*)_for.Payload.DangerousGetHandle(),
                _forMetadata);
            IntPtr _forHandle = _forCopyBuffer;
            var _forCopyBufferWrapper = new CopyBufferWithType(_forCopyBuffer, _forMetadata);
            IntPtr _selfPtr = *(IntPtr*)_payload.DangerousGetHandle();
            Arc.Retain(_selfPtr);
            TaskCompletionSource<(Swift.Data, Swift.SwiftOptional<Foundation.NSUrlResponse>)> task = new TaskCompletionSource<(Swift.Data, Swift.SwiftOptional<Foundation.NSUrlResponse>)>();
            object[] _asyncCallHolder = new object[] { task, _forCopyBufferWrapper, (object)_for, new RetainedSelfPtr(_selfPtr), (object)this };
            GCHandle handle = GCHandle.Alloc(_asyncCallHolder, GCHandleType.Normal);
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                
                
                PInvoke_data_20CF2ACE(s_dataCallback_20CF2ACE, s_dataErrorCallback_20CF2ACE, GCHandle.ToIntPtr(handle), _forHandle);
                
                return task.Task;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("SwiftBindings", EntryPoint = "$s4Nuke13ImagePipelineC4data3for10Foundation4DataV_So13NSURLResponseCSgtAA0B7RequestV_tYaKF_async")]
        private static extern void PInvoke_data_20CF2ACE( void* s_dataCallback_20CF2ACE,  void* s_dataErrorCallback_20CF2ACE,  IntPtr handle,  IntPtr _for);
        
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, SwiftSelf, void> s_loadImage_completion_2A84F030_Callback = &loadImage_completion_2A84F030_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void loadImage_completion_2A84F030_Callback(void* arg0, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Action<Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error>>>(new IntPtr(context.Value));
            del(SwiftMarshal.MarshalFromSwift<Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error>>(new IntPtr(arg0)));
        }
        
        public unsafe Swift.Nuke.ImageTask LoadImage( Foundation.NSUrl with,  Action<Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error>> completion)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            GCHandle completionHandle = default;
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                completionHandle = GCHandle.Alloc(completion);
                var completionClosure = new SwiftClosureData((IntPtr)s_loadImage_completion_2A84F030_Callback, GCHandle.ToIntPtr(completionHandle));
                using var withSwift = Swift.URL.FromNSUrl(with);
                
                var result = PInvoke_loadImage_2A84F030(withSwift.Payload, completionClosure, self);
                
                var classPayload = NativeMemory.Alloc((nuint)sizeof(IntPtr));
                *(IntPtr*)classPayload = result;
                return (Swift.Nuke.ImageTask)SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageTask>(new IntPtr(classPayload));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
                if (completionHandle.IsAllocated) completionHandle.Free();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC04loadB04with10completionAA0B4TaskC10Foundation3URLV_ys6ResultOyAA0B8ResponseVAC5ErrorOGctF")]
        private static extern IntPtr PInvoke_loadImage_2A84F030( SafeHandle with,  SwiftClosureData completion,  SwiftSelf self);
        
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, SwiftSelf, void> s_loadImage_completion_1A35AAD2_Callback = &loadImage_completion_1A35AAD2_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void loadImage_completion_1A35AAD2_Callback(void* arg0, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Action<Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error>>>(new IntPtr(context.Value));
            del(SwiftMarshal.MarshalFromSwift<Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error>>(new IntPtr(arg0)));
        }
        
        public unsafe Swift.Nuke.ImageTask LoadImage( Swift.Nuke.ImageRequest with,  Action<Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error>> completion)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            GCHandle completionHandle = default;
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                completionHandle = GCHandle.Alloc(completion);
                var completionClosure = new SwiftClosureData((IntPtr)s_loadImage_completion_1A35AAD2_Callback, GCHandle.ToIntPtr(completionHandle));
                
                var result = PInvoke_loadImage_1A35AAD2(with.Payload, completionClosure, self);
                
                var classPayload = NativeMemory.Alloc((nuint)sizeof(IntPtr));
                *(IntPtr*)classPayload = result;
                return (Swift.Nuke.ImageTask)SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageTask>(new IntPtr(classPayload));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
                if (completionHandle.IsAllocated) completionHandle.Free();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC04loadB04with10completionAA0B4TaskCAA0B7RequestV_ys6ResultOyAA0B8ResponseVAC5ErrorOGctF")]
        private static extern IntPtr PInvoke_loadImage_1A35AAD2( SafeHandle with,  SwiftClosureData completion,  SwiftSelf self);
        
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, long, long, SwiftSelf, void> s_loadImage_progress_3916B679_Callback = &loadImage_progress_3916B679_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void loadImage_progress_3916B679_Callback(void* arg0, long arg1, long arg2, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Action<Swift.SwiftOptional<Swift.Nuke.ImageResponse>, System.Int64, System.Int64>>(new IntPtr(context.Value));
            del(SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Nuke.ImageResponse>>(new IntPtr(arg0)), arg1, arg2);
        }
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, SwiftSelf, void> s_loadImage_completion_3916B679_Callback = &loadImage_completion_3916B679_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void loadImage_completion_3916B679_Callback(void* arg0, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Action<Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error>>>(new IntPtr(context.Value));
            del(SwiftMarshal.MarshalFromSwift<Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error>>(new IntPtr(arg0)));
        }
        
        public unsafe Swift.Nuke.ImageTask LoadImage( Swift.Nuke.ImageRequest with,  Swift.DispatchQueue? queue,  Action<Swift.SwiftOptional<Swift.Nuke.ImageResponse>, System.Int64, System.Int64>? progress,  Action<Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error>> completion)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            GCHandle progressHandle = default;
            GCHandle completionHandle = default;
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                SwiftClosureData progressClosure;
                if (progress != null)
                {
                    progressHandle = GCHandle.Alloc(progress);
                    progressClosure = new SwiftClosureData((IntPtr)s_loadImage_progress_3916B679_Callback, GCHandle.ToIntPtr(progressHandle));
                }
                else
                {
                    progressClosure = default; // Zero-initialized = nil in Swift
                }
                completionHandle = GCHandle.Alloc(completion);
                var completionClosure = new SwiftClosureData((IntPtr)s_loadImage_completion_3916B679_Callback, GCHandle.ToIntPtr(completionHandle));
                using var queueSwift = queue is {} queueValue ? SwiftOptional<Swift.DispatchQueue>.NewSome(queueValue) : SwiftOptional<Swift.DispatchQueue>.NewNone();
                using PayloadBuffer<IntPtr> queueDisposable = queueSwift.PayloadBuffer;
                IntPtr queueBuffer = queueDisposable.Buffer;
                
                var result = PInvoke_loadImage_3916B679(with.Payload, queueBuffer, progressClosure, completionClosure, self);
                
                var classPayload = NativeMemory.Alloc((nuint)sizeof(IntPtr));
                *(IntPtr*)classPayload = result;
                return (Swift.Nuke.ImageTask)SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageTask>(new IntPtr(classPayload));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
                if (progressHandle.IsAllocated) progressHandle.Free();
                if (completionHandle.IsAllocated) completionHandle.Free();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC04loadB04with5queue8progress10completionAA0B4TaskCAA0B7RequestV_So012OS_dispatch_F0CSgyAA0B8ResponseVSg_s5Int64VATtcSgys6ResultOyAqC5ErrorOGctF")]
        private static extern IntPtr PInvoke_loadImage_3916B679( SafeHandle with,  IntPtr queueBuffer,  SwiftClosureData progress,  SwiftClosureData completion,  SwiftSelf self);
        
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, SwiftSelf, void> s_loadData_completion_21705F83_Callback = &loadData_completion_21705F83_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void loadData_completion_21705F83_Callback(void* arg0, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Action<Swift.SwiftResult<(Swift.Data data, Swift.SwiftOptional<Foundation.NSUrlResponse> response), Swift.Nuke.ImagePipeline.Error>>>(new IntPtr(context.Value));
            del(SwiftMarshal.MarshalFromSwift<Swift.SwiftResult<(Swift.Data data, Swift.SwiftOptional<Foundation.NSUrlResponse> response), Swift.Nuke.ImagePipeline.Error>>(new IntPtr(arg0)));
        }
        
        public unsafe Swift.Nuke.ImageTask LoadData( Swift.Nuke.ImageRequest with,  Action<Swift.SwiftResult<(Swift.Data data, Swift.SwiftOptional<Foundation.NSUrlResponse> response), Swift.Nuke.ImagePipeline.Error>> completion)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            GCHandle completionHandle = default;
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                completionHandle = GCHandle.Alloc(completion);
                var completionClosure = new SwiftClosureData((IntPtr)s_loadData_completion_21705F83_Callback, GCHandle.ToIntPtr(completionHandle));
                
                var result = PInvoke_loadData_21705F83(with.Payload, completionClosure, self);
                
                var classPayload = NativeMemory.Alloc((nuint)sizeof(IntPtr));
                *(IntPtr*)classPayload = result;
                return (Swift.Nuke.ImageTask)SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageTask>(new IntPtr(classPayload));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
                if (completionHandle.IsAllocated) completionHandle.Free();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC8loadData4with10completionAA0B4TaskCAA0B7RequestV_ys6ResultOy10Foundation0E0V4data_So13NSURLResponseCSg8responsetAC5ErrorOGctF")]
        private static extern IntPtr PInvoke_loadData_21705F83( SafeHandle with,  SwiftClosureData completion,  SwiftSelf self);
        
        
        private static unsafe readonly delegate* unmanaged[Swift]<long, long, SwiftSelf, void> s_loadData_progress_3928EBD9_Callback = &loadData_progress_3928EBD9_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void loadData_progress_3928EBD9_Callback(long arg0, long arg1, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Action<System.Int64, System.Int64>>(new IntPtr(context.Value));
            del(arg0, arg1);
        }
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, SwiftSelf, void> s_loadData_completion_3928EBD9_Callback = &loadData_completion_3928EBD9_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void loadData_completion_3928EBD9_Callback(void* arg0, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Action<Swift.SwiftResult<(Swift.Data data, Swift.SwiftOptional<Foundation.NSUrlResponse> response), Swift.Nuke.ImagePipeline.Error>>>(new IntPtr(context.Value));
            del(SwiftMarshal.MarshalFromSwift<Swift.SwiftResult<(Swift.Data data, Swift.SwiftOptional<Foundation.NSUrlResponse> response), Swift.Nuke.ImagePipeline.Error>>(new IntPtr(arg0)));
        }
        
        public unsafe Swift.Nuke.ImageTask LoadData( Swift.Nuke.ImageRequest with,  Swift.DispatchQueue? queue,  Action<System.Int64, System.Int64>? progress,  Action<Swift.SwiftResult<(Swift.Data data, Swift.SwiftOptional<Foundation.NSUrlResponse> response), Swift.Nuke.ImagePipeline.Error>> completion)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            GCHandle progressHandle = default;
            GCHandle completionHandle = default;
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                SwiftClosureData progressClosure;
                if (progress != null)
                {
                    progressHandle = GCHandle.Alloc(progress);
                    progressClosure = new SwiftClosureData((IntPtr)s_loadData_progress_3928EBD9_Callback, GCHandle.ToIntPtr(progressHandle));
                }
                else
                {
                    progressClosure = default; // Zero-initialized = nil in Swift
                }
                completionHandle = GCHandle.Alloc(completion);
                var completionClosure = new SwiftClosureData((IntPtr)s_loadData_completion_3928EBD9_Callback, GCHandle.ToIntPtr(completionHandle));
                using var queueSwift = queue is {} queueValue ? SwiftOptional<Swift.DispatchQueue>.NewSome(queueValue) : SwiftOptional<Swift.DispatchQueue>.NewNone();
                using PayloadBuffer<IntPtr> queueDisposable = queueSwift.PayloadBuffer;
                IntPtr queueBuffer = queueDisposable.Buffer;
                
                var result = PInvoke_loadData_3928EBD9(with.Payload, queueBuffer, progressClosure, completionClosure, self);
                
                var classPayload = NativeMemory.Alloc((nuint)sizeof(IntPtr));
                *(IntPtr*)classPayload = result;
                return (Swift.Nuke.ImageTask)SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageTask>(new IntPtr(classPayload));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
                if (progressHandle.IsAllocated) progressHandle.Free();
                if (completionHandle.IsAllocated) completionHandle.Free();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC8loadData4with5queue8progress10completionAA0B4TaskCAA0B7RequestV_So012OS_dispatch_G0CSgys5Int64V_AQtcSgys6ResultOy10Foundation0E0V4data_So13NSURLResponseCSg8responsetAC5ErrorOGctF")]
        private static extern IntPtr PInvoke_loadData_3928EBD9( SafeHandle with,  IntPtr queueBuffer,  SwiftClosureData progress,  SwiftClosureData completion,  SwiftSelf self);
        
        
        
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, SwiftSelf, void> s_loadData_completion_2CE27590_Callback = &loadData_completion_2CE27590_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void loadData_completion_2CE27590_Callback(void* arg0, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Action<Swift.SwiftResult<(Swift.Data data, Swift.SwiftOptional<Foundation.NSUrlResponse> response), Swift.Nuke.ImagePipeline.Error>>>(new IntPtr(context.Value));
            del(SwiftMarshal.MarshalFromSwift<Swift.SwiftResult<(Swift.Data data, Swift.SwiftOptional<Foundation.NSUrlResponse> response), Swift.Nuke.ImagePipeline.Error>>(new IntPtr(arg0)));
        }
        
        public unsafe Swift.Nuke.ImageTask LoadData( Foundation.NSUrl with,  Action<Swift.SwiftResult<(Swift.Data data, Swift.SwiftOptional<Foundation.NSUrlResponse> response), Swift.Nuke.ImagePipeline.Error>> completion)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            GCHandle completionHandle = default;
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                completionHandle = GCHandle.Alloc(completion);
                var completionClosure = new SwiftClosureData((IntPtr)s_loadData_completion_2CE27590_Callback, GCHandle.ToIntPtr(completionHandle));
                using var withSwift = Swift.URL.FromNSUrl(with);
                
                var result = PInvoke_loadData_2CE27590(withSwift.Payload, completionClosure, self);
                
                var classPayload = NativeMemory.Alloc((nuint)sizeof(IntPtr));
                *(IntPtr*)classPayload = result;
                return (Swift.Nuke.ImageTask)SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageTask>(new IntPtr(classPayload));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
                if (completionHandle.IsAllocated) completionHandle.Free();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC8loadData4with10completionAA0B4TaskC10Foundation3URLV_ys6ResultOyAI0E0V4data_So13NSURLResponseCSg8responsetAC5ErrorOGctF")]
        private static extern IntPtr PInvoke_loadData_2CE27590( SafeHandle with,  SwiftClosureData completion,  SwiftSelf self);
        
        
                private static unsafe delegate* unmanaged[Cdecl]<Swift.Data, IntPtr, IntPtr, void> s_dataCallback_1166B2DF = &dataOnComplete_1166B2DF;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void dataOnComplete_1166B2DF(Swift.Data rawItem0, IntPtr rawItem1, IntPtr task)
        {
            GCHandle handle = GCHandle.FromIntPtr(task);
            try
            {
                var item0 = rawItem0;
                    var item1 = rawItem1 == IntPtr.Zero ? Swift.SwiftOptional<Foundation.NSUrlResponse>.NewNone() : Swift.SwiftOptional<Foundation.NSUrlResponse>.NewSome(ObjCRuntime.Runtime.GetNSObject<Foundation.NSUrlResponse>(rawItem1));
                var result = (item0, item1);
                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<(Swift.Data, Swift.SwiftOptional<Foundation.NSUrlResponse>)> holderTcs)
                {
                    // Free copy buffer memory for non-frozen params and release retained self
                    for (int i = 1; i < holder.Length; i++)
                    {
                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                        {
                            Arc.Release(retained.Ptr);
                        }
                        else if (holder[i] is DeferredSafeHandleRelease deferred)
                        {
                            // Release the SafeHandle that was kept alive for async safety
                            deferred.Handle.DangerousRelease();
                        }
                        else if (holder[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                        {
                            // Call Destroy to release Swift references, then free buffer
                            copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                            NativeMemory.Free((void*)copyBuffer.Buffer);
                        }
                    }
                    holderTcs.TrySetResult(result);
                }
                else if (handle.Target is TaskCompletionSource<(Swift.Data, Swift.SwiftOptional<Foundation.NSUrlResponse>)> directTcs)
                {
                    directTcs.TrySetResult(result);
                }
            }
            finally
            {
                handle.Free();
            }
        }

        private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> s_dataErrorCallback_1166B2DF = &dataOnError_1166B2DF;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void dataOnError_1166B2DF(IntPtr errorMessagePtr, IntPtr task)
        {
            GCHandle handle = GCHandle.FromIntPtr(task);
            try
            {
                var errorMessage = Marshal.PtrToStringUTF8(errorMessagePtr) ?? "Unknown Swift error";
                var exception = new SwiftException(errorMessage);

                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<(Swift.Data, Swift.SwiftOptional<Foundation.NSUrlResponse>)> holderTcs)
                {
                    // Free copy buffer memory for non-frozen params and release retained self
                    for (int i = 1; i < holder.Length; i++)
                    {
                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                        {
                            Arc.Release(retained.Ptr);
                        }
                        else if (holder[i] is DeferredSafeHandleRelease deferred)
                        {
                            // Release the SafeHandle that was kept alive for async safety
                            deferred.Handle.DangerousRelease();
                        }
                        else if (holder[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                        {
                            // Call Destroy to release Swift references, then free buffer
                            copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                            NativeMemory.Free((void*)copyBuffer.Buffer);
                        }
                    }
                    holderTcs.TrySetException(exception);
                }
                else if (handle.Target is TaskCompletionSource<(Swift.Data, Swift.SwiftOptional<Foundation.NSUrlResponse>)> directTcs)
                {
                    directTcs.TrySetException(exception);
                }
            }
            finally
            {
                handle.Free();
            }
        }
        public unsafe Task<(Swift.Data, Swift.SwiftOptional<Foundation.NSUrlResponse>)> DataAsync( Foundation.NSUrl _for)
        {
            var _forMetadata = SwiftObjectHelper<Swift.URL>.GetTypeMetadata();
            IntPtr _forCopyBuffer = (IntPtr)NativeMemory.Alloc(_forMetadata.Size);
            using var _forSwiftTemp = Swift.URL.FromNSUrl(_for);
            _forMetadata.ValueWitnessTable->InitializeWithCopy(
                (void*)_forCopyBuffer,
                (void*)_forSwiftTemp.Payload.DangerousGetHandle(),
                _forMetadata);
            IntPtr _forHandle = _forCopyBuffer;
            var _forCopyBufferWrapper = new CopyBufferWithType(_forCopyBuffer, _forMetadata);
            IntPtr _selfPtr = *(IntPtr*)_payload.DangerousGetHandle();
            Arc.Retain(_selfPtr);
            TaskCompletionSource<(Swift.Data, Swift.SwiftOptional<Foundation.NSUrlResponse>)> task = new TaskCompletionSource<(Swift.Data, Swift.SwiftOptional<Foundation.NSUrlResponse>)>();
            object[] _asyncCallHolder = new object[] { task, _forCopyBufferWrapper, (object)_for, new RetainedSelfPtr(_selfPtr), (object)this };
            GCHandle handle = GCHandle.Alloc(_asyncCallHolder, GCHandleType.Normal);
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                
                using var _forSwift = Swift.URL.FromNSUrl(_for);
                
                PInvoke_data_1166B2DF(s_dataCallback_1166B2DF, s_dataErrorCallback_1166B2DF, GCHandle.ToIntPtr(handle), _forSwift.Payload);
                
                return task.Task;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("SwiftBindings", EntryPoint = "$s4Nuke13ImagePipelineC4data3for10Foundation4DataV_So13NSURLResponseCSgtAF3URLV_tYaKF_async")]
        private static extern void PInvoke_data_1166B2DF( void* s_dataCallback_1166B2DF,  void* s_dataErrorCallback_1166B2DF,  IntPtr handle,  SafeHandle _for);
        
        
    }
    
    
    public unsafe class DataLoader : ISwiftObject
    {
        private unsafe Foundation.NSUrlSession Session_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Foundation.NSUrlSession>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_session_Get_29551193(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Foundation.NSUrlSession>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10DataLoaderC7sessionSo12NSURLSessionCvg")]
        private static extern void PInvoke_session_Get_29551193( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Foundation.NSUrlSession Session
        {
            get => Session_Get();
        }
        
        private unsafe System.Boolean PrefersIncrementalDelivery_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_prefersIncrementalDelivery_Get_4FFDE4AD(self);
                
                return result;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10DataLoaderC26prefersIncrementalDeliverySbvg")]
        private static extern System.Boolean PInvoke_prefersIncrementalDelivery_Get_4FFDE4AD( SwiftSelf self);
        
        private unsafe void PrefersIncrementalDelivery_Set( System.Boolean value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_prefersIncrementalDelivery_Set_20B88BA7(value, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10DataLoaderC26prefersIncrementalDeliverySbvs")]
        private static extern void PInvoke_prefersIncrementalDelivery_Set_20B88BA7( System.Boolean value,  SwiftSelf self);
        
        public System.Boolean PrefersIncrementalDelivery
        {
            get => PrefersIncrementalDelivery_Get();
            set => PrefersIncrementalDelivery_Set(value);
        }
        
        private static unsafe Foundation.NSUrlSessionConfiguration DefaultConfiguration_Get()
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Foundation.NSUrlSessionConfiguration>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_defaultConfiguration_Get_69044A9F(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Foundation.NSUrlSessionConfiguration>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10DataLoaderC20defaultConfigurationSo012NSURLSessionE0CvgZ")]
        private static extern void PInvoke_defaultConfiguration_Get_69044A9F( SwiftIndirectResult swiftIndirectResult);
        
        public static Foundation.NSUrlSessionConfiguration DefaultConfiguration
        {
            get => DefaultConfiguration_Get();
        }
        
        private static unsafe Foundation.NSUrlCache SharedUrlCache_Get()
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Foundation.NSUrlCache>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_sharedUrlCache_Get_39E90D6F(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Foundation.NSUrlCache>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10DataLoaderC14sharedUrlCacheSo10NSURLCacheCvgZ")]
        private static extern void PInvoke_sharedUrlCache_Get_39E90D6F( SwiftIndirectResult swiftIndirectResult);
        
        public static Foundation.NSUrlCache SharedUrlCache
        {
            get => SharedUrlCache_Get();
        }
        
        static nuint _payloadSize = SwiftObjectHelper<DataLoader>.GetTypeMetadata().Size;
        SwiftSafeHandle<DataLoader> _payload = SwiftSafeHandle<DataLoader>.Zero;
        
        internal SwiftSafeHandle<DataLoader> Payload => _payload;
        
        public void Dispose() => _payload.Dispose();
        
        static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10DataLoaderCMa")]
        internal static extern TypeMetadata PInvoke_getMetadata();
        
        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
        {
            return new DataLoader(handle);
        }
        
        DataLoader(SwiftHandle handle)
        {
            _payload = new SwiftSafeHandle<DataLoader>(handle);
        }
        
        unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            var metadata = SwiftObjectHelper<DataLoader>.GetTypeMetadata();
            if ((int)metadata.Size > swiftDestSpan.Length)
            {
                throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
            }
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure that the instance is valid before making copy
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
        
        private static Dictionary<Type, string> _protocolConformanceSymbols;
        static DataLoader()
        {
            _protocolConformanceSymbols = new Dictionary<Type, string>
            {
                {typeof(IDataLoading), "$s4Nuke10DataLoaderCAA0B7LoadingAAMc"}
            };
        }
        
        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
            where TProtocol : class
        {
            if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
            {
                throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type DataLoader and protocol {typeof(TProtocol).Name}, but no conformance was found.");
            }
            return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
        }
        
        public unsafe class Error : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<Error>.GetTypeMetadata().Size;
            SwiftSafeHandle<Error> _payload = SwiftSafeHandle<Error>.Zero;
            internal SwiftSafeHandle<Error> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            /// <summary>
            /// Creates the 'statusCodeUnacceptable' case of Error.
            /// </summary>
            public static Error StatusCodeUnacceptable(nint value0)
            {
                var result = new Error();
                var metadata = PInvoke_getMetadata();
                IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                var indirectResult = new SwiftIndirectResult((void*)buffer);
                PInvoke_StatusCodeUnacceptable(indirectResult, value0);
                result._payload = new SwiftSafeHandle<Error>(buffer);
                return result;
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke10DataLoaderC5ErrorO22statusCodeUnacceptableyAESicAEmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_StatusCodeUnacceptable(SwiftIndirectResult result, nint value0);
            
            /// <summary>
            /// Enum representing the possible cases of Error.
            /// Tag values follow Swift's ordering: payload cases first, then no-payload cases.
            /// </summary>
            public enum CaseTag : uint
            {
                StatusCodeUnacceptable = 0,
            }
            
            /// <summary>
            /// Gets the current case of this enum instance.
            /// </summary>
            public unsafe CaseTag Tag
            {
                get
                {
                    bool success = false;
                    _payload.DangerousAddRef(ref success);
                    try
                    {
                        var metadata = SwiftObjectHelper<Error>.GetTypeMetadata();
                        byte* payload = (byte*)_payload.DangerousGetHandle();
                        return (CaseTag)metadata.ValueWitnessTable->GetEnumTag(payload, metadata);
                    }
                    finally
                    {
                        if (success)
                            _payload.DangerousRelease();
                    }
                }
            }
            
            /// <summary>
            /// Attempts to extract the associated value(s) for the 'statusCodeUnacceptable' case.
            /// </summary>
            /// <param name="value">When this method returns true, contains the associated value(s).</param>
            /// <returns>True if this enum is the 'statusCodeUnacceptable' case; otherwise, false.</returns>
            public unsafe bool TryGetStatusCodeUnacceptable([MaybeNullWhen(false)] out nint value)
            {
                if (Tag != CaseTag.StatusCodeUnacceptable)
                {
                    value = default;
                    return false;
                }
                
                var metadata = SwiftObjectHelper<Error>.GetTypeMetadata();
                
                // Create a non-destructive copy of the enum
                byte* enumCopy = stackalloc byte[(int)metadata.Size];
                bool success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    metadata.ValueWitnessTable->InitializeWithCopy(enumCopy, (void*)_payload.DangerousGetHandle(), metadata);
                }
                finally
                {
                    if (success)
                        _payload.DangerousRelease();
                }
                
                // Strip the tag to get the raw payload
                metadata.ValueWitnessTable->DestructiveProjectEnumData(enumCopy, metadata);
                
                // Marshal the payload to C# type(s)
                value = SwiftMarshal.MarshalFromSwift<nint>(new IntPtr(enumCopy));
                return true;
            }
            
            
            private unsafe Swift.SwiftString Description_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_37CB5CC5(self);
                    
                    unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke10DataLoaderC5ErrorO11descriptionSSvg")]
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_37CB5CC5( SwiftSelf self);
            
            public string Description
            {
                get => Description_Get().ToString();
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke10DataLoaderC5ErrorOMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new Error(handle);
            }
            
            Error()
            {
            }
            
            Error(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<Error>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<Error>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static Error()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type Error and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
        }
        
        
        
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, void*, SwiftSelf, void> s_loadData_didReceiveData_1B7FD515_Callback = &loadData_didReceiveData_1B7FD515_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void loadData_didReceiveData_1B7FD515_Callback(void* arg0, void* arg1, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Action<Swift.Data, Foundation.NSUrlResponse>>(new IntPtr(context.Value));
            del(SwiftMarshal.MarshalFromSwift<Swift.Data>(new IntPtr(arg0)), SwiftMarshal.MarshalFromSwift<Foundation.NSUrlResponse>(new IntPtr(arg1)));
        }
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, SwiftSelf, void> s_loadData_completion_1B7FD515_Callback = &loadData_completion_1B7FD515_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void loadData_completion_1B7FD515_Callback(void* arg0, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Action<Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1>>>(new IntPtr(context.Value));
            del(SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1>>(new IntPtr(arg0)));
        }
        
        [global::Swift.UnsupportedSwiftType("Existential type fallback", "any Nuke.Cancellable")]
        public unsafe ICancellable LoadData( Swift.URLRequest with,  Action<Swift.Data, Foundation.NSUrlResponse> didReceiveData,  Action<Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1>> completion)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            GCHandle didReceiveDataHandle = default;
            GCHandle completionHandle = default;
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                didReceiveDataHandle = GCHandle.Alloc(didReceiveData);
                var didReceiveDataClosure = new SwiftClosureData((IntPtr)s_loadData_didReceiveData_1B7FD515_Callback, GCHandle.ToIntPtr(didReceiveDataHandle));
                completionHandle = GCHandle.Alloc(completion);
                var completionClosure = new SwiftClosureData((IntPtr)s_loadData_completion_1B7FD515_Callback, GCHandle.ToIntPtr(completionHandle));
                
                var result = PInvoke_loadData_1B7FD515(with.Payload, didReceiveDataClosure, completionClosure, self);
                
                return new CancellableProxy(result);
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
                if (didReceiveDataHandle.IsAllocated) didReceiveDataHandle.Free();
                if (completionHandle.IsAllocated) completionHandle.Free();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10DataLoaderC04loadB04with010didReceiveB010completionAA11Cancellable_p10Foundation10URLRequestV_yAI0B0V_So13NSURLResponseCtcys5Error_pSgctF")]
        private static extern Swift.Runtime.ExistentialContainer1 PInvoke_loadData_1B7FD515( SafeHandle with,  SwiftClosureData didReceiveData,  SwiftClosureData completion,  SwiftSelf self);
        
        
    }
    
    
    public interface IImageEncoding
    {
        Swift.Data? Encode(UIKit.UIImage arg0);
        Swift.Data? Encode(Swift.Nuke.ImageContainer arg0, Swift.Nuke.ImageEncodingContext context);
    }
    
    /// <summary>
    /// Proxy class that enables C# implementations of the ImageEncoding protocol.
    /// Can wrap either a C# implementation or receive Swift existential containers.
    /// </summary>
    public unsafe class ImageEncodingProxy : IImageEncoding, ISwiftObject, Swift.Runtime.ISwiftExistentialConvertible<ExistentialContainer1>
    {
        /// <summary>Matches Swift ImageEncoding_vtable layout</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct ImageEncodingSwiftVTable
        {
            public IntPtr csVTHandle;
            public IntPtr func_encode_0;
            public IntPtr func_encode_1;
        }
        
        /// <summary>Local vtable holding managed delegates</summary>
        private struct ImageEncodingLocalVTable
        {
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr> Func_encode_0;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> Func_encode_1;
        }
        
        private static IntPtr _protocolWitnessTable;
        private static ImageEncodingSwiftVTable _swiftVTable;
        private static ImageEncodingLocalVTable _localVTable;
        private static GCHandle _localVTableHandle;
        private static bool _vtableInitialized;
        private static readonly object _vtableLock = new object();
        private readonly IImageEncoding? _csharpImpl;
        private readonly EveryProtocol? _everyProtocol;
        private ExistentialContainer1 _swiftContainer;
        static ImageEncodingProxy()
        {
            InitializeVtable();
        }
        
        private static void InitializeVtable()
        {
            lock (_vtableLock)
            {
                if (_vtableInitialized) return;
                
                _localVTable = new ImageEncodingLocalVTable
                {
                    Func_encode_0 = &Receive_encode_0,
                    Func_encode_1 = &Receive_encode_1,
                };
                
                _localVTableHandle = GCHandle.Alloc(_localVTable, GCHandleType.Pinned);
                
                _swiftVTable = new ImageEncodingSwiftVTable
                {
                    csVTHandle = GCHandle.ToIntPtr(_localVTableHandle),
                    func_encode_0 = (IntPtr)_localVTable.Func_encode_0,
                    func_encode_1 = (IntPtr)_localVTable.Func_encode_1,
                };
                
                fixed (ImageEncodingSwiftVTable* vtPtr = &_swiftVTable)
                {
                    NativeMethods.SetImageEncoding_vtable((IntPtr)vtPtr);
                }
                _vtableInitialized = true;
            }
        }
        
        #region Swift Callback Receivers
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_encode_0(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImageEncodingProxy>(container);
            var param0 = MarshalFromSwift<UIKit.UIImage>(rawArg0);
            var result = proxy._csharpImpl!.Encode(param0);
            return MarshalToSwiftBuffer(result);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_encode_1(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImageEncodingProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageContainer>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImageEncodingContext>(rawArg1);
            var result = proxy._csharpImpl!.Encode(param0, param1);
            return MarshalToSwiftBuffer(result);
        }
        
        #endregion
        
        /// <summary>
        /// Creates a proxy wrapping a C# implementation of IImageEncoding.
        /// </summary>
        /// <param name="implementation">The C# implementation of the protocol.</param>
        public ImageEncodingProxy(IImageEncoding implementation)
        {
            _csharpImpl = implementation ?? throw new ArgumentNullException(nameof(implementation));
            _everyProtocol = new EveryProtocol();
            // Create existential container manually
            // The container holds: payload (EveryProtocol pointer), metadata, and witness table
            _swiftContainer = new ExistentialContainer1();
            _swiftContainer.Payload0 = _everyProtocol.Handle;
            _swiftContainer.ObjectMetadata = EveryProtocol.GetTypeMetadata();
            _swiftContainer[0] = ProtocolWitnessTableHandle;
            // Register this proxy so Swift callbacks can find us
            SwiftObjectRegistry.RegisterStrong(_everyProtocol.Handle, this);
        }
        /// <summary>
        /// Creates a proxy from an existing Swift existential container.
        /// Use this when receiving protocol values from Swift code.
        /// </summary>
        /// <remarks>
        /// Swift-backed proxies created with this constructor dispatch blittable and String
        /// protocol members through witness table accessors. Non-dispatchable members
        /// (non-blittable non-String types, throwing, async) throw <see cref="NotSupportedException"/>.
        /// </remarks>
        /// <param name="container">The Swift existential container.</param>
        public ImageEncodingProxy(ExistentialContainer1 container)
        {
            _swiftContainer = container;
            _csharpImpl = null;
            _everyProtocol = null;
        }
        #region Interface Implementation
        
        public Swift.Data? Encode(UIKit.UIImage arg0)
        {
            if (_csharpImpl != null)
                return _csharpImpl.Encode(arg0);
            throw new NotSupportedException(
                "Cannot call method 'Encode' on a Swift-backed existential container. " +
                "Protocol member access is only supported when wrapping a C# implementation.");
        }
        
        public Swift.Data? Encode(Swift.Nuke.ImageContainer arg0, Swift.Nuke.ImageEncodingContext context)
        {
            if (_csharpImpl != null)
                return _csharpImpl.Encode(arg0, context);
            throw new NotSupportedException(
                "Cannot call method 'Encode' on a Swift-backed existential container. " +
                "Protocol member access is only supported when wrapping a C# implementation.");
        }
        
        #endregion
        
        #region ISwiftObject Implementation
        /// <summary>
        /// Gets the protocol witness table handle for EveryProtocol conforming to ImageEncoding.
        /// </summary>
        public static IntPtr ProtocolWitnessTableHandle
        {
            get
            {
                if (_protocolWitnessTable == IntPtr.Zero)
                {
                    // The witness table is generated by the Swift compiler
                    // It will be available after the Swift wrapper is loaded
                    // For now, we look it up dynamically
                    _protocolWitnessTable = GetWitnessTableFromSwift();
                }
                return _protocolWitnessTable;
            }
        }
        private static IntPtr GetWitnessTableFromSwift()
        {
            // Call the Swift-exported function that returns the witness table pointer
            // This function is generated by EveryProtocolEmitter.EmitWitnessTableGetter()
            return NativeMethods.GetWitnessTable();
        }
        /// <summary>
        /// Gets the existential container that can be passed to Swift code.
        /// </summary>
        public ExistentialContainer1 GetExistentialContainer() => _swiftContainer;
        public static TypeMetadata GetTypeMetadata()
        {
            // Proxy classes don't have their own Swift metadata
            // They use the EveryProtocol metadata
            return EveryProtocol.GetTypeMetadata();
        }
        public static ISwiftObject NewFromPayload(IntPtr payload)
        {
            // Create from existential container
            var container = *(ExistentialContainer1*)payload;
            return new ImageEncodingProxy(container);
        }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            // Marshal the existential container
            var size = _swiftContainer.SizeOf;
            if (swiftDestSpan.Length < size)
                throw new ArgumentException("Destination span too small", nameof(swiftDestSpan));
            fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
            {
                new Span<byte>(containerPtr, size).CopyTo(swiftDestSpan);
            }
            return size;
        }
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
        {
            throw new NotSupportedException(
                "Protocol conformance descriptor is not available for proxy types. " +
                "Proxy classes use EveryProtocol's witness table, not native conformance descriptors.");
        }
        public void Dispose() { }
        #endregion
        #region Marshalling Helpers
        [StructLayout(LayoutKind.Sequential)]
        private struct Utf8Slice
        {
            public IntPtr Ptr;
            public nint Len;
        }
        private static IntPtr MarshalToSwiftBuffer<T>(T value)
        {
            // Use direct memory operations for all types
            // This works for blittable types (value types, structs with blittable fields)
            var size = Unsafe.SizeOf<T>();
            var ptr = (IntPtr)NativeMemory.Alloc((nuint)size);
            Unsafe.Write((void*)ptr, value);
            return ptr;
        }
        private static T MarshalFromSwift<T>(IntPtr ptr)
        {
            // Use direct memory operations for all types
            return Unsafe.Read<T>((void*)ptr);
        }
        #endregion
        private static class NativeMethods
        {
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SetImageEncoding_vtable")]
            public static extern void SetImageEncoding_vtable(IntPtr vtable);
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "Get_EveryProtocol_ImageEncoding_WitnessTable")]
            public static extern IntPtr GetWitnessTable();
        }
    }
    
    
    public unsafe class ImageEncodingContext : ISwiftObject
    {
        private unsafe Swift.Nuke.ImageRequest Request_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_request_Get_638AE36B(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageEncodingContextV7requestAA0B7RequestVvg")]
        private static extern void PInvoke_request_Get_638AE36B( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Swift.Nuke.ImageRequest Request
        {
            get => Request_Get();
        }
        
        private unsafe UIKit.UIImage Image_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<UIKit.UIImage>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_image_Get_7F8E9650(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<UIKit.UIImage>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageEncodingContextV5imageSo7UIImageCvg")]
        private static extern void PInvoke_image_Get_7F8E9650( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public UIKit.UIImage Image
        {
            get => Image_Get();
        }
        
        private unsafe Swift.SwiftOptional<Foundation.NSUrlResponse> UrlResponse_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_urlResponse_Get_47AE6450(self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Foundation.NSUrlResponse>>(new IntPtr(&result));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageEncodingContextV11urlResponseSo13NSURLResponseCSgvg")]
        private static extern IntPtr PInvoke_urlResponse_Get_47AE6450( SwiftSelf self);
        
        public Foundation.NSUrlResponse? UrlResponse
        {
            get => ((Foundation.NSUrlResponse?)UrlResponse_Get());
        }
        
        static nuint _payloadSize = SwiftObjectHelper<ImageEncodingContext>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageEncodingContext> _payload = SwiftSafeHandle<ImageEncodingContext>.Zero;
        
        internal SwiftSafeHandle<ImageEncodingContext> Payload => _payload;
        
        public void Dispose() => _payload.Dispose();
        
        static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageEncodingContextVMa")]
        internal static extern TypeMetadata PInvoke_getMetadata();
        
        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
        {
            return new ImageEncodingContext(handle);
        }
        
        ImageEncodingContext(SwiftHandle handle)
        {
            _payload = new SwiftSafeHandle<ImageEncodingContext>(handle);
        }
        
        unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            var metadata = SwiftObjectHelper<ImageEncodingContext>.GetTypeMetadata();
            if ((int)metadata.Size > swiftDestSpan.Length)
            {
                throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
            }
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure that the instance is valid before making copy
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
        
        private static Dictionary<Type, string> _protocolConformanceSymbols;
        static ImageEncodingContext()
        {
            _protocolConformanceSymbols = new Dictionary<Type, string>
            {
                
            };
        }
        
        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
            where TProtocol : class
        {
            if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
            {
                throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type ImageEncodingContext and protocol {typeof(TProtocol).Name}, but no conformance was found.");
            }
            return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
        }
        
        
    }
    
    
    public interface IDataLoading
    {
        [global::Swift.UnsupportedSwiftType("Existential type fallback", "any Nuke.Cancellable")]
        ICancellable LoadData(Swift.URLRequest with, Action<Swift.Data, Foundation.NSUrlResponse> didReceiveData, Action<Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1>> completion);
    }
    
    /// <summary>
    /// Proxy class that enables C# implementations of the DataLoading protocol.
    /// Can wrap either a C# implementation or receive Swift existential containers.
    /// </summary>
    public unsafe class DataLoadingProxy : IDataLoading, ISwiftObject, Swift.Runtime.ISwiftExistentialConvertible<ExistentialContainer1>
    {
        /// <summary>Matches Swift DataLoading_vtable layout</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct DataLoadingSwiftVTable
        {
            public IntPtr csVTHandle;
            public IntPtr func_loadData_0;
        }
        
        /// <summary>Local vtable holding managed delegates</summary>
        private struct DataLoadingLocalVTable
        {
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> Func_loadData_0;
        }
        
        private static IntPtr _protocolWitnessTable;
        private static DataLoadingSwiftVTable _swiftVTable;
        private static DataLoadingLocalVTable _localVTable;
        private static GCHandle _localVTableHandle;
        private static bool _vtableInitialized;
        private static readonly object _vtableLock = new object();
        private readonly IDataLoading? _csharpImpl;
        private readonly EveryProtocol? _everyProtocol;
        private ExistentialContainer1 _swiftContainer;
        static DataLoadingProxy()
        {
            InitializeVtable();
        }
        
        private static void InitializeVtable()
        {
            lock (_vtableLock)
            {
                if (_vtableInitialized) return;
                
                _localVTable = new DataLoadingLocalVTable
                {
                    Func_loadData_0 = &Receive_loadData_0,
                };
                
                _localVTableHandle = GCHandle.Alloc(_localVTable, GCHandleType.Pinned);
                
                _swiftVTable = new DataLoadingSwiftVTable
                {
                    csVTHandle = GCHandle.ToIntPtr(_localVTableHandle),
                    func_loadData_0 = (IntPtr)_localVTable.Func_loadData_0,
                };
                
                fixed (DataLoadingSwiftVTable* vtPtr = &_swiftVTable)
                {
                    NativeMethods.SetDataLoading_vtable((IntPtr)vtPtr);
                }
                _vtableInitialized = true;
            }
        }
        
        #region Swift Callback Receivers
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_loadData_0(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1, IntPtr rawArg2)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<DataLoadingProxy>(container);
            var param0 = MarshalFromSwift<Swift.URLRequest>(rawArg0);
            var param1 = MarshalFromSwift<Action<Swift.Data, Foundation.NSUrlResponse>>(rawArg1);
            var param2 = MarshalFromSwift<Action<Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1>>>(rawArg2);
            var result = proxy._csharpImpl!.LoadData(param0, param1, param2);
            return MarshalToSwiftBuffer(result);
        }
        
        #endregion
        
        /// <summary>
        /// Creates a proxy wrapping a C# implementation of IDataLoading.
        /// </summary>
        /// <param name="implementation">The C# implementation of the protocol.</param>
        public DataLoadingProxy(IDataLoading implementation)
        {
            _csharpImpl = implementation ?? throw new ArgumentNullException(nameof(implementation));
            _everyProtocol = new EveryProtocol();
            // Create existential container manually
            // The container holds: payload (EveryProtocol pointer), metadata, and witness table
            _swiftContainer = new ExistentialContainer1();
            _swiftContainer.Payload0 = _everyProtocol.Handle;
            _swiftContainer.ObjectMetadata = EveryProtocol.GetTypeMetadata();
            _swiftContainer[0] = ProtocolWitnessTableHandle;
            // Register this proxy so Swift callbacks can find us
            SwiftObjectRegistry.RegisterStrong(_everyProtocol.Handle, this);
        }
        /// <summary>
        /// Creates a proxy from an existing Swift existential container.
        /// Use this when receiving protocol values from Swift code.
        /// </summary>
        /// <remarks>
        /// Swift-backed proxies created with this constructor dispatch blittable and String
        /// protocol members through witness table accessors. Non-dispatchable members
        /// (non-blittable non-String types, throwing, async) throw <see cref="NotSupportedException"/>.
        /// </remarks>
        /// <param name="container">The Swift existential container.</param>
        public DataLoadingProxy(ExistentialContainer1 container)
        {
            _swiftContainer = container;
            _csharpImpl = null;
            _everyProtocol = null;
        }
        #region Interface Implementation
        
        public ICancellable LoadData(Swift.URLRequest with, Action<Swift.Data, Foundation.NSUrlResponse> didReceiveData, Action<Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1>> completion)
        {
            if (_csharpImpl != null)
                return _csharpImpl.LoadData(with, didReceiveData, completion);
            throw new NotSupportedException(
                "Cannot call method 'LoadData' on a Swift-backed existential container. " +
                "Protocol member access is only supported when wrapping a C# implementation.");
        }
        
        #endregion
        
        #region ISwiftObject Implementation
        /// <summary>
        /// Gets the protocol witness table handle for EveryProtocol conforming to DataLoading.
        /// </summary>
        public static IntPtr ProtocolWitnessTableHandle
        {
            get
            {
                if (_protocolWitnessTable == IntPtr.Zero)
                {
                    // The witness table is generated by the Swift compiler
                    // It will be available after the Swift wrapper is loaded
                    // For now, we look it up dynamically
                    _protocolWitnessTable = GetWitnessTableFromSwift();
                }
                return _protocolWitnessTable;
            }
        }
        private static IntPtr GetWitnessTableFromSwift()
        {
            // Call the Swift-exported function that returns the witness table pointer
            // This function is generated by EveryProtocolEmitter.EmitWitnessTableGetter()
            return NativeMethods.GetWitnessTable();
        }
        /// <summary>
        /// Gets the existential container that can be passed to Swift code.
        /// </summary>
        public ExistentialContainer1 GetExistentialContainer() => _swiftContainer;
        public static TypeMetadata GetTypeMetadata()
        {
            // Proxy classes don't have their own Swift metadata
            // They use the EveryProtocol metadata
            return EveryProtocol.GetTypeMetadata();
        }
        public static ISwiftObject NewFromPayload(IntPtr payload)
        {
            // Create from existential container
            var container = *(ExistentialContainer1*)payload;
            return new DataLoadingProxy(container);
        }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            // Marshal the existential container
            var size = _swiftContainer.SizeOf;
            if (swiftDestSpan.Length < size)
                throw new ArgumentException("Destination span too small", nameof(swiftDestSpan));
            fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
            {
                new Span<byte>(containerPtr, size).CopyTo(swiftDestSpan);
            }
            return size;
        }
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
        {
            throw new NotSupportedException(
                "Protocol conformance descriptor is not available for proxy types. " +
                "Proxy classes use EveryProtocol's witness table, not native conformance descriptors.");
        }
        public void Dispose() { }
        #endregion
        #region Marshalling Helpers
        [StructLayout(LayoutKind.Sequential)]
        private struct Utf8Slice
        {
            public IntPtr Ptr;
            public nint Len;
        }
        private static IntPtr MarshalToSwiftBuffer<T>(T value)
        {
            // Use direct memory operations for all types
            // This works for blittable types (value types, structs with blittable fields)
            var size = Unsafe.SizeOf<T>();
            var ptr = (IntPtr)NativeMemory.Alloc((nuint)size);
            Unsafe.Write((void*)ptr, value);
            return ptr;
        }
        private static T MarshalFromSwift<T>(IntPtr ptr)
        {
            // Use direct memory operations for all types
            return Unsafe.Read<T>((void*)ptr);
        }
        #endregion
        private static class NativeMethods
        {
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SetDataLoading_vtable")]
            public static extern void SetDataLoading_vtable(IntPtr vtable);
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "Get_EveryProtocol_DataLoading_WitnessTable")]
            public static extern IntPtr GetWitnessTable();
        }
    }
    
    
    public interface ICancellable
    {
        void Cancel();
    }
    
    /// <summary>
    /// Proxy class that enables C# implementations of the Cancellable protocol.
    /// Can wrap either a C# implementation or receive Swift existential containers.
    /// </summary>
    public unsafe class CancellableProxy : ICancellable, ISwiftObject, Swift.Runtime.ISwiftExistentialConvertible<ExistentialContainer1>
    {
        /// <summary>Matches Swift Cancellable_vtable layout</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct CancellableSwiftVTable
        {
            public IntPtr csVTHandle;
            public IntPtr func_cancel_0;
        }
        
        /// <summary>Local vtable holding managed delegates</summary>
        private struct CancellableLocalVTable
        {
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> Func_cancel_0;
        }
        
        private static IntPtr _protocolWitnessTable;
        private static CancellableSwiftVTable _swiftVTable;
        private static CancellableLocalVTable _localVTable;
        private static GCHandle _localVTableHandle;
        private static bool _vtableInitialized;
        private static readonly object _vtableLock = new object();
        private readonly ICancellable? _csharpImpl;
        private readonly EveryProtocol? _everyProtocol;
        private ExistentialContainer1 _swiftContainer;
        static CancellableProxy()
        {
            InitializeVtable();
        }
        
        private static void InitializeVtable()
        {
            lock (_vtableLock)
            {
                if (_vtableInitialized) return;
                
                _localVTable = new CancellableLocalVTable
                {
                    Func_cancel_0 = &Receive_cancel_0,
                };
                
                _localVTableHandle = GCHandle.Alloc(_localVTable, GCHandleType.Pinned);
                
                _swiftVTable = new CancellableSwiftVTable
                {
                    csVTHandle = GCHandle.ToIntPtr(_localVTableHandle),
                    func_cancel_0 = (IntPtr)_localVTable.Func_cancel_0,
                };
                
                fixed (CancellableSwiftVTable* vtPtr = &_swiftVTable)
                {
                    NativeMethods.SetCancellable_vtable((IntPtr)vtPtr);
                }
                _vtableInitialized = true;
            }
        }
        
        #region Swift Callback Receivers
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Receive_cancel_0(IntPtr vtHandle, IntPtr selfContainer)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<CancellableProxy>(container);
            proxy._csharpImpl!.Cancel();
        }
        
        #endregion
        
        /// <summary>
        /// Creates a proxy wrapping a C# implementation of ICancellable.
        /// </summary>
        /// <param name="implementation">The C# implementation of the protocol.</param>
        public CancellableProxy(ICancellable implementation)
        {
            _csharpImpl = implementation ?? throw new ArgumentNullException(nameof(implementation));
            _everyProtocol = new EveryProtocol();
            // Create existential container manually
            // The container holds: payload (EveryProtocol pointer), metadata, and witness table
            _swiftContainer = new ExistentialContainer1();
            _swiftContainer.Payload0 = _everyProtocol.Handle;
            _swiftContainer.ObjectMetadata = EveryProtocol.GetTypeMetadata();
            _swiftContainer[0] = ProtocolWitnessTableHandle;
            // Register this proxy so Swift callbacks can find us
            SwiftObjectRegistry.RegisterStrong(_everyProtocol.Handle, this);
        }
        /// <summary>
        /// Creates a proxy from an existing Swift existential container.
        /// Use this when receiving protocol values from Swift code.
        /// </summary>
        /// <remarks>
        /// Swift-backed proxies created with this constructor dispatch blittable and String
        /// protocol members through witness table accessors. Non-dispatchable members
        /// (non-blittable non-String types, throwing, async) throw <see cref="NotSupportedException"/>.
        /// </remarks>
        /// <param name="container">The Swift existential container.</param>
        public CancellableProxy(ExistentialContainer1 container)
        {
            _swiftContainer = container;
            _csharpImpl = null;
            _everyProtocol = null;
        }
        #region Interface Implementation
        
        public void Cancel()
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.Cancel();
                return;
            }
            fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
            {
                NativeMethods.SBW_Cancellable_method_cancel_0((IntPtr)containerPtr);
            }
        }
        
        #endregion
        
        #region ISwiftObject Implementation
        /// <summary>
        /// Gets the protocol witness table handle for EveryProtocol conforming to Cancellable.
        /// </summary>
        public static IntPtr ProtocolWitnessTableHandle
        {
            get
            {
                if (_protocolWitnessTable == IntPtr.Zero)
                {
                    // The witness table is generated by the Swift compiler
                    // It will be available after the Swift wrapper is loaded
                    // For now, we look it up dynamically
                    _protocolWitnessTable = GetWitnessTableFromSwift();
                }
                return _protocolWitnessTable;
            }
        }
        private static IntPtr GetWitnessTableFromSwift()
        {
            // Call the Swift-exported function that returns the witness table pointer
            // This function is generated by EveryProtocolEmitter.EmitWitnessTableGetter()
            return NativeMethods.GetWitnessTable();
        }
        /// <summary>
        /// Gets the existential container that can be passed to Swift code.
        /// </summary>
        public ExistentialContainer1 GetExistentialContainer() => _swiftContainer;
        public static TypeMetadata GetTypeMetadata()
        {
            // Proxy classes don't have their own Swift metadata
            // They use the EveryProtocol metadata
            return EveryProtocol.GetTypeMetadata();
        }
        public static ISwiftObject NewFromPayload(IntPtr payload)
        {
            // Create from existential container
            var container = *(ExistentialContainer1*)payload;
            return new CancellableProxy(container);
        }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            // Marshal the existential container
            var size = _swiftContainer.SizeOf;
            if (swiftDestSpan.Length < size)
                throw new ArgumentException("Destination span too small", nameof(swiftDestSpan));
            fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
            {
                new Span<byte>(containerPtr, size).CopyTo(swiftDestSpan);
            }
            return size;
        }
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
        {
            throw new NotSupportedException(
                "Protocol conformance descriptor is not available for proxy types. " +
                "Proxy classes use EveryProtocol's witness table, not native conformance descriptors.");
        }
        public void Dispose() { }
        #endregion
        #region Marshalling Helpers
        [StructLayout(LayoutKind.Sequential)]
        private struct Utf8Slice
        {
            public IntPtr Ptr;
            public nint Len;
        }
        private static IntPtr MarshalToSwiftBuffer<T>(T value)
        {
            // Use direct memory operations for all types
            // This works for blittable types (value types, structs with blittable fields)
            var size = Unsafe.SizeOf<T>();
            var ptr = (IntPtr)NativeMemory.Alloc((nuint)size);
            Unsafe.Write((void*)ptr, value);
            return ptr;
        }
        private static T MarshalFromSwift<T>(IntPtr ptr)
        {
            // Use direct memory operations for all types
            return Unsafe.Read<T>((void*)ptr);
        }
        #endregion
        private static class NativeMethods
        {
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SetCancellable_vtable")]
            public static extern void SetCancellable_vtable(IntPtr vtable);
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "Get_EveryProtocol_Cancellable_WitnessTable")]
            public static extern IntPtr GetWitnessTable();
            
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SBW_Cancellable_method_cancel_0")]
            public static extern void SBW_Cancellable_method_cancel_0(IntPtr containerPtr);
        }
    }
    
    
    public unsafe class ImageContainer : ISwiftObject
    {
        private unsafe UIKit.UIImage Image_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<UIKit.UIImage>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_image_Get_003349C9(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<UIKit.UIImage>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV5imageSo7UIImageCvg")]
        private static extern void PInvoke_image_Get_003349C9( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Image_Set( UIKit.UIImage value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            IntPtr valueHandle = value?.Handle ?? IntPtr.Zero;
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_image_Set_40BC01C6(valueHandle, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV5imageSo7UIImageCvs")]
        private static extern void PInvoke_image_Set_40BC01C6( IntPtr value,  SwiftSelf self);
        
        public UIKit.UIImage Image
        {
            get => Image_Get();
            set => Image_Set(value);
        }
        
        private unsafe Swift.SwiftOptional<Swift.Nuke.AssetType> Type_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_type_Get_5FE0669D(self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Nuke.AssetType>>(new IntPtr(&result));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV4typeAA9AssetTypeVSgvg")]
        private static extern IntPtr PInvoke_type_Get_5FE0669D( SwiftSelf self);
        
        private unsafe void Type_Set( Swift.SwiftOptional<Swift.Nuke.AssetType> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_type_Set_770D8B05(valueBuffer, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV4typeAA9AssetTypeVSgvs")]
        private static extern void PInvoke_type_Set_770D8B05( IntPtr valueBuffer,  SwiftSelf self);
        
        public Swift.Nuke.AssetType? Type
        {
            get => ((Swift.Nuke.AssetType?)Type_Get());
            set => Type_Set((value is {} valueVal ? SwiftOptional<Swift.Nuke.AssetType>.NewSome(valueVal) : SwiftOptional<Swift.Nuke.AssetType>.NewNone()));
        }
        
        private unsafe System.Boolean IsPreview_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_isPreview_Get_04553A83(self);
                
                return result;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV9isPreviewSbvg")]
        private static extern System.Boolean PInvoke_isPreview_Get_04553A83( SwiftSelf self);
        
        private unsafe void IsPreview_Set( System.Boolean value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_isPreview_Set_5586DB75(value, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV9isPreviewSbvs")]
        private static extern void PInvoke_isPreview_Set_5586DB75( System.Boolean value,  SwiftSelf self);
        
        public System.Boolean IsPreview
        {
            get => IsPreview_Get();
            set => IsPreview_Set(value);
        }
        
        private unsafe Swift.SwiftOptional<Swift.Data> Data_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_data_Get_4DE9D2A1(self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Data>>(new IntPtr(&result));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV4data10Foundation4DataVSgvg")]
        private static extern IntPtr PInvoke_data_Get_4DE9D2A1( SwiftSelf self);
        
        private unsafe void Data_Set( Swift.SwiftOptional<Swift.Data> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_data_Set_1D93FB10(valueBuffer, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV4data10Foundation4DataVSgvs")]
        private static extern void PInvoke_data_Set_1D93FB10( IntPtr valueBuffer,  SwiftSelf self);
        
        public Swift.Data? Data
        {
            get => ((Swift.Data?)Data_Get());
            set => Data_Set((value is {} valueVal ? SwiftOptional<Swift.Data>.NewSome(valueVal) : SwiftOptional<Swift.Data>.NewNone()));
        }
        
        static nuint _payloadSize = SwiftObjectHelper<ImageContainer>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageContainer> _payload = SwiftSafeHandle<ImageContainer>.Zero;
        
        internal SwiftSafeHandle<ImageContainer> Payload => _payload;
        
        public void Dispose() => _payload.Dispose();
        
        static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerVMa")]
        internal static extern TypeMetadata PInvoke_getMetadata();
        
        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
        {
            return new ImageContainer(handle);
        }
        
        ImageContainer(SwiftHandle handle)
        {
            _payload = new SwiftSafeHandle<ImageContainer>(handle);
        }
        
        unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            var metadata = SwiftObjectHelper<ImageContainer>.GetTypeMetadata();
            if ((int)metadata.Size > swiftDestSpan.Length)
            {
                throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
            }
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure that the instance is valid before making copy
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
        
        private static Dictionary<Type, string> _protocolConformanceSymbols;
        static ImageContainer()
        {
            _protocolConformanceSymbols = new Dictionary<Type, string>
            {
                
            };
        }
        
        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
            where TProtocol : class
        {
            if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
            {
                throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type ImageContainer and protocol {typeof(TProtocol).Name}, but no conformance was found.");
            }
            return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
        }
        
        
        public unsafe class UserInfoKey : ISwiftObject, IEquatable<UserInfoKey>
        {
            private unsafe Swift.SwiftString RawValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_rawValue_Get_0B76C865(self);
                    
                    unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV11UserInfoKeyV8rawValueSSvg")]
            private static extern Swift.SwiftString.Buffer PInvoke_rawValue_Get_0B76C865( SwiftSelf self);
            
            public string RawValue
            {
                get => RawValue_Get().ToString();
            }
            
            private static unsafe Swift.Nuke.ImageContainer.UserInfoKey ScanNumberKey_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageContainer.UserInfoKey>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_scanNumberKey_Get_04DC267C(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageContainer.UserInfoKey>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV11UserInfoKeyV010scanNumberF0AEvgZ")]
            private static extern void PInvoke_scanNumberKey_Get_04DC267C( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageContainer.UserInfoKey ScanNumberKey
            {
                get => ScanNumberKey_Get();
            }
            
            private unsafe nint HashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_6966B378(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV11UserInfoKeyV9hashValueSivg")]
            private static extern nint PInvoke_hashValue_Get_6966B378( SwiftSelf self);
            
            public nint HashValue
            {
                get => HashValue_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<UserInfoKey>.GetTypeMetadata().Size;
            SwiftSafeHandle<UserInfoKey> _payload = SwiftSafeHandle<UserInfoKey>.Zero;
            
            internal SwiftSafeHandle<UserInfoKey> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            public static System.Boolean operator ==(Swift.Nuke.ImageContainer.UserInfoKey arg0, Swift.Nuke.ImageContainer.UserInfoKey arg1)
            {
                if (arg0 is null) return arg1 is null;
                if (arg1 is null) return false;
                return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
            }
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV11UserInfoKeyV2eeoiySbAE_AEtFZ")]
            private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
            
            public static bool operator !=(UserInfoKey left, UserInfoKey right)
            {
                if (left is null) return right is not null;
                if (right is null) return true;
                return !(left == right);
            }
            
            public override bool Equals(object? obj)
            {
                return obj is UserInfoKey other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            public override int GetHashCode()
            {
                // TODO: Implement when Swift Hashable protocol binding is supported.
                // Returning constant 0 satisfies the Equals/GetHashCode contract
                // (equal objects must have equal hashes). This is correct but makes
                // hash-based collections O(n) until Hashable is supported.
                return 0;
            }
            
            public bool Equals(UserInfoKey? other)
            {
                return Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV11UserInfoKeyVMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new UserInfoKey(handle);
            }
            
            UserInfoKey(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<UserInfoKey>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<UserInfoKey>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static UserInfoKey()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IEquatable<UserInfoKey>), "$s4Nuke14ImageContainerV11UserInfoKeyVSQAAMc"}
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type UserInfoKey and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
            
            public unsafe UserInfoKey( string arg0)
            {
                _payload = new SwiftSafeHandle<UserInfoKey>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                using var arg0Swift = new SwiftString(arg0);
                using PayloadBuffer<SwiftString.Buffer> arg0Disposable = arg0Swift.PayloadBuffer;
                PInvoke_init_7560907C(swiftIndirectResult, arg0Disposable.Buffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV11UserInfoKeyVyAESScfC")]
            private static extern void PInvoke_init_7560907C( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer arg0);
            
            
            public unsafe void Hash(ref Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_7A7FD264(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV11UserInfoKeyV4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_7A7FD264( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        
    }
    
    
    public interface IDataCaching
    {
        Swift.Data? CachedData(string _for);
        System.Boolean ContainsData(string _for);
        void StoreData(Foundation.NSData arg0, string _for);
        void RemoveData(string _for);
        void RemoveAll();
    }
    
    /// <summary>
    /// Proxy class that enables C# implementations of the DataCaching protocol.
    /// Can wrap either a C# implementation or receive Swift existential containers.
    /// </summary>
    public unsafe class DataCachingProxy : IDataCaching, ISwiftObject, Swift.Runtime.ISwiftExistentialConvertible<ExistentialContainer1>
    {
        /// <summary>Matches Swift DataCaching_vtable layout</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct DataCachingSwiftVTable
        {
            public IntPtr csVTHandle;
            public IntPtr func_cachedData_0;
            public IntPtr func_containsData_1;
            public IntPtr func_storeData_2;
            public IntPtr func_removeData_3;
            public IntPtr func_removeAll_4;
        }
        
        /// <summary>Local vtable holding managed delegates</summary>
        private struct DataCachingLocalVTable
        {
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr> Func_cachedData_0;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr> Func_containsData_1;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void> Func_storeData_2;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void> Func_removeData_3;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> Func_removeAll_4;
        }
        
        private static IntPtr _protocolWitnessTable;
        private static DataCachingSwiftVTable _swiftVTable;
        private static DataCachingLocalVTable _localVTable;
        private static GCHandle _localVTableHandle;
        private static bool _vtableInitialized;
        private static readonly object _vtableLock = new object();
        private readonly IDataCaching? _csharpImpl;
        private readonly EveryProtocol? _everyProtocol;
        private ExistentialContainer1 _swiftContainer;
        static DataCachingProxy()
        {
            InitializeVtable();
        }
        
        private static void InitializeVtable()
        {
            lock (_vtableLock)
            {
                if (_vtableInitialized) return;
                
                _localVTable = new DataCachingLocalVTable
                {
                    Func_cachedData_0 = &Receive_cachedData_0,
                    Func_containsData_1 = &Receive_containsData_1,
                    Func_storeData_2 = &Receive_storeData_2,
                    Func_removeData_3 = &Receive_removeData_3,
                    Func_removeAll_4 = &Receive_removeAll_4,
                };
                
                _localVTableHandle = GCHandle.Alloc(_localVTable, GCHandleType.Pinned);
                
                _swiftVTable = new DataCachingSwiftVTable
                {
                    csVTHandle = GCHandle.ToIntPtr(_localVTableHandle),
                    func_cachedData_0 = (IntPtr)_localVTable.Func_cachedData_0,
                    func_containsData_1 = (IntPtr)_localVTable.Func_containsData_1,
                    func_storeData_2 = (IntPtr)_localVTable.Func_storeData_2,
                    func_removeData_3 = (IntPtr)_localVTable.Func_removeData_3,
                    func_removeAll_4 = (IntPtr)_localVTable.Func_removeAll_4,
                };
                
                fixed (DataCachingSwiftVTable* vtPtr = &_swiftVTable)
                {
                    NativeMethods.SetDataCaching_vtable((IntPtr)vtPtr);
                }
                _vtableInitialized = true;
            }
        }
        
        #region Swift Callback Receivers
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_cachedData_0(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<DataCachingProxy>(container);
            var param0 = MarshalFromSwift<Swift.SwiftString>(rawArg0);
            var result = proxy._csharpImpl!.CachedData(param0);
            return MarshalToSwiftBuffer(result);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_containsData_1(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<DataCachingProxy>(container);
            var param0 = MarshalFromSwift<Swift.SwiftString>(rawArg0);
            var result = proxy._csharpImpl!.ContainsData(param0);
            return MarshalToSwiftBuffer(result);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Receive_storeData_2(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<DataCachingProxy>(container);
            var param0 = MarshalFromSwift<Swift.Data>(rawArg0);
            var param1 = MarshalFromSwift<Swift.SwiftString>(rawArg1);
            proxy._csharpImpl!.StoreData(param0, param1);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Receive_removeData_3(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<DataCachingProxy>(container);
            var param0 = MarshalFromSwift<Swift.SwiftString>(rawArg0);
            proxy._csharpImpl!.RemoveData(param0);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Receive_removeAll_4(IntPtr vtHandle, IntPtr selfContainer)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<DataCachingProxy>(container);
            proxy._csharpImpl!.RemoveAll();
        }
        
        #endregion
        
        /// <summary>
        /// Creates a proxy wrapping a C# implementation of IDataCaching.
        /// </summary>
        /// <param name="implementation">The C# implementation of the protocol.</param>
        public DataCachingProxy(IDataCaching implementation)
        {
            _csharpImpl = implementation ?? throw new ArgumentNullException(nameof(implementation));
            _everyProtocol = new EveryProtocol();
            // Create existential container manually
            // The container holds: payload (EveryProtocol pointer), metadata, and witness table
            _swiftContainer = new ExistentialContainer1();
            _swiftContainer.Payload0 = _everyProtocol.Handle;
            _swiftContainer.ObjectMetadata = EveryProtocol.GetTypeMetadata();
            _swiftContainer[0] = ProtocolWitnessTableHandle;
            // Register this proxy so Swift callbacks can find us
            SwiftObjectRegistry.RegisterStrong(_everyProtocol.Handle, this);
        }
        /// <summary>
        /// Creates a proxy from an existing Swift existential container.
        /// Use this when receiving protocol values from Swift code.
        /// </summary>
        /// <remarks>
        /// Swift-backed proxies created with this constructor dispatch blittable and String
        /// protocol members through witness table accessors. Non-dispatchable members
        /// (non-blittable non-String types, throwing, async) throw <see cref="NotSupportedException"/>.
        /// </remarks>
        /// <param name="container">The Swift existential container.</param>
        public DataCachingProxy(ExistentialContainer1 container)
        {
            _swiftContainer = container;
            _csharpImpl = null;
            _everyProtocol = null;
        }
        #region Interface Implementation
        
        public Swift.Data? CachedData(string _for)
        {
            if (_csharpImpl != null)
                return _csharpImpl.CachedData(_for);
            throw new NotSupportedException(
                "Cannot call method 'CachedData' on a Swift-backed existential container. " +
                "Protocol member access is only supported when wrapping a C# implementation.");
        }
        
        public System.Boolean ContainsData(string _for)
        {
            if (_csharpImpl != null)
                return _csharpImpl.ContainsData(_for);
            fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
            {
                var arg0Handle = default(GCHandle);
                try
                {
                    var arg0Bytes = System.Text.Encoding.UTF8.GetBytes(_for ?? string.Empty);
                    arg0Handle = GCHandle.Alloc(arg0Bytes, GCHandleType.Pinned);
                    var arg0Slice = new Utf8Slice { Ptr = arg0Handle.AddrOfPinnedObject(), Len = (nint)arg0Bytes.Length };
                    IntPtr resultPtr = NativeMethods.SBW_DataCaching_method_containsData_1((IntPtr)containerPtr, (IntPtr)(&arg0Slice));
                    try { return MarshalFromSwift<bool>(resultPtr); }
                    finally
                    {
                        NativeMethods.SBW_DataCaching_free_method_containsData_1(resultPtr);
                    }
                }
                finally
                {
                    if (arg0Handle.IsAllocated) arg0Handle.Free();
                }
            }
        }
        
        public void StoreData(Foundation.NSData arg0, string _for)
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.StoreData(arg0, _for);
                return;
            }
            throw new NotSupportedException(
                "Cannot call method 'StoreData' on a Swift-backed existential container. " +
                "Protocol member access is only supported when wrapping a C# implementation.");
        }
        
        public void RemoveData(string _for)
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.RemoveData(_for);
                return;
            }
            fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
            {
                var arg0Handle = default(GCHandle);
                try
                {
                    var arg0Bytes = System.Text.Encoding.UTF8.GetBytes(_for ?? string.Empty);
                    arg0Handle = GCHandle.Alloc(arg0Bytes, GCHandleType.Pinned);
                    var arg0Slice = new Utf8Slice { Ptr = arg0Handle.AddrOfPinnedObject(), Len = (nint)arg0Bytes.Length };
                    NativeMethods.SBW_DataCaching_method_removeData_3((IntPtr)containerPtr, (IntPtr)(&arg0Slice));
                }
                finally
                {
                    if (arg0Handle.IsAllocated) arg0Handle.Free();
                }
            }
        }
        
        public void RemoveAll()
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.RemoveAll();
                return;
            }
            fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
            {
                NativeMethods.SBW_DataCaching_method_removeAll_4((IntPtr)containerPtr);
            }
        }
        
        #endregion
        
        #region ISwiftObject Implementation
        /// <summary>
        /// Gets the protocol witness table handle for EveryProtocol conforming to DataCaching.
        /// </summary>
        public static IntPtr ProtocolWitnessTableHandle
        {
            get
            {
                if (_protocolWitnessTable == IntPtr.Zero)
                {
                    // The witness table is generated by the Swift compiler
                    // It will be available after the Swift wrapper is loaded
                    // For now, we look it up dynamically
                    _protocolWitnessTable = GetWitnessTableFromSwift();
                }
                return _protocolWitnessTable;
            }
        }
        private static IntPtr GetWitnessTableFromSwift()
        {
            // Call the Swift-exported function that returns the witness table pointer
            // This function is generated by EveryProtocolEmitter.EmitWitnessTableGetter()
            return NativeMethods.GetWitnessTable();
        }
        /// <summary>
        /// Gets the existential container that can be passed to Swift code.
        /// </summary>
        public ExistentialContainer1 GetExistentialContainer() => _swiftContainer;
        public static TypeMetadata GetTypeMetadata()
        {
            // Proxy classes don't have their own Swift metadata
            // They use the EveryProtocol metadata
            return EveryProtocol.GetTypeMetadata();
        }
        public static ISwiftObject NewFromPayload(IntPtr payload)
        {
            // Create from existential container
            var container = *(ExistentialContainer1*)payload;
            return new DataCachingProxy(container);
        }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            // Marshal the existential container
            var size = _swiftContainer.SizeOf;
            if (swiftDestSpan.Length < size)
                throw new ArgumentException("Destination span too small", nameof(swiftDestSpan));
            fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
            {
                new Span<byte>(containerPtr, size).CopyTo(swiftDestSpan);
            }
            return size;
        }
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
        {
            throw new NotSupportedException(
                "Protocol conformance descriptor is not available for proxy types. " +
                "Proxy classes use EveryProtocol's witness table, not native conformance descriptors.");
        }
        public void Dispose() { }
        #endregion
        #region Marshalling Helpers
        [StructLayout(LayoutKind.Sequential)]
        private struct Utf8Slice
        {
            public IntPtr Ptr;
            public nint Len;
        }
        private static IntPtr MarshalToSwiftBuffer<T>(T value)
        {
            // Use direct memory operations for all types
            // This works for blittable types (value types, structs with blittable fields)
            var size = Unsafe.SizeOf<T>();
            var ptr = (IntPtr)NativeMemory.Alloc((nuint)size);
            Unsafe.Write((void*)ptr, value);
            return ptr;
        }
        private static T MarshalFromSwift<T>(IntPtr ptr)
        {
            // Use direct memory operations for all types
            return Unsafe.Read<T>((void*)ptr);
        }
        #endregion
        private static class NativeMethods
        {
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SetDataCaching_vtable")]
            public static extern void SetDataCaching_vtable(IntPtr vtable);
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "Get_EveryProtocol_DataCaching_WitnessTable")]
            public static extern IntPtr GetWitnessTable();
            
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SBW_DataCaching_method_containsData_1")]
            public static extern IntPtr SBW_DataCaching_method_containsData_1(IntPtr containerPtr, IntPtr arg0Ptr);
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SBW_DataCaching_free_method_containsData_1")]
            public static extern void SBW_DataCaching_free_method_containsData_1(IntPtr ptr);
            
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SBW_DataCaching_method_removeData_3")]
            public static extern void SBW_DataCaching_method_removeData_3(IntPtr containerPtr, IntPtr arg0Ptr);
            
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SBW_DataCaching_method_removeAll_4")]
            public static extern void SBW_DataCaching_method_removeAll_4(IntPtr containerPtr);
        }
    }
    
    
    public unsafe class ImageProcessingOptions : ISwiftObject
    {
        static nuint _payloadSize = SwiftObjectHelper<ImageProcessingOptions>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageProcessingOptions> _payload = SwiftSafeHandle<ImageProcessingOptions>.Zero;
        internal SwiftSafeHandle<ImageProcessingOptions> Payload => _payload;
        
        public void Dispose() => _payload.Dispose();
        
        static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsOMa")]
        internal static extern TypeMetadata PInvoke_getMetadata();
        
        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
        {
            return new ImageProcessingOptions(handle);
        }
        
        ImageProcessingOptions()
        {
        }
        
        ImageProcessingOptions(SwiftHandle handle)
        {
            _payload = new SwiftSafeHandle<ImageProcessingOptions>(handle);
        }
        
        unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            var metadata = SwiftObjectHelper<ImageProcessingOptions>.GetTypeMetadata();
            if ((int)metadata.Size > swiftDestSpan.Length)
            {
                throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
            }
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure that the instance is valid before making copy
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
        
        private static Dictionary<Type, string> _protocolConformanceSymbols;
        static ImageProcessingOptions()
        {
            _protocolConformanceSymbols = new Dictionary<Type, string>
            {
                
            };
        }
        
        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
            where TProtocol : class
        {
            if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
            {
                throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type ImageProcessingOptions and protocol {typeof(TProtocol).Name}, but no conformance was found.");
            }
            return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
        }
        
        public unsafe class Unit : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<Unit>.GetTypeMetadata().Size;
            SwiftSafeHandle<Unit> _payload = SwiftSafeHandle<Unit>.Zero;
            internal SwiftSafeHandle<Unit> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            /// <summary>
            /// Gets the 'points' case of Unit.
            /// </summary>
            public static Unit Points
            {
                get
                {
                    var result = new Unit();
                    var metadata = SwiftObjectHelper<Unit>.GetTypeMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    metadata.ValueWitnessTable->DestructiveInjectEnumTag((void*)buffer, (uint)0, metadata);
                    result._payload = new SwiftSafeHandle<Unit>(buffer);
                    return result;
                }
            }
            
            /// <summary>
            /// Gets the 'pixels' case of Unit.
            /// </summary>
            public static Unit Pixels
            {
                get
                {
                    var result = new Unit();
                    var metadata = SwiftObjectHelper<Unit>.GetTypeMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    metadata.ValueWitnessTable->DestructiveInjectEnumTag((void*)buffer, (uint)1, metadata);
                    result._payload = new SwiftSafeHandle<Unit>(buffer);
                    return result;
                }
            }
            
            /// <summary>
            /// Enum representing the possible cases of Unit.
            /// Tag values follow Swift's ordering: payload cases first, then no-payload cases.
            /// </summary>
            public enum CaseTag : uint
            {
                Points = 0,
                Pixels = 1,
            }
            
            /// <summary>
            /// Gets the current case of this enum instance.
            /// </summary>
            public unsafe CaseTag Tag
            {
                get
                {
                    bool success = false;
                    _payload.DangerousAddRef(ref success);
                    try
                    {
                        var metadata = SwiftObjectHelper<Unit>.GetTypeMetadata();
                        byte* payload = (byte*)_payload.DangerousGetHandle();
                        return (CaseTag)metadata.ValueWitnessTable->GetEnumTag(payload, metadata);
                    }
                    finally
                    {
                        if (success)
                            _payload.DangerousRelease();
                    }
                }
            }
            
            
            private unsafe Swift.SwiftString Description_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_0098DC3D(self);
                    
                    unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO4UnitO11descriptionSSvg")]
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_0098DC3D( SwiftSelf self);
            
            public string Description
            {
                get => Description_Get().ToString();
            }
            
            private unsafe nint HashValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_53EE39C6(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO4UnitO9hashValueSivg")]
            private static extern nint PInvoke_hashValue_Get_53EE39C6( SwiftSelf self);
            
            public nint HashValue
            {
                get => HashValue_Get();
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO4UnitOMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new Unit(handle);
            }
            
            Unit()
            {
            }
            
            Unit(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<Unit>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<Unit>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static Unit()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IEquatable<Unit>), "$s4Nuke22ImageProcessingOptionsO4UnitOSQAAMc"}
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type Unit and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
            public unsafe void Hash(ref Swift.Hasher into)
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_40CEDFED(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO4UnitO4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_40CEDFED( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        public unsafe class Border : ISwiftObject, IEquatable<Border>
        {
            private unsafe System.Double Width_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_width_Get_4D98B5E5(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO6BorderV5width12CoreGraphics7CGFloatVvg")]
            private static extern System.Double PInvoke_width_Get_4D98B5E5( SwiftSelf self);
            
            public System.Double Width
            {
                get => Width_Get();
            }
            
            private unsafe UIKit.UIColor Color_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<UIKit.UIColor>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_color_Get_14B80F9A(swiftIndirectResult, self);
                    
                    return SwiftMarshal.MarshalFromSwift<UIKit.UIColor>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO6BorderV5colorSo7UIColorCvg")]
            private static extern void PInvoke_color_Get_14B80F9A( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            public UIKit.UIColor Color
            {
                get => Color_Get();
            }
            
            private unsafe Swift.SwiftString Description_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_41EEB81A(self);
                    
                    unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO6BorderV11descriptionSSvg")]
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_41EEB81A( SwiftSelf self);
            
            public string Description
            {
                get => Description_Get().ToString();
            }
            
            private unsafe nint HashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_55CAC8D8(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO6BorderV9hashValueSivg")]
            private static extern nint PInvoke_hashValue_Get_55CAC8D8( SwiftSelf self);
            
            public nint HashValue
            {
                get => HashValue_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<Border>.GetTypeMetadata().Size;
            SwiftSafeHandle<Border> _payload = SwiftSafeHandle<Border>.Zero;
            
            internal SwiftSafeHandle<Border> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            public static System.Boolean operator ==(Swift.Nuke.ImageProcessingOptions.Border arg0, Swift.Nuke.ImageProcessingOptions.Border arg1)
            {
                if (arg0 is null) return arg1 is null;
                if (arg1 is null) return false;
                return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
            }
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO6BorderV2eeoiySbAE_AEtFZ")]
            private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
            
            public static bool operator !=(Border left, Border right)
            {
                if (left is null) return right is not null;
                if (right is null) return true;
                return !(left == right);
            }
            
            public override bool Equals(object? obj)
            {
                return obj is Border other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            public override int GetHashCode()
            {
                // TODO: Implement when Swift Hashable protocol binding is supported.
                // Returning constant 0 satisfies the Equals/GetHashCode contract
                // (equal objects must have equal hashes). This is correct but makes
                // hash-based collections O(n) until Hashable is supported.
                return 0;
            }
            
            public bool Equals(Border? other)
            {
                return Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO6BorderVMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new Border(handle);
            }
            
            Border(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<Border>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<Border>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static Border()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IEquatable<Border>), "$s4Nuke22ImageProcessingOptionsO6BorderVSQAAMc"}
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type Border and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
            
            public unsafe Border( UIKit.UIColor color,  System.Double width,  Swift.Nuke.ImageProcessingOptions.Unit unit)
            {
                IntPtr colorHandle = color?.Handle ?? IntPtr.Zero;
                _payload = new SwiftSafeHandle<Border>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_795C4E46(swiftIndirectResult, colorHandle, width, unit.Payload.DangerousGetHandle());
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO6BorderV5color5width4unitAESo7UIColorC_12CoreGraphics7CGFloatVAC4UnitOtcfC")]
            private static extern void PInvoke_init_795C4E46( SwiftIndirectResult swiftIndirectResult,  IntPtr color,  System.Double width,  IntPtr unit);
            public unsafe Border( UIKit.UIColor color)
            {
                IntPtr colorHandle = color?.Handle ?? IntPtr.Zero;
                _payload = new SwiftSafeHandle<Border>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_31D566D6(swiftIndirectResult, colorHandle);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("SwiftBindings", EntryPoint = "DBW_Border_init_46D2AA2C_2")]
            private static extern void PInvoke_init_31D566D6( SwiftIndirectResult swiftIndirectResult,  IntPtr color);
            public unsafe Border( UIKit.UIColor color,  System.Double width)
            {
                IntPtr colorHandle = color?.Handle ?? IntPtr.Zero;
                _payload = new SwiftSafeHandle<Border>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_213194F2(swiftIndirectResult, colorHandle, width);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("SwiftBindings", EntryPoint = "DBW_Border_init_46D2AA2C_1")]
            private static extern void PInvoke_init_213194F2( SwiftIndirectResult swiftIndirectResult,  IntPtr color,  System.Double width);
            
            
            public unsafe void Hash(ref Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_3781540D(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO6BorderV4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_3781540D( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        public unsafe class ContentMode : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<ContentMode>.GetTypeMetadata().Size;
            SwiftSafeHandle<ContentMode> _payload = SwiftSafeHandle<ContentMode>.Zero;
            internal SwiftSafeHandle<ContentMode> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            /// <summary>
            /// Gets the 'aspectFill' case of ContentMode.
            /// </summary>
            public static ContentMode AspectFill
            {
                get
                {
                    var result = new ContentMode();
                    var metadata = SwiftObjectHelper<ContentMode>.GetTypeMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    metadata.ValueWitnessTable->DestructiveInjectEnumTag((void*)buffer, (uint)0, metadata);
                    result._payload = new SwiftSafeHandle<ContentMode>(buffer);
                    return result;
                }
            }
            
            /// <summary>
            /// Gets the 'aspectFit' case of ContentMode.
            /// </summary>
            public static ContentMode AspectFit
            {
                get
                {
                    var result = new ContentMode();
                    var metadata = SwiftObjectHelper<ContentMode>.GetTypeMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    metadata.ValueWitnessTable->DestructiveInjectEnumTag((void*)buffer, (uint)1, metadata);
                    result._payload = new SwiftSafeHandle<ContentMode>(buffer);
                    return result;
                }
            }
            
            /// <summary>
            /// Enum representing the possible cases of ContentMode.
            /// Tag values follow Swift's ordering: payload cases first, then no-payload cases.
            /// </summary>
            public enum CaseTag : uint
            {
                AspectFill = 0,
                AspectFit = 1,
            }
            
            /// <summary>
            /// Gets the current case of this enum instance.
            /// </summary>
            public unsafe CaseTag Tag
            {
                get
                {
                    bool success = false;
                    _payload.DangerousAddRef(ref success);
                    try
                    {
                        var metadata = SwiftObjectHelper<ContentMode>.GetTypeMetadata();
                        byte* payload = (byte*)_payload.DangerousGetHandle();
                        return (CaseTag)metadata.ValueWitnessTable->GetEnumTag(payload, metadata);
                    }
                    finally
                    {
                        if (success)
                            _payload.DangerousRelease();
                    }
                }
            }
            
            
            private unsafe Swift.SwiftString Description_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_63A3723F(self);
                    
                    unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO11ContentModeO11descriptionSSvg")]
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_63A3723F( SwiftSelf self);
            
            public string Description
            {
                get => Description_Get().ToString();
            }
            
            private unsafe nint HashValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_79DF55FC(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO11ContentModeO9hashValueSivg")]
            private static extern nint PInvoke_hashValue_Get_79DF55FC( SwiftSelf self);
            
            public nint HashValue
            {
                get => HashValue_Get();
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO11ContentModeOMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new ContentMode(handle);
            }
            
            ContentMode()
            {
            }
            
            ContentMode(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<ContentMode>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<ContentMode>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static ContentMode()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IEquatable<ContentMode>), "$s4Nuke22ImageProcessingOptionsO11ContentModeOSQAAMc"}
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type ContentMode and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
            public unsafe void Hash(ref Swift.Hasher into)
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_6C5C3BE4(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO11ContentModeO4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_6C5C3BE4( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
    }
    
    
    public interface IImagePipelineDelegate
    {
        [global::Swift.UnsupportedSwiftType("Existential type fallback", "any Nuke.DataLoading")]
        IDataLoading DataLoader(Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline);
        [global::Swift.UnsupportedSwiftType("Existential type fallback", "any Nuke.ImageDecoding")]
        Swift.AnyType? ImageDecoder(Swift.Nuke.ImageDecodingContext _for, Swift.Nuke.ImagePipeline pipeline);
        [global::Swift.UnsupportedSwiftType("Existential type fallback", "any Nuke.ImageEncoding")]
        IImageEncoding ImageEncoder(Swift.Nuke.ImageEncodingContext _for, Swift.Nuke.ImagePipeline pipeline);
        [global::Swift.UnsupportedSwiftType("Existential type fallback", "any Nuke.ImageCaching")]
        Swift.AnyType? ImageCache(Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline);
        [global::Swift.UnsupportedSwiftType("Existential type fallback", "any Nuke.DataCaching")]
        Swift.AnyType? DataCache(Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline);
        Swift.SwiftString? CacheKey(Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline);
        void WillCache(Foundation.NSData data, Swift.Nuke.ImageContainer? image, Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline, Action<Swift.SwiftOptional<Swift.Data>> completion);
        System.Boolean ShouldDecompress(Swift.Nuke.ImageResponse response, Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline);
        Swift.Nuke.ImageResponse Decompress(Swift.Nuke.ImageResponse response, Swift.Nuke.ImageRequest request, Swift.Nuke.ImagePipeline pipeline);
        void ImageTaskCreated(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImagePipeline pipeline);
        void ImageTask(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImageTask.Event didReceiveEvent, Swift.Nuke.ImagePipeline pipeline);
        void ImageTaskDidStart(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImagePipeline pipeline);
        void ImageTask(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImageTask.ProgressInfo didUpdateProgress, Swift.Nuke.ImagePipeline pipeline);
        void ImageTask(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImageResponse didReceivePreview, Swift.Nuke.ImagePipeline pipeline);
        void ImageTaskDidCancel(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImagePipeline pipeline);
        void ImageTask(Swift.Nuke.ImageTask arg0, Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error> didCompleteWithResult, Swift.Nuke.ImagePipeline pipeline);
    }
    
    /// <summary>
    /// Proxy class that enables C# implementations of the ImagePipelineDelegate protocol.
    /// Can wrap either a C# implementation or receive Swift existential containers.
    /// </summary>
    public unsafe class ImagePipelineDelegateProxy : IImagePipelineDelegate, ISwiftObject, Swift.Runtime.ISwiftExistentialConvertible<ExistentialContainer1>
    {
        /// <summary>Matches Swift ImagePipelineDelegate_vtable layout</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct ImagePipelineDelegateSwiftVTable
        {
            public IntPtr csVTHandle;
            public IntPtr func_dataLoader_0;
            public IntPtr func_imageDecoder_1;
            public IntPtr func_imageEncoder_2;
            public IntPtr func_imageCache_3;
            public IntPtr func_dataCache_4;
            public IntPtr func_cacheKey_5;
            public IntPtr func_willCache_6;
            public IntPtr func_shouldDecompress_7;
            public IntPtr func_decompress_8;
            public IntPtr func_imageTaskCreated_9;
            public IntPtr func_imageTask_10;
            public IntPtr func_imageTaskDidStart_11;
            public IntPtr func_imageTask_12;
            public IntPtr func_imageTask_13;
            public IntPtr func_imageTaskDidCancel_14;
            public IntPtr func_imageTask_15;
        }
        
        /// <summary>Local vtable holding managed delegates</summary>
        private struct ImagePipelineDelegateLocalVTable
        {
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> Func_dataLoader_0;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> Func_imageDecoder_1;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> Func_imageEncoder_2;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> Func_imageCache_3;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> Func_dataCache_4;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> Func_cacheKey_5;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> Func_willCache_6;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> Func_shouldDecompress_7;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> Func_decompress_8;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void> Func_imageTaskCreated_9;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> Func_imageTask_10;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void> Func_imageTaskDidStart_11;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> Func_imageTask_12;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> Func_imageTask_13;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void> Func_imageTaskDidCancel_14;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> Func_imageTask_15;
        }
        
        private static IntPtr _protocolWitnessTable;
        private static ImagePipelineDelegateSwiftVTable _swiftVTable;
        private static ImagePipelineDelegateLocalVTable _localVTable;
        private static GCHandle _localVTableHandle;
        private static bool _vtableInitialized;
        private static readonly object _vtableLock = new object();
        private readonly IImagePipelineDelegate? _csharpImpl;
        private readonly EveryProtocol? _everyProtocol;
        private ExistentialContainer1 _swiftContainer;
        static ImagePipelineDelegateProxy()
        {
            InitializeVtable();
        }
        
        private static void InitializeVtable()
        {
            lock (_vtableLock)
            {
                if (_vtableInitialized) return;
                
                _localVTable = new ImagePipelineDelegateLocalVTable
                {
                    Func_dataLoader_0 = &Receive_dataLoader_0,
                    Func_imageDecoder_1 = &Receive_imageDecoder_1,
                    Func_imageEncoder_2 = &Receive_imageEncoder_2,
                    Func_imageCache_3 = &Receive_imageCache_3,
                    Func_dataCache_4 = &Receive_dataCache_4,
                    Func_cacheKey_5 = &Receive_cacheKey_5,
                    Func_willCache_6 = &Receive_willCache_6,
                    Func_shouldDecompress_7 = &Receive_shouldDecompress_7,
                    Func_decompress_8 = &Receive_decompress_8,
                    Func_imageTaskCreated_9 = &Receive_imageTaskCreated_9,
                    Func_imageTask_10 = &Receive_imageTask_10,
                    Func_imageTaskDidStart_11 = &Receive_imageTaskDidStart_11,
                    Func_imageTask_12 = &Receive_imageTask_12,
                    Func_imageTask_13 = &Receive_imageTask_13,
                    Func_imageTaskDidCancel_14 = &Receive_imageTaskDidCancel_14,
                    Func_imageTask_15 = &Receive_imageTask_15,
                };
                
                _localVTableHandle = GCHandle.Alloc(_localVTable, GCHandleType.Pinned);
                
                _swiftVTable = new ImagePipelineDelegateSwiftVTable
                {
                    csVTHandle = GCHandle.ToIntPtr(_localVTableHandle),
                    func_dataLoader_0 = (IntPtr)_localVTable.Func_dataLoader_0,
                    func_imageDecoder_1 = (IntPtr)_localVTable.Func_imageDecoder_1,
                    func_imageEncoder_2 = (IntPtr)_localVTable.Func_imageEncoder_2,
                    func_imageCache_3 = (IntPtr)_localVTable.Func_imageCache_3,
                    func_dataCache_4 = (IntPtr)_localVTable.Func_dataCache_4,
                    func_cacheKey_5 = (IntPtr)_localVTable.Func_cacheKey_5,
                    func_willCache_6 = (IntPtr)_localVTable.Func_willCache_6,
                    func_shouldDecompress_7 = (IntPtr)_localVTable.Func_shouldDecompress_7,
                    func_decompress_8 = (IntPtr)_localVTable.Func_decompress_8,
                    func_imageTaskCreated_9 = (IntPtr)_localVTable.Func_imageTaskCreated_9,
                    func_imageTask_10 = (IntPtr)_localVTable.Func_imageTask_10,
                    func_imageTaskDidStart_11 = (IntPtr)_localVTable.Func_imageTaskDidStart_11,
                    func_imageTask_12 = (IntPtr)_localVTable.Func_imageTask_12,
                    func_imageTask_13 = (IntPtr)_localVTable.Func_imageTask_13,
                    func_imageTaskDidCancel_14 = (IntPtr)_localVTable.Func_imageTaskDidCancel_14,
                    func_imageTask_15 = (IntPtr)_localVTable.Func_imageTask_15,
                };
                
                fixed (ImagePipelineDelegateSwiftVTable* vtPtr = &_swiftVTable)
                {
                    NativeMethods.SetImagePipelineDelegate_vtable((IntPtr)vtPtr);
                }
                _vtableInitialized = true;
            }
        }
        
        #region Swift Callback Receivers
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_dataLoader_0(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageRequest>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg1);
            var result = proxy._csharpImpl!.DataLoader(param0, param1);
            return MarshalToSwiftBuffer(result);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_imageDecoder_1(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageDecodingContext>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg1);
            var result = proxy._csharpImpl!.ImageDecoder(param0, param1);
            return MarshalToSwiftBuffer(result);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_imageEncoder_2(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageEncodingContext>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg1);
            var result = proxy._csharpImpl!.ImageEncoder(param0, param1);
            return MarshalToSwiftBuffer(result);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_imageCache_3(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageRequest>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg1);
            var result = proxy._csharpImpl!.ImageCache(param0, param1);
            return MarshalToSwiftBuffer(result);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_dataCache_4(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageRequest>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg1);
            var result = proxy._csharpImpl!.DataCache(param0, param1);
            return MarshalToSwiftBuffer(result);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_cacheKey_5(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageRequest>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg1);
            var result = proxy._csharpImpl!.CacheKey(param0, param1);
            return MarshalToSwiftBuffer(result);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Receive_willCache_6(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1, IntPtr rawArg2, IntPtr rawArg3, IntPtr rawArg4)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Data>(rawArg0);
            var param1 = MarshalFromSwift<Swift.SwiftOptional<Swift.Nuke.ImageContainer>>(rawArg1);
            var param2 = MarshalFromSwift<Swift.Nuke.ImageRequest>(rawArg2);
            var param3 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg3);
            var param4 = MarshalFromSwift<Action<Swift.SwiftOptional<Swift.Data>>>(rawArg4);
            proxy._csharpImpl!.WillCache(param0, param1, param2, param3, param4);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_shouldDecompress_7(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1, IntPtr rawArg2)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageResponse>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImageRequest>(rawArg1);
            var param2 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg2);
            var result = proxy._csharpImpl!.ShouldDecompress(param0, param1, param2);
            return MarshalToSwiftBuffer(result);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_decompress_8(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1, IntPtr rawArg2)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageResponse>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImageRequest>(rawArg1);
            var param2 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg2);
            var result = proxy._csharpImpl!.Decompress(param0, param1, param2);
            return MarshalToSwiftBuffer(result);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Receive_imageTaskCreated_9(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageTask>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg1);
            proxy._csharpImpl!.ImageTaskCreated(param0, param1);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Receive_imageTask_10(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1, IntPtr rawArg2)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageTask>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImageTask.Event>(rawArg1);
            var param2 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg2);
            proxy._csharpImpl!.ImageTask(param0, param1, param2);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Receive_imageTaskDidStart_11(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageTask>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg1);
            proxy._csharpImpl!.ImageTaskDidStart(param0, param1);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Receive_imageTask_12(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1, IntPtr rawArg2)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageTask>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImageTask.ProgressInfo>(rawArg1);
            var param2 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg2);
            proxy._csharpImpl!.ImageTask(param0, param1, param2);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Receive_imageTask_13(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1, IntPtr rawArg2)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageTask>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImageResponse>(rawArg1);
            var param2 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg2);
            proxy._csharpImpl!.ImageTask(param0, param1, param2);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Receive_imageTaskDidCancel_14(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageTask>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg1);
            proxy._csharpImpl!.ImageTaskDidCancel(param0, param1);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Receive_imageTask_15(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1, IntPtr rawArg2)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageTask>(rawArg0);
            var param1 = MarshalFromSwift<Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error>>(rawArg1);
            var param2 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg2);
            proxy._csharpImpl!.ImageTask(param0, param1, param2);
        }
        
        #endregion
        
        /// <summary>
        /// Creates a proxy wrapping a C# implementation of IImagePipelineDelegate.
        /// </summary>
        /// <param name="implementation">The C# implementation of the protocol.</param>
        public ImagePipelineDelegateProxy(IImagePipelineDelegate implementation)
        {
            _csharpImpl = implementation ?? throw new ArgumentNullException(nameof(implementation));
            _everyProtocol = new EveryProtocol();
            // Create existential container manually
            // The container holds: payload (EveryProtocol pointer), metadata, and witness table
            _swiftContainer = new ExistentialContainer1();
            _swiftContainer.Payload0 = _everyProtocol.Handle;
            _swiftContainer.ObjectMetadata = EveryProtocol.GetTypeMetadata();
            _swiftContainer[0] = ProtocolWitnessTableHandle;
            // Register this proxy so Swift callbacks can find us
            SwiftObjectRegistry.RegisterStrong(_everyProtocol.Handle, this);
        }
        /// <summary>
        /// Creates a proxy from an existing Swift existential container.
        /// Use this when receiving protocol values from Swift code.
        /// </summary>
        /// <remarks>
        /// Swift-backed proxies created with this constructor dispatch blittable and String
        /// protocol members through witness table accessors. Non-dispatchable members
        /// (non-blittable non-String types, throwing, async) throw <see cref="NotSupportedException"/>.
        /// </remarks>
        /// <param name="container">The Swift existential container.</param>
        public ImagePipelineDelegateProxy(ExistentialContainer1 container)
        {
            _swiftContainer = container;
            _csharpImpl = null;
            _everyProtocol = null;
        }
        #region Interface Implementation
        
        public IDataLoading DataLoader(Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
                return _csharpImpl.DataLoader(_for, pipeline);
            throw new NotSupportedException(
                "Cannot call method 'DataLoader' on a Swift-backed existential container. " +
                "Protocol member access is only supported when wrapping a C# implementation.");
        }
        
        public Swift.AnyType? ImageDecoder(Swift.Nuke.ImageDecodingContext _for, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
                return _csharpImpl.ImageDecoder(_for, pipeline);
            throw new NotSupportedException(
                "Cannot call method 'ImageDecoder' on a Swift-backed existential container. " +
                "Protocol member access is only supported when wrapping a C# implementation.");
        }
        
        public IImageEncoding ImageEncoder(Swift.Nuke.ImageEncodingContext _for, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
                return _csharpImpl.ImageEncoder(_for, pipeline);
            throw new NotSupportedException(
                "Cannot call method 'ImageEncoder' on a Swift-backed existential container. " +
                "Protocol member access is only supported when wrapping a C# implementation.");
        }
        
        public Swift.AnyType? ImageCache(Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
                return _csharpImpl.ImageCache(_for, pipeline);
            throw new NotSupportedException(
                "Cannot call method 'ImageCache' on a Swift-backed existential container. " +
                "Protocol member access is only supported when wrapping a C# implementation.");
        }
        
        public Swift.AnyType? DataCache(Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
                return _csharpImpl.DataCache(_for, pipeline);
            throw new NotSupportedException(
                "Cannot call method 'DataCache' on a Swift-backed existential container. " +
                "Protocol member access is only supported when wrapping a C# implementation.");
        }
        
        public Swift.SwiftString? CacheKey(Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
                return _csharpImpl.CacheKey(_for, pipeline);
            throw new NotSupportedException(
                "Cannot call method 'CacheKey' on a Swift-backed existential container. " +
                "Protocol member access is only supported when wrapping a C# implementation.");
        }
        
        public void WillCache(Foundation.NSData data, Swift.Nuke.ImageContainer? image, Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline, Action<Swift.SwiftOptional<Swift.Data>> completion)
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.WillCache(data, image, _for, pipeline, completion);
                return;
            }
            throw new NotSupportedException(
                "Cannot call method 'WillCache' on a Swift-backed existential container. " +
                "Protocol member access is only supported when wrapping a C# implementation.");
        }
        
        public System.Boolean ShouldDecompress(Swift.Nuke.ImageResponse response, Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
                return _csharpImpl.ShouldDecompress(response, _for, pipeline);
            throw new NotSupportedException(
                "Cannot call method 'ShouldDecompress' on a Swift-backed existential container. " +
                "Protocol member access is only supported when wrapping a C# implementation.");
        }
        
        public Swift.Nuke.ImageResponse Decompress(Swift.Nuke.ImageResponse response, Swift.Nuke.ImageRequest request, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
                return _csharpImpl.Decompress(response, request, pipeline);
            throw new NotSupportedException(
                "Cannot call method 'Decompress' on a Swift-backed existential container. " +
                "Protocol member access is only supported when wrapping a C# implementation.");
        }
        
        public void ImageTaskCreated(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.ImageTaskCreated(arg0, pipeline);
                return;
            }
            throw new NotSupportedException(
                "Cannot call method 'ImageTaskCreated' on a Swift-backed existential container. " +
                "Protocol member access is only supported when wrapping a C# implementation.");
        }
        
        public void ImageTask(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImageTask.Event didReceiveEvent, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.ImageTask(arg0, didReceiveEvent, pipeline);
                return;
            }
            throw new NotSupportedException(
                "Cannot call method 'ImageTask' on a Swift-backed existential container. " +
                "Protocol member access is only supported when wrapping a C# implementation.");
        }
        
        public void ImageTaskDidStart(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.ImageTaskDidStart(arg0, pipeline);
                return;
            }
            throw new NotSupportedException(
                "Cannot call method 'ImageTaskDidStart' on a Swift-backed existential container. " +
                "Protocol member access is only supported when wrapping a C# implementation.");
        }
        
        public void ImageTask(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImageTask.ProgressInfo didUpdateProgress, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.ImageTask(arg0, didUpdateProgress, pipeline);
                return;
            }
            throw new NotSupportedException(
                "Cannot call method 'ImageTask' on a Swift-backed existential container. " +
                "Protocol member access is only supported when wrapping a C# implementation.");
        }
        
        public void ImageTask(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImageResponse didReceivePreview, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.ImageTask(arg0, didReceivePreview, pipeline);
                return;
            }
            throw new NotSupportedException(
                "Cannot call method 'ImageTask' on a Swift-backed existential container. " +
                "Protocol member access is only supported when wrapping a C# implementation.");
        }
        
        public void ImageTaskDidCancel(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.ImageTaskDidCancel(arg0, pipeline);
                return;
            }
            throw new NotSupportedException(
                "Cannot call method 'ImageTaskDidCancel' on a Swift-backed existential container. " +
                "Protocol member access is only supported when wrapping a C# implementation.");
        }
        
        public void ImageTask(Swift.Nuke.ImageTask arg0, Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error> didCompleteWithResult, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.ImageTask(arg0, didCompleteWithResult, pipeline);
                return;
            }
            throw new NotSupportedException(
                "Cannot call method 'ImageTask' on a Swift-backed existential container. " +
                "Protocol member access is only supported when wrapping a C# implementation.");
        }
        
        #endregion
        
        #region ISwiftObject Implementation
        /// <summary>
        /// Gets the protocol witness table handle for EveryProtocol conforming to ImagePipelineDelegate.
        /// </summary>
        public static IntPtr ProtocolWitnessTableHandle
        {
            get
            {
                if (_protocolWitnessTable == IntPtr.Zero)
                {
                    // The witness table is generated by the Swift compiler
                    // It will be available after the Swift wrapper is loaded
                    // For now, we look it up dynamically
                    _protocolWitnessTable = GetWitnessTableFromSwift();
                }
                return _protocolWitnessTable;
            }
        }
        private static IntPtr GetWitnessTableFromSwift()
        {
            // Call the Swift-exported function that returns the witness table pointer
            // This function is generated by EveryProtocolEmitter.EmitWitnessTableGetter()
            return NativeMethods.GetWitnessTable();
        }
        /// <summary>
        /// Gets the existential container that can be passed to Swift code.
        /// </summary>
        public ExistentialContainer1 GetExistentialContainer() => _swiftContainer;
        public static TypeMetadata GetTypeMetadata()
        {
            // Proxy classes don't have their own Swift metadata
            // They use the EveryProtocol metadata
            return EveryProtocol.GetTypeMetadata();
        }
        public static ISwiftObject NewFromPayload(IntPtr payload)
        {
            // Create from existential container
            var container = *(ExistentialContainer1*)payload;
            return new ImagePipelineDelegateProxy(container);
        }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            // Marshal the existential container
            var size = _swiftContainer.SizeOf;
            if (swiftDestSpan.Length < size)
                throw new ArgumentException("Destination span too small", nameof(swiftDestSpan));
            fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
            {
                new Span<byte>(containerPtr, size).CopyTo(swiftDestSpan);
            }
            return size;
        }
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
        {
            throw new NotSupportedException(
                "Protocol conformance descriptor is not available for proxy types. " +
                "Proxy classes use EveryProtocol's witness table, not native conformance descriptors.");
        }
        public void Dispose() { }
        #endregion
        #region Marshalling Helpers
        [StructLayout(LayoutKind.Sequential)]
        private struct Utf8Slice
        {
            public IntPtr Ptr;
            public nint Len;
        }
        private static IntPtr MarshalToSwiftBuffer<T>(T value)
        {
            // Use direct memory operations for all types
            // This works for blittable types (value types, structs with blittable fields)
            var size = Unsafe.SizeOf<T>();
            var ptr = (IntPtr)NativeMemory.Alloc((nuint)size);
            Unsafe.Write((void*)ptr, value);
            return ptr;
        }
        private static T MarshalFromSwift<T>(IntPtr ptr)
        {
            // Use direct memory operations for all types
            return Unsafe.Read<T>((void*)ptr);
        }
        #endregion
        private static class NativeMethods
        {
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SetImagePipelineDelegate_vtable")]
            public static extern void SetImagePipelineDelegate_vtable(IntPtr vtable);
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "Get_EveryProtocol_ImagePipelineDelegate_WitnessTable")]
            public static extern IntPtr GetWitnessTable();
        }
    }
    
    
    public interface IImageCaching
    {
        Swift.SwiftOptional<Swift.Nuke.ImageContainer> this[Swift.Nuke.ImageCacheKey index0] { get; set; }
        void RemoveAll();
    }
    
    /// <summary>
    /// Proxy class that enables C# implementations of the ImageCaching protocol.
    /// Can wrap either a C# implementation or receive Swift existential containers.
    /// </summary>
    public unsafe class ImageCachingProxy : IImageCaching, ISwiftObject, Swift.Runtime.ISwiftExistentialConvertible<ExistentialContainer1>
    {
        /// <summary>Matches Swift ImageCaching_vtable layout</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct ImageCachingSwiftVTable
        {
            public IntPtr csVTHandle;
            public IntPtr func_subscript_0_get;
            public IntPtr func_subscript_0_set;
            public IntPtr func_removeAll_0;
        }
        
        /// <summary>Local vtable holding managed delegates</summary>
        private struct ImageCachingLocalVTable
        {
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr> Func_subscript_0_get;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void> Func_subscript_0_set;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> Func_removeAll_0;
        }
        
        private static IntPtr _protocolWitnessTable;
        private static ImageCachingSwiftVTable _swiftVTable;
        private static ImageCachingLocalVTable _localVTable;
        private static GCHandle _localVTableHandle;
        private static bool _vtableInitialized;
        private static readonly object _vtableLock = new object();
        private readonly IImageCaching? _csharpImpl;
        private readonly EveryProtocol? _everyProtocol;
        private ExistentialContainer1 _swiftContainer;
        static ImageCachingProxy()
        {
            InitializeVtable();
        }
        
        private static void InitializeVtable()
        {
            lock (_vtableLock)
            {
                if (_vtableInitialized) return;
                
                _localVTable = new ImageCachingLocalVTable
                {
                    Func_subscript_0_get = &Receive_subscript_0_get,
                    Func_subscript_0_set = &Receive_subscript_0_set,
                    Func_removeAll_0 = &Receive_removeAll_0,
                };
                
                _localVTableHandle = GCHandle.Alloc(_localVTable, GCHandleType.Pinned);
                
                _swiftVTable = new ImageCachingSwiftVTable
                {
                    csVTHandle = GCHandle.ToIntPtr(_localVTableHandle),
                    func_subscript_0_get = (IntPtr)_localVTable.Func_subscript_0_get,
                    func_subscript_0_set = (IntPtr)_localVTable.Func_subscript_0_set,
                    func_removeAll_0 = (IntPtr)_localVTable.Func_removeAll_0,
                };
                
                fixed (ImageCachingSwiftVTable* vtPtr = &_swiftVTable)
                {
                    NativeMethods.SetImageCaching_vtable((IntPtr)vtPtr);
                }
                _vtableInitialized = true;
            }
        }
        
        #region Swift Callback Receivers
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_subscript_0_get(IntPtr vtHandle, IntPtr selfContainer, IntPtr arg0)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImageCachingProxy>(container);
            var index0 = MarshalFromSwift<Swift.Nuke.ImageCacheKey>(arg0);
            var result = proxy._csharpImpl![index0];
            return MarshalToSwiftBuffer(result);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Receive_subscript_0_set(IntPtr vtHandle, IntPtr selfContainer, IntPtr valuePtr, IntPtr arg0)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImageCachingProxy>(container);
            var value = MarshalFromSwift<Swift.SwiftOptional<Swift.Nuke.ImageContainer>>(valuePtr);
            var index0 = MarshalFromSwift<Swift.Nuke.ImageCacheKey>(arg0);
            proxy._csharpImpl![index0] = value;
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Receive_removeAll_0(IntPtr vtHandle, IntPtr selfContainer)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImageCachingProxy>(container);
            proxy._csharpImpl!.RemoveAll();
        }
        
        #endregion
        
        /// <summary>
        /// Creates a proxy wrapping a C# implementation of IImageCaching.
        /// </summary>
        /// <param name="implementation">The C# implementation of the protocol.</param>
        public ImageCachingProxy(IImageCaching implementation)
        {
            _csharpImpl = implementation ?? throw new ArgumentNullException(nameof(implementation));
            _everyProtocol = new EveryProtocol();
            // Create existential container manually
            // The container holds: payload (EveryProtocol pointer), metadata, and witness table
            _swiftContainer = new ExistentialContainer1();
            _swiftContainer.Payload0 = _everyProtocol.Handle;
            _swiftContainer.ObjectMetadata = EveryProtocol.GetTypeMetadata();
            _swiftContainer[0] = ProtocolWitnessTableHandle;
            // Register this proxy so Swift callbacks can find us
            SwiftObjectRegistry.RegisterStrong(_everyProtocol.Handle, this);
        }
        /// <summary>
        /// Creates a proxy from an existing Swift existential container.
        /// Use this when receiving protocol values from Swift code.
        /// </summary>
        /// <remarks>
        /// Swift-backed proxies created with this constructor dispatch blittable and String
        /// protocol members through witness table accessors. Non-dispatchable members
        /// (non-blittable non-String types, throwing, async) throw <see cref="NotSupportedException"/>.
        /// </remarks>
        /// <param name="container">The Swift existential container.</param>
        public ImageCachingProxy(ExistentialContainer1 container)
        {
            _swiftContainer = container;
            _csharpImpl = null;
            _everyProtocol = null;
        }
        #region Interface Implementation
        
        public Swift.SwiftOptional<Swift.Nuke.ImageContainer> this[Swift.Nuke.ImageCacheKey index0]
        {
            get
            {
                if (_csharpImpl != null)
                    return _csharpImpl[index0];
                throw new NotSupportedException(
                    "Cannot get subscript on a Swift-backed existential container. " +
                    "Protocol member access is only supported when wrapping a C# implementation.");
            }
            set
            {
                if (_csharpImpl != null)
                {
                    _csharpImpl[index0] = value;
                    return;
                }
                throw new NotSupportedException(
                    "Cannot set subscript on a Swift-backed existential container. " +
                    "Protocol member access is only supported when wrapping a C# implementation.");
            }
        }
        
        public void RemoveAll()
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.RemoveAll();
                return;
            }
            fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
            {
                NativeMethods.SBW_ImageCaching_method_removeAll_0((IntPtr)containerPtr);
            }
        }
        
        #endregion
        
        #region ISwiftObject Implementation
        /// <summary>
        /// Gets the protocol witness table handle for EveryProtocol conforming to ImageCaching.
        /// </summary>
        public static IntPtr ProtocolWitnessTableHandle
        {
            get
            {
                if (_protocolWitnessTable == IntPtr.Zero)
                {
                    // The witness table is generated by the Swift compiler
                    // It will be available after the Swift wrapper is loaded
                    // For now, we look it up dynamically
                    _protocolWitnessTable = GetWitnessTableFromSwift();
                }
                return _protocolWitnessTable;
            }
        }
        private static IntPtr GetWitnessTableFromSwift()
        {
            // Call the Swift-exported function that returns the witness table pointer
            // This function is generated by EveryProtocolEmitter.EmitWitnessTableGetter()
            return NativeMethods.GetWitnessTable();
        }
        /// <summary>
        /// Gets the existential container that can be passed to Swift code.
        /// </summary>
        public ExistentialContainer1 GetExistentialContainer() => _swiftContainer;
        public static TypeMetadata GetTypeMetadata()
        {
            // Proxy classes don't have their own Swift metadata
            // They use the EveryProtocol metadata
            return EveryProtocol.GetTypeMetadata();
        }
        public static ISwiftObject NewFromPayload(IntPtr payload)
        {
            // Create from existential container
            var container = *(ExistentialContainer1*)payload;
            return new ImageCachingProxy(container);
        }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            // Marshal the existential container
            var size = _swiftContainer.SizeOf;
            if (swiftDestSpan.Length < size)
                throw new ArgumentException("Destination span too small", nameof(swiftDestSpan));
            fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
            {
                new Span<byte>(containerPtr, size).CopyTo(swiftDestSpan);
            }
            return size;
        }
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
        {
            throw new NotSupportedException(
                "Protocol conformance descriptor is not available for proxy types. " +
                "Proxy classes use EveryProtocol's witness table, not native conformance descriptors.");
        }
        public void Dispose() { }
        #endregion
        #region Marshalling Helpers
        [StructLayout(LayoutKind.Sequential)]
        private struct Utf8Slice
        {
            public IntPtr Ptr;
            public nint Len;
        }
        private static IntPtr MarshalToSwiftBuffer<T>(T value)
        {
            // Use direct memory operations for all types
            // This works for blittable types (value types, structs with blittable fields)
            var size = Unsafe.SizeOf<T>();
            var ptr = (IntPtr)NativeMemory.Alloc((nuint)size);
            Unsafe.Write((void*)ptr, value);
            return ptr;
        }
        private static T MarshalFromSwift<T>(IntPtr ptr)
        {
            // Use direct memory operations for all types
            return Unsafe.Read<T>((void*)ptr);
        }
        #endregion
        private static class NativeMethods
        {
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SetImageCaching_vtable")]
            public static extern void SetImageCaching_vtable(IntPtr vtable);
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "Get_EveryProtocol_ImageCaching_WitnessTable")]
            public static extern IntPtr GetWitnessTable();
            
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SBW_ImageCaching_method_removeAll_0")]
            public static extern void SBW_ImageCaching_method_removeAll_0(IntPtr containerPtr);
        }
    }
    
    
    public unsafe class ImageCacheKey : ISwiftObject, IEquatable<ImageCacheKey>
    {
        private unsafe nint HashValue_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_hashValue_Get_3886E187(self);
                
                return result;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageCacheKeyV9hashValueSivg")]
        private static extern nint PInvoke_hashValue_Get_3886E187( SwiftSelf self);
        
        public nint HashValue
        {
            get => HashValue_Get();
        }
        
        static nuint _payloadSize = SwiftObjectHelper<ImageCacheKey>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageCacheKey> _payload = SwiftSafeHandle<ImageCacheKey>.Zero;
        
        internal SwiftSafeHandle<ImageCacheKey> Payload => _payload;
        
        public void Dispose() => _payload.Dispose();
        
        public static System.Boolean operator ==(Swift.Nuke.ImageCacheKey arg0, Swift.Nuke.ImageCacheKey arg1)
        {
            if (arg0 is null) return arg1 is null;
            if (arg1 is null) return false;
            return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
        }
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageCacheKeyV2eeoiySbAC_ACtFZ")]
        private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
        
        public static bool operator !=(ImageCacheKey left, ImageCacheKey right)
        {
            if (left is null) return right is not null;
            if (right is null) return true;
            return !(left == right);
        }
        
        public override bool Equals(object? obj)
        {
            return obj is ImageCacheKey other && Swift.Runtime.SwiftEquatable.Equals(this, other);
        }
        public override int GetHashCode()
        {
            // TODO: Implement when Swift Hashable protocol binding is supported.
            // Returning constant 0 satisfies the Equals/GetHashCode contract
            // (equal objects must have equal hashes). This is correct but makes
            // hash-based collections O(n) until Hashable is supported.
            return 0;
        }
        
        public bool Equals(ImageCacheKey? other)
        {
            return Swift.Runtime.SwiftEquatable.Equals(this, other);
        }
        
        static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageCacheKeyVMa")]
        internal static extern TypeMetadata PInvoke_getMetadata();
        
        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
        {
            return new ImageCacheKey(handle);
        }
        
        ImageCacheKey(SwiftHandle handle)
        {
            _payload = new SwiftSafeHandle<ImageCacheKey>(handle);
        }
        
        unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            var metadata = SwiftObjectHelper<ImageCacheKey>.GetTypeMetadata();
            if ((int)metadata.Size > swiftDestSpan.Length)
            {
                throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
            }
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure that the instance is valid before making copy
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
        
        private static Dictionary<Type, string> _protocolConformanceSymbols;
        static ImageCacheKey()
        {
            _protocolConformanceSymbols = new Dictionary<Type, string>
            {
                {typeof(IEquatable<ImageCacheKey>), "$s4Nuke13ImageCacheKeyVSQAAMc"}
            };
        }
        
        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
            where TProtocol : class
        {
            if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
            {
                throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type ImageCacheKey and protocol {typeof(TProtocol).Name}, but no conformance was found.");
            }
            return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
        }
        
        
        public unsafe ImageCacheKey( string key)
        {
            _payload = new SwiftSafeHandle<ImageCacheKey>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            using var keySwift = new SwiftString(key);
            using PayloadBuffer<SwiftString.Buffer> keyDisposable = keySwift.PayloadBuffer;
            PInvoke_init_1299D4E3(swiftIndirectResult, keyDisposable.Buffer);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageCacheKeyV3keyACSS_tcfC")]
        private static extern void PInvoke_init_1299D4E3( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer key);
        
        
        public unsafe ImageCacheKey( Swift.Nuke.ImageRequest request)
        {
            _payload = new SwiftSafeHandle<ImageCacheKey>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            PInvoke_init_7A81961D(swiftIndirectResult, request.Payload);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageCacheKeyV7requestAcA0B7RequestV_tcfC")]
        private static extern void PInvoke_init_7A81961D( SwiftIndirectResult swiftIndirectResult,  SafeHandle request);
        
        
        public unsafe void Hash(ref Swift.Hasher into)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_hash_6848CE3F(into.Payload, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageCacheKeyV4hash4intoys6HasherVz_tF")]
        private static extern void PInvoke_hash_6848CE3F( SafeHandle into,  SwiftSelf self);
        
        
    }
    
    
    public unsafe class DataCache : ISwiftObject, IDataCaching
    {
        private unsafe nint SizeLimit_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_sizeLimit_Get_5F5BED5C(self);
                
                return result;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC9sizeLimitSivg")]
        private static extern nint PInvoke_sizeLimit_Get_5F5BED5C( SwiftSelf self);
        
        private unsafe void SizeLimit_Set( nint value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_sizeLimit_Set_175FEC9B(value, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC9sizeLimitSivs")]
        private static extern void PInvoke_sizeLimit_Set_175FEC9B( nint value,  SwiftSelf self);
        
        public nint SizeLimit
        {
            get => SizeLimit_Get();
            set => SizeLimit_Set(value);
        }
        
        private unsafe Swift.URL Path_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.URL>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_path_Get_1FED4147(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.URL>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC4path10Foundation3URLVvg")]
        private static extern void PInvoke_path_Get_1FED4147( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Swift.URL Path
        {
            get => Path_Get();
        }
        
        private unsafe System.Double SweepInterval_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_sweepInterval_Get_6112B799(self);
                
                return result;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC13sweepIntervalSdvg")]
        private static extern System.Double PInvoke_sweepInterval_Get_6112B799( SwiftSelf self);
        
        private unsafe void SweepInterval_Set( System.Double value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_sweepInterval_Set_634160A9(value, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC13sweepIntervalSdvs")]
        private static extern void PInvoke_sweepInterval_Set_634160A9( System.Double value,  SwiftSelf self);
        
        public System.Double SweepInterval
        {
            get => SweepInterval_Get();
            set => SweepInterval_Set(value);
        }
        
        private unsafe System.Boolean IsCompressionEnabled_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_isCompressionEnabled_Get_1AA662DB(self);
                
                return result;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC20isCompressionEnabledSbvg")]
        private static extern System.Boolean PInvoke_isCompressionEnabled_Get_1AA662DB( SwiftSelf self);
        
        private unsafe void IsCompressionEnabled_Set( System.Boolean value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_isCompressionEnabled_Set_3A109A7A(value, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC20isCompressionEnabledSbvs")]
        private static extern void PInvoke_isCompressionEnabled_Set_3A109A7A( System.Boolean value,  SwiftSelf self);
        
        public System.Boolean IsCompressionEnabled
        {
            get => IsCompressionEnabled_Get();
            set => IsCompressionEnabled_Set(value);
        }
        
        private unsafe Swift.DispatchQueue Queue_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.DispatchQueue>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_queue_Get_2F71560B(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.DispatchQueue>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC5queueSo012OS_dispatch_D0Cvg")]
        private static extern void PInvoke_queue_Get_2F71560B( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Swift.DispatchQueue Queue
        {
            get => Queue_Get();
        }
        
        private unsafe nint TotalCount_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_totalCount_Get_21FB3E52(self);
                
                return result;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC10totalCountSivg")]
        private static extern nint PInvoke_totalCount_Get_21FB3E52( SwiftSelf self);
        
        public nint TotalCount
        {
            get => TotalCount_Get();
        }
        
        private unsafe nint TotalSize_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_totalSize_Get_5815789D(self);
                
                return result;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC9totalSizeSivg")]
        private static extern nint PInvoke_totalSize_Get_5815789D( SwiftSelf self);
        
        public nint TotalSize
        {
            get => TotalSize_Get();
        }
        
        private unsafe nint TotalAllocatedSize_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_totalAllocatedSize_Get_138D357F(self);
                
                return result;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC18totalAllocatedSizeSivg")]
        private static extern nint PInvoke_totalAllocatedSize_Get_138D357F( SwiftSelf self);
        
        public nint TotalAllocatedSize
        {
            get => TotalAllocatedSize_Get();
        }
        
        static nuint _payloadSize = SwiftObjectHelper<DataCache>.GetTypeMetadata().Size;
        SwiftSafeHandle<DataCache> _payload = SwiftSafeHandle<DataCache>.Zero;
        
        internal SwiftSafeHandle<DataCache> Payload => _payload;
        
        public void Dispose() => _payload.Dispose();
        
        static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheCMa")]
        internal static extern TypeMetadata PInvoke_getMetadata();
        
        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
        {
            return new DataCache(handle);
        }
        
        DataCache(SwiftHandle handle)
        {
            _payload = new SwiftSafeHandle<DataCache>(handle);
        }
        
        unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            var metadata = SwiftObjectHelper<DataCache>.GetTypeMetadata();
            if ((int)metadata.Size > swiftDestSpan.Length)
            {
                throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
            }
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure that the instance is valid before making copy
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
        
        private static Dictionary<Type, string> _protocolConformanceSymbols;
        static DataCache()
        {
            _protocolConformanceSymbols = new Dictionary<Type, string>
            {
                {typeof(IDataCaching), "$s4Nuke9DataCacheCAA0B7CachingAAMc"}
            };
        }
        
        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
            where TProtocol : class
        {
            if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
            {
                throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type DataCache and protocol {typeof(TProtocol).Name}, but no conformance was found.");
            }
            return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
        }
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, void*, SwiftSelf, void> s_init_filenameGenerator_66A8801C_Callback = &init_filenameGenerator_66A8801C_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void init_filenameGenerator_66A8801C_Callback(void* indirectResult, void* arg0, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Func<Swift.SwiftString, Swift.SwiftOptional<Swift.SwiftString>>>(new IntPtr(context.Value));
            var result = del(SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(arg0)));
            // Marshal the result to the indirect result buffer
            var metadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.SwiftOptional<Swift.SwiftString>>();
            var resultSpan = new Span<byte>(indirectResult, (int)metadata.Size);
            SwiftMarshal.MarshalToSwift(result, ref resultSpan);
        }
        
        public unsafe DataCache( string name,  Func<Swift.SwiftString, Swift.SwiftOptional<Swift.SwiftString>> filenameGenerator)
        {
            GCHandle filenameGeneratorHandle = default;
            try
            {
                _payload = new SwiftSafeHandle<DataCache>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                filenameGeneratorHandle = GCHandle.Alloc(filenameGenerator);
                var filenameGeneratorClosure = new SwiftClosureData((IntPtr)s_init_filenameGenerator_66A8801C_Callback, GCHandle.ToIntPtr(filenameGeneratorHandle));
                using var nameSwift = new SwiftString(name);
                using PayloadBuffer<SwiftString.Buffer> nameDisposable = nameSwift.PayloadBuffer;
                PInvoke_init_66A8801C(swiftIndirectResult, nameDisposable.Buffer, filenameGeneratorClosure, out var error);
                
                if (error.Value != null)
                {
                    throw new SwiftRuntimeException("Call to Swift method init failed.");
                }
                
            }
            
            finally
            {
                if (filenameGeneratorHandle.IsAllocated) filenameGeneratorHandle.Free();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC4name17filenameGeneratorACSS_SSSgSSctKcfC")]
        private static extern void PInvoke_init_66A8801C( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer name,  SwiftClosureData filenameGenerator, out SwiftError error);
        public unsafe DataCache( string name)
        {
            _payload = new SwiftSafeHandle<DataCache>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            using var nameSwift = new SwiftString(name);
            using PayloadBuffer<SwiftString.Buffer> nameDisposable = nameSwift.PayloadBuffer;
            PInvoke_init_270C604E(swiftIndirectResult, nameDisposable.Buffer, out var error);
            
            if (error.Value != null)
            {
                throw new SwiftRuntimeException("Call to Swift method init failed.");
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("SwiftBindings", EntryPoint = "DBW_DataCache_init_AB279AAF_1")]
        private static extern void PInvoke_init_270C604E( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer name, out SwiftError error);
        
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, void*, SwiftSelf, void> s_init_filenameGenerator_7FBBC767_Callback = &init_filenameGenerator_7FBBC767_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void init_filenameGenerator_7FBBC767_Callback(void* indirectResult, void* arg0, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Func<Swift.SwiftString, Swift.SwiftOptional<Swift.SwiftString>>>(new IntPtr(context.Value));
            var result = del(SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(arg0)));
            // Marshal the result to the indirect result buffer
            var metadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.SwiftOptional<Swift.SwiftString>>();
            var resultSpan = new Span<byte>(indirectResult, (int)metadata.Size);
            SwiftMarshal.MarshalToSwift(result, ref resultSpan);
        }
        
        public unsafe DataCache( Foundation.NSUrl path,  Func<Swift.SwiftString, Swift.SwiftOptional<Swift.SwiftString>> filenameGenerator)
        {
            GCHandle filenameGeneratorHandle = default;
            try
            {
                _payload = new SwiftSafeHandle<DataCache>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                filenameGeneratorHandle = GCHandle.Alloc(filenameGenerator);
                var filenameGeneratorClosure = new SwiftClosureData((IntPtr)s_init_filenameGenerator_7FBBC767_Callback, GCHandle.ToIntPtr(filenameGeneratorHandle));
                using var pathSwift = Swift.URL.FromNSUrl(path);
                PInvoke_init_7FBBC767(swiftIndirectResult, pathSwift.Payload, filenameGeneratorClosure, out var error);
                
                if (error.Value != null)
                {
                    throw new SwiftRuntimeException("Call to Swift method init failed.");
                }
                
            }
            
            finally
            {
                if (filenameGeneratorHandle.IsAllocated) filenameGeneratorHandle.Free();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC4path17filenameGeneratorAC10Foundation3URLV_SSSgSSctKcfC")]
        private static extern void PInvoke_init_7FBBC767( SwiftIndirectResult swiftIndirectResult,  SafeHandle path,  SwiftClosureData filenameGenerator, out SwiftError error);
        public unsafe DataCache( Foundation.NSUrl path)
        {
            _payload = new SwiftSafeHandle<DataCache>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            using var pathSwift = Swift.URL.FromNSUrl(path);
            PInvoke_init_779E5F73(swiftIndirectResult, pathSwift.Payload, out var error);
            
            if (error.Value != null)
            {
                throw new SwiftRuntimeException("Call to Swift method init failed.");
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("SwiftBindings", EntryPoint = "DBW_DataCache_init_CF2F43D1_1")]
        private static extern void PInvoke_init_779E5F73( SwiftIndirectResult swiftIndirectResult,  SafeHandle path, out SwiftError error);
        
        
        public static unsafe Swift.SwiftString? Filename( string _for)
        {
            try
            {
                
                using var _forSwift = new SwiftString(_for);
                using PayloadBuffer<SwiftString.Buffer> _forDisposable = _forSwift.PayloadBuffer;
                
                var result = PInvoke_filename_79720EE4(_forDisposable.Buffer);
                
                var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.SwiftString>>(new IntPtr(&result));
                return swiftResult.ToNullable();
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC8filename3forSSSgSS_tFZ")]
        private static extern IntPtr PInvoke_filename_79720EE4( Swift.SwiftString.Buffer _for);
        
        
        public unsafe Swift.Data? CachedData( string _for)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                using var _forSwift = new SwiftString(_for);
                using PayloadBuffer<SwiftString.Buffer> _forDisposable = _forSwift.PayloadBuffer;
                
                var result = PInvoke_cachedData_2897E8AD(_forDisposable.Buffer, self);
                
                var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Data>>(new IntPtr(&result));
                return swiftResult.ToNullable();
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC06cachedB03for10Foundation0B0VSgSS_tF")]
        private static extern IntPtr PInvoke_cachedData_2897E8AD( Swift.SwiftString.Buffer _for,  SwiftSelf self);
        
        
        public unsafe System.Boolean ContainsData( string _for)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                using var _forSwift = new SwiftString(_for);
                using PayloadBuffer<SwiftString.Buffer> _forDisposable = _forSwift.PayloadBuffer;
                
                var result = PInvoke_containsData_5467AC91(_forDisposable.Buffer, self);
                
                return result;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC08containsB03forSbSS_tF")]
        private static extern System.Boolean PInvoke_containsData_5467AC91( Swift.SwiftString.Buffer _for,  SwiftSelf self);
        
        
        public unsafe void StoreData( Foundation.NSData arg0,  string _for)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                var arg0Swift = Swift.Data.FromNSData(arg0);
                using var _forSwift = new SwiftString(_for);
                using PayloadBuffer<SwiftString.Buffer> _forDisposable = _forSwift.PayloadBuffer;
                
                PInvoke_storeData_5074DD3A(arg0Swift, _forDisposable.Buffer, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC05storeB0_3fory10Foundation0B0V_SStF")]
        private static extern void PInvoke_storeData_5074DD3A( Swift.Data arg0,  Swift.SwiftString.Buffer _for,  SwiftSelf self);
        
        
        public unsafe void RemoveData( string _for)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                using var _forSwift = new SwiftString(_for);
                using PayloadBuffer<SwiftString.Buffer> _forDisposable = _forSwift.PayloadBuffer;
                
                PInvoke_removeData_057538E9(_forDisposable.Buffer, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC06removeB03forySS_tF")]
        private static extern void PInvoke_removeData_057538E9( Swift.SwiftString.Buffer _for,  SwiftSelf self);
        
        
        public unsafe void RemoveAll()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_removeAll_46E3AE6F(self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC9removeAllyyF")]
        private static extern void PInvoke_removeAll_46E3AE6F( SwiftSelf self);
        
        
        public unsafe Swift.URL? Url( string _for)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                using var _forSwift = new SwiftString(_for);
                using PayloadBuffer<SwiftString.Buffer> _forDisposable = _forSwift.PayloadBuffer;
                
                var result = PInvoke_url_1C69C5DD(_forDisposable.Buffer, self);
                
                var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.URL>>(new IntPtr(&result));
                return swiftResult.ToNullable();
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC3url3for10Foundation3URLVSgSS_tF")]
        private static extern IntPtr PInvoke_url_1C69C5DD( Swift.SwiftString.Buffer _for,  SwiftSelf self);
        
        
        public unsafe void Flush()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_flush_5AA14AC8(self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC5flushyyF")]
        private static extern void PInvoke_flush_5AA14AC8( SwiftSelf self);
        
        
        public unsafe void Flush( string _for)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                using var _forSwift = new SwiftString(_for);
                using PayloadBuffer<SwiftString.Buffer> _forDisposable = _forSwift.PayloadBuffer;
                
                PInvoke_flush_3FB1711B(_forDisposable.Buffer, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC5flush3forySS_tF")]
        private static extern void PInvoke_flush_3FB1711B( Swift.SwiftString.Buffer _for,  SwiftSelf self);
        
        
        public unsafe void Sweep()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_sweep_747616F3(self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC5sweepyyF")]
        private static extern void PInvoke_sweep_747616F3( SwiftSelf self);
        
        
    }
    
    
    public unsafe class ImageDecoderRegistry : ISwiftObject
    {
        private static Swift.Nuke.ImageDecoderRegistry Shared_Get()
        {
            try
            {
                
                
                var result = PInvoke_shared_Get_01C4DCB5();
                
                var classPayload = NativeMemory.Alloc((nuint)sizeof(IntPtr));
                *(IntPtr*)classPayload = result;
                return (Swift.Nuke.ImageDecoderRegistry)SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageDecoderRegistry>(new IntPtr(classPayload));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecoderRegistryC6sharedACvgZ")]
        private static extern IntPtr PInvoke_shared_Get_01C4DCB5();
        
        public static Swift.Nuke.ImageDecoderRegistry Shared
        {
            get => Shared_Get();
        }
        
        static nuint _payloadSize = SwiftObjectHelper<ImageDecoderRegistry>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageDecoderRegistry> _payload = SwiftSafeHandle<ImageDecoderRegistry>.Zero;
        
        internal SwiftSafeHandle<ImageDecoderRegistry> Payload => _payload;
        
        public void Dispose() => _payload.Dispose();
        
        static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecoderRegistryCMa")]
        internal static extern TypeMetadata PInvoke_getMetadata();
        
        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
        {
            return new ImageDecoderRegistry(handle);
        }
        
        ImageDecoderRegistry(SwiftHandle handle)
        {
            _payload = new SwiftSafeHandle<ImageDecoderRegistry>(handle);
        }
        
        unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            var metadata = SwiftObjectHelper<ImageDecoderRegistry>.GetTypeMetadata();
            if ((int)metadata.Size > swiftDestSpan.Length)
            {
                throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
            }
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure that the instance is valid before making copy
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
        
        private static Dictionary<Type, string> _protocolConformanceSymbols;
        static ImageDecoderRegistry()
        {
            _protocolConformanceSymbols = new Dictionary<Type, string>
            {
                
            };
        }
        
        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
            where TProtocol : class
        {
            if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
            {
                throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type ImageDecoderRegistry and protocol {typeof(TProtocol).Name}, but no conformance was found.");
            }
            return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
        }
        
        public unsafe ImageDecoderRegistry()
        {
            _payload = new SwiftSafeHandle<ImageDecoderRegistry>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            PInvoke_init_44443242(swiftIndirectResult);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecoderRegistryCACycfC")]
        private static extern void PInvoke_init_44443242( SwiftIndirectResult swiftIndirectResult);
        
        
        [global::Swift.UnsupportedSwiftType("Existential type fallback", "any Nuke.ImageDecoding")]
        public unsafe IImageDecoding? Decoder( Swift.Nuke.ImageDecodingContext _for)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_decoder_39343C8D(_for.Payload, self);
                
                var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1>>(new IntPtr(&result));
                if (swiftResult.Case == Swift.SwiftOptionalCases.None) return null;
                return new ImageDecodingProxy(swiftResult.Some);
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecoderRegistryC7decoder3forAA0B8Decoding_pSgAA0bG7ContextV_tF")]
        private static extern IntPtr PInvoke_decoder_39343C8D( SafeHandle _for,  SwiftSelf self);
        
        
        
        public unsafe void Clear()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_clear_60F2588A(self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecoderRegistryC5clearyyF")]
        private static extern void PInvoke_clear_60F2588A( SwiftSelf self);
        
        
    }
    
    
    public unsafe class ImageDecodingContext : ISwiftObject
    {
        private unsafe Swift.Nuke.ImageRequest Request_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_request_Get_0FE7E4BF(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecodingContextV7requestAA0B7RequestVvg")]
        private static extern void PInvoke_request_Get_0FE7E4BF( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Request_Set( Swift.Nuke.ImageRequest value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_request_Set_71F2DD2F(value.Payload, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecodingContextV7requestAA0B7RequestVvs")]
        private static extern void PInvoke_request_Set_71F2DD2F( SafeHandle value,  SwiftSelf self);
        
        public Swift.Nuke.ImageRequest Request
        {
            get => Request_Get();
            set => Request_Set(value);
        }
        
        private unsafe Swift.Data Data_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_data_Get_4709A6F1(self);
                
                return result;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecodingContextV4data10Foundation4DataVvg")]
        private static extern Swift.Data PInvoke_data_Get_4709A6F1( SwiftSelf self);
        
        private unsafe void Data_Set( Swift.Data value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_data_Set_6CCA62E6(value, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecodingContextV4data10Foundation4DataVvs")]
        private static extern void PInvoke_data_Set_6CCA62E6( Swift.Data value,  SwiftSelf self);
        
        public Swift.Data Data
        {
            get => Data_Get();
            set => Data_Set(value);
        }
        
        private unsafe System.Boolean IsCompleted_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_isCompleted_Get_3117C4F2(self);
                
                return result;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecodingContextV11isCompletedSbvg")]
        private static extern System.Boolean PInvoke_isCompleted_Get_3117C4F2( SwiftSelf self);
        
        private unsafe void IsCompleted_Set( System.Boolean value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_isCompleted_Set_3B929A8D(value, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecodingContextV11isCompletedSbvs")]
        private static extern void PInvoke_isCompleted_Set_3B929A8D( System.Boolean value,  SwiftSelf self);
        
        public System.Boolean IsCompleted
        {
            get => IsCompleted_Get();
            set => IsCompleted_Set(value);
        }
        
        private unsafe Swift.SwiftOptional<Foundation.NSUrlResponse> UrlResponse_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_urlResponse_Get_75F95D71(self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Foundation.NSUrlResponse>>(new IntPtr(&result));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecodingContextV11urlResponseSo13NSURLResponseCSgvg")]
        private static extern IntPtr PInvoke_urlResponse_Get_75F95D71( SwiftSelf self);
        
        private unsafe void UrlResponse_Set( Swift.SwiftOptional<Foundation.NSUrlResponse> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_urlResponse_Set_5FC707E4(valueBuffer, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecodingContextV11urlResponseSo13NSURLResponseCSgvs")]
        private static extern void PInvoke_urlResponse_Set_5FC707E4( IntPtr valueBuffer,  SwiftSelf self);
        
        public Foundation.NSUrlResponse? UrlResponse
        {
            get => ((Foundation.NSUrlResponse?)UrlResponse_Get());
            set => UrlResponse_Set((value is {} valueVal ? SwiftOptional<Foundation.NSUrlResponse>.NewSome(valueVal) : SwiftOptional<Foundation.NSUrlResponse>.NewNone()));
        }
        
        private unsafe Swift.SwiftOptional<Swift.Nuke.ImageResponse.CacheTypeInfo> CacheType_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_cacheType_Get_3861FAAD(self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Nuke.ImageResponse.CacheTypeInfo>>(new IntPtr(&result));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecodingContextV9cacheTypeAA0B8ResponseV05CacheF0OSgvg")]
        private static extern IntPtr PInvoke_cacheType_Get_3861FAAD( SwiftSelf self);
        
        private unsafe void CacheType_Set( Swift.SwiftOptional<Swift.Nuke.ImageResponse.CacheTypeInfo> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_cacheType_Set_534D7D3A(valueBuffer, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecodingContextV9cacheTypeAA0B8ResponseV05CacheF0OSgvs")]
        private static extern void PInvoke_cacheType_Set_534D7D3A( IntPtr valueBuffer,  SwiftSelf self);
        
        public Swift.Nuke.ImageResponse.CacheTypeInfo? CacheType
        {
            get => ((Swift.Nuke.ImageResponse.CacheTypeInfo?)CacheType_Get());
            set => CacheType_Set((value is {} valueVal ? SwiftOptional<Swift.Nuke.ImageResponse.CacheTypeInfo>.NewSome(valueVal) : SwiftOptional<Swift.Nuke.ImageResponse.CacheTypeInfo>.NewNone()));
        }
        
        static nuint _payloadSize = SwiftObjectHelper<ImageDecodingContext>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageDecodingContext> _payload = SwiftSafeHandle<ImageDecodingContext>.Zero;
        
        internal SwiftSafeHandle<ImageDecodingContext> Payload => _payload;
        
        public void Dispose() => _payload.Dispose();
        
        static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecodingContextVMa")]
        internal static extern TypeMetadata PInvoke_getMetadata();
        
        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
        {
            return new ImageDecodingContext(handle);
        }
        
        ImageDecodingContext(SwiftHandle handle)
        {
            _payload = new SwiftSafeHandle<ImageDecodingContext>(handle);
        }
        
        unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            var metadata = SwiftObjectHelper<ImageDecodingContext>.GetTypeMetadata();
            if ((int)metadata.Size > swiftDestSpan.Length)
            {
                throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
            }
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure that the instance is valid before making copy
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
        
        private static Dictionary<Type, string> _protocolConformanceSymbols;
        static ImageDecodingContext()
        {
            _protocolConformanceSymbols = new Dictionary<Type, string>
            {
                
            };
        }
        
        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
            where TProtocol : class
        {
            if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
            {
                throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type ImageDecodingContext and protocol {typeof(TProtocol).Name}, but no conformance was found.");
            }
            return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
        }
        
        
        public unsafe ImageDecodingContext( Swift.Nuke.ImageRequest request,  Foundation.NSData data,  System.Boolean isCompleted,  Foundation.NSUrlResponse? urlResponse,  Swift.Nuke.ImageResponse.CacheTypeInfo? cacheType)
        {
            _payload = new SwiftSafeHandle<ImageDecodingContext>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            var dataSwift = Swift.Data.FromNSData(data);
            using var urlResponseSwift = urlResponse is {} urlResponseValue ? SwiftOptional<Foundation.NSUrlResponse>.NewSome(urlResponseValue) : SwiftOptional<Foundation.NSUrlResponse>.NewNone();
            using PayloadBuffer<IntPtr> urlResponseDisposable = urlResponseSwift.PayloadBuffer;
            IntPtr urlResponseBuffer = urlResponseDisposable.Buffer;
            using var cacheTypeSwift = cacheType is {} cacheTypeValue ? SwiftOptional<Swift.Nuke.ImageResponse.CacheTypeInfo>.NewSome(cacheTypeValue) : SwiftOptional<Swift.Nuke.ImageResponse.CacheTypeInfo>.NewNone();
            using PayloadBuffer<IntPtr> cacheTypeDisposable = cacheTypeSwift.PayloadBuffer;
            IntPtr cacheTypeBuffer = cacheTypeDisposable.Buffer;
            PInvoke_init_2C188F45(swiftIndirectResult, request.Payload, dataSwift, isCompleted, urlResponseBuffer, cacheTypeBuffer);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecodingContextV7request4data11isCompleted11urlResponse9cacheTypeAcA0B7RequestV_10Foundation4DataVSbSo13NSURLResponseCSgAA0bJ0V05CacheL0OSgtcfC")]
        private static extern void PInvoke_init_2C188F45( SwiftIndirectResult swiftIndirectResult,  SafeHandle request,  Swift.Data data,  System.Boolean isCompleted,  IntPtr urlResponseBuffer,  IntPtr cacheTypeBuffer);
        public unsafe ImageDecodingContext( Swift.Nuke.ImageRequest request,  Foundation.NSData data)
        {
            _payload = new SwiftSafeHandle<ImageDecodingContext>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            var dataSwift = Swift.Data.FromNSData(data);
            PInvoke_init_72123399(swiftIndirectResult, request.Payload, dataSwift);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("SwiftBindings", EntryPoint = "DBW_ImageDecodingContext_init_6CA396CF_3")]
        private static extern void PInvoke_init_72123399( SwiftIndirectResult swiftIndirectResult,  SafeHandle request,  Swift.Data data);
        public unsafe ImageDecodingContext( Swift.Nuke.ImageRequest request,  Foundation.NSData data,  System.Boolean isCompleted)
        {
            _payload = new SwiftSafeHandle<ImageDecodingContext>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            var dataSwift = Swift.Data.FromNSData(data);
            PInvoke_init_437DDB5A(swiftIndirectResult, request.Payload, dataSwift, isCompleted);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("SwiftBindings", EntryPoint = "DBW_ImageDecodingContext_init_6CA396CF_2")]
        private static extern void PInvoke_init_437DDB5A( SwiftIndirectResult swiftIndirectResult,  SafeHandle request,  Swift.Data data,  System.Boolean isCompleted);
        public unsafe ImageDecodingContext( Swift.Nuke.ImageRequest request,  Foundation.NSData data,  System.Boolean isCompleted,  Foundation.NSUrlResponse? urlResponse)
        {
            _payload = new SwiftSafeHandle<ImageDecodingContext>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            var dataSwift = Swift.Data.FromNSData(data);
            using var urlResponseSwift = urlResponse is {} urlResponseValue ? SwiftOptional<Foundation.NSUrlResponse>.NewSome(urlResponseValue) : SwiftOptional<Foundation.NSUrlResponse>.NewNone();
            using PayloadBuffer<IntPtr> urlResponseDisposable = urlResponseSwift.PayloadBuffer;
            IntPtr urlResponseBuffer = urlResponseDisposable.Buffer;
            PInvoke_init_7A820DD7(swiftIndirectResult, request.Payload, dataSwift, isCompleted, urlResponseBuffer);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("SwiftBindings", EntryPoint = "DBW_ImageDecodingContext_init_6CA396CF_1")]
        private static extern void PInvoke_init_7A820DD7( SwiftIndirectResult swiftIndirectResult,  SafeHandle request,  Swift.Data data,  System.Boolean isCompleted,  IntPtr urlResponseBuffer);
        
        
    }
    
    
    public unsafe class ImageProcessors : ISwiftObject
    {
        static nuint _payloadSize = SwiftObjectHelper<ImageProcessors>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageProcessors> _payload = SwiftSafeHandle<ImageProcessors>.Zero;
        internal SwiftSafeHandle<ImageProcessors> Payload => _payload;
        
        public void Dispose() => _payload.Dispose();
        
        static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsOMa")]
        internal static extern TypeMetadata PInvoke_getMetadata();
        
        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
        {
            return new ImageProcessors(handle);
        }
        
        ImageProcessors()
        {
        }
        
        ImageProcessors(SwiftHandle handle)
        {
            _payload = new SwiftSafeHandle<ImageProcessors>(handle);
        }
        
        unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            var metadata = SwiftObjectHelper<ImageProcessors>.GetTypeMetadata();
            if ((int)metadata.Size > swiftDestSpan.Length)
            {
                throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
            }
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure that the instance is valid before making copy
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
        
        private static Dictionary<Type, string> _protocolConformanceSymbols;
        static ImageProcessors()
        {
            _protocolConformanceSymbols = new Dictionary<Type, string>
            {
                
            };
        }
        
        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
            where TProtocol : class
        {
            if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
            {
                throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type ImageProcessors and protocol {typeof(TProtocol).Name}, but no conformance was found.");
            }
            return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
        }
        
        public unsafe class Anonymous : ISwiftObject
        {
            private unsafe Swift.SwiftString Identifier_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_identifier_Get_59BB98FB(self);
                    
                    unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO9AnonymousV10identifierSSvg")]
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_59BB98FB( SwiftSelf self);
            
            public string Identifier
            {
                get => Identifier_Get().ToString();
            }
            
            private unsafe Swift.SwiftString Description_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_22D9E31D(self);
                    
                    unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO9AnonymousV11descriptionSSvg")]
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_22D9E31D( SwiftSelf self);
            
            public string Description
            {
                get => Description_Get().ToString();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<Anonymous>.GetTypeMetadata().Size;
            SwiftSafeHandle<Anonymous> _payload = SwiftSafeHandle<Anonymous>.Zero;
            
            internal SwiftSafeHandle<Anonymous> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO9AnonymousVMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new Anonymous(handle);
            }
            
            Anonymous(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<Anonymous>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<Anonymous>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static Anonymous()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IImageProcessing), "$s4Nuke15ImageProcessorsO9AnonymousVAA0B10ProcessingAAMc"}
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type Anonymous and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
            
            private static unsafe readonly delegate* unmanaged[Swift]<void*, void*, SwiftSelf, void> s_init_arg1_0E450586_Callback = &init_arg1_0E450586_Callback;
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
            private static void init_arg1_0E450586_Callback(void* indirectResult, void* arg0, SwiftSelf context)
            {
                var del = SwiftClosureMarshaller.GetDelegateFromContext<Func<UIKit.UIImage, Swift.SwiftOptional<UIKit.UIImage>>>(new IntPtr(context.Value));
                var result = del(SwiftMarshal.MarshalFromSwift<UIKit.UIImage>(new IntPtr(arg0)));
                // Marshal the result to the indirect result buffer
                var metadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.SwiftOptional<UIKit.UIImage>>();
                var resultSpan = new Span<byte>(indirectResult, (int)metadata.Size);
                SwiftMarshal.MarshalToSwift(result, ref resultSpan);
            }
            
            public unsafe Anonymous( string id,  Func<UIKit.UIImage, Swift.SwiftOptional<UIKit.UIImage>> arg1)
            {
                GCHandle arg1Handle = default;
                try
                {
                    _payload = new SwiftSafeHandle<Anonymous>((IntPtr)NativeMemory.Alloc(_payloadSize));
                    var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                    
                    arg1Handle = GCHandle.Alloc(arg1);
                    var arg1Closure = new SwiftClosureData((IntPtr)s_init_arg1_0E450586_Callback, GCHandle.ToIntPtr(arg1Handle));
                    using var idSwift = new SwiftString(id);
                    using PayloadBuffer<SwiftString.Buffer> idDisposable = idSwift.PayloadBuffer;
                    PInvoke_init_0E450586(swiftIndirectResult, idDisposable.Buffer, arg1Closure);
                    
                }
                
                finally
                {
                    if (arg1Handle.IsAllocated) arg1Handle.Free();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO9AnonymousV2id_AESS_So7UIImageCSgAHYbctcfC")]
            private static extern void PInvoke_init_0E450586( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer id,  SwiftClosureData arg1);
            
            
            public unsafe UIKit.UIImage? Process( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_200B4E5C(arg0Handle, self);
                    
                    var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<UIKit.UIImage>>(new IntPtr(&result));
                    return swiftResult.ToNullable();
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO9AnonymousV7processySo7UIImageCSgAHF")]
            private static extern IntPtr PInvoke_process_200B4E5C( IntPtr arg0,  SwiftSelf self);
            
            
        }
        
        
        public unsafe class RoundedCorners : ISwiftObject, IEquatable<RoundedCorners>
        {
            private unsafe Swift.SwiftString Identifier_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_identifier_Get_42FA2445(self);
                    
                    unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO14RoundedCornersV10identifierSSvg")]
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_42FA2445( SwiftSelf self);
            
            public string Identifier
            {
                get => Identifier_Get().ToString();
            }
            
            private unsafe Swift.SwiftString Description_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_74BD1ED5(self);
                    
                    unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO14RoundedCornersV11descriptionSSvg")]
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_74BD1ED5( SwiftSelf self);
            
            public string Description
            {
                get => Description_Get().ToString();
            }
            
            private unsafe nint HashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_3A97B955(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO14RoundedCornersV9hashValueSivg")]
            private static extern nint PInvoke_hashValue_Get_3A97B955( SwiftSelf self);
            
            public nint HashValue
            {
                get => HashValue_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<RoundedCorners>.GetTypeMetadata().Size;
            SwiftSafeHandle<RoundedCorners> _payload = SwiftSafeHandle<RoundedCorners>.Zero;
            
            internal SwiftSafeHandle<RoundedCorners> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            public static System.Boolean operator ==(Swift.Nuke.ImageProcessors.RoundedCorners arg0, Swift.Nuke.ImageProcessors.RoundedCorners arg1)
            {
                if (arg0 is null) return arg1 is null;
                if (arg1 is null) return false;
                return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
            }
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO14RoundedCornersV2eeoiySbAE_AEtFZ")]
            private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
            
            public static bool operator !=(RoundedCorners left, RoundedCorners right)
            {
                if (left is null) return right is not null;
                if (right is null) return true;
                return !(left == right);
            }
            
            public override bool Equals(object? obj)
            {
                return obj is RoundedCorners other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            public override int GetHashCode()
            {
                // TODO: Implement when Swift Hashable protocol binding is supported.
                // Returning constant 0 satisfies the Equals/GetHashCode contract
                // (equal objects must have equal hashes). This is correct but makes
                // hash-based collections O(n) until Hashable is supported.
                return 0;
            }
            
            public bool Equals(RoundedCorners? other)
            {
                return Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO14RoundedCornersVMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new RoundedCorners(handle);
            }
            
            RoundedCorners(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<RoundedCorners>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<RoundedCorners>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static RoundedCorners()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IImageProcessing), "$s4Nuke15ImageProcessorsO14RoundedCornersVAA0B10ProcessingAAMc"},
            {typeof(IEquatable<RoundedCorners>), "$s4Nuke15ImageProcessorsO14RoundedCornersVSQAAMc"}
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type RoundedCorners and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
            
            public unsafe RoundedCorners( System.Double radius,  Swift.Nuke.ImageProcessingOptions.Unit unit,  Swift.Nuke.ImageProcessingOptions.Border? border)
            {
                _payload = new SwiftSafeHandle<RoundedCorners>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                using var borderSwift = border is {} borderValue ? SwiftOptional<Swift.Nuke.ImageProcessingOptions.Border>.NewSome(borderValue) : SwiftOptional<Swift.Nuke.ImageProcessingOptions.Border>.NewNone();
                using PayloadBuffer<IntPtr> borderDisposable = borderSwift.PayloadBuffer;
                IntPtr borderBuffer = borderDisposable.Buffer;
                PInvoke_init_2B17EECD(swiftIndirectResult, radius, unit.Payload.DangerousGetHandle(), borderBuffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO14RoundedCornersV6radius4unit6borderAE12CoreGraphics7CGFloatV_AA0B17ProcessingOptionsO4UnitOAM6BorderVSgtcfC")]
            private static extern void PInvoke_init_2B17EECD( SwiftIndirectResult swiftIndirectResult,  System.Double radius,  IntPtr unit,  IntPtr borderBuffer);
            public unsafe RoundedCorners( System.Double radius)
            {
                _payload = new SwiftSafeHandle<RoundedCorners>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_3EF382E7(swiftIndirectResult, radius);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("SwiftBindings", EntryPoint = "DBW_RoundedCorners_init_DD16F922_2")]
            private static extern void PInvoke_init_3EF382E7( SwiftIndirectResult swiftIndirectResult,  System.Double radius);
            public unsafe RoundedCorners( System.Double radius,  Swift.Nuke.ImageProcessingOptions.Unit unit)
            {
                _payload = new SwiftSafeHandle<RoundedCorners>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_71B5A2D4(swiftIndirectResult, radius, unit.Payload.DangerousGetHandle());
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("SwiftBindings", EntryPoint = "DBW_RoundedCorners_init_DD16F922_1")]
            private static extern void PInvoke_init_71B5A2D4( SwiftIndirectResult swiftIndirectResult,  System.Double radius,  IntPtr unit);
            
            
            public unsafe UIKit.UIImage? Process( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_3F7F4D4D(arg0Handle, self);
                    
                    var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<UIKit.UIImage>>(new IntPtr(&result));
                    return swiftResult.ToNullable();
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO14RoundedCornersV7processySo7UIImageCSgAHF")]
            private static extern IntPtr PInvoke_process_3F7F4D4D( IntPtr arg0,  SwiftSelf self);
            
            
            public unsafe void Hash(ref Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_73D884A0(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO14RoundedCornersV4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_73D884A0( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        public unsafe class Resize : ISwiftObject, IEquatable<Resize>
        {
            private unsafe Swift.SwiftString Identifier_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_identifier_Get_3E4D5804(self);
                    
                    unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6ResizeV10identifierSSvg")]
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_3E4D5804( SwiftSelf self);
            
            public string Identifier
            {
                get => Identifier_Get().ToString();
            }
            
            private unsafe Swift.SwiftString Description_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_533FD9CA(self);
                    
                    unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6ResizeV11descriptionSSvg")]
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_533FD9CA( SwiftSelf self);
            
            public string Description
            {
                get => Description_Get().ToString();
            }
            
            private unsafe nint HashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_31B8193C(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6ResizeV9hashValueSivg")]
            private static extern nint PInvoke_hashValue_Get_31B8193C( SwiftSelf self);
            
            public nint HashValue
            {
                get => HashValue_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<Resize>.GetTypeMetadata().Size;
            SwiftSafeHandle<Resize> _payload = SwiftSafeHandle<Resize>.Zero;
            
            internal SwiftSafeHandle<Resize> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            public static System.Boolean operator ==(Swift.Nuke.ImageProcessors.Resize arg0, Swift.Nuke.ImageProcessors.Resize arg1)
            {
                if (arg0 is null) return arg1 is null;
                if (arg1 is null) return false;
                return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
            }
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6ResizeV2eeoiySbAE_AEtFZ")]
            private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
            
            public static bool operator !=(Resize left, Resize right)
            {
                if (left is null) return right is not null;
                if (right is null) return true;
                return !(left == right);
            }
            
            public override bool Equals(object? obj)
            {
                return obj is Resize other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            public override int GetHashCode()
            {
                // TODO: Implement when Swift Hashable protocol binding is supported.
                // Returning constant 0 satisfies the Equals/GetHashCode contract
                // (equal objects must have equal hashes). This is correct but makes
                // hash-based collections O(n) until Hashable is supported.
                return 0;
            }
            
            public bool Equals(Resize? other)
            {
                return Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6ResizeVMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new Resize(handle);
            }
            
            Resize(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<Resize>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<Resize>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static Resize()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IImageProcessing), "$s4Nuke15ImageProcessorsO6ResizeVAA0B10ProcessingAAMc"},
            {typeof(IEquatable<Resize>), "$s4Nuke15ImageProcessorsO6ResizeVSQAAMc"}
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type Resize and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
            
            public unsafe Resize( Swift.CGSize size,  Swift.Nuke.ImageProcessingOptions.Unit unit,  Swift.Nuke.ImageProcessingOptions.ContentMode contentMode,  System.Boolean crop,  System.Boolean upscale)
            {
                _payload = new SwiftSafeHandle<Resize>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_252DD655(swiftIndirectResult, size, unit.Payload.DangerousGetHandle(), contentMode.Payload.DangerousGetHandle(), crop, upscale);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6ResizeV4size4unit11contentMode4crop7upscaleAESo6CGSizeV_AA0B17ProcessingOptionsO4UnitOAN07ContentH0OS2btcfC")]
            private static extern void PInvoke_init_252DD655( SwiftIndirectResult swiftIndirectResult,  Swift.CGSize size,  IntPtr unit,  IntPtr contentMode,  System.Boolean crop,  System.Boolean upscale);
            public unsafe Resize( Swift.CGSize size)
            {
                _payload = new SwiftSafeHandle<Resize>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_632FD6C6(swiftIndirectResult, size);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("SwiftBindings", EntryPoint = "DBW_Resize_init_51CAC367_4")]
            private static extern void PInvoke_init_632FD6C6( SwiftIndirectResult swiftIndirectResult,  Swift.CGSize size);
            public unsafe Resize( Swift.CGSize size,  Swift.Nuke.ImageProcessingOptions.Unit unit)
            {
                _payload = new SwiftSafeHandle<Resize>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_4DD6F4E6(swiftIndirectResult, size, unit.Payload.DangerousGetHandle());
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("SwiftBindings", EntryPoint = "DBW_Resize_init_51CAC367_3")]
            private static extern void PInvoke_init_4DD6F4E6( SwiftIndirectResult swiftIndirectResult,  Swift.CGSize size,  IntPtr unit);
            public unsafe Resize( Swift.CGSize size,  Swift.Nuke.ImageProcessingOptions.Unit unit,  Swift.Nuke.ImageProcessingOptions.ContentMode contentMode,  System.Boolean crop)
            {
                _payload = new SwiftSafeHandle<Resize>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_51FCA0F5(swiftIndirectResult, size, unit.Payload.DangerousGetHandle(), contentMode.Payload.DangerousGetHandle(), crop);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("SwiftBindings", EntryPoint = "DBW_Resize_init_51CAC367_1")]
            private static extern void PInvoke_init_51FCA0F5( SwiftIndirectResult swiftIndirectResult,  Swift.CGSize size,  IntPtr unit,  IntPtr contentMode,  System.Boolean crop);
            
            
            public unsafe Resize( System.Double width,  Swift.Nuke.ImageProcessingOptions.Unit unit,  System.Boolean upscale)
            {
                _payload = new SwiftSafeHandle<Resize>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_67AFC0BB(swiftIndirectResult, width, unit.Payload.DangerousGetHandle(), upscale);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6ResizeV5width4unit7upscaleAE12CoreGraphics7CGFloatV_AA0B17ProcessingOptionsO4UnitOSbtcfC")]
            private static extern void PInvoke_init_67AFC0BB( SwiftIndirectResult swiftIndirectResult,  System.Double width,  IntPtr unit,  System.Boolean upscale);
            public unsafe Resize( System.Double width)
            {
                _payload = new SwiftSafeHandle<Resize>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_517E59D1(swiftIndirectResult, width);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("SwiftBindings", EntryPoint = "DBW_Resize_init_62C284BA_2")]
            private static extern void PInvoke_init_517E59D1( SwiftIndirectResult swiftIndirectResult,  System.Double width);
            public unsafe Resize( System.Double width,  Swift.Nuke.ImageProcessingOptions.Unit unit)
            {
                _payload = new SwiftSafeHandle<Resize>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_52879F14(swiftIndirectResult, width, unit.Payload.DangerousGetHandle());
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("SwiftBindings", EntryPoint = "DBW_Resize_init_62C284BA_1")]
            private static extern void PInvoke_init_52879F14( SwiftIndirectResult swiftIndirectResult,  System.Double width,  IntPtr unit);
            
            
            public unsafe UIKit.UIImage? Process( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_552455D9(arg0Handle, self);
                    
                    var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<UIKit.UIImage>>(new IntPtr(&result));
                    return swiftResult.ToNullable();
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6ResizeV7processySo7UIImageCSgAHF")]
            private static extern IntPtr PInvoke_process_552455D9( IntPtr arg0,  SwiftSelf self);
            
            
            public unsafe void Hash(ref Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_368F85DB(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6ResizeV4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_368F85DB( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        public unsafe class GaussianBlur : ISwiftObject, IEquatable<GaussianBlur>
        {
            private unsafe Swift.SwiftString Identifier_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_identifier_Get_0561EA1B(self);
                    
                    unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO12GaussianBlurV10identifierSSvg")]
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_0561EA1B( SwiftSelf self);
            
            public string Identifier
            {
                get => Identifier_Get().ToString();
            }
            
            private unsafe Swift.SwiftString Description_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_181865D5(self);
                    
                    unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO12GaussianBlurV11descriptionSSvg")]
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_181865D5( SwiftSelf self);
            
            public string Description
            {
                get => Description_Get().ToString();
            }
            
            private unsafe nint HashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_1159FE91(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO12GaussianBlurV9hashValueSivg")]
            private static extern nint PInvoke_hashValue_Get_1159FE91( SwiftSelf self);
            
            public nint HashValue
            {
                get => HashValue_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<GaussianBlur>.GetTypeMetadata().Size;
            SwiftSafeHandle<GaussianBlur> _payload = SwiftSafeHandle<GaussianBlur>.Zero;
            
            internal SwiftSafeHandle<GaussianBlur> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            public static System.Boolean operator ==(Swift.Nuke.ImageProcessors.GaussianBlur arg0, Swift.Nuke.ImageProcessors.GaussianBlur arg1)
            {
                if (arg0 is null) return arg1 is null;
                if (arg1 is null) return false;
                return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
            }
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO12GaussianBlurV2eeoiySbAE_AEtFZ")]
            private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
            
            public static bool operator !=(GaussianBlur left, GaussianBlur right)
            {
                if (left is null) return right is not null;
                if (right is null) return true;
                return !(left == right);
            }
            
            public override bool Equals(object? obj)
            {
                return obj is GaussianBlur other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            public override int GetHashCode()
            {
                // TODO: Implement when Swift Hashable protocol binding is supported.
                // Returning constant 0 satisfies the Equals/GetHashCode contract
                // (equal objects must have equal hashes). This is correct but makes
                // hash-based collections O(n) until Hashable is supported.
                return 0;
            }
            
            public bool Equals(GaussianBlur? other)
            {
                return Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO12GaussianBlurVMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new GaussianBlur(handle);
            }
            
            GaussianBlur(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<GaussianBlur>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<GaussianBlur>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static GaussianBlur()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IImageProcessing), "$s4Nuke15ImageProcessorsO12GaussianBlurVAA0B10ProcessingAAMc"},
            {typeof(IEquatable<GaussianBlur>), "$s4Nuke15ImageProcessorsO12GaussianBlurVSQAAMc"}
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type GaussianBlur and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
            
            public unsafe GaussianBlur( nint radius)
            {
                _payload = new SwiftSafeHandle<GaussianBlur>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_033EF042(swiftIndirectResult, radius);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO12GaussianBlurV6radiusAESi_tcfC")]
            private static extern void PInvoke_init_033EF042( SwiftIndirectResult swiftIndirectResult,  nint radius);
            public unsafe GaussianBlur()
            {
                _payload = new SwiftSafeHandle<GaussianBlur>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_56BAB0E6(swiftIndirectResult);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("SwiftBindings", EntryPoint = "DBW_GaussianBlur_init_05E0C1C6_1")]
            private static extern void PInvoke_init_56BAB0E6( SwiftIndirectResult swiftIndirectResult);
            
            
            public unsafe UIKit.UIImage? Process( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_2B56482A(arg0Handle, self);
                    
                    var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<UIKit.UIImage>>(new IntPtr(&result));
                    return swiftResult.ToNullable();
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO12GaussianBlurV7processySo7UIImageCSgAHF")]
            private static extern IntPtr PInvoke_process_2B56482A( IntPtr arg0,  SwiftSelf self);
            
            
            public unsafe Swift.Nuke.ImageContainer Process( Swift.Nuke.ImageContainer arg0,  Swift.Nuke.ImageProcessingContext context)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageContainer>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_process_4999B414(swiftIndirectResult, arg0.Payload, context.Payload, self, out var error);
                    
                    if (error.Value != null)
                    {
                        throw new SwiftRuntimeException("Call to Swift method process failed.");
                    }
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageContainer>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO12GaussianBlurV7process_7contextAA0B9ContainerVAI_AA0B17ProcessingContextVtKF")]
            private static extern void PInvoke_process_4999B414( SwiftIndirectResult swiftIndirectResult,  SafeHandle arg0,  SafeHandle context,  SwiftSelf self, out SwiftError error);
            
            
            public unsafe void Hash(ref Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_19400196(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO12GaussianBlurV4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_19400196( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        public unsafe class Composition : ISwiftObject, IEquatable<Composition>
        {
            private unsafe Swift.SwiftString Identifier_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_identifier_Get_4DB8BDEE(self);
                    
                    unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO11CompositionV10identifierSSvg")]
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_4DB8BDEE( SwiftSelf self);
            
            public string Identifier
            {
                get => Identifier_Get().ToString();
            }
            
            private unsafe Swift.SwiftString Description_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_74B6BDC3(self);
                    
                    unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO11CompositionV11descriptionSSvg")]
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_74B6BDC3( SwiftSelf self);
            
            public string Description
            {
                get => Description_Get().ToString();
            }
            
            private unsafe nint HashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_475FF105(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO11CompositionV9hashValueSivg")]
            private static extern nint PInvoke_hashValue_Get_475FF105( SwiftSelf self);
            
            public nint HashValue
            {
                get => HashValue_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<Composition>.GetTypeMetadata().Size;
            SwiftSafeHandle<Composition> _payload = SwiftSafeHandle<Composition>.Zero;
            
            internal SwiftSafeHandle<Composition> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            public static System.Boolean operator ==(Swift.Nuke.ImageProcessors.Composition arg0, Swift.Nuke.ImageProcessors.Composition arg1)
            {
                if (arg0 is null) return arg1 is null;
                if (arg1 is null) return false;
                return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
            }
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO11CompositionV2eeoiySbAE_AEtFZ")]
            private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
            
            public static bool operator !=(Composition left, Composition right)
            {
                if (left is null) return right is not null;
                if (right is null) return true;
                return !(left == right);
            }
            
            public override bool Equals(object? obj)
            {
                return obj is Composition other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            public override int GetHashCode()
            {
                // TODO: Implement when Swift Hashable protocol binding is supported.
                // Returning constant 0 satisfies the Equals/GetHashCode contract
                // (equal objects must have equal hashes). This is correct but makes
                // hash-based collections O(n) until Hashable is supported.
                return 0;
            }
            
            public bool Equals(Composition? other)
            {
                return Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO11CompositionVMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new Composition(handle);
            }
            
            Composition(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<Composition>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<Composition>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static Composition()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IImageProcessing), "$s4Nuke15ImageProcessorsO11CompositionVAA0B10ProcessingAAMc"},
            {typeof(IEquatable<Composition>), "$s4Nuke15ImageProcessorsO11CompositionVSQAAMc"}
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type Composition and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
            
            
            public unsafe UIKit.UIImage? Process( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_4256B0CE(arg0Handle, self);
                    
                    var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<UIKit.UIImage>>(new IntPtr(&result));
                    return swiftResult.ToNullable();
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO11CompositionV7processySo7UIImageCSgAHF")]
            private static extern IntPtr PInvoke_process_4256B0CE( IntPtr arg0,  SwiftSelf self);
            
            
            public unsafe Swift.Nuke.ImageContainer Process( Swift.Nuke.ImageContainer arg0,  Swift.Nuke.ImageProcessingContext context)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageContainer>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_process_4601F899(swiftIndirectResult, arg0.Payload, context.Payload, self, out var error);
                    
                    if (error.Value != null)
                    {
                        throw new SwiftRuntimeException("Call to Swift method process failed.");
                    }
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageContainer>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO11CompositionV7process_7contextAA0B9ContainerVAI_AA0B17ProcessingContextVtKF")]
            private static extern void PInvoke_process_4601F899( SwiftIndirectResult swiftIndirectResult,  SafeHandle arg0,  SafeHandle context,  SwiftSelf self, out SwiftError error);
            
            
            public unsafe void Hash(ref Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_7AEE57D6(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO11CompositionV4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_7AEE57D6( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        public unsafe class Circle : ISwiftObject, IEquatable<Circle>
        {
            private unsafe Swift.SwiftString Identifier_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_identifier_Get_6775E0D3(self);
                    
                    unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6CircleV10identifierSSvg")]
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_6775E0D3( SwiftSelf self);
            
            public string Identifier
            {
                get => Identifier_Get().ToString();
            }
            
            private unsafe Swift.SwiftString Description_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_4368BC28(self);
                    
                    unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6CircleV11descriptionSSvg")]
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_4368BC28( SwiftSelf self);
            
            public string Description
            {
                get => Description_Get().ToString();
            }
            
            private unsafe nint HashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_0E5004DE(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6CircleV9hashValueSivg")]
            private static extern nint PInvoke_hashValue_Get_0E5004DE( SwiftSelf self);
            
            public nint HashValue
            {
                get => HashValue_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<Circle>.GetTypeMetadata().Size;
            SwiftSafeHandle<Circle> _payload = SwiftSafeHandle<Circle>.Zero;
            
            internal SwiftSafeHandle<Circle> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            public static System.Boolean operator ==(Swift.Nuke.ImageProcessors.Circle arg0, Swift.Nuke.ImageProcessors.Circle arg1)
            {
                if (arg0 is null) return arg1 is null;
                if (arg1 is null) return false;
                return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
            }
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6CircleV2eeoiySbAE_AEtFZ")]
            private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
            
            public static bool operator !=(Circle left, Circle right)
            {
                if (left is null) return right is not null;
                if (right is null) return true;
                return !(left == right);
            }
            
            public override bool Equals(object? obj)
            {
                return obj is Circle other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            public override int GetHashCode()
            {
                // TODO: Implement when Swift Hashable protocol binding is supported.
                // Returning constant 0 satisfies the Equals/GetHashCode contract
                // (equal objects must have equal hashes). This is correct but makes
                // hash-based collections O(n) until Hashable is supported.
                return 0;
            }
            
            public bool Equals(Circle? other)
            {
                return Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6CircleVMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new Circle(handle);
            }
            
            Circle(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<Circle>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<Circle>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static Circle()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IImageProcessing), "$s4Nuke15ImageProcessorsO6CircleVAA0B10ProcessingAAMc"},
            {typeof(IEquatable<Circle>), "$s4Nuke15ImageProcessorsO6CircleVSQAAMc"}
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type Circle and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
            
            public unsafe Circle( Swift.Nuke.ImageProcessingOptions.Border? border)
            {
                _payload = new SwiftSafeHandle<Circle>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                using var borderSwift = border is {} borderValue ? SwiftOptional<Swift.Nuke.ImageProcessingOptions.Border>.NewSome(borderValue) : SwiftOptional<Swift.Nuke.ImageProcessingOptions.Border>.NewNone();
                using PayloadBuffer<IntPtr> borderDisposable = borderSwift.PayloadBuffer;
                IntPtr borderBuffer = borderDisposable.Buffer;
                PInvoke_init_57724314(swiftIndirectResult, borderBuffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6CircleV6borderAeA0B17ProcessingOptionsO6BorderVSg_tcfC")]
            private static extern void PInvoke_init_57724314( SwiftIndirectResult swiftIndirectResult,  IntPtr borderBuffer);
            public unsafe Circle()
            {
                _payload = new SwiftSafeHandle<Circle>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_5D78B27C(swiftIndirectResult);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("SwiftBindings", EntryPoint = "DBW_Circle_init_36B9D008_1")]
            private static extern void PInvoke_init_5D78B27C( SwiftIndirectResult swiftIndirectResult);
            
            
            public unsafe UIKit.UIImage? Process( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_0DDA789D(arg0Handle, self);
                    
                    var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<UIKit.UIImage>>(new IntPtr(&result));
                    return swiftResult.ToNullable();
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6CircleV7processySo7UIImageCSgAHF")]
            private static extern IntPtr PInvoke_process_0DDA789D( IntPtr arg0,  SwiftSelf self);
            
            
            public unsafe void Hash(ref Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_5A9E17CB(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6CircleV4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_5A9E17CB( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        public unsafe class CoreImageFilter : ISwiftObject
        {
            private unsafe Swift.SwiftString Identifier_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_identifier_Get_64934208(self);
                    
                    unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV10identifierSSvg")]
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_64934208( SwiftSelf self);
            
            public string Identifier
            {
                get => Identifier_Get().ToString();
            }
            
            private static unsafe Swift.CIContext Context_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.CIContext>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_context_Get_4EF0FE1D(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.CIContext>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV7contextSo9CIContextCvgZ")]
            private static extern void PInvoke_context_Get_4EF0FE1D( SwiftIndirectResult swiftIndirectResult);
            
            private static void Context_Set( Swift.CIContext value)
            {
                try
                {
                    
                    
                    PInvoke_context_Set_1897B870(value.Payload);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV7contextSo9CIContextCvsZ")]
            private static extern void PInvoke_context_Set_1897B870( SafeHandle value);
            
            public static Swift.CIContext Context
            {
                get => Context_Get();
                set => Context_Set(value);
            }
            
            private unsafe Swift.SwiftString Description_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_48B699DB(self);
                    
                    unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV11descriptionSSvg")]
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_48B699DB( SwiftSelf self);
            
            public string Description
            {
                get => Description_Get().ToString();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<CoreImageFilter>.GetTypeMetadata().Size;
            SwiftSafeHandle<CoreImageFilter> _payload = SwiftSafeHandle<CoreImageFilter>.Zero;
            
            internal SwiftSafeHandle<CoreImageFilter> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterVMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new CoreImageFilter(handle);
            }
            
            CoreImageFilter(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<CoreImageFilter>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<CoreImageFilter>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static CoreImageFilter()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IImageProcessing), "$s4Nuke15ImageProcessorsO04CoreB6FilterVAA0B10ProcessingAAMc"}
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type CoreImageFilter and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
            
            public unsafe class Error : ISwiftObject
            {
                static nuint _payloadSize = SwiftObjectHelper<Error>.GetTypeMetadata().Size;
                SwiftSafeHandle<Error> _payload = SwiftSafeHandle<Error>.Zero;
                internal SwiftSafeHandle<Error> Payload => _payload;
                
                public void Dispose() => _payload.Dispose();
                
                /// <summary>
                /// Creates the 'failedToCreateFilter' case of Error.
                /// </summary>
                public static Error FailedToCreateFilter((Swift.SwiftString name, Swift.SwiftDictionary<Swift.SwiftString, Swift.Runtime.ExistentialContainer0> parameters) value0)
                {
                    var result = new Error();
                    var metadata = PInvoke_getMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    var indirectResult = new SwiftIndirectResult((void*)buffer);
                    PInvoke_FailedToCreateFilter(indirectResult, (value0.name.Payload.DangerousGetHandle(), value0.parameters.Payload.DangerousGetHandle()));
                    result._payload = new SwiftSafeHandle<Error>(buffer);
                    return result;
                }
                
                [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV5ErrorO014failedToCreateE0yAGSS_SDySSypGtcAGmF")]
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                private static extern void PInvoke_FailedToCreateFilter(SwiftIndirectResult result, ValueTuple<IntPtr, IntPtr> value0);
                
                /// <summary>
                /// Creates the 'inputImageIsEmpty' case of Error.
                /// </summary>
                public static Error InputImageIsEmpty(UIKit.UIImage inputImage)
                {
                    var result = new Error();
                    var metadata = PInvoke_getMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    var indirectResult = new SwiftIndirectResult((void*)buffer);
                    PInvoke_InputImageIsEmpty(indirectResult, inputImage.Handle);
                    result._payload = new SwiftSafeHandle<Error>(buffer);
                    return result;
                }
                
                [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV5ErrorO05inputB7IsEmptyyAGSo7UIImageC_tcAGmF")]
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                private static extern void PInvoke_InputImageIsEmpty(SwiftIndirectResult result, IntPtr inputImage);
                
                /// <summary>
                /// Creates the 'failedToApplyFilter' case of Error.
                /// </summary>
                public static Error FailedToApplyFilter(CoreImage.CIFilter filter)
                {
                    var result = new Error();
                    var metadata = PInvoke_getMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    var indirectResult = new SwiftIndirectResult((void*)buffer);
                    PInvoke_FailedToApplyFilter(indirectResult, filter.Handle);
                    result._payload = new SwiftSafeHandle<Error>(buffer);
                    return result;
                }
                
                [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV5ErrorO013failedToApplyE0yAGSo8CIFilterC_tcAGmF")]
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                private static extern void PInvoke_FailedToApplyFilter(SwiftIndirectResult result, IntPtr filter);
                
                /// <summary>
                /// Creates the 'failedToCreateOutputCGImage' case of Error.
                /// </summary>
                public static Error FailedToCreateOutputCGImage(CoreImage.CIImage image)
                {
                    var result = new Error();
                    var metadata = PInvoke_getMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    var indirectResult = new SwiftIndirectResult((void*)buffer);
                    PInvoke_FailedToCreateOutputCGImage(indirectResult, image.Handle);
                    result._payload = new SwiftSafeHandle<Error>(buffer);
                    return result;
                }
                
                [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV5ErrorO27failedToCreateOutputCGImageyAGSo7CIImageC_tcAGmF")]
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                private static extern void PInvoke_FailedToCreateOutputCGImage(SwiftIndirectResult result, IntPtr image);
                
                /// <summary>
                /// Enum representing the possible cases of Error.
                /// Tag values follow Swift's ordering: payload cases first, then no-payload cases.
                /// </summary>
                public enum CaseTag : uint
                {
                    FailedToCreateFilter = 0,
                    InputImageIsEmpty = 1,
                    FailedToApplyFilter = 2,
                    FailedToCreateOutputCGImage = 3,
                }
                
                /// <summary>
                /// Gets the current case of this enum instance.
                /// </summary>
                public unsafe CaseTag Tag
                {
                    get
                    {
                        bool success = false;
                        _payload.DangerousAddRef(ref success);
                        try
                        {
                            var metadata = SwiftObjectHelper<Error>.GetTypeMetadata();
                            byte* payload = (byte*)_payload.DangerousGetHandle();
                            return (CaseTag)metadata.ValueWitnessTable->GetEnumTag(payload, metadata);
                        }
                        finally
                        {
                            if (success)
                                _payload.DangerousRelease();
                        }
                    }
                }
                
                /// <summary>
                /// Attempts to extract the associated value(s) for the 'failedToCreateFilter' case.
                /// </summary>
                /// <param name="name">When this method returns true, contains the associated value.</param>
                /// <param name="parameters">When this method returns true, contains the associated value.</param>
                /// <returns>True if this enum is the 'failedToCreateFilter' case; otherwise, false.</returns>
                public unsafe bool TryGetFailedToCreateFilter([MaybeNullWhen(false)] out Swift.SwiftString name, [MaybeNullWhen(false)] out Swift.SwiftDictionary<Swift.SwiftString, Swift.Runtime.ExistentialContainer0> parameters)
                {
                    if (Tag != CaseTag.FailedToCreateFilter)
                    {
                        name = default;
                        parameters = default;
                        return false;
                    }
                    
                    var metadata = SwiftObjectHelper<Error>.GetTypeMetadata();
                    
                    // Create a non-destructive copy of the enum
                    byte* enumCopy = stackalloc byte[(int)metadata.Size];
                    bool success = false;
                    _payload.DangerousAddRef(ref success);
                    try
                    {
                        metadata.ValueWitnessTable->InitializeWithCopy(enumCopy, (void*)_payload.DangerousGetHandle(), metadata);
                    }
                    finally
                    {
                        if (success)
                            _payload.DangerousRelease();
                    }
                    
                    // Strip the tag to get the raw payload (which is the tuple)
                    metadata.ValueWitnessTable->DestructiveProjectEnumData(enumCopy, metadata);
                    
                    // Get tuple metadata to determine element offsets
                    var tupleMetadata = GetTupleMetadata_FailedToCreateFilter();
                    
                    // Marshal each tuple element from its computed offset
                    var offset0 = tupleMetadata->GetElementOffset(0);
                    name = SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(enumCopy + (int)offset0));
                    var offset1 = tupleMetadata->GetElementOffset(1);
                    parameters = SwiftMarshal.MarshalFromSwift<Swift.SwiftDictionary<Swift.SwiftString, Swift.Runtime.ExistentialContainer0>>(new IntPtr(enumCopy + (int)offset1));
                    
                    return true;
                }
                
                private static TupleTypeMetadata* _tupleMetadata_FailedToCreateFilter;
                
                private static unsafe TupleTypeMetadata* GetTupleMetadata_FailedToCreateFilter()
                {
                    if (_tupleMetadata_FailedToCreateFilter != null)
                        return _tupleMetadata_FailedToCreateFilter;
                    
                    // Build tuple metadata from element types
                    var elementMetadataArray = new TypeMetadata[2];
                    elementMetadataArray[0] = SwiftObjectHelper<Swift.SwiftString>.GetTypeMetadata();
                    elementMetadataArray[1] = SwiftObjectHelper<Swift.SwiftDictionary<Swift.SwiftString, Swift.Runtime.ExistentialContainer0>>.GetTypeMetadata();
                    
                    // Get tuple type metadata from Swift runtime
                    var tupleMetadata = TypeMetadata.GetTupleTypeMetadataFromElements(elementMetadataArray);
                    
                    _tupleMetadata_FailedToCreateFilter = tupleMetadata.AsTupleMetadata();
                    return _tupleMetadata_FailedToCreateFilter;
                }
                
                /// <summary>
                /// Attempts to extract the associated value(s) for the 'inputImageIsEmpty' case.
                /// </summary>
                /// <param name="value">When this method returns true, contains the associated value(s).</param>
                /// <returns>True if this enum is the 'inputImageIsEmpty' case; otherwise, false.</returns>
                public unsafe bool TryGetInputImageIsEmpty([MaybeNullWhen(false)] out UIKit.UIImage value)
                {
                    if (Tag != CaseTag.InputImageIsEmpty)
                    {
                        value = default;
                        return false;
                    }
                    
                    var metadata = SwiftObjectHelper<Error>.GetTypeMetadata();
                    
                    // Create a non-destructive copy of the enum
                    byte* enumCopy = stackalloc byte[(int)metadata.Size];
                    bool success = false;
                    _payload.DangerousAddRef(ref success);
                    try
                    {
                        metadata.ValueWitnessTable->InitializeWithCopy(enumCopy, (void*)_payload.DangerousGetHandle(), metadata);
                    }
                    finally
                    {
                        if (success)
                            _payload.DangerousRelease();
                    }
                    
                    // Strip the tag to get the raw payload
                    metadata.ValueWitnessTable->DestructiveProjectEnumData(enumCopy, metadata);
                    
                    // Marshal the payload to C# type(s)
                    value = SwiftMarshal.MarshalFromSwift<UIKit.UIImage>(new IntPtr(enumCopy));
                    return true;
                }
                
                /// <summary>
                /// Attempts to extract the associated value(s) for the 'failedToApplyFilter' case.
                /// </summary>
                /// <param name="value">When this method returns true, contains the associated value(s).</param>
                /// <returns>True if this enum is the 'failedToApplyFilter' case; otherwise, false.</returns>
                public unsafe bool TryGetFailedToApplyFilter([MaybeNullWhen(false)] out CoreImage.CIFilter value)
                {
                    if (Tag != CaseTag.FailedToApplyFilter)
                    {
                        value = default;
                        return false;
                    }
                    
                    var metadata = SwiftObjectHelper<Error>.GetTypeMetadata();
                    
                    // Create a non-destructive copy of the enum
                    byte* enumCopy = stackalloc byte[(int)metadata.Size];
                    bool success = false;
                    _payload.DangerousAddRef(ref success);
                    try
                    {
                        metadata.ValueWitnessTable->InitializeWithCopy(enumCopy, (void*)_payload.DangerousGetHandle(), metadata);
                    }
                    finally
                    {
                        if (success)
                            _payload.DangerousRelease();
                    }
                    
                    // Strip the tag to get the raw payload
                    metadata.ValueWitnessTable->DestructiveProjectEnumData(enumCopy, metadata);
                    
                    // Marshal the payload to C# type(s)
                    value = SwiftMarshal.MarshalFromSwift<CoreImage.CIFilter>(new IntPtr(enumCopy));
                    return true;
                }
                
                /// <summary>
                /// Attempts to extract the associated value(s) for the 'failedToCreateOutputCGImage' case.
                /// </summary>
                /// <param name="value">When this method returns true, contains the associated value(s).</param>
                /// <returns>True if this enum is the 'failedToCreateOutputCGImage' case; otherwise, false.</returns>
                public unsafe bool TryGetFailedToCreateOutputCGImage([MaybeNullWhen(false)] out CoreImage.CIImage value)
                {
                    if (Tag != CaseTag.FailedToCreateOutputCGImage)
                    {
                        value = default;
                        return false;
                    }
                    
                    var metadata = SwiftObjectHelper<Error>.GetTypeMetadata();
                    
                    // Create a non-destructive copy of the enum
                    byte* enumCopy = stackalloc byte[(int)metadata.Size];
                    bool success = false;
                    _payload.DangerousAddRef(ref success);
                    try
                    {
                        metadata.ValueWitnessTable->InitializeWithCopy(enumCopy, (void*)_payload.DangerousGetHandle(), metadata);
                    }
                    finally
                    {
                        if (success)
                            _payload.DangerousRelease();
                    }
                    
                    // Strip the tag to get the raw payload
                    metadata.ValueWitnessTable->DestructiveProjectEnumData(enumCopy, metadata);
                    
                    // Marshal the payload to C# type(s)
                    value = SwiftMarshal.MarshalFromSwift<CoreImage.CIImage>(new IntPtr(enumCopy));
                    return true;
                }
                
                
                private unsafe Swift.SwiftString Description_Get()
                {
                    try
                    {
                        var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                        
                        
                        
                        var result = PInvoke_description_Get_15982A2C(self);
                        
                        unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
                    }
                    
                    finally
                    {
                    }
                    
                }
                
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV5ErrorO11descriptionSSvg")]
                private static extern Swift.SwiftString.Buffer PInvoke_description_Get_15982A2C( SwiftSelf self);
                
                public string Description
                {
                    get => Description_Get().ToString();
                }
                
                static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
                
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV5ErrorOMa")]
                internal static extern TypeMetadata PInvoke_getMetadata();
                
                static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                {
                    return new Error(handle);
                }
                
                Error()
                {
                }
                
                Error(SwiftHandle handle)
                {
                    _payload = new SwiftSafeHandle<Error>(handle);
                }
                
                unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                {
                    var metadata = SwiftObjectHelper<Error>.GetTypeMetadata();
                    if ((int)metadata.Size > swiftDestSpan.Length)
                    {
                        throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                    }
                    fixed (void* swiftDest = swiftDestSpan)
                    {
                        // Ensure that the instance is valid before making copy
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
                
                private static Dictionary<Type, string> _protocolConformanceSymbols;
                static Error()
                {
                    _protocolConformanceSymbols = new Dictionary<Type, string>
                    {
                        
                    };
                }
                
                static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                    where TProtocol : class
                {
                    if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                    {
                        throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type Error and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                    }
                    return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
                }
                
            }
            
            
            
            public unsafe CoreImageFilter( string name)
            {
                _payload = new SwiftSafeHandle<CoreImageFilter>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                using var nameSwift = new SwiftString(name);
                using PayloadBuffer<SwiftString.Buffer> nameDisposable = nameSwift.PayloadBuffer;
                PInvoke_init_4F21E5CB(swiftIndirectResult, nameDisposable.Buffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV4nameAESS_tcfC")]
            private static extern void PInvoke_init_4F21E5CB( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer name);
            
            
            public unsafe CoreImageFilter( CoreImage.CIFilter arg0,  string identifier)
            {
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                _payload = new SwiftSafeHandle<CoreImageFilter>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                using var identifierSwift = new SwiftString(identifier);
                using PayloadBuffer<SwiftString.Buffer> identifierDisposable = identifierSwift.PayloadBuffer;
                PInvoke_init_349A48E1(swiftIndirectResult, arg0Handle, identifierDisposable.Buffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV_10identifierAESo8CIFilterC_SStcfC")]
            private static extern void PInvoke_init_349A48E1( SwiftIndirectResult swiftIndirectResult,  IntPtr arg0,  Swift.SwiftString.Buffer identifier);
            
            
            public unsafe UIKit.UIImage? Process( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_7B6C9288(arg0Handle, self);
                    
                    var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<UIKit.UIImage>>(new IntPtr(&result));
                    return swiftResult.ToNullable();
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV7processySo7UIImageCSgAHF")]
            private static extern IntPtr PInvoke_process_7B6C9288( IntPtr arg0,  SwiftSelf self);
            
            
            public unsafe Swift.Nuke.ImageContainer Process( Swift.Nuke.ImageContainer arg0,  Swift.Nuke.ImageProcessingContext context)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageContainer>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_process_0587D3D8(swiftIndirectResult, arg0.Payload, context.Payload, self, out var error);
                    
                    if (error.Value != null)
                    {
                        throw new SwiftRuntimeException("Call to Swift method process failed.");
                    }
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageContainer>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV7process_7contextAA0B9ContainerVAI_AA0B17ProcessingContextVtKF")]
            private static extern void PInvoke_process_0587D3D8( SwiftIndirectResult swiftIndirectResult,  SafeHandle arg0,  SafeHandle context,  SwiftSelf self, out SwiftError error);
            
            
            public static unsafe UIKit.UIImage Apply( CoreImage.CIFilter filter,  UIKit.UIImage to)
            {
                IntPtr filterHandle = filter?.Handle ?? IntPtr.Zero;
                IntPtr toHandle = to?.Handle ?? IntPtr.Zero;
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<UIKit.UIImage>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_apply_52344BBE(swiftIndirectResult, filterHandle, toHandle, out var error);
                    
                    if (error.Value != null)
                    {
                        throw new SwiftRuntimeException("Call to Swift method apply failed.");
                    }
                    
                    return SwiftMarshal.MarshalFromSwift<UIKit.UIImage>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV5apply6filter2toSo7UIImageCSo8CIFilterC_AJtKFZ")]
            private static extern void PInvoke_apply_52344BBE( SwiftIndirectResult swiftIndirectResult,  IntPtr filter,  IntPtr to, out SwiftError error);
            
            
        }
        
        
    }
    
    
    public unsafe class ImageRequest : ISwiftObject
    {
        private unsafe Swift.Nuke.ImageRequest.PriorityInfo Priority_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.PriorityInfo>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_priority_Get_2F0A5CCF(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.PriorityInfo>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV8priorityAC8PriorityOvg")]
        private static extern void PInvoke_priority_Get_2F0A5CCF( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Priority_Set( Swift.Nuke.ImageRequest.PriorityInfo value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_priority_Set_36F1F833(value.Payload.DangerousGetHandle(), self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV8priorityAC8PriorityOvs")]
        private static extern void PInvoke_priority_Set_36F1F833( IntPtr value,  SwiftSelf self);
        
        public Swift.Nuke.ImageRequest.PriorityInfo Priority
        {
            get => Priority_Get();
            set => Priority_Set(value);
        }
        
        private unsafe Swift.Nuke.ImageRequest.OptionsInfo Options_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.OptionsInfo>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_options_Get_7FE67A7C(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.OptionsInfo>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7optionsAC7OptionsVvg")]
        private static extern void PInvoke_options_Get_7FE67A7C( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Options_Set( Swift.Nuke.ImageRequest.OptionsInfo value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_options_Set_378F6EED(value.Payload, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7optionsAC7OptionsVvs")]
        private static extern void PInvoke_options_Set_378F6EED( SafeHandle value,  SwiftSelf self);
        
        public Swift.Nuke.ImageRequest.OptionsInfo Options
        {
            get => Options_Get();
            set => Options_Set(value);
        }
        
        private unsafe Swift.SwiftOptional<Swift.URLRequest> UrlRequest_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_urlRequest_Get_21816F22(self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.URLRequest>>(new IntPtr(&result));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV03urlC010Foundation10URLRequestVSgvg")]
        private static extern IntPtr PInvoke_urlRequest_Get_21816F22( SwiftSelf self);
        
        public Swift.URLRequest? UrlRequest
        {
            get => ((Swift.URLRequest?)UrlRequest_Get());
        }
        
        private unsafe Swift.SwiftOptional<Swift.URL> Url_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_url_Get_26E92334(self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.URL>>(new IntPtr(&result));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV3url10Foundation3URLVSgvg")]
        private static extern IntPtr PInvoke_url_Get_26E92334( SwiftSelf self);
        
        public Swift.URL? Url
        {
            get => ((Swift.URL?)Url_Get());
        }
        
        private unsafe Swift.SwiftOptional<Swift.SwiftString> ImageId_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_imageId_Get_656BE862(self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.SwiftString>>(new IntPtr(&result));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7imageIdSSSgvg")]
        private static extern IntPtr PInvoke_imageId_Get_656BE862( SwiftSelf self);
        
        public Swift.SwiftString? ImageId
        {
            get => ((Swift.SwiftString?)ImageId_Get());
        }
        
        private unsafe Swift.SwiftString Description_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_description_Get_053929DF(self);
                
                unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV11descriptionSSvg")]
        private static extern Swift.SwiftString.Buffer PInvoke_description_Get_053929DF( SwiftSelf self);
        
        public string Description
        {
            get => Description_Get().ToString();
        }
        
        static nuint _payloadSize = SwiftObjectHelper<ImageRequest>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageRequest> _payload = SwiftSafeHandle<ImageRequest>.Zero;
        
        internal SwiftSafeHandle<ImageRequest> Payload => _payload;
        
        public void Dispose() => _payload.Dispose();
        
        static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestVMa")]
        internal static extern TypeMetadata PInvoke_getMetadata();
        
        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
        {
            return new ImageRequest(handle);
        }
        
        ImageRequest(SwiftHandle handle)
        {
            _payload = new SwiftSafeHandle<ImageRequest>(handle);
        }
        
        unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            var metadata = SwiftObjectHelper<ImageRequest>.GetTypeMetadata();
            if ((int)metadata.Size > swiftDestSpan.Length)
            {
                throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
            }
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure that the instance is valid before making copy
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
        
        private static Dictionary<Type, string> _protocolConformanceSymbols;
        static ImageRequest()
        {
            _protocolConformanceSymbols = new Dictionary<Type, string>
            {
                
            };
        }
        
        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
            where TProtocol : class
        {
            if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
            {
                throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type ImageRequest and protocol {typeof(TProtocol).Name}, but no conformance was found.");
            }
            return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
        }
        
        
        public unsafe class PriorityInfo : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<PriorityInfo>.GetTypeMetadata().Size;
            SwiftSafeHandle<PriorityInfo> _payload = SwiftSafeHandle<PriorityInfo>.Zero;
            internal SwiftSafeHandle<PriorityInfo> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            /// <summary>
            /// Creates a PriorityInfo from its raw value.
            /// Returns null if the raw value doesn't correspond to a valid case.
            /// </summary>
            public static unsafe PriorityInfo? FromRawValue(long rawValue)
            {
                // Get metadata for the enum type
                var enumMetadata = PInvoke_getMetadata();
                
                // Get metadata for SwiftOptional<EnumType>
                var optionalMetadata = PInvokesForSwiftOptional_MetadataAccessor(
                    TypeMetadataRequest.Complete, enumMetadata);
                
                // Allocate buffer for SwiftOptional<EnumType> result
                void* resultBuffer = NativeMemory.AllocZeroed(optionalMetadata.Size);
                try
                {
                    // Call the failable initializer with indirect result
                    var swiftIndirectResult = new SwiftIndirectResult(resultBuffer);
                    PInvoke_InitWithRawValue(swiftIndirectResult, rawValue);
                    
                    // Check if result is Some (tag 0) or None (tag 1)
                    uint tag = optionalMetadata.ValueWitnessTable->GetEnumTag((byte*)resultBuffer, optionalMetadata);
                    
                    // SwiftOptionalCases.None = 1
                    if (tag == 1)
                    {
                        return null;
                    }
                    
                    // Extract the enum value from the optional's payload
                    IntPtr enumBuffer = (IntPtr)NativeMemory.Alloc(enumMetadata.Size);
                    enumMetadata.ValueWitnessTable->InitializeWithCopy((void*)enumBuffer, resultBuffer, enumMetadata);
                    
                    var result = new PriorityInfo();
                    result._payload = new SwiftSafeHandle<PriorityInfo>(enumBuffer);
                    return result;
                }
                finally
                {
                    // Clean up the optional buffer
                    optionalMetadata.ValueWitnessTable->Destroy(resultBuffer, optionalMetadata);
                    NativeMemory.Free(resultBuffer);
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV8PriorityO8rawValueAESgSi_tcfC")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_InitWithRawValue(SwiftIndirectResult result, long rawValue);
            
            // SwiftOptional metadata accessor from Swift stdlib
            [DllImport("/usr/lib/swift/libswiftCore.dylib", EntryPoint = "$sSqMa")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern TypeMetadata PInvokesForSwiftOptional_MetadataAccessor(TypeMetadataRequest request, TypeMetadata typeMetadata);
            
            /// <summary>
            /// Gets the 'veryLow' case of PriorityInfo.
            /// </summary>
            public static PriorityInfo VeryLow
            {
                get
                {
                    var result = FromRawValue(0);
                    if (result == null)
                    {
                        throw new InvalidOperationException("Failed to create PriorityInfo.VeryLow from raw value 0");
                    }
                    return result;
                }
            }
            
            /// <summary>
            /// Gets the 'low' case of PriorityInfo.
            /// </summary>
            public static PriorityInfo Low
            {
                get
                {
                    var result = FromRawValue(1);
                    if (result == null)
                    {
                        throw new InvalidOperationException("Failed to create PriorityInfo.Low from raw value 1");
                    }
                    return result;
                }
            }
            
            /// <summary>
            /// Gets the 'normal' case of PriorityInfo.
            /// </summary>
            public static PriorityInfo Normal
            {
                get
                {
                    var result = FromRawValue(2);
                    if (result == null)
                    {
                        throw new InvalidOperationException("Failed to create PriorityInfo.Normal from raw value 2");
                    }
                    return result;
                }
            }
            
            /// <summary>
            /// Gets the 'high' case of PriorityInfo.
            /// </summary>
            public static PriorityInfo High
            {
                get
                {
                    var result = FromRawValue(3);
                    if (result == null)
                    {
                        throw new InvalidOperationException("Failed to create PriorityInfo.High from raw value 3");
                    }
                    return result;
                }
            }
            
            /// <summary>
            /// Gets the 'veryHigh' case of PriorityInfo.
            /// </summary>
            public static PriorityInfo VeryHigh
            {
                get
                {
                    var result = FromRawValue(4);
                    if (result == null)
                    {
                        throw new InvalidOperationException("Failed to create PriorityInfo.VeryHigh from raw value 4");
                    }
                    return result;
                }
            }
            
            /// <summary>
            /// Enum representing the possible cases of Priority.
            /// Tag values follow Swift's ordering: payload cases first, then no-payload cases.
            /// </summary>
            public enum CaseTag : uint
            {
                VeryLow = 0,
                Low = 1,
                Normal = 2,
                High = 3,
                VeryHigh = 4,
            }
            
            /// <summary>
            /// Gets the current case of this enum instance.
            /// </summary>
            public unsafe CaseTag Tag
            {
                get
                {
                    bool success = false;
                    _payload.DangerousAddRef(ref success);
                    try
                    {
                        var metadata = SwiftObjectHelper<PriorityInfo>.GetTypeMetadata();
                        byte* payload = (byte*)_payload.DangerousGetHandle();
                        return (CaseTag)metadata.ValueWitnessTable->GetEnumTag(payload, metadata);
                    }
                    finally
                    {
                        if (success)
                            _payload.DangerousRelease();
                    }
                }
            }
            
            
            private unsafe nint RawValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_rawValue_Get_1DA0CA8E(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV8PriorityO8rawValueSivg")]
            private static extern nint PInvoke_rawValue_Get_1DA0CA8E( SwiftSelf self);
            
            public nint RawValue
            {
                get => RawValue_Get();
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV8PriorityOMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new PriorityInfo(handle);
            }
            
            PriorityInfo()
            {
            }
            
            PriorityInfo(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<PriorityInfo>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<PriorityInfo>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static PriorityInfo()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IEquatable<PriorityInfo>), "$s4Nuke12ImageRequestV8PriorityOSQAAMc"}
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type Priority and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
        }
        
        
        public unsafe class OptionsInfo : ISwiftObject, IEquatable<OptionsInfo>
        {
            private unsafe System.UInt16 RawValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_rawValue_Get_18E963D7(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV8rawValues6UInt16Vvg")]
            private static extern System.UInt16 PInvoke_rawValue_Get_18E963D7( SwiftSelf self);
            
            public System.UInt16 RawValue
            {
                get => RawValue_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.OptionsInfo DisableMemoryCacheReads_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.OptionsInfo>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_disableMemoryCacheReads_Get_2CADBB48(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.OptionsInfo>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV23disableMemoryCacheReadsAEvgZ")]
            private static extern void PInvoke_disableMemoryCacheReads_Get_2CADBB48( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.OptionsInfo DisableMemoryCacheReads
            {
                get => DisableMemoryCacheReads_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.OptionsInfo DisableMemoryCacheWrites_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.OptionsInfo>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_disableMemoryCacheWrites_Get_51C57767(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.OptionsInfo>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV24disableMemoryCacheWritesAEvgZ")]
            private static extern void PInvoke_disableMemoryCacheWrites_Get_51C57767( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.OptionsInfo DisableMemoryCacheWrites
            {
                get => DisableMemoryCacheWrites_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.OptionsInfo DisableMemoryCache_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.OptionsInfo>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_disableMemoryCache_Get_37746B88(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.OptionsInfo>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV18disableMemoryCacheAEvgZ")]
            private static extern void PInvoke_disableMemoryCache_Get_37746B88( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.OptionsInfo DisableMemoryCache
            {
                get => DisableMemoryCache_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.OptionsInfo DisableDiskCacheReads_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.OptionsInfo>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_disableDiskCacheReads_Get_71D66E55(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.OptionsInfo>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV21disableDiskCacheReadsAEvgZ")]
            private static extern void PInvoke_disableDiskCacheReads_Get_71D66E55( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.OptionsInfo DisableDiskCacheReads
            {
                get => DisableDiskCacheReads_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.OptionsInfo DisableDiskCacheWrites_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.OptionsInfo>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_disableDiskCacheWrites_Get_5664C7BE(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.OptionsInfo>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV22disableDiskCacheWritesAEvgZ")]
            private static extern void PInvoke_disableDiskCacheWrites_Get_5664C7BE( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.OptionsInfo DisableDiskCacheWrites
            {
                get => DisableDiskCacheWrites_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.OptionsInfo DisableDiskCache_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.OptionsInfo>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_disableDiskCache_Get_0279231E(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.OptionsInfo>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV16disableDiskCacheAEvgZ")]
            private static extern void PInvoke_disableDiskCache_Get_0279231E( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.OptionsInfo DisableDiskCache
            {
                get => DisableDiskCache_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.OptionsInfo ReloadIgnoringCachedData_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.OptionsInfo>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_reloadIgnoringCachedData_Get_3D37B397(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.OptionsInfo>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV24reloadIgnoringCachedDataAEvgZ")]
            private static extern void PInvoke_reloadIgnoringCachedData_Get_3D37B397( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.OptionsInfo ReloadIgnoringCachedData
            {
                get => ReloadIgnoringCachedData_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.OptionsInfo ReturnCacheDataDontLoad_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.OptionsInfo>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_returnCacheDataDontLoad_Get_3723460D(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.OptionsInfo>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV23returnCacheDataDontLoadAEvgZ")]
            private static extern void PInvoke_returnCacheDataDontLoad_Get_3723460D( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.OptionsInfo ReturnCacheDataDontLoad
            {
                get => ReturnCacheDataDontLoad_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.OptionsInfo SkipDecompression_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.OptionsInfo>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_skipDecompression_Get_765C6816(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.OptionsInfo>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV17skipDecompressionAEvgZ")]
            private static extern void PInvoke_skipDecompression_Get_765C6816( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.OptionsInfo SkipDecompression
            {
                get => SkipDecompression_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.OptionsInfo SkipDataLoadingQueue_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.OptionsInfo>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_skipDataLoadingQueue_Get_0A6BDCFD(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.OptionsInfo>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV20skipDataLoadingQueueAEvgZ")]
            private static extern void PInvoke_skipDataLoadingQueue_Get_0A6BDCFD( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.OptionsInfo SkipDataLoadingQueue
            {
                get => SkipDataLoadingQueue_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<OptionsInfo>.GetTypeMetadata().Size;
            SwiftSafeHandle<OptionsInfo> _payload = SwiftSafeHandle<OptionsInfo>.Zero;
            
            internal SwiftSafeHandle<OptionsInfo> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            public override bool Equals(object? obj)
            {
                return obj is OptionsInfo other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            public override int GetHashCode()
            {
                // TODO: Implement when Swift Hashable protocol binding is supported.
                // Returning constant 0 satisfies the Equals/GetHashCode contract
                // (equal objects must have equal hashes). This is correct but makes
                // hash-based collections O(n) until Hashable is supported.
                return 0;
            }
            
            public static bool operator ==(OptionsInfo left, OptionsInfo right)
            {
                return Swift.Runtime.SwiftEquatable.Equals(left, right);
            }
            
            public static bool operator !=(OptionsInfo left, OptionsInfo right)
            {
                return !Swift.Runtime.SwiftEquatable.Equals(left, right);
            }
            
            public bool Equals(OptionsInfo? other)
            {
                return Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsVMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new OptionsInfo(handle);
            }
            
            OptionsInfo(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<OptionsInfo>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<OptionsInfo>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static OptionsInfo()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IEquatable<OptionsInfo>), "$s4Nuke12ImageRequestV7OptionsVSQAAMc"}
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type Options and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
            
            public unsafe OptionsInfo( System.UInt16 rawValue)
            {
                _payload = new SwiftSafeHandle<OptionsInfo>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_3A20D625(swiftIndirectResult, rawValue);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV8rawValueAEs6UInt16V_tcfC")]
            private static extern void PInvoke_init_3A20D625( SwiftIndirectResult swiftIndirectResult,  System.UInt16 rawValue);
            
            
        }
        
        
        public unsafe class UserInfoKey : ISwiftObject, IEquatable<UserInfoKey>
        {
            private unsafe Swift.SwiftString RawValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_rawValue_Get_178DBCB4(self);
                    
                    unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV11UserInfoKeyV8rawValueSSvg")]
            private static extern Swift.SwiftString.Buffer PInvoke_rawValue_Get_178DBCB4( SwiftSelf self);
            
            public string RawValue
            {
                get => RawValue_Get().ToString();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.UserInfoKey ImageIdKey_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.UserInfoKey>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_imageIdKey_Get_53908599(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.UserInfoKey>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV11UserInfoKeyV07imageIdF0AEvgZ")]
            private static extern void PInvoke_imageIdKey_Get_53908599( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.UserInfoKey ImageIdKey
            {
                get => ImageIdKey_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.UserInfoKey ScaleKey_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.UserInfoKey>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_scaleKey_Get_0C6C6E3F(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.UserInfoKey>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV11UserInfoKeyV05scaleF0AEvgZ")]
            private static extern void PInvoke_scaleKey_Get_0C6C6E3F( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.UserInfoKey ScaleKey
            {
                get => ScaleKey_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.UserInfoKey ThumbnailKey_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.UserInfoKey>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_thumbnailKey_Get_048CB9B1(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.UserInfoKey>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV11UserInfoKeyV09thumbnailF0AEvgZ")]
            private static extern void PInvoke_thumbnailKey_Get_048CB9B1( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.UserInfoKey ThumbnailKey
            {
                get => ThumbnailKey_Get();
            }
            
            private unsafe nint HashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_7D7C0C3F(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV11UserInfoKeyV9hashValueSivg")]
            private static extern nint PInvoke_hashValue_Get_7D7C0C3F( SwiftSelf self);
            
            public nint HashValue
            {
                get => HashValue_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<UserInfoKey>.GetTypeMetadata().Size;
            SwiftSafeHandle<UserInfoKey> _payload = SwiftSafeHandle<UserInfoKey>.Zero;
            
            internal SwiftSafeHandle<UserInfoKey> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            public static System.Boolean operator ==(Swift.Nuke.ImageRequest.UserInfoKey arg0, Swift.Nuke.ImageRequest.UserInfoKey arg1)
            {
                if (arg0 is null) return arg1 is null;
                if (arg1 is null) return false;
                return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
            }
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV11UserInfoKeyV2eeoiySbAE_AEtFZ")]
            private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
            
            public static bool operator !=(UserInfoKey left, UserInfoKey right)
            {
                if (left is null) return right is not null;
                if (right is null) return true;
                return !(left == right);
            }
            
            public override bool Equals(object? obj)
            {
                return obj is UserInfoKey other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            public override int GetHashCode()
            {
                // TODO: Implement when Swift Hashable protocol binding is supported.
                // Returning constant 0 satisfies the Equals/GetHashCode contract
                // (equal objects must have equal hashes). This is correct but makes
                // hash-based collections O(n) until Hashable is supported.
                return 0;
            }
            
            public bool Equals(UserInfoKey? other)
            {
                return Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV11UserInfoKeyVMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new UserInfoKey(handle);
            }
            
            UserInfoKey(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<UserInfoKey>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<UserInfoKey>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static UserInfoKey()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IEquatable<UserInfoKey>), "$s4Nuke12ImageRequestV11UserInfoKeyVSQAAMc"}
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type UserInfoKey and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
            
            public unsafe UserInfoKey( string arg0)
            {
                _payload = new SwiftSafeHandle<UserInfoKey>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                using var arg0Swift = new SwiftString(arg0);
                using PayloadBuffer<SwiftString.Buffer> arg0Disposable = arg0Swift.PayloadBuffer;
                PInvoke_init_55E9B1FE(swiftIndirectResult, arg0Disposable.Buffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV11UserInfoKeyVyAESScfC")]
            private static extern void PInvoke_init_55E9B1FE( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer arg0);
            
            
            public unsafe void Hash(ref Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_13E8D39B(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV11UserInfoKeyV4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_13E8D39B( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        public unsafe class ThumbnailOptions : ISwiftObject, IEquatable<ThumbnailOptions>
        {
            private unsafe System.Boolean CreateThumbnailFromImageIfAbsent_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_createThumbnailFromImageIfAbsent_Get_25CE7FE2(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV16ThumbnailOptionsV06created4FromB8IfAbsentSbvg")]
            private static extern System.Boolean PInvoke_createThumbnailFromImageIfAbsent_Get_25CE7FE2( SwiftSelf self);
            
            private unsafe void CreateThumbnailFromImageIfAbsent_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_createThumbnailFromImageIfAbsent_Set_4DF1E8E2(value, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV16ThumbnailOptionsV06created4FromB8IfAbsentSbvs")]
            private static extern void PInvoke_createThumbnailFromImageIfAbsent_Set_4DF1E8E2( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean CreateThumbnailFromImageIfAbsent
            {
                get => CreateThumbnailFromImageIfAbsent_Get();
                set => CreateThumbnailFromImageIfAbsent_Set(value);
            }
            
            private unsafe System.Boolean CreateThumbnailFromImageAlways_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_createThumbnailFromImageAlways_Get_62848793(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV16ThumbnailOptionsV06created4FromB6AlwaysSbvg")]
            private static extern System.Boolean PInvoke_createThumbnailFromImageAlways_Get_62848793( SwiftSelf self);
            
            private unsafe void CreateThumbnailFromImageAlways_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_createThumbnailFromImageAlways_Set_0CB6C9F2(value, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV16ThumbnailOptionsV06created4FromB6AlwaysSbvs")]
            private static extern void PInvoke_createThumbnailFromImageAlways_Set_0CB6C9F2( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean CreateThumbnailFromImageAlways
            {
                get => CreateThumbnailFromImageAlways_Get();
                set => CreateThumbnailFromImageAlways_Set(value);
            }
            
            private unsafe System.Boolean CreateThumbnailWithTransform_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_createThumbnailWithTransform_Get_11CB290F(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV16ThumbnailOptionsV06createD13WithTransformSbvg")]
            private static extern System.Boolean PInvoke_createThumbnailWithTransform_Get_11CB290F( SwiftSelf self);
            
            private unsafe void CreateThumbnailWithTransform_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_createThumbnailWithTransform_Set_74AB8EF2(value, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV16ThumbnailOptionsV06createD13WithTransformSbvs")]
            private static extern void PInvoke_createThumbnailWithTransform_Set_74AB8EF2( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean CreateThumbnailWithTransform
            {
                get => CreateThumbnailWithTransform_Get();
                set => CreateThumbnailWithTransform_Set(value);
            }
            
            private unsafe System.Boolean ShouldCacheImmediately_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_shouldCacheImmediately_Get_153994AE(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV16ThumbnailOptionsV22shouldCacheImmediatelySbvg")]
            private static extern System.Boolean PInvoke_shouldCacheImmediately_Get_153994AE( SwiftSelf self);
            
            private unsafe void ShouldCacheImmediately_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_shouldCacheImmediately_Set_79953D65(value, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV16ThumbnailOptionsV22shouldCacheImmediatelySbvs")]
            private static extern void PInvoke_shouldCacheImmediately_Set_79953D65( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean ShouldCacheImmediately
            {
                get => ShouldCacheImmediately_Get();
                set => ShouldCacheImmediately_Set(value);
            }
            
            private unsafe nint HashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_4A040EB9(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV16ThumbnailOptionsV9hashValueSivg")]
            private static extern nint PInvoke_hashValue_Get_4A040EB9( SwiftSelf self);
            
            public nint HashValue
            {
                get => HashValue_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<ThumbnailOptions>.GetTypeMetadata().Size;
            SwiftSafeHandle<ThumbnailOptions> _payload = SwiftSafeHandle<ThumbnailOptions>.Zero;
            
            internal SwiftSafeHandle<ThumbnailOptions> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            public static System.Boolean operator ==(Swift.Nuke.ImageRequest.ThumbnailOptions arg0, Swift.Nuke.ImageRequest.ThumbnailOptions arg1)
            {
                if (arg0 is null) return arg1 is null;
                if (arg1 is null) return false;
                return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
            }
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV16ThumbnailOptionsV2eeoiySbAE_AEtFZ")]
            private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
            
            public static bool operator !=(ThumbnailOptions left, ThumbnailOptions right)
            {
                if (left is null) return right is not null;
                if (right is null) return true;
                return !(left == right);
            }
            
            public override bool Equals(object? obj)
            {
                return obj is ThumbnailOptions other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            public override int GetHashCode()
            {
                // TODO: Implement when Swift Hashable protocol binding is supported.
                // Returning constant 0 satisfies the Equals/GetHashCode contract
                // (equal objects must have equal hashes). This is correct but makes
                // hash-based collections O(n) until Hashable is supported.
                return 0;
            }
            
            public bool Equals(ThumbnailOptions? other)
            {
                return Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV16ThumbnailOptionsVMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new ThumbnailOptions(handle);
            }
            
            ThumbnailOptions(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<ThumbnailOptions>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<ThumbnailOptions>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static ThumbnailOptions()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IEquatable<ThumbnailOptions>), "$s4Nuke12ImageRequestV16ThumbnailOptionsVSQAAMc"}
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type ThumbnailOptions and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
            
            public unsafe ThumbnailOptions( System.Single maxPixelSize)
            {
                _payload = new SwiftSafeHandle<ThumbnailOptions>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_65E7637F(swiftIndirectResult, maxPixelSize);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV16ThumbnailOptionsV12maxPixelSizeAESf_tcfC")]
            private static extern void PInvoke_init_65E7637F( SwiftIndirectResult swiftIndirectResult,  System.Single maxPixelSize);
            
            
            public unsafe ThumbnailOptions( Swift.CGSize size,  Swift.Nuke.ImageProcessingOptions.Unit unit,  Swift.Nuke.ImageProcessingOptions.ContentMode contentMode)
            {
                _payload = new SwiftSafeHandle<ThumbnailOptions>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_2E794388(swiftIndirectResult, size, unit.Payload.DangerousGetHandle(), contentMode.Payload.DangerousGetHandle());
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV16ThumbnailOptionsV4size4unit11contentModeAESo6CGSizeV_AA0b10ProcessingE0O4UnitOAL07ContentI0OtcfC")]
            private static extern void PInvoke_init_2E794388( SwiftIndirectResult swiftIndirectResult,  Swift.CGSize size,  IntPtr unit,  IntPtr contentMode);
            public unsafe ThumbnailOptions( Swift.CGSize size,  Swift.Nuke.ImageProcessingOptions.Unit unit)
            {
                _payload = new SwiftSafeHandle<ThumbnailOptions>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_4576E56B(swiftIndirectResult, size, unit.Payload.DangerousGetHandle());
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("SwiftBindings", EntryPoint = "DBW_ThumbnailOptions_init_3D39AF54_1")]
            private static extern void PInvoke_init_4576E56B( SwiftIndirectResult swiftIndirectResult,  Swift.CGSize size,  IntPtr unit);
            
            
            public unsafe UIKit.UIImage? MakeThumbnail( Foundation.NSData with)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    var withSwift = Swift.Data.FromNSData(with);
                    
                    var result = PInvoke_makeThumbnail_06F3D9CA(withSwift, self);
                    
                    var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<UIKit.UIImage>>(new IntPtr(&result));
                    return swiftResult.ToNullable();
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV16ThumbnailOptionsV04makeD04withSo7UIImageCSg10Foundation4DataV_tF")]
            private static extern IntPtr PInvoke_makeThumbnail_06F3D9CA( Swift.Data with,  SwiftSelf self);
            
            
            public unsafe void Hash(ref Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_2816FF14(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV16ThumbnailOptionsV4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_2816FF14( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        public unsafe ImageRequest( string stringLiteral)
        {
            _payload = new SwiftSafeHandle<ImageRequest>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            using var stringLiteralSwift = new SwiftString(stringLiteral);
            using PayloadBuffer<SwiftString.Buffer> stringLiteralDisposable = stringLiteralSwift.PayloadBuffer;
            PInvoke_init_1EBB4A44(swiftIndirectResult, stringLiteralDisposable.Buffer);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV13stringLiteralACSS_tcfC")]
        private static extern void PInvoke_init_1EBB4A44( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer stringLiteral);
        
        
        
        
        
    }
    
    
    public unsafe class ImageDecoders : ISwiftObject
    {
        static nuint _payloadSize = SwiftObjectHelper<ImageDecoders>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageDecoders> _payload = SwiftSafeHandle<ImageDecoders>.Zero;
        internal SwiftSafeHandle<ImageDecoders> Payload => _payload;
        
        public void Dispose() => _payload.Dispose();
        
        static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersOMa")]
        internal static extern TypeMetadata PInvoke_getMetadata();
        
        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
        {
            return new ImageDecoders(handle);
        }
        
        ImageDecoders()
        {
        }
        
        ImageDecoders(SwiftHandle handle)
        {
            _payload = new SwiftSafeHandle<ImageDecoders>(handle);
        }
        
        unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            var metadata = SwiftObjectHelper<ImageDecoders>.GetTypeMetadata();
            if ((int)metadata.Size > swiftDestSpan.Length)
            {
                throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
            }
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure that the instance is valid before making copy
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
        
        private static Dictionary<Type, string> _protocolConformanceSymbols;
        static ImageDecoders()
        {
            _protocolConformanceSymbols = new Dictionary<Type, string>
            {
                
            };
        }
        
        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
            where TProtocol : class
        {
            if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
            {
                throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type ImageDecoders and protocol {typeof(TProtocol).Name}, but no conformance was found.");
            }
            return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
        }
        
        public unsafe class Empty : ISwiftObject, IImageDecoding
        {
            private unsafe System.Boolean IsProgressive_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isProgressive_Get_0A4EED27(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO5EmptyV13isProgressiveSbvg")]
            private static extern System.Boolean PInvoke_isProgressive_Get_0A4EED27( SwiftSelf self);
            
            public System.Boolean IsProgressive
            {
                get => IsProgressive_Get();
            }
            
            private unsafe System.Boolean IsAsynchronous_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isAsynchronous_Get_3F05FC2E(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO5EmptyV14isAsynchronousSbvg")]
            private static extern System.Boolean PInvoke_isAsynchronous_Get_3F05FC2E( SwiftSelf self);
            
            public System.Boolean IsAsynchronous
            {
                get => IsAsynchronous_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<Empty>.GetTypeMetadata().Size;
            SwiftSafeHandle<Empty> _payload = SwiftSafeHandle<Empty>.Zero;
            
            internal SwiftSafeHandle<Empty> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO5EmptyVMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new Empty(handle);
            }
            
            Empty(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<Empty>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<Empty>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static Empty()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IImageDecoding), "$s4Nuke13ImageDecodersO5EmptyVAA0B8DecodingAAMc"}
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type Empty and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
            
            public unsafe Empty( Swift.Nuke.AssetType? assetType,  System.Boolean isProgressive)
            {
                _payload = new SwiftSafeHandle<Empty>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                using var assetTypeSwift = assetType is {} assetTypeValue ? SwiftOptional<Swift.Nuke.AssetType>.NewSome(assetTypeValue) : SwiftOptional<Swift.Nuke.AssetType>.NewNone();
                using PayloadBuffer<IntPtr> assetTypeDisposable = assetTypeSwift.PayloadBuffer;
                IntPtr assetTypeBuffer = assetTypeDisposable.Buffer;
                PInvoke_init_23BA4212(swiftIndirectResult, assetTypeBuffer, isProgressive);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO5EmptyV9assetType13isProgressiveAeA05AssetF0VSg_SbtcfC")]
            private static extern void PInvoke_init_23BA4212( SwiftIndirectResult swiftIndirectResult,  IntPtr assetTypeBuffer,  System.Boolean isProgressive);
            public unsafe Empty()
            {
                _payload = new SwiftSafeHandle<Empty>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_6E59D481(swiftIndirectResult);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("SwiftBindings", EntryPoint = "DBW_Empty_init_B1B9E014_2")]
            private static extern void PInvoke_init_6E59D481( SwiftIndirectResult swiftIndirectResult);
            public unsafe Empty( Swift.Nuke.AssetType? assetType)
            {
                _payload = new SwiftSafeHandle<Empty>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                using var assetTypeSwift = assetType is {} assetTypeValue ? SwiftOptional<Swift.Nuke.AssetType>.NewSome(assetTypeValue) : SwiftOptional<Swift.Nuke.AssetType>.NewNone();
                using PayloadBuffer<IntPtr> assetTypeDisposable = assetTypeSwift.PayloadBuffer;
                IntPtr assetTypeBuffer = assetTypeDisposable.Buffer;
                PInvoke_init_25E6D5B1(swiftIndirectResult, assetTypeBuffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("SwiftBindings", EntryPoint = "DBW_Empty_init_B1B9E014_1")]
            private static extern void PInvoke_init_25E6D5B1( SwiftIndirectResult swiftIndirectResult,  IntPtr assetTypeBuffer);
            
            
            public unsafe Swift.Nuke.ImageContainer Decode( Foundation.NSData arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageContainer>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    var arg0Swift = Swift.Data.FromNSData(arg0);
                    
                    PInvoke_decode_7E611E83(swiftIndirectResult, arg0Swift, self, out var error);
                    
                    if (error.Value != null)
                    {
                        throw new SwiftRuntimeException("Call to Swift method decode failed.");
                    }
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageContainer>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO5EmptyV6decodeyAA0B9ContainerV10Foundation4DataVKF")]
            private static extern void PInvoke_decode_7E611E83( SwiftIndirectResult swiftIndirectResult,  Swift.Data arg0,  SwiftSelf self, out SwiftError error);
            
            
            public unsafe Swift.Nuke.ImageContainer? DecodePartiallyDownloadedData( Foundation.NSData arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    var arg0Swift = Swift.Data.FromNSData(arg0);
                    
                    var result = PInvoke_decodePartiallyDownloadedData_2EE58784(arg0Swift, self);
                    
                    var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Nuke.ImageContainer>>(new IntPtr(&result));
                    return swiftResult.ToNullable();
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO5EmptyV29decodePartiallyDownloadedDatayAA0B9ContainerVSg10Foundation0H0VF")]
            private static extern IntPtr PInvoke_decodePartiallyDownloadedData_2EE58784( Swift.Data arg0,  SwiftSelf self);
            
            
        }
        
        
        public unsafe class Default : ISwiftObject, IImageDecoding
        {
            private unsafe System.Boolean IsAsynchronous_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isAsynchronous_Get_143A653E(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO7DefaultC14isAsynchronousSbvg")]
            private static extern System.Boolean PInvoke_isAsynchronous_Get_143A653E( SwiftSelf self);
            
            public System.Boolean IsAsynchronous
            {
                get => IsAsynchronous_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<Default>.GetTypeMetadata().Size;
            SwiftSafeHandle<Default> _payload = SwiftSafeHandle<Default>.Zero;
            
            internal SwiftSafeHandle<Default> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO7DefaultCMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new Default(handle);
            }
            
            Default(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<Default>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<Default>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static Default()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IImageDecoding), "$s4Nuke13ImageDecodersO7DefaultCAA0B8DecodingAAMc"}
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type Default and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
            public unsafe Default()
            {
                _payload = new SwiftSafeHandle<Default>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_79BBCA22(swiftIndirectResult);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO7DefaultCAEycfC")]
            private static extern void PInvoke_init_79BBCA22( SwiftIndirectResult swiftIndirectResult);
            
            
            public static unsafe bool TryCreate( Swift.Nuke.ImageDecodingContext context, out Default result)
            {
                var selfMetadata = TypeMetadata.GetTypeMetadataOrThrow<Default>();
                
                var optionalMetadata = PInvokesForSwiftOptional_MetadataAccessor(
                    TypeMetadataRequest.Complete, selfMetadata);
                
                void* resultBuffer = NativeMemory.AllocZeroed(optionalMetadata.Size);
                try
                {
                    var swiftIndirectResult = new SwiftIndirectResult(resultBuffer);
                    
                    
                    
                    PInvoke_init_02C85483(swiftIndirectResult, context.Payload);
                    
                    uint tag = optionalMetadata.ValueWitnessTable->GetEnumTag((byte*)resultBuffer, optionalMetadata);
                    
                    if (tag == 1) // None
                    {
                        result = default;
                        return false;
                    }
                    
                    IntPtr payloadBuffer = (IntPtr)NativeMemory.Alloc(selfMetadata.Size);
                    selfMetadata.ValueWitnessTable->InitializeWithCopy((void*)payloadBuffer, resultBuffer, selfMetadata);
                    result = new Default(payloadBuffer);
                    return true;
                }
                finally
                {
                    optionalMetadata.ValueWitnessTable->Destroy(resultBuffer, optionalMetadata);
                    NativeMemory.Free(resultBuffer);
                }
            }
            
            [DllImport("/usr/lib/swift/libswiftCore.dylib", EntryPoint = "$sSqMa")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern TypeMetadata PInvokesForSwiftOptional_MetadataAccessor(TypeMetadataRequest request, TypeMetadata typeMetadata);
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO7DefaultC7contextAESgAA0B15DecodingContextV_tcfC")]
            private static extern void PInvoke_init_02C85483( SwiftIndirectResult swiftIndirectResult,  SafeHandle context);
            
            
            public unsafe Swift.Nuke.ImageContainer Decode( Foundation.NSData arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageContainer>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    var arg0Swift = Swift.Data.FromNSData(arg0);
                    
                    PInvoke_decode_2A8BF3F2(swiftIndirectResult, arg0Swift, self, out var error);
                    
                    if (error.Value != null)
                    {
                        throw new SwiftRuntimeException("Call to Swift method decode failed.");
                    }
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageContainer>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO7DefaultC6decodeyAA0B9ContainerV10Foundation4DataVKF")]
            private static extern void PInvoke_decode_2A8BF3F2( SwiftIndirectResult swiftIndirectResult,  Swift.Data arg0,  SwiftSelf self, out SwiftError error);
            
            
            public unsafe Swift.Nuke.ImageContainer? DecodePartiallyDownloadedData( Foundation.NSData arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                    
                    
                    var arg0Swift = Swift.Data.FromNSData(arg0);
                    
                    var result = PInvoke_decodePartiallyDownloadedData_0998A854(arg0Swift, self);
                    
                    var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Nuke.ImageContainer>>(new IntPtr(&result));
                    return swiftResult.ToNullable();
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO7DefaultC29decodePartiallyDownloadedDatayAA0B9ContainerVSg10Foundation0H0VF")]
            private static extern IntPtr PInvoke_decodePartiallyDownloadedData_0998A854( Swift.Data arg0,  SwiftSelf self);
            
            
        }
        
        
    }
    
    
    public unsafe class AssetType : ISwiftObject, IEquatable<AssetType>
    {
        private unsafe Swift.SwiftString RawValue_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_rawValue_Get_68969715(self);
                
                unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV8rawValueSSvg")]
        private static extern Swift.SwiftString.Buffer PInvoke_rawValue_Get_68969715( SwiftSelf self);
        
        public string RawValue
        {
            get => RawValue_Get().ToString();
        }
        
        private static unsafe Swift.Nuke.AssetType Png_Get()
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.AssetType>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_png_Get_31EBBFBC(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV3pngACvgZ")]
        private static extern void PInvoke_png_Get_31EBBFBC( SwiftIndirectResult swiftIndirectResult);
        
        public static Swift.Nuke.AssetType Png
        {
            get => Png_Get();
        }
        
        private static unsafe Swift.Nuke.AssetType Jpeg_Get()
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.AssetType>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_jpeg_Get_51876EDA(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV4jpegACvgZ")]
        private static extern void PInvoke_jpeg_Get_51876EDA( SwiftIndirectResult swiftIndirectResult);
        
        public static Swift.Nuke.AssetType Jpeg
        {
            get => Jpeg_Get();
        }
        
        private static unsafe Swift.Nuke.AssetType Gif_Get()
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.AssetType>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_gif_Get_0094603E(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV3gifACvgZ")]
        private static extern void PInvoke_gif_Get_0094603E( SwiftIndirectResult swiftIndirectResult);
        
        public static Swift.Nuke.AssetType Gif
        {
            get => Gif_Get();
        }
        
        private static unsafe Swift.Nuke.AssetType Heic_Get()
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.AssetType>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_heic_Get_5479C3C2(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV4heicACvgZ")]
        private static extern void PInvoke_heic_Get_5479C3C2( SwiftIndirectResult swiftIndirectResult);
        
        public static Swift.Nuke.AssetType Heic
        {
            get => Heic_Get();
        }
        
        private static unsafe Swift.Nuke.AssetType Webp_Get()
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.AssetType>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_webp_Get_2C5F73AA(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV4webpACvgZ")]
        private static extern void PInvoke_webp_Get_2C5F73AA( SwiftIndirectResult swiftIndirectResult);
        
        public static Swift.Nuke.AssetType Webp
        {
            get => Webp_Get();
        }
        
        private static unsafe Swift.Nuke.AssetType Mp4_Get()
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.AssetType>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_mp4_Get_0EEA5A9C(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV3mp4ACvgZ")]
        private static extern void PInvoke_mp4_Get_0EEA5A9C( SwiftIndirectResult swiftIndirectResult);
        
        public static Swift.Nuke.AssetType Mp4
        {
            get => Mp4_Get();
        }
        
        private static unsafe Swift.Nuke.AssetType M4v_Get()
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.AssetType>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_m4v_Get_5DB0691F(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV3m4vACvgZ")]
        private static extern void PInvoke_m4v_Get_5DB0691F( SwiftIndirectResult swiftIndirectResult);
        
        public static Swift.Nuke.AssetType M4v
        {
            get => M4v_Get();
        }
        
        private static unsafe Swift.Nuke.AssetType Mov_Get()
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.AssetType>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_mov_Get_11C7496C(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV3movACvgZ")]
        private static extern void PInvoke_mov_Get_11C7496C( SwiftIndirectResult swiftIndirectResult);
        
        public static Swift.Nuke.AssetType Mov
        {
            get => Mov_Get();
        }
        
        private unsafe nint HashValue_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_hashValue_Get_3C4692B7(self);
                
                return result;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV9hashValueSivg")]
        private static extern nint PInvoke_hashValue_Get_3C4692B7( SwiftSelf self);
        
        public nint HashValue
        {
            get => HashValue_Get();
        }
        
        static nuint _payloadSize = SwiftObjectHelper<AssetType>.GetTypeMetadata().Size;
        SwiftSafeHandle<AssetType> _payload = SwiftSafeHandle<AssetType>.Zero;
        
        internal SwiftSafeHandle<AssetType> Payload => _payload;
        
        public void Dispose() => _payload.Dispose();
        
        public static System.Boolean operator ==(Swift.Nuke.AssetType arg0, Swift.Nuke.AssetType arg1)
        {
            if (arg0 is null) return arg1 is null;
            if (arg1 is null) return false;
            return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
        }
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV2eeoiySbAC_ACtFZ")]
        private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
        
        public static bool operator !=(AssetType left, AssetType right)
        {
            if (left is null) return right is not null;
            if (right is null) return true;
            return !(left == right);
        }
        
        public override bool Equals(object? obj)
        {
            return obj is AssetType other && Swift.Runtime.SwiftEquatable.Equals(this, other);
        }
        public override int GetHashCode()
        {
            // TODO: Implement when Swift Hashable protocol binding is supported.
            // Returning constant 0 satisfies the Equals/GetHashCode contract
            // (equal objects must have equal hashes). This is correct but makes
            // hash-based collections O(n) until Hashable is supported.
            return 0;
        }
        
        public bool Equals(AssetType? other)
        {
            return Swift.Runtime.SwiftEquatable.Equals(this, other);
        }
        
        static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeVMa")]
        internal static extern TypeMetadata PInvoke_getMetadata();
        
        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
        {
            return new AssetType(handle);
        }
        
        AssetType(SwiftHandle handle)
        {
            _payload = new SwiftSafeHandle<AssetType>(handle);
        }
        
        unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            var metadata = SwiftObjectHelper<AssetType>.GetTypeMetadata();
            if ((int)metadata.Size > swiftDestSpan.Length)
            {
                throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
            }
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure that the instance is valid before making copy
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
        
        private static Dictionary<Type, string> _protocolConformanceSymbols;
        static AssetType()
        {
            _protocolConformanceSymbols = new Dictionary<Type, string>
            {
                {typeof(IEquatable<AssetType>), "$s4Nuke9AssetTypeVSQAAMc"}
            };
        }
        
        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
            where TProtocol : class
        {
            if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
            {
                throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type AssetType and protocol {typeof(TProtocol).Name}, but no conformance was found.");
            }
            return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
        }
        
        
        public unsafe AssetType( string rawValue)
        {
            _payload = new SwiftSafeHandle<AssetType>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            using var rawValueSwift = new SwiftString(rawValue);
            using PayloadBuffer<SwiftString.Buffer> rawValueDisposable = rawValueSwift.PayloadBuffer;
            PInvoke_init_243C44BC(swiftIndirectResult, rawValueDisposable.Buffer);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV8rawValueACSS_tcfC")]
        private static extern void PInvoke_init_243C44BC( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer rawValue);
        
        
        public unsafe void Hash(ref Swift.Hasher into)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_hash_1F63DB57(into.Payload, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV4hash4intoys6HasherVz_tF")]
        private static extern void PInvoke_hash_1F63DB57( SafeHandle into,  SwiftSelf self);
        
        
        public static unsafe bool TryCreate( Foundation.NSData arg0, out AssetType result)
        {
            var selfMetadata = TypeMetadata.GetTypeMetadataOrThrow<AssetType>();
            
            var optionalMetadata = PInvokesForSwiftOptional_MetadataAccessor(
                TypeMetadataRequest.Complete, selfMetadata);
            
            void* resultBuffer = NativeMemory.AllocZeroed(optionalMetadata.Size);
            try
            {
                var swiftIndirectResult = new SwiftIndirectResult(resultBuffer);
                
                
                var arg0Swift = Swift.Data.FromNSData(arg0);
                
                PInvoke_init_18F0FB88(swiftIndirectResult, arg0Swift);
                
                uint tag = optionalMetadata.ValueWitnessTable->GetEnumTag((byte*)resultBuffer, optionalMetadata);
                
                if (tag == 1) // None
                {
                    result = default;
                    return false;
                }
                
                IntPtr payloadBuffer = (IntPtr)NativeMemory.Alloc(selfMetadata.Size);
                selfMetadata.ValueWitnessTable->InitializeWithCopy((void*)payloadBuffer, resultBuffer, selfMetadata);
                result = new AssetType(payloadBuffer);
                return true;
            }
            finally
            {
                optionalMetadata.ValueWitnessTable->Destroy(resultBuffer, optionalMetadata);
                NativeMemory.Free(resultBuffer);
            }
        }
        
        [DllImport("/usr/lib/swift/libswiftCore.dylib", EntryPoint = "$sSqMa")]
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        private static extern TypeMetadata PInvokesForSwiftOptional_MetadataAccessor(TypeMetadataRequest request, TypeMetadata typeMetadata);
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeVyACSg10Foundation4DataVcfC")]
        private static extern void PInvoke_init_18F0FB88( SwiftIndirectResult swiftIndirectResult,  Swift.Data arg0);
        
        
    }
    
    
    public unsafe class ImageTask : ISwiftObject, IEquatable<ImageTask>
    {
        private unsafe System.Int64 TaskId_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_taskId_Get_5F1D9146(self);
                
                return result;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC6taskIds5Int64Vvg")]
        private static extern System.Int64 PInvoke_taskId_Get_5F1D9146( SwiftSelf self);
        
        public System.Int64 TaskId
        {
            get => TaskId_Get();
        }
        
        private unsafe Swift.Nuke.ImageRequest Request_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_request_Get_5848402B(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC7requestAA0B7RequestVvg")]
        private static extern void PInvoke_request_Get_5848402B( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Swift.Nuke.ImageRequest Request
        {
            get => Request_Get();
        }
        
        private unsafe Swift.Nuke.ImageRequest.PriorityInfo Priority_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.PriorityInfo>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_priority_Get_269BA429(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.PriorityInfo>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC8priorityAA0B7RequestV8PriorityOvg")]
        private static extern void PInvoke_priority_Get_269BA429( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Priority_Set( Swift.Nuke.ImageRequest.PriorityInfo value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_priority_Set_19CA64C6(value.Payload.DangerousGetHandle(), self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC8priorityAA0B7RequestV8PriorityOvs")]
        private static extern void PInvoke_priority_Set_19CA64C6( IntPtr value,  SwiftSelf self);
        
        public Swift.Nuke.ImageRequest.PriorityInfo Priority
        {
            get => Priority_Get();
            set => Priority_Set(value);
        }
        
        private unsafe Swift.Nuke.ImageTask.ProgressInfo CurrentProgress_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageTask.ProgressInfo>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_currentProgress_Get_64DED17E(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageTask.ProgressInfo>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC15currentProgressAC0E0Vvg")]
        private static extern void PInvoke_currentProgress_Get_64DED17E( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Swift.Nuke.ImageTask.ProgressInfo CurrentProgress
        {
            get => CurrentProgress_Get();
        }
        
        private unsafe Swift.Nuke.ImageTask.StateInfo State_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageTask.StateInfo>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_state_Get_346CDD5F(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageTask.StateInfo>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5stateAC5StateOvg")]
        private static extern void PInvoke_state_Get_346CDD5F( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Swift.Nuke.ImageTask.StateInfo State
        {
            get => State_Get();
        }
        
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static unsafe byte progress_AsyncStream_OnElement(void* elementPtr, long context)
        {
            var stream = SwiftAsyncStream<Swift.Nuke.ImageTask.ProgressInfo>.FromContext(context);
            if (stream == null) return 0;
            var element = SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageTask.ProgressInfo>(new IntPtr(elementPtr));
            return stream.GetElementCallback()(new IntPtr(elementPtr), context) ? (byte)1 : (byte)0;
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void progress_AsyncStream_OnComplete(long context)
        {
            // Stream completion is handled by the SwiftAsyncStream instance
        }
        
        [DllImport("Nuke", EntryPoint = "ImageTask_progress_AsyncStream")]
        private static extern unsafe void PInvoke_ImageTask_progress_AsyncStream(
            void* self, delegate* unmanaged[Cdecl]<void*, long, byte> elementCallback,
            delegate* unmanaged[Cdecl]<long, void> completionCallback,
            long context);
        
        public IAsyncEnumerable<Swift.Nuke.ImageTask.ProgressInfo> Progress
        {
            get
            {
                unsafe
                {
                    var stream = new SwiftAsyncStream<Swift.Nuke.ImageTask.ProgressInfo>();
                    PInvoke_ImageTask_progress_AsyncStream(
                        (void*)_payload.DangerousGetHandle(), &progress_AsyncStream_OnElement,
                        &progress_AsyncStream_OnComplete,
                        stream.GetContext());
                    return stream;
                }
            }
        }
        
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static unsafe byte previews_AsyncStream_OnElement(void* elementPtr, long context)
        {
            var stream = SwiftAsyncStream<Swift.Nuke.ImageResponse>.FromContext(context);
            if (stream == null) return 0;
            var element = SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageResponse>(new IntPtr(elementPtr));
            return stream.GetElementCallback()(new IntPtr(elementPtr), context) ? (byte)1 : (byte)0;
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void previews_AsyncStream_OnComplete(long context)
        {
            // Stream completion is handled by the SwiftAsyncStream instance
        }
        
        [DllImport("Nuke", EntryPoint = "ImageTask_previews_AsyncStream")]
        private static extern unsafe void PInvoke_ImageTask_previews_AsyncStream(
            void* self, delegate* unmanaged[Cdecl]<void*, long, byte> elementCallback,
            delegate* unmanaged[Cdecl]<long, void> completionCallback,
            long context);
        
        public IAsyncEnumerable<Swift.Nuke.ImageResponse> Previews
        {
            get
            {
                unsafe
                {
                    var stream = new SwiftAsyncStream<Swift.Nuke.ImageResponse>();
                    PInvoke_ImageTask_previews_AsyncStream(
                        (void*)_payload.DangerousGetHandle(), &previews_AsyncStream_OnElement,
                        &previews_AsyncStream_OnComplete,
                        stream.GetContext());
                    return stream;
                }
            }
        }
        
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static unsafe byte events_AsyncStream_OnElement(void* elementPtr, long context)
        {
            var stream = SwiftAsyncStream<Swift.Nuke.ImageTask.Event>.FromContext(context);
            if (stream == null) return 0;
            var element = SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageTask.Event>(new IntPtr(elementPtr));
            return stream.GetElementCallback()(new IntPtr(elementPtr), context) ? (byte)1 : (byte)0;
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void events_AsyncStream_OnComplete(long context)
        {
            // Stream completion is handled by the SwiftAsyncStream instance
        }
        
        [DllImport("Nuke", EntryPoint = "ImageTask_events_AsyncStream")]
        private static extern unsafe void PInvoke_ImageTask_events_AsyncStream(
            void* self, delegate* unmanaged[Cdecl]<void*, long, byte> elementCallback,
            delegate* unmanaged[Cdecl]<long, void> completionCallback,
            long context);
        
        public IAsyncEnumerable<Swift.Nuke.ImageTask.Event> Events
        {
            get
            {
                unsafe
                {
                    var stream = new SwiftAsyncStream<Swift.Nuke.ImageTask.Event>();
                    PInvoke_ImageTask_events_AsyncStream(
                        (void*)_payload.DangerousGetHandle(), &events_AsyncStream_OnElement,
                        &events_AsyncStream_OnComplete,
                        stream.GetContext());
                    return stream;
                }
            }
        }
        
        private unsafe Swift.SwiftString Description_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_description_Get_061A3BCF(self);
                
                unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC11descriptionSSvg")]
        private static extern Swift.SwiftString.Buffer PInvoke_description_Get_061A3BCF( SwiftSelf self);
        
        public string Description
        {
            get => Description_Get().ToString();
        }
        
        private unsafe nint HashValue_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_hashValue_Get_7AC78140(self);
                
                return result;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC9hashValueSivg")]
        private static extern nint PInvoke_hashValue_Get_7AC78140( SwiftSelf self);
        
        public nint HashValue
        {
            get => HashValue_Get();
        }
        
        static nuint _payloadSize = SwiftObjectHelper<ImageTask>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageTask> _payload = SwiftSafeHandle<ImageTask>.Zero;
        
        internal SwiftSafeHandle<ImageTask> Payload => _payload;
        
        public void Dispose() => _payload.Dispose();
        
        public static System.Boolean operator ==(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImageTask arg1)
        {
            if (arg0 is null) return arg1 is null;
            if (arg1 is null) return false;
            return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
        }
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC2eeoiySbAC_ACtFZ")]
        private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
        
        public static bool operator !=(ImageTask left, ImageTask right)
        {
            if (left is null) return right is not null;
            if (right is null) return true;
            return !(left == right);
        }
        
        public override bool Equals(object? obj)
        {
            return obj is ImageTask other && Swift.Runtime.SwiftEquatable.Equals(this, other);
        }
        public override int GetHashCode()
        {
            // TODO: Implement when Swift Hashable protocol binding is supported.
            // Returning constant 0 satisfies the Equals/GetHashCode contract
            // (equal objects must have equal hashes). This is correct but makes
            // hash-based collections O(n) until Hashable is supported.
            return 0;
        }
        
        public bool Equals(ImageTask? other)
        {
            return Swift.Runtime.SwiftEquatable.Equals(this, other);
        }
        
        static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskCMa")]
        internal static extern TypeMetadata PInvoke_getMetadata();
        
        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
        {
            return new ImageTask(handle);
        }
        
        ImageTask(SwiftHandle handle)
        {
            _payload = new SwiftSafeHandle<ImageTask>(handle);
        }
        
        unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            var metadata = SwiftObjectHelper<ImageTask>.GetTypeMetadata();
            if ((int)metadata.Size > swiftDestSpan.Length)
            {
                throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
            }
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure that the instance is valid before making copy
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
        
        private static Dictionary<Type, string> _protocolConformanceSymbols;
        static ImageTask()
        {
            _protocolConformanceSymbols = new Dictionary<Type, string>
            {
                {typeof(IEquatable<ImageTask>), "$s4Nuke9ImageTaskCSQAAMc"}
            };
        }
        
        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
            where TProtocol : class
        {
            if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
            {
                throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type ImageTask and protocol {typeof(TProtocol).Name}, but no conformance was found.");
            }
            return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
        }
        
        public unsafe class ProgressInfo : ISwiftObject, IEquatable<ProgressInfo>
        {
            private unsafe System.Int64 Completed_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_completed_Get_54DA5417(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC8ProgressV9completeds5Int64Vvg")]
            private static extern System.Int64 PInvoke_completed_Get_54DA5417( SwiftSelf self);
            
            public System.Int64 Completed
            {
                get => Completed_Get();
            }
            
            private unsafe System.Int64 Total_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_total_Get_5A47C898(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC8ProgressV5totals5Int64Vvg")]
            private static extern System.Int64 PInvoke_total_Get_5A47C898( SwiftSelf self);
            
            public System.Int64 Total
            {
                get => Total_Get();
            }
            
            private unsafe System.Single Fraction_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_fraction_Get_5436C3FD(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC8ProgressV8fractionSfvg")]
            private static extern System.Single PInvoke_fraction_Get_5436C3FD( SwiftSelf self);
            
            public System.Single Fraction
            {
                get => Fraction_Get();
            }
            
            private unsafe nint HashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_68E80372(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC8ProgressV9hashValueSivg")]
            private static extern nint PInvoke_hashValue_Get_68E80372( SwiftSelf self);
            
            public nint HashValue
            {
                get => HashValue_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<ProgressInfo>.GetTypeMetadata().Size;
            SwiftSafeHandle<ProgressInfo> _payload = SwiftSafeHandle<ProgressInfo>.Zero;
            
            internal SwiftSafeHandle<ProgressInfo> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            public static System.Boolean operator ==(Swift.Nuke.ImageTask.ProgressInfo arg0, Swift.Nuke.ImageTask.ProgressInfo arg1)
            {
                if (arg0 is null) return arg1 is null;
                if (arg1 is null) return false;
                return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
            }
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC8ProgressV2eeoiySbAE_AEtFZ")]
            private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
            
            public static bool operator !=(ProgressInfo left, ProgressInfo right)
            {
                if (left is null) return right is not null;
                if (right is null) return true;
                return !(left == right);
            }
            
            public override bool Equals(object? obj)
            {
                return obj is ProgressInfo other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            public override int GetHashCode()
            {
                // TODO: Implement when Swift Hashable protocol binding is supported.
                // Returning constant 0 satisfies the Equals/GetHashCode contract
                // (equal objects must have equal hashes). This is correct but makes
                // hash-based collections O(n) until Hashable is supported.
                return 0;
            }
            
            public bool Equals(ProgressInfo? other)
            {
                return Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC8ProgressVMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new ProgressInfo(handle);
            }
            
            ProgressInfo(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<ProgressInfo>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<ProgressInfo>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static ProgressInfo()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IEquatable<ProgressInfo>), "$s4Nuke9ImageTaskC8ProgressVSQAAMc"}
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type Progress and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
            
            public unsafe ProgressInfo( System.Int64 completed,  System.Int64 total)
            {
                _payload = new SwiftSafeHandle<ProgressInfo>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_60BF2E97(swiftIndirectResult, completed, total);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC8ProgressV9completed5totalAEs5Int64V_AItcfC")]
            private static extern void PInvoke_init_60BF2E97( SwiftIndirectResult swiftIndirectResult,  System.Int64 completed,  System.Int64 total);
            
            
            public unsafe void Hash(ref Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_4377A6F8(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC8ProgressV4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_4377A6F8( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        public unsafe class StateInfo : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<StateInfo>.GetTypeMetadata().Size;
            SwiftSafeHandle<StateInfo> _payload = SwiftSafeHandle<StateInfo>.Zero;
            internal SwiftSafeHandle<StateInfo> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            /// <summary>
            /// Gets the 'running' case of StateInfo.
            /// </summary>
            public static StateInfo Running
            {
                get
                {
                    var result = new StateInfo();
                    var metadata = SwiftObjectHelper<StateInfo>.GetTypeMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    metadata.ValueWitnessTable->DestructiveInjectEnumTag((void*)buffer, (uint)0, metadata);
                    result._payload = new SwiftSafeHandle<StateInfo>(buffer);
                    return result;
                }
            }
            
            /// <summary>
            /// Gets the 'cancelled' case of StateInfo.
            /// </summary>
            public static StateInfo Cancelled
            {
                get
                {
                    var result = new StateInfo();
                    var metadata = SwiftObjectHelper<StateInfo>.GetTypeMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    metadata.ValueWitnessTable->DestructiveInjectEnumTag((void*)buffer, (uint)1, metadata);
                    result._payload = new SwiftSafeHandle<StateInfo>(buffer);
                    return result;
                }
            }
            
            /// <summary>
            /// Gets the 'completed' case of StateInfo.
            /// </summary>
            public static StateInfo Completed
            {
                get
                {
                    var result = new StateInfo();
                    var metadata = SwiftObjectHelper<StateInfo>.GetTypeMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    metadata.ValueWitnessTable->DestructiveInjectEnumTag((void*)buffer, (uint)2, metadata);
                    result._payload = new SwiftSafeHandle<StateInfo>(buffer);
                    return result;
                }
            }
            
            /// <summary>
            /// Enum representing the possible cases of State.
            /// Tag values follow Swift's ordering: payload cases first, then no-payload cases.
            /// </summary>
            public enum CaseTag : uint
            {
                Running = 0,
                Cancelled = 1,
                Completed = 2,
            }
            
            /// <summary>
            /// Gets the current case of this enum instance.
            /// </summary>
            public unsafe CaseTag Tag
            {
                get
                {
                    bool success = false;
                    _payload.DangerousAddRef(ref success);
                    try
                    {
                        var metadata = SwiftObjectHelper<StateInfo>.GetTypeMetadata();
                        byte* payload = (byte*)_payload.DangerousGetHandle();
                        return (CaseTag)metadata.ValueWitnessTable->GetEnumTag(payload, metadata);
                    }
                    finally
                    {
                        if (success)
                            _payload.DangerousRelease();
                    }
                }
            }
            
            
            private unsafe nint HashValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_1A4FE697(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5StateO9hashValueSivg")]
            private static extern nint PInvoke_hashValue_Get_1A4FE697( SwiftSelf self);
            
            public nint HashValue
            {
                get => HashValue_Get();
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5StateOMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new StateInfo(handle);
            }
            
            StateInfo()
            {
            }
            
            StateInfo(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<StateInfo>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<StateInfo>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static StateInfo()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IEquatable<StateInfo>), "$s4Nuke9ImageTaskC5StateOSQAAMc"}
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type State and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
            public unsafe void Hash(ref Swift.Hasher into)
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_752D8297(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5StateO4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_752D8297( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        public unsafe class Event : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<Event>.GetTypeMetadata().Size;
            SwiftSafeHandle<Event> _payload = SwiftSafeHandle<Event>.Zero;
            internal SwiftSafeHandle<Event> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            /// <summary>
            /// Creates the 'progress' case of Event.
            /// </summary>
            public static Event Progress(Swift.Nuke.ImageTask.ProgressInfo value0)
            {
                var result = new Event();
                var metadata = PInvoke_getMetadata();
                IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                var indirectResult = new SwiftIndirectResult((void*)buffer);
                PInvoke_Progress(indirectResult, value0);
                result._payload = new SwiftSafeHandle<Event>(buffer);
                return result;
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5EventO8progressyAeC8ProgressVcAEmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_Progress(SwiftIndirectResult result, Swift.Nuke.ImageTask.ProgressInfo value0);
            
            /// <summary>
            /// Creates the 'preview' case of Event.
            /// </summary>
            public static Event Preview(Swift.Nuke.ImageResponse value0)
            {
                var result = new Event();
                var metadata = PInvoke_getMetadata();
                IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                var indirectResult = new SwiftIndirectResult((void*)buffer);
                PInvoke_Preview(indirectResult, value0);
                result._payload = new SwiftSafeHandle<Event>(buffer);
                return result;
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5EventO7previewyAeA0B8ResponseVcAEmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_Preview(SwiftIndirectResult result, Swift.Nuke.ImageResponse value0);
            
            /// <summary>
            /// Creates the 'finished' case of Event.
            /// </summary>
            public static Event Finished(Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error> value0)
            {
                var result = new Event();
                var metadata = PInvoke_getMetadata();
                IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                var indirectResult = new SwiftIndirectResult((void*)buffer);
                PInvoke_Finished(indirectResult, value0.Payload.DangerousGetHandle());
                result._payload = new SwiftSafeHandle<Event>(buffer);
                return result;
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5EventO8finishedyAEs6ResultOyAA0B8ResponseVAA0B8PipelineC5ErrorOGcAEmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_Finished(SwiftIndirectResult result, IntPtr value0);
            
            /// <summary>
            /// Gets the 'cancelled' case of Event.
            /// </summary>
            public static Event Cancelled
            {
                get
                {
                    var result = new Event();
                    var metadata = SwiftObjectHelper<Event>.GetTypeMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    metadata.ValueWitnessTable->DestructiveInjectEnumTag((void*)buffer, (uint)3, metadata);
                    result._payload = new SwiftSafeHandle<Event>(buffer);
                    return result;
                }
            }
            
            /// <summary>
            /// Enum representing the possible cases of Event.
            /// Tag values follow Swift's ordering: payload cases first, then no-payload cases.
            /// </summary>
            public enum CaseTag : uint
            {
                Progress = 0,
                Preview = 1,
                Finished = 2,
                Cancelled = 3,
            }
            
            /// <summary>
            /// Gets the current case of this enum instance.
            /// </summary>
            public unsafe CaseTag Tag
            {
                get
                {
                    bool success = false;
                    _payload.DangerousAddRef(ref success);
                    try
                    {
                        var metadata = SwiftObjectHelper<Event>.GetTypeMetadata();
                        byte* payload = (byte*)_payload.DangerousGetHandle();
                        return (CaseTag)metadata.ValueWitnessTable->GetEnumTag(payload, metadata);
                    }
                    finally
                    {
                        if (success)
                            _payload.DangerousRelease();
                    }
                }
            }
            
            /// <summary>
            /// Attempts to extract the associated value(s) for the 'progress' case.
            /// </summary>
            /// <param name="value">When this method returns true, contains the associated value(s).</param>
            /// <returns>True if this enum is the 'progress' case; otherwise, false.</returns>
            public unsafe bool TryGetProgress([MaybeNullWhen(false)] out Swift.Nuke.ImageTask.ProgressInfo value)
            {
                if (Tag != CaseTag.Progress)
                {
                    value = default;
                    return false;
                }
                
                var metadata = SwiftObjectHelper<Event>.GetTypeMetadata();
                
                // Create a non-destructive copy of the enum
                byte* enumCopy = stackalloc byte[(int)metadata.Size];
                bool success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    metadata.ValueWitnessTable->InitializeWithCopy(enumCopy, (void*)_payload.DangerousGetHandle(), metadata);
                }
                finally
                {
                    if (success)
                        _payload.DangerousRelease();
                }
                
                // Strip the tag to get the raw payload
                metadata.ValueWitnessTable->DestructiveProjectEnumData(enumCopy, metadata);
                
                // Marshal the payload to C# type(s)
                value = SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageTask.ProgressInfo>(new IntPtr(enumCopy));
                return true;
            }
            
            /// <summary>
            /// Attempts to extract the associated value(s) for the 'preview' case.
            /// </summary>
            /// <param name="value">When this method returns true, contains the associated value(s).</param>
            /// <returns>True if this enum is the 'preview' case; otherwise, false.</returns>
            public unsafe bool TryGetPreview([MaybeNullWhen(false)] out Swift.Nuke.ImageResponse value)
            {
                if (Tag != CaseTag.Preview)
                {
                    value = default;
                    return false;
                }
                
                var metadata = SwiftObjectHelper<Event>.GetTypeMetadata();
                
                // Create a non-destructive copy of the enum
                byte* enumCopy = stackalloc byte[(int)metadata.Size];
                bool success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    metadata.ValueWitnessTable->InitializeWithCopy(enumCopy, (void*)_payload.DangerousGetHandle(), metadata);
                }
                finally
                {
                    if (success)
                        _payload.DangerousRelease();
                }
                
                // Strip the tag to get the raw payload
                metadata.ValueWitnessTable->DestructiveProjectEnumData(enumCopy, metadata);
                
                // Marshal the payload to C# type(s)
                value = SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageResponse>(new IntPtr(enumCopy));
                return true;
            }
            
            /// <summary>
            /// Attempts to extract the associated value(s) for the 'finished' case.
            /// </summary>
            /// <param name="value">When this method returns true, contains the associated value(s).</param>
            /// <returns>True if this enum is the 'finished' case; otherwise, false.</returns>
            public unsafe bool TryGetFinished([MaybeNullWhen(false)] out Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error> value)
            {
                if (Tag != CaseTag.Finished)
                {
                    value = default;
                    return false;
                }
                
                var metadata = SwiftObjectHelper<Event>.GetTypeMetadata();
                
                // Create a non-destructive copy of the enum
                byte* enumCopy = stackalloc byte[(int)metadata.Size];
                bool success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    metadata.ValueWitnessTable->InitializeWithCopy(enumCopy, (void*)_payload.DangerousGetHandle(), metadata);
                }
                finally
                {
                    if (success)
                        _payload.DangerousRelease();
                }
                
                // Strip the tag to get the raw payload
                metadata.ValueWitnessTable->DestructiveProjectEnumData(enumCopy, metadata);
                
                // Marshal the payload to C# type(s)
                value = SwiftMarshal.MarshalFromSwift<Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error>>(new IntPtr(enumCopy));
                return true;
            }
            
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5EventOMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new Event(handle);
            }
            
            Event()
            {
            }
            
            Event(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<Event>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<Event>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static Event()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type Event and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
        }
        
        
        public unsafe void Cancel()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_cancel_661376C0(self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC6cancelyyF")]
        private static extern void PInvoke_cancel_661376C0( SwiftSelf self);
        
        
        public unsafe void Hash(ref Swift.Hasher into)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_hash_384EEEE9(into.Payload, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC4hash4intoys6HasherVz_tF")]
        private static extern void PInvoke_hash_384EEEE9( SafeHandle into,  SwiftSelf self);
        
        
    }
    
    
    public interface IImageDecoding
    {
        System.Boolean IsAsynchronous { get; }
        Swift.Nuke.ImageContainer Decode(Foundation.NSData arg0);
        Swift.Nuke.ImageContainer? DecodePartiallyDownloadedData(Foundation.NSData arg0);
    }
    
    /// <summary>
    /// Proxy class that enables C# implementations of the ImageDecoding protocol.
    /// Can wrap either a C# implementation or receive Swift existential containers.
    /// </summary>
    public unsafe class ImageDecodingProxy : IImageDecoding, ISwiftObject, Swift.Runtime.ISwiftExistentialConvertible<ExistentialContainer1>
    {
        /// <summary>Matches Swift ImageDecoding_vtable layout</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct ImageDecodingSwiftVTable
        {
            public IntPtr csVTHandle;
            public IntPtr func_isAsynchronous_get;
            public IntPtr func_decode_0;
            public IntPtr func_decodePartiallyDownloadedData_1;
        }
        
        /// <summary>Local vtable holding managed delegates</summary>
        private struct ImageDecodingLocalVTable
        {
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr> Func_isAsynchronous_get;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr> Func_decode_0;
            public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr> Func_decodePartiallyDownloadedData_1;
        }
        
        private static IntPtr _protocolWitnessTable;
        private static ImageDecodingSwiftVTable _swiftVTable;
        private static ImageDecodingLocalVTable _localVTable;
        private static GCHandle _localVTableHandle;
        private static bool _vtableInitialized;
        private static readonly object _vtableLock = new object();
        private readonly IImageDecoding? _csharpImpl;
        private readonly EveryProtocol? _everyProtocol;
        private ExistentialContainer1 _swiftContainer;
        static ImageDecodingProxy()
        {
            InitializeVtable();
        }
        
        private static void InitializeVtable()
        {
            lock (_vtableLock)
            {
                if (_vtableInitialized) return;
                
                _localVTable = new ImageDecodingLocalVTable
                {
                    Func_isAsynchronous_get = &Receive_isAsynchronous_get,
                    Func_decode_0 = &Receive_decode_0,
                    Func_decodePartiallyDownloadedData_1 = &Receive_decodePartiallyDownloadedData_1,
                };
                
                _localVTableHandle = GCHandle.Alloc(_localVTable, GCHandleType.Pinned);
                
                _swiftVTable = new ImageDecodingSwiftVTable
                {
                    csVTHandle = GCHandle.ToIntPtr(_localVTableHandle),
                    func_isAsynchronous_get = (IntPtr)_localVTable.Func_isAsynchronous_get,
                    func_decode_0 = (IntPtr)_localVTable.Func_decode_0,
                    func_decodePartiallyDownloadedData_1 = (IntPtr)_localVTable.Func_decodePartiallyDownloadedData_1,
                };
                
                fixed (ImageDecodingSwiftVTable* vtPtr = &_swiftVTable)
                {
                    NativeMethods.SetImageDecoding_vtable((IntPtr)vtPtr);
                }
                _vtableInitialized = true;
            }
        }
        
        #region Swift Callback Receivers
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_isAsynchronous_get(IntPtr vtHandle, IntPtr selfContainer)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImageDecodingProxy>(container);
            var result = proxy._csharpImpl!.IsAsynchronous;
            return MarshalToSwiftBuffer(result);
        }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_decode_0(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImageDecodingProxy>(container);
            var param0 = MarshalFromSwift<Swift.Data>(rawArg0);
            var result = proxy._csharpImpl!.Decode(param0);
            return MarshalToSwiftBuffer(result);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_decodePartiallyDownloadedData_1(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImageDecodingProxy>(container);
            var param0 = MarshalFromSwift<Swift.Data>(rawArg0);
            var result = proxy._csharpImpl!.DecodePartiallyDownloadedData(param0);
            return MarshalToSwiftBuffer(result);
        }
        
        #endregion
        
        /// <summary>
        /// Creates a proxy wrapping a C# implementation of IImageDecoding.
        /// </summary>
        /// <param name="implementation">The C# implementation of the protocol.</param>
        public ImageDecodingProxy(IImageDecoding implementation)
        {
            _csharpImpl = implementation ?? throw new ArgumentNullException(nameof(implementation));
            _everyProtocol = new EveryProtocol();
            // Create existential container manually
            // The container holds: payload (EveryProtocol pointer), metadata, and witness table
            _swiftContainer = new ExistentialContainer1();
            _swiftContainer.Payload0 = _everyProtocol.Handle;
            _swiftContainer.ObjectMetadata = EveryProtocol.GetTypeMetadata();
            _swiftContainer[0] = ProtocolWitnessTableHandle;
            // Register this proxy so Swift callbacks can find us
            SwiftObjectRegistry.RegisterStrong(_everyProtocol.Handle, this);
        }
        /// <summary>
        /// Creates a proxy from an existing Swift existential container.
        /// Use this when receiving protocol values from Swift code.
        /// </summary>
        /// <remarks>
        /// Swift-backed proxies created with this constructor dispatch blittable and String
        /// protocol members through witness table accessors. Non-dispatchable members
        /// (non-blittable non-String types, throwing, async) throw <see cref="NotSupportedException"/>.
        /// </remarks>
        /// <param name="container">The Swift existential container.</param>
        public ImageDecodingProxy(ExistentialContainer1 container)
        {
            _swiftContainer = container;
            _csharpImpl = null;
            _everyProtocol = null;
        }
        #region Interface Implementation
        
        public System.Boolean IsAsynchronous
        {
            get
            {
                if (_csharpImpl != null)
                    return _csharpImpl.IsAsynchronous;
                fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                {
                    IntPtr resultPtr = NativeMethods.SBW_ImageDecoding_get_isAsynchronous_0((IntPtr)containerPtr);
                    try { return MarshalFromSwift<bool>(resultPtr); }
                    finally { NativeMethods.SBW_ImageDecoding_free_get_isAsynchronous_0(resultPtr); }
                }
            }
        }
        
        public Swift.Nuke.ImageContainer Decode(Foundation.NSData arg0)
        {
            if (_csharpImpl != null)
                return _csharpImpl.Decode(arg0);
            throw new NotSupportedException(
                "Cannot call method 'Decode' on a Swift-backed existential container. " +
                "Protocol member access is only supported when wrapping a C# implementation.");
        }
        
        public Swift.Nuke.ImageContainer? DecodePartiallyDownloadedData(Foundation.NSData arg0)
        {
            if (_csharpImpl != null)
                return _csharpImpl.DecodePartiallyDownloadedData(arg0);
            throw new NotSupportedException(
                "Cannot call method 'DecodePartiallyDownloadedData' on a Swift-backed existential container. " +
                "Protocol member access is only supported when wrapping a C# implementation.");
        }
        
        #endregion
        
        #region ISwiftObject Implementation
        /// <summary>
        /// Gets the protocol witness table handle for EveryProtocol conforming to ImageDecoding.
        /// </summary>
        public static IntPtr ProtocolWitnessTableHandle
        {
            get
            {
                if (_protocolWitnessTable == IntPtr.Zero)
                {
                    // The witness table is generated by the Swift compiler
                    // It will be available after the Swift wrapper is loaded
                    // For now, we look it up dynamically
                    _protocolWitnessTable = GetWitnessTableFromSwift();
                }
                return _protocolWitnessTable;
            }
        }
        private static IntPtr GetWitnessTableFromSwift()
        {
            // Call the Swift-exported function that returns the witness table pointer
            // This function is generated by EveryProtocolEmitter.EmitWitnessTableGetter()
            return NativeMethods.GetWitnessTable();
        }
        /// <summary>
        /// Gets the existential container that can be passed to Swift code.
        /// </summary>
        public ExistentialContainer1 GetExistentialContainer() => _swiftContainer;
        public static TypeMetadata GetTypeMetadata()
        {
            // Proxy classes don't have their own Swift metadata
            // They use the EveryProtocol metadata
            return EveryProtocol.GetTypeMetadata();
        }
        public static ISwiftObject NewFromPayload(IntPtr payload)
        {
            // Create from existential container
            var container = *(ExistentialContainer1*)payload;
            return new ImageDecodingProxy(container);
        }
        public int MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            // Marshal the existential container
            var size = _swiftContainer.SizeOf;
            if (swiftDestSpan.Length < size)
                throw new ArgumentException("Destination span too small", nameof(swiftDestSpan));
            fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
            {
                new Span<byte>(containerPtr, size).CopyTo(swiftDestSpan);
            }
            return size;
        }
        public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
        {
            throw new NotSupportedException(
                "Protocol conformance descriptor is not available for proxy types. " +
                "Proxy classes use EveryProtocol's witness table, not native conformance descriptors.");
        }
        public void Dispose() { }
        #endregion
        #region Marshalling Helpers
        [StructLayout(LayoutKind.Sequential)]
        private struct Utf8Slice
        {
            public IntPtr Ptr;
            public nint Len;
        }
        private static IntPtr MarshalToSwiftBuffer<T>(T value)
        {
            // Use direct memory operations for all types
            // This works for blittable types (value types, structs with blittable fields)
            var size = Unsafe.SizeOf<T>();
            var ptr = (IntPtr)NativeMemory.Alloc((nuint)size);
            Unsafe.Write((void*)ptr, value);
            return ptr;
        }
        private static T MarshalFromSwift<T>(IntPtr ptr)
        {
            // Use direct memory operations for all types
            return Unsafe.Read<T>((void*)ptr);
        }
        #endregion
        private static class NativeMethods
        {
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SetImageDecoding_vtable")]
            public static extern void SetImageDecoding_vtable(IntPtr vtable);
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "Get_EveryProtocol_ImageDecoding_WitnessTable")]
            public static extern IntPtr GetWitnessTable();
            
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SBW_ImageDecoding_get_isAsynchronous_0")]
            public static extern IntPtr SBW_ImageDecoding_get_isAsynchronous_0(IntPtr containerPtr);
            [DllImport("SwiftBindings", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SBW_ImageDecoding_free_get_isAsynchronous_0")]
            public static extern void SBW_ImageDecoding_free_get_isAsynchronous_0(IntPtr ptr);
        }
    }
    
    
    public unsafe class ImageDecodingError : ISwiftObject
    {
        static nuint _payloadSize = SwiftObjectHelper<ImageDecodingError>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageDecodingError> _payload = SwiftSafeHandle<ImageDecodingError>.Zero;
        internal SwiftSafeHandle<ImageDecodingError> Payload => _payload;
        
        public void Dispose() => _payload.Dispose();
        
        /// <summary>
        /// Gets the 'unknown' case of ImageDecodingError.
        /// </summary>
        public static ImageDecodingError Unknown
        {
            get
            {
                var result = new ImageDecodingError();
                var metadata = SwiftObjectHelper<ImageDecodingError>.GetTypeMetadata();
                IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                metadata.ValueWitnessTable->DestructiveInjectEnumTag((void*)buffer, (uint)0, metadata);
                result._payload = new SwiftSafeHandle<ImageDecodingError>(buffer);
                return result;
            }
        }
        
        /// <summary>
        /// Enum representing the possible cases of ImageDecodingError.
        /// Tag values follow Swift's ordering: payload cases first, then no-payload cases.
        /// </summary>
        public enum CaseTag : uint
        {
            Unknown = 0,
        }
        
        /// <summary>
        /// Gets the current case of this enum instance.
        /// </summary>
        public unsafe CaseTag Tag
        {
            get
            {
                bool success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var metadata = SwiftObjectHelper<ImageDecodingError>.GetTypeMetadata();
                    byte* payload = (byte*)_payload.DangerousGetHandle();
                    return (CaseTag)metadata.ValueWitnessTable->GetEnumTag(payload, metadata);
                }
                finally
                {
                    if (success)
                        _payload.DangerousRelease();
                }
            }
        }
        
        
        private unsafe Swift.SwiftString Description_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_description_Get_47557816(self);
                
                unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke18ImageDecodingErrorO11descriptionSSvg")]
        private static extern Swift.SwiftString.Buffer PInvoke_description_Get_47557816( SwiftSelf self);
        
        public string Description
        {
            get => Description_Get().ToString();
        }
        
        private unsafe nint HashValue_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_hashValue_Get_45DB3B54(self);
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke18ImageDecodingErrorO9hashValueSivg")]
        private static extern nint PInvoke_hashValue_Get_45DB3B54( SwiftSelf self);
        
        public nint HashValue
        {
            get => HashValue_Get();
        }
        
        static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke18ImageDecodingErrorOMa")]
        internal static extern TypeMetadata PInvoke_getMetadata();
        
        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
        {
            return new ImageDecodingError(handle);
        }
        
        ImageDecodingError()
        {
        }
        
        ImageDecodingError(SwiftHandle handle)
        {
            _payload = new SwiftSafeHandle<ImageDecodingError>(handle);
        }
        
        unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            var metadata = SwiftObjectHelper<ImageDecodingError>.GetTypeMetadata();
            if ((int)metadata.Size > swiftDestSpan.Length)
            {
                throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
            }
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure that the instance is valid before making copy
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
        
        private static Dictionary<Type, string> _protocolConformanceSymbols;
        static ImageDecodingError()
        {
            _protocolConformanceSymbols = new Dictionary<Type, string>
            {
                {typeof(IEquatable<ImageDecodingError>), "$s4Nuke18ImageDecodingErrorOSQAAMc"}
            };
        }
        
        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
            where TProtocol : class
        {
            if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
            {
                throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type ImageDecodingError and protocol {typeof(TProtocol).Name}, but no conformance was found.");
            }
            return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
        }
        
        public unsafe void Hash(ref Swift.Hasher into)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_hash_5234B170(into.Payload, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke18ImageDecodingErrorO4hash4intoys6HasherVz_tF")]
        private static extern void PInvoke_hash_5234B170( SafeHandle into,  SwiftSelf self);
        
        
    }
    
    
    public unsafe class ImageEncoders : ISwiftObject
    {
        static nuint _payloadSize = SwiftObjectHelper<ImageEncoders>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageEncoders> _payload = SwiftSafeHandle<ImageEncoders>.Zero;
        internal SwiftSafeHandle<ImageEncoders> Payload => _payload;
        
        public void Dispose() => _payload.Dispose();
        
        static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageEncodersOMa")]
        internal static extern TypeMetadata PInvoke_getMetadata();
        
        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
        {
            return new ImageEncoders(handle);
        }
        
        ImageEncoders()
        {
        }
        
        ImageEncoders(SwiftHandle handle)
        {
            _payload = new SwiftSafeHandle<ImageEncoders>(handle);
        }
        
        unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            var metadata = SwiftObjectHelper<ImageEncoders>.GetTypeMetadata();
            if ((int)metadata.Size > swiftDestSpan.Length)
            {
                throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
            }
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure that the instance is valid before making copy
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
        
        private static Dictionary<Type, string> _protocolConformanceSymbols;
        static ImageEncoders()
        {
            _protocolConformanceSymbols = new Dictionary<Type, string>
            {
                
            };
        }
        
        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
            where TProtocol : class
        {
            if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
            {
                throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type ImageEncoders and protocol {typeof(TProtocol).Name}, but no conformance was found.");
            }
            return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
        }
        
        public unsafe class ImageIO : ISwiftObject
        {
            private unsafe Swift.Nuke.AssetType Type_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.AssetType>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_type_Get_5FAF88AF(swiftIndirectResult, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageEncodersO0B2IOV4typeAA9AssetTypeVvg")]
            private static extern void PInvoke_type_Get_5FAF88AF( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            public Swift.Nuke.AssetType Type
            {
                get => Type_Get();
            }
            
            private unsafe System.Single CompressionRatio_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_compressionRatio_Get_5CA8FB6D(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageEncodersO0B2IOV16compressionRatioSfvg")]
            private static extern System.Single PInvoke_compressionRatio_Get_5CA8FB6D( SwiftSelf self);
            
            public System.Single CompressionRatio
            {
                get => CompressionRatio_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<ImageIO>.GetTypeMetadata().Size;
            SwiftSafeHandle<ImageIO> _payload = SwiftSafeHandle<ImageIO>.Zero;
            
            internal SwiftSafeHandle<ImageIO> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageEncodersO0B2IOVMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new ImageIO(handle);
            }
            
            ImageIO(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<ImageIO>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<ImageIO>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static ImageIO()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IImageEncoding), "$s4Nuke13ImageEncodersO0B2IOVAA0B8EncodingAAMc"}
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type ImageIO and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
            
            public unsafe ImageIO( Swift.Nuke.AssetType type,  System.Single compressionRatio)
            {
                _payload = new SwiftSafeHandle<ImageIO>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_6888C41B(swiftIndirectResult, type.Payload, compressionRatio);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageEncodersO0B2IOV4type16compressionRatioAeA9AssetTypeV_SftcfC")]
            private static extern void PInvoke_init_6888C41B( SwiftIndirectResult swiftIndirectResult,  SafeHandle type,  System.Single compressionRatio);
            public unsafe ImageIO( Swift.Nuke.AssetType type)
            {
                _payload = new SwiftSafeHandle<ImageIO>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_2D5384E6(swiftIndirectResult, type.Payload);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("SwiftBindings", EntryPoint = "DBW_ImageIO_init_5E8C4DC8_1")]
            private static extern void PInvoke_init_2D5384E6( SwiftIndirectResult swiftIndirectResult,  SafeHandle type);
            
            
            public static System.Boolean IsSupported( Swift.Nuke.AssetType type)
            {
                try
                {
                    
                    
                    var result = PInvoke_isSupported_6ED9E83D(type.Payload);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageEncodersO0B2IOV11isSupported4typeSbAA9AssetTypeV_tFZ")]
            private static extern System.Boolean PInvoke_isSupported_6ED9E83D( SafeHandle type);
            
            
            public unsafe Swift.Data? Encode( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_encode_50CA4828(arg0Handle, self);
                    
                    var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Data>>(new IntPtr(&result));
                    return swiftResult.ToNullable();
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageEncodersO0B2IOV6encodey10Foundation4DataVSgSo7UIImageCF")]
            private static extern IntPtr PInvoke_encode_50CA4828( IntPtr arg0,  SwiftSelf self);
            
            
        }
        
        
        public unsafe class Default : ISwiftObject
        {
            private unsafe System.Single CompressionQuality_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_compressionQuality_Get_5F895A31(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageEncodersO7DefaultV18compressionQualitySfvg")]
            private static extern System.Single PInvoke_compressionQuality_Get_5F895A31( SwiftSelf self);
            
            private unsafe void CompressionQuality_Set( System.Single value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_compressionQuality_Set_3F47B717(value, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageEncodersO7DefaultV18compressionQualitySfvs")]
            private static extern void PInvoke_compressionQuality_Set_3F47B717( System.Single value,  SwiftSelf self);
            
            public System.Single CompressionQuality
            {
                get => CompressionQuality_Get();
                set => CompressionQuality_Set(value);
            }
            
            private unsafe System.Boolean IsHEIFPreferred_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isHEIFPreferred_Get_04CAFB38(self);
                    
                    return result;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageEncodersO7DefaultV15isHEIFPreferredSbvg")]
            private static extern System.Boolean PInvoke_isHEIFPreferred_Get_04CAFB38( SwiftSelf self);
            
            private unsafe void IsHEIFPreferred_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isHEIFPreferred_Set_452077AC(value, self);
                    
                    return;
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageEncodersO7DefaultV15isHEIFPreferredSbvs")]
            private static extern void PInvoke_isHEIFPreferred_Set_452077AC( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean IsHEIFPreferred
            {
                get => IsHEIFPreferred_Get();
                set => IsHEIFPreferred_Set(value);
            }
            
            static nuint _payloadSize = SwiftObjectHelper<Default>.GetTypeMetadata().Size;
            SwiftSafeHandle<Default> _payload = SwiftSafeHandle<Default>.Zero;
            
            internal SwiftSafeHandle<Default> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageEncodersO7DefaultVMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new Default(handle);
            }
            
            Default(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<Default>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<Default>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static Default()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IImageEncoding), "$s4Nuke13ImageEncodersO7DefaultVAA0B8EncodingAAMc"}
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type Default and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
            
            public unsafe Default( System.Single compressionQuality)
            {
                _payload = new SwiftSafeHandle<Default>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_4E509090(swiftIndirectResult, compressionQuality);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageEncodersO7DefaultV18compressionQualityAESf_tcfC")]
            private static extern void PInvoke_init_4E509090( SwiftIndirectResult swiftIndirectResult,  System.Single compressionQuality);
            public unsafe Default()
            {
                _payload = new SwiftSafeHandle<Default>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_1EB0F1FC(swiftIndirectResult);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("SwiftBindings", EntryPoint = "DBW_Default_init_42CADF88_1")]
            private static extern void PInvoke_init_1EB0F1FC( SwiftIndirectResult swiftIndirectResult);
            
            
            public unsafe Swift.Data? Encode( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_encode_2D24BA94(arg0Handle, self);
                    
                    var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Data>>(new IntPtr(&result));
                    return swiftResult.ToNullable();
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageEncodersO7DefaultV6encodey10Foundation4DataVSgSo7UIImageCF")]
            private static extern IntPtr PInvoke_encode_2D24BA94( IntPtr arg0,  SwiftSelf self);
            
            
        }
        
        
    }
    
    
    public unsafe class ImagePrefetcher : ISwiftObject
    {
        private unsafe System.Boolean IsPaused_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_isPaused_Get_4B7A784B(self);
                
                return result;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC8isPausedSbvg")]
        private static extern System.Boolean PInvoke_isPaused_Get_4B7A784B( SwiftSelf self);
        
        private unsafe void IsPaused_Set( System.Boolean value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_isPaused_Set_16F57A35(value, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC8isPausedSbvs")]
        private static extern void PInvoke_isPaused_Set_16F57A35( System.Boolean value,  SwiftSelf self);
        
        public System.Boolean IsPaused
        {
            get => IsPaused_Get();
            set => IsPaused_Set(value);
        }
        
        private unsafe Swift.Nuke.ImageRequest.PriorityInfo Priority_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.PriorityInfo>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_priority_Get_785DB4EE(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.PriorityInfo>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC8priorityAA0B7RequestV8PriorityOvg")]
        private static extern void PInvoke_priority_Get_785DB4EE( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Priority_Set( Swift.Nuke.ImageRequest.PriorityInfo value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_priority_Set_7416A6CE(value.Payload.DangerousGetHandle(), self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC8priorityAA0B7RequestV8PriorityOvs")]
        private static extern void PInvoke_priority_Set_7416A6CE( IntPtr value,  SwiftSelf self);
        
        public Swift.Nuke.ImageRequest.PriorityInfo Priority
        {
            get => Priority_Get();
            set => Priority_Set(value);
        }
        
        private unsafe Action? DidComplete_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_didComplete_Get_46466B47(self);
                
                // Wrap Swift closure in SwiftEscapingClosure for ARC management
                var _closureWrapper = SwiftEscapingClosure<Action>.FromSwift(result.FunctionPointer, result.Context);
                // Create invoker delegate that captures wrapper (keeps it alive for proper ARC)
                Action _invoker = () =>
                {
                    unsafe
                    {
                        var _fp = (delegate* unmanaged[Swift]<SwiftSelf, void>)_closureWrapper.FunctionPointer;
                        var _swiftSelf = new SwiftSelf((void*)_closureWrapper.Context.ToPointer());
                        _fp(_swiftSelf);
                    }
                };
                return _invoker;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC11didCompleteyyYbScMYccSgvg")]
        private static extern SwiftClosureData PInvoke_didComplete_Get_46466B47( SwiftSelf self);
        
        private static unsafe readonly delegate* unmanaged[Swift]<SwiftSelf, void> s_didComplete_Set_value_33742735_Callback = &didComplete_Set_value_33742735_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void didComplete_Set_value_33742735_Callback(SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Action>(new IntPtr(context.Value));
            del();
        }
        
        private unsafe void DidComplete_Set( Action? value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            GCHandle valueHandle = default;
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                SwiftClosureData valueClosure;
                if (value != null)
                {
                    valueHandle = GCHandle.Alloc(value);
                    valueClosure = new SwiftClosureData((IntPtr)s_didComplete_Set_value_33742735_Callback, GCHandle.ToIntPtr(valueHandle));
                }
                else
                {
                    valueClosure = default; // Zero-initialized = nil in Swift
                }
                
                PInvoke_didComplete_Set_33742735(valueClosure, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
                if (valueHandle.IsAllocated) valueHandle.Free();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC11didCompleteyyYbScMYccSgvs")]
        private static extern void PInvoke_didComplete_Set_33742735( SwiftClosureData value,  SwiftSelf self);
        
        public Action? DidComplete
        {
            get => DidComplete_Get();
            set => DidComplete_Set(value);
        }
        
        static nuint _payloadSize = SwiftObjectHelper<ImagePrefetcher>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImagePrefetcher> _payload = SwiftSafeHandle<ImagePrefetcher>.Zero;
        
        internal SwiftSafeHandle<ImagePrefetcher> Payload => _payload;
        
        public void Dispose() => _payload.Dispose();
        
        static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherCMa")]
        internal static extern TypeMetadata PInvoke_getMetadata();
        
        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
        {
            return new ImagePrefetcher(handle);
        }
        
        ImagePrefetcher(SwiftHandle handle)
        {
            _payload = new SwiftSafeHandle<ImagePrefetcher>(handle);
        }
        
        unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
        {
            var metadata = SwiftObjectHelper<ImagePrefetcher>.GetTypeMetadata();
            if ((int)metadata.Size > swiftDestSpan.Length)
            {
                throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
            }
            fixed (void* swiftDest = swiftDestSpan)
            {
                // Ensure that the instance is valid before making copy
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
        
        private static Dictionary<Type, string> _protocolConformanceSymbols;
        static ImagePrefetcher()
        {
            _protocolConformanceSymbols = new Dictionary<Type, string>
            {
                
            };
        }
        
        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
            where TProtocol : class
        {
            if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
            {
                throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type ImagePrefetcher and protocol {typeof(TProtocol).Name}, but no conformance was found.");
            }
            return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
        }
        
        public unsafe class Destination : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<Destination>.GetTypeMetadata().Size;
            SwiftSafeHandle<Destination> _payload = SwiftSafeHandle<Destination>.Zero;
            internal SwiftSafeHandle<Destination> Payload => _payload;
            
            public void Dispose() => _payload.Dispose();
            
            /// <summary>
            /// Gets the 'memoryCache' case of Destination.
            /// </summary>
            public static Destination MemoryCache
            {
                get
                {
                    var result = new Destination();
                    var metadata = SwiftObjectHelper<Destination>.GetTypeMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    metadata.ValueWitnessTable->DestructiveInjectEnumTag((void*)buffer, (uint)0, metadata);
                    result._payload = new SwiftSafeHandle<Destination>(buffer);
                    return result;
                }
            }
            
            /// <summary>
            /// Gets the 'diskCache' case of Destination.
            /// </summary>
            public static Destination DiskCache
            {
                get
                {
                    var result = new Destination();
                    var metadata = SwiftObjectHelper<Destination>.GetTypeMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    metadata.ValueWitnessTable->DestructiveInjectEnumTag((void*)buffer, (uint)1, metadata);
                    result._payload = new SwiftSafeHandle<Destination>(buffer);
                    return result;
                }
            }
            
            /// <summary>
            /// Enum representing the possible cases of Destination.
            /// Tag values follow Swift's ordering: payload cases first, then no-payload cases.
            /// </summary>
            public enum CaseTag : uint
            {
                MemoryCache = 0,
                DiskCache = 1,
            }
            
            /// <summary>
            /// Gets the current case of this enum instance.
            /// </summary>
            public unsafe CaseTag Tag
            {
                get
                {
                    bool success = false;
                    _payload.DangerousAddRef(ref success);
                    try
                    {
                        var metadata = SwiftObjectHelper<Destination>.GetTypeMetadata();
                        byte* payload = (byte*)_payload.DangerousGetHandle();
                        return (CaseTag)metadata.ValueWitnessTable->GetEnumTag(payload, metadata);
                    }
                    finally
                    {
                        if (success)
                            _payload.DangerousRelease();
                    }
                }
            }
            
            
            private unsafe nint HashValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_6A917493(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC11DestinationO9hashValueSivg")]
            private static extern nint PInvoke_hashValue_Get_6A917493( SwiftSelf self);
            
            public nint HashValue
            {
                get => HashValue_Get();
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC11DestinationOMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new Destination(handle);
            }
            
            Destination()
            {
            }
            
            Destination(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<Destination>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<Destination>.GetTypeMetadata();
                if ((int)metadata.Size > swiftDestSpan.Length)
                {
                    throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                }
                fixed (void* swiftDest = swiftDestSpan)
                {
                    // Ensure that the instance is valid before making copy
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
            
            private static Dictionary<Type, string> _protocolConformanceSymbols;
            static Destination()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IEquatable<Destination>), "$s4Nuke15ImagePrefetcherC11DestinationOSQAAMc"}
                };
            }
            
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type Destination and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }
                return ProtocolConformanceDescriptor.LoadFromSymbol("Nuke", symbolName);
            }
            
            public unsafe void Hash(ref Swift.Hasher into)
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_283CCECA(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC11DestinationO4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_283CCECA( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        public unsafe ImagePrefetcher( Swift.Nuke.ImagePipeline pipeline,  Swift.Nuke.ImagePrefetcher.Destination destination,  nint maxConcurrentRequestCount)
        {
            _payload = new SwiftSafeHandle<ImagePrefetcher>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            PInvoke_init_6602364A(swiftIndirectResult, pipeline.Payload, destination.Payload.DangerousGetHandle(), maxConcurrentRequestCount);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC8pipeline11destination25maxConcurrentRequestCountAcA0B8PipelineC_AC11DestinationOSitcfC")]
        private static extern void PInvoke_init_6602364A( SwiftIndirectResult swiftIndirectResult,  SafeHandle pipeline,  IntPtr destination,  nint maxConcurrentRequestCount);
        public unsafe ImagePrefetcher()
        {
            _payload = new SwiftSafeHandle<ImagePrefetcher>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            PInvoke_init_75ED5F57(swiftIndirectResult);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("SwiftBindings", EntryPoint = "DBW_ImagePrefetcher_init_9862FED5_3")]
        private static extern void PInvoke_init_75ED5F57( SwiftIndirectResult swiftIndirectResult);
        public unsafe ImagePrefetcher( Swift.Nuke.ImagePipeline pipeline)
        {
            _payload = new SwiftSafeHandle<ImagePrefetcher>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            PInvoke_init_1C589B94(swiftIndirectResult, pipeline.Payload);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("SwiftBindings", EntryPoint = "DBW_ImagePrefetcher_init_9862FED5_2")]
        private static extern void PInvoke_init_1C589B94( SwiftIndirectResult swiftIndirectResult,  SafeHandle pipeline);
        public unsafe ImagePrefetcher( Swift.Nuke.ImagePipeline pipeline,  Swift.Nuke.ImagePrefetcher.Destination destination)
        {
            _payload = new SwiftSafeHandle<ImagePrefetcher>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            PInvoke_init_2082E4EF(swiftIndirectResult, pipeline.Payload, destination.Payload.DangerousGetHandle());
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("SwiftBindings", EntryPoint = "DBW_ImagePrefetcher_init_9862FED5_1")]
        private static extern void PInvoke_init_2082E4EF( SwiftIndirectResult swiftIndirectResult,  SafeHandle pipeline,  IntPtr destination);
        
        
        public unsafe void StartPrefetching( IEnumerable<Swift.URL> with)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                using var withSwift = SwiftArray<Swift.URL>.FromEnumerable(with);
                using PayloadBuffer<IntPtr> withDisposable = withSwift.PayloadBuffer;
                IntPtr withBuffer = withDisposable.Buffer;
                
                PInvoke_startPrefetching_4BE61EC0(withBuffer, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC16startPrefetching4withySay10Foundation3URLVG_tF")]
        private static extern void PInvoke_startPrefetching_4BE61EC0( IntPtr withBuffer,  SwiftSelf self);
        
        
        public unsafe void _startPrefetching( IEnumerable<Swift.Nuke.ImageRequest> with)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                using var withSwift = SwiftArray<Swift.Nuke.ImageRequest>.FromEnumerable(with);
                using PayloadBuffer<IntPtr> withDisposable = withSwift.PayloadBuffer;
                IntPtr withBuffer = withDisposable.Buffer;
                
                PInvoke__startPrefetching_6D42A9F8(withBuffer, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC17_startPrefetching4withySayAA0B7RequestVG_tF")]
        private static extern void PInvoke__startPrefetching_6D42A9F8( IntPtr withBuffer,  SwiftSelf self);
        
        
        public unsafe void StopPrefetching( IEnumerable<Swift.URL> with)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                using var withSwift = SwiftArray<Swift.URL>.FromEnumerable(with);
                using PayloadBuffer<IntPtr> withDisposable = withSwift.PayloadBuffer;
                IntPtr withBuffer = withDisposable.Buffer;
                
                PInvoke_stopPrefetching_2591EA43(withBuffer, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC15stopPrefetching4withySay10Foundation3URLVG_tF")]
        private static extern void PInvoke_stopPrefetching_2591EA43( IntPtr withBuffer,  SwiftSelf self);
        
        
        public unsafe void StopPrefetching()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_stopPrefetching_0721FE1C(self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC15stopPrefetchingyyF")]
        private static extern void PInvoke_stopPrefetching_0721FE1C( SwiftSelf self);
        
        
    }
    
    
}
