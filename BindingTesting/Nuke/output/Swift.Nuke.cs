using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;
using Swift;
using Swift.Runtime;
using Swift.Runtime.InteropServices;

namespace Swift.Nuke
{
    public interface ISwiftImageProcessing
    {
        Swift.SwiftString identifier { get; }
        Swift.AnyType hashableIdentifier { get; }
        Swift.SwiftOptional<Swift.NSImage> process(Swift.NSImage arg0);
        Swift.Nuke.ImageContainer process(Swift.Nuke.ImageContainer arg0, Swift.Nuke.ImageProcessingContext context);
    }
    
    
    public unsafe class ImageProcessingContext : ISwiftObject
    {
        private unsafe Swift.Nuke.ImageRequest request_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_request_Get_43DD1058(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_request_Get_43DD1058( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void request_Set( Swift.Nuke.ImageRequest value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_request_Set_49501ECA(value.Payload, self);
                
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
        private static extern void PInvoke_request_Set_49501ECA( SafeHandle value,  SwiftSelf self);
        
        public Swift.Nuke.ImageRequest request
        {
            get => request_Get();
            set => request_Set(value);
        }
        
        private unsafe Swift.Nuke.ImageResponse response_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageResponse>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_response_Get_1790DBBA(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_response_Get_1790DBBA( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void response_Set( Swift.Nuke.ImageResponse value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_response_Set_4EC95DBE(value.Payload, self);
                
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
        private static extern void PInvoke_response_Set_4EC95DBE( SafeHandle value,  SwiftSelf self);
        
        public Swift.Nuke.ImageResponse response
        {
            get => response_Get();
            set => response_Set(value);
        }
        
        private unsafe System.Boolean isCompleted_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_isCompleted_Get_2EC9F273(self);
                
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
        private static extern System.Boolean PInvoke_isCompleted_Get_2EC9F273( SwiftSelf self);
        
        private unsafe void isCompleted_Set( System.Boolean value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_isCompleted_Set_5741DB44(value, self);
                
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
        private static extern void PInvoke_isCompleted_Set_5741DB44( System.Boolean value,  SwiftSelf self);
        
        public System.Boolean isCompleted
        {
            get => isCompleted_Get();
            set => isCompleted_Set(value);
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
            
            PInvoke_init_09299A8F(swiftIndirectResult, request.Payload, response.Payload, isCompleted);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingContextV7request8response11isCompletedAcA0B7RequestV_AA0B8ResponseVSbtcfC")]
        private static extern void PInvoke_init_09299A8F( SwiftIndirectResult swiftIndirectResult,  SafeHandle request,  SafeHandle response,  System.Boolean isCompleted);
        
        
    }
    
    
    public unsafe class ImageProcessingError : ISwiftObject
    {
        static nuint _payloadSize = SwiftObjectHelper<ImageProcessingError>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageProcessingError> _payload = SwiftSafeHandle<ImageProcessingError>.Zero;
        public SwiftSafeHandle<ImageProcessingError> Payload => _payload;
        
        /// <summary>
        /// Gets the 'unknown' case of ImageProcessingError.
        /// </summary>
        public static ImageProcessingError Unknown => throw new NotImplementedException("Enum case constructors not yet implemented");
        
        
        private unsafe Swift.SwiftString description_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_description_Get_4945AC84(self);
                
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
        private static extern Swift.SwiftString.Buffer PInvoke_description_Get_4945AC84( SwiftSelf self);
        
        public Swift.SwiftString description
        {
            get => description_Get();
        }
        
        private unsafe System.IntPtr hashValue_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_hashValue_Get_042FFE7E(self);
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageProcessingErrorO9hashValueSivg")]
        private static extern System.IntPtr PInvoke_hashValue_Get_042FFE7E( SwiftSelf self);
        
        public System.IntPtr hashValue
        {
            get => hashValue_Get();
        }
        
                        static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException("Enum type metadata not yet implemented");
                        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                            where TProtocol : class
                        {
                            throw new NotImplementedException("Enum protocol conformance not yet implemented");
                        }
                        int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                        {
                            throw new NotImplementedException("Enum marshalling not yet implemented");
                        }
                        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                        {
                            throw new NotImplementedException("Enum NewFromPayload not yet implemented");
                        }
                    
        
    }
    
    
    public unsafe class ImageResponse : ISwiftObject
    {
        private unsafe Swift.Nuke.ImageContainer container_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageContainer>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_container_Get_508CD1D0(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_container_Get_508CD1D0( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void container_Set( Swift.Nuke.ImageContainer value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_container_Set_05F70F6F(value.Payload, self);
                
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
        private static extern void PInvoke_container_Set_05F70F6F( SafeHandle value,  SwiftSelf self);
        
        public Swift.Nuke.ImageContainer container
        {
            get => container_Get();
            set => container_Set(value);
        }
        
        private unsafe Swift.NSImage image_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.NSImage>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_image_Get_29B0FCFF(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.NSImage>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV5imageSo7NSImageCvg")]
        private static extern void PInvoke_image_Get_29B0FCFF( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Swift.NSImage image
        {
            get => image_Get();
        }
        
        private unsafe System.Boolean isPreview_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_isPreview_Get_01B573C5(self);
                
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
        private static extern System.Boolean PInvoke_isPreview_Get_01B573C5( SwiftSelf self);
        
        public System.Boolean isPreview
        {
            get => isPreview_Get();
        }
        
        private unsafe Swift.Nuke.ImageRequest request_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_request_Get_1B5371FE(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_request_Get_1B5371FE( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void request_Set( Swift.Nuke.ImageRequest value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_request_Set_5C9D2656(value.Payload, self);
                
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
        private static extern void PInvoke_request_Set_5C9D2656( SafeHandle value,  SwiftSelf self);
        
        public Swift.Nuke.ImageRequest request
        {
            get => request_Get();
            set => request_Set(value);
        }
        
        private unsafe Swift.SwiftOptional<Swift.Nuke.ImageResponse.CacheType> cacheType_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_cacheType_Get_6C0EA55F(self);
                
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
        private static extern IntPtr PInvoke_cacheType_Get_6C0EA55F( SwiftSelf self);
        
        private unsafe void cacheType_Set( Swift.SwiftOptional<Swift.Nuke.ImageResponse.CacheType> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_cacheType_Set_3079EF18(valueBuffer, self);
                
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
        private static extern void PInvoke_cacheType_Set_3079EF18( IntPtr valueBuffer,  SwiftSelf self);
        
        public Swift.SwiftOptional<Swift.Nuke.ImageResponse.CacheType> cacheType
        {
            get => cacheType_Get();
            set => cacheType_Set(value);
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
            public static CacheType Memory => throw new NotImplementedException("Enum case constructors not yet implemented");
            
            /// <summary>
            /// Gets the 'disk' case of CacheType.
            /// </summary>
            public static CacheType Disk => throw new NotImplementedException("Enum case constructors not yet implemented");
            
            
            private unsafe System.IntPtr hashValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_3F8A33DD(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV9CacheTypeO9hashValueSivg")]
            private static extern System.IntPtr PInvoke_hashValue_Get_3F8A33DD( SwiftSelf self);
            
            public System.IntPtr hashValue
            {
                get => hashValue_Get();
            }
            
                            static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException("Enum type metadata not yet implemented");
                            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                                where TProtocol : class
                            {
                                throw new NotImplementedException("Enum protocol conformance not yet implemented");
                            }
                            int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                            {
                                throw new NotImplementedException("Enum marshalling not yet implemented");
                            }
                            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                            {
                                throw new NotImplementedException("Enum NewFromPayload not yet implemented");
                            }
                        
            
        }
        
        
        
    }
    
    
    public unsafe class ImageCache : ISwiftObject {
        SwiftSafeHandle<ImageCache> _payload = SwiftSafeHandle<ImageCache>.Zero;
        
        public SwiftSafeHandle<ImageCache> Payload => _payload;
        
        
                static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException();

                static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                    where TProtocol : class
                {
                    throw new NotImplementedException();
                }

                int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                {
                    throw new NotImplementedException();
                }
                static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                {
                    throw new NotImplementedException();
                }
            
        public unsafe Swift.Nuke.ImageCache init( System.IntPtr costLimit,  System.IntPtr countLimit)
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageCache>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_init_54983121(swiftIndirectResult, costLimit, countLimit);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageCache>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC9costLimit05countE0ACSi_SitcfC")]
        private static extern void PInvoke_init_54983121( SwiftIndirectResult swiftIndirectResult,  System.IntPtr costLimit,  System.IntPtr countLimit);
        
        
        public static System.IntPtr defaultCostLimit()
        {
            try
            {
                
                
                var result = PInvoke_defaultCostLimit_2D68D946();
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC16defaultCostLimitSiyFZ")]
        private static extern System.IntPtr PInvoke_defaultCostLimit_2D68D946();
        
        
        public unsafe void removeAll()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_removeAll_6893711B(self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC9removeAllyyF")]
        private static extern void PInvoke_removeAll_6893711B( SwiftSelf self);
        
        
        public unsafe void trim( System.IntPtr toCost)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_trim_34981804(toCost, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC4trim6toCostySi_tF")]
        private static extern void PInvoke_trim_34981804( System.IntPtr toCost,  SwiftSelf self);
        
        
    }
    
    
    public unsafe class ImagePipeline : ISwiftObject {
        SwiftSafeHandle<ImagePipeline> _payload = SwiftSafeHandle<ImagePipeline>.Zero;
        
        public SwiftSafeHandle<ImagePipeline> Payload => _payload;
        
        
                static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException();

                static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                    where TProtocol : class
                {
                    throw new NotImplementedException();
                }

                int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                {
                    throw new NotImplementedException();
                }
                static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                {
                    throw new NotImplementedException();
                }
            
        public unsafe class Error : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<Error>.GetTypeMetadata().Size;
            SwiftSafeHandle<Error> _payload = SwiftSafeHandle<Error>.Zero;
            public SwiftSafeHandle<Error> Payload => _payload;
            
            /// <summary>
            /// Gets the 'dataMissingInCache' case of Error.
            /// </summary>
            public static Error DataMissingInCache => throw new NotImplementedException("Enum case constructors not yet implemented");
            
            /// <summary>
            /// Gets the 'dataIsEmpty' case of Error.
            /// </summary>
            public static Error DataIsEmpty => throw new NotImplementedException("Enum case constructors not yet implemented");
            
            /// <summary>
            /// Gets the 'imageRequestMissing' case of Error.
            /// </summary>
            public static Error ImageRequestMissing => throw new NotImplementedException("Enum case constructors not yet implemented");
            
            /// <summary>
            /// Gets the 'pipelineInvalidated' case of Error.
            /// </summary>
            public static Error PipelineInvalidated => throw new NotImplementedException("Enum case constructors not yet implemented");
            
            
            private unsafe Swift.SwiftString description_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_0DF4F9AC(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_0DF4F9AC( SwiftSelf self);
            
            public Swift.SwiftString description
            {
                get => description_Get();
            }
            
                            static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException("Enum type metadata not yet implemented");
                            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                                where TProtocol : class
                            {
                                throw new NotImplementedException("Enum protocol conformance not yet implemented");
                            }
                            int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                            {
                                throw new NotImplementedException("Enum marshalling not yet implemented");
                            }
                            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                            {
                                throw new NotImplementedException("Enum NewFromPayload not yet implemented");
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
                private unsafe System.IntPtr rawValue_Get()
                {
                    var success = false;
                    _payload.DangerousAddRef(ref success);
                    try
                    {
                        var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                        
                        
                        
                        var result = PInvoke_rawValue_Get_6010B3CC(self);
                        
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
                private static extern System.IntPtr PInvoke_rawValue_Get_6010B3CC( SwiftSelf self);
                
                public System.IntPtr rawValue
                {
                    get => rawValue_Get();
                }
                
                private static unsafe Swift.Nuke.ImagePipeline.Cache.Caches memory_Get()
                {
                    try
                    {
                        var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.Cache.Caches>();
                        var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                        var swiftIndirectResult = new SwiftIndirectResult(payload);
                        
                        
                        
                        PInvoke_memory_Get_6622D6F8(swiftIndirectResult);
                        
                        return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.Cache.Caches>(new IntPtr(swiftIndirectResult.Value));
                    }
                    
                    finally
                    {
                    }
                    
                }
                
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV6CachesV6memoryAGvgZ")]
                private static extern void PInvoke_memory_Get_6622D6F8( SwiftIndirectResult swiftIndirectResult);
                
                public static Swift.Nuke.ImagePipeline.Cache.Caches memory
                {
                    get => memory_Get();
                }
                
                private static unsafe Swift.Nuke.ImagePipeline.Cache.Caches disk_Get()
                {
                    try
                    {
                        var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.Cache.Caches>();
                        var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                        var swiftIndirectResult = new SwiftIndirectResult(payload);
                        
                        
                        
                        PInvoke_disk_Get_4B9AF0AF(swiftIndirectResult);
                        
                        return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.Cache.Caches>(new IntPtr(swiftIndirectResult.Value));
                    }
                    
                    finally
                    {
                    }
                    
                }
                
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV6CachesV4diskAGvgZ")]
                private static extern void PInvoke_disk_Get_4B9AF0AF( SwiftIndirectResult swiftIndirectResult);
                
                public static Swift.Nuke.ImagePipeline.Cache.Caches disk
                {
                    get => disk_Get();
                }
                
                private static unsafe Swift.Nuke.ImagePipeline.Cache.Caches all_Get()
                {
                    try
                    {
                        var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.Cache.Caches>();
                        var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                        var swiftIndirectResult = new SwiftIndirectResult(payload);
                        
                        
                        
                        PInvoke_all_Get_0BC708A5(swiftIndirectResult);
                        
                        return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.Cache.Caches>(new IntPtr(swiftIndirectResult.Value));
                    }
                    
                    finally
                    {
                    }
                    
                }
                
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV6CachesV3allAGvgZ")]
                private static extern void PInvoke_all_Get_0BC708A5( SwiftIndirectResult swiftIndirectResult);
                
                public static Swift.Nuke.ImagePipeline.Cache.Caches all
                {
                    get => all_Get();
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
                    
                    PInvoke_init_7C85736B(swiftIndirectResult, rawValue);
                    
                }
                
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV6CachesV8rawValueAGSi_tcfC")]
                private static extern void PInvoke_init_7C85736B( SwiftIndirectResult swiftIndirectResult,  System.IntPtr rawValue);
                
                
            }
            
            
            public unsafe Swift.SwiftOptional<Swift.Nuke.ImageContainer> cachedImage( Swift.Nuke.ImageRequest _for,  Swift.Nuke.ImagePipeline.Cache.Caches caches)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_cachedImage_18D1CE1B(_for.Payload, caches.Payload, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Nuke.ImageContainer>>(new IntPtr(&result));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV06cachedB03for6cachesAA0B9ContainerVSgAA0B7RequestV_AE6CachesVtF")]
            private static extern IntPtr PInvoke_cachedImage_18D1CE1B( SafeHandle _for,  SafeHandle caches,  SwiftSelf self);
            
            
            public unsafe void storeCachedImage( Swift.Nuke.ImageContainer arg0,  Swift.Nuke.ImageRequest _for,  Swift.Nuke.ImagePipeline.Cache.Caches caches)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_storeCachedImage_2AEE0DB1(arg0.Payload, _for.Payload, caches.Payload, self);
                    
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
            private static extern void PInvoke_storeCachedImage_2AEE0DB1( SafeHandle arg0,  SafeHandle _for,  SafeHandle caches,  SwiftSelf self);
            
            
            public unsafe void removeCachedImage( Swift.Nuke.ImageRequest _for,  Swift.Nuke.ImagePipeline.Cache.Caches caches)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_removeCachedImage_6FAEE941(_for.Payload, caches.Payload, self);
                    
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
            private static extern void PInvoke_removeCachedImage_6FAEE941( SafeHandle _for,  SafeHandle caches,  SwiftSelf self);
            
            
            public unsafe System.Boolean containsCachedImage( Swift.Nuke.ImageRequest _for,  Swift.Nuke.ImagePipeline.Cache.Caches caches)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_containsCachedImage_6D39BEE5(_for.Payload, caches.Payload, self);
                    
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
            private static extern System.Boolean PInvoke_containsCachedImage_6D39BEE5( SafeHandle _for,  SafeHandle caches,  SwiftSelf self);
            
            
            public unsafe Swift.SwiftOptional<Swift.Data> cachedData( Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_cachedData_2AD3D5D7(_for.Payload, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Data>>(new IntPtr(&result));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV10cachedData3for10Foundation0F0VSgAA0B7RequestV_tF")]
            private static extern IntPtr PInvoke_cachedData_2AD3D5D7( SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe void storeCachedData( Swift.Data arg0,  Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_storeCachedData_6B1320BA(arg0, _for.Payload, self);
                    
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
            private static extern void PInvoke_storeCachedData_6B1320BA( Swift.Data arg0,  SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe System.Boolean containsData( Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_containsData_1786CDA3(_for.Payload, self);
                    
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
            private static extern System.Boolean PInvoke_containsData_1786CDA3( SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe void removeCachedData( Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_removeCachedData_06380B33(_for.Payload, self);
                    
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
            private static extern void PInvoke_removeCachedData_06380B33( SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe Swift.Nuke.ImageCacheKey makeImageCacheKey( Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageCacheKey>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_makeImageCacheKey_67C7C425(swiftIndirectResult, _for.Payload, self);
                    
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
            private static extern void PInvoke_makeImageCacheKey_67C7C425( SwiftIndirectResult swiftIndirectResult,  SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe Swift.SwiftString makeDataCacheKey( Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_makeDataCacheKey_5C818FA3(_for.Payload, self);
                    
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
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV08makeDataD3Key3forSSAA0B7RequestV_tF")]
            private static extern Swift.SwiftString.Buffer PInvoke_makeDataCacheKey_5C818FA3( SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe void removeAll( Swift.Nuke.ImagePipeline.Cache.Caches caches)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_removeAll_02161A15(caches.Payload, self);
                    
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
            private static extern void PInvoke_removeAll_02161A15( SafeHandle caches,  SwiftSelf self);
            
            
        }
        
        
        public unsafe class Configuration : ISwiftObject
        {
            private unsafe Swift.SwiftOptional<Swift.Nuke.ISwiftDataCaching> dataCache_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_dataCache_Get_6935246D(self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Nuke.ISwiftDataCaching>>(new IntPtr(&result));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV9dataCacheAA11DataCaching_pSgvg")]
            private static extern IntPtr PInvoke_dataCache_Get_6935246D( SwiftSelf self);
            
            private unsafe void dataCache_Set( Swift.SwiftOptional<Swift.Nuke.ISwiftDataCaching> value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                    IntPtr valueBuffer = valueDisposable.Buffer;
                    
                    PInvoke_dataCache_Set_50CDEA9D(valueBuffer, self);
                    
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
            private static extern void PInvoke_dataCache_Set_50CDEA9D( IntPtr valueBuffer,  SwiftSelf self);
            
            public Swift.SwiftOptional<Swift.Nuke.ISwiftDataCaching> dataCache
            {
                get => dataCache_Get();
                set => dataCache_Set(value);
            }
            
            private unsafe Swift.SwiftOptional<Swift.Nuke.ISwiftImageCaching> imageCache_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_imageCache_Get_4BFCB522(self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Nuke.ISwiftImageCaching>>(new IntPtr(&result));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV10imageCacheAA0B7Caching_pSgvg")]
            private static extern IntPtr PInvoke_imageCache_Get_4BFCB522( SwiftSelf self);
            
            private unsafe void imageCache_Set( Swift.SwiftOptional<Swift.Nuke.ISwiftImageCaching> value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                    IntPtr valueBuffer = valueDisposable.Buffer;
                    
                    PInvoke_imageCache_Set_1BBF7F82(valueBuffer, self);
                    
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
            private static extern void PInvoke_imageCache_Set_1BBF7F82( IntPtr valueBuffer,  SwiftSelf self);
            
            public Swift.SwiftOptional<Swift.Nuke.ISwiftImageCaching> imageCache
            {
                get => imageCache_Get();
                set => imageCache_Set(value);
            }
            
            private unsafe System.Boolean isDecompressionEnabled_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isDecompressionEnabled_Get_6E4718CF(self);
                    
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
            private static extern System.Boolean PInvoke_isDecompressionEnabled_Get_6E4718CF( SwiftSelf self);
            
            private unsafe void isDecompressionEnabled_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isDecompressionEnabled_Set_7691F94B(value, self);
                    
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
            private static extern void PInvoke_isDecompressionEnabled_Set_7691F94B( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean isDecompressionEnabled
            {
                get => isDecompressionEnabled_Get();
                set => isDecompressionEnabled_Set(value);
            }
            
            private unsafe System.Boolean isUsingPrepareForDisplay_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isUsingPrepareForDisplay_Get_6E11AD31(self);
                    
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
            private static extern System.Boolean PInvoke_isUsingPrepareForDisplay_Get_6E11AD31( SwiftSelf self);
            
            private unsafe void isUsingPrepareForDisplay_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isUsingPrepareForDisplay_Set_3847B87D(value, self);
                    
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
            private static extern void PInvoke_isUsingPrepareForDisplay_Set_3847B87D( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean isUsingPrepareForDisplay
            {
                get => isUsingPrepareForDisplay_Get();
                set => isUsingPrepareForDisplay_Set(value);
            }
            
            private unsafe Swift.Nuke.ImagePipeline.DataCachePolicy dataCachePolicy_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.DataCachePolicy>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_dataCachePolicy_Get_3FDEC3BF(swiftIndirectResult, self);
                    
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
            private static extern void PInvoke_dataCachePolicy_Get_3FDEC3BF( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void dataCachePolicy_Set( Swift.Nuke.ImagePipeline.DataCachePolicy value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_dataCachePolicy_Set_21E60B24(value.Payload, self);
                    
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
            private static extern void PInvoke_dataCachePolicy_Set_21E60B24( SafeHandle value,  SwiftSelf self);
            
            public Swift.Nuke.ImagePipeline.DataCachePolicy dataCachePolicy
            {
                get => dataCachePolicy_Get();
                set => dataCachePolicy_Set(value);
            }
            
            private unsafe System.Boolean isTaskCoalescingEnabled_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isTaskCoalescingEnabled_Get_6935D249(self);
                    
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
            private static extern System.Boolean PInvoke_isTaskCoalescingEnabled_Get_6935D249( SwiftSelf self);
            
            private unsafe void isTaskCoalescingEnabled_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isTaskCoalescingEnabled_Set_0BBC1C54(value, self);
                    
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
            private static extern void PInvoke_isTaskCoalescingEnabled_Set_0BBC1C54( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean isTaskCoalescingEnabled
            {
                get => isTaskCoalescingEnabled_Get();
                set => isTaskCoalescingEnabled_Set(value);
            }
            
            private unsafe System.Boolean isRateLimiterEnabled_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isRateLimiterEnabled_Get_46A27E10(self);
                    
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
            private static extern System.Boolean PInvoke_isRateLimiterEnabled_Get_46A27E10( SwiftSelf self);
            
            private unsafe void isRateLimiterEnabled_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isRateLimiterEnabled_Set_1F96B74E(value, self);
                    
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
            private static extern void PInvoke_isRateLimiterEnabled_Set_1F96B74E( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean isRateLimiterEnabled
            {
                get => isRateLimiterEnabled_Get();
                set => isRateLimiterEnabled_Set(value);
            }
            
            private unsafe System.Boolean isProgressiveDecodingEnabled_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isProgressiveDecodingEnabled_Get_62653857(self);
                    
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
            private static extern System.Boolean PInvoke_isProgressiveDecodingEnabled_Get_62653857( SwiftSelf self);
            
            private unsafe void isProgressiveDecodingEnabled_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isProgressiveDecodingEnabled_Set_029CA47B(value, self);
                    
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
            private static extern void PInvoke_isProgressiveDecodingEnabled_Set_029CA47B( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean isProgressiveDecodingEnabled
            {
                get => isProgressiveDecodingEnabled_Get();
                set => isProgressiveDecodingEnabled_Set(value);
            }
            
            private unsafe System.Boolean isStoringPreviewsInMemoryCache_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isStoringPreviewsInMemoryCache_Get_17BB1AFF(self);
                    
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
            private static extern System.Boolean PInvoke_isStoringPreviewsInMemoryCache_Get_17BB1AFF( SwiftSelf self);
            
            private unsafe void isStoringPreviewsInMemoryCache_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isStoringPreviewsInMemoryCache_Set_1326E922(value, self);
                    
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
            private static extern void PInvoke_isStoringPreviewsInMemoryCache_Set_1326E922( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean isStoringPreviewsInMemoryCache
            {
                get => isStoringPreviewsInMemoryCache_Get();
                set => isStoringPreviewsInMemoryCache_Set(value);
            }
            
            private unsafe System.Boolean isResumableDataEnabled_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isResumableDataEnabled_Get_412038B3(self);
                    
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
            private static extern System.Boolean PInvoke_isResumableDataEnabled_Get_412038B3( SwiftSelf self);
            
            private unsafe void isResumableDataEnabled_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isResumableDataEnabled_Set_0EF7D0A3(value, self);
                    
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
            private static extern void PInvoke_isResumableDataEnabled_Set_0EF7D0A3( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean isResumableDataEnabled
            {
                get => isResumableDataEnabled_Get();
                set => isResumableDataEnabled_Set(value);
            }
            
            private unsafe System.Boolean isLocalResourcesSupportEnabled_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isLocalResourcesSupportEnabled_Get_6A51BA5F(self);
                    
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
            private static extern System.Boolean PInvoke_isLocalResourcesSupportEnabled_Get_6A51BA5F( SwiftSelf self);
            
            private unsafe void isLocalResourcesSupportEnabled_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isLocalResourcesSupportEnabled_Set_0C8BB609(value, self);
                    
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
            private static extern void PInvoke_isLocalResourcesSupportEnabled_Set_0C8BB609( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean isLocalResourcesSupportEnabled
            {
                get => isLocalResourcesSupportEnabled_Get();
                set => isLocalResourcesSupportEnabled_Set(value);
            }
            
            private unsafe Swift.DispatchQueue callbackQueue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.DispatchQueue>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_callbackQueue_Get_564D1BA2(swiftIndirectResult, self);
                    
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
            private static extern void PInvoke_callbackQueue_Get_564D1BA2( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void callbackQueue_Set( Swift.DispatchQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_callbackQueue_Set_6D700708(value.Payload, self);
                    
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
            private static extern void PInvoke_callbackQueue_Set_6D700708( SafeHandle value,  SwiftSelf self);
            
            public Swift.DispatchQueue callbackQueue
            {
                get => callbackQueue_Get();
                set => callbackQueue_Set(value);
            }
            
            private static System.Boolean isSignpostLoggingEnabled_Get()
            {
                try
                {
                    
                    
                    var result = PInvoke_isSignpostLoggingEnabled_Get_68F478E3();
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV24isSignpostLoggingEnabledSbvgZ")]
            private static extern System.Boolean PInvoke_isSignpostLoggingEnabled_Get_68F478E3();
            
            private static void isSignpostLoggingEnabled_Set( System.Boolean value)
            {
                try
                {
                    
                    
                    PInvoke_isSignpostLoggingEnabled_Set_71D6151A(value);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV24isSignpostLoggingEnabledSbvsZ")]
            private static extern void PInvoke_isSignpostLoggingEnabled_Set_71D6151A( System.Boolean value);
            
            public static System.Boolean isSignpostLoggingEnabled
            {
                get => isSignpostLoggingEnabled_Get();
                set => isSignpostLoggingEnabled_Set(value);
            }
            
            private unsafe Swift.OperationQueue dataLoadingQueue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.OperationQueue>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_dataLoadingQueue_Get_408BA8D3(swiftIndirectResult, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.OperationQueue>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV16dataLoadingQueueSo011NSOperationG0Cvg")]
            private static extern void PInvoke_dataLoadingQueue_Get_408BA8D3( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void dataLoadingQueue_Set( Swift.OperationQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_dataLoadingQueue_Set_7EC40266(value.Payload, self);
                    
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
            private static extern void PInvoke_dataLoadingQueue_Set_7EC40266( SafeHandle value,  SwiftSelf self);
            
            public Swift.OperationQueue dataLoadingQueue
            {
                get => dataLoadingQueue_Get();
                set => dataLoadingQueue_Set(value);
            }
            
            private unsafe Swift.OperationQueue dataCachingQueue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.OperationQueue>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_dataCachingQueue_Get_047E4AF4(swiftIndirectResult, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.OperationQueue>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV16dataCachingQueueSo011NSOperationG0Cvg")]
            private static extern void PInvoke_dataCachingQueue_Get_047E4AF4( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void dataCachingQueue_Set( Swift.OperationQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_dataCachingQueue_Set_6A942BD5(value.Payload, self);
                    
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
            private static extern void PInvoke_dataCachingQueue_Set_6A942BD5( SafeHandle value,  SwiftSelf self);
            
            public Swift.OperationQueue dataCachingQueue
            {
                get => dataCachingQueue_Get();
                set => dataCachingQueue_Set(value);
            }
            
            private unsafe Swift.OperationQueue imageDecodingQueue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.OperationQueue>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_imageDecodingQueue_Get_15590DFB(swiftIndirectResult, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.OperationQueue>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV18imageDecodingQueueSo011NSOperationG0Cvg")]
            private static extern void PInvoke_imageDecodingQueue_Get_15590DFB( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void imageDecodingQueue_Set( Swift.OperationQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_imageDecodingQueue_Set_63A246E9(value.Payload, self);
                    
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
            private static extern void PInvoke_imageDecodingQueue_Set_63A246E9( SafeHandle value,  SwiftSelf self);
            
            public Swift.OperationQueue imageDecodingQueue
            {
                get => imageDecodingQueue_Get();
                set => imageDecodingQueue_Set(value);
            }
            
            private unsafe Swift.OperationQueue imageEncodingQueue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.OperationQueue>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_imageEncodingQueue_Get_14E335BD(swiftIndirectResult, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.OperationQueue>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV18imageEncodingQueueSo011NSOperationG0Cvg")]
            private static extern void PInvoke_imageEncodingQueue_Get_14E335BD( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void imageEncodingQueue_Set( Swift.OperationQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_imageEncodingQueue_Set_253FC518(value.Payload, self);
                    
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
            private static extern void PInvoke_imageEncodingQueue_Set_253FC518( SafeHandle value,  SwiftSelf self);
            
            public Swift.OperationQueue imageEncodingQueue
            {
                get => imageEncodingQueue_Get();
                set => imageEncodingQueue_Set(value);
            }
            
            private unsafe Swift.OperationQueue imageProcessingQueue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.OperationQueue>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_imageProcessingQueue_Get_2AB77D08(swiftIndirectResult, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.OperationQueue>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV20imageProcessingQueueSo011NSOperationG0Cvg")]
            private static extern void PInvoke_imageProcessingQueue_Get_2AB77D08( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void imageProcessingQueue_Set( Swift.OperationQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_imageProcessingQueue_Set_4593DDB1(value.Payload, self);
                    
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
            private static extern void PInvoke_imageProcessingQueue_Set_4593DDB1( SafeHandle value,  SwiftSelf self);
            
            public Swift.OperationQueue imageProcessingQueue
            {
                get => imageProcessingQueue_Get();
                set => imageProcessingQueue_Set(value);
            }
            
            private unsafe Swift.OperationQueue imageDecompressingQueue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.OperationQueue>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_imageDecompressingQueue_Get_74CB31DA(swiftIndirectResult, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.OperationQueue>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV23imageDecompressingQueueSo011NSOperationG0Cvg")]
            private static extern void PInvoke_imageDecompressingQueue_Get_74CB31DA( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void imageDecompressingQueue_Set( Swift.OperationQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_imageDecompressingQueue_Set_4B3065CC(value.Payload, self);
                    
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
            private static extern void PInvoke_imageDecompressingQueue_Set_4B3065CC( SafeHandle value,  SwiftSelf self);
            
            public Swift.OperationQueue imageDecompressingQueue
            {
                get => imageDecompressingQueue_Get();
                set => imageDecompressingQueue_Set(value);
            }
            
            private static unsafe Swift.Nuke.ImagePipeline.Configuration withURLCache_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.Configuration>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_withURLCache_Get_0116AABC(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.Configuration>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV12withURLCacheAEvgZ")]
            private static extern void PInvoke_withURLCache_Get_0116AABC( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImagePipeline.Configuration withURLCache
            {
                get => withURLCache_Get();
            }
            
            private static unsafe Swift.Nuke.ImagePipeline.Configuration withDataCache_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.Configuration>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_withDataCache_Get_4CE3334D(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.Configuration>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV13withDataCacheAEvgZ")]
            private static extern void PInvoke_withDataCache_Get_4CE3334D( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImagePipeline.Configuration withDataCache
            {
                get => withDataCache_Get();
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
            
            
            
            public static unsafe Swift.Nuke.ImagePipeline.Configuration withDataCacheMethod( Swift.SwiftString name,  Swift.SwiftOptional<System.IntPtr> sizeLimit)
            {
                using PayloadBuffer<Swift.SwiftString.Buffer> nameDisposable = name.PayloadBuffer;
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.Configuration>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    using PayloadBuffer<IntPtr> sizeLimitDisposable = sizeLimit.PayloadBuffer;
                    IntPtr sizeLimitBuffer = sizeLimitDisposable.Buffer;
                    
                    PInvoke_withDataCache_361383D6(swiftIndirectResult, nameDisposable.Buffer, sizeLimitBuffer);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.Configuration>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV13withDataCache4name9sizeLimitAESS_SiSgtFZ")]
            private static extern void PInvoke_withDataCache_361383D6( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer name,  IntPtr sizeLimitBuffer);
            
            
        }
        
        
        public unsafe class DataCachePolicy : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<DataCachePolicy>.GetTypeMetadata().Size;
            SwiftSafeHandle<DataCachePolicy> _payload = SwiftSafeHandle<DataCachePolicy>.Zero;
            public SwiftSafeHandle<DataCachePolicy> Payload => _payload;
            
            /// <summary>
            /// Gets the 'automatic' case of DataCachePolicy.
            /// </summary>
            public static DataCachePolicy Automatic => throw new NotImplementedException("Enum case constructors not yet implemented");
            
            /// <summary>
            /// Gets the 'storeOriginalData' case of DataCachePolicy.
            /// </summary>
            public static DataCachePolicy StoreOriginalData => throw new NotImplementedException("Enum case constructors not yet implemented");
            
            /// <summary>
            /// Gets the 'storeEncodedImages' case of DataCachePolicy.
            /// </summary>
            public static DataCachePolicy StoreEncodedImages => throw new NotImplementedException("Enum case constructors not yet implemented");
            
            /// <summary>
            /// Gets the 'storeAll' case of DataCachePolicy.
            /// </summary>
            public static DataCachePolicy StoreAll => throw new NotImplementedException("Enum case constructors not yet implemented");
            
            
            private unsafe System.IntPtr hashValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_3CD2498C(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC15DataCachePolicyO9hashValueSivg")]
            private static extern System.IntPtr PInvoke_hashValue_Get_3CD2498C( SwiftSelf self);
            
            public System.IntPtr hashValue
            {
                get => hashValue_Get();
            }
            
                            static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException("Enum type metadata not yet implemented");
                            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                                where TProtocol : class
                            {
                                throw new NotImplementedException("Enum protocol conformance not yet implemented");
                            }
                            int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                            {
                                throw new NotImplementedException("Enum marshalling not yet implemented");
                            }
                            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                            {
                                throw new NotImplementedException("Enum NewFromPayload not yet implemented");
                            }
                        
            
        }
        
        
        public unsafe Swift.Nuke.ImagePipeline init( Swift.Nuke.ImagePipeline.Configuration configuration,  Swift.SwiftOptional<Swift.Nuke.ISwiftImagePipelineDelegate> _delegate)
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                using PayloadBuffer<IntPtr> _delegateDisposable = _delegate.PayloadBuffer;
                IntPtr _delegateBuffer = _delegateDisposable.Buffer;
                
                PInvoke_init_6B09F422(swiftIndirectResult, configuration.Payload, _delegateBuffer);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13configuration8delegateA2C13ConfigurationV_AA0bC8Delegate_pSgtcfC")]
        private static extern void PInvoke_init_6B09F422( SwiftIndirectResult swiftIndirectResult,  SafeHandle configuration,  IntPtr _delegateBuffer);
        
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, SwiftSelf, void> s_init_arg1_Callback = &init_arg1_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void init_arg1_Callback(void* arg0, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Action<Swift.Nuke.ImagePipeline.Configuration>>(new IntPtr(context.Value));
            del(SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.Configuration>(new IntPtr(arg0)));
        }
        
        public unsafe Swift.Nuke.ImagePipeline init( Swift.SwiftOptional<Swift.Nuke.ISwiftImagePipelineDelegate> _delegate,  Action<Swift.Nuke.ImagePipeline.Configuration> arg1)
        {
            GCHandle arg1Handle = default;
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                using PayloadBuffer<IntPtr> _delegateDisposable = _delegate.PayloadBuffer;
                IntPtr _delegateBuffer = _delegateDisposable.Buffer;
                arg1Handle = GCHandle.Alloc(arg1);
                var arg1Closure = new SwiftClosureData((IntPtr)s_init_arg1_Callback, GCHandle.ToIntPtr(arg1Handle));
                
                PInvoke_init_1EF3CE76(swiftIndirectResult, _delegateBuffer, arg1Closure);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (arg1Handle.IsAllocated) arg1Handle.Free();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC8delegate_AcA0bC8Delegate_pSg_yAC13ConfigurationVzXEtcfC")]
        private static extern void PInvoke_init_1EF3CE76( SwiftIndirectResult swiftIndirectResult,  IntPtr _delegateBuffer,  SwiftClosureData arg1);
        
        
        public unsafe void invalidate()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_invalidate_1D4776E0(self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC10invalidateyyF")]
        private static extern void PInvoke_invalidate_1D4776E0( SwiftSelf self);
        
        
        public unsafe Swift.Nuke.ImageTask imageTask( Swift.URL with)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageTask>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_imageTask_6572828F(swiftIndirectResult, with.Payload, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageTask>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC9imageTask4withAA0bE0C10Foundation3URLV_tF")]
        private static extern void PInvoke_imageTask_6572828F( SwiftIndirectResult swiftIndirectResult,  SafeHandle with,  SwiftSelf self);
        
        
        public unsafe Swift.Nuke.ImageTask imageTask( Swift.Nuke.ImageRequest with)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageTask>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_imageTask_3556D945(swiftIndirectResult, with.Payload, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageTask>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC9imageTask4withAA0bE0CAA0B7RequestV_tF")]
        private static extern void PInvoke_imageTask_3556D945( SwiftIndirectResult swiftIndirectResult,  SafeHandle with,  SwiftSelf self);
        
        
                private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> s_imageCallback_35DA2955 = &imageOnComplete_35DA2955;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void imageOnComplete_35DA2955(IntPtr rawResult, IntPtr task)
        {
            GCHandle handle = GCHandle.FromIntPtr(task);
            try
            {
                var result = SwiftMarshal.MarshalFromSwift<Swift.NSImage>(new IntPtr(&rawResult));
                var metadata = SwiftObjectHelper<Swift.NSImage>.GetTypeMetadata();
                Span<byte> payloadSpan = stackalloc byte[(int)metadata.Size];
                IntPtr payload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(payloadSpan));
                SwiftMarshal.MarshalToSwift(result, ref payloadSpan);
                if (handle.Target is TaskCompletionSource<Swift.NSImage> tcs)
                {
                    tcs.TrySetResult(result);
                }
            }
            finally
            {
                handle.Free();
            }
        }
        public unsafe Task<Swift.NSImage> image( Swift.URL _for)
        {
            TaskCompletionSource<Swift.NSImage> task = new TaskCompletionSource<Swift.NSImage>();
            GCHandle handle = GCHandle.Alloc(task, GCHandleType.Normal);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_image_35DA2955(s_imageCallback_35DA2955, null, GCHandle.ToIntPtr(handle), _for.Payload, self, out var error);
                
                if (error.Value != null)
                {
                    throw new SwiftRuntimeException("Call to Swift method image failed.");
                }
                
                return task.Task;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5image3forSo7NSImageC10Foundation3URLV_tYaKF_async")]
        private static extern void PInvoke_image_35DA2955( void* s_imageCallback_35DA2955,  void* context,  IntPtr handle,  SafeHandle _for,  SwiftSelf self, out SwiftError error);
        
        
                private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> s_imageCallback_44C70742 = &imageOnComplete_44C70742;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void imageOnComplete_44C70742(IntPtr rawResult, IntPtr task)
        {
            GCHandle handle = GCHandle.FromIntPtr(task);
            try
            {
                var result = SwiftMarshal.MarshalFromSwift<Swift.NSImage>(new IntPtr(&rawResult));
                var metadata = SwiftObjectHelper<Swift.NSImage>.GetTypeMetadata();
                Span<byte> payloadSpan = stackalloc byte[(int)metadata.Size];
                IntPtr payload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(payloadSpan));
                SwiftMarshal.MarshalToSwift(result, ref payloadSpan);
                if (handle.Target is TaskCompletionSource<Swift.NSImage> tcs)
                {
                    tcs.TrySetResult(result);
                }
            }
            finally
            {
                handle.Free();
            }
        }
        public unsafe Task<Swift.NSImage> image( Swift.Nuke.ImageRequest _for)
        {
            TaskCompletionSource<Swift.NSImage> task = new TaskCompletionSource<Swift.NSImage>();
            GCHandle handle = GCHandle.Alloc(task, GCHandleType.Normal);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_image_44C70742(s_imageCallback_44C70742, null, GCHandle.ToIntPtr(handle), _for.Payload, self, out var error);
                
                if (error.Value != null)
                {
                    throw new SwiftRuntimeException("Call to Swift method image failed.");
                }
                
                return task.Task;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5image3forSo7NSImageCAA0B7RequestV_tYaKF_async")]
        private static extern void PInvoke_image_44C70742( void* s_imageCallback_44C70742,  void* context,  IntPtr handle,  SafeHandle _for,  SwiftSelf self, out SwiftError error);
        
        
        
        
        
        
        
        
        
        
        
        
    }
    
    
    public unsafe class DataLoader : ISwiftObject {
        SwiftSafeHandle<DataLoader> _payload = SwiftSafeHandle<DataLoader>.Zero;
        
        public SwiftSafeHandle<DataLoader> Payload => _payload;
        
        
                static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException();

                static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                    where TProtocol : class
                {
                    throw new NotImplementedException();
                }

                int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                {
                    throw new NotImplementedException();
                }
                static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                {
                    throw new NotImplementedException();
                }
            
        public unsafe class Error : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<Error>.GetTypeMetadata().Size;
            SwiftSafeHandle<Error> _payload = SwiftSafeHandle<Error>.Zero;
            public SwiftSafeHandle<Error> Payload => _payload;
            
            private unsafe Swift.SwiftString description_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_4C688D1A(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_4C688D1A( SwiftSelf self);
            
            public Swift.SwiftString description
            {
                get => description_Get();
            }
            
                            static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException("Enum type metadata not yet implemented");
                            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                                where TProtocol : class
                            {
                                throw new NotImplementedException("Enum protocol conformance not yet implemented");
                            }
                            int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                            {
                                throw new NotImplementedException("Enum marshalling not yet implemented");
                            }
                            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                            {
                                throw new NotImplementedException("Enum NewFromPayload not yet implemented");
                            }
                        
        }
        
        
        
        
        
    }
    
    
    public interface ISwiftImageEncoding
    {
        Swift.SwiftOptional<Swift.Data> encode(Swift.NSImage arg0);
        Swift.SwiftOptional<Swift.Data> encode(Swift.Nuke.ImageContainer arg0, Swift.Nuke.ImageEncodingContext context);
    }
    
    
    public unsafe class ImageEncodingContext : ISwiftObject
    {
        private unsafe Swift.Nuke.ImageRequest request_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_request_Get_31B3592C(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_request_Get_31B3592C( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Swift.Nuke.ImageRequest request
        {
            get => request_Get();
        }
        
        private unsafe Swift.NSImage image_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.NSImage>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_image_Get_6DF8ADED(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.NSImage>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageEncodingContextV5imageSo7NSImageCvg")]
        private static extern void PInvoke_image_Get_6DF8ADED( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Swift.NSImage image
        {
            get => image_Get();
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
        Swift.Nuke.ISwiftCancellable loadData(Swift.AnyType with, Swift.AnyType didReceiveData, Swift.AnyType completion);
    }
    
    
    public interface ISwiftCancellable
    {
        void cancel();
    }
    
    
    public unsafe class ImageContainer : ISwiftObject
    {
        private unsafe Swift.NSImage image_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.NSImage>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_image_Get_54310C82(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.NSImage>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV5imageSo7NSImageCvg")]
        private static extern void PInvoke_image_Get_54310C82( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void image_Set( Swift.NSImage value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_image_Set_008484C0(value.Payload, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV5imageSo7NSImageCvs")]
        private static extern void PInvoke_image_Set_008484C0( SafeHandle value,  SwiftSelf self);
        
        public Swift.NSImage image
        {
            get => image_Get();
            set => image_Set(value);
        }
        
        private unsafe Swift.SwiftOptional<Swift.Nuke.AssetType> type_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_type_Get_2B460083(self);
                
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
        private static extern IntPtr PInvoke_type_Get_2B460083( SwiftSelf self);
        
        private unsafe void type_Set( Swift.SwiftOptional<Swift.Nuke.AssetType> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_type_Set_14466B3A(valueBuffer, self);
                
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
        private static extern void PInvoke_type_Set_14466B3A( IntPtr valueBuffer,  SwiftSelf self);
        
        public Swift.SwiftOptional<Swift.Nuke.AssetType> type
        {
            get => type_Get();
            set => type_Set(value);
        }
        
        private unsafe System.Boolean isPreview_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_isPreview_Get_4499E88D(self);
                
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
        private static extern System.Boolean PInvoke_isPreview_Get_4499E88D( SwiftSelf self);
        
        private unsafe void isPreview_Set( System.Boolean value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_isPreview_Set_02E0765F(value, self);
                
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
        private static extern void PInvoke_isPreview_Set_02E0765F( System.Boolean value,  SwiftSelf self);
        
        public System.Boolean isPreview
        {
            get => isPreview_Get();
            set => isPreview_Set(value);
        }
        
        private unsafe Swift.SwiftOptional<Swift.Data> data_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_data_Get_7DB40792(self);
                
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
        private static extern IntPtr PInvoke_data_Get_7DB40792( SwiftSelf self);
        
        private unsafe void data_Set( Swift.SwiftOptional<Swift.Data> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_data_Set_700C3B6F(valueBuffer, self);
                
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
        private static extern void PInvoke_data_Set_700C3B6F( IntPtr valueBuffer,  SwiftSelf self);
        
        public Swift.SwiftOptional<Swift.Data> data
        {
            get => data_Get();
            set => data_Set(value);
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
            private unsafe Swift.SwiftString rawValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_rawValue_Get_58A3D5EE(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_rawValue_Get_58A3D5EE( SwiftSelf self);
            
            public Swift.SwiftString rawValue
            {
                get => rawValue_Get();
            }
            
            private static unsafe Swift.Nuke.ImageContainer.UserInfoKey scanNumberKey_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageContainer.UserInfoKey>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_scanNumberKey_Get_6E254A68(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageContainer.UserInfoKey>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV11UserInfoKeyV010scanNumberF0AEvgZ")]
            private static extern void PInvoke_scanNumberKey_Get_6E254A68( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageContainer.UserInfoKey scanNumberKey
            {
                get => scanNumberKey_Get();
            }
            
            private unsafe System.IntPtr hashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_3F394682(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_3F394682( SwiftSelf self);
            
            public System.IntPtr hashValue
            {
                get => hashValue_Get();
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
            
            
            public unsafe UserInfoKey( Swift.SwiftString arg0)
            {
                using PayloadBuffer<Swift.SwiftString.Buffer> arg0Disposable = arg0.PayloadBuffer;
                _payload = new SwiftSafeHandle<UserInfoKey>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_647A3303(swiftIndirectResult, arg0Disposable.Buffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV11UserInfoKeyVyAESScfC")]
            private static extern void PInvoke_init_647A3303( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer arg0);
            
            
            
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
    
    
    public unsafe class ImageProcessingOptions : ISwiftObject
    {
        static nuint _payloadSize = SwiftObjectHelper<ImageProcessingOptions>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageProcessingOptions> _payload = SwiftSafeHandle<ImageProcessingOptions>.Zero;
        public SwiftSafeHandle<ImageProcessingOptions> Payload => _payload;
        
                        static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException("Enum type metadata not yet implemented");
                        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                            where TProtocol : class
                        {
                            throw new NotImplementedException("Enum protocol conformance not yet implemented");
                        }
                        int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                        {
                            throw new NotImplementedException("Enum marshalling not yet implemented");
                        }
                        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                        {
                            throw new NotImplementedException("Enum NewFromPayload not yet implemented");
                        }
                    
        public unsafe class Unit : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<Unit>.GetTypeMetadata().Size;
            SwiftSafeHandle<Unit> _payload = SwiftSafeHandle<Unit>.Zero;
            public SwiftSafeHandle<Unit> Payload => _payload;
            
            /// <summary>
            /// Gets the 'points' case of Unit.
            /// </summary>
            public static Unit Points => throw new NotImplementedException("Enum case constructors not yet implemented");
            
            /// <summary>
            /// Gets the 'pixels' case of Unit.
            /// </summary>
            public static Unit Pixels => throw new NotImplementedException("Enum case constructors not yet implemented");
            
            
            private unsafe Swift.SwiftString description_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_5F30030C(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_5F30030C( SwiftSelf self);
            
            public Swift.SwiftString description
            {
                get => description_Get();
            }
            
            private unsafe System.IntPtr hashValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_35828B17(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO4UnitO9hashValueSivg")]
            private static extern System.IntPtr PInvoke_hashValue_Get_35828B17( SwiftSelf self);
            
            public System.IntPtr hashValue
            {
                get => hashValue_Get();
            }
            
                            static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException("Enum type metadata not yet implemented");
                            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                                where TProtocol : class
                            {
                                throw new NotImplementedException("Enum protocol conformance not yet implemented");
                            }
                            int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                            {
                                throw new NotImplementedException("Enum marshalling not yet implemented");
                            }
                            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                            {
                                throw new NotImplementedException("Enum NewFromPayload not yet implemented");
                            }
                        
            
        }
        
        
        public unsafe class Border : ISwiftObject, IEquatable<Border>
        {
            private unsafe System.Double width_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_width_Get_2552B3EA(self);
                    
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
            private static extern System.Double PInvoke_width_Get_2552B3EA( SwiftSelf self);
            
            public System.Double width
            {
                get => width_Get();
            }
            
            private unsafe Swift.NSColor color_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.NSColor>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_color_Get_18B205D1(swiftIndirectResult, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.NSColor>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO6BorderV5colorSo7NSColorCvg")]
            private static extern void PInvoke_color_Get_18B205D1( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            public Swift.NSColor color
            {
                get => color_Get();
            }
            
            private unsafe Swift.SwiftString description_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_3ADDBF16(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_3ADDBF16( SwiftSelf self);
            
            public Swift.SwiftString description
            {
                get => description_Get();
            }
            
            private unsafe System.IntPtr hashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_4A74FD6E(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_4A74FD6E( SwiftSelf self);
            
            public System.IntPtr hashValue
            {
                get => hashValue_Get();
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
            
            
            public unsafe Border( Swift.NSColor color,  System.Double width,  Swift.Nuke.ImageProcessingOptions.Unit unit)
            {
                _payload = new SwiftSafeHandle<Border>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_1E65BDD3(swiftIndirectResult, color.Payload, width, unit.Payload);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO6BorderV5color5width4unitAESo7NSColorC_12CoreGraphics7CGFloatVAC4UnitOtcfC")]
            private static extern void PInvoke_init_1E65BDD3( SwiftIndirectResult swiftIndirectResult,  SafeHandle color,  System.Double width,  SafeHandle unit);
            
            
            
        }
        
        
        public unsafe class ContentMode : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<ContentMode>.GetTypeMetadata().Size;
            SwiftSafeHandle<ContentMode> _payload = SwiftSafeHandle<ContentMode>.Zero;
            public SwiftSafeHandle<ContentMode> Payload => _payload;
            
            /// <summary>
            /// Gets the 'aspectFill' case of ContentMode.
            /// </summary>
            public static ContentMode AspectFill => throw new NotImplementedException("Enum case constructors not yet implemented");
            
            /// <summary>
            /// Gets the 'aspectFit' case of ContentMode.
            /// </summary>
            public static ContentMode AspectFit => throw new NotImplementedException("Enum case constructors not yet implemented");
            
            
            private unsafe Swift.SwiftString description_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_4C4FF2F6(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_4C4FF2F6( SwiftSelf self);
            
            public Swift.SwiftString description
            {
                get => description_Get();
            }
            
            private unsafe System.IntPtr hashValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_79C50F88(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO11ContentModeO9hashValueSivg")]
            private static extern System.IntPtr PInvoke_hashValue_Get_79C50F88( SwiftSelf self);
            
            public System.IntPtr hashValue
            {
                get => hashValue_Get();
            }
            
                            static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException("Enum type metadata not yet implemented");
                            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                                where TProtocol : class
                            {
                                throw new NotImplementedException("Enum protocol conformance not yet implemented");
                            }
                            int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                            {
                                throw new NotImplementedException("Enum marshalling not yet implemented");
                            }
                            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                            {
                                throw new NotImplementedException("Enum NewFromPayload not yet implemented");
                            }
                        
            
        }
        
        
    }
    
    
    public interface ISwiftImagePipelineDelegate
    {
        Swift.Nuke.ISwiftDataLoading dataLoader(Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline);
        Swift.SwiftOptional<Swift.Nuke.ISwiftImageDecoding> imageDecoder(Swift.Nuke.ImageDecodingContext _for, Swift.Nuke.ImagePipeline pipeline);
        Swift.Nuke.ISwiftImageEncoding imageEncoder(Swift.Nuke.ImageEncodingContext _for, Swift.Nuke.ImagePipeline pipeline);
        Swift.SwiftOptional<Swift.Nuke.ISwiftImageCaching> imageCache(Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline);
        Swift.SwiftOptional<Swift.Nuke.ISwiftDataCaching> dataCache(Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline);
        Swift.SwiftOptional<Swift.SwiftString> cacheKey(Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline);
        void willCache(Swift.Data data, Swift.SwiftOptional<Swift.Nuke.ImageContainer> image, Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline, Swift.AnyType completion);
        System.Boolean shouldDecompress(Swift.Nuke.ImageResponse response, Swift.Nuke.ImageRequest _for, Swift.Nuke.ImagePipeline pipeline);
        Swift.Nuke.ImageResponse decompress(Swift.Nuke.ImageResponse response, Swift.Nuke.ImageRequest request, Swift.Nuke.ImagePipeline pipeline);
        void imageTaskCreated(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImagePipeline pipeline);
        void imageTask(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImageTask.Event didReceiveEvent, Swift.Nuke.ImagePipeline pipeline);
        void imageTaskDidStart(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImagePipeline pipeline);
        void imageTask(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImageTask.Progress didUpdateProgress, Swift.Nuke.ImagePipeline pipeline);
        void imageTask(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImageResponse didReceivePreview, Swift.Nuke.ImagePipeline pipeline);
        void imageTaskDidCancel(Swift.Nuke.ImageTask arg0, Swift.Nuke.ImagePipeline pipeline);
        void imageTask(Swift.Nuke.ImageTask arg0, Swift.AnyType didCompleteWithResult, Swift.Nuke.ImagePipeline pipeline);
    }
    
    
    public interface ISwiftImageCaching
    {
        void removeAll();
    }
    
    
    public unsafe class ImageCacheKey : ISwiftObject, IEquatable<ImageCacheKey>
    {
        private unsafe System.IntPtr hashValue_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_hashValue_Get_153A8419(self);
                
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
        private static extern System.IntPtr PInvoke_hashValue_Get_153A8419( SwiftSelf self);
        
        public System.IntPtr hashValue
        {
            get => hashValue_Get();
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
        
        
        public unsafe ImageCacheKey( Swift.SwiftString key)
        {
            using PayloadBuffer<Swift.SwiftString.Buffer> keyDisposable = key.PayloadBuffer;
            _payload = new SwiftSafeHandle<ImageCacheKey>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            PInvoke_init_3A0AFBB1(swiftIndirectResult, keyDisposable.Buffer);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageCacheKeyV3keyACSS_tcfC")]
        private static extern void PInvoke_init_3A0AFBB1( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer key);
        
        
        public unsafe ImageCacheKey( Swift.Nuke.ImageRequest request)
        {
            _payload = new SwiftSafeHandle<ImageCacheKey>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            PInvoke_init_686CABD3(swiftIndirectResult, request.Payload);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageCacheKeyV7requestAcA0B7RequestV_tcfC")]
        private static extern void PInvoke_init_686CABD3( SwiftIndirectResult swiftIndirectResult,  SafeHandle request);
        
        
        
    }
    
    
    public unsafe class DataCache : ISwiftObject {
        SwiftSafeHandle<DataCache> _payload = SwiftSafeHandle<DataCache>.Zero;
        
        public SwiftSafeHandle<DataCache> Payload => _payload;
        
        
                static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException();

                static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                    where TProtocol : class
                {
                    throw new NotImplementedException();
                }

                int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                {
                    throw new NotImplementedException();
                }
                static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                {
                    throw new NotImplementedException();
                }
            
        
        
        public static unsafe Swift.SwiftOptional<Swift.SwiftString> filename( Swift.SwiftString _for)
        {
            using PayloadBuffer<Swift.SwiftString.Buffer> _forDisposable = _for.PayloadBuffer;
            try
            {
                
                
                var result = PInvoke_filename_225E0632(_forDisposable.Buffer);
                
                return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.SwiftString>>(new IntPtr(&result));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC8filename3forSSSgSS_tFZ")]
        private static extern IntPtr PInvoke_filename_225E0632( Swift.SwiftString.Buffer _for);
        
        
        public unsafe Swift.SwiftOptional<Swift.Data> cachedData( Swift.SwiftString _for)
        {
            using PayloadBuffer<Swift.SwiftString.Buffer> _forDisposable = _for.PayloadBuffer;
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_cachedData_7AEC3161(_forDisposable.Buffer, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Data>>(new IntPtr(&result));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC06cachedB03for10Foundation0B0VSgSS_tF")]
        private static extern IntPtr PInvoke_cachedData_7AEC3161( Swift.SwiftString.Buffer _for,  SwiftSelf self);
        
        
        public unsafe System.Boolean containsData( Swift.SwiftString _for)
        {
            using PayloadBuffer<Swift.SwiftString.Buffer> _forDisposable = _for.PayloadBuffer;
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_containsData_44F7220A(_forDisposable.Buffer, self);
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC08containsB03forSbSS_tF")]
        private static extern System.Boolean PInvoke_containsData_44F7220A( Swift.SwiftString.Buffer _for,  SwiftSelf self);
        
        
        public unsafe void storeData( Swift.Data arg0,  Swift.SwiftString _for)
        {
            using PayloadBuffer<Swift.SwiftString.Buffer> _forDisposable = _for.PayloadBuffer;
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_storeData_1641608D(arg0, _forDisposable.Buffer, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC05storeB0_3fory10Foundation0B0V_SStF")]
        private static extern void PInvoke_storeData_1641608D( Swift.Data arg0,  Swift.SwiftString.Buffer _for,  SwiftSelf self);
        
        
        public unsafe void removeData( Swift.SwiftString _for)
        {
            using PayloadBuffer<Swift.SwiftString.Buffer> _forDisposable = _for.PayloadBuffer;
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_removeData_3C82D8BA(_forDisposable.Buffer, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC06removeB03forySS_tF")]
        private static extern void PInvoke_removeData_3C82D8BA( Swift.SwiftString.Buffer _for,  SwiftSelf self);
        
        
        public unsafe void removeAll()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_removeAll_2C8D6932(self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC9removeAllyyF")]
        private static extern void PInvoke_removeAll_2C8D6932( SwiftSelf self);
        
        
        public unsafe Swift.SwiftOptional<Swift.URL> url( Swift.SwiftString _for)
        {
            using PayloadBuffer<Swift.SwiftString.Buffer> _forDisposable = _for.PayloadBuffer;
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_url_33E66B06(_forDisposable.Buffer, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.URL>>(new IntPtr(&result));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC3url3for10Foundation3URLVSgSS_tF")]
        private static extern IntPtr PInvoke_url_33E66B06( Swift.SwiftString.Buffer _for,  SwiftSelf self);
        
        
        public unsafe void flush()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_flush_785DD36E(self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC5flushyyF")]
        private static extern void PInvoke_flush_785DD36E( SwiftSelf self);
        
        
        public unsafe void flush( Swift.SwiftString _for)
        {
            using PayloadBuffer<Swift.SwiftString.Buffer> _forDisposable = _for.PayloadBuffer;
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_flush_574A8E85(_forDisposable.Buffer, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC5flush3forySS_tF")]
        private static extern void PInvoke_flush_574A8E85( Swift.SwiftString.Buffer _for,  SwiftSelf self);
        
        
        public unsafe void sweep()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_sweep_75857D41(self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC5sweepyyF")]
        private static extern void PInvoke_sweep_75857D41( SwiftSelf self);
        
        
    }
    
    
    public unsafe class ImageDecoderRegistry : ISwiftObject {
        SwiftSafeHandle<ImageDecoderRegistry> _payload = SwiftSafeHandle<ImageDecoderRegistry>.Zero;
        
        public SwiftSafeHandle<ImageDecoderRegistry> Payload => _payload;
        
        
                static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException();

                static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                    where TProtocol : class
                {
                    throw new NotImplementedException();
                }

                int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                {
                    throw new NotImplementedException();
                }
                static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                {
                    throw new NotImplementedException();
                }
            
        public unsafe Swift.Nuke.ImageDecoderRegistry init()
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageDecoderRegistry>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_init_3883F1E3(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageDecoderRegistry>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecoderRegistryCACycfC")]
        private static extern void PInvoke_init_3883F1E3( SwiftIndirectResult swiftIndirectResult);
        
        
        public unsafe Swift.SwiftOptional<Swift.Nuke.ISwiftImageDecoding> decoder( Swift.Nuke.ImageDecodingContext _for)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_decoder_0637EDC3(_for.Payload, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Nuke.ISwiftImageDecoding>>(new IntPtr(&result));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecoderRegistryC7decoder3forAA0B8Decoding_pSgAA0bG7ContextV_tF")]
        private static extern IntPtr PInvoke_decoder_0637EDC3( SafeHandle _for,  SwiftSelf self);
        
        
        
        public unsafe void clear()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_clear_2B2EF11C(self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecoderRegistryC5clearyyF")]
        private static extern void PInvoke_clear_2B2EF11C( SwiftSelf self);
        
        
    }
    
    
    public unsafe class ImageDecodingContext : ISwiftObject
    {
        private unsafe Swift.Nuke.ImageRequest request_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_request_Get_15CD1540(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_request_Get_15CD1540( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void request_Set( Swift.Nuke.ImageRequest value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_request_Set_2DEEC53E(value.Payload, self);
                
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
        private static extern void PInvoke_request_Set_2DEEC53E( SafeHandle value,  SwiftSelf self);
        
        public Swift.Nuke.ImageRequest request
        {
            get => request_Get();
            set => request_Set(value);
        }
        
        private unsafe Swift.Data data_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_data_Get_799A5471(self);
                
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
        private static extern Swift.Data PInvoke_data_Get_799A5471( SwiftSelf self);
        
        private unsafe void data_Set( Swift.Data value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_data_Set_1C1B7044(value, self);
                
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
        private static extern void PInvoke_data_Set_1C1B7044( Swift.Data value,  SwiftSelf self);
        
        public Swift.Data data
        {
            get => data_Get();
            set => data_Set(value);
        }
        
        private unsafe System.Boolean isCompleted_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_isCompleted_Get_03F69234(self);
                
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
        private static extern System.Boolean PInvoke_isCompleted_Get_03F69234( SwiftSelf self);
        
        private unsafe void isCompleted_Set( System.Boolean value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_isCompleted_Set_6ED21659(value, self);
                
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
        private static extern void PInvoke_isCompleted_Set_6ED21659( System.Boolean value,  SwiftSelf self);
        
        public System.Boolean isCompleted
        {
            get => isCompleted_Get();
            set => isCompleted_Set(value);
        }
        
        private unsafe Swift.SwiftOptional<Swift.Nuke.ImageResponse.CacheType> cacheType_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_cacheType_Get_3EA70C53(self);
                
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
        private static extern IntPtr PInvoke_cacheType_Get_3EA70C53( SwiftSelf self);
        
        private unsafe void cacheType_Set( Swift.SwiftOptional<Swift.Nuke.ImageResponse.CacheType> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_cacheType_Set_081A1980(valueBuffer, self);
                
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
        private static extern void PInvoke_cacheType_Set_081A1980( IntPtr valueBuffer,  SwiftSelf self);
        
        public Swift.SwiftOptional<Swift.Nuke.ImageResponse.CacheType> cacheType
        {
            get => cacheType_Get();
            set => cacheType_Set(value);
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
        
        
        
    }
    
    
    public unsafe class ImageProcessors : ISwiftObject
    {
        static nuint _payloadSize = SwiftObjectHelper<ImageProcessors>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageProcessors> _payload = SwiftSafeHandle<ImageProcessors>.Zero;
        public SwiftSafeHandle<ImageProcessors> Payload => _payload;
        
                        static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException("Enum type metadata not yet implemented");
                        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                            where TProtocol : class
                        {
                            throw new NotImplementedException("Enum protocol conformance not yet implemented");
                        }
                        int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                        {
                            throw new NotImplementedException("Enum marshalling not yet implemented");
                        }
                        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                        {
                            throw new NotImplementedException("Enum NewFromPayload not yet implemented");
                        }
                    
        public unsafe class Anonymous : ISwiftObject
        {
            private unsafe Swift.SwiftString identifier_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_identifier_Get_48ED2998(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_48ED2998( SwiftSelf self);
            
            public Swift.SwiftString identifier
            {
                get => identifier_Get();
            }
            
            private unsafe Swift.SwiftString description_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_05E58AFB(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_05E58AFB( SwiftSelf self);
            
            public Swift.SwiftString description
            {
                get => description_Get();
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
            
            
            
            public unsafe Swift.SwiftOptional<Swift.NSImage> process( Swift.NSImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_21F317B3(arg0.Payload, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.NSImage>>(new IntPtr(&result));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO9AnonymousV7processySo7NSImageCSgAHF")]
            private static extern IntPtr PInvoke_process_21F317B3( SafeHandle arg0,  SwiftSelf self);
            
            
        }
        
        
        public unsafe class RoundedCorners : ISwiftObject, IEquatable<RoundedCorners>
        {
            private unsafe Swift.SwiftString identifier_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_identifier_Get_61DEA783(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_61DEA783( SwiftSelf self);
            
            public Swift.SwiftString identifier
            {
                get => identifier_Get();
            }
            
            private unsafe Swift.SwiftString description_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_506375D4(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_506375D4( SwiftSelf self);
            
            public Swift.SwiftString description
            {
                get => description_Get();
            }
            
            private unsafe System.IntPtr hashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_71B4D20A(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_71B4D20A( SwiftSelf self);
            
            public System.IntPtr hashValue
            {
                get => hashValue_Get();
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
            
            
            public unsafe RoundedCorners( System.Double radius,  Swift.Nuke.ImageProcessingOptions.Unit unit,  Swift.SwiftOptional<Swift.Nuke.ImageProcessingOptions.Border> border)
            {
                using PayloadBuffer<IntPtr> borderDisposable = border.PayloadBuffer;
                IntPtr borderBuffer = borderDisposable.Buffer;
                _payload = new SwiftSafeHandle<RoundedCorners>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_2E8013F2(swiftIndirectResult, radius, unit.Payload, borderBuffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO14RoundedCornersV6radius4unit6borderAE12CoreGraphics7CGFloatV_AA0B17ProcessingOptionsO4UnitOAM6BorderVSgtcfC")]
            private static extern void PInvoke_init_2E8013F2( SwiftIndirectResult swiftIndirectResult,  System.Double radius,  SafeHandle unit,  IntPtr borderBuffer);
            
            
            public unsafe Swift.SwiftOptional<Swift.NSImage> process( Swift.NSImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_0AA88C80(arg0.Payload, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.NSImage>>(new IntPtr(&result));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO14RoundedCornersV7processySo7NSImageCSgAHF")]
            private static extern IntPtr PInvoke_process_0AA88C80( SafeHandle arg0,  SwiftSelf self);
            
            
            
        }
        
        
        public unsafe class Resize : ISwiftObject, IEquatable<Resize>
        {
            private unsafe Swift.SwiftString identifier_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_identifier_Get_00BB20B3(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_00BB20B3( SwiftSelf self);
            
            public Swift.SwiftString identifier
            {
                get => identifier_Get();
            }
            
            private unsafe Swift.SwiftString description_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_5D1C97C4(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_5D1C97C4( SwiftSelf self);
            
            public Swift.SwiftString description
            {
                get => description_Get();
            }
            
            private unsafe System.IntPtr hashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_6D83BCB3(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_6D83BCB3( SwiftSelf self);
            
            public System.IntPtr hashValue
            {
                get => hashValue_Get();
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
            
            
            
            public unsafe Resize( System.Double width,  Swift.Nuke.ImageProcessingOptions.Unit unit,  System.Boolean upscale)
            {
                _payload = new SwiftSafeHandle<Resize>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_69A01071(swiftIndirectResult, width, unit.Payload, upscale);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6ResizeV5width4unit7upscaleAE12CoreGraphics7CGFloatV_AA0B17ProcessingOptionsO4UnitOSbtcfC")]
            private static extern void PInvoke_init_69A01071( SwiftIndirectResult swiftIndirectResult,  System.Double width,  SafeHandle unit,  System.Boolean upscale);
            
            
            public unsafe Swift.SwiftOptional<Swift.NSImage> process( Swift.NSImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_5B4272C0(arg0.Payload, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.NSImage>>(new IntPtr(&result));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6ResizeV7processySo7NSImageCSgAHF")]
            private static extern IntPtr PInvoke_process_5B4272C0( SafeHandle arg0,  SwiftSelf self);
            
            
            
        }
        
        
        public unsafe class GaussianBlur : ISwiftObject, IEquatable<GaussianBlur>
        {
            private unsafe Swift.SwiftString identifier_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_identifier_Get_5879E7A2(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_5879E7A2( SwiftSelf self);
            
            public Swift.SwiftString identifier
            {
                get => identifier_Get();
            }
            
            private unsafe Swift.SwiftString description_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_528BF3A3(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_528BF3A3( SwiftSelf self);
            
            public Swift.SwiftString description
            {
                get => description_Get();
            }
            
            private unsafe System.IntPtr hashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_2C7CA273(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_2C7CA273( SwiftSelf self);
            
            public System.IntPtr hashValue
            {
                get => hashValue_Get();
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
                
                PInvoke_init_48935CE0(swiftIndirectResult, radius);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO12GaussianBlurV6radiusAESi_tcfC")]
            private static extern void PInvoke_init_48935CE0( SwiftIndirectResult swiftIndirectResult,  System.IntPtr radius);
            
            
            public unsafe Swift.SwiftOptional<Swift.NSImage> process( Swift.NSImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_09AE8295(arg0.Payload, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.NSImage>>(new IntPtr(&result));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO12GaussianBlurV7processySo7NSImageCSgAHF")]
            private static extern IntPtr PInvoke_process_09AE8295( SafeHandle arg0,  SwiftSelf self);
            
            
            public unsafe Swift.Nuke.ImageContainer process( Swift.Nuke.ImageContainer arg0,  Swift.Nuke.ImageProcessingContext context)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageContainer>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_process_0F2DCE27(swiftIndirectResult, arg0.Payload, context.Payload, self, out var error);
                    
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
            private static extern void PInvoke_process_0F2DCE27( SwiftIndirectResult swiftIndirectResult,  SafeHandle arg0,  SafeHandle context,  SwiftSelf self, out SwiftError error);
            
            
            
        }
        
        
        public unsafe class Composition : ISwiftObject, IEquatable<Composition>
        {
            private unsafe Swift.SwiftString identifier_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_identifier_Get_59E950C0(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_59E950C0( SwiftSelf self);
            
            public Swift.SwiftString identifier
            {
                get => identifier_Get();
            }
            
            private unsafe Swift.SwiftString description_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_5D9BAE9C(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_5D9BAE9C( SwiftSelf self);
            
            public Swift.SwiftString description
            {
                get => description_Get();
            }
            
            private unsafe System.IntPtr hashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_4E193BBF(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_4E193BBF( SwiftSelf self);
            
            public System.IntPtr hashValue
            {
                get => hashValue_Get();
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
            
            
            public unsafe Composition( Swift.SwiftArray<Swift.Nuke.ISwiftImageProcessing> arg0)
            {
                using PayloadBuffer<IntPtr> arg0Disposable = arg0.PayloadBuffer;
                IntPtr arg0Buffer = arg0Disposable.Buffer;
                _payload = new SwiftSafeHandle<Composition>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_51631AEB(swiftIndirectResult, arg0Buffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO11CompositionVyAESayAA0B10Processing_pGcfC")]
            private static extern void PInvoke_init_51631AEB( SwiftIndirectResult swiftIndirectResult,  IntPtr arg0Buffer);
            
            
            public unsafe Swift.SwiftOptional<Swift.NSImage> process( Swift.NSImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_32329D17(arg0.Payload, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.NSImage>>(new IntPtr(&result));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO11CompositionV7processySo7NSImageCSgAHF")]
            private static extern IntPtr PInvoke_process_32329D17( SafeHandle arg0,  SwiftSelf self);
            
            
            public unsafe Swift.Nuke.ImageContainer process( Swift.Nuke.ImageContainer arg0,  Swift.Nuke.ImageProcessingContext context)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageContainer>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_process_7B9C3C1C(swiftIndirectResult, arg0.Payload, context.Payload, self, out var error);
                    
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
            private static extern void PInvoke_process_7B9C3C1C( SwiftIndirectResult swiftIndirectResult,  SafeHandle arg0,  SafeHandle context,  SwiftSelf self, out SwiftError error);
            
            
            
        }
        
        
        public unsafe class Circle : ISwiftObject, IEquatable<Circle>
        {
            private unsafe Swift.SwiftString identifier_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_identifier_Get_7E49AFE8(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_7E49AFE8( SwiftSelf self);
            
            public Swift.SwiftString identifier
            {
                get => identifier_Get();
            }
            
            private unsafe Swift.SwiftString description_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_763605E2(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_763605E2( SwiftSelf self);
            
            public Swift.SwiftString description
            {
                get => description_Get();
            }
            
            private unsafe System.IntPtr hashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_16815CF6(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_16815CF6( SwiftSelf self);
            
            public System.IntPtr hashValue
            {
                get => hashValue_Get();
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
            
            
            public unsafe Circle( Swift.SwiftOptional<Swift.Nuke.ImageProcessingOptions.Border> border)
            {
                using PayloadBuffer<IntPtr> borderDisposable = border.PayloadBuffer;
                IntPtr borderBuffer = borderDisposable.Buffer;
                _payload = new SwiftSafeHandle<Circle>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_5F01F260(swiftIndirectResult, borderBuffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6CircleV6borderAeA0B17ProcessingOptionsO6BorderVSg_tcfC")]
            private static extern void PInvoke_init_5F01F260( SwiftIndirectResult swiftIndirectResult,  IntPtr borderBuffer);
            
            
            public unsafe Swift.SwiftOptional<Swift.NSImage> process( Swift.NSImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_0C0A14E5(arg0.Payload, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.NSImage>>(new IntPtr(&result));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6CircleV7processySo7NSImageCSgAHF")]
            private static extern IntPtr PInvoke_process_0C0A14E5( SafeHandle arg0,  SwiftSelf self);
            
            
            
        }
        
        
        public unsafe class CoreImageFilter : ISwiftObject
        {
            private unsafe Swift.SwiftString identifier_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_identifier_Get_40A0E5E4(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_40A0E5E4( SwiftSelf self);
            
            public Swift.SwiftString identifier
            {
                get => identifier_Get();
            }
            
            private static unsafe Swift.CIContext context_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.CIContext>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_context_Get_42D6FD0D(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.CIContext>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV7contextSo9CIContextCvgZ")]
            private static extern void PInvoke_context_Get_42D6FD0D( SwiftIndirectResult swiftIndirectResult);
            
            private static void context_Set( Swift.CIContext value)
            {
                try
                {
                    
                    
                    PInvoke_context_Set_782E3F9B(value.Payload);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV7contextSo9CIContextCvsZ")]
            private static extern void PInvoke_context_Set_782E3F9B( SafeHandle value);
            
            public static Swift.CIContext context
            {
                get => context_Get();
                set => context_Set(value);
            }
            
            private unsafe Swift.SwiftString description_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_33CCDE68(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_33CCDE68( SwiftSelf self);
            
            public Swift.SwiftString description
            {
                get => description_Get();
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
                
                private unsafe Swift.SwiftString description_Get()
                {
                    try
                    {
                        var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                        
                        
                        
                        var result = PInvoke_description_Get_1DF9A062(self);
                        
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
                private static extern Swift.SwiftString.Buffer PInvoke_description_Get_1DF9A062( SwiftSelf self);
                
                public Swift.SwiftString description
                {
                    get => description_Get();
                }
                
                                static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException("Enum type metadata not yet implemented");
                                static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                                    where TProtocol : class
                                {
                                    throw new NotImplementedException("Enum protocol conformance not yet implemented");
                                }
                                int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                                {
                                    throw new NotImplementedException("Enum marshalling not yet implemented");
                                }
                                static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                                {
                                    throw new NotImplementedException("Enum NewFromPayload not yet implemented");
                                }
                            
            }
            
            
            
            public unsafe CoreImageFilter( Swift.SwiftString name)
            {
                using PayloadBuffer<Swift.SwiftString.Buffer> nameDisposable = name.PayloadBuffer;
                _payload = new SwiftSafeHandle<CoreImageFilter>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_2219CB48(swiftIndirectResult, nameDisposable.Buffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV4nameAESS_tcfC")]
            private static extern void PInvoke_init_2219CB48( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer name);
            
            
            
            public unsafe Swift.SwiftOptional<Swift.NSImage> process( Swift.NSImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_1D2ADCC1(arg0.Payload, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.NSImage>>(new IntPtr(&result));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV7processySo7NSImageCSgAHF")]
            private static extern IntPtr PInvoke_process_1D2ADCC1( SafeHandle arg0,  SwiftSelf self);
            
            
            public unsafe Swift.Nuke.ImageContainer process( Swift.Nuke.ImageContainer arg0,  Swift.Nuke.ImageProcessingContext context)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageContainer>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_process_1C7C73D6(swiftIndirectResult, arg0.Payload, context.Payload, self, out var error);
                    
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
            private static extern void PInvoke_process_1C7C73D6( SwiftIndirectResult swiftIndirectResult,  SafeHandle arg0,  SafeHandle context,  SwiftSelf self, out SwiftError error);
            
            
            
        }
        
        
    }
    
    
    public unsafe class ImageRequest : ISwiftObject
    {
        private unsafe Swift.Nuke.ImageRequest.Priority priority_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Priority>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_priority_Get_1AF0F77D(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_priority_Get_1AF0F77D( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void priority_Set( Swift.Nuke.ImageRequest.Priority value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_priority_Set_2B988352(value.Payload, self);
                
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
        private static extern void PInvoke_priority_Set_2B988352( SafeHandle value,  SwiftSelf self);
        
        public Swift.Nuke.ImageRequest.Priority priority
        {
            get => priority_Get();
            set => priority_Set(value);
        }
        
        private unsafe Swift.SwiftArray<Swift.Nuke.ISwiftImageProcessing> processors_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_processors_Get_6CF67069(self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.SwiftArray<Swift.Nuke.ISwiftImageProcessing>>(new IntPtr(&result));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV10processorsSayAA0B10Processing_pGvg")]
        private static extern IntPtr PInvoke_processors_Get_6CF67069( SwiftSelf self);
        
        private unsafe void processors_Set( Swift.SwiftArray<Swift.Nuke.ISwiftImageProcessing> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_processors_Set_2ACE26B4(valueBuffer, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV10processorsSayAA0B10Processing_pGvs")]
        private static extern void PInvoke_processors_Set_2ACE26B4( IntPtr valueBuffer,  SwiftSelf self);
        
        public Swift.SwiftArray<Swift.Nuke.ISwiftImageProcessing> processors
        {
            get => processors_Get();
            set => processors_Set(value);
        }
        
        private unsafe Swift.Nuke.ImageRequest.Options options_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Options>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_options_Get_2C61AABB(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_options_Get_2C61AABB( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void options_Set( Swift.Nuke.ImageRequest.Options value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_options_Set_76BE2867(value.Payload, self);
                
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
        private static extern void PInvoke_options_Set_76BE2867( SafeHandle value,  SwiftSelf self);
        
        public Swift.Nuke.ImageRequest.Options options
        {
            get => options_Get();
            set => options_Set(value);
        }
        
        private unsafe Swift.SwiftOptional<Swift.URL> url_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_url_Get_1A45BA9D(self);
                
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
        private static extern IntPtr PInvoke_url_Get_1A45BA9D( SwiftSelf self);
        
        public Swift.SwiftOptional<Swift.URL> url
        {
            get => url_Get();
        }
        
        private unsafe Swift.SwiftOptional<Swift.SwiftString> imageId_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_imageId_Get_4A4C610D(self);
                
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
        private static extern IntPtr PInvoke_imageId_Get_4A4C610D( SwiftSelf self);
        
        public Swift.SwiftOptional<Swift.SwiftString> imageId
        {
            get => imageId_Get();
        }
        
        private unsafe Swift.SwiftString description_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_description_Get_6F947CA1(self);
                
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
        private static extern Swift.SwiftString.Buffer PInvoke_description_Get_6F947CA1( SwiftSelf self);
        
        public Swift.SwiftString description
        {
            get => description_Get();
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
            /// Gets the 'veryLow' case of Priority.
            /// </summary>
            public static Priority VeryLow => throw new NotImplementedException("Enum case constructors not yet implemented");
            
            /// <summary>
            /// Gets the 'low' case of Priority.
            /// </summary>
            public static Priority Low => throw new NotImplementedException("Enum case constructors not yet implemented");
            
            /// <summary>
            /// Gets the 'normal' case of Priority.
            /// </summary>
            public static Priority Normal => throw new NotImplementedException("Enum case constructors not yet implemented");
            
            /// <summary>
            /// Gets the 'high' case of Priority.
            /// </summary>
            public static Priority High => throw new NotImplementedException("Enum case constructors not yet implemented");
            
            /// <summary>
            /// Gets the 'veryHigh' case of Priority.
            /// </summary>
            public static Priority VeryHigh => throw new NotImplementedException("Enum case constructors not yet implemented");
            
            
            private unsafe System.IntPtr rawValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_rawValue_Get_50BA299E(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV8PriorityO8rawValueSivg")]
            private static extern System.IntPtr PInvoke_rawValue_Get_50BA299E( SwiftSelf self);
            
            public System.IntPtr rawValue
            {
                get => rawValue_Get();
            }
            
                            static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException("Enum type metadata not yet implemented");
                            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                                where TProtocol : class
                            {
                                throw new NotImplementedException("Enum protocol conformance not yet implemented");
                            }
                            int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                            {
                                throw new NotImplementedException("Enum marshalling not yet implemented");
                            }
                            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                            {
                                throw new NotImplementedException("Enum NewFromPayload not yet implemented");
                            }
                        
        }
        
        
        public unsafe class Options : ISwiftObject, IEquatable<Options>
        {
            private unsafe System.UInt16 rawValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_rawValue_Get_42E4C95B(self);
                    
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
            private static extern System.UInt16 PInvoke_rawValue_Get_42E4C95B( SwiftSelf self);
            
            public System.UInt16 rawValue
            {
                get => rawValue_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.Options disableMemoryCacheReads_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Options>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_disableMemoryCacheReads_Get_58B83D2D(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV23disableMemoryCacheReadsAEvgZ")]
            private static extern void PInvoke_disableMemoryCacheReads_Get_58B83D2D( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.Options disableMemoryCacheReads
            {
                get => disableMemoryCacheReads_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.Options disableMemoryCacheWrites_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Options>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_disableMemoryCacheWrites_Get_490EC6A2(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV24disableMemoryCacheWritesAEvgZ")]
            private static extern void PInvoke_disableMemoryCacheWrites_Get_490EC6A2( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.Options disableMemoryCacheWrites
            {
                get => disableMemoryCacheWrites_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.Options disableMemoryCache_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Options>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_disableMemoryCache_Get_07002330(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV18disableMemoryCacheAEvgZ")]
            private static extern void PInvoke_disableMemoryCache_Get_07002330( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.Options disableMemoryCache
            {
                get => disableMemoryCache_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.Options disableDiskCacheReads_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Options>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_disableDiskCacheReads_Get_2E19F0C5(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV21disableDiskCacheReadsAEvgZ")]
            private static extern void PInvoke_disableDiskCacheReads_Get_2E19F0C5( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.Options disableDiskCacheReads
            {
                get => disableDiskCacheReads_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.Options disableDiskCacheWrites_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Options>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_disableDiskCacheWrites_Get_56DA1F4D(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV22disableDiskCacheWritesAEvgZ")]
            private static extern void PInvoke_disableDiskCacheWrites_Get_56DA1F4D( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.Options disableDiskCacheWrites
            {
                get => disableDiskCacheWrites_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.Options disableDiskCache_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Options>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_disableDiskCache_Get_3B67B4EB(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV16disableDiskCacheAEvgZ")]
            private static extern void PInvoke_disableDiskCache_Get_3B67B4EB( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.Options disableDiskCache
            {
                get => disableDiskCache_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.Options reloadIgnoringCachedData_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Options>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_reloadIgnoringCachedData_Get_0ED0FADC(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV24reloadIgnoringCachedDataAEvgZ")]
            private static extern void PInvoke_reloadIgnoringCachedData_Get_0ED0FADC( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.Options reloadIgnoringCachedData
            {
                get => reloadIgnoringCachedData_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.Options returnCacheDataDontLoad_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Options>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_returnCacheDataDontLoad_Get_0D182A9F(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV23returnCacheDataDontLoadAEvgZ")]
            private static extern void PInvoke_returnCacheDataDontLoad_Get_0D182A9F( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.Options returnCacheDataDontLoad
            {
                get => returnCacheDataDontLoad_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.Options skipDecompression_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Options>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_skipDecompression_Get_3A184B63(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV17skipDecompressionAEvgZ")]
            private static extern void PInvoke_skipDecompression_Get_3A184B63( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.Options skipDecompression
            {
                get => skipDecompression_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.Options skipDataLoadingQueue_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Options>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_skipDataLoadingQueue_Get_05B21751(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV20skipDataLoadingQueueAEvgZ")]
            private static extern void PInvoke_skipDataLoadingQueue_Get_05B21751( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.Options skipDataLoadingQueue
            {
                get => skipDataLoadingQueue_Get();
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
                
                PInvoke_init_5DFA8865(swiftIndirectResult, rawValue);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV8rawValueAEs6UInt16V_tcfC")]
            private static extern void PInvoke_init_5DFA8865( SwiftIndirectResult swiftIndirectResult,  System.UInt16 rawValue);
            
            
        }
        
        
        public unsafe class UserInfoKey : ISwiftObject, IEquatable<UserInfoKey>
        {
            private unsafe Swift.SwiftString rawValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_rawValue_Get_413C209D(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_rawValue_Get_413C209D( SwiftSelf self);
            
            public Swift.SwiftString rawValue
            {
                get => rawValue_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.UserInfoKey imageIdKey_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.UserInfoKey>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_imageIdKey_Get_775F3F05(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.UserInfoKey>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV11UserInfoKeyV07imageIdF0AEvgZ")]
            private static extern void PInvoke_imageIdKey_Get_775F3F05( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.UserInfoKey imageIdKey
            {
                get => imageIdKey_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.UserInfoKey scaleKey_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.UserInfoKey>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_scaleKey_Get_7C51AA2F(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.UserInfoKey>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV11UserInfoKeyV05scaleF0AEvgZ")]
            private static extern void PInvoke_scaleKey_Get_7C51AA2F( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.UserInfoKey scaleKey
            {
                get => scaleKey_Get();
            }
            
            private static unsafe Swift.Nuke.ImageRequest.UserInfoKey thumbnailKey_Get()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.UserInfoKey>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_thumbnailKey_Get_72C32C44(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.UserInfoKey>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV11UserInfoKeyV09thumbnailF0AEvgZ")]
            private static extern void PInvoke_thumbnailKey_Get_72C32C44( SwiftIndirectResult swiftIndirectResult);
            
            public static Swift.Nuke.ImageRequest.UserInfoKey thumbnailKey
            {
                get => thumbnailKey_Get();
            }
            
            private unsafe System.IntPtr hashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_579AFECC(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_579AFECC( SwiftSelf self);
            
            public System.IntPtr hashValue
            {
                get => hashValue_Get();
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
            
            
            public unsafe UserInfoKey( Swift.SwiftString arg0)
            {
                using PayloadBuffer<Swift.SwiftString.Buffer> arg0Disposable = arg0.PayloadBuffer;
                _payload = new SwiftSafeHandle<UserInfoKey>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_5C945C24(swiftIndirectResult, arg0Disposable.Buffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV11UserInfoKeyVyAESScfC")]
            private static extern void PInvoke_init_5C945C24( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer arg0);
            
            
            
        }
        
        
        public unsafe class ThumbnailOptions : ISwiftObject, IEquatable<ThumbnailOptions>
        {
            private unsafe System.Boolean createThumbnailFromImageIfAbsent_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_createThumbnailFromImageIfAbsent_Get_5A9B6DCA(self);
                    
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
            private static extern System.Boolean PInvoke_createThumbnailFromImageIfAbsent_Get_5A9B6DCA( SwiftSelf self);
            
            private unsafe void createThumbnailFromImageIfAbsent_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_createThumbnailFromImageIfAbsent_Set_6D34B69A(value, self);
                    
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
            private static extern void PInvoke_createThumbnailFromImageIfAbsent_Set_6D34B69A( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean createThumbnailFromImageIfAbsent
            {
                get => createThumbnailFromImageIfAbsent_Get();
                set => createThumbnailFromImageIfAbsent_Set(value);
            }
            
            private unsafe System.Boolean createThumbnailFromImageAlways_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_createThumbnailFromImageAlways_Get_079B7C8A(self);
                    
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
            private static extern System.Boolean PInvoke_createThumbnailFromImageAlways_Get_079B7C8A( SwiftSelf self);
            
            private unsafe void createThumbnailFromImageAlways_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_createThumbnailFromImageAlways_Set_57CD6D81(value, self);
                    
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
            private static extern void PInvoke_createThumbnailFromImageAlways_Set_57CD6D81( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean createThumbnailFromImageAlways
            {
                get => createThumbnailFromImageAlways_Get();
                set => createThumbnailFromImageAlways_Set(value);
            }
            
            private unsafe System.Boolean createThumbnailWithTransform_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_createThumbnailWithTransform_Get_6F6C79D2(self);
                    
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
            private static extern System.Boolean PInvoke_createThumbnailWithTransform_Get_6F6C79D2( SwiftSelf self);
            
            private unsafe void createThumbnailWithTransform_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_createThumbnailWithTransform_Set_2E54F03E(value, self);
                    
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
            private static extern void PInvoke_createThumbnailWithTransform_Set_2E54F03E( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean createThumbnailWithTransform
            {
                get => createThumbnailWithTransform_Get();
                set => createThumbnailWithTransform_Set(value);
            }
            
            private unsafe System.Boolean shouldCacheImmediately_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_shouldCacheImmediately_Get_07C481DD(self);
                    
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
            private static extern System.Boolean PInvoke_shouldCacheImmediately_Get_07C481DD( SwiftSelf self);
            
            private unsafe void shouldCacheImmediately_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_shouldCacheImmediately_Set_2ABF0007(value, self);
                    
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
            private static extern void PInvoke_shouldCacheImmediately_Set_2ABF0007( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean shouldCacheImmediately
            {
                get => shouldCacheImmediately_Get();
                set => shouldCacheImmediately_Set(value);
            }
            
            private unsafe System.IntPtr hashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_42BE2B47(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_42BE2B47( SwiftSelf self);
            
            public System.IntPtr hashValue
            {
                get => hashValue_Get();
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
                
                PInvoke_init_59A02B00(swiftIndirectResult, maxPixelSize);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV16ThumbnailOptionsV12maxPixelSizeAESf_tcfC")]
            private static extern void PInvoke_init_59A02B00( SwiftIndirectResult swiftIndirectResult,  System.Single maxPixelSize);
            
            
            
            public unsafe Swift.SwiftOptional<Swift.NSImage> makeThumbnail( Swift.Data with)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_makeThumbnail_3ACC7985(with, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.NSImage>>(new IntPtr(&result));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV16ThumbnailOptionsV04makeD04withSo7NSImageCSg10Foundation4DataV_tF")]
            private static extern IntPtr PInvoke_makeThumbnail_3ACC7985( Swift.Data with,  SwiftSelf self);
            
            
            
        }
        
        
        public unsafe ImageRequest( Swift.SwiftString stringLiteral)
        {
            using PayloadBuffer<Swift.SwiftString.Buffer> stringLiteralDisposable = stringLiteral.PayloadBuffer;
            _payload = new SwiftSafeHandle<ImageRequest>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            PInvoke_init_5EB4BB1B(swiftIndirectResult, stringLiteralDisposable.Buffer);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV13stringLiteralACSS_tcfC")]
        private static extern void PInvoke_init_5EB4BB1B( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer stringLiteral);
        
        
        
        
        
        
    }
    
    
    public unsafe class ImageDecoders : ISwiftObject
    {
        static nuint _payloadSize = SwiftObjectHelper<ImageDecoders>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageDecoders> _payload = SwiftSafeHandle<ImageDecoders>.Zero;
        public SwiftSafeHandle<ImageDecoders> Payload => _payload;
        
                        static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException("Enum type metadata not yet implemented");
                        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                            where TProtocol : class
                        {
                            throw new NotImplementedException("Enum protocol conformance not yet implemented");
                        }
                        int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                        {
                            throw new NotImplementedException("Enum marshalling not yet implemented");
                        }
                        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                        {
                            throw new NotImplementedException("Enum NewFromPayload not yet implemented");
                        }
                    
        public unsafe class Empty : ISwiftObject
        {
            private unsafe System.Boolean isProgressive_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isProgressive_Get_2CD7F002(self);
                    
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
            private static extern System.Boolean PInvoke_isProgressive_Get_2CD7F002( SwiftSelf self);
            
            public System.Boolean isProgressive
            {
                get => isProgressive_Get();
            }
            
            private unsafe System.Boolean isAsynchronous_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isAsynchronous_Get_5FADD5FF(self);
                    
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
            private static extern System.Boolean PInvoke_isAsynchronous_Get_5FADD5FF( SwiftSelf self);
            
            public System.Boolean isAsynchronous
            {
                get => isAsynchronous_Get();
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
            
            
            public unsafe Empty( Swift.SwiftOptional<Swift.Nuke.AssetType> assetType,  System.Boolean isProgressive)
            {
                using PayloadBuffer<IntPtr> assetTypeDisposable = assetType.PayloadBuffer;
                IntPtr assetTypeBuffer = assetTypeDisposable.Buffer;
                _payload = new SwiftSafeHandle<Empty>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_081923F4(swiftIndirectResult, assetTypeBuffer, isProgressive);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO5EmptyV9assetType13isProgressiveAeA05AssetF0VSg_SbtcfC")]
            private static extern void PInvoke_init_081923F4( SwiftIndirectResult swiftIndirectResult,  IntPtr assetTypeBuffer,  System.Boolean isProgressive);
            
            
            public unsafe Swift.Nuke.ImageContainer decode( Swift.Data arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageContainer>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_decode_71EC599B(swiftIndirectResult, arg0, self, out var error);
                    
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
            private static extern void PInvoke_decode_71EC599B( SwiftIndirectResult swiftIndirectResult,  Swift.Data arg0,  SwiftSelf self, out SwiftError error);
            
            
            public unsafe Swift.SwiftOptional<Swift.Nuke.ImageContainer> decodePartiallyDownloadedData( Swift.Data arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_decodePartiallyDownloadedData_36F269B3(arg0, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Nuke.ImageContainer>>(new IntPtr(&result));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO5EmptyV29decodePartiallyDownloadedDatayAA0B9ContainerVSg10Foundation0H0VF")]
            private static extern IntPtr PInvoke_decodePartiallyDownloadedData_36F269B3( Swift.Data arg0,  SwiftSelf self);
            
            
        }
        
        
        public unsafe class Default : ISwiftObject {
            SwiftSafeHandle<Default> _payload = SwiftSafeHandle<Default>.Zero;
            
            public SwiftSafeHandle<Default> Payload => _payload;
            
            
                static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException();

                static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                    where TProtocol : class
                {
                    throw new NotImplementedException();
                }

                int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                {
                    throw new NotImplementedException();
                }
                static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                {
                    throw new NotImplementedException();
                }
            
            public unsafe Swift.Nuke.ImageDecoders.Default init()
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageDecoders.Default>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_init_56D371AC(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageDecoders.Default>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO7DefaultCAEycfC")]
            private static extern void PInvoke_init_56D371AC( SwiftIndirectResult swiftIndirectResult);
            
            
            public unsafe Swift.SwiftOptional<Swift.Nuke.ImageDecoders.Default> init( Swift.Nuke.ImageDecodingContext context)
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.SwiftOptional<Swift.Nuke.ImageDecoders.Default>>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_init_458A3907(swiftIndirectResult, context.Payload);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Nuke.ImageDecoders.Default>>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO7DefaultC7contextAESgAA0B15DecodingContextV_tcfC")]
            private static extern void PInvoke_init_458A3907( SwiftIndirectResult swiftIndirectResult,  SafeHandle context);
            
            
            public unsafe Swift.Nuke.ImageContainer decode( Swift.Data arg0)
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageContainer>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_decode_7CB41E8B(swiftIndirectResult, arg0, self, out var error);
                    
                    if (error.Value != null)
                    {
                        throw new SwiftRuntimeException("Call to Swift method decode failed.");
                    }
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageContainer>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO7DefaultC6decodeyAA0B9ContainerV10Foundation4DataVKF")]
            private static extern void PInvoke_decode_7CB41E8B( SwiftIndirectResult swiftIndirectResult,  Swift.Data arg0,  SwiftSelf self, out SwiftError error);
            
            
            public unsafe Swift.SwiftOptional<Swift.Nuke.ImageContainer> decodePartiallyDownloadedData( Swift.Data arg0)
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_decodePartiallyDownloadedData_15730635(arg0, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Nuke.ImageContainer>>(new IntPtr(&result));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO7DefaultC29decodePartiallyDownloadedDatayAA0B9ContainerVSg10Foundation0H0VF")]
            private static extern IntPtr PInvoke_decodePartiallyDownloadedData_15730635( Swift.Data arg0,  SwiftSelf self);
            
            
        }
        
        
    }
    
    
    public unsafe class AssetType : ISwiftObject, IEquatable<AssetType>
    {
        private unsafe Swift.SwiftString rawValue_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_rawValue_Get_2EAA1682(self);
                
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
        private static extern Swift.SwiftString.Buffer PInvoke_rawValue_Get_2EAA1682( SwiftSelf self);
        
        public Swift.SwiftString rawValue
        {
            get => rawValue_Get();
        }
        
        private static unsafe Swift.Nuke.AssetType png_Get()
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.AssetType>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_png_Get_460334BF(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV3pngACvgZ")]
        private static extern void PInvoke_png_Get_460334BF( SwiftIndirectResult swiftIndirectResult);
        
        public static Swift.Nuke.AssetType png
        {
            get => png_Get();
        }
        
        private static unsafe Swift.Nuke.AssetType jpeg_Get()
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.AssetType>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_jpeg_Get_6FFDC839(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV4jpegACvgZ")]
        private static extern void PInvoke_jpeg_Get_6FFDC839( SwiftIndirectResult swiftIndirectResult);
        
        public static Swift.Nuke.AssetType jpeg
        {
            get => jpeg_Get();
        }
        
        private static unsafe Swift.Nuke.AssetType gif_Get()
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.AssetType>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_gif_Get_0EAEF72D(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV3gifACvgZ")]
        private static extern void PInvoke_gif_Get_0EAEF72D( SwiftIndirectResult swiftIndirectResult);
        
        public static Swift.Nuke.AssetType gif
        {
            get => gif_Get();
        }
        
        private static unsafe Swift.Nuke.AssetType heic_Get()
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.AssetType>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_heic_Get_3071684E(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV4heicACvgZ")]
        private static extern void PInvoke_heic_Get_3071684E( SwiftIndirectResult swiftIndirectResult);
        
        public static Swift.Nuke.AssetType heic
        {
            get => heic_Get();
        }
        
        private static unsafe Swift.Nuke.AssetType webp_Get()
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.AssetType>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_webp_Get_0D629847(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV4webpACvgZ")]
        private static extern void PInvoke_webp_Get_0D629847( SwiftIndirectResult swiftIndirectResult);
        
        public static Swift.Nuke.AssetType webp
        {
            get => webp_Get();
        }
        
        private static unsafe Swift.Nuke.AssetType mp4_Get()
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.AssetType>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_mp4_Get_03FE2E66(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV3mp4ACvgZ")]
        private static extern void PInvoke_mp4_Get_03FE2E66( SwiftIndirectResult swiftIndirectResult);
        
        public static Swift.Nuke.AssetType mp4
        {
            get => mp4_Get();
        }
        
        private static unsafe Swift.Nuke.AssetType m4v_Get()
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.AssetType>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_m4v_Get_18DAB684(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV3m4vACvgZ")]
        private static extern void PInvoke_m4v_Get_18DAB684( SwiftIndirectResult swiftIndirectResult);
        
        public static Swift.Nuke.AssetType m4v
        {
            get => m4v_Get();
        }
        
        private static unsafe Swift.Nuke.AssetType mov_Get()
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.AssetType>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_mov_Get_060D4A62(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV3movACvgZ")]
        private static extern void PInvoke_mov_Get_060D4A62( SwiftIndirectResult swiftIndirectResult);
        
        public static Swift.Nuke.AssetType mov
        {
            get => mov_Get();
        }
        
        private unsafe System.IntPtr hashValue_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_hashValue_Get_4743EFE9(self);
                
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
        private static extern System.IntPtr PInvoke_hashValue_Get_4743EFE9( SwiftSelf self);
        
        public System.IntPtr hashValue
        {
            get => hashValue_Get();
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
        
        
        public unsafe AssetType( Swift.SwiftString rawValue)
        {
            using PayloadBuffer<Swift.SwiftString.Buffer> rawValueDisposable = rawValue.PayloadBuffer;
            _payload = new SwiftSafeHandle<AssetType>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            PInvoke_init_68DA312C(swiftIndirectResult, rawValueDisposable.Buffer);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV8rawValueACSS_tcfC")]
        private static extern void PInvoke_init_68DA312C( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer rawValue);
        
        
        
        public unsafe AssetType( Swift.Data arg0)
        {
            _payload = new SwiftSafeHandle<AssetType>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            PInvoke_init_471D29F8(swiftIndirectResult, arg0);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeVyACSg10Foundation4DataVcfC")]
        private static extern void PInvoke_init_471D29F8( SwiftIndirectResult swiftIndirectResult,  Swift.Data arg0);
        
        
    }
    
    
    public unsafe class ImageTask : ISwiftObject {
        SwiftSafeHandle<ImageTask> _payload = SwiftSafeHandle<ImageTask>.Zero;
        
        public SwiftSafeHandle<ImageTask> Payload => _payload;
        
        
                static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException();

                static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                    where TProtocol : class
                {
                    throw new NotImplementedException();
                }

                int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                {
                    throw new NotImplementedException();
                }
                static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                {
                    throw new NotImplementedException();
                }
            
        public unsafe class Progress : ISwiftObject, IEquatable<Progress>
        {
            private unsafe System.Int64 completed_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_completed_Get_6F427935(self);
                    
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
            private static extern System.Int64 PInvoke_completed_Get_6F427935( SwiftSelf self);
            
            public System.Int64 completed
            {
                get => completed_Get();
            }
            
            private unsafe System.Int64 total_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_total_Get_466AFBF3(self);
                    
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
            private static extern System.Int64 PInvoke_total_Get_466AFBF3( SwiftSelf self);
            
            public System.Int64 total
            {
                get => total_Get();
            }
            
            private unsafe System.Single fraction_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_fraction_Get_10B37888(self);
                    
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
            private static extern System.Single PInvoke_fraction_Get_10B37888( SwiftSelf self);
            
            public System.Single fraction
            {
                get => fraction_Get();
            }
            
            private unsafe System.IntPtr hashValue_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_54BD8DFB(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_54BD8DFB( SwiftSelf self);
            
            public System.IntPtr hashValue
            {
                get => hashValue_Get();
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
                
                PInvoke_init_0FB9A5B3(swiftIndirectResult, completed, total);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC8ProgressV9completed5totalAEs5Int64V_AItcfC")]
            private static extern void PInvoke_init_0FB9A5B3( SwiftIndirectResult swiftIndirectResult,  System.Int64 completed,  System.Int64 total);
            
            
            
        }
        
        
        public unsafe class State : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<State>.GetTypeMetadata().Size;
            SwiftSafeHandle<State> _payload = SwiftSafeHandle<State>.Zero;
            public SwiftSafeHandle<State> Payload => _payload;
            
            /// <summary>
            /// Gets the 'running' case of State.
            /// </summary>
            public static State Running => throw new NotImplementedException("Enum case constructors not yet implemented");
            
            /// <summary>
            /// Gets the 'cancelled' case of State.
            /// </summary>
            public static State Cancelled => throw new NotImplementedException("Enum case constructors not yet implemented");
            
            /// <summary>
            /// Gets the 'completed' case of State.
            /// </summary>
            public static State Completed => throw new NotImplementedException("Enum case constructors not yet implemented");
            
            
            private unsafe System.IntPtr hashValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_634092B2(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5StateO9hashValueSivg")]
            private static extern System.IntPtr PInvoke_hashValue_Get_634092B2( SwiftSelf self);
            
            public System.IntPtr hashValue
            {
                get => hashValue_Get();
            }
            
                            static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException("Enum type metadata not yet implemented");
                            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                                where TProtocol : class
                            {
                                throw new NotImplementedException("Enum protocol conformance not yet implemented");
                            }
                            int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                            {
                                throw new NotImplementedException("Enum marshalling not yet implemented");
                            }
                            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                            {
                                throw new NotImplementedException("Enum NewFromPayload not yet implemented");
                            }
                        
            
        }
        
        
        public unsafe class Event : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<Event>.GetTypeMetadata().Size;
            SwiftSafeHandle<Event> _payload = SwiftSafeHandle<Event>.Zero;
            public SwiftSafeHandle<Event> Payload => _payload;
            
            /// <summary>
            /// Gets the 'cancelled' case of Event.
            /// </summary>
            public static Event Cancelled => throw new NotImplementedException("Enum case constructors not yet implemented");
            
            
                            static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException("Enum type metadata not yet implemented");
                            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                                where TProtocol : class
                            {
                                throw new NotImplementedException("Enum protocol conformance not yet implemented");
                            }
                            int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                            {
                                throw new NotImplementedException("Enum marshalling not yet implemented");
                            }
                            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                            {
                                throw new NotImplementedException("Enum NewFromPayload not yet implemented");
                            }
                        
        }
        
        
        public unsafe void cancel()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_cancel_18635A5B(self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC6cancelyyF")]
        private static extern void PInvoke_cancel_18635A5B( SwiftSelf self);
        
        
        
    }
    
    
    public interface ISwiftImageDecoding
    {
        System.Boolean isAsynchronous { get; }
        Swift.Nuke.ImageContainer decode(Swift.Data arg0);
        Swift.SwiftOptional<Swift.Nuke.ImageContainer> decodePartiallyDownloadedData(Swift.Data arg0);
    }
    
    
    public unsafe class ImageDecodingError : ISwiftObject
    {
        static nuint _payloadSize = SwiftObjectHelper<ImageDecodingError>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageDecodingError> _payload = SwiftSafeHandle<ImageDecodingError>.Zero;
        public SwiftSafeHandle<ImageDecodingError> Payload => _payload;
        
        /// <summary>
        /// Gets the 'unknown' case of ImageDecodingError.
        /// </summary>
        public static ImageDecodingError Unknown => throw new NotImplementedException("Enum case constructors not yet implemented");
        
        
        private unsafe Swift.SwiftString description_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_description_Get_7DE34714(self);
                
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
        private static extern Swift.SwiftString.Buffer PInvoke_description_Get_7DE34714( SwiftSelf self);
        
        public Swift.SwiftString description
        {
            get => description_Get();
        }
        
        private unsafe System.IntPtr hashValue_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_hashValue_Get_57DC7312(self);
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke18ImageDecodingErrorO9hashValueSivg")]
        private static extern System.IntPtr PInvoke_hashValue_Get_57DC7312( SwiftSelf self);
        
        public System.IntPtr hashValue
        {
            get => hashValue_Get();
        }
        
                        static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException("Enum type metadata not yet implemented");
                        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                            where TProtocol : class
                        {
                            throw new NotImplementedException("Enum protocol conformance not yet implemented");
                        }
                        int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                        {
                            throw new NotImplementedException("Enum marshalling not yet implemented");
                        }
                        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                        {
                            throw new NotImplementedException("Enum NewFromPayload not yet implemented");
                        }
                    
        
    }
    
    
    public unsafe class ImageEncoders : ISwiftObject
    {
        static nuint _payloadSize = SwiftObjectHelper<ImageEncoders>.GetTypeMetadata().Size;
        SwiftSafeHandle<ImageEncoders> _payload = SwiftSafeHandle<ImageEncoders>.Zero;
        public SwiftSafeHandle<ImageEncoders> Payload => _payload;
        
                        static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException("Enum type metadata not yet implemented");
                        static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                            where TProtocol : class
                        {
                            throw new NotImplementedException("Enum protocol conformance not yet implemented");
                        }
                        int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                        {
                            throw new NotImplementedException("Enum marshalling not yet implemented");
                        }
                        static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                        {
                            throw new NotImplementedException("Enum NewFromPayload not yet implemented");
                        }
                    
        public unsafe class ImageIO : ISwiftObject
        {
            private unsafe Swift.Nuke.AssetType type_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.AssetType>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_type_Get_56EAA311(swiftIndirectResult, self);
                    
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
            private static extern void PInvoke_type_Get_56EAA311( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            public Swift.Nuke.AssetType type
            {
                get => type_Get();
            }
            
            private unsafe System.Single compressionRatio_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_compressionRatio_Get_36210181(self);
                    
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
            private static extern System.Single PInvoke_compressionRatio_Get_36210181( SwiftSelf self);
            
            public System.Single compressionRatio
            {
                get => compressionRatio_Get();
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
                
                PInvoke_init_5D50D98C(swiftIndirectResult, type.Payload, compressionRatio);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageEncodersO0B2IOV4type16compressionRatioAeA9AssetTypeV_SftcfC")]
            private static extern void PInvoke_init_5D50D98C( SwiftIndirectResult swiftIndirectResult,  SafeHandle type,  System.Single compressionRatio);
            
            
            public static System.Boolean isSupported( Swift.Nuke.AssetType type)
            {
                try
                {
                    
                    
                    var result = PInvoke_isSupported_1D699E21(type.Payload);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageEncodersO0B2IOV11isSupported4typeSbAA9AssetTypeV_tFZ")]
            private static extern System.Boolean PInvoke_isSupported_1D699E21( SafeHandle type);
            
            
            public unsafe Swift.SwiftOptional<Swift.Data> encode( Swift.NSImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_encode_04F577EA(arg0.Payload, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Data>>(new IntPtr(&result));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageEncodersO0B2IOV6encodey10Foundation4DataVSgSo7NSImageCF")]
            private static extern IntPtr PInvoke_encode_04F577EA( SafeHandle arg0,  SwiftSelf self);
            
            
        }
        
        
        public unsafe class Default : ISwiftObject
        {
            private unsafe System.Single compressionQuality_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_compressionQuality_Get_567EB2E6(self);
                    
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
            private static extern System.Single PInvoke_compressionQuality_Get_567EB2E6( SwiftSelf self);
            
            private unsafe void compressionQuality_Set( System.Single value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_compressionQuality_Set_124C4CC7(value, self);
                    
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
            private static extern void PInvoke_compressionQuality_Set_124C4CC7( System.Single value,  SwiftSelf self);
            
            public System.Single compressionQuality
            {
                get => compressionQuality_Get();
                set => compressionQuality_Set(value);
            }
            
            private unsafe System.Boolean isHEIFPreferred_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isHEIFPreferred_Get_5F866EA9(self);
                    
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
            private static extern System.Boolean PInvoke_isHEIFPreferred_Get_5F866EA9( SwiftSelf self);
            
            private unsafe void isHEIFPreferred_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isHEIFPreferred_Set_4D164B8A(value, self);
                    
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
            private static extern void PInvoke_isHEIFPreferred_Set_4D164B8A( System.Boolean value,  SwiftSelf self);
            
            public System.Boolean isHEIFPreferred
            {
                get => isHEIFPreferred_Get();
                set => isHEIFPreferred_Set(value);
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
                
                PInvoke_init_48159C02(swiftIndirectResult, compressionQuality);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageEncodersO7DefaultV18compressionQualityAESf_tcfC")]
            private static extern void PInvoke_init_48159C02( SwiftIndirectResult swiftIndirectResult,  System.Single compressionQuality);
            
            
            public unsafe Swift.SwiftOptional<Swift.Data> encode( Swift.NSImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_encode_6F3D770D(arg0.Payload, self);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Data>>(new IntPtr(&result));
                }
                
                finally
                {
                    if (success)
                       _payload.DangerousRelease();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageEncodersO7DefaultV6encodey10Foundation4DataVSgSo7NSImageCF")]
            private static extern IntPtr PInvoke_encode_6F3D770D( SafeHandle arg0,  SwiftSelf self);
            
            
        }
        
        
    }
    
    
    public unsafe class ImagePrefetcher : ISwiftObject {
        SwiftSafeHandle<ImagePrefetcher> _payload = SwiftSafeHandle<ImagePrefetcher>.Zero;
        
        public SwiftSafeHandle<ImagePrefetcher> Payload => _payload;
        
        
                static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException();

                static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                    where TProtocol : class
                {
                    throw new NotImplementedException();
                }

                int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                {
                    throw new NotImplementedException();
                }
                static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                {
                    throw new NotImplementedException();
                }
            
        public unsafe class Destination : ISwiftObject
        {
            static nuint _payloadSize = SwiftObjectHelper<Destination>.GetTypeMetadata().Size;
            SwiftSafeHandle<Destination> _payload = SwiftSafeHandle<Destination>.Zero;
            public SwiftSafeHandle<Destination> Payload => _payload;
            
            /// <summary>
            /// Gets the 'memoryCache' case of Destination.
            /// </summary>
            public static Destination MemoryCache => throw new NotImplementedException("Enum case constructors not yet implemented");
            
            /// <summary>
            /// Gets the 'diskCache' case of Destination.
            /// </summary>
            public static Destination DiskCache => throw new NotImplementedException("Enum case constructors not yet implemented");
            
            
            private unsafe System.IntPtr hashValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_075BA6EA(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC11DestinationO9hashValueSivg")]
            private static extern System.IntPtr PInvoke_hashValue_Get_075BA6EA( SwiftSelf self);
            
            public System.IntPtr hashValue
            {
                get => hashValue_Get();
            }
            
                            static TypeMetadata ISwiftObject.GetTypeMetadata() => throw new NotImplementedException("Enum type metadata not yet implemented");
                            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                                where TProtocol : class
                            {
                                throw new NotImplementedException("Enum protocol conformance not yet implemented");
                            }
                            int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                            {
                                throw new NotImplementedException("Enum marshalling not yet implemented");
                            }
                            static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                            {
                                throw new NotImplementedException("Enum NewFromPayload not yet implemented");
                            }
                        
            
        }
        
        
        public unsafe Swift.Nuke.ImagePrefetcher init( Swift.Nuke.ImagePipeline pipeline,  Swift.Nuke.ImagePrefetcher.Destination destination,  System.IntPtr maxConcurrentRequestCount)
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePrefetcher>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_init_5B114626(swiftIndirectResult, pipeline.Payload, destination.Payload, maxConcurrentRequestCount);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePrefetcher>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC8pipeline11destination25maxConcurrentRequestCountAcA0B8PipelineC_AC11DestinationOSitcfC")]
        private static extern void PInvoke_init_5B114626( SwiftIndirectResult swiftIndirectResult,  SafeHandle pipeline,  SafeHandle destination,  System.IntPtr maxConcurrentRequestCount);
        
        
        public unsafe void startPrefetching( Swift.SwiftArray<Swift.URL> with)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> withDisposable = with.PayloadBuffer;
                IntPtr withBuffer = withDisposable.Buffer;
                
                PInvoke_startPrefetching_759232F3(withBuffer, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC16startPrefetching4withySay10Foundation3URLVG_tF")]
        private static extern void PInvoke_startPrefetching_759232F3( IntPtr withBuffer,  SwiftSelf self);
        
        
        public unsafe void _startPrefetching( Swift.SwiftArray<Swift.Nuke.ImageRequest> with)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> withDisposable = with.PayloadBuffer;
                IntPtr withBuffer = withDisposable.Buffer;
                
                PInvoke__startPrefetching_6EF22E6D(withBuffer, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC17_startPrefetching4withySayAA0B7RequestVG_tF")]
        private static extern void PInvoke__startPrefetching_6EF22E6D( IntPtr withBuffer,  SwiftSelf self);
        
        
        public unsafe void stopPrefetching( Swift.SwiftArray<Swift.URL> with)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> withDisposable = with.PayloadBuffer;
                IntPtr withBuffer = withDisposable.Buffer;
                
                PInvoke_stopPrefetching_5BCEAD12(withBuffer, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC15stopPrefetching4withySay10Foundation3URLVG_tF")]
        private static extern void PInvoke_stopPrefetching_5BCEAD12( IntPtr withBuffer,  SwiftSelf self);
        
        
        public unsafe void stopPrefetching()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_stopPrefetching_5EC44C4F(self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC15stopPrefetchingyyF")]
        private static extern void PInvoke_stopPrefetching_5EC44C4F( SwiftSelf self);
        
        
    }
    
    
}
