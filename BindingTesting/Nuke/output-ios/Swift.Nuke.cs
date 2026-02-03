using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
    public interface ISwiftImageProcessing
    {
        Swift.SwiftString identifier { get; }
        Swift.AnyType hashableIdentifier { get; }
        Swift.SwiftOptional<UIKit.UIImage> process(UIKit.UIImage arg0);
        Swift.Nuke.ImageContainer process(Swift.Nuke.ImageContainer arg0, Swift.Nuke.ImageProcessingContext context);
    }
    
    /// <summary>
    /// Proxy class that enables C# implementations of the ImageProcessing protocol.
    /// Can wrap either a C# implementation or receive Swift existential containers.
    /// </summary>
    public unsafe class ImageProcessingProxy : ISwiftImageProcessing, ISwiftObject
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
        private readonly ISwiftImageProcessing? _csharpImpl;
        private readonly EveryProtocol? _everyProtocol;
        private readonly ExistentialContainer1 _swiftContainer;
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
            var result = proxy._csharpImpl!.identifier;
            return MarshalToSwiftBuffer(result);
        }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_hashableIdentifier_get(IntPtr vtHandle, IntPtr selfContainer)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImageProcessingProxy>(container);
            var result = proxy._csharpImpl!.hashableIdentifier;
            return MarshalToSwiftBuffer(result);
        }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_process_0(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImageProcessingProxy>(container);
            var param0 = MarshalFromSwift<UIKit.UIImage>(rawArg0);
            var result = proxy._csharpImpl!.process(param0);
            return MarshalToSwiftBuffer(result);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_process_1(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImageProcessingProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageContainer>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImageProcessingContext>(rawArg1);
            var result = proxy._csharpImpl!.process(param0, param1);
            return MarshalToSwiftBuffer(result);
        }
        
        #endregion
        
        /// <summary>
        /// Creates a proxy wrapping a C# implementation of ISwiftImageProcessing.
        /// </summary>
        /// <param name="implementation">The C# implementation of the protocol.</param>
        public ImageProcessingProxy(ISwiftImageProcessing implementation)
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
        /// <param name="container">The Swift existential container.</param>
        public ImageProcessingProxy(ExistentialContainer1 container)
        {
            _swiftContainer = container;
            _csharpImpl = null;
            _everyProtocol = null;
        }
        #region Interface Implementation
        
        public Swift.SwiftString identifier
        {
            get
            {
                if (_csharpImpl != null)
                    return _csharpImpl.identifier;
                // TODO: Call Swift via P/Invoke for Swift implementation
                throw new NotImplementedException("Swift implementation not yet supported");
            }
        }
        
        public Swift.AnyType hashableIdentifier
        {
            get
            {
                if (_csharpImpl != null)
                    return _csharpImpl.hashableIdentifier;
                // TODO: Call Swift via P/Invoke for Swift implementation
                throw new NotImplementedException("Swift implementation not yet supported");
            }
        }
        
        public Swift.SwiftOptional<UIKit.UIImage> process(UIKit.UIImage arg0)
        {
            if (_csharpImpl != null)
                return _csharpImpl.process(arg0);
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
        }
        
        public Swift.Nuke.ImageContainer process(Swift.Nuke.ImageContainer arg0, Swift.Nuke.ImageProcessingContext context)
        {
            if (_csharpImpl != null)
                return _csharpImpl.process(arg0, context);
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
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
            throw new NotImplementedException("Protocol conformance descriptor not available for proxy types");
        }
        #endregion
        #region Marshalling Helpers
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
                
                
                
                PInvoke_request_Get_4C07EC0D(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_request_Get_4C07EC0D( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Request_Set( Swift.Nuke.ImageRequest value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_request_Set_0653F067(value.Payload, self);
                
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
        private static extern void PInvoke_request_Set_0653F067( SafeHandle value,  SwiftSelf self);
        
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
                
                
                
                PInvoke_response_Get_6A8CC7B6(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_response_Get_6A8CC7B6( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Response_Set( Swift.Nuke.ImageResponse value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_response_Set_10D85C54(value.Payload, self);
                
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
        private static extern void PInvoke_response_Set_10D85C54( SafeHandle value,  SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_isCompleted_Get_461ED4EF(self);
                
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
        private static extern System.Boolean PInvoke_isCompleted_Get_461ED4EF( SwiftSelf self);
        
        private unsafe void IsCompleted_Set( System.Boolean value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_isCompleted_Set_0461BFB8(value, self);
                
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
        private static extern void PInvoke_isCompleted_Set_0461BFB8( System.Boolean value,  SwiftSelf self);
        
        public System.Boolean IsCompleted
        {
            get => IsCompleted_Get();
            set => IsCompleted_Set(value);
        }
        
        static nuint _payloadSize = SwiftObjectHelper<ImageProcessingContext>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageProcessingContext> _payload = SwiftSafeHandle<ImageProcessingContext>.Zero;
        
        public SwiftSafeHandle<ImageProcessingContext> Payload => _payload;
        
        // Swift structs cannot be compared using .NET's default equality semantics,
        // since Swift's equality is defined by the Equatable protocol.
        // This type does not implement Swift's Equatable protocol.
        public override bool Equals(object? obj)
        {
            throw new InvalidOperationException("Type ImageProcessingContext does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        public override int GetHashCode()
        {
            throw new InvalidOperationException("Type ImageProcessingContext does not implement Swift's Equatable protocol, so GetHashCode() is not supported.");
        }
        
        public static bool operator ==(ImageProcessingContext left, ImageProcessingContext right)
        {
            throw new InvalidOperationException("Type ImageProcessingContext does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        
        public static bool operator !=(ImageProcessingContext left, ImageProcessingContext right)
        {
            throw new InvalidOperationException("Type ImageProcessingContext does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        
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
            
            PInvoke_init_7302467F(swiftIndirectResult, request.Payload, response.Payload, isCompleted);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingContextV7request8response11isCompletedAcA0B7RequestV_AA0B8ResponseVSbtcfC")]
        private static extern void PInvoke_init_7302467F( SwiftIndirectResult swiftIndirectResult,  SafeHandle request,  SafeHandle response,  System.Boolean isCompleted);
        
        
    }
    
    
    public unsafe class ImageProcessingError : ISwiftObject
    {
        static nuint _payloadSize = SwiftObjectHelper<ImageProcessingError>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageProcessingError> _payload = SwiftSafeHandle<ImageProcessingError>.Zero;
        public SwiftSafeHandle<ImageProcessingError> Payload => _payload;
        
        /// <summary>
        /// Gets the 'unknown' case of ImageProcessingError.
        /// </summary>
        public static ImageProcessingError Unknown
        {
            get
            {
                var result = new ImageProcessingError();
                var metadata = PInvoke_getMetadata();
                IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                var indirectResult = new SwiftIndirectResult((void*)buffer);
                PInvoke_Unknown(indirectResult);
                result._payload = new SwiftSafeHandle<ImageProcessingError>(buffer);
                return result;
            }
        }
        
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageProcessingErrorO7unknownyA2CmF")]
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        private static extern void PInvoke_Unknown(SwiftIndirectResult result);
        
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
                
                
                
                var result = PInvoke_description_Get_19A7AB01(self);
                
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
        private static extern Swift.SwiftString.Buffer PInvoke_description_Get_19A7AB01( SwiftSelf self);
        
        public Swift.SwiftString Description
        {
            get => Description_Get();
        }
        
        private unsafe System.IntPtr HashValue_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_hashValue_Get_5604CF80(self);
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageProcessingErrorO9hashValueSivg")]
        private static extern System.IntPtr PInvoke_hashValue_Get_5604CF80( SwiftSelf self);
        
        public System.IntPtr HashValue
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
        
        public unsafe void Hash( Swift.Hasher into)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_hash_5E6AA099(into.Payload, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageProcessingErrorO4hash4intoys6HasherVz_tF")]
        private static extern void PInvoke_hash_5E6AA099( SafeHandle into,  SwiftSelf self);
        
        
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
                
                
                
                PInvoke_container_Get_31ACFE1F(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_container_Get_31ACFE1F( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Container_Set( Swift.Nuke.ImageContainer value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_container_Set_77D299D2(value.Payload, self);
                
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
        private static extern void PInvoke_container_Set_77D299D2( SafeHandle value,  SwiftSelf self);
        
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
                
                
                
                PInvoke_image_Get_7038A786(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_image_Get_7038A786( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_isPreview_Get_3C589F67(self);
                
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
        private static extern System.Boolean PInvoke_isPreview_Get_3C589F67( SwiftSelf self);
        
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
                
                
                
                PInvoke_request_Get_3C94A315(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_request_Get_3C94A315( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Request_Set( Swift.Nuke.ImageRequest value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_request_Set_27DBD654(value.Payload, self);
                
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
        private static extern void PInvoke_request_Set_27DBD654( SafeHandle value,  SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_urlResponse_Get_55BD9E4B(self);
                
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
        private static extern IntPtr PInvoke_urlResponse_Get_55BD9E4B( SwiftSelf self);
        
        private unsafe void UrlResponse_Set( Swift.SwiftOptional<Foundation.NSUrlResponse> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_urlResponse_Set_3C429A30(valueBuffer, self);
                
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
        private static extern void PInvoke_urlResponse_Set_3C429A30( IntPtr valueBuffer,  SwiftSelf self);
        
        public Swift.SwiftOptional<Foundation.NSUrlResponse> UrlResponse
        {
            get => UrlResponse_Get();
            set => UrlResponse_Set(value);
        }
        
        private unsafe Swift.SwiftOptional<Swift.Nuke.ImageResponse.CacheType> CacheType_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_cacheType_Get_6BA65E3E(self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Nuke.ImageResponse.CacheType>>(new IntPtr(&result));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV9cacheTypeAC05CacheE0OSgvg")]
        private static extern IntPtr PInvoke_cacheType_Get_6BA65E3E( SwiftSelf self);
        
        private unsafe void CacheType_Set( Swift.SwiftOptional<Swift.Nuke.ImageResponse.CacheType> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_cacheType_Set_28C4108B(valueBuffer, self);
                
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
        private static extern void PInvoke_cacheType_Set_28C4108B( IntPtr valueBuffer,  SwiftSelf self);
        
        public Swift.SwiftOptional<Swift.Nuke.ImageResponse.CacheType> CacheTypeValue
        {
            get => CacheType_Get();
            set => CacheType_Set(value);
        }
        
        static nuint _payloadSize = SwiftObjectHelper<ImageResponse>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageResponse> _payload = SwiftSafeHandle<ImageResponse>.Zero;
        
        public SwiftSafeHandle<ImageResponse> Payload => _payload;
        
        // Swift structs cannot be compared using .NET's default equality semantics,
        // since Swift's equality is defined by the Equatable protocol.
        // This type does not implement Swift's Equatable protocol.
        public override bool Equals(object? obj)
        {
            throw new InvalidOperationException("Type ImageResponse does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        public override int GetHashCode()
        {
            throw new InvalidOperationException("Type ImageResponse does not implement Swift's Equatable protocol, so GetHashCode() is not supported.");
        }
        
        public static bool operator ==(ImageResponse left, ImageResponse right)
        {
            throw new InvalidOperationException("Type ImageResponse does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        
        public static bool operator !=(ImageResponse left, ImageResponse right)
        {
            throw new InvalidOperationException("Type ImageResponse does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        
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
        
        
        public unsafe class CacheType : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<CacheType>.GetTypeMetadata().Size;
            SwiftSafeHandle<CacheType> _payload = SwiftSafeHandle<CacheType>.Zero;
            public SwiftSafeHandle<CacheType> Payload => _payload;
            
            /// <summary>
            /// Gets the 'memory' case of CacheType.
            /// </summary>
            public static CacheType Memory
            {
                get
                {
                    var result = new CacheType();
                    var metadata = PInvoke_getMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    var indirectResult = new SwiftIndirectResult((void*)buffer);
                    PInvoke_Memory(indirectResult);
                    result._payload = new SwiftSafeHandle<CacheType>(buffer);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV9CacheTypeO6memoryyA2EmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_Memory(SwiftIndirectResult result);
            
            /// <summary>
            /// Gets the 'disk' case of CacheType.
            /// </summary>
            public static CacheType Disk
            {
                get
                {
                    var result = new CacheType();
                    var metadata = PInvoke_getMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    var indirectResult = new SwiftIndirectResult((void*)buffer);
                    PInvoke_Disk(indirectResult);
                    result._payload = new SwiftSafeHandle<CacheType>(buffer);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV9CacheTypeO4diskyA2EmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_Disk(SwiftIndirectResult result);
            
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
                        var metadata = SwiftObjectHelper<CacheType>.GetTypeMetadata();
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
            
            
            private unsafe System.IntPtr HashValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_27AE4744(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV9CacheTypeO9hashValueSivg")]
            private static extern System.IntPtr PInvoke_hashValue_Get_27AE4744( SwiftSelf self);
            
            public System.IntPtr HashValue
            {
                get => HashValue_Get();
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV9CacheTypeOMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new CacheType(handle);
            }
            
            CacheType()
            {
            }
            
            CacheType(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<CacheType>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<CacheType>.GetTypeMetadata();
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
            static CacheType()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IEquatable<CacheType>), "$s4Nuke13ImageResponseV9CacheTypeOSQAAMc"}
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
            
            public unsafe void Hash( Swift.Hasher into)
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_1D02A85A(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV9CacheTypeO4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_1D02A85A( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        public unsafe ImageResponse( Swift.Nuke.ImageContainer container,  Swift.Nuke.ImageRequest request,  Foundation.NSUrlResponse? urlResponse,  Swift.Nuke.ImageResponse.CacheType? cacheType)
        {
            _payload = new SwiftSafeHandle<ImageResponse>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            using var urlResponseSwift = urlResponse is {} urlResponseValue ? SwiftOptional<Foundation.NSUrlResponse>.NewSome(urlResponseValue) : SwiftOptional<Foundation.NSUrlResponse>.NewNone();
            using PayloadBuffer<IntPtr> urlResponseDisposable = urlResponseSwift.PayloadBuffer;
            IntPtr urlResponseBuffer = urlResponseDisposable.Buffer;
            using var cacheTypeSwift = cacheType is {} cacheTypeValue ? SwiftOptional<Swift.Nuke.ImageResponse.CacheType>.NewSome(cacheTypeValue) : SwiftOptional<Swift.Nuke.ImageResponse.CacheType>.NewNone();
            using PayloadBuffer<IntPtr> cacheTypeDisposable = cacheTypeSwift.PayloadBuffer;
            IntPtr cacheTypeBuffer = cacheTypeDisposable.Buffer;
            PInvoke_init_02A692DA(swiftIndirectResult, container.Payload, request.Payload, urlResponseBuffer, cacheTypeBuffer);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV9container7request03urlC09cacheTypeAcA0B9ContainerV_AA0B7RequestVSo13NSURLResponseCSgAC05CacheH0OSgtcfC")]
        private static extern void PInvoke_init_02A692DA( SwiftIndirectResult swiftIndirectResult,  SafeHandle container,  SafeHandle request,  IntPtr urlResponseBuffer,  IntPtr cacheTypeBuffer);
        
        
    }
    
    
    public unsafe class ImageCache : ISwiftObject
    {
        private unsafe System.IntPtr CostLimit_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_costLimit_Get_1D2584B5(self);
                
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
        private static extern System.IntPtr PInvoke_costLimit_Get_1D2584B5( SwiftSelf self);
        
        private unsafe void CostLimit_Set( System.IntPtr value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_costLimit_Set_363F45EC(value, self);
                
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
        private static extern void PInvoke_costLimit_Set_363F45EC( System.IntPtr value,  SwiftSelf self);
        
        public System.IntPtr CostLimit
        {
            get => CostLimit_Get();
            set => CostLimit_Set(value);
        }
        
        private unsafe System.IntPtr CountLimit_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_countLimit_Get_1BD5E497(self);
                
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
        private static extern System.IntPtr PInvoke_countLimit_Get_1BD5E497( SwiftSelf self);
        
        private unsafe void CountLimit_Set( System.IntPtr value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_countLimit_Set_20556784(value, self);
                
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
        private static extern void PInvoke_countLimit_Set_20556784( System.IntPtr value,  SwiftSelf self);
        
        public System.IntPtr CountLimit
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
                
                
                
                var result = PInvoke_ttl_Get_6E89353A(self);
                
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
        private static extern IntPtr PInvoke_ttl_Get_6E89353A( SwiftSelf self);
        
        private unsafe void Ttl_Set( Swift.SwiftOptional<System.Double> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_ttl_Set_002D701B(valueBuffer, self);
                
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
        private static extern void PInvoke_ttl_Set_002D701B( IntPtr valueBuffer,  SwiftSelf self);
        
        public Swift.SwiftOptional<System.Double> Ttl
        {
            get => Ttl_Get();
            set => Ttl_Set(value);
        }
        
        private unsafe System.Double EntryCostLimit_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_entryCostLimit_Get_010C8489(self);
                
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
        private static extern System.Double PInvoke_entryCostLimit_Get_010C8489( SwiftSelf self);
        
        private unsafe void EntryCostLimit_Set( System.Double value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_entryCostLimit_Set_59B9FE9B(value, self);
                
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
        private static extern void PInvoke_entryCostLimit_Set_59B9FE9B( System.Double value,  SwiftSelf self);
        
        public System.Double EntryCostLimit
        {
            get => EntryCostLimit_Get();
            set => EntryCostLimit_Set(value);
        }
        
        private unsafe System.IntPtr TotalCount_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_totalCount_Get_44F9AC75(self);
                
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
        private static extern System.IntPtr PInvoke_totalCount_Get_44F9AC75( SwiftSelf self);
        
        public System.IntPtr TotalCount
        {
            get => TotalCount_Get();
        }
        
        private unsafe System.IntPtr TotalCost_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_totalCost_Get_3A723C86(self);
                
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
        private static extern System.IntPtr PInvoke_totalCost_Get_3A723C86( SwiftSelf self);
        
        public System.IntPtr TotalCost
        {
            get => TotalCost_Get();
        }
        
        private static Swift.Nuke.ImageCache Shared_Get()
        {
            try
            {
                
                
                var result = PInvoke_shared_Get_6E46BDE1();
                
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
        private static extern IntPtr PInvoke_shared_Get_6E46BDE1();
        
        public static Swift.Nuke.ImageCache Shared
        {
            get => Shared_Get();
        }
        
        static nuint _payloadSize = SwiftObjectHelper<ImageCache>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageCache> _payload = SwiftSafeHandle<ImageCache>.Zero;
        
        public SwiftSafeHandle<ImageCache> Payload => _payload;
        
        // Swift classes cannot be compared using .NET's default equality semantics,
        // since Swift's equality is defined by the Equatable protocol.
        // This type does not implement Swift's Equatable protocol.
        public override bool Equals(object? obj)
        {
            throw new InvalidOperationException("Type ImageCache does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        public override int GetHashCode()
        {
            throw new InvalidOperationException("Type ImageCache does not implement Swift's Equatable protocol, so GetHashCode() is not supported.");
        }
        
        public static bool operator ==(ImageCache left, ImageCache right)
        {
            throw new InvalidOperationException("Type ImageCache does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        
        public static bool operator !=(ImageCache left, ImageCache right)
        {
            throw new InvalidOperationException("Type ImageCache does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        
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
                {typeof(ISwiftImageCaching), "$s4Nuke10ImageCacheCAA0B7CachingAAMc"}
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
        
        public unsafe Swift.Nuke.ImageCache Init( System.IntPtr costLimit,  System.IntPtr countLimit)
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageCache>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_init_27211854(swiftIndirectResult, costLimit, countLimit);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageCache>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC9costLimit05countE0ACSi_SitcfC")]
        private static extern void PInvoke_init_27211854( SwiftIndirectResult swiftIndirectResult,  System.IntPtr costLimit,  System.IntPtr countLimit);
        
        
        public static System.IntPtr DefaultCostLimit()
        {
            try
            {
                
                
                var result = PInvoke_defaultCostLimit_60F3AFB5();
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC16defaultCostLimitSiyFZ")]
        private static extern System.IntPtr PInvoke_defaultCostLimit_60F3AFB5();
        
        
        public unsafe void RemoveAll()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_removeAll_28CC0559(self);
                
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
        private static extern void PInvoke_removeAll_28CC0559( SwiftSelf self);
        
        
        public unsafe void Trim( System.IntPtr toCost)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_trim_77352AE2(toCost, self);
                
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
        private static extern void PInvoke_trim_77352AE2( System.IntPtr toCost,  SwiftSelf self);
        
        
    }
    
    
    public unsafe class ImagePipeline : ISwiftObject
    {
        private static Swift.Nuke.ImagePipeline Shared_Get()
        {
            try
            {
                
                
                var result = PInvoke_shared_Get_147EE872();
                
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
        private static extern IntPtr PInvoke_shared_Get_147EE872();
        
        private static void Shared_Set( Swift.Nuke.ImagePipeline value)
        {
            try
            {
                
                
                PInvoke_shared_Set_2BE11CFB(value.Payload);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC6sharedACvsZ")]
        private static extern void PInvoke_shared_Set_2BE11CFB( SafeHandle value);
        
        public static Swift.Nuke.ImagePipeline Shared
        {
            get => Shared_Get();
            set => Shared_Set(value);
        }
        
        private unsafe Swift.Nuke.ImagePipeline.Configuration Configuration_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.Configuration>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_configuration_Get_7776BEEA(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.Configuration>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13configurationAC13ConfigurationVvg")]
        private static extern void PInvoke_configuration_Get_7776BEEA( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Swift.Nuke.ImagePipeline.Configuration ConfigurationValue
        {
            get => Configuration_Get();
        }
        
        private unsafe Swift.Nuke.ImagePipeline.Cache Cache_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.Cache>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_cache_Get_31771539(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.Cache>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5cacheAC5CacheVvg")]
        private static extern void PInvoke_cache_Get_31771539( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Swift.Nuke.ImagePipeline.Cache CacheValue
        {
            get => Cache_Get();
        }
        
        static nuint _payloadSize = SwiftObjectHelper<ImagePipeline>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImagePipeline> _payload = SwiftSafeHandle<ImagePipeline>.Zero;
        
        public SwiftSafeHandle<ImagePipeline> Payload => _payload;
        
        // Swift classes cannot be compared using .NET's default equality semantics,
        // since Swift's equality is defined by the Equatable protocol.
        // This type does not implement Swift's Equatable protocol.
        public override bool Equals(object? obj)
        {
            throw new InvalidOperationException("Type ImagePipeline does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        public override int GetHashCode()
        {
            throw new InvalidOperationException("Type ImagePipeline does not implement Swift's Equatable protocol, so GetHashCode() is not supported.");
        }
        
        public static bool operator ==(ImagePipeline left, ImagePipeline right)
        {
            throw new InvalidOperationException("Type ImagePipeline does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        
        public static bool operator !=(ImagePipeline left, ImagePipeline right)
        {
            throw new InvalidOperationException("Type ImagePipeline does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        
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
            public SwiftSafeHandle<Error> Payload => _payload;
            
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
                    var metadata = PInvoke_getMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    var indirectResult = new SwiftIndirectResult((void*)buffer);
                    PInvoke_DataMissingInCache(indirectResult);
                    result._payload = new SwiftSafeHandle<Error>(buffer);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5ErrorO18dataMissingInCacheyA2EmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_DataMissingInCache(SwiftIndirectResult result);
            
            /// <summary>
            /// Gets the 'dataIsEmpty' case of Error.
            /// </summary>
            public static Error DataIsEmpty
            {
                get
                {
                    var result = new Error();
                    var metadata = PInvoke_getMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    var indirectResult = new SwiftIndirectResult((void*)buffer);
                    PInvoke_DataIsEmpty(indirectResult);
                    result._payload = new SwiftSafeHandle<Error>(buffer);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5ErrorO11dataIsEmptyyA2EmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_DataIsEmpty(SwiftIndirectResult result);
            
            /// <summary>
            /// Gets the 'imageRequestMissing' case of Error.
            /// </summary>
            public static Error ImageRequestMissing
            {
                get
                {
                    var result = new Error();
                    var metadata = PInvoke_getMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    var indirectResult = new SwiftIndirectResult((void*)buffer);
                    PInvoke_ImageRequestMissing(indirectResult);
                    result._payload = new SwiftSafeHandle<Error>(buffer);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5ErrorO19imageRequestMissingyA2EmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_ImageRequestMissing(SwiftIndirectResult result);
            
            /// <summary>
            /// Gets the 'pipelineInvalidated' case of Error.
            /// </summary>
            public static Error PipelineInvalidated
            {
                get
                {
                    var result = new Error();
                    var metadata = PInvoke_getMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    var indirectResult = new SwiftIndirectResult((void*)buffer);
                    PInvoke_PipelineInvalidated(indirectResult);
                    result._payload = new SwiftSafeHandle<Error>(buffer);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5ErrorO19pipelineInvalidatedyA2EmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_PipelineInvalidated(SwiftIndirectResult result);
            
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
                    
                    
                    
                    var result = PInvoke_description_Get_050797E0(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_050797E0( SwiftSelf self);
            
            public Swift.SwiftString Description
            {
                get => Description_Get();
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
        
        
        public unsafe class Cache : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<Cache>.GetTypeMetadata().Size;
            SwiftSafeHandle<Cache> _payload = SwiftSafeHandle<Cache>.Zero;
            
            public SwiftSafeHandle<Cache> Payload => _payload;
            
            // Swift structs cannot be compared using .NET's default equality semantics,
            // since Swift's equality is defined by the Equatable protocol.
            // This type does not implement Swift's Equatable protocol.
            public override bool Equals(object? obj)
            {
                throw new InvalidOperationException("Type Cache does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }
            public override int GetHashCode()
            {
                throw new InvalidOperationException("Type Cache does not implement Swift's Equatable protocol, so GetHashCode() is not supported.");
            }
            
            public static bool operator ==(Cache left, Cache right)
            {
                throw new InvalidOperationException("Type Cache does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }
            
            public static bool operator !=(Cache left, Cache right)
            {
                throw new InvalidOperationException("Type Cache does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheVMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new Cache(handle);
            }
            
            Cache(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<Cache>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<Cache>.GetTypeMetadata();
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
            static Cache()
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
                private unsafe System.IntPtr RawValue_Get()
                {
                    var success = false;
                    _payload.DangerousAddRef(ref success);
                    try
                    {
                        var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                        
                        
                        
                        var result = PInvoke_rawValue_Get_74425596(self);
                        
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
                private static extern System.IntPtr PInvoke_rawValue_Get_74425596( SwiftSelf self);
                
                public System.IntPtr RawValue
                {
                    get => RawValue_Get();
                }
                
                private static unsafe Swift.Nuke.ImagePipeline.Cache.Caches Memory_Get()
                {
                    try
                    {
                        var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.Cache.Caches>();
                        var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                        var swiftIndirectResult = new SwiftIndirectResult(payload);
                        
                        
                        
                        PInvoke_memory_Get_6DFFF282(swiftIndirectResult);
                        
                        return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.Cache.Caches>(new IntPtr(swiftIndirectResult.Value));
                    }
                    
                    finally
                    {
                    }
                    
                }
                
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV6CachesV6memoryAGvgZ")]
                private static extern void PInvoke_memory_Get_6DFFF282( SwiftIndirectResult swiftIndirectResult);
                
                public static Swift.Nuke.ImagePipeline.Cache.Caches Memory
                {
                    get => Memory_Get();
                }
                
                private static unsafe Swift.Nuke.ImagePipeline.Cache.Caches Disk_Get()
                {
                    try
                    {
                        var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.Cache.Caches>();
                        var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                        var swiftIndirectResult = new SwiftIndirectResult(payload);
                        
                        
                        
                        PInvoke_disk_Get_4A3E4B50(swiftIndirectResult);
                        
                        return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.Cache.Caches>(new IntPtr(swiftIndirectResult.Value));
                    }
                    
                    finally
                    {
                    }
                    
                }
                
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV6CachesV4diskAGvgZ")]
                private static extern void PInvoke_disk_Get_4A3E4B50( SwiftIndirectResult swiftIndirectResult);
                
                public static Swift.Nuke.ImagePipeline.Cache.Caches Disk
                {
                    get => Disk_Get();
                }
                
                private static unsafe Swift.Nuke.ImagePipeline.Cache.Caches All_Get()
                {
                    try
                    {
                        var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.Cache.Caches>();
                        var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                        var swiftIndirectResult = new SwiftIndirectResult(payload);
                        
                        
                        
                        PInvoke_all_Get_19613CD1(swiftIndirectResult);
                        
                        return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.Cache.Caches>(new IntPtr(swiftIndirectResult.Value));
                    }
                    
                    finally
                    {
                    }
                    
                }
                
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV6CachesV3allAGvgZ")]
                private static extern void PInvoke_all_Get_19613CD1( SwiftIndirectResult swiftIndirectResult);
                
                public static Swift.Nuke.ImagePipeline.Cache.Caches All
                {
                    get => All_Get();
                }
                
                static nuint _payloadSize = SwiftObjectHelper<Caches>.GetTypeMetadata().Size;
                SwiftSafeHandle<Caches> _payload = SwiftSafeHandle<Caches>.Zero;
                
                public SwiftSafeHandle<Caches> Payload => _payload;
                
                public override bool Equals(object? obj)
                {
                    return obj is Caches other && Swift.Runtime.SwiftEquatable.Equals(this, other);
                }
                public override int GetHashCode()
                {
                    throw new InvalidOperationException("Type Caches does not implement Swift's Hashable protocol, so GetHashCode() is not supported.");
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
                
                
                public unsafe Caches( System.IntPtr rawValue)
                {
                    _payload = new SwiftSafeHandle<Caches>((IntPtr)NativeMemory.Alloc(_payloadSize));
                    var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                    
                    PInvoke_init_410C6A13(swiftIndirectResult, rawValue);
                    
                }
                
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV6CachesV8rawValueAGSi_tcfC")]
                private static extern void PInvoke_init_410C6A13( SwiftIndirectResult swiftIndirectResult,  System.IntPtr rawValue);
                
                
            }
            
            
            public unsafe Swift.Nuke.ImageContainer? CachedImage( Swift.Nuke.ImageRequest _for,  Swift.Nuke.ImagePipeline.Cache.Caches caches)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_cachedImage_4A1131DA(_for.Payload, caches.Payload, self);
                    
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
            private static extern IntPtr PInvoke_cachedImage_4A1131DA( SafeHandle _for,  SafeHandle caches,  SwiftSelf self);
            
            
            public unsafe void StoreCachedImage( Swift.Nuke.ImageContainer arg0,  Swift.Nuke.ImageRequest _for,  Swift.Nuke.ImagePipeline.Cache.Caches caches)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_storeCachedImage_6ADDD916(arg0.Payload, _for.Payload, caches.Payload, self);
                    
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
            private static extern void PInvoke_storeCachedImage_6ADDD916( SafeHandle arg0,  SafeHandle _for,  SafeHandle caches,  SwiftSelf self);
            
            
            public unsafe void RemoveCachedImage( Swift.Nuke.ImageRequest _for,  Swift.Nuke.ImagePipeline.Cache.Caches caches)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_removeCachedImage_0B490BB8(_for.Payload, caches.Payload, self);
                    
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
            private static extern void PInvoke_removeCachedImage_0B490BB8( SafeHandle _for,  SafeHandle caches,  SwiftSelf self);
            
            
            public unsafe System.Boolean ContainsCachedImage( Swift.Nuke.ImageRequest _for,  Swift.Nuke.ImagePipeline.Cache.Caches caches)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_containsCachedImage_07A4906A(_for.Payload, caches.Payload, self);
                    
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
            private static extern System.Boolean PInvoke_containsCachedImage_07A4906A( SafeHandle _for,  SafeHandle caches,  SwiftSelf self);
            
            
            public unsafe Swift.Data? CachedData( Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_cachedData_03AB4F8B(_for.Payload, self);
                    
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
            private static extern IntPtr PInvoke_cachedData_03AB4F8B( SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe void StoreCachedData( Foundation.NSData arg0,  Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    var arg0Swift = Swift.Data.FromNSData(arg0);
                    
                    PInvoke_storeCachedData_64136902(arg0Swift, _for.Payload, self);
                    
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
            private static extern void PInvoke_storeCachedData_64136902( Swift.Data arg0,  SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe System.Boolean ContainsData( Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_containsData_266F2833(_for.Payload, self);
                    
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
            private static extern System.Boolean PInvoke_containsData_266F2833( SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe void RemoveCachedData( Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_removeCachedData_0E69F14A(_for.Payload, self);
                    
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
            private static extern void PInvoke_removeCachedData_0E69F14A( SafeHandle _for,  SwiftSelf self);
            
            
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
                    
                    
                    
                    PInvoke_makeImageCacheKey_3736A094(swiftIndirectResult, _for.Payload, self);
                    
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
            private static extern void PInvoke_makeImageCacheKey_3736A094( SwiftIndirectResult swiftIndirectResult,  SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe string MakeDataCacheKey( Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_makeDataCacheKey_2040B568(_for.Payload, self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_makeDataCacheKey_2040B568( SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe void RemoveAll( Swift.Nuke.ImagePipeline.Cache.Caches caches)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_removeAll_46035BC8(caches.Payload, self);
                    
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
            private static extern void PInvoke_removeAll_46035BC8( SafeHandle caches,  SwiftSelf self);
            
            
        }
        
        
        public unsafe class Configuration : ISwiftObject
        {
            private unsafe Swift.Runtime.ExistentialContainer1 DataLoader_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Runtime.ExistentialContainer1>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_dataLoader_Get_1E0D9388(self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Runtime.ExistentialContainer1>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV10dataLoaderAA11DataLoading_pvg")]
            private static extern Swift.Runtime.ExistentialContainer1 PInvoke_dataLoader_Get_1E0D9388( SwiftSelf self);
            
            private unsafe void DataLoader_Set( Swift.Runtime.ExistentialContainer1 value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_dataLoader_Set_798E920F(value, self);
                    
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
            private static extern void PInvoke_dataLoader_Set_798E920F( Swift.Runtime.ExistentialContainer1 value,  SwiftSelf self);
            
            [global::Swift.UnsupportedSwiftType("Existential type fallback", "any Nuke.DataLoading")]
            public Swift.Runtime.ExistentialContainer1 DataLoader
            {
                get => DataLoader_Get();
                set => DataLoader_Set(value);
            }
            
            private unsafe Func<Swift.Nuke.ImageEncodingContext, Swift.Runtime.ExistentialContainer1> MakeImageEncoder_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_makeImageEncoder_Get_183F8030(self);
                    
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
            private static extern SwiftClosureData PInvoke_makeImageEncoder_Get_183F8030( SwiftSelf self);
            
            private static unsafe readonly delegate* unmanaged[Swift]<void*, SwiftSelf, Swift.Runtime.ExistentialContainer1> s_makeImageEncoder_Set_value_7B5CDCB3_Callback = &makeImageEncoder_Set_value_7B5CDCB3_Callback;
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
            private static Swift.Runtime.ExistentialContainer1 makeImageEncoder_Set_value_7B5CDCB3_Callback(void* arg0, SwiftSelf context)
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
                    var valueClosure = new SwiftClosureData((IntPtr)s_makeImageEncoder_Set_value_7B5CDCB3_Callback, GCHandle.ToIntPtr(valueHandle));
                    
                    PInvoke_makeImageEncoder_Set_7B5CDCB3(valueClosure, self);
                    
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
            private static extern void PInvoke_makeImageEncoder_Set_7B5CDCB3( SwiftClosureData value,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_isDecompressionEnabled_Get_54023B17(self);
                    
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
            private static extern System.Boolean PInvoke_isDecompressionEnabled_Get_54023B17( SwiftSelf self);
            
            private unsafe void IsDecompressionEnabled_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isDecompressionEnabled_Set_70A0D944(value, self);
                    
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
            private static extern void PInvoke_isDecompressionEnabled_Set_70A0D944( System.Boolean value,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_isUsingPrepareForDisplay_Get_2DCB0B8C(self);
                    
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
            private static extern System.Boolean PInvoke_isUsingPrepareForDisplay_Get_2DCB0B8C( SwiftSelf self);
            
            private unsafe void IsUsingPrepareForDisplay_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isUsingPrepareForDisplay_Set_1C57EBCD(value, self);
                    
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
            private static extern void PInvoke_isUsingPrepareForDisplay_Set_1C57EBCD( System.Boolean value,  SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_dataCachePolicy_Get_012F396D(swiftIndirectResult, self);
                    
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
            private static extern void PInvoke_dataCachePolicy_Get_012F396D( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void DataCachePolicy_Set( Swift.Nuke.ImagePipeline.DataCachePolicy value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_dataCachePolicy_Set_36C2AF62(value.Payload, self);
                    
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
            private static extern void PInvoke_dataCachePolicy_Set_36C2AF62( SafeHandle value,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_isTaskCoalescingEnabled_Get_6ACB8FBE(self);
                    
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
            private static extern System.Boolean PInvoke_isTaskCoalescingEnabled_Get_6ACB8FBE( SwiftSelf self);
            
            private unsafe void IsTaskCoalescingEnabled_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isTaskCoalescingEnabled_Set_4FD0871E(value, self);
                    
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
            private static extern void PInvoke_isTaskCoalescingEnabled_Set_4FD0871E( System.Boolean value,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_isRateLimiterEnabled_Get_3D9A89AC(self);
                    
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
            private static extern System.Boolean PInvoke_isRateLimiterEnabled_Get_3D9A89AC( SwiftSelf self);
            
            private unsafe void IsRateLimiterEnabled_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isRateLimiterEnabled_Set_505E9A91(value, self);
                    
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
            private static extern void PInvoke_isRateLimiterEnabled_Set_505E9A91( System.Boolean value,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_isProgressiveDecodingEnabled_Get_5F126A93(self);
                    
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
            private static extern System.Boolean PInvoke_isProgressiveDecodingEnabled_Get_5F126A93( SwiftSelf self);
            
            private unsafe void IsProgressiveDecodingEnabled_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isProgressiveDecodingEnabled_Set_61EA737D(value, self);
                    
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
            private static extern void PInvoke_isProgressiveDecodingEnabled_Set_61EA737D( System.Boolean value,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_isStoringPreviewsInMemoryCache_Get_35F3BE96(self);
                    
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
            private static extern System.Boolean PInvoke_isStoringPreviewsInMemoryCache_Get_35F3BE96( SwiftSelf self);
            
            private unsafe void IsStoringPreviewsInMemoryCache_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isStoringPreviewsInMemoryCache_Set_2F917E9C(value, self);
                    
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
            private static extern void PInvoke_isStoringPreviewsInMemoryCache_Set_2F917E9C( System.Boolean value,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_isResumableDataEnabled_Get_6E58EFF2(self);
                    
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
            private static extern System.Boolean PInvoke_isResumableDataEnabled_Get_6E58EFF2( SwiftSelf self);
            
            private unsafe void IsResumableDataEnabled_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isResumableDataEnabled_Set_5F7E9883(value, self);
                    
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
            private static extern void PInvoke_isResumableDataEnabled_Set_5F7E9883( System.Boolean value,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_isLocalResourcesSupportEnabled_Get_3AC31DE3(self);
                    
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
            private static extern System.Boolean PInvoke_isLocalResourcesSupportEnabled_Get_3AC31DE3( SwiftSelf self);
            
            private unsafe void IsLocalResourcesSupportEnabled_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isLocalResourcesSupportEnabled_Set_64F7221B(value, self);
                    
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
            private static extern void PInvoke_isLocalResourcesSupportEnabled_Set_64F7221B( System.Boolean value,  SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_callbackQueue_Get_38D2FB67(swiftIndirectResult, self);
                    
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
            private static extern void PInvoke_callbackQueue_Get_38D2FB67( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void CallbackQueue_Set( Swift.DispatchQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_callbackQueue_Set_57D35560(value.Payload, self);
                    
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
            private static extern void PInvoke_callbackQueue_Set_57D35560( SafeHandle value,  SwiftSelf self);
            
            public Swift.DispatchQueue CallbackQueue
            {
                get => CallbackQueue_Get();
                set => CallbackQueue_Set(value);
            }
            
            private static System.Boolean IsSignpostLoggingEnabled_Get()
            {
                try
                {
                    
                    
                    var result = PInvoke_isSignpostLoggingEnabled_Get_56E8B0B0();
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV24isSignpostLoggingEnabledSbvgZ")]
            private static extern System.Boolean PInvoke_isSignpostLoggingEnabled_Get_56E8B0B0();
            
            private static void IsSignpostLoggingEnabled_Set( System.Boolean value)
            {
                try
                {
                    
                    
                    PInvoke_isSignpostLoggingEnabled_Set_3E27615E(value);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV24isSignpostLoggingEnabledSbvsZ")]
            private static extern void PInvoke_isSignpostLoggingEnabled_Set_3E27615E( System.Boolean value);
            
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
                    
                    
                    
                    PInvoke_dataLoadingQueue_Get_12C9339E(swiftIndirectResult, self);
                    
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
            private static extern void PInvoke_dataLoadingQueue_Get_12C9339E( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void DataLoadingQueue_Set( Foundation.NSOperationQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr valueHandle = value?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_dataLoadingQueue_Set_6D88E93B(valueHandle, self);
                    
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
            private static extern void PInvoke_dataLoadingQueue_Set_6D88E93B( IntPtr value,  SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_dataCachingQueue_Get_7D5EE6DB(swiftIndirectResult, self);
                    
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
            private static extern void PInvoke_dataCachingQueue_Get_7D5EE6DB( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void DataCachingQueue_Set( Foundation.NSOperationQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr valueHandle = value?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_dataCachingQueue_Set_183D60E3(valueHandle, self);
                    
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
            private static extern void PInvoke_dataCachingQueue_Set_183D60E3( IntPtr value,  SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_imageDecodingQueue_Get_73A3055A(swiftIndirectResult, self);
                    
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
            private static extern void PInvoke_imageDecodingQueue_Get_73A3055A( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void ImageDecodingQueue_Set( Foundation.NSOperationQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr valueHandle = value?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_imageDecodingQueue_Set_193F7CA7(valueHandle, self);
                    
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
            private static extern void PInvoke_imageDecodingQueue_Set_193F7CA7( IntPtr value,  SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_imageEncodingQueue_Get_6804F9F1(swiftIndirectResult, self);
                    
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
            private static extern void PInvoke_imageEncodingQueue_Get_6804F9F1( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void ImageEncodingQueue_Set( Foundation.NSOperationQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr valueHandle = value?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_imageEncodingQueue_Set_46517129(valueHandle, self);
                    
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
            private static extern void PInvoke_imageEncodingQueue_Set_46517129( IntPtr value,  SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_imageProcessingQueue_Get_0ED5DD49(swiftIndirectResult, self);
                    
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
            private static extern void PInvoke_imageProcessingQueue_Get_0ED5DD49( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void ImageProcessingQueue_Set( Foundation.NSOperationQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr valueHandle = value?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_imageProcessingQueue_Set_170C3EA2(valueHandle, self);
                    
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
            private static extern void PInvoke_imageProcessingQueue_Set_170C3EA2( IntPtr value,  SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_imageDecompressingQueue_Get_5E3ABAAB(swiftIndirectResult, self);
                    
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
            private static extern void PInvoke_imageDecompressingQueue_Get_5E3ABAAB( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void ImageDecompressingQueue_Set( Foundation.NSOperationQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr valueHandle = value?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_imageDecompressingQueue_Set_4E7540D7(valueHandle, self);
                    
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
            private static extern void PInvoke_imageDecompressingQueue_Set_4E7540D7( IntPtr value,  SwiftSelf self);
            
            public Foundation.NSOperationQueue ImageDecompressingQueue
            {
                get => ImageDecompressingQueue_Get();
                set => ImageDecompressingQueue_Set(value);
            }
            
            private static unsafe Swift.Nuke.ImagePipeline.Configuration WithURLCache_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.Configuration>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_withURLCache_Get_6442D513(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.Configuration>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV12withURLCacheAEvgZ")]
            private static extern void PInvoke_withURLCache_Get_6442D513( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImagePipeline.Configuration WithURLCache
            {
                get => WithURLCache_Get();
            }
            
            private static unsafe Swift.Nuke.ImagePipeline.Configuration WithDataCache_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.Configuration>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_withDataCache_Get_00A865B7(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.Configuration>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV13withDataCacheAEvgZ")]
            private static extern void PInvoke_withDataCache_Get_00A865B7( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImagePipeline.Configuration WithDataCache
            {
                get => WithDataCache_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<Configuration>.GetTypeMetadata().Size;
            SwiftSafeHandle<Configuration> _payload = SwiftSafeHandle<Configuration>.Zero;
            
            public SwiftSafeHandle<Configuration> Payload => _payload;
            
            // Swift structs cannot be compared using .NET's default equality semantics,
            // since Swift's equality is defined by the Equatable protocol.
            // This type does not implement Swift's Equatable protocol.
            public override bool Equals(object? obj)
            {
                throw new InvalidOperationException("Type Configuration does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }
            public override int GetHashCode()
            {
                throw new InvalidOperationException("Type Configuration does not implement Swift's Equatable protocol, so GetHashCode() is not supported.");
            }
            
            public static bool operator ==(Configuration left, Configuration right)
            {
                throw new InvalidOperationException("Type Configuration does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }
            
            public static bool operator !=(Configuration left, Configuration right)
            {
                throw new InvalidOperationException("Type Configuration does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationVMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new Configuration(handle);
            }
            
            Configuration(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<Configuration>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<Configuration>.GetTypeMetadata();
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
            static Configuration()
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
            
            
            public unsafe Configuration( Swift.Runtime.ExistentialContainer1 dataLoader)
            {
                _payload = new SwiftSafeHandle<Configuration>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_75F709A2(swiftIndirectResult, dataLoader);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV10dataLoaderAeA11DataLoading_p_tcfC")]
            private static extern void PInvoke_init_75F709A2( SwiftIndirectResult swiftIndirectResult,  Swift.Runtime.ExistentialContainer1 dataLoader);
            
            
            public static unsafe Swift.Nuke.ImagePipeline.Configuration WithDataCacheMethod( string name,  System.IntPtr? sizeLimit)
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.Configuration>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    using var nameSwift = new SwiftString(name);
                    using PayloadBuffer<SwiftString.Buffer> nameDisposable = nameSwift.PayloadBuffer;
                    using var sizeLimitSwift = sizeLimit is {} sizeLimitValue ? SwiftOptional<System.IntPtr>.NewSome(sizeLimitValue) : SwiftOptional<System.IntPtr>.NewNone();
                    using PayloadBuffer<IntPtr> sizeLimitDisposable = sizeLimitSwift.PayloadBuffer;
                    IntPtr sizeLimitBuffer = sizeLimitDisposable.Buffer;
                    
                    PInvoke_withDataCache_47D941A9(swiftIndirectResult, nameDisposable.Buffer, sizeLimitBuffer);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.Configuration>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV13withDataCache4name9sizeLimitAESS_SiSgtFZ")]
            private static extern void PInvoke_withDataCache_47D941A9( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer name,  IntPtr sizeLimitBuffer);
            
            
        }
        
        
        public unsafe class DataCachePolicy : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<DataCachePolicy>.GetTypeMetadata().Size;
            SwiftSafeHandle<DataCachePolicy> _payload = SwiftSafeHandle<DataCachePolicy>.Zero;
            public SwiftSafeHandle<DataCachePolicy> Payload => _payload;
            
            /// <summary>
            /// Gets the 'automatic' case of DataCachePolicy.
            /// </summary>
            public static DataCachePolicy Automatic
            {
                get
                {
                    var result = new DataCachePolicy();
                    var metadata = PInvoke_getMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    var indirectResult = new SwiftIndirectResult((void*)buffer);
                    PInvoke_Automatic(indirectResult);
                    result._payload = new SwiftSafeHandle<DataCachePolicy>(buffer);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC15DataCachePolicyO9automaticyA2EmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_Automatic(SwiftIndirectResult result);
            
            /// <summary>
            /// Gets the 'storeOriginalData' case of DataCachePolicy.
            /// </summary>
            public static DataCachePolicy StoreOriginalData
            {
                get
                {
                    var result = new DataCachePolicy();
                    var metadata = PInvoke_getMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    var indirectResult = new SwiftIndirectResult((void*)buffer);
                    PInvoke_StoreOriginalData(indirectResult);
                    result._payload = new SwiftSafeHandle<DataCachePolicy>(buffer);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC15DataCachePolicyO013storeOriginalD0yA2EmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_StoreOriginalData(SwiftIndirectResult result);
            
            /// <summary>
            /// Gets the 'storeEncodedImages' case of DataCachePolicy.
            /// </summary>
            public static DataCachePolicy StoreEncodedImages
            {
                get
                {
                    var result = new DataCachePolicy();
                    var metadata = PInvoke_getMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    var indirectResult = new SwiftIndirectResult((void*)buffer);
                    PInvoke_StoreEncodedImages(indirectResult);
                    result._payload = new SwiftSafeHandle<DataCachePolicy>(buffer);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC15DataCachePolicyO18storeEncodedImagesyA2EmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_StoreEncodedImages(SwiftIndirectResult result);
            
            /// <summary>
            /// Gets the 'storeAll' case of DataCachePolicy.
            /// </summary>
            public static DataCachePolicy StoreAll
            {
                get
                {
                    var result = new DataCachePolicy();
                    var metadata = PInvoke_getMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    var indirectResult = new SwiftIndirectResult((void*)buffer);
                    PInvoke_StoreAll(indirectResult);
                    result._payload = new SwiftSafeHandle<DataCachePolicy>(buffer);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC15DataCachePolicyO8storeAllyA2EmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_StoreAll(SwiftIndirectResult result);
            
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
            
            
            private unsafe System.IntPtr HashValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_5AC60256(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC15DataCachePolicyO9hashValueSivg")]
            private static extern System.IntPtr PInvoke_hashValue_Get_5AC60256( SwiftSelf self);
            
            public System.IntPtr HashValue
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
            
            public unsafe void Hash( Swift.Hasher into)
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_37FD93F6(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC15DataCachePolicyO4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_37FD93F6( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        
        
        public unsafe void Invalidate()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_invalidate_236BECC7(self);
                
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
        private static extern void PInvoke_invalidate_236BECC7( SwiftSelf self);
        
        
        public unsafe Swift.Nuke.ImageTask ImageTask( Foundation.NSUrl with)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                using var withSwift = Swift.URL.FromNSUrl(with);
                
                var result = PInvoke_imageTask_1C312DA5(withSwift.Payload, self);
                
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
        private static extern IntPtr PInvoke_imageTask_1C312DA5( SafeHandle with,  SwiftSelf self);
        
        
        public unsafe Swift.Nuke.ImageTask ImageTask( Swift.Nuke.ImageRequest with)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_imageTask_5EEB291B(with.Payload, self);
                
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
        private static extern IntPtr PInvoke_imageTask_5EEB291B( SafeHandle with,  SwiftSelf self);
        
        
                private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> s_imageCallback_37F7BB53 = &imageOnComplete_37F7BB53;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void imageOnComplete_37F7BB53(IntPtr rawResult, IntPtr task)
        {
            GCHandle handle = GCHandle.FromIntPtr(task);
            try
            {
                var result = ObjCRuntime.Runtime.GetNSObject<UIKit.UIImage>(rawResult);
                
                
                
                
                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<UIKit.UIImage> holderTcs)
                {
                    // Free copy buffer memory for non-frozen params and release retained self
                    // Note: Original params in holder keep internal storage alive
                    for (int i = 1; i < holder.Length; i++)
                    {
                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                        {
                            // Release the extra retain added for async safety
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
                else if (handle.Target is TaskCompletionSource<UIKit.UIImage> directTcs)
                {
                    directTcs.TrySetResult(result);
                }
            }
            finally
            {
                handle.Free();
            }
        }

        private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> s_imageErrorCallback_37F7BB53 = &imageOnError_37F7BB53;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void imageOnError_37F7BB53(IntPtr errorMessagePtr, IntPtr task)
        {
            GCHandle handle = GCHandle.FromIntPtr(task);
            try
            {
                var errorMessage = Marshal.PtrToStringUTF8(errorMessagePtr) ?? "Unknown Swift error";
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
        public unsafe Task<UIKit.UIImage> Image( Foundation.NSUrl _for)
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
                
                PInvoke_image_37F7BB53(s_imageCallback_37F7BB53, s_imageErrorCallback_37F7BB53, GCHandle.ToIntPtr(handle), _forSwift.Payload);
                
                return task.Task;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("SwiftBindings", EntryPoint = "$s4Nuke13ImagePipelineC5image3forSo7UIImageC10Foundation3URLV_tYaKF_async")]
        private static extern void PInvoke_image_37F7BB53( void* s_imageCallback_37F7BB53,  void* s_imageErrorCallback_37F7BB53,  IntPtr handle,  SafeHandle _for);
        
        
                private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> s_imageCallback_03BA14BF = &imageOnComplete_03BA14BF;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void imageOnComplete_03BA14BF(IntPtr rawResult, IntPtr task)
        {
            GCHandle handle = GCHandle.FromIntPtr(task);
            try
            {
                var result = ObjCRuntime.Runtime.GetNSObject<UIKit.UIImage>(rawResult);
                
                
                
                
                // Handle both cases: direct TCS or object[] holder (with copy buffer pointers)
                if (handle.Target is object[] holder && holder[0] is TaskCompletionSource<UIKit.UIImage> holderTcs)
                {
                    // Free copy buffer memory for non-frozen params and release retained self
                    // Note: Original params in holder keep internal storage alive
                    for (int i = 1; i < holder.Length; i++)
                    {
                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                        {
                            // Release the extra retain added for async safety
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
                else if (handle.Target is TaskCompletionSource<UIKit.UIImage> directTcs)
                {
                    directTcs.TrySetResult(result);
                }
            }
            finally
            {
                handle.Free();
            }
        }

        private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> s_imageErrorCallback_03BA14BF = &imageOnError_03BA14BF;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void imageOnError_03BA14BF(IntPtr errorMessagePtr, IntPtr task)
        {
            GCHandle handle = GCHandle.FromIntPtr(task);
            try
            {
                var errorMessage = Marshal.PtrToStringUTF8(errorMessagePtr) ?? "Unknown Swift error";
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
        public unsafe Task<UIKit.UIImage> Image( Swift.Nuke.ImageRequest _for)
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
                
                
                PInvoke_image_03BA14BF(s_imageCallback_03BA14BF, s_imageErrorCallback_03BA14BF, GCHandle.ToIntPtr(handle), _forHandle);
                
                return task.Task;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("SwiftBindings", EntryPoint = "$s4Nuke13ImagePipelineC5image3forSo7UIImageCAA0B7RequestV_tYaKF_async")]
        private static extern void PInvoke_image_03BA14BF( void* s_imageCallback_03BA14BF,  void* s_imageErrorCallback_03BA14BF,  IntPtr handle,  IntPtr _for);
        
        
                private static unsafe delegate* unmanaged[Cdecl]<Swift.Data, IntPtr, IntPtr, void> s_dataCallback_7B6EAAFD = &dataOnComplete_7B6EAAFD;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void dataOnComplete_7B6EAAFD(Swift.Data rawItem0, IntPtr rawItem1, IntPtr task)
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

        private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> s_dataErrorCallback_7B6EAAFD = &dataOnError_7B6EAAFD;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void dataOnError_7B6EAAFD(IntPtr errorMessagePtr, IntPtr task)
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
        public unsafe Task<(Swift.Data, Swift.SwiftOptional<Foundation.NSUrlResponse>)> Data( Swift.Nuke.ImageRequest _for)
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
                
                
                PInvoke_data_7B6EAAFD(s_dataCallback_7B6EAAFD, s_dataErrorCallback_7B6EAAFD, GCHandle.ToIntPtr(handle), _forHandle);
                
                return task.Task;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("SwiftBindings", EntryPoint = "$s4Nuke13ImagePipelineC4data3for10Foundation4DataV_So13NSURLResponseCSgtAA0B7RequestV_tYaKF_async")]
        private static extern void PInvoke_data_7B6EAAFD( void* s_dataCallback_7B6EAAFD,  void* s_dataErrorCallback_7B6EAAFD,  IntPtr handle,  IntPtr _for);
        
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, SwiftSelf, void> s_loadImage_completion_3AD159F1_Callback = &loadImage_completion_3AD159F1_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void loadImage_completion_3AD159F1_Callback(void* arg0, SwiftSelf context)
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
                var completionClosure = new SwiftClosureData((IntPtr)s_loadImage_completion_3AD159F1_Callback, GCHandle.ToIntPtr(completionHandle));
                using var withSwift = Swift.URL.FromNSUrl(with);
                
                var result = PInvoke_loadImage_3AD159F1(withSwift.Payload, completionClosure, self);
                
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
        private static extern IntPtr PInvoke_loadImage_3AD159F1( SafeHandle with,  SwiftClosureData completion,  SwiftSelf self);
        
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, SwiftSelf, void> s_loadImage_completion_476AA4AA_Callback = &loadImage_completion_476AA4AA_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void loadImage_completion_476AA4AA_Callback(void* arg0, SwiftSelf context)
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
                var completionClosure = new SwiftClosureData((IntPtr)s_loadImage_completion_476AA4AA_Callback, GCHandle.ToIntPtr(completionHandle));
                
                var result = PInvoke_loadImage_476AA4AA(with.Payload, completionClosure, self);
                
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
        private static extern IntPtr PInvoke_loadImage_476AA4AA( SafeHandle with,  SwiftClosureData completion,  SwiftSelf self);
        
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, long, long, SwiftSelf, void> s_loadImage_progress_0AF02E86_Callback = &loadImage_progress_0AF02E86_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void loadImage_progress_0AF02E86_Callback(void* arg0, long arg1, long arg2, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Action<Swift.SwiftOptional<Swift.Nuke.ImageResponse>, System.Int64, System.Int64>>(new IntPtr(context.Value));
            del(SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Nuke.ImageResponse>>(new IntPtr(arg0)), arg1, arg2);
        }
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, SwiftSelf, void> s_loadImage_completion_0AF02E86_Callback = &loadImage_completion_0AF02E86_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void loadImage_completion_0AF02E86_Callback(void* arg0, SwiftSelf context)
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
                    progressClosure = new SwiftClosureData((IntPtr)s_loadImage_progress_0AF02E86_Callback, GCHandle.ToIntPtr(progressHandle));
                }
                else
                {
                    progressClosure = default; // Zero-initialized = nil in Swift
                }
                completionHandle = GCHandle.Alloc(completion);
                var completionClosure = new SwiftClosureData((IntPtr)s_loadImage_completion_0AF02E86_Callback, GCHandle.ToIntPtr(completionHandle));
                using var queueSwift = queue is {} queueValue ? SwiftOptional<Swift.DispatchQueue>.NewSome(queueValue) : SwiftOptional<Swift.DispatchQueue>.NewNone();
                using PayloadBuffer<IntPtr> queueDisposable = queueSwift.PayloadBuffer;
                IntPtr queueBuffer = queueDisposable.Buffer;
                
                var result = PInvoke_loadImage_0AF02E86(with.Payload, queueBuffer, progressClosure, completionClosure, self);
                
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
        private static extern IntPtr PInvoke_loadImage_0AF02E86( SafeHandle with,  IntPtr queueBuffer,  SwiftClosureData progress,  SwiftClosureData completion,  SwiftSelf self);
        
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, SwiftSelf, void> s_loadData_completion_37522276_Callback = &loadData_completion_37522276_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void loadData_completion_37522276_Callback(void* arg0, SwiftSelf context)
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
                var completionClosure = new SwiftClosureData((IntPtr)s_loadData_completion_37522276_Callback, GCHandle.ToIntPtr(completionHandle));
                
                var result = PInvoke_loadData_37522276(with.Payload, completionClosure, self);
                
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
        private static extern IntPtr PInvoke_loadData_37522276( SafeHandle with,  SwiftClosureData completion,  SwiftSelf self);
        
        
        private static unsafe readonly delegate* unmanaged[Swift]<long, long, SwiftSelf, void> s_loadData_progress_31128A12_Callback = &loadData_progress_31128A12_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void loadData_progress_31128A12_Callback(long arg0, long arg1, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Action<System.Int64, System.Int64>>(new IntPtr(context.Value));
            del(arg0, arg1);
        }
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, SwiftSelf, void> s_loadData_completion_31128A12_Callback = &loadData_completion_31128A12_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void loadData_completion_31128A12_Callback(void* arg0, SwiftSelf context)
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
                    progressClosure = new SwiftClosureData((IntPtr)s_loadData_progress_31128A12_Callback, GCHandle.ToIntPtr(progressHandle));
                }
                else
                {
                    progressClosure = default; // Zero-initialized = nil in Swift
                }
                completionHandle = GCHandle.Alloc(completion);
                var completionClosure = new SwiftClosureData((IntPtr)s_loadData_completion_31128A12_Callback, GCHandle.ToIntPtr(completionHandle));
                using var queueSwift = queue is {} queueValue ? SwiftOptional<Swift.DispatchQueue>.NewSome(queueValue) : SwiftOptional<Swift.DispatchQueue>.NewNone();
                using PayloadBuffer<IntPtr> queueDisposable = queueSwift.PayloadBuffer;
                IntPtr queueBuffer = queueDisposable.Buffer;
                
                var result = PInvoke_loadData_31128A12(with.Payload, queueBuffer, progressClosure, completionClosure, self);
                
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
        private static extern IntPtr PInvoke_loadData_31128A12( SafeHandle with,  IntPtr queueBuffer,  SwiftClosureData progress,  SwiftClosureData completion,  SwiftSelf self);
        
        
        
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, SwiftSelf, void> s_loadData_completion_19A1B4DA_Callback = &loadData_completion_19A1B4DA_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void loadData_completion_19A1B4DA_Callback(void* arg0, SwiftSelf context)
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
                var completionClosure = new SwiftClosureData((IntPtr)s_loadData_completion_19A1B4DA_Callback, GCHandle.ToIntPtr(completionHandle));
                using var withSwift = Swift.URL.FromNSUrl(with);
                
                var result = PInvoke_loadData_19A1B4DA(withSwift.Payload, completionClosure, self);
                
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
        private static extern IntPtr PInvoke_loadData_19A1B4DA( SafeHandle with,  SwiftClosureData completion,  SwiftSelf self);
        
        
                private static unsafe delegate* unmanaged[Cdecl]<Swift.Data, IntPtr, IntPtr, void> s_dataCallback_0F72171F = &dataOnComplete_0F72171F;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void dataOnComplete_0F72171F(Swift.Data rawItem0, IntPtr rawItem1, IntPtr task)
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

        private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> s_dataErrorCallback_0F72171F = &dataOnError_0F72171F;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void dataOnError_0F72171F(IntPtr errorMessagePtr, IntPtr task)
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
        public unsafe Task<(Swift.Data, Swift.SwiftOptional<Foundation.NSUrlResponse>)> Data( Foundation.NSUrl _for)
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
                
                PInvoke_data_0F72171F(s_dataCallback_0F72171F, s_dataErrorCallback_0F72171F, GCHandle.ToIntPtr(handle), _forSwift.Payload);
                
                return task.Task;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("SwiftBindings", EntryPoint = "$s4Nuke13ImagePipelineC4data3for10Foundation4DataV_So13NSURLResponseCSgtAF3URLV_tYaKF_async")]
        private static extern void PInvoke_data_0F72171F( void* s_dataCallback_0F72171F,  void* s_dataErrorCallback_0F72171F,  IntPtr handle,  SafeHandle _for);
        
        
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
                
                
                
                PInvoke_session_Get_5E6E561F(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_session_Get_5E6E561F( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_prefersIncrementalDelivery_Get_3E82AA31(self);
                
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
        private static extern System.Boolean PInvoke_prefersIncrementalDelivery_Get_3E82AA31( SwiftSelf self);
        
        private unsafe void PrefersIncrementalDelivery_Set( System.Boolean value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_prefersIncrementalDelivery_Set_0A5323B0(value, self);
                
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
        private static extern void PInvoke_prefersIncrementalDelivery_Set_0A5323B0( System.Boolean value,  SwiftSelf self);
        
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
                
                
                
                PInvoke_defaultConfiguration_Get_05A1E0E9(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Foundation.NSUrlSessionConfiguration>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10DataLoaderC20defaultConfigurationSo012NSURLSessionE0CvgZ")]
        private static extern void PInvoke_defaultConfiguration_Get_05A1E0E9( SwiftIndirectResult swiftIndirectResult);
        
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
                
                
                
                PInvoke_sharedUrlCache_Get_5BD2C257(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Foundation.NSUrlCache>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10DataLoaderC14sharedUrlCacheSo10NSURLCacheCvgZ")]
        private static extern void PInvoke_sharedUrlCache_Get_5BD2C257( SwiftIndirectResult swiftIndirectResult);
        
        public static Foundation.NSUrlCache SharedUrlCache
        {
            get => SharedUrlCache_Get();
        }
        
        static nuint _payloadSize = SwiftObjectHelper<DataLoader>.GetTypeMetadata().Size;
        SwiftSafeHandle<DataLoader> _payload = SwiftSafeHandle<DataLoader>.Zero;
        
        public SwiftSafeHandle<DataLoader> Payload => _payload;
        
        // Swift classes cannot be compared using .NET's default equality semantics,
        // since Swift's equality is defined by the Equatable protocol.
        // This type does not implement Swift's Equatable protocol.
        public override bool Equals(object? obj)
        {
            throw new InvalidOperationException("Type DataLoader does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        public override int GetHashCode()
        {
            throw new InvalidOperationException("Type DataLoader does not implement Swift's Equatable protocol, so GetHashCode() is not supported.");
        }
        
        public static bool operator ==(DataLoader left, DataLoader right)
        {
            throw new InvalidOperationException("Type DataLoader does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        
        public static bool operator !=(DataLoader left, DataLoader right)
        {
            throw new InvalidOperationException("Type DataLoader does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        
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
                {typeof(ISwiftDataLoading), "$s4Nuke10DataLoaderCAA0B7LoadingAAMc"}
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
            public SwiftSafeHandle<Error> Payload => _payload;
            
            /// <summary>
            /// Creates the 'statusCodeUnacceptable' case of Error.
            /// </summary>
            public static Error StatusCodeUnacceptable(System.IntPtr value0)
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
            private static extern void PInvoke_StatusCodeUnacceptable(SwiftIndirectResult result, System.IntPtr value0);
            
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
            public unsafe bool TryGetStatusCodeUnacceptable([MaybeNullWhen(false)] out System.IntPtr value)
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
                value = SwiftMarshal.MarshalFromSwift<System.IntPtr>(new IntPtr(enumCopy));
                return true;
            }
            
            
            private unsafe Swift.SwiftString Description_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_185711FE(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_185711FE( SwiftSelf self);
            
            public Swift.SwiftString Description
            {
                get => Description_Get();
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
        
        
        
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, void*, SwiftSelf, void> s_loadData_didReceiveData_51ADCE1E_Callback = &loadData_didReceiveData_51ADCE1E_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void loadData_didReceiveData_51ADCE1E_Callback(void* arg0, void* arg1, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Action<Swift.Data, Foundation.NSUrlResponse>>(new IntPtr(context.Value));
            del(SwiftMarshal.MarshalFromSwift<Swift.Data>(new IntPtr(arg0)), SwiftMarshal.MarshalFromSwift<Foundation.NSUrlResponse>(new IntPtr(arg1)));
        }
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, SwiftSelf, void> s_loadData_completion_51ADCE1E_Callback = &loadData_completion_51ADCE1E_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void loadData_completion_51ADCE1E_Callback(void* arg0, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Action<Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1>>>(new IntPtr(context.Value));
            del(SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1>>(new IntPtr(arg0)));
        }
        
        [global::Swift.UnsupportedSwiftType("Existential type fallback", "any Nuke.Cancellable")]
        public unsafe Swift.Runtime.ExistentialContainer1 LoadData( Swift.URLRequest with,  Action<Swift.Data, Foundation.NSUrlResponse> didReceiveData,  Action<Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1>> completion)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            GCHandle didReceiveDataHandle = default;
            GCHandle completionHandle = default;
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Runtime.ExistentialContainer1>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                didReceiveDataHandle = GCHandle.Alloc(didReceiveData);
                var didReceiveDataClosure = new SwiftClosureData((IntPtr)s_loadData_didReceiveData_51ADCE1E_Callback, GCHandle.ToIntPtr(didReceiveDataHandle));
                completionHandle = GCHandle.Alloc(completion);
                var completionClosure = new SwiftClosureData((IntPtr)s_loadData_completion_51ADCE1E_Callback, GCHandle.ToIntPtr(completionHandle));
                
                PInvoke_loadData_51ADCE1E(with.Payload, didReceiveDataClosure, completionClosure, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Runtime.ExistentialContainer1>(new IntPtr(swiftIndirectResult.Value));
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
        private static extern Swift.Runtime.ExistentialContainer1 PInvoke_loadData_51ADCE1E( SafeHandle with,  SwiftClosureData didReceiveData,  SwiftClosureData completion,  SwiftSelf self);
        
        
    }
    
    
    public interface ISwiftImageEncoding
    {
        Swift.SwiftOptional<Swift.Data> encode(UIKit.UIImage arg0);
        Swift.SwiftOptional<Swift.Data> encode(Swift.Nuke.ImageContainer arg0, Swift.Nuke.ImageEncodingContext context);
    }
    
    /// <summary>
    /// Proxy class that enables C# implementations of the ImageEncoding protocol.
    /// Can wrap either a C# implementation or receive Swift existential containers.
    /// </summary>
    public unsafe class ImageEncodingProxy : ISwiftImageEncoding, ISwiftObject
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
        private readonly ISwiftImageEncoding? _csharpImpl;
        private readonly EveryProtocol? _everyProtocol;
        private readonly ExistentialContainer1 _swiftContainer;
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
            var result = proxy._csharpImpl!.encode(param0);
            return MarshalToSwiftBuffer(result);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_encode_1(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImageEncodingProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageContainer>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImageEncodingContext>(rawArg1);
            var result = proxy._csharpImpl!.encode(param0, param1);
            return MarshalToSwiftBuffer(result);
        }
        
        #endregion
        
        /// <summary>
        /// Creates a proxy wrapping a C# implementation of ISwiftImageEncoding.
        /// </summary>
        /// <param name="implementation">The C# implementation of the protocol.</param>
        public ImageEncodingProxy(ISwiftImageEncoding implementation)
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
        /// <param name="container">The Swift existential container.</param>
        public ImageEncodingProxy(ExistentialContainer1 container)
        {
            _swiftContainer = container;
            _csharpImpl = null;
            _everyProtocol = null;
        }
        #region Interface Implementation
        
        public Swift.SwiftOptional<Swift.Data> encode(UIKit.UIImage arg0)
        {
            if (_csharpImpl != null)
                return _csharpImpl.encode(arg0);
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
        }
        
        public Swift.SwiftOptional<Swift.Data> encode(Swift.Nuke.ImageContainer arg0, Swift.Nuke.ImageEncodingContext context)
        {
            if (_csharpImpl != null)
                return _csharpImpl.encode(arg0, context);
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
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
            throw new NotImplementedException("Protocol conformance descriptor not available for proxy types");
        }
        #endregion
        #region Marshalling Helpers
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
                
                
                
                PInvoke_request_Get_63B83647(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_request_Get_63B83647( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
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
                
                
                
                PInvoke_image_Get_3B4B91B0(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_image_Get_3B4B91B0( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_urlResponse_Get_3264EE38(self);
                
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
        private static extern IntPtr PInvoke_urlResponse_Get_3264EE38( SwiftSelf self);
        
        public Swift.SwiftOptional<Foundation.NSUrlResponse> UrlResponse
        {
            get => UrlResponse_Get();
        }
        
        static nuint _payloadSize = SwiftObjectHelper<ImageEncodingContext>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageEncodingContext> _payload = SwiftSafeHandle<ImageEncodingContext>.Zero;
        
        public SwiftSafeHandle<ImageEncodingContext> Payload => _payload;
        
        // Swift structs cannot be compared using .NET's default equality semantics,
        // since Swift's equality is defined by the Equatable protocol.
        // This type does not implement Swift's Equatable protocol.
        public override bool Equals(object? obj)
        {
            throw new InvalidOperationException("Type ImageEncodingContext does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        public override int GetHashCode()
        {
            throw new InvalidOperationException("Type ImageEncodingContext does not implement Swift's Equatable protocol, so GetHashCode() is not supported.");
        }
        
        public static bool operator ==(ImageEncodingContext left, ImageEncodingContext right)
        {
            throw new InvalidOperationException("Type ImageEncodingContext does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        
        public static bool operator !=(ImageEncodingContext left, ImageEncodingContext right)
        {
            throw new InvalidOperationException("Type ImageEncodingContext does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        
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
    
    
    public interface ISwiftDataLoading
    {
        Swift.Runtime.ExistentialContainer1 loadData(Swift.URLRequest with, Action<Swift.Data, Foundation.NSUrlResponse> didReceiveData, Action<Swift.SwiftOptional<Swift.AnyType>> completion);
    }
    
    /// <summary>
    /// Proxy class that enables C# implementations of the DataLoading protocol.
    /// Can wrap either a C# implementation or receive Swift existential containers.
    /// </summary>
    public unsafe class DataLoadingProxy : ISwiftDataLoading, ISwiftObject
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
        private readonly ISwiftDataLoading? _csharpImpl;
        private readonly EveryProtocol? _everyProtocol;
        private readonly ExistentialContainer1 _swiftContainer;
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
            var param2 = MarshalFromSwift<Action<Swift.SwiftOptional<Swift.AnyType>>>(rawArg2);
            var result = proxy._csharpImpl!.loadData(param0, param1, param2);
            return MarshalToSwiftBuffer(result);
        }
        
        #endregion
        
        /// <summary>
        /// Creates a proxy wrapping a C# implementation of ISwiftDataLoading.
        /// </summary>
        /// <param name="implementation">The C# implementation of the protocol.</param>
        public DataLoadingProxy(ISwiftDataLoading implementation)
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
        /// <param name="container">The Swift existential container.</param>
        public DataLoadingProxy(ExistentialContainer1 container)
        {
            _swiftContainer = container;
            _csharpImpl = null;
            _everyProtocol = null;
        }
        #region Interface Implementation
        
        public Swift.Runtime.ExistentialContainer1 loadData(Swift.URLRequest with, Action<Swift.Data, Foundation.NSUrlResponse> didReceiveData, Action<Swift.SwiftOptional<Swift.AnyType>> completion)
        {
            if (_csharpImpl != null)
                return _csharpImpl.loadData(with, didReceiveData, completion);
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
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
            throw new NotImplementedException("Protocol conformance descriptor not available for proxy types");
        }
        #endregion
        #region Marshalling Helpers
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
    
    
    public interface ISwiftCancellable
    {
        void cancel();
    }
    
    /// <summary>
    /// Proxy class that enables C# implementations of the Cancellable protocol.
    /// Can wrap either a C# implementation or receive Swift existential containers.
    /// </summary>
    public unsafe class CancellableProxy : ISwiftCancellable, ISwiftObject
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
        private readonly ISwiftCancellable? _csharpImpl;
        private readonly EveryProtocol? _everyProtocol;
        private readonly ExistentialContainer1 _swiftContainer;
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
            proxy._csharpImpl!.cancel();
        }
        
        #endregion
        
        /// <summary>
        /// Creates a proxy wrapping a C# implementation of ISwiftCancellable.
        /// </summary>
        /// <param name="implementation">The C# implementation of the protocol.</param>
        public CancellableProxy(ISwiftCancellable implementation)
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
        /// <param name="container">The Swift existential container.</param>
        public CancellableProxy(ExistentialContainer1 container)
        {
            _swiftContainer = container;
            _csharpImpl = null;
            _everyProtocol = null;
        }
        #region Interface Implementation
        
        public void cancel()
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.cancel();
                return;
            }
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
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
            throw new NotImplementedException("Protocol conformance descriptor not available for proxy types");
        }
        #endregion
        #region Marshalling Helpers
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
                
                
                
                PInvoke_image_Get_75212E50(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_image_Get_75212E50( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Image_Set( UIKit.UIImage value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            IntPtr valueHandle = value?.Handle ?? IntPtr.Zero;
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_image_Set_498EB3A9(valueHandle, self);
                
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
        private static extern void PInvoke_image_Set_498EB3A9( IntPtr value,  SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_type_Get_6A7E9F0E(self);
                
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
        private static extern IntPtr PInvoke_type_Get_6A7E9F0E( SwiftSelf self);
        
        private unsafe void Type_Set( Swift.SwiftOptional<Swift.Nuke.AssetType> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_type_Set_1CF519BE(valueBuffer, self);
                
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
        private static extern void PInvoke_type_Set_1CF519BE( IntPtr valueBuffer,  SwiftSelf self);
        
        public Swift.SwiftOptional<Swift.Nuke.AssetType> Type
        {
            get => Type_Get();
            set => Type_Set(value);
        }
        
        private unsafe System.Boolean IsPreview_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_isPreview_Get_38023388(self);
                
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
        private static extern System.Boolean PInvoke_isPreview_Get_38023388( SwiftSelf self);
        
        private unsafe void IsPreview_Set( System.Boolean value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_isPreview_Set_1869C0E7(value, self);
                
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
        private static extern void PInvoke_isPreview_Set_1869C0E7( System.Boolean value,  SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_data_Get_432BDA09(self);
                
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
        private static extern IntPtr PInvoke_data_Get_432BDA09( SwiftSelf self);
        
        private unsafe void Data_Set( Swift.SwiftOptional<Swift.Data> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_data_Set_55C04603(valueBuffer, self);
                
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
        private static extern void PInvoke_data_Set_55C04603( IntPtr valueBuffer,  SwiftSelf self);
        
        public Swift.SwiftOptional<Swift.Data> Data
        {
            get => Data_Get();
            set => Data_Set(value);
        }
        
        static nuint _payloadSize = SwiftObjectHelper<ImageContainer>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageContainer> _payload = SwiftSafeHandle<ImageContainer>.Zero;
        
        public SwiftSafeHandle<ImageContainer> Payload => _payload;
        
        // Swift structs cannot be compared using .NET's default equality semantics,
        // since Swift's equality is defined by the Equatable protocol.
        // This type does not implement Swift's Equatable protocol.
        public override bool Equals(object? obj)
        {
            throw new InvalidOperationException("Type ImageContainer does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        public override int GetHashCode()
        {
            throw new InvalidOperationException("Type ImageContainer does not implement Swift's Equatable protocol, so GetHashCode() is not supported.");
        }
        
        public static bool operator ==(ImageContainer left, ImageContainer right)
        {
            throw new InvalidOperationException("Type ImageContainer does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        
        public static bool operator !=(ImageContainer left, ImageContainer right)
        {
            throw new InvalidOperationException("Type ImageContainer does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        
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
                    
                    
                    
                    var result = PInvoke_rawValue_Get_5AB27482(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_rawValue_Get_5AB27482( SwiftSelf self);
            
            public Swift.SwiftString RawValue
            {
                get => RawValue_Get();
            }
            
            private static unsafe Swift.Nuke.ImageContainer.UserInfoKey ScanNumberKey_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageContainer.UserInfoKey>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_scanNumberKey_Get_41F4347E(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageContainer.UserInfoKey>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV11UserInfoKeyV010scanNumberF0AEvgZ")]
            private static extern void PInvoke_scanNumberKey_Get_41F4347E( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageContainer.UserInfoKey ScanNumberKey
            {
                get => ScanNumberKey_Get();
            }
            
            private unsafe System.IntPtr HashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_0BC5095A(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_0BC5095A( SwiftSelf self);
            
            public System.IntPtr HashValue
            {
                get => HashValue_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<UserInfoKey>.GetTypeMetadata().Size;
            SwiftSafeHandle<UserInfoKey> _payload = SwiftSafeHandle<UserInfoKey>.Zero;
            
            public SwiftSafeHandle<UserInfoKey> Payload => _payload;
            
            public static System.Boolean operator ==(Swift.Nuke.ImageContainer.UserInfoKey arg0, Swift.Nuke.ImageContainer.UserInfoKey arg1)
            {
                return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
            }
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV11UserInfoKeyV2eeoiySbAE_AEtFZ")]
            private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
            
            public static bool operator !=(UserInfoKey left, UserInfoKey right)
            {
                return !(left == right);
            }
            
            public override bool Equals(object? obj)
            {
                return obj is UserInfoKey other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            public override int GetHashCode()
            {
                throw new InvalidOperationException("Type UserInfoKey does not implement Swift's Hashable protocol, so GetHashCode() is not supported.");
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
                PInvoke_init_6B1C3B22(swiftIndirectResult, arg0Disposable.Buffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV11UserInfoKeyVyAESScfC")]
            private static extern void PInvoke_init_6B1C3B22( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer arg0);
            
            
            public unsafe void Hash( Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_4CB47A00(into.Payload, self);
                    
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
            private static extern void PInvoke_hash_4CB47A00( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        
    }
    
    
    public interface ISwiftDataCaching
    {
        Swift.SwiftOptional<Swift.Data> cachedData(Swift.SwiftString _for);
        System.Boolean containsData(Swift.SwiftString _for);
        void storeData(Swift.Data arg0, Swift.SwiftString _for);
        void removeData(Swift.SwiftString _for);
        void removeAll();
    }
    
    /// <summary>
    /// Proxy class that enables C# implementations of the DataCaching protocol.
    /// Can wrap either a C# implementation or receive Swift existential containers.
    /// </summary>
    public unsafe class DataCachingProxy : ISwiftDataCaching, ISwiftObject
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
        private readonly ISwiftDataCaching? _csharpImpl;
        private readonly EveryProtocol? _everyProtocol;
        private readonly ExistentialContainer1 _swiftContainer;
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
            var result = proxy._csharpImpl!.cachedData(param0);
            return MarshalToSwiftBuffer(result);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_containsData_1(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<DataCachingProxy>(container);
            var param0 = MarshalFromSwift<Swift.SwiftString>(rawArg0);
            var result = proxy._csharpImpl!.containsData(param0);
            return MarshalToSwiftBuffer(result);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Receive_storeData_2(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<DataCachingProxy>(container);
            var param0 = MarshalFromSwift<Swift.Data>(rawArg0);
            var param1 = MarshalFromSwift<Swift.SwiftString>(rawArg1);
            proxy._csharpImpl!.storeData(param0, param1);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Receive_removeData_3(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<DataCachingProxy>(container);
            var param0 = MarshalFromSwift<Swift.SwiftString>(rawArg0);
            proxy._csharpImpl!.removeData(param0);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Receive_removeAll_4(IntPtr vtHandle, IntPtr selfContainer)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<DataCachingProxy>(container);
            proxy._csharpImpl!.removeAll();
        }
        
        #endregion
        
        /// <summary>
        /// Creates a proxy wrapping a C# implementation of ISwiftDataCaching.
        /// </summary>
        /// <param name="implementation">The C# implementation of the protocol.</param>
        public DataCachingProxy(ISwiftDataCaching implementation)
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
        /// <param name="container">The Swift existential container.</param>
        public DataCachingProxy(ExistentialContainer1 container)
        {
            _swiftContainer = container;
            _csharpImpl = null;
            _everyProtocol = null;
        }
        #region Interface Implementation
        
        public Swift.SwiftOptional<Swift.Data> cachedData(Swift.SwiftString _for)
        {
            if (_csharpImpl != null)
                return _csharpImpl.cachedData(_for);
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
        }
        
        public System.Boolean containsData(Swift.SwiftString _for)
        {
            if (_csharpImpl != null)
                return _csharpImpl.containsData(_for);
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
        }
        
        public void storeData(Swift.Data arg0, Swift.SwiftString _for)
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.storeData(arg0, _for);
                return;
            }
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
        }
        
        public void removeData(Swift.SwiftString _for)
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.removeData(_for);
                return;
            }
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
        }
        
        public void removeAll()
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.removeAll();
                return;
            }
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
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
            throw new NotImplementedException("Protocol conformance descriptor not available for proxy types");
        }
        #endregion
        #region Marshalling Helpers
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
        }
    }
    
    
    public unsafe class ImageProcessingOptions : ISwiftObject
    {
        static nuint _payloadSize = SwiftObjectHelper<ImageProcessingOptions>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageProcessingOptions> _payload = SwiftSafeHandle<ImageProcessingOptions>.Zero;
        public SwiftSafeHandle<ImageProcessingOptions> Payload => _payload;
        
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
            public SwiftSafeHandle<Unit> Payload => _payload;
            
            /// <summary>
            /// Gets the 'points' case of Unit.
            /// </summary>
            public static Unit Points
            {
                get
                {
                    var result = new Unit();
                    var metadata = PInvoke_getMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    var indirectResult = new SwiftIndirectResult((void*)buffer);
                    PInvoke_Points(indirectResult);
                    result._payload = new SwiftSafeHandle<Unit>(buffer);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO4UnitO6pointsyA2EmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_Points(SwiftIndirectResult result);
            
            /// <summary>
            /// Gets the 'pixels' case of Unit.
            /// </summary>
            public static Unit Pixels
            {
                get
                {
                    var result = new Unit();
                    var metadata = PInvoke_getMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    var indirectResult = new SwiftIndirectResult((void*)buffer);
                    PInvoke_Pixels(indirectResult);
                    result._payload = new SwiftSafeHandle<Unit>(buffer);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO4UnitO6pixelsyA2EmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_Pixels(SwiftIndirectResult result);
            
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
                    
                    
                    
                    var result = PInvoke_description_Get_5625A265(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_5625A265( SwiftSelf self);
            
            public Swift.SwiftString Description
            {
                get => Description_Get();
            }
            
            private unsafe System.IntPtr HashValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_5542FE6B(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO4UnitO9hashValueSivg")]
            private static extern System.IntPtr PInvoke_hashValue_Get_5542FE6B( SwiftSelf self);
            
            public System.IntPtr HashValue
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
            
            public unsafe void Hash( Swift.Hasher into)
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_45D6CA80(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO4UnitO4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_45D6CA80( SafeHandle into,  SwiftSelf self);
            
            
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
                    
                    
                    
                    var result = PInvoke_width_Get_56F8DC77(self);
                    
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
            private static extern System.Double PInvoke_width_Get_56F8DC77( SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_color_Get_08B66BF4(swiftIndirectResult, self);
                    
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
            private static extern void PInvoke_color_Get_08B66BF4( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_description_Get_598A6DFA(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_598A6DFA( SwiftSelf self);
            
            public Swift.SwiftString Description
            {
                get => Description_Get();
            }
            
            private unsafe System.IntPtr HashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_2E3A32B4(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_2E3A32B4( SwiftSelf self);
            
            public System.IntPtr HashValue
            {
                get => HashValue_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<Border>.GetTypeMetadata().Size;
            SwiftSafeHandle<Border> _payload = SwiftSafeHandle<Border>.Zero;
            
            public SwiftSafeHandle<Border> Payload => _payload;
            
            public static System.Boolean operator ==(Swift.Nuke.ImageProcessingOptions.Border arg0, Swift.Nuke.ImageProcessingOptions.Border arg1)
            {
                return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
            }
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO6BorderV2eeoiySbAE_AEtFZ")]
            private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
            
            public static bool operator !=(Border left, Border right)
            {
                return !(left == right);
            }
            
            public override bool Equals(object? obj)
            {
                return obj is Border other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            public override int GetHashCode()
            {
                throw new InvalidOperationException("Type Border does not implement Swift's Hashable protocol, so GetHashCode() is not supported.");
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
                
                PInvoke_init_3789C6AD(swiftIndirectResult, colorHandle, width, unit.Payload);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO6BorderV5color5width4unitAESo7UIColorC_12CoreGraphics7CGFloatVAC4UnitOtcfC")]
            private static extern void PInvoke_init_3789C6AD( SwiftIndirectResult swiftIndirectResult,  IntPtr color,  System.Double width,  SafeHandle unit);
            
            
            public unsafe void Hash( Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_444E9871(into.Payload, self);
                    
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
            private static extern void PInvoke_hash_444E9871( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        public unsafe class ContentMode : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<ContentMode>.GetTypeMetadata().Size;
            SwiftSafeHandle<ContentMode> _payload = SwiftSafeHandle<ContentMode>.Zero;
            public SwiftSafeHandle<ContentMode> Payload => _payload;
            
            /// <summary>
            /// Gets the 'aspectFill' case of ContentMode.
            /// </summary>
            public static ContentMode AspectFill
            {
                get
                {
                    var result = new ContentMode();
                    var metadata = PInvoke_getMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    var indirectResult = new SwiftIndirectResult((void*)buffer);
                    PInvoke_AspectFill(indirectResult);
                    result._payload = new SwiftSafeHandle<ContentMode>(buffer);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO11ContentModeO10aspectFillyA2EmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_AspectFill(SwiftIndirectResult result);
            
            /// <summary>
            /// Gets the 'aspectFit' case of ContentMode.
            /// </summary>
            public static ContentMode AspectFit
            {
                get
                {
                    var result = new ContentMode();
                    var metadata = PInvoke_getMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    var indirectResult = new SwiftIndirectResult((void*)buffer);
                    PInvoke_AspectFit(indirectResult);
                    result._payload = new SwiftSafeHandle<ContentMode>(buffer);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO11ContentModeO9aspectFityA2EmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_AspectFit(SwiftIndirectResult result);
            
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
                    
                    
                    
                    var result = PInvoke_description_Get_790C6566(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_790C6566( SwiftSelf self);
            
            public Swift.SwiftString Description
            {
                get => Description_Get();
            }
            
            private unsafe System.IntPtr HashValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_055A8976(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO11ContentModeO9hashValueSivg")]
            private static extern System.IntPtr PInvoke_hashValue_Get_055A8976( SwiftSelf self);
            
            public System.IntPtr HashValue
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
            
            public unsafe void Hash( Swift.Hasher into)
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_0EC0A6DA(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO11ContentModeO4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_0EC0A6DA( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
    }
    
    
    public interface ISwiftImagePipelineDelegate
    {
        Swift.Runtime.ExistentialContainer1 dataLoader(Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline);
        Swift.SwiftOptional<Swift.AnyType> imageDecoder(Swift.Nuke.ImageDecodingContext _for, Swift.Nuke.ImagePipeline pipeline);
        Swift.Runtime.ExistentialContainer1 imageEncoder(Swift.Nuke.ImageEncodingContext _for, Swift.Nuke.ImagePipeline pipeline);
        Swift.SwiftOptional<Swift.AnyType> imageCache(Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline);
        Swift.SwiftOptional<Swift.AnyType> dataCache(Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline);
        Swift.SwiftOptional<Swift.SwiftString> cacheKey(Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline);
        void willCache(Swift.Data data, Swift.SwiftOptional<Swift.Nuke.ImageContainer> image, Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline, Action<Swift.SwiftOptional<Swift.Data>> completion);
        System.Boolean shouldDecompress(Swift.Nuke.ImageResponse response, Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline);
        Swift.Nuke.ImageResponse decompress(Swift.Nuke.ImageResponse response, Swift.Nuke.ImageRequest request, Swift.Nuke.ImagePipeline pipeline);
        void imageTaskCreated(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImagePipeline pipeline);
        void imageTask(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImageTask.Event didReceiveEvent, Swift.Nuke.ImagePipeline pipeline);
        void imageTaskDidStart(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImagePipeline pipeline);
        void imageTask(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImageTask.Progress didUpdateProgress, Swift.Nuke.ImagePipeline pipeline);
        void imageTask(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImageResponse didReceivePreview, Swift.Nuke.ImagePipeline pipeline);
        void imageTaskDidCancel(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImagePipeline pipeline);
        void imageTask(Swift.Nuke.ImageTask arg0, Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error> didCompleteWithResult, Swift.Nuke.ImagePipeline pipeline);
    }
    
    /// <summary>
    /// Proxy class that enables C# implementations of the ImagePipelineDelegate protocol.
    /// Can wrap either a C# implementation or receive Swift existential containers.
    /// </summary>
    public unsafe class ImagePipelineDelegateProxy : ISwiftImagePipelineDelegate, ISwiftObject
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
        private readonly ISwiftImagePipelineDelegate? _csharpImpl;
        private readonly EveryProtocol? _everyProtocol;
        private readonly ExistentialContainer1 _swiftContainer;
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
            var result = proxy._csharpImpl!.dataLoader(param0, param1);
            return MarshalToSwiftBuffer(result);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_imageDecoder_1(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageDecodingContext>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg1);
            var result = proxy._csharpImpl!.imageDecoder(param0, param1);
            return MarshalToSwiftBuffer(result);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_imageEncoder_2(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageEncodingContext>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg1);
            var result = proxy._csharpImpl!.imageEncoder(param0, param1);
            return MarshalToSwiftBuffer(result);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_imageCache_3(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageRequest>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg1);
            var result = proxy._csharpImpl!.imageCache(param0, param1);
            return MarshalToSwiftBuffer(result);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_dataCache_4(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageRequest>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg1);
            var result = proxy._csharpImpl!.dataCache(param0, param1);
            return MarshalToSwiftBuffer(result);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_cacheKey_5(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageRequest>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg1);
            var result = proxy._csharpImpl!.cacheKey(param0, param1);
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
            proxy._csharpImpl!.willCache(param0, param1, param2, param3, param4);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_shouldDecompress_7(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1, IntPtr rawArg2)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageResponse>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImageRequest>(rawArg1);
            var param2 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg2);
            var result = proxy._csharpImpl!.shouldDecompress(param0, param1, param2);
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
            var result = proxy._csharpImpl!.decompress(param0, param1, param2);
            return MarshalToSwiftBuffer(result);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Receive_imageTaskCreated_9(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageTask>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg1);
            proxy._csharpImpl!.imageTaskCreated(param0, param1);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Receive_imageTask_10(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1, IntPtr rawArg2)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageTask>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImageTask.Event>(rawArg1);
            var param2 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg2);
            proxy._csharpImpl!.imageTask(param0, param1, param2);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Receive_imageTaskDidStart_11(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageTask>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg1);
            proxy._csharpImpl!.imageTaskDidStart(param0, param1);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Receive_imageTask_12(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1, IntPtr rawArg2)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageTask>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImageTask.Progress>(rawArg1);
            var param2 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg2);
            proxy._csharpImpl!.imageTask(param0, param1, param2);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Receive_imageTask_13(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1, IntPtr rawArg2)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageTask>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImageResponse>(rawArg1);
            var param2 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg2);
            proxy._csharpImpl!.imageTask(param0, param1, param2);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Receive_imageTaskDidCancel_14(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageTask>(rawArg0);
            var param1 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg1);
            proxy._csharpImpl!.imageTaskDidCancel(param0, param1);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Receive_imageTask_15(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0, IntPtr rawArg1, IntPtr rawArg2)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImagePipelineDelegateProxy>(container);
            var param0 = MarshalFromSwift<Swift.Nuke.ImageTask>(rawArg0);
            var param1 = MarshalFromSwift<Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error>>(rawArg1);
            var param2 = MarshalFromSwift<Swift.Nuke.ImagePipeline>(rawArg2);
            proxy._csharpImpl!.imageTask(param0, param1, param2);
        }
        
        #endregion
        
        /// <summary>
        /// Creates a proxy wrapping a C# implementation of ISwiftImagePipelineDelegate.
        /// </summary>
        /// <param name="implementation">The C# implementation of the protocol.</param>
        public ImagePipelineDelegateProxy(ISwiftImagePipelineDelegate implementation)
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
        /// <param name="container">The Swift existential container.</param>
        public ImagePipelineDelegateProxy(ExistentialContainer1 container)
        {
            _swiftContainer = container;
            _csharpImpl = null;
            _everyProtocol = null;
        }
        #region Interface Implementation
        
        public Swift.Runtime.ExistentialContainer1 dataLoader(Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
                return _csharpImpl.dataLoader(_for, pipeline);
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
        }
        
        public Swift.SwiftOptional<Swift.AnyType> imageDecoder(Swift.Nuke.ImageDecodingContext _for, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
                return _csharpImpl.imageDecoder(_for, pipeline);
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
        }
        
        public Swift.Runtime.ExistentialContainer1 imageEncoder(Swift.Nuke.ImageEncodingContext _for, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
                return _csharpImpl.imageEncoder(_for, pipeline);
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
        }
        
        public Swift.SwiftOptional<Swift.AnyType> imageCache(Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
                return _csharpImpl.imageCache(_for, pipeline);
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
        }
        
        public Swift.SwiftOptional<Swift.AnyType> dataCache(Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
                return _csharpImpl.dataCache(_for, pipeline);
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
        }
        
        public Swift.SwiftOptional<Swift.SwiftString> cacheKey(Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
                return _csharpImpl.cacheKey(_for, pipeline);
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
        }
        
        public void willCache(Swift.Data data, Swift.SwiftOptional<Swift.Nuke.ImageContainer> image, Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline, Action<Swift.SwiftOptional<Swift.Data>> completion)
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.willCache(data, image, _for, pipeline, completion);
                return;
            }
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
        }
        
        public System.Boolean shouldDecompress(Swift.Nuke.ImageResponse response, Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
                return _csharpImpl.shouldDecompress(response, _for, pipeline);
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
        }
        
        public Swift.Nuke.ImageResponse decompress(Swift.Nuke.ImageResponse response, Swift.Nuke.ImageRequest request, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
                return _csharpImpl.decompress(response, request, pipeline);
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
        }
        
        public void imageTaskCreated(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.imageTaskCreated(arg0, pipeline);
                return;
            }
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
        }
        
        public void imageTask(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImageTask.Event didReceiveEvent, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.imageTask(arg0, didReceiveEvent, pipeline);
                return;
            }
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
        }
        
        public void imageTaskDidStart(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.imageTaskDidStart(arg0, pipeline);
                return;
            }
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
        }
        
        public void imageTask(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImageTask.Progress didUpdateProgress, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.imageTask(arg0, didUpdateProgress, pipeline);
                return;
            }
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
        }
        
        public void imageTask(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImageResponse didReceivePreview, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.imageTask(arg0, didReceivePreview, pipeline);
                return;
            }
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
        }
        
        public void imageTaskDidCancel(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.imageTaskDidCancel(arg0, pipeline);
                return;
            }
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
        }
        
        public void imageTask(Swift.Nuke.ImageTask arg0, Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error> didCompleteWithResult, Swift.Nuke.ImagePipeline pipeline)
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.imageTask(arg0, didCompleteWithResult, pipeline);
                return;
            }
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
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
            throw new NotImplementedException("Protocol conformance descriptor not available for proxy types");
        }
        #endregion
        #region Marshalling Helpers
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
    
    
    public interface ISwiftImageCaching
    {
        Swift.SwiftOptional<Swift.Nuke.ImageContainer> this[Swift.Nuke.ImageCacheKey index0] { get; set; }
        void removeAll();
    }
    
    /// <summary>
    /// Proxy class that enables C# implementations of the ImageCaching protocol.
    /// Can wrap either a C# implementation or receive Swift existential containers.
    /// </summary>
    public unsafe class ImageCachingProxy : ISwiftImageCaching, ISwiftObject
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
        private readonly ISwiftImageCaching? _csharpImpl;
        private readonly EveryProtocol? _everyProtocol;
        private readonly ExistentialContainer1 _swiftContainer;
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
            proxy._csharpImpl!.removeAll();
        }
        
        #endregion
        
        /// <summary>
        /// Creates a proxy wrapping a C# implementation of ISwiftImageCaching.
        /// </summary>
        /// <param name="implementation">The C# implementation of the protocol.</param>
        public ImageCachingProxy(ISwiftImageCaching implementation)
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
                // TODO: Call Swift via P/Invoke for Swift implementation
                throw new NotImplementedException("Swift implementation not yet supported");
            }
            set
            {
                if (_csharpImpl != null)
                {
                    _csharpImpl[index0] = value;
                    return;
                }
                // TODO: Call Swift via P/Invoke for Swift implementation
                throw new NotImplementedException("Swift implementation not yet supported");
            }
        }
        
        public void removeAll()
        {
            if (_csharpImpl != null)
            {
                _csharpImpl.removeAll();
                return;
            }
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
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
            throw new NotImplementedException("Protocol conformance descriptor not available for proxy types");
        }
        #endregion
        #region Marshalling Helpers
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
        }
    }
    
    
    public unsafe class ImageCacheKey : ISwiftObject, IEquatable<ImageCacheKey>
    {
        private unsafe System.IntPtr HashValue_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_hashValue_Get_08B44D4E(self);
                
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
        private static extern System.IntPtr PInvoke_hashValue_Get_08B44D4E( SwiftSelf self);
        
        public System.IntPtr HashValue
        {
            get => HashValue_Get();
        }
        
        static nuint _payloadSize = SwiftObjectHelper<ImageCacheKey>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageCacheKey> _payload = SwiftSafeHandle<ImageCacheKey>.Zero;
        
        public SwiftSafeHandle<ImageCacheKey> Payload => _payload;
        
        public static System.Boolean operator ==(Swift.Nuke.ImageCacheKey arg0, Swift.Nuke.ImageCacheKey arg1)
        {
            return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
        }
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageCacheKeyV2eeoiySbAC_ACtFZ")]
        private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
        
        public static bool operator !=(ImageCacheKey left, ImageCacheKey right)
        {
            return !(left == right);
        }
        
        public override bool Equals(object? obj)
        {
            return obj is ImageCacheKey other && Swift.Runtime.SwiftEquatable.Equals(this, other);
        }
        public override int GetHashCode()
        {
            throw new InvalidOperationException("Type ImageCacheKey does not implement Swift's Hashable protocol, so GetHashCode() is not supported.");
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
            PInvoke_init_04338352(swiftIndirectResult, keyDisposable.Buffer);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageCacheKeyV3keyACSS_tcfC")]
        private static extern void PInvoke_init_04338352( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer key);
        
        
        public unsafe ImageCacheKey( Swift.Nuke.ImageRequest request)
        {
            _payload = new SwiftSafeHandle<ImageCacheKey>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            PInvoke_init_6D0ABCD3(swiftIndirectResult, request.Payload);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageCacheKeyV7requestAcA0B7RequestV_tcfC")]
        private static extern void PInvoke_init_6D0ABCD3( SwiftIndirectResult swiftIndirectResult,  SafeHandle request);
        
        
        public unsafe void Hash( Swift.Hasher into)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_hash_675930D7(into.Payload, self);
                
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
        private static extern void PInvoke_hash_675930D7( SafeHandle into,  SwiftSelf self);
        
        
    }
    
    
    public unsafe class DataCache : ISwiftObject
    {
        private unsafe System.IntPtr SizeLimit_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_sizeLimit_Get_24D90063(self);
                
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
        private static extern System.IntPtr PInvoke_sizeLimit_Get_24D90063( SwiftSelf self);
        
        private unsafe void SizeLimit_Set( System.IntPtr value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_sizeLimit_Set_67DD6403(value, self);
                
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
        private static extern void PInvoke_sizeLimit_Set_67DD6403( System.IntPtr value,  SwiftSelf self);
        
        public System.IntPtr SizeLimit
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
                
                
                
                PInvoke_path_Get_36F2E50A(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_path_Get_36F2E50A( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_sweepInterval_Get_6C11C422(self);
                
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
        private static extern System.Double PInvoke_sweepInterval_Get_6C11C422( SwiftSelf self);
        
        private unsafe void SweepInterval_Set( System.Double value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_sweepInterval_Set_56E671DA(value, self);
                
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
        private static extern void PInvoke_sweepInterval_Set_56E671DA( System.Double value,  SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_isCompressionEnabled_Get_70307087(self);
                
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
        private static extern System.Boolean PInvoke_isCompressionEnabled_Get_70307087( SwiftSelf self);
        
        private unsafe void IsCompressionEnabled_Set( System.Boolean value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_isCompressionEnabled_Set_7F04C29B(value, self);
                
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
        private static extern void PInvoke_isCompressionEnabled_Set_7F04C29B( System.Boolean value,  SwiftSelf self);
        
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
                
                
                
                PInvoke_queue_Get_43DF47D7(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_queue_Get_43DF47D7( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Swift.DispatchQueue Queue
        {
            get => Queue_Get();
        }
        
        private unsafe System.IntPtr TotalCount_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_totalCount_Get_7A9FFDBC(self);
                
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
        private static extern System.IntPtr PInvoke_totalCount_Get_7A9FFDBC( SwiftSelf self);
        
        public System.IntPtr TotalCount
        {
            get => TotalCount_Get();
        }
        
        private unsafe System.IntPtr TotalSize_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_totalSize_Get_34B28759(self);
                
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
        private static extern System.IntPtr PInvoke_totalSize_Get_34B28759( SwiftSelf self);
        
        public System.IntPtr TotalSize
        {
            get => TotalSize_Get();
        }
        
        private unsafe System.IntPtr TotalAllocatedSize_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_totalAllocatedSize_Get_0FCD0F32(self);
                
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
        private static extern System.IntPtr PInvoke_totalAllocatedSize_Get_0FCD0F32( SwiftSelf self);
        
        public System.IntPtr TotalAllocatedSize
        {
            get => TotalAllocatedSize_Get();
        }
        
        static nuint _payloadSize = SwiftObjectHelper<DataCache>.GetTypeMetadata().Size;
        SwiftSafeHandle<DataCache> _payload = SwiftSafeHandle<DataCache>.Zero;
        
        public SwiftSafeHandle<DataCache> Payload => _payload;
        
        // Swift classes cannot be compared using .NET's default equality semantics,
        // since Swift's equality is defined by the Equatable protocol.
        // This type does not implement Swift's Equatable protocol.
        public override bool Equals(object? obj)
        {
            throw new InvalidOperationException("Type DataCache does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        public override int GetHashCode()
        {
            throw new InvalidOperationException("Type DataCache does not implement Swift's Equatable protocol, so GetHashCode() is not supported.");
        }
        
        public static bool operator ==(DataCache left, DataCache right)
        {
            throw new InvalidOperationException("Type DataCache does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        
        public static bool operator !=(DataCache left, DataCache right)
        {
            throw new InvalidOperationException("Type DataCache does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        
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
                {typeof(ISwiftDataCaching), "$s4Nuke9DataCacheCAA0B7CachingAAMc"}
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
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, void*, SwiftSelf, void> s_init_filenameGenerator_3269F69B_Callback = &init_filenameGenerator_3269F69B_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void init_filenameGenerator_3269F69B_Callback(void* indirectResult, void* arg0, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Func<Swift.SwiftString, Swift.SwiftOptional<Swift.SwiftString>>>(new IntPtr(context.Value));
            var result = del(SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(arg0)));
            // Marshal the result to the indirect result buffer
            var metadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.SwiftOptional<Swift.SwiftString>>();
            var resultSpan = new Span<byte>(indirectResult, (int)metadata.Size);
            SwiftMarshal.MarshalToSwift(result, ref resultSpan);
        }
        
        public unsafe Swift.Nuke.DataCache Init( string name,  Func<Swift.SwiftString, Swift.SwiftOptional<Swift.SwiftString>> filenameGenerator)
        {
            GCHandle filenameGeneratorHandle = default;
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.DataCache>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                filenameGeneratorHandle = GCHandle.Alloc(filenameGenerator);
                var filenameGeneratorClosure = new SwiftClosureData((IntPtr)s_init_filenameGenerator_3269F69B_Callback, GCHandle.ToIntPtr(filenameGeneratorHandle));
                using var nameSwift = new SwiftString(name);
                using PayloadBuffer<SwiftString.Buffer> nameDisposable = nameSwift.PayloadBuffer;
                
                PInvoke_init_3269F69B(swiftIndirectResult, nameDisposable.Buffer, filenameGeneratorClosure, out var error);
                
                if (error.Value != null)
                {
                    throw new SwiftRuntimeException("Call to Swift method init failed.");
                }
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.DataCache>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (filenameGeneratorHandle.IsAllocated) filenameGeneratorHandle.Free();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC4name17filenameGeneratorACSS_SSSgSSctKcfC")]
        private static extern void PInvoke_init_3269F69B( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer name,  SwiftClosureData filenameGenerator, out SwiftError error);
        
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, void*, SwiftSelf, void> s_init_filenameGenerator_6B56ABD0_Callback = &init_filenameGenerator_6B56ABD0_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void init_filenameGenerator_6B56ABD0_Callback(void* indirectResult, void* arg0, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Func<Swift.SwiftString, Swift.SwiftOptional<Swift.SwiftString>>>(new IntPtr(context.Value));
            var result = del(SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(arg0)));
            // Marshal the result to the indirect result buffer
            var metadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.SwiftOptional<Swift.SwiftString>>();
            var resultSpan = new Span<byte>(indirectResult, (int)metadata.Size);
            SwiftMarshal.MarshalToSwift(result, ref resultSpan);
        }
        
        public unsafe Swift.Nuke.DataCache Init( Foundation.NSUrl path,  Func<Swift.SwiftString, Swift.SwiftOptional<Swift.SwiftString>> filenameGenerator)
        {
            GCHandle filenameGeneratorHandle = default;
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.DataCache>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                filenameGeneratorHandle = GCHandle.Alloc(filenameGenerator);
                var filenameGeneratorClosure = new SwiftClosureData((IntPtr)s_init_filenameGenerator_6B56ABD0_Callback, GCHandle.ToIntPtr(filenameGeneratorHandle));
                using var pathSwift = Swift.URL.FromNSUrl(path);
                
                PInvoke_init_6B56ABD0(swiftIndirectResult, pathSwift.Payload, filenameGeneratorClosure, out var error);
                
                if (error.Value != null)
                {
                    throw new SwiftRuntimeException("Call to Swift method init failed.");
                }
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.DataCache>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (filenameGeneratorHandle.IsAllocated) filenameGeneratorHandle.Free();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC4path17filenameGeneratorAC10Foundation3URLV_SSSgSSctKcfC")]
        private static extern void PInvoke_init_6B56ABD0( SwiftIndirectResult swiftIndirectResult,  SafeHandle path,  SwiftClosureData filenameGenerator, out SwiftError error);
        
        
        public static unsafe Swift.SwiftString? Filename( string _for)
        {
            try
            {
                
                using var _forSwift = new SwiftString(_for);
                using PayloadBuffer<SwiftString.Buffer> _forDisposable = _forSwift.PayloadBuffer;
                
                var result = PInvoke_filename_21913D5D(_forDisposable.Buffer);
                
                var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.SwiftString>>(new IntPtr(&result));
                return swiftResult.ToNullable();
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC8filename3forSSSgSS_tFZ")]
        private static extern IntPtr PInvoke_filename_21913D5D( Swift.SwiftString.Buffer _for);
        
        
        public unsafe Swift.Data? CachedData( string _for)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                using var _forSwift = new SwiftString(_for);
                using PayloadBuffer<SwiftString.Buffer> _forDisposable = _forSwift.PayloadBuffer;
                
                var result = PInvoke_cachedData_6613606A(_forDisposable.Buffer, self);
                
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
        private static extern IntPtr PInvoke_cachedData_6613606A( Swift.SwiftString.Buffer _for,  SwiftSelf self);
        
        
        public unsafe System.Boolean ContainsData( string _for)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                using var _forSwift = new SwiftString(_for);
                using PayloadBuffer<SwiftString.Buffer> _forDisposable = _forSwift.PayloadBuffer;
                
                var result = PInvoke_containsData_5F260006(_forDisposable.Buffer, self);
                
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
        private static extern System.Boolean PInvoke_containsData_5F260006( Swift.SwiftString.Buffer _for,  SwiftSelf self);
        
        
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
                
                PInvoke_storeData_76A94B49(arg0Swift, _forDisposable.Buffer, self);
                
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
        private static extern void PInvoke_storeData_76A94B49( Swift.Data arg0,  Swift.SwiftString.Buffer _for,  SwiftSelf self);
        
        
        public unsafe void RemoveData( string _for)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                using var _forSwift = new SwiftString(_for);
                using PayloadBuffer<SwiftString.Buffer> _forDisposable = _forSwift.PayloadBuffer;
                
                PInvoke_removeData_211D6FFF(_forDisposable.Buffer, self);
                
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
        private static extern void PInvoke_removeData_211D6FFF( Swift.SwiftString.Buffer _for,  SwiftSelf self);
        
        
        public unsafe void RemoveAll()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_removeAll_424D4E46(self);
                
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
        private static extern void PInvoke_removeAll_424D4E46( SwiftSelf self);
        
        
        public unsafe Swift.URL? Url( string _for)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                using var _forSwift = new SwiftString(_for);
                using PayloadBuffer<SwiftString.Buffer> _forDisposable = _forSwift.PayloadBuffer;
                
                var result = PInvoke_url_0D9B1513(_forDisposable.Buffer, self);
                
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
        private static extern IntPtr PInvoke_url_0D9B1513( Swift.SwiftString.Buffer _for,  SwiftSelf self);
        
        
        public unsafe void Flush()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_flush_6F2B905D(self);
                
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
        private static extern void PInvoke_flush_6F2B905D( SwiftSelf self);
        
        
        public unsafe void Flush( string _for)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                using var _forSwift = new SwiftString(_for);
                using PayloadBuffer<SwiftString.Buffer> _forDisposable = _forSwift.PayloadBuffer;
                
                PInvoke_flush_1E51FD72(_forDisposable.Buffer, self);
                
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
        private static extern void PInvoke_flush_1E51FD72( Swift.SwiftString.Buffer _for,  SwiftSelf self);
        
        
        public unsafe void Sweep()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_sweep_398D0690(self);
                
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
        private static extern void PInvoke_sweep_398D0690( SwiftSelf self);
        
        
    }
    
    
    public unsafe class ImageDecoderRegistry : ISwiftObject
    {
        private static Swift.Nuke.ImageDecoderRegistry Shared_Get()
        {
            try
            {
                
                
                var result = PInvoke_shared_Get_7E6D1F71();
                
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
        private static extern IntPtr PInvoke_shared_Get_7E6D1F71();
        
        public static Swift.Nuke.ImageDecoderRegistry Shared
        {
            get => Shared_Get();
        }
        
        static nuint _payloadSize = SwiftObjectHelper<ImageDecoderRegistry>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageDecoderRegistry> _payload = SwiftSafeHandle<ImageDecoderRegistry>.Zero;
        
        public SwiftSafeHandle<ImageDecoderRegistry> Payload => _payload;
        
        // Swift classes cannot be compared using .NET's default equality semantics,
        // since Swift's equality is defined by the Equatable protocol.
        // This type does not implement Swift's Equatable protocol.
        public override bool Equals(object? obj)
        {
            throw new InvalidOperationException("Type ImageDecoderRegistry does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        public override int GetHashCode()
        {
            throw new InvalidOperationException("Type ImageDecoderRegistry does not implement Swift's Equatable protocol, so GetHashCode() is not supported.");
        }
        
        public static bool operator ==(ImageDecoderRegistry left, ImageDecoderRegistry right)
        {
            throw new InvalidOperationException("Type ImageDecoderRegistry does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        
        public static bool operator !=(ImageDecoderRegistry left, ImageDecoderRegistry right)
        {
            throw new InvalidOperationException("Type ImageDecoderRegistry does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        
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
        
        public unsafe Swift.Nuke.ImageDecoderRegistry Init()
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageDecoderRegistry>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_init_1E395875(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageDecoderRegistry>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecoderRegistryCACycfC")]
        private static extern void PInvoke_init_1E395875( SwiftIndirectResult swiftIndirectResult);
        
        
        
        
        public unsafe void Clear()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_clear_064BC8C6(self);
                
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
        private static extern void PInvoke_clear_064BC8C6( SwiftSelf self);
        
        
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
                
                
                
                PInvoke_request_Get_31CBA633(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_request_Get_31CBA633( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Request_Set( Swift.Nuke.ImageRequest value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_request_Set_4C8586BD(value.Payload, self);
                
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
        private static extern void PInvoke_request_Set_4C8586BD( SafeHandle value,  SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_data_Get_7535DADD(self);
                
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
        private static extern Swift.Data PInvoke_data_Get_7535DADD( SwiftSelf self);
        
        private unsafe void Data_Set( Swift.Data value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_data_Set_672C771B(value, self);
                
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
        private static extern void PInvoke_data_Set_672C771B( Swift.Data value,  SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_isCompleted_Get_40B80979(self);
                
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
        private static extern System.Boolean PInvoke_isCompleted_Get_40B80979( SwiftSelf self);
        
        private unsafe void IsCompleted_Set( System.Boolean value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_isCompleted_Set_03E08A76(value, self);
                
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
        private static extern void PInvoke_isCompleted_Set_03E08A76( System.Boolean value,  SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_urlResponse_Get_111D8DEA(self);
                
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
        private static extern IntPtr PInvoke_urlResponse_Get_111D8DEA( SwiftSelf self);
        
        private unsafe void UrlResponse_Set( Swift.SwiftOptional<Foundation.NSUrlResponse> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_urlResponse_Set_152C9E0B(valueBuffer, self);
                
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
        private static extern void PInvoke_urlResponse_Set_152C9E0B( IntPtr valueBuffer,  SwiftSelf self);
        
        public Swift.SwiftOptional<Foundation.NSUrlResponse> UrlResponse
        {
            get => UrlResponse_Get();
            set => UrlResponse_Set(value);
        }
        
        private unsafe Swift.SwiftOptional<Swift.Nuke.ImageResponse.CacheType> CacheType_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_cacheType_Get_7DA12B41(self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Nuke.ImageResponse.CacheType>>(new IntPtr(&result));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecodingContextV9cacheTypeAA0B8ResponseV05CacheF0OSgvg")]
        private static extern IntPtr PInvoke_cacheType_Get_7DA12B41( SwiftSelf self);
        
        private unsafe void CacheType_Set( Swift.SwiftOptional<Swift.Nuke.ImageResponse.CacheType> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_cacheType_Set_33416375(valueBuffer, self);
                
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
        private static extern void PInvoke_cacheType_Set_33416375( IntPtr valueBuffer,  SwiftSelf self);
        
        public Swift.SwiftOptional<Swift.Nuke.ImageResponse.CacheType> CacheType
        {
            get => CacheType_Get();
            set => CacheType_Set(value);
        }
        
        static nuint _payloadSize = SwiftObjectHelper<ImageDecodingContext>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageDecodingContext> _payload = SwiftSafeHandle<ImageDecodingContext>.Zero;
        
        public SwiftSafeHandle<ImageDecodingContext> Payload => _payload;
        
        // Swift structs cannot be compared using .NET's default equality semantics,
        // since Swift's equality is defined by the Equatable protocol.
        // This type does not implement Swift's Equatable protocol.
        public override bool Equals(object? obj)
        {
            throw new InvalidOperationException("Type ImageDecodingContext does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        public override int GetHashCode()
        {
            throw new InvalidOperationException("Type ImageDecodingContext does not implement Swift's Equatable protocol, so GetHashCode() is not supported.");
        }
        
        public static bool operator ==(ImageDecodingContext left, ImageDecodingContext right)
        {
            throw new InvalidOperationException("Type ImageDecodingContext does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        
        public static bool operator !=(ImageDecodingContext left, ImageDecodingContext right)
        {
            throw new InvalidOperationException("Type ImageDecodingContext does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        
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
        
        
        public unsafe ImageDecodingContext( Swift.Nuke.ImageRequest request,  Foundation.NSData data,  System.Boolean isCompleted,  Foundation.NSUrlResponse? urlResponse,  Swift.Nuke.ImageResponse.CacheType? cacheType)
        {
            _payload = new SwiftSafeHandle<ImageDecodingContext>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            var dataSwift = Swift.Data.FromNSData(data);
            using var urlResponseSwift = urlResponse is {} urlResponseValue ? SwiftOptional<Foundation.NSUrlResponse>.NewSome(urlResponseValue) : SwiftOptional<Foundation.NSUrlResponse>.NewNone();
            using PayloadBuffer<IntPtr> urlResponseDisposable = urlResponseSwift.PayloadBuffer;
            IntPtr urlResponseBuffer = urlResponseDisposable.Buffer;
            using var cacheTypeSwift = cacheType is {} cacheTypeValue ? SwiftOptional<Swift.Nuke.ImageResponse.CacheType>.NewSome(cacheTypeValue) : SwiftOptional<Swift.Nuke.ImageResponse.CacheType>.NewNone();
            using PayloadBuffer<IntPtr> cacheTypeDisposable = cacheTypeSwift.PayloadBuffer;
            IntPtr cacheTypeBuffer = cacheTypeDisposable.Buffer;
            PInvoke_init_40684564(swiftIndirectResult, request.Payload, dataSwift, isCompleted, urlResponseBuffer, cacheTypeBuffer);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecodingContextV7request4data11isCompleted11urlResponse9cacheTypeAcA0B7RequestV_10Foundation4DataVSbSo13NSURLResponseCSgAA0bJ0V05CacheL0OSgtcfC")]
        private static extern void PInvoke_init_40684564( SwiftIndirectResult swiftIndirectResult,  SafeHandle request,  Swift.Data data,  System.Boolean isCompleted,  IntPtr urlResponseBuffer,  IntPtr cacheTypeBuffer);
        
        
    }
    
    
    public unsafe class ImageProcessors : ISwiftObject
    {
        static nuint _payloadSize = SwiftObjectHelper<ImageProcessors>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageProcessors> _payload = SwiftSafeHandle<ImageProcessors>.Zero;
        public SwiftSafeHandle<ImageProcessors> Payload => _payload;
        
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
                    
                    
                    
                    var result = PInvoke_identifier_Get_58A60804(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_58A60804( SwiftSelf self);
            
            public Swift.SwiftString Identifier
            {
                get => Identifier_Get();
            }
            
            private unsafe Swift.SwiftString Description_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_15C6CAF8(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_15C6CAF8( SwiftSelf self);
            
            public Swift.SwiftString Description
            {
                get => Description_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<Anonymous>.GetTypeMetadata().Size;
            SwiftSafeHandle<Anonymous> _payload = SwiftSafeHandle<Anonymous>.Zero;
            
            public SwiftSafeHandle<Anonymous> Payload => _payload;
            
            // Swift structs cannot be compared using .NET's default equality semantics,
            // since Swift's equality is defined by the Equatable protocol.
            // This type does not implement Swift's Equatable protocol.
            public override bool Equals(object? obj)
            {
                throw new InvalidOperationException("Type Anonymous does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }
            public override int GetHashCode()
            {
                throw new InvalidOperationException("Type Anonymous does not implement Swift's Equatable protocol, so GetHashCode() is not supported.");
            }
            
            public static bool operator ==(Anonymous left, Anonymous right)
            {
                throw new InvalidOperationException("Type Anonymous does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }
            
            public static bool operator !=(Anonymous left, Anonymous right)
            {
                throw new InvalidOperationException("Type Anonymous does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }
            
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
                    {typeof(ISwiftImageProcessing), "$s4Nuke15ImageProcessorsO9AnonymousVAA0B10ProcessingAAMc"}
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
            
            
            private static unsafe readonly delegate* unmanaged[Swift]<void*, void*, SwiftSelf, void> s_init_arg1_30A5C6C0_Callback = &init_arg1_30A5C6C0_Callback;
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
            private static void init_arg1_30A5C6C0_Callback(void* indirectResult, void* arg0, SwiftSelf context)
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
                    var arg1Closure = new SwiftClosureData((IntPtr)s_init_arg1_30A5C6C0_Callback, GCHandle.ToIntPtr(arg1Handle));
                    using var idSwift = new SwiftString(id);
                    using PayloadBuffer<SwiftString.Buffer> idDisposable = idSwift.PayloadBuffer;
                    PInvoke_init_30A5C6C0(swiftIndirectResult, idDisposable.Buffer, arg1Closure);
                    
                }
                
                finally
                {
                    if (arg1Handle.IsAllocated) arg1Handle.Free();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO9AnonymousV2id_AESS_So7UIImageCSgAHYbctcfC")]
            private static extern void PInvoke_init_30A5C6C0( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer id,  SwiftClosureData arg1);
            
            
            public unsafe UIKit.UIImage? Process( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_084CB5C4(arg0Handle, self);
                    
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
            private static extern IntPtr PInvoke_process_084CB5C4( IntPtr arg0,  SwiftSelf self);
            
            
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
                    
                    
                    
                    var result = PInvoke_identifier_Get_1C216C2E(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_1C216C2E( SwiftSelf self);
            
            public Swift.SwiftString Identifier
            {
                get => Identifier_Get();
            }
            
            private unsafe Swift.SwiftString Description_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_63525FC6(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_63525FC6( SwiftSelf self);
            
            public Swift.SwiftString Description
            {
                get => Description_Get();
            }
            
            private unsafe System.IntPtr HashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_7D70D429(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_7D70D429( SwiftSelf self);
            
            public System.IntPtr HashValue
            {
                get => HashValue_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<RoundedCorners>.GetTypeMetadata().Size;
            SwiftSafeHandle<RoundedCorners> _payload = SwiftSafeHandle<RoundedCorners>.Zero;
            
            public SwiftSafeHandle<RoundedCorners> Payload => _payload;
            
            public static System.Boolean operator ==(Swift.Nuke.ImageProcessors.RoundedCorners arg0, Swift.Nuke.ImageProcessors.RoundedCorners arg1)
            {
                return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
            }
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO14RoundedCornersV2eeoiySbAE_AEtFZ")]
            private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
            
            public static bool operator !=(RoundedCorners left, RoundedCorners right)
            {
                return !(left == right);
            }
            
            public override bool Equals(object? obj)
            {
                return obj is RoundedCorners other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            public override int GetHashCode()
            {
                throw new InvalidOperationException("Type RoundedCorners does not implement Swift's Hashable protocol, so GetHashCode() is not supported.");
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
                    {typeof(ISwiftImageProcessing), "$s4Nuke15ImageProcessorsO14RoundedCornersVAA0B10ProcessingAAMc"},
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
                PInvoke_init_3F5699A0(swiftIndirectResult, radius, unit.Payload, borderBuffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO14RoundedCornersV6radius4unit6borderAE12CoreGraphics7CGFloatV_AA0B17ProcessingOptionsO4UnitOAM6BorderVSgtcfC")]
            private static extern void PInvoke_init_3F5699A0( SwiftIndirectResult swiftIndirectResult,  System.Double radius,  SafeHandle unit,  IntPtr borderBuffer);
            
            
            public unsafe UIKit.UIImage? Process( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_5CDCB236(arg0Handle, self);
                    
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
            private static extern IntPtr PInvoke_process_5CDCB236( IntPtr arg0,  SwiftSelf self);
            
            
            public unsafe void Hash( Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_7B7A6B77(into.Payload, self);
                    
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
            private static extern void PInvoke_hash_7B7A6B77( SafeHandle into,  SwiftSelf self);
            
            
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
                    
                    
                    
                    var result = PInvoke_identifier_Get_15F4C246(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_15F4C246( SwiftSelf self);
            
            public Swift.SwiftString Identifier
            {
                get => Identifier_Get();
            }
            
            private unsafe Swift.SwiftString Description_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_7D173268(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_7D173268( SwiftSelf self);
            
            public Swift.SwiftString Description
            {
                get => Description_Get();
            }
            
            private unsafe System.IntPtr HashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_14A35B90(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_14A35B90( SwiftSelf self);
            
            public System.IntPtr HashValue
            {
                get => HashValue_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<Resize>.GetTypeMetadata().Size;
            SwiftSafeHandle<Resize> _payload = SwiftSafeHandle<Resize>.Zero;
            
            public SwiftSafeHandle<Resize> Payload => _payload;
            
            public static System.Boolean operator ==(Swift.Nuke.ImageProcessors.Resize arg0, Swift.Nuke.ImageProcessors.Resize arg1)
            {
                return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
            }
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6ResizeV2eeoiySbAE_AEtFZ")]
            private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
            
            public static bool operator !=(Resize left, Resize right)
            {
                return !(left == right);
            }
            
            public override bool Equals(object? obj)
            {
                return obj is Resize other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            public override int GetHashCode()
            {
                throw new InvalidOperationException("Type Resize does not implement Swift's Hashable protocol, so GetHashCode() is not supported.");
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
                    {typeof(ISwiftImageProcessing), "$s4Nuke15ImageProcessorsO6ResizeVAA0B10ProcessingAAMc"},
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
                
                PInvoke_init_17A95BFE(swiftIndirectResult, size, unit.Payload, contentMode.Payload, crop, upscale);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6ResizeV4size4unit11contentMode4crop7upscaleAESo6CGSizeV_AA0B17ProcessingOptionsO4UnitOAN07ContentH0OS2btcfC")]
            private static extern void PInvoke_init_17A95BFE( SwiftIndirectResult swiftIndirectResult,  Swift.CGSize size,  SafeHandle unit,  SafeHandle contentMode,  System.Boolean crop,  System.Boolean upscale);
            
            
            public unsafe Resize( System.Double width,  Swift.Nuke.ImageProcessingOptions.Unit unit,  System.Boolean upscale)
            {
                _payload = new SwiftSafeHandle<Resize>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_76BA6B70(swiftIndirectResult, width, unit.Payload, upscale);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6ResizeV5width4unit7upscaleAE12CoreGraphics7CGFloatV_AA0B17ProcessingOptionsO4UnitOSbtcfC")]
            private static extern void PInvoke_init_76BA6B70( SwiftIndirectResult swiftIndirectResult,  System.Double width,  SafeHandle unit,  System.Boolean upscale);
            
            
            public unsafe UIKit.UIImage? Process( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_04CE6076(arg0Handle, self);
                    
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
            private static extern IntPtr PInvoke_process_04CE6076( IntPtr arg0,  SwiftSelf self);
            
            
            public unsafe void Hash( Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_02AAF030(into.Payload, self);
                    
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
            private static extern void PInvoke_hash_02AAF030( SafeHandle into,  SwiftSelf self);
            
            
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
                    
                    
                    
                    var result = PInvoke_identifier_Get_5239B643(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_5239B643( SwiftSelf self);
            
            public Swift.SwiftString Identifier
            {
                get => Identifier_Get();
            }
            
            private unsafe Swift.SwiftString Description_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_1747BB00(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_1747BB00( SwiftSelf self);
            
            public Swift.SwiftString Description
            {
                get => Description_Get();
            }
            
            private unsafe System.IntPtr HashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_616D7827(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_616D7827( SwiftSelf self);
            
            public System.IntPtr HashValue
            {
                get => HashValue_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<GaussianBlur>.GetTypeMetadata().Size;
            SwiftSafeHandle<GaussianBlur> _payload = SwiftSafeHandle<GaussianBlur>.Zero;
            
            public SwiftSafeHandle<GaussianBlur> Payload => _payload;
            
            public static System.Boolean operator ==(Swift.Nuke.ImageProcessors.GaussianBlur arg0, Swift.Nuke.ImageProcessors.GaussianBlur arg1)
            {
                return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
            }
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO12GaussianBlurV2eeoiySbAE_AEtFZ")]
            private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
            
            public static bool operator !=(GaussianBlur left, GaussianBlur right)
            {
                return !(left == right);
            }
            
            public override bool Equals(object? obj)
            {
                return obj is GaussianBlur other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            public override int GetHashCode()
            {
                throw new InvalidOperationException("Type GaussianBlur does not implement Swift's Hashable protocol, so GetHashCode() is not supported.");
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
                    {typeof(ISwiftImageProcessing), "$s4Nuke15ImageProcessorsO12GaussianBlurVAA0B10ProcessingAAMc"},
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
            
            
            public unsafe GaussianBlur( System.IntPtr radius)
            {
                _payload = new SwiftSafeHandle<GaussianBlur>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_6A8CB881(swiftIndirectResult, radius);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO12GaussianBlurV6radiusAESi_tcfC")]
            private static extern void PInvoke_init_6A8CB881( SwiftIndirectResult swiftIndirectResult,  System.IntPtr radius);
            
            
            public unsafe UIKit.UIImage? Process( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_79128BF1(arg0Handle, self);
                    
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
            private static extern IntPtr PInvoke_process_79128BF1( IntPtr arg0,  SwiftSelf self);
            
            
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
                    
                    
                    
                    PInvoke_process_75926E1C(swiftIndirectResult, arg0.Payload, context.Payload, self, out var error);
                    
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
            private static extern void PInvoke_process_75926E1C( SwiftIndirectResult swiftIndirectResult,  SafeHandle arg0,  SafeHandle context,  SwiftSelf self, out SwiftError error);
            
            
            public unsafe void Hash( Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_5FB7EC54(into.Payload, self);
                    
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
            private static extern void PInvoke_hash_5FB7EC54( SafeHandle into,  SwiftSelf self);
            
            
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
                    
                    
                    
                    var result = PInvoke_identifier_Get_5F53A510(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_5F53A510( SwiftSelf self);
            
            public Swift.SwiftString Identifier
            {
                get => Identifier_Get();
            }
            
            private unsafe Swift.SwiftString Description_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_255E464D(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_255E464D( SwiftSelf self);
            
            public Swift.SwiftString Description
            {
                get => Description_Get();
            }
            
            private unsafe System.IntPtr HashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_33B4B57F(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_33B4B57F( SwiftSelf self);
            
            public System.IntPtr HashValue
            {
                get => HashValue_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<Composition>.GetTypeMetadata().Size;
            SwiftSafeHandle<Composition> _payload = SwiftSafeHandle<Composition>.Zero;
            
            public SwiftSafeHandle<Composition> Payload => _payload;
            
            public static System.Boolean operator ==(Swift.Nuke.ImageProcessors.Composition arg0, Swift.Nuke.ImageProcessors.Composition arg1)
            {
                return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
            }
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO11CompositionV2eeoiySbAE_AEtFZ")]
            private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
            
            public static bool operator !=(Composition left, Composition right)
            {
                return !(left == right);
            }
            
            public override bool Equals(object? obj)
            {
                return obj is Composition other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            public override int GetHashCode()
            {
                throw new InvalidOperationException("Type Composition does not implement Swift's Hashable protocol, so GetHashCode() is not supported.");
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
                    {typeof(ISwiftImageProcessing), "$s4Nuke15ImageProcessorsO11CompositionVAA0B10ProcessingAAMc"},
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
                    
                    
                    
                    var result = PInvoke_process_24207862(arg0Handle, self);
                    
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
            private static extern IntPtr PInvoke_process_24207862( IntPtr arg0,  SwiftSelf self);
            
            
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
                    
                    
                    
                    PInvoke_process_57DE0A40(swiftIndirectResult, arg0.Payload, context.Payload, self, out var error);
                    
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
            private static extern void PInvoke_process_57DE0A40( SwiftIndirectResult swiftIndirectResult,  SafeHandle arg0,  SafeHandle context,  SwiftSelf self, out SwiftError error);
            
            
            public unsafe void Hash( Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_6C57B693(into.Payload, self);
                    
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
            private static extern void PInvoke_hash_6C57B693( SafeHandle into,  SwiftSelf self);
            
            
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
                    
                    
                    
                    var result = PInvoke_identifier_Get_355A28AD(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_355A28AD( SwiftSelf self);
            
            public Swift.SwiftString Identifier
            {
                get => Identifier_Get();
            }
            
            private unsafe Swift.SwiftString Description_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_7C7509D9(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_7C7509D9( SwiftSelf self);
            
            public Swift.SwiftString Description
            {
                get => Description_Get();
            }
            
            private unsafe System.IntPtr HashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_29D21C79(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_29D21C79( SwiftSelf self);
            
            public System.IntPtr HashValue
            {
                get => HashValue_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<Circle>.GetTypeMetadata().Size;
            SwiftSafeHandle<Circle> _payload = SwiftSafeHandle<Circle>.Zero;
            
            public SwiftSafeHandle<Circle> Payload => _payload;
            
            public static System.Boolean operator ==(Swift.Nuke.ImageProcessors.Circle arg0, Swift.Nuke.ImageProcessors.Circle arg1)
            {
                return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
            }
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6CircleV2eeoiySbAE_AEtFZ")]
            private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
            
            public static bool operator !=(Circle left, Circle right)
            {
                return !(left == right);
            }
            
            public override bool Equals(object? obj)
            {
                return obj is Circle other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            public override int GetHashCode()
            {
                throw new InvalidOperationException("Type Circle does not implement Swift's Hashable protocol, so GetHashCode() is not supported.");
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
                    {typeof(ISwiftImageProcessing), "$s4Nuke15ImageProcessorsO6CircleVAA0B10ProcessingAAMc"},
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
                PInvoke_init_772A0652(swiftIndirectResult, borderBuffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6CircleV6borderAeA0B17ProcessingOptionsO6BorderVSg_tcfC")]
            private static extern void PInvoke_init_772A0652( SwiftIndirectResult swiftIndirectResult,  IntPtr borderBuffer);
            
            
            public unsafe UIKit.UIImage? Process( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_7BFCDFC8(arg0Handle, self);
                    
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
            private static extern IntPtr PInvoke_process_7BFCDFC8( IntPtr arg0,  SwiftSelf self);
            
            
            public unsafe void Hash( Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_2C61D467(into.Payload, self);
                    
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
            private static extern void PInvoke_hash_2C61D467( SafeHandle into,  SwiftSelf self);
            
            
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
                    
                    
                    
                    var result = PInvoke_identifier_Get_5D11E7B8(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_5D11E7B8( SwiftSelf self);
            
            public Swift.SwiftString Identifier
            {
                get => Identifier_Get();
            }
            
            private static unsafe Swift.CIContext Context_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.CIContext>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_context_Get_19032D21(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.CIContext>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV7contextSo9CIContextCvgZ")]
            private static extern void PInvoke_context_Get_19032D21( SwiftIndirectResult swiftIndirectResult);
            
            private static void Context_Set( Swift.CIContext value)
            {
                try
                {
                    
                    
                    PInvoke_context_Set_0B09D9BA(value.Payload);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV7contextSo9CIContextCvsZ")]
            private static extern void PInvoke_context_Set_0B09D9BA( SafeHandle value);
            
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
                    
                    
                    
                    var result = PInvoke_description_Get_78A6C15D(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_78A6C15D( SwiftSelf self);
            
            public Swift.SwiftString Description
            {
                get => Description_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<CoreImageFilter>.GetTypeMetadata().Size;
            SwiftSafeHandle<CoreImageFilter> _payload = SwiftSafeHandle<CoreImageFilter>.Zero;
            
            public SwiftSafeHandle<CoreImageFilter> Payload => _payload;
            
            // Swift structs cannot be compared using .NET's default equality semantics,
            // since Swift's equality is defined by the Equatable protocol.
            // This type does not implement Swift's Equatable protocol.
            public override bool Equals(object? obj)
            {
                throw new InvalidOperationException("Type CoreImageFilter does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }
            public override int GetHashCode()
            {
                throw new InvalidOperationException("Type CoreImageFilter does not implement Swift's Equatable protocol, so GetHashCode() is not supported.");
            }
            
            public static bool operator ==(CoreImageFilter left, CoreImageFilter right)
            {
                throw new InvalidOperationException("Type CoreImageFilter does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }
            
            public static bool operator !=(CoreImageFilter left, CoreImageFilter right)
            {
                throw new InvalidOperationException("Type CoreImageFilter does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }
            
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
                    {typeof(ISwiftImageProcessing), "$s4Nuke15ImageProcessorsO04CoreB6FilterVAA0B10ProcessingAAMc"}
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
                public SwiftSafeHandle<Error> Payload => _payload;
                
                /// <summary>
                /// Creates the 'failedToCreateFilter' case of Error.
                /// </summary>
                public static Error FailedToCreateFilter((Swift.SwiftString name, Swift.SwiftDictionary<Swift.SwiftString, Swift.AnyType> parameters) value0)
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
                public unsafe bool TryGetFailedToCreateFilter([MaybeNullWhen(false)] out Swift.SwiftString name, [MaybeNullWhen(false)] out Swift.SwiftDictionary<Swift.SwiftString, Swift.AnyType> parameters)
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
                    parameters = SwiftMarshal.MarshalFromSwift<Swift.SwiftDictionary<Swift.SwiftString, Swift.AnyType>>(new IntPtr(enumCopy + (int)offset1));
                    
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
                    elementMetadataArray[1] = SwiftObjectHelper<Swift.SwiftDictionary<Swift.SwiftString, Swift.AnyType>>.GetTypeMetadata();
                    
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
                        
                        
                        
                        var result = PInvoke_description_Get_1BB81DCC(self);
                        
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
                private static extern Swift.SwiftString.Buffer PInvoke_description_Get_1BB81DCC( SwiftSelf self);
                
                public Swift.SwiftString Description
                {
                    get => Description_Get();
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
                PInvoke_init_1AC6053A(swiftIndirectResult, nameDisposable.Buffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV4nameAESS_tcfC")]
            private static extern void PInvoke_init_1AC6053A( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer name);
            
            
            public unsafe CoreImageFilter( CoreImage.CIFilter arg0,  string identifier)
            {
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                _payload = new SwiftSafeHandle<CoreImageFilter>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                using var identifierSwift = new SwiftString(identifier);
                using PayloadBuffer<SwiftString.Buffer> identifierDisposable = identifierSwift.PayloadBuffer;
                PInvoke_init_2641F219(swiftIndirectResult, arg0Handle, identifierDisposable.Buffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV_10identifierAESo8CIFilterC_SStcfC")]
            private static extern void PInvoke_init_2641F219( SwiftIndirectResult swiftIndirectResult,  IntPtr arg0,  Swift.SwiftString.Buffer identifier);
            
            
            public unsafe UIKit.UIImage? Process( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_572AA34A(arg0Handle, self);
                    
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
            private static extern IntPtr PInvoke_process_572AA34A( IntPtr arg0,  SwiftSelf self);
            
            
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
                    
                    
                    
                    PInvoke_process_2B1D53E6(swiftIndirectResult, arg0.Payload, context.Payload, self, out var error);
                    
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
            private static extern void PInvoke_process_2B1D53E6( SwiftIndirectResult swiftIndirectResult,  SafeHandle arg0,  SafeHandle context,  SwiftSelf self, out SwiftError error);
            
            
            public static unsafe UIKit.UIImage Apply( CoreImage.CIFilter filter,  UIKit.UIImage to)
            {
                IntPtr filterHandle = filter?.Handle ?? IntPtr.Zero;
                IntPtr toHandle = to?.Handle ?? IntPtr.Zero;
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<UIKit.UIImage>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_apply_1298B2A1(swiftIndirectResult, filterHandle, toHandle, out var error);
                    
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
            private static extern void PInvoke_apply_1298B2A1( SwiftIndirectResult swiftIndirectResult,  IntPtr filter,  IntPtr to, out SwiftError error);
            
            
        }
        
        
    }
    
    
    public unsafe class ImageRequest : ISwiftObject
    {
        private unsafe Swift.Nuke.ImageRequest.Priority Priority_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Priority>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_priority_Get_62058B66(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Priority>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV8priorityAC8PriorityOvg")]
        private static extern void PInvoke_priority_Get_62058B66( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Priority_Set( Swift.Nuke.ImageRequest.Priority value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_priority_Set_1ECF37EB(value.Payload, self);
                
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
        private static extern void PInvoke_priority_Set_1ECF37EB( SafeHandle value,  SwiftSelf self);
        
        public Swift.Nuke.ImageRequest.Priority PriorityValue
        {
            get => Priority_Get();
            set => Priority_Set(value);
        }
        
        private unsafe Swift.Nuke.ImageRequest.Options Options_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Options>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_options_Get_50620680(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7optionsAC7OptionsVvg")]
        private static extern void PInvoke_options_Get_50620680( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Options_Set( Swift.Nuke.ImageRequest.Options value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_options_Set_4EC0A9B9(value.Payload, self);
                
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
        private static extern void PInvoke_options_Set_4EC0A9B9( SafeHandle value,  SwiftSelf self);
        
        public Swift.Nuke.ImageRequest.Options OptionsValue
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
                
                
                
                var result = PInvoke_urlRequest_Get_76C24399(self);
                
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
        private static extern IntPtr PInvoke_urlRequest_Get_76C24399( SwiftSelf self);
        
        public Swift.SwiftOptional<Swift.URLRequest> UrlRequest
        {
            get => UrlRequest_Get();
        }
        
        private unsafe Swift.SwiftOptional<Swift.URL> Url_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_url_Get_78366D82(self);
                
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
        private static extern IntPtr PInvoke_url_Get_78366D82( SwiftSelf self);
        
        public Swift.SwiftOptional<Swift.URL> Url
        {
            get => Url_Get();
        }
        
        private unsafe Swift.SwiftOptional<Swift.SwiftString> ImageId_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_imageId_Get_5E1F8C8C(self);
                
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
        private static extern IntPtr PInvoke_imageId_Get_5E1F8C8C( SwiftSelf self);
        
        public Swift.SwiftOptional<Swift.SwiftString> ImageId
        {
            get => ImageId_Get();
        }
        
        private unsafe Swift.SwiftString Description_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_description_Get_32CC7E95(self);
                
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
        private static extern Swift.SwiftString.Buffer PInvoke_description_Get_32CC7E95( SwiftSelf self);
        
        public Swift.SwiftString Description
        {
            get => Description_Get();
        }
        
        static nuint _payloadSize = SwiftObjectHelper<ImageRequest>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageRequest> _payload = SwiftSafeHandle<ImageRequest>.Zero;
        
        public SwiftSafeHandle<ImageRequest> Payload => _payload;
        
        // Swift structs cannot be compared using .NET's default equality semantics,
        // since Swift's equality is defined by the Equatable protocol.
        // This type does not implement Swift's Equatable protocol.
        public override bool Equals(object? obj)
        {
            throw new InvalidOperationException("Type ImageRequest does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        public override int GetHashCode()
        {
            throw new InvalidOperationException("Type ImageRequest does not implement Swift's Equatable protocol, so GetHashCode() is not supported.");
        }
        
        public static bool operator ==(ImageRequest left, ImageRequest right)
        {
            throw new InvalidOperationException("Type ImageRequest does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        
        public static bool operator !=(ImageRequest left, ImageRequest right)
        {
            throw new InvalidOperationException("Type ImageRequest does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        
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
        
        
        public unsafe class Priority : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<Priority>.GetTypeMetadata().Size;
            SwiftSafeHandle<Priority> _payload = SwiftSafeHandle<Priority>.Zero;
            public SwiftSafeHandle<Priority> Payload => _payload;
            
            /// <summary>
            /// Creates a Priority from its raw value.
            /// Returns null if the raw value doesn't correspond to a valid case.
            /// </summary>
            public static unsafe Priority? FromRawValue(long rawValue)
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
                    
                    var result = new Priority();
                    result._payload = new SwiftSafeHandle<Priority>(enumBuffer);
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
            /// Gets the 'veryLow' case of Priority.
            /// </summary>
            public static Priority VeryLow
            {
                get
                {
                    var result = FromRawValue(0);
                    if (result == null)
                    {
                        throw new InvalidOperationException("Failed to create Priority.VeryLow from raw value 0");
                    }
                    return result;
                }
            }
            
            /// <summary>
            /// Gets the 'low' case of Priority.
            /// </summary>
            public static Priority Low
            {
                get
                {
                    var result = FromRawValue(1);
                    if (result == null)
                    {
                        throw new InvalidOperationException("Failed to create Priority.Low from raw value 1");
                    }
                    return result;
                }
            }
            
            /// <summary>
            /// Gets the 'normal' case of Priority.
            /// </summary>
            public static Priority Normal
            {
                get
                {
                    var result = FromRawValue(2);
                    if (result == null)
                    {
                        throw new InvalidOperationException("Failed to create Priority.Normal from raw value 2");
                    }
                    return result;
                }
            }
            
            /// <summary>
            /// Gets the 'high' case of Priority.
            /// </summary>
            public static Priority High
            {
                get
                {
                    var result = FromRawValue(3);
                    if (result == null)
                    {
                        throw new InvalidOperationException("Failed to create Priority.High from raw value 3");
                    }
                    return result;
                }
            }
            
            /// <summary>
            /// Gets the 'veryHigh' case of Priority.
            /// </summary>
            public static Priority VeryHigh
            {
                get
                {
                    var result = FromRawValue(4);
                    if (result == null)
                    {
                        throw new InvalidOperationException("Failed to create Priority.VeryHigh from raw value 4");
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
                        var metadata = SwiftObjectHelper<Priority>.GetTypeMetadata();
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
            
            
            private unsafe System.IntPtr RawValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_rawValue_Get_0702BBF5(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV8PriorityO8rawValueSivg")]
            private static extern System.IntPtr PInvoke_rawValue_Get_0702BBF5( SwiftSelf self);
            
            public System.IntPtr RawValue
            {
                get => RawValue_Get();
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV8PriorityOMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new Priority(handle);
            }
            
            Priority()
            {
            }
            
            Priority(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<Priority>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<Priority>.GetTypeMetadata();
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
            static Priority()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IEquatable<Priority>), "$s4Nuke12ImageRequestV8PriorityOSQAAMc"}
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
        
        
        public unsafe class Options : ISwiftObject, IEquatable<Options>
        {
            private unsafe System.UInt16 RawValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_rawValue_Get_521BD2CF(self);
                    
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
            private static extern System.UInt16 PInvoke_rawValue_Get_521BD2CF( SwiftSelf self);
            
            public System.UInt16 RawValue
            {
                get => RawValue_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.Options DisableMemoryCacheReads_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Options>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_disableMemoryCacheReads_Get_768703E9(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV23disableMemoryCacheReadsAEvgZ")]
            private static extern void PInvoke_disableMemoryCacheReads_Get_768703E9( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.Options DisableMemoryCacheReads
            {
                get => DisableMemoryCacheReads_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.Options DisableMemoryCacheWrites_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Options>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_disableMemoryCacheWrites_Get_758F4DED(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV24disableMemoryCacheWritesAEvgZ")]
            private static extern void PInvoke_disableMemoryCacheWrites_Get_758F4DED( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.Options DisableMemoryCacheWrites
            {
                get => DisableMemoryCacheWrites_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.Options DisableMemoryCache_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Options>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_disableMemoryCache_Get_01DD47C9(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV18disableMemoryCacheAEvgZ")]
            private static extern void PInvoke_disableMemoryCache_Get_01DD47C9( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.Options DisableMemoryCache
            {
                get => DisableMemoryCache_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.Options DisableDiskCacheReads_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Options>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_disableDiskCacheReads_Get_4ED87420(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV21disableDiskCacheReadsAEvgZ")]
            private static extern void PInvoke_disableDiskCacheReads_Get_4ED87420( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.Options DisableDiskCacheReads
            {
                get => DisableDiskCacheReads_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.Options DisableDiskCacheWrites_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Options>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_disableDiskCacheWrites_Get_3CCC004F(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV22disableDiskCacheWritesAEvgZ")]
            private static extern void PInvoke_disableDiskCacheWrites_Get_3CCC004F( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.Options DisableDiskCacheWrites
            {
                get => DisableDiskCacheWrites_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.Options DisableDiskCache_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Options>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_disableDiskCache_Get_1BE4DCC1(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV16disableDiskCacheAEvgZ")]
            private static extern void PInvoke_disableDiskCache_Get_1BE4DCC1( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.Options DisableDiskCache
            {
                get => DisableDiskCache_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.Options ReloadIgnoringCachedData_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Options>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_reloadIgnoringCachedData_Get_1FAE86B6(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV24reloadIgnoringCachedDataAEvgZ")]
            private static extern void PInvoke_reloadIgnoringCachedData_Get_1FAE86B6( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.Options ReloadIgnoringCachedData
            {
                get => ReloadIgnoringCachedData_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.Options ReturnCacheDataDontLoad_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Options>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_returnCacheDataDontLoad_Get_090D29B3(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV23returnCacheDataDontLoadAEvgZ")]
            private static extern void PInvoke_returnCacheDataDontLoad_Get_090D29B3( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.Options ReturnCacheDataDontLoad
            {
                get => ReturnCacheDataDontLoad_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.Options SkipDecompression_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Options>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_skipDecompression_Get_3353BF55(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV17skipDecompressionAEvgZ")]
            private static extern void PInvoke_skipDecompression_Get_3353BF55( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.Options SkipDecompression
            {
                get => SkipDecompression_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.Options SkipDataLoadingQueue_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Options>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_skipDataLoadingQueue_Get_22D30AAE(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV20skipDataLoadingQueueAEvgZ")]
            private static extern void PInvoke_skipDataLoadingQueue_Get_22D30AAE( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.Options SkipDataLoadingQueue
            {
                get => SkipDataLoadingQueue_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<Options>.GetTypeMetadata().Size;
            SwiftSafeHandle<Options> _payload = SwiftSafeHandle<Options>.Zero;
            
            public SwiftSafeHandle<Options> Payload => _payload;
            
            public override bool Equals(object? obj)
            {
                return obj is Options other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            public override int GetHashCode()
            {
                throw new InvalidOperationException("Type Options does not implement Swift's Hashable protocol, so GetHashCode() is not supported.");
            }
            
            public static bool operator ==(Options left, Options right)
            {
                return Swift.Runtime.SwiftEquatable.Equals(left, right);
            }
            
            public static bool operator !=(Options left, Options right)
            {
                return !Swift.Runtime.SwiftEquatable.Equals(left, right);
            }
            
            public bool Equals(Options? other)
            {
                return Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsVMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new Options(handle);
            }
            
            Options(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<Options>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<Options>.GetTypeMetadata();
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
            static Options()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IEquatable<Options>), "$s4Nuke12ImageRequestV7OptionsVSQAAMc"}
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
            
            
            public unsafe Options( System.UInt16 rawValue)
            {
                _payload = new SwiftSafeHandle<Options>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_7B2C0900(swiftIndirectResult, rawValue);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV8rawValueAEs6UInt16V_tcfC")]
            private static extern void PInvoke_init_7B2C0900( SwiftIndirectResult swiftIndirectResult,  System.UInt16 rawValue);
            
            
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
                    
                    
                    
                    var result = PInvoke_rawValue_Get_1592B446(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_rawValue_Get_1592B446( SwiftSelf self);
            
            public Swift.SwiftString RawValue
            {
                get => RawValue_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.UserInfoKey ImageIdKey_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.UserInfoKey>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_imageIdKey_Get_703A5898(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.UserInfoKey>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV11UserInfoKeyV07imageIdF0AEvgZ")]
            private static extern void PInvoke_imageIdKey_Get_703A5898( SwiftIndirectResult swiftIndirectResult);
            
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
                    
                    
                    
                    PInvoke_scaleKey_Get_0ECC552C(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.UserInfoKey>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV11UserInfoKeyV05scaleF0AEvgZ")]
            private static extern void PInvoke_scaleKey_Get_0ECC552C( SwiftIndirectResult swiftIndirectResult);
            
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
                    
                    
                    
                    PInvoke_thumbnailKey_Get_3CB4F525(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.UserInfoKey>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV11UserInfoKeyV09thumbnailF0AEvgZ")]
            private static extern void PInvoke_thumbnailKey_Get_3CB4F525( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.UserInfoKey ThumbnailKey
            {
                get => ThumbnailKey_Get();
            }
            
            private unsafe System.IntPtr HashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_53E15B33(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_53E15B33( SwiftSelf self);
            
            public System.IntPtr HashValue
            {
                get => HashValue_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<UserInfoKey>.GetTypeMetadata().Size;
            SwiftSafeHandle<UserInfoKey> _payload = SwiftSafeHandle<UserInfoKey>.Zero;
            
            public SwiftSafeHandle<UserInfoKey> Payload => _payload;
            
            public static System.Boolean operator ==(Swift.Nuke.ImageRequest.UserInfoKey arg0, Swift.Nuke.ImageRequest.UserInfoKey arg1)
            {
                return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
            }
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV11UserInfoKeyV2eeoiySbAE_AEtFZ")]
            private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
            
            public static bool operator !=(UserInfoKey left, UserInfoKey right)
            {
                return !(left == right);
            }
            
            public override bool Equals(object? obj)
            {
                return obj is UserInfoKey other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            public override int GetHashCode()
            {
                throw new InvalidOperationException("Type UserInfoKey does not implement Swift's Hashable protocol, so GetHashCode() is not supported.");
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
                PInvoke_init_59B43141(swiftIndirectResult, arg0Disposable.Buffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV11UserInfoKeyVyAESScfC")]
            private static extern void PInvoke_init_59B43141( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer arg0);
            
            
            public unsafe void Hash( Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_1FF4187A(into.Payload, self);
                    
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
            private static extern void PInvoke_hash_1FF4187A( SafeHandle into,  SwiftSelf self);
            
            
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
                    
                    
                    
                    var result = PInvoke_createThumbnailFromImageIfAbsent_Get_7B9C2613(self);
                    
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
            private static extern System.Boolean PInvoke_createThumbnailFromImageIfAbsent_Get_7B9C2613( SwiftSelf self);
            
            private unsafe void CreateThumbnailFromImageIfAbsent_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_createThumbnailFromImageIfAbsent_Set_0180C2A6(value, self);
                    
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
            private static extern void PInvoke_createThumbnailFromImageIfAbsent_Set_0180C2A6( System.Boolean value,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_createThumbnailFromImageAlways_Get_33FE1116(self);
                    
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
            private static extern System.Boolean PInvoke_createThumbnailFromImageAlways_Get_33FE1116( SwiftSelf self);
            
            private unsafe void CreateThumbnailFromImageAlways_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_createThumbnailFromImageAlways_Set_3AC43BD2(value, self);
                    
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
            private static extern void PInvoke_createThumbnailFromImageAlways_Set_3AC43BD2( System.Boolean value,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_createThumbnailWithTransform_Get_02DBE323(self);
                    
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
            private static extern System.Boolean PInvoke_createThumbnailWithTransform_Get_02DBE323( SwiftSelf self);
            
            private unsafe void CreateThumbnailWithTransform_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_createThumbnailWithTransform_Set_2E87D21F(value, self);
                    
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
            private static extern void PInvoke_createThumbnailWithTransform_Set_2E87D21F( System.Boolean value,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_shouldCacheImmediately_Get_7764AB7C(self);
                    
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
            private static extern System.Boolean PInvoke_shouldCacheImmediately_Get_7764AB7C( SwiftSelf self);
            
            private unsafe void ShouldCacheImmediately_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_shouldCacheImmediately_Set_236BE5A7(value, self);
                    
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
            private static extern void PInvoke_shouldCacheImmediately_Set_236BE5A7( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean ShouldCacheImmediately
            {
                get => ShouldCacheImmediately_Get();
                set => ShouldCacheImmediately_Set(value);
            }
            
            private unsafe System.IntPtr HashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_1D3CEF69(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_1D3CEF69( SwiftSelf self);
            
            public System.IntPtr HashValue
            {
                get => HashValue_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<ThumbnailOptions>.GetTypeMetadata().Size;
            SwiftSafeHandle<ThumbnailOptions> _payload = SwiftSafeHandle<ThumbnailOptions>.Zero;
            
            public SwiftSafeHandle<ThumbnailOptions> Payload => _payload;
            
            public static System.Boolean operator ==(Swift.Nuke.ImageRequest.ThumbnailOptions arg0, Swift.Nuke.ImageRequest.ThumbnailOptions arg1)
            {
                return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
            }
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV16ThumbnailOptionsV2eeoiySbAE_AEtFZ")]
            private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
            
            public static bool operator !=(ThumbnailOptions left, ThumbnailOptions right)
            {
                return !(left == right);
            }
            
            public override bool Equals(object? obj)
            {
                return obj is ThumbnailOptions other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            public override int GetHashCode()
            {
                throw new InvalidOperationException("Type ThumbnailOptions does not implement Swift's Hashable protocol, so GetHashCode() is not supported.");
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
                
                PInvoke_init_13F6A49D(swiftIndirectResult, maxPixelSize);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV16ThumbnailOptionsV12maxPixelSizeAESf_tcfC")]
            private static extern void PInvoke_init_13F6A49D( SwiftIndirectResult swiftIndirectResult,  System.Single maxPixelSize);
            
            
            public unsafe ThumbnailOptions( Swift.CGSize size,  Swift.Nuke.ImageProcessingOptions.Unit unit,  Swift.Nuke.ImageProcessingOptions.ContentMode contentMode)
            {
                _payload = new SwiftSafeHandle<ThumbnailOptions>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_1ED60B67(swiftIndirectResult, size, unit.Payload, contentMode.Payload);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV16ThumbnailOptionsV4size4unit11contentModeAESo6CGSizeV_AA0b10ProcessingE0O4UnitOAL07ContentI0OtcfC")]
            private static extern void PInvoke_init_1ED60B67( SwiftIndirectResult swiftIndirectResult,  Swift.CGSize size,  SafeHandle unit,  SafeHandle contentMode);
            
            
            public unsafe UIKit.UIImage? MakeThumbnail( Foundation.NSData with)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    var withSwift = Swift.Data.FromNSData(with);
                    
                    var result = PInvoke_makeThumbnail_584E2080(withSwift, self);
                    
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
            private static extern IntPtr PInvoke_makeThumbnail_584E2080( Swift.Data with,  SwiftSelf self);
            
            
            public unsafe void Hash( Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_35DBFD74(into.Payload, self);
                    
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
            private static extern void PInvoke_hash_35DBFD74( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        public unsafe ImageRequest( string stringLiteral)
        {
            _payload = new SwiftSafeHandle<ImageRequest>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            using var stringLiteralSwift = new SwiftString(stringLiteral);
            using PayloadBuffer<SwiftString.Buffer> stringLiteralDisposable = stringLiteralSwift.PayloadBuffer;
            PInvoke_init_2BDCBB2E(swiftIndirectResult, stringLiteralDisposable.Buffer);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV13stringLiteralACSS_tcfC")]
        private static extern void PInvoke_init_2BDCBB2E( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer stringLiteral);
        
        
        
        
        
    }
    
    
    public unsafe class ImageDecoders : ISwiftObject
    {
        static nuint _payloadSize = SwiftObjectHelper<ImageDecoders>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageDecoders> _payload = SwiftSafeHandle<ImageDecoders>.Zero;
        public SwiftSafeHandle<ImageDecoders> Payload => _payload;
        
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
        
        public unsafe class Empty : ISwiftObject
        {
            private unsafe System.Boolean IsProgressive_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isProgressive_Get_6AD67625(self);
                    
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
            private static extern System.Boolean PInvoke_isProgressive_Get_6AD67625( SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_isAsynchronous_Get_451FBDD1(self);
                    
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
            private static extern System.Boolean PInvoke_isAsynchronous_Get_451FBDD1( SwiftSelf self);
            
            public System.Boolean IsAsynchronous
            {
                get => IsAsynchronous_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<Empty>.GetTypeMetadata().Size;
            SwiftSafeHandle<Empty> _payload = SwiftSafeHandle<Empty>.Zero;
            
            public SwiftSafeHandle<Empty> Payload => _payload;
            
            // Swift structs cannot be compared using .NET's default equality semantics,
            // since Swift's equality is defined by the Equatable protocol.
            // This type does not implement Swift's Equatable protocol.
            public override bool Equals(object? obj)
            {
                throw new InvalidOperationException("Type Empty does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }
            public override int GetHashCode()
            {
                throw new InvalidOperationException("Type Empty does not implement Swift's Equatable protocol, so GetHashCode() is not supported.");
            }
            
            public static bool operator ==(Empty left, Empty right)
            {
                throw new InvalidOperationException("Type Empty does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }
            
            public static bool operator !=(Empty left, Empty right)
            {
                throw new InvalidOperationException("Type Empty does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }
            
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
                    {typeof(ISwiftImageDecoding), "$s4Nuke13ImageDecodersO5EmptyVAA0B8DecodingAAMc"}
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
                PInvoke_init_0620CFAB(swiftIndirectResult, assetTypeBuffer, isProgressive);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO5EmptyV9assetType13isProgressiveAeA05AssetF0VSg_SbtcfC")]
            private static extern void PInvoke_init_0620CFAB( SwiftIndirectResult swiftIndirectResult,  IntPtr assetTypeBuffer,  System.Boolean isProgressive);
            
            
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
                    
                    PInvoke_decode_11E2EA0D(swiftIndirectResult, arg0Swift, self, out var error);
                    
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
            private static extern void PInvoke_decode_11E2EA0D( SwiftIndirectResult swiftIndirectResult,  Swift.Data arg0,  SwiftSelf self, out SwiftError error);
            
            
            public unsafe Swift.Nuke.ImageContainer? DecodePartiallyDownloadedData( Foundation.NSData arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    var arg0Swift = Swift.Data.FromNSData(arg0);
                    
                    var result = PInvoke_decodePartiallyDownloadedData_360E5B37(arg0Swift, self);
                    
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
            private static extern IntPtr PInvoke_decodePartiallyDownloadedData_360E5B37( Swift.Data arg0,  SwiftSelf self);
            
            
        }
        
        
        public unsafe class Default : ISwiftObject
        {
            private unsafe System.Boolean IsAsynchronous_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isAsynchronous_Get_6AEE0FE2(self);
                    
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
            private static extern System.Boolean PInvoke_isAsynchronous_Get_6AEE0FE2( SwiftSelf self);
            
            public System.Boolean IsAsynchronous
            {
                get => IsAsynchronous_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<Default>.GetTypeMetadata().Size;
            SwiftSafeHandle<Default> _payload = SwiftSafeHandle<Default>.Zero;
            
            public SwiftSafeHandle<Default> Payload => _payload;
            
            // Swift classes cannot be compared using .NET's default equality semantics,
            // since Swift's equality is defined by the Equatable protocol.
            // This type does not implement Swift's Equatable protocol.
            public override bool Equals(object? obj)
            {
                throw new InvalidOperationException("Type Default does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }
            public override int GetHashCode()
            {
                throw new InvalidOperationException("Type Default does not implement Swift's Equatable protocol, so GetHashCode() is not supported.");
            }
            
            public static bool operator ==(Default left, Default right)
            {
                throw new InvalidOperationException("Type Default does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }
            
            public static bool operator !=(Default left, Default right)
            {
                throw new InvalidOperationException("Type Default does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }
            
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
                    {typeof(ISwiftImageDecoding), "$s4Nuke13ImageDecodersO7DefaultCAA0B8DecodingAAMc"}
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
            
            public unsafe Swift.Nuke.ImageDecoders.Default Init()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageDecoders.Default>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_init_4E382735(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageDecoders.Default>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO7DefaultCAEycfC")]
            private static extern void PInvoke_init_4E382735( SwiftIndirectResult swiftIndirectResult);
            
            
            public unsafe Swift.Nuke.ImageDecoders.Default? Init( Swift.Nuke.ImageDecodingContext context)
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageDecoders.Default?>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_init_42BFF4F5(swiftIndirectResult, context.Payload);
                    
                    var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Nuke.ImageDecoders.Default>>(new IntPtr(swiftIndirectResult.Value));
                    return swiftResult.ToNullable();
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO7DefaultC7contextAESgAA0B15DecodingContextV_tcfC")]
            private static extern void PInvoke_init_42BFF4F5( SwiftIndirectResult swiftIndirectResult,  SafeHandle context);
            
            
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
                    
                    PInvoke_decode_7940E1DC(swiftIndirectResult, arg0Swift, self, out var error);
                    
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
            private static extern void PInvoke_decode_7940E1DC( SwiftIndirectResult swiftIndirectResult,  Swift.Data arg0,  SwiftSelf self, out SwiftError error);
            
            
            public unsafe Swift.Nuke.ImageContainer? DecodePartiallyDownloadedData( Foundation.NSData arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                    
                    
                    var arg0Swift = Swift.Data.FromNSData(arg0);
                    
                    var result = PInvoke_decodePartiallyDownloadedData_1C97C2F7(arg0Swift, self);
                    
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
            private static extern IntPtr PInvoke_decodePartiallyDownloadedData_1C97C2F7( Swift.Data arg0,  SwiftSelf self);
            
            
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
                
                
                
                var result = PInvoke_rawValue_Get_3B0E40FE(self);
                
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
        private static extern Swift.SwiftString.Buffer PInvoke_rawValue_Get_3B0E40FE( SwiftSelf self);
        
        public Swift.SwiftString RawValue
        {
            get => RawValue_Get();
        }
        
        private static unsafe Swift.Nuke.AssetType Png_Get()
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.AssetType>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_png_Get_37088694(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV3pngACvgZ")]
        private static extern void PInvoke_png_Get_37088694( SwiftIndirectResult swiftIndirectResult);
        
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
                
                
                
                PInvoke_jpeg_Get_04645F92(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV4jpegACvgZ")]
        private static extern void PInvoke_jpeg_Get_04645F92( SwiftIndirectResult swiftIndirectResult);
        
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
                
                
                
                PInvoke_gif_Get_439B2F02(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV3gifACvgZ")]
        private static extern void PInvoke_gif_Get_439B2F02( SwiftIndirectResult swiftIndirectResult);
        
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
                
                
                
                PInvoke_heic_Get_1A54B51B(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV4heicACvgZ")]
        private static extern void PInvoke_heic_Get_1A54B51B( SwiftIndirectResult swiftIndirectResult);
        
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
                
                
                
                PInvoke_webp_Get_4F69B3C4(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV4webpACvgZ")]
        private static extern void PInvoke_webp_Get_4F69B3C4( SwiftIndirectResult swiftIndirectResult);
        
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
                
                
                
                PInvoke_mp4_Get_620EDDDE(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV3mp4ACvgZ")]
        private static extern void PInvoke_mp4_Get_620EDDDE( SwiftIndirectResult swiftIndirectResult);
        
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
                
                
                
                PInvoke_m4v_Get_6A268334(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV3m4vACvgZ")]
        private static extern void PInvoke_m4v_Get_6A268334( SwiftIndirectResult swiftIndirectResult);
        
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
                
                
                
                PInvoke_mov_Get_5BD7DEB9(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV3movACvgZ")]
        private static extern void PInvoke_mov_Get_5BD7DEB9( SwiftIndirectResult swiftIndirectResult);
        
        public static Swift.Nuke.AssetType Mov
        {
            get => Mov_Get();
        }
        
        private unsafe System.IntPtr HashValue_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_hashValue_Get_75A0671F(self);
                
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
        private static extern System.IntPtr PInvoke_hashValue_Get_75A0671F( SwiftSelf self);
        
        public System.IntPtr HashValue
        {
            get => HashValue_Get();
        }
        
        static nuint _payloadSize = SwiftObjectHelper<AssetType>.GetTypeMetadata().Size;
        SwiftSafeHandle<AssetType> _payload = SwiftSafeHandle<AssetType>.Zero;
        
        public SwiftSafeHandle<AssetType> Payload => _payload;
        
        public static System.Boolean operator ==(Swift.Nuke.AssetType arg0, Swift.Nuke.AssetType arg1)
        {
            return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
        }
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV2eeoiySbAC_ACtFZ")]
        private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
        
        public static bool operator !=(AssetType left, AssetType right)
        {
            return !(left == right);
        }
        
        public override bool Equals(object? obj)
        {
            return obj is AssetType other && Swift.Runtime.SwiftEquatable.Equals(this, other);
        }
        public override int GetHashCode()
        {
            throw new InvalidOperationException("Type AssetType does not implement Swift's Hashable protocol, so GetHashCode() is not supported.");
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
            PInvoke_init_780D01B0(swiftIndirectResult, rawValueDisposable.Buffer);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV8rawValueACSS_tcfC")]
        private static extern void PInvoke_init_780D01B0( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer rawValue);
        
        
        public unsafe void Hash( Swift.Hasher into)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_hash_5501F91A(into.Payload, self);
                
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
        private static extern void PInvoke_hash_5501F91A( SafeHandle into,  SwiftSelf self);
        
        
        public unsafe AssetType( Foundation.NSData arg0)
        {
            _payload = new SwiftSafeHandle<AssetType>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            var arg0Swift = Swift.Data.FromNSData(arg0);
            PInvoke_init_1953F9D6(swiftIndirectResult, arg0Swift);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeVyACSg10Foundation4DataVcfC")]
        private static extern void PInvoke_init_1953F9D6( SwiftIndirectResult swiftIndirectResult,  Swift.Data arg0);
        
        
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
                
                
                
                var result = PInvoke_taskId_Get_7EFB9B4F(self);
                
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
        private static extern System.Int64 PInvoke_taskId_Get_7EFB9B4F( SwiftSelf self);
        
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
                
                
                
                PInvoke_request_Get_178DC357(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_request_Get_178DC357( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Swift.Nuke.ImageRequest Request
        {
            get => Request_Get();
        }
        
        private unsafe Swift.Nuke.ImageRequest.Priority Priority_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Priority>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_priority_Get_5F134A6E(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Priority>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC8priorityAA0B7RequestV8PriorityOvg")]
        private static extern void PInvoke_priority_Get_5F134A6E( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Priority_Set( Swift.Nuke.ImageRequest.Priority value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_priority_Set_4B65DCF4(value.Payload, self);
                
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
        private static extern void PInvoke_priority_Set_4B65DCF4( SafeHandle value,  SwiftSelf self);
        
        public Swift.Nuke.ImageRequest.Priority Priority
        {
            get => Priority_Get();
            set => Priority_Set(value);
        }
        
        private unsafe Swift.Nuke.ImageTask.Progress CurrentProgress_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageTask.Progress>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_currentProgress_Get_3E210954(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageTask.Progress>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC15currentProgressAC0E0Vvg")]
        private static extern void PInvoke_currentProgress_Get_3E210954( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Swift.Nuke.ImageTask.Progress CurrentProgress
        {
            get => CurrentProgress_Get();
        }
        
        private unsafe Swift.Nuke.ImageTask.State State_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageTask.State>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_state_Get_4F8978AF(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageTask.State>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5stateAC5StateOvg")]
        private static extern void PInvoke_state_Get_4F8978AF( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Swift.Nuke.ImageTask.State StateValue
        {
            get => State_Get();
        }
        
        private unsafe UIKit.UIImage Image_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<UIKit.UIImage>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_image_Get_52F3ADAA(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<UIKit.UIImage>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5imageSo7UIImageCvg")]
        private static extern void PInvoke_image_Get_52F3ADAA( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public UIKit.UIImage Image
        {
            get => Image_Get();
        }
        
        private unsafe Swift.Nuke.ImageResponse Response_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageResponse>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_response_Get_334181B3(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageResponse>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC8responseAA0B8ResponseVvg")]
        private static extern void PInvoke_response_Get_334181B3( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Swift.Nuke.ImageResponse Response
        {
            get => Response_Get();
        }
        
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static unsafe byte progress_AsyncStream_OnElement(void* elementPtr, long context)
        {
            var stream = SwiftAsyncStream<Swift.Nuke.ImageTask.Progress>.FromContext(context);
            if (stream == null) return 0;
            var element = SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageTask.Progress>(new IntPtr(elementPtr));
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
        
        public IAsyncEnumerable<Swift.Nuke.ImageTask.Progress> ProgressValue
        {
            get
            {
                unsafe
                {
                    var stream = new SwiftAsyncStream<Swift.Nuke.ImageTask.Progress>();
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
                
                
                
                var result = PInvoke_description_Get_5E6402E6(self);
                
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
        private static extern Swift.SwiftString.Buffer PInvoke_description_Get_5E6402E6( SwiftSelf self);
        
        public Swift.SwiftString Description
        {
            get => Description_Get();
        }
        
        private unsafe System.IntPtr HashValue_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_hashValue_Get_1CDE31C6(self);
                
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
        private static extern System.IntPtr PInvoke_hashValue_Get_1CDE31C6( SwiftSelf self);
        
        public System.IntPtr HashValue
        {
            get => HashValue_Get();
        }
        
        static nuint _payloadSize = SwiftObjectHelper<ImageTask>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageTask> _payload = SwiftSafeHandle<ImageTask>.Zero;
        
        public SwiftSafeHandle<ImageTask> Payload => _payload;
        
        public static System.Boolean operator ==(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImageTask arg1)
        {
            return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
        }
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC2eeoiySbAC_ACtFZ")]
        private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
        
        public static bool operator !=(ImageTask left, ImageTask right)
        {
            return !(left == right);
        }
        
        public override bool Equals(object? obj)
        {
            return obj is ImageTask other && Swift.Runtime.SwiftEquatable.Equals(this, other);
        }
        public override int GetHashCode()
        {
            throw new InvalidOperationException("Type ImageTask does not implement Swift's Hashable protocol, so GetHashCode() is not supported.");
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
        
        public unsafe class Progress : ISwiftObject, IEquatable<Progress>
        {
            private unsafe System.Int64 Completed_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_completed_Get_0D9AF975(self);
                    
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
            private static extern System.Int64 PInvoke_completed_Get_0D9AF975( SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_total_Get_6F70FC6C(self);
                    
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
            private static extern System.Int64 PInvoke_total_Get_6F70FC6C( SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_fraction_Get_07A61121(self);
                    
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
            private static extern System.Single PInvoke_fraction_Get_07A61121( SwiftSelf self);
            
            public System.Single Fraction
            {
                get => Fraction_Get();
            }
            
            private unsafe System.IntPtr HashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_2588659B(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_2588659B( SwiftSelf self);
            
            public System.IntPtr HashValue
            {
                get => HashValue_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<Progress>.GetTypeMetadata().Size;
            SwiftSafeHandle<Progress> _payload = SwiftSafeHandle<Progress>.Zero;
            
            public SwiftSafeHandle<Progress> Payload => _payload;
            
            public static System.Boolean operator ==(Swift.Nuke.ImageTask.Progress arg0, Swift.Nuke.ImageTask.Progress arg1)
            {
                return PInvoke_op_Equality(arg0.Payload, arg1.Payload);
            }
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC8ProgressV2eeoiySbAE_AEtFZ")]
            private static extern System.Boolean PInvoke_op_Equality( SafeHandle arg0,  SafeHandle arg1);
            
            public static bool operator !=(Progress left, Progress right)
            {
                return !(left == right);
            }
            
            public override bool Equals(object? obj)
            {
                return obj is Progress other && Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            public override int GetHashCode()
            {
                throw new InvalidOperationException("Type Progress does not implement Swift's Hashable protocol, so GetHashCode() is not supported.");
            }
            
            public bool Equals(Progress? other)
            {
                return Swift.Runtime.SwiftEquatable.Equals(this, other);
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC8ProgressVMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new Progress(handle);
            }
            
            Progress(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<Progress>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<Progress>.GetTypeMetadata();
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
            static Progress()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IEquatable<Progress>), "$s4Nuke9ImageTaskC8ProgressVSQAAMc"}
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
            
            
            public unsafe Progress( System.Int64 completed,  System.Int64 total)
            {
                _payload = new SwiftSafeHandle<Progress>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_6BFAD5FA(swiftIndirectResult, completed, total);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC8ProgressV9completed5totalAEs5Int64V_AItcfC")]
            private static extern void PInvoke_init_6BFAD5FA( SwiftIndirectResult swiftIndirectResult,  System.Int64 completed,  System.Int64 total);
            
            
            public unsafe void Hash( Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_0A6BFD4E(into.Payload, self);
                    
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
            private static extern void PInvoke_hash_0A6BFD4E( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        public unsafe class State : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<State>.GetTypeMetadata().Size;
            SwiftSafeHandle<State> _payload = SwiftSafeHandle<State>.Zero;
            public SwiftSafeHandle<State> Payload => _payload;
            
            /// <summary>
            /// Gets the 'running' case of State.
            /// </summary>
            public static State Running
            {
                get
                {
                    var result = new State();
                    var metadata = PInvoke_getMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    var indirectResult = new SwiftIndirectResult((void*)buffer);
                    PInvoke_Running(indirectResult);
                    result._payload = new SwiftSafeHandle<State>(buffer);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5StateO7runningyA2EmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_Running(SwiftIndirectResult result);
            
            /// <summary>
            /// Gets the 'cancelled' case of State.
            /// </summary>
            public static State Cancelled
            {
                get
                {
                    var result = new State();
                    var metadata = PInvoke_getMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    var indirectResult = new SwiftIndirectResult((void*)buffer);
                    PInvoke_Cancelled(indirectResult);
                    result._payload = new SwiftSafeHandle<State>(buffer);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5StateO9cancelledyA2EmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_Cancelled(SwiftIndirectResult result);
            
            /// <summary>
            /// Gets the 'completed' case of State.
            /// </summary>
            public static State Completed
            {
                get
                {
                    var result = new State();
                    var metadata = PInvoke_getMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    var indirectResult = new SwiftIndirectResult((void*)buffer);
                    PInvoke_Completed(indirectResult);
                    result._payload = new SwiftSafeHandle<State>(buffer);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5StateO9completedyA2EmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_Completed(SwiftIndirectResult result);
            
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
                        var metadata = SwiftObjectHelper<State>.GetTypeMetadata();
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
            
            
            private unsafe System.IntPtr HashValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_6159F442(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5StateO9hashValueSivg")]
            private static extern System.IntPtr PInvoke_hashValue_Get_6159F442( SwiftSelf self);
            
            public System.IntPtr HashValue
            {
                get => HashValue_Get();
            }
            
            static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5StateOMa")]
            internal static extern TypeMetadata PInvoke_getMetadata();
            
            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
            {
                return new State(handle);
            }
            
            State()
            {
            }
            
            State(SwiftHandle handle)
            {
                _payload = new SwiftSafeHandle<State>(handle);
            }
            
            unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                var metadata = SwiftObjectHelper<State>.GetTypeMetadata();
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
            static State()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {typeof(IEquatable<State>), "$s4Nuke9ImageTaskC5StateOSQAAMc"}
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
            
            public unsafe void Hash( Swift.Hasher into)
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_5D62A5F4(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5StateO4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_5D62A5F4( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        public unsafe class Event : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<Event>.GetTypeMetadata().Size;
            SwiftSafeHandle<Event> _payload = SwiftSafeHandle<Event>.Zero;
            public SwiftSafeHandle<Event> Payload => _payload;
            
            /// <summary>
            /// Creates the 'progress' case of Event.
            /// </summary>
            public static Event Progress(Swift.Nuke.ImageTask.Progress value0)
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
            private static extern void PInvoke_Progress(SwiftIndirectResult result, Swift.Nuke.ImageTask.Progress value0);
            
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
                    var metadata = PInvoke_getMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    var indirectResult = new SwiftIndirectResult((void*)buffer);
                    PInvoke_Cancelled(indirectResult);
                    result._payload = new SwiftSafeHandle<Event>(buffer);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5EventO9cancelledyA2EmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_Cancelled(SwiftIndirectResult result);
            
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
            public unsafe bool TryGetProgress([MaybeNullWhen(false)] out Swift.Nuke.ImageTask.Progress value)
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
                value = SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageTask.Progress>(new IntPtr(enumCopy));
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
                
                
                
                PInvoke_cancel_33B5E6D1(self);
                
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
        private static extern void PInvoke_cancel_33B5E6D1( SwiftSelf self);
        
        
        public unsafe void Hash( Swift.Hasher into)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_hash_3ADB8F6F(into.Payload, self);
                
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
        private static extern void PInvoke_hash_3ADB8F6F( SafeHandle into,  SwiftSelf self);
        
        
    }
    
    
    public interface ISwiftImageDecoding
    {
        System.Boolean isAsynchronous { get; }
        Swift.Nuke.ImageContainer decode(Swift.Data arg0);
        Swift.SwiftOptional<Swift.Nuke.ImageContainer> decodePartiallyDownloadedData(Swift.Data arg0);
    }
    
    /// <summary>
    /// Proxy class that enables C# implementations of the ImageDecoding protocol.
    /// Can wrap either a C# implementation or receive Swift existential containers.
    /// </summary>
    public unsafe class ImageDecodingProxy : ISwiftImageDecoding, ISwiftObject
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
        private readonly ISwiftImageDecoding? _csharpImpl;
        private readonly EveryProtocol? _everyProtocol;
        private readonly ExistentialContainer1 _swiftContainer;
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
            var result = proxy._csharpImpl!.isAsynchronous;
            return MarshalToSwiftBuffer(result);
        }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_decode_0(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImageDecodingProxy>(container);
            var param0 = MarshalFromSwift<Swift.Data>(rawArg0);
            var result = proxy._csharpImpl!.decode(param0);
            return MarshalToSwiftBuffer(result);
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr Receive_decodePartiallyDownloadedData_1(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0)
        {
            var container = *(ExistentialContainer1*)selfContainer;
            var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImageDecodingProxy>(container);
            var param0 = MarshalFromSwift<Swift.Data>(rawArg0);
            var result = proxy._csharpImpl!.decodePartiallyDownloadedData(param0);
            return MarshalToSwiftBuffer(result);
        }
        
        #endregion
        
        /// <summary>
        /// Creates a proxy wrapping a C# implementation of ISwiftImageDecoding.
        /// </summary>
        /// <param name="implementation">The C# implementation of the protocol.</param>
        public ImageDecodingProxy(ISwiftImageDecoding implementation)
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
        /// <param name="container">The Swift existential container.</param>
        public ImageDecodingProxy(ExistentialContainer1 container)
        {
            _swiftContainer = container;
            _csharpImpl = null;
            _everyProtocol = null;
        }
        #region Interface Implementation
        
        public System.Boolean isAsynchronous
        {
            get
            {
                if (_csharpImpl != null)
                    return _csharpImpl.isAsynchronous;
                // TODO: Call Swift via P/Invoke for Swift implementation
                throw new NotImplementedException("Swift implementation not yet supported");
            }
        }
        
        public Swift.Nuke.ImageContainer decode(Swift.Data arg0)
        {
            if (_csharpImpl != null)
                return _csharpImpl.decode(arg0);
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
        }
        
        public Swift.SwiftOptional<Swift.Nuke.ImageContainer> decodePartiallyDownloadedData(Swift.Data arg0)
        {
            if (_csharpImpl != null)
                return _csharpImpl.decodePartiallyDownloadedData(arg0);
            // TODO: Call Swift via P/Invoke for Swift implementation
            throw new NotImplementedException("Swift implementation not yet supported");
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
            throw new NotImplementedException("Protocol conformance descriptor not available for proxy types");
        }
        #endregion
        #region Marshalling Helpers
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
        }
    }
    
    
    public unsafe class ImageDecodingError : ISwiftObject
    {
        static nuint _payloadSize = SwiftObjectHelper<ImageDecodingError>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageDecodingError> _payload = SwiftSafeHandle<ImageDecodingError>.Zero;
        public SwiftSafeHandle<ImageDecodingError> Payload => _payload;
        
        /// <summary>
        /// Gets the 'unknown' case of ImageDecodingError.
        /// </summary>
        public static ImageDecodingError Unknown
        {
            get
            {
                var result = new ImageDecodingError();
                var metadata = PInvoke_getMetadata();
                IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                var indirectResult = new SwiftIndirectResult((void*)buffer);
                PInvoke_Unknown(indirectResult);
                result._payload = new SwiftSafeHandle<ImageDecodingError>(buffer);
                return result;
            }
        }
        
        [DllImport("Nuke", EntryPoint = "$s4Nuke18ImageDecodingErrorO7unknownyA2CmF")]
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        private static extern void PInvoke_Unknown(SwiftIndirectResult result);
        
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
                
                
                
                var result = PInvoke_description_Get_76AC29C9(self);
                
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
        private static extern Swift.SwiftString.Buffer PInvoke_description_Get_76AC29C9( SwiftSelf self);
        
        public Swift.SwiftString Description
        {
            get => Description_Get();
        }
        
        private unsafe System.IntPtr HashValue_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_hashValue_Get_1C9155F5(self);
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke18ImageDecodingErrorO9hashValueSivg")]
        private static extern System.IntPtr PInvoke_hashValue_Get_1C9155F5( SwiftSelf self);
        
        public System.IntPtr HashValue
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
        
        public unsafe void Hash( Swift.Hasher into)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_hash_0E5A5E84(into.Payload, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke18ImageDecodingErrorO4hash4intoys6HasherVz_tF")]
        private static extern void PInvoke_hash_0E5A5E84( SafeHandle into,  SwiftSelf self);
        
        
    }
    
    
    public unsafe class ImageEncoders : ISwiftObject
    {
        static nuint _payloadSize = SwiftObjectHelper<ImageEncoders>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageEncoders> _payload = SwiftSafeHandle<ImageEncoders>.Zero;
        public SwiftSafeHandle<ImageEncoders> Payload => _payload;
        
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
                    
                    
                    
                    PInvoke_type_Get_7866E5F2(swiftIndirectResult, self);
                    
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
            private static extern void PInvoke_type_Get_7866E5F2( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_compressionRatio_Get_0B0D49B0(self);
                    
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
            private static extern System.Single PInvoke_compressionRatio_Get_0B0D49B0( SwiftSelf self);
            
            public System.Single CompressionRatio
            {
                get => CompressionRatio_Get();
            }
            
            static nuint _payloadSize = SwiftObjectHelper<ImageIO>.GetTypeMetadata().Size;
            SwiftSafeHandle<ImageIO> _payload = SwiftSafeHandle<ImageIO>.Zero;
            
            public SwiftSafeHandle<ImageIO> Payload => _payload;
            
            // Swift structs cannot be compared using .NET's default equality semantics,
            // since Swift's equality is defined by the Equatable protocol.
            // This type does not implement Swift's Equatable protocol.
            public override bool Equals(object? obj)
            {
                throw new InvalidOperationException("Type ImageIO does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }
            public override int GetHashCode()
            {
                throw new InvalidOperationException("Type ImageIO does not implement Swift's Equatable protocol, so GetHashCode() is not supported.");
            }
            
            public static bool operator ==(ImageIO left, ImageIO right)
            {
                throw new InvalidOperationException("Type ImageIO does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }
            
            public static bool operator !=(ImageIO left, ImageIO right)
            {
                throw new InvalidOperationException("Type ImageIO does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }
            
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
                    {typeof(ISwiftImageEncoding), "$s4Nuke13ImageEncodersO0B2IOVAA0B8EncodingAAMc"}
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
                
                PInvoke_init_60B537D5(swiftIndirectResult, type.Payload, compressionRatio);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageEncodersO0B2IOV4type16compressionRatioAeA9AssetTypeV_SftcfC")]
            private static extern void PInvoke_init_60B537D5( SwiftIndirectResult swiftIndirectResult,  SafeHandle type,  System.Single compressionRatio);
            
            
            public static System.Boolean IsSupported( Swift.Nuke.AssetType type)
            {
                try
                {
                    
                    
                    var result = PInvoke_isSupported_6A5C5E53(type.Payload);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageEncodersO0B2IOV11isSupported4typeSbAA9AssetTypeV_tFZ")]
            private static extern System.Boolean PInvoke_isSupported_6A5C5E53( SafeHandle type);
            
            
            public unsafe Swift.Data? Encode( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_encode_0043F293(arg0Handle, self);
                    
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
            private static extern IntPtr PInvoke_encode_0043F293( IntPtr arg0,  SwiftSelf self);
            
            
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
                    
                    
                    
                    var result = PInvoke_compressionQuality_Get_764C57D7(self);
                    
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
            private static extern System.Single PInvoke_compressionQuality_Get_764C57D7( SwiftSelf self);
            
            private unsafe void CompressionQuality_Set( System.Single value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_compressionQuality_Set_04C8A115(value, self);
                    
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
            private static extern void PInvoke_compressionQuality_Set_04C8A115( System.Single value,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_isHEIFPreferred_Get_1060FFCF(self);
                    
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
            private static extern System.Boolean PInvoke_isHEIFPreferred_Get_1060FFCF( SwiftSelf self);
            
            private unsafe void IsHEIFPreferred_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isHEIFPreferred_Set_497A797B(value, self);
                    
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
            private static extern void PInvoke_isHEIFPreferred_Set_497A797B( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean IsHEIFPreferred
            {
                get => IsHEIFPreferred_Get();
                set => IsHEIFPreferred_Set(value);
            }
            
            static nuint _payloadSize = SwiftObjectHelper<Default>.GetTypeMetadata().Size;
            SwiftSafeHandle<Default> _payload = SwiftSafeHandle<Default>.Zero;
            
            public SwiftSafeHandle<Default> Payload => _payload;
            
            // Swift structs cannot be compared using .NET's default equality semantics,
            // since Swift's equality is defined by the Equatable protocol.
            // This type does not implement Swift's Equatable protocol.
            public override bool Equals(object? obj)
            {
                throw new InvalidOperationException("Type Default does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }
            public override int GetHashCode()
            {
                throw new InvalidOperationException("Type Default does not implement Swift's Equatable protocol, so GetHashCode() is not supported.");
            }
            
            public static bool operator ==(Default left, Default right)
            {
                throw new InvalidOperationException("Type Default does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }
            
            public static bool operator !=(Default left, Default right)
            {
                throw new InvalidOperationException("Type Default does not implement Swift's Equatable protocol, so equality comparison is not supported.");
            }
            
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
                    {typeof(ISwiftImageEncoding), "$s4Nuke13ImageEncodersO7DefaultVAA0B8EncodingAAMc"}
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
                
                PInvoke_init_12A0CB23(swiftIndirectResult, compressionQuality);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageEncodersO7DefaultV18compressionQualityAESf_tcfC")]
            private static extern void PInvoke_init_12A0CB23( SwiftIndirectResult swiftIndirectResult,  System.Single compressionQuality);
            
            
            public unsafe Swift.Data? Encode( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_encode_7E4FD899(arg0Handle, self);
                    
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
            private static extern IntPtr PInvoke_encode_7E4FD899( IntPtr arg0,  SwiftSelf self);
            
            
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
                
                
                
                var result = PInvoke_isPaused_Get_268EECC6(self);
                
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
        private static extern System.Boolean PInvoke_isPaused_Get_268EECC6( SwiftSelf self);
        
        private unsafe void IsPaused_Set( System.Boolean value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_isPaused_Set_54DBCCF3(value, self);
                
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
        private static extern void PInvoke_isPaused_Set_54DBCCF3( System.Boolean value,  SwiftSelf self);
        
        public System.Boolean IsPaused
        {
            get => IsPaused_Get();
            set => IsPaused_Set(value);
        }
        
        private unsafe Swift.Nuke.ImageRequest.Priority Priority_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Priority>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_priority_Get_578E1F9B(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Priority>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC8priorityAA0B7RequestV8PriorityOvg")]
        private static extern void PInvoke_priority_Get_578E1F9B( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Priority_Set( Swift.Nuke.ImageRequest.Priority value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_priority_Set_7AA5B53E(value.Payload, self);
                
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
        private static extern void PInvoke_priority_Set_7AA5B53E( SafeHandle value,  SwiftSelf self);
        
        public Swift.Nuke.ImageRequest.Priority Priority
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
                
                
                
                var result = PInvoke_didComplete_Get_08EB6C07(self);
                
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
        private static extern SwiftClosureData PInvoke_didComplete_Get_08EB6C07( SwiftSelf self);
        
        private static unsafe readonly delegate* unmanaged[Swift]<SwiftSelf, void> s_didComplete_Set_value_22307C0B_Callback = &didComplete_Set_value_22307C0B_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void didComplete_Set_value_22307C0B_Callback(SwiftSelf context)
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
                    valueClosure = new SwiftClosureData((IntPtr)s_didComplete_Set_value_22307C0B_Callback, GCHandle.ToIntPtr(valueHandle));
                }
                else
                {
                    valueClosure = default; // Zero-initialized = nil in Swift
                }
                
                PInvoke_didComplete_Set_22307C0B(valueClosure, self);
                
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
        private static extern void PInvoke_didComplete_Set_22307C0B( SwiftClosureData value,  SwiftSelf self);
        
        public Action? DidComplete
        {
            get => DidComplete_Get();
            set => DidComplete_Set(value);
        }
        
        static nuint _payloadSize = SwiftObjectHelper<ImagePrefetcher>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImagePrefetcher> _payload = SwiftSafeHandle<ImagePrefetcher>.Zero;
        
        public SwiftSafeHandle<ImagePrefetcher> Payload => _payload;
        
        // Swift classes cannot be compared using .NET's default equality semantics,
        // since Swift's equality is defined by the Equatable protocol.
        // This type does not implement Swift's Equatable protocol.
        public override bool Equals(object? obj)
        {
            throw new InvalidOperationException("Type ImagePrefetcher does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        public override int GetHashCode()
        {
            throw new InvalidOperationException("Type ImagePrefetcher does not implement Swift's Equatable protocol, so GetHashCode() is not supported.");
        }
        
        public static bool operator ==(ImagePrefetcher left, ImagePrefetcher right)
        {
            throw new InvalidOperationException("Type ImagePrefetcher does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        
        public static bool operator !=(ImagePrefetcher left, ImagePrefetcher right)
        {
            throw new InvalidOperationException("Type ImagePrefetcher does not implement Swift's Equatable protocol, so equality comparison is not supported.");
        }
        
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
            public SwiftSafeHandle<Destination> Payload => _payload;
            
            /// <summary>
            /// Gets the 'memoryCache' case of Destination.
            /// </summary>
            public static Destination MemoryCache
            {
                get
                {
                    var result = new Destination();
                    var metadata = PInvoke_getMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    var indirectResult = new SwiftIndirectResult((void*)buffer);
                    PInvoke_MemoryCache(indirectResult);
                    result._payload = new SwiftSafeHandle<Destination>(buffer);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC11DestinationO11memoryCacheyA2EmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_MemoryCache(SwiftIndirectResult result);
            
            /// <summary>
            /// Gets the 'diskCache' case of Destination.
            /// </summary>
            public static Destination DiskCache
            {
                get
                {
                    var result = new Destination();
                    var metadata = PInvoke_getMetadata();
                    IntPtr buffer = (IntPtr)NativeMemory.Alloc(metadata.Size);
                    var indirectResult = new SwiftIndirectResult((void*)buffer);
                    PInvoke_DiskCache(indirectResult);
                    result._payload = new SwiftSafeHandle<Destination>(buffer);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC11DestinationO9diskCacheyA2EmF")]
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            private static extern void PInvoke_DiskCache(SwiftIndirectResult result);
            
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
            
            
            private unsafe System.IntPtr HashValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_3C1A08A5(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC11DestinationO9hashValueSivg")]
            private static extern System.IntPtr PInvoke_hashValue_Get_3C1A08A5( SwiftSelf self);
            
            public System.IntPtr HashValue
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
            
            public unsafe void Hash( Swift.Hasher into)
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_2B9B24A0(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC11DestinationO4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_2B9B24A0( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        public unsafe Swift.Nuke.ImagePrefetcher Init( Swift.Nuke.ImagePipeline pipeline,  Swift.Nuke.ImagePrefetcher.Destination destination,  System.IntPtr maxConcurrentRequestCount)
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePrefetcher>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_init_56FE1AD8(swiftIndirectResult, pipeline.Payload, destination.Payload, maxConcurrentRequestCount);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePrefetcher>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC8pipeline11destination25maxConcurrentRequestCountAcA0B8PipelineC_AC11DestinationOSitcfC")]
        private static extern void PInvoke_init_56FE1AD8( SwiftIndirectResult swiftIndirectResult,  SafeHandle pipeline,  SafeHandle destination,  System.IntPtr maxConcurrentRequestCount);
        
        
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
                
                PInvoke_startPrefetching_06DEDF40(withBuffer, self);
                
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
        private static extern void PInvoke_startPrefetching_06DEDF40( IntPtr withBuffer,  SwiftSelf self);
        
        
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
                
                PInvoke__startPrefetching_18DA9472(withBuffer, self);
                
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
        private static extern void PInvoke__startPrefetching_18DA9472( IntPtr withBuffer,  SwiftSelf self);
        
        
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
                
                PInvoke_stopPrefetching_70BD10B6(withBuffer, self);
                
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
        private static extern void PInvoke_stopPrefetching_70BD10B6( IntPtr withBuffer,  SwiftSelf self);
        
        
        public unsafe void StopPrefetching()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf(*(void**)_payload.DangerousGetHandle());
                
                
                
                PInvoke_stopPrefetching_296FB45D(self);
                
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
        private static extern void PInvoke_stopPrefetching_296FB45D( SwiftSelf self);
        
        
    }
    
    
}
