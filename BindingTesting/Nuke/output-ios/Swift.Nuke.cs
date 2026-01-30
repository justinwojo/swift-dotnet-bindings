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
    public interface ISwiftImageProcessing
    {
        Swift.SwiftString identifier { get; }
        Swift.AnyType hashableIdentifier { get; }
        Swift.SwiftOptional<UIKit.UIImage> process(UIKit.UIImage arg0);
        Swift.Nuke.ImageContainer process(Swift.Nuke.ImageContainer arg0, Swift.Nuke.ImageProcessingContext context);
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
                
                
                
                PInvoke_request_Get_7321BC07(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_request_Get_7321BC07( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Request_Set( Swift.Nuke.ImageRequest value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_request_Set_0CFD943E(value.Payload, self);
                
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
        private static extern void PInvoke_request_Set_0CFD943E( SafeHandle value,  SwiftSelf self);
        
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
                
                
                
                PInvoke_response_Get_030F80C6(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_response_Get_030F80C6( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Response_Set( Swift.Nuke.ImageResponse value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_response_Set_22F9E21D(value.Payload, self);
                
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
        private static extern void PInvoke_response_Set_22F9E21D( SafeHandle value,  SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_isCompleted_Get_25B66D71(self);
                
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
        private static extern System.Boolean PInvoke_isCompleted_Get_25B66D71( SwiftSelf self);
        
        private unsafe void IsCompleted_Set( System.Boolean value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_isCompleted_Set_5E299909(value, self);
                
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
        private static extern void PInvoke_isCompleted_Set_5E299909( System.Boolean value,  SwiftSelf self);
        
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
            
            PInvoke_init_01BA56B8(swiftIndirectResult, request.Payload, response.Payload, isCompleted);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingContextV7request8response11isCompletedAcA0B7RequestV_AA0B8ResponseVSbtcfC")]
        private static extern void PInvoke_init_01BA56B8( SwiftIndirectResult swiftIndirectResult,  SafeHandle request,  SafeHandle response,  System.Boolean isCompleted);
        
        
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
                IntPtr casePtr = PInvoke_Unknown();
                result._payload = new SwiftSafeHandle<ImageProcessingError>(casePtr);
                return result;
            }
        }
        
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageProcessingErrorO7unknownyA2CmF")]
        private static extern IntPtr PInvoke_Unknown();
        
        
        private unsafe Swift.SwiftString Description_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_description_Get_166B1318(self);
                
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
        private static extern Swift.SwiftString.Buffer PInvoke_description_Get_166B1318( SwiftSelf self);
        
        public Swift.SwiftString Description
        {
            get => Description_Get();
        }
        
        private unsafe System.IntPtr HashValue_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_hashValue_Get_75CC32A0(self);
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageProcessingErrorO9hashValueSivg")]
        private static extern System.IntPtr PInvoke_hashValue_Get_75CC32A0( SwiftSelf self);
        
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
                
                
                
                PInvoke_hash_438065C0(into.Payload, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageProcessingErrorO4hash4intoys6HasherVz_tF")]
        private static extern void PInvoke_hash_438065C0( SafeHandle into,  SwiftSelf self);
        
        
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
                
                
                
                PInvoke_container_Get_771B1C0F(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_container_Get_771B1C0F( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Container_Set( Swift.Nuke.ImageContainer value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_container_Set_714443BE(value.Payload, self);
                
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
        private static extern void PInvoke_container_Set_714443BE( SafeHandle value,  SwiftSelf self);
        
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
                
                
                
                PInvoke_image_Get_32314308(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_image_Get_32314308( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_isPreview_Get_08FB77C4(self);
                
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
        private static extern System.Boolean PInvoke_isPreview_Get_08FB77C4( SwiftSelf self);
        
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
                
                
                
                PInvoke_request_Get_695B8814(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_request_Get_695B8814( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Request_Set( Swift.Nuke.ImageRequest value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_request_Set_26507974(value.Payload, self);
                
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
        private static extern void PInvoke_request_Set_26507974( SafeHandle value,  SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_urlResponse_Get_3496E985(self);
                
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
        private static extern IntPtr PInvoke_urlResponse_Get_3496E985( SwiftSelf self);
        
        private unsafe void UrlResponse_Set( Swift.SwiftOptional<Foundation.NSUrlResponse> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_urlResponse_Set_6749190E(valueBuffer, self);
                
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
        private static extern void PInvoke_urlResponse_Set_6749190E( IntPtr valueBuffer,  SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_cacheType_Get_258D642B(self);
                
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
        private static extern IntPtr PInvoke_cacheType_Get_258D642B( SwiftSelf self);
        
        private unsafe void CacheType_Set( Swift.SwiftOptional<Swift.Nuke.ImageResponse.CacheType> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_cacheType_Set_709741AF(valueBuffer, self);
                
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
        private static extern void PInvoke_cacheType_Set_709741AF( IntPtr valueBuffer,  SwiftSelf self);
        
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
                    IntPtr casePtr = PInvoke_Memory();
                    result._payload = new SwiftSafeHandle<CacheType>(casePtr);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV9CacheTypeO6memoryyA2EmF")]
            private static extern IntPtr PInvoke_Memory();
            
            /// <summary>
            /// Gets the 'disk' case of CacheType.
            /// </summary>
            public static CacheType Disk
            {
                get
                {
                    var result = new CacheType();
                    IntPtr casePtr = PInvoke_Disk();
                    result._payload = new SwiftSafeHandle<CacheType>(casePtr);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV9CacheTypeO4diskyA2EmF")]
            private static extern IntPtr PInvoke_Disk();
            
            
            private unsafe System.IntPtr HashValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_148B6B95(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV9CacheTypeO9hashValueSivg")]
            private static extern System.IntPtr PInvoke_hashValue_Get_148B6B95( SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_hash_0A046AF5(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV9CacheTypeO4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_0A046AF5( SafeHandle into,  SwiftSelf self);
            
            
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
            PInvoke_init_2905CF38(swiftIndirectResult, container.Payload, request.Payload, urlResponseBuffer, cacheTypeBuffer);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageResponseV9container7request03urlC09cacheTypeAcA0B9ContainerV_AA0B7RequestVSo13NSURLResponseCSgAC05CacheH0OSgtcfC")]
        private static extern void PInvoke_init_2905CF38( SwiftIndirectResult swiftIndirectResult,  SafeHandle container,  SafeHandle request,  IntPtr urlResponseBuffer,  IntPtr cacheTypeBuffer);
        
        
    }
    
    
    public unsafe class ImageCache : ISwiftObject
    {
        private unsafe System.IntPtr CostLimit_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_costLimit_Get_1DFC8C6D(self);
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC9costLimitSivg")]
        private static extern System.IntPtr PInvoke_costLimit_Get_1DFC8C6D( SwiftSelf self);
        
        private unsafe void CostLimit_Set( System.IntPtr value)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_costLimit_Set_07397D85(value, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC9costLimitSivs")]
        private static extern void PInvoke_costLimit_Set_07397D85( System.IntPtr value,  SwiftSelf self);
        
        public System.IntPtr CostLimit
        {
            get => CostLimit_Get();
            set => CostLimit_Set(value);
        }
        
        private unsafe System.IntPtr CountLimit_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_countLimit_Get_6FF8C225(self);
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC10countLimitSivg")]
        private static extern System.IntPtr PInvoke_countLimit_Get_6FF8C225( SwiftSelf self);
        
        private unsafe void CountLimit_Set( System.IntPtr value)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_countLimit_Set_78D54964(value, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC10countLimitSivs")]
        private static extern void PInvoke_countLimit_Set_78D54964( System.IntPtr value,  SwiftSelf self);
        
        public System.IntPtr CountLimit
        {
            get => CountLimit_Get();
            set => CountLimit_Set(value);
        }
        
        private unsafe Swift.SwiftOptional<System.Double> Ttl_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_ttl_Get_29888FF5(self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<System.Double>>(new IntPtr(&result));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC3ttlSdSgvg")]
        private static extern IntPtr PInvoke_ttl_Get_29888FF5( SwiftSelf self);
        
        private unsafe void Ttl_Set( Swift.SwiftOptional<System.Double> value)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_ttl_Set_3A3A459E(valueBuffer, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC3ttlSdSgvs")]
        private static extern void PInvoke_ttl_Set_3A3A459E( IntPtr valueBuffer,  SwiftSelf self);
        
        public Swift.SwiftOptional<System.Double> Ttl
        {
            get => Ttl_Get();
            set => Ttl_Set(value);
        }
        
        private unsafe System.Double EntryCostLimit_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_entryCostLimit_Get_685E0942(self);
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC14entryCostLimitSdvg")]
        private static extern System.Double PInvoke_entryCostLimit_Get_685E0942( SwiftSelf self);
        
        private unsafe void EntryCostLimit_Set( System.Double value)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_entryCostLimit_Set_1333CD3A(value, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC14entryCostLimitSdvs")]
        private static extern void PInvoke_entryCostLimit_Set_1333CD3A( System.Double value,  SwiftSelf self);
        
        public System.Double EntryCostLimit
        {
            get => EntryCostLimit_Get();
            set => EntryCostLimit_Set(value);
        }
        
        private unsafe System.IntPtr TotalCount_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_totalCount_Get_2667CB07(self);
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC10totalCountSivg")]
        private static extern System.IntPtr PInvoke_totalCount_Get_2667CB07( SwiftSelf self);
        
        public System.IntPtr TotalCount
        {
            get => TotalCount_Get();
        }
        
        private unsafe System.IntPtr TotalCost_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_totalCost_Get_7ECFBD8C(self);
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC9totalCostSivg")]
        private static extern System.IntPtr PInvoke_totalCost_Get_7ECFBD8C( SwiftSelf self);
        
        public System.IntPtr TotalCost
        {
            get => TotalCost_Get();
        }
        
        private static unsafe Swift.Nuke.ImageCache Shared_Get()
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageCache>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_shared_Get_44B784F0(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageCache>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC6sharedACvgZ")]
        private static extern void PInvoke_shared_Get_44B784F0( SwiftIndirectResult swiftIndirectResult);
        
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
                
                
                
                PInvoke_init_603C48CC(swiftIndirectResult, costLimit, countLimit);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageCache>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC9costLimit05countE0ACSi_SitcfC")]
        private static extern void PInvoke_init_603C48CC( SwiftIndirectResult swiftIndirectResult,  System.IntPtr costLimit,  System.IntPtr countLimit);
        
        
        public static System.IntPtr DefaultCostLimit()
        {
            try
            {
                
                
                var result = PInvoke_defaultCostLimit_6602AF5A();
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC16defaultCostLimitSiyFZ")]
        private static extern System.IntPtr PInvoke_defaultCostLimit_6602AF5A();
        
        
        public unsafe void RemoveAll()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_removeAll_1DEE0943(self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC9removeAllyyF")]
        private static extern void PInvoke_removeAll_1DEE0943( SwiftSelf self);
        
        
        public unsafe void Trim( System.IntPtr toCost)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_trim_1FAE1D67(toCost, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10ImageCacheC4trim6toCostySi_tF")]
        private static extern void PInvoke_trim_1FAE1D67( System.IntPtr toCost,  SwiftSelf self);
        
        
    }
    
    
    public unsafe class ImagePipeline : ISwiftObject
    {
        private static unsafe Swift.Nuke.ImagePipeline Shared_Get()
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_shared_Get_4F3AEF4F(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC6sharedACvgZ")]
        private static extern void PInvoke_shared_Get_4F3AEF4F( SwiftIndirectResult swiftIndirectResult);
        
        private static void Shared_Set( Swift.Nuke.ImagePipeline value)
        {
            try
            {
                
                
                PInvoke_shared_Set_40D1CD5B(value.Payload);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC6sharedACvsZ")]
        private static extern void PInvoke_shared_Set_40D1CD5B( SafeHandle value);
        
        public static Swift.Nuke.ImagePipeline Shared
        {
            get => Shared_Get();
            set => Shared_Set(value);
        }
        
        private unsafe Swift.Nuke.ImagePipeline.Configuration Configuration_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.Configuration>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_configuration_Get_6706CA19(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.Configuration>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13configurationAC13ConfigurationVvg")]
        private static extern void PInvoke_configuration_Get_6706CA19( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Swift.Nuke.ImagePipeline.Configuration ConfigurationValue
        {
            get => Configuration_Get();
        }
        
        private unsafe Swift.Nuke.ImagePipeline.Cache Cache_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline.Cache>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_cache_Get_482512D7(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.Cache>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5cacheAC5CacheVvg")]
        private static extern void PInvoke_cache_Get_482512D7( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
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
            /// Gets the 'dataMissingInCache' case of Error.
            /// </summary>
            public static Error DataMissingInCache
            {
                get
                {
                    var result = new Error();
                    IntPtr casePtr = PInvoke_DataMissingInCache();
                    result._payload = new SwiftSafeHandle<Error>(casePtr);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5ErrorO18dataMissingInCacheyA2EmF")]
            private static extern IntPtr PInvoke_DataMissingInCache();
            
            /// <summary>
            /// Creates the 'dataLoadingFailed' case of Error.
            /// </summary>
            public static Error DataLoadingFailed((Swift.AnyType error, Swift.AnyType) value0)
            {
                var result = new Error();
                IntPtr casePtr = PInvoke_DataLoadingFailed((value0.error, value0.Item2));
                result._payload = new SwiftSafeHandle<Error>(casePtr);
                return result;
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5ErrorO17dataLoadingFailedyAEsAD_p_tcAEmF")]
            private static extern IntPtr PInvoke_DataLoadingFailed(ValueTuple<Swift.AnyType, Swift.AnyType> value0);
            
            /// <summary>
            /// Gets the 'dataIsEmpty' case of Error.
            /// </summary>
            public static Error DataIsEmpty
            {
                get
                {
                    var result = new Error();
                    IntPtr casePtr = PInvoke_DataIsEmpty();
                    result._payload = new SwiftSafeHandle<Error>(casePtr);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5ErrorO11dataIsEmptyyA2EmF")]
            private static extern IntPtr PInvoke_DataIsEmpty();
            
            /// <summary>
            /// Creates the 'decoderNotRegistered' case of Error.
            /// </summary>
            public static Error DecoderNotRegistered(Swift.Nuke.ImageDecodingContext context)
            {
                var result = new Error();
                IntPtr casePtr = PInvoke_DecoderNotRegistered(context);
                result._payload = new SwiftSafeHandle<Error>(casePtr);
                return result;
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5ErrorO20decoderNotRegisteredyAeA0B15DecodingContextV_tcAEmF")]
            private static extern IntPtr PInvoke_DecoderNotRegistered(Swift.Nuke.ImageDecodingContext context);
            
            /// <summary>
            /// Creates the 'decodingFailed' case of Error.
            /// </summary>
            public static Error DecodingFailed((Swift.AnyType decoder, Swift.Nuke.ISwiftImageDecoding, Swift.Nuke.ImageDecodingContext context, Swift.AnyType error, Swift.AnyType) value0)
            {
                var result = new Error();
                IntPtr casePtr = PInvoke_DecodingFailed((value0.decoder, value0.Item2, value0.context, value0.error, value0.Item5));
                result._payload = new SwiftSafeHandle<Error>(casePtr);
                return result;
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5ErrorO14decodingFailedyAeA0B8Decoding_p_AA0bG7ContextVsAD_ptcAEmF")]
            private static extern IntPtr PInvoke_DecodingFailed(ValueTuple<Swift.AnyType, Swift.Nuke.ISwiftImageDecoding, Swift.Nuke.ImageDecodingContext, Swift.AnyType, Swift.AnyType> value0);
            
            /// <summary>
            /// Creates the 'processingFailed' case of Error.
            /// </summary>
            public static Error ProcessingFailed((Swift.AnyType processor, Swift.Nuke.ISwiftImageProcessing, Swift.Nuke.ImageProcessingContext context, Swift.AnyType error, Swift.AnyType) value0)
            {
                var result = new Error();
                IntPtr casePtr = PInvoke_ProcessingFailed((value0.processor, value0.Item2, value0.context, value0.error, value0.Item5));
                result._payload = new SwiftSafeHandle<Error>(casePtr);
                return result;
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5ErrorO16processingFailedyAeA0B10Processing_p_AA0bG7ContextVsAD_ptcAEmF")]
            private static extern IntPtr PInvoke_ProcessingFailed(ValueTuple<Swift.AnyType, Swift.Nuke.ISwiftImageProcessing, Swift.Nuke.ImageProcessingContext, Swift.AnyType, Swift.AnyType> value0);
            
            /// <summary>
            /// Gets the 'imageRequestMissing' case of Error.
            /// </summary>
            public static Error ImageRequestMissing
            {
                get
                {
                    var result = new Error();
                    IntPtr casePtr = PInvoke_ImageRequestMissing();
                    result._payload = new SwiftSafeHandle<Error>(casePtr);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5ErrorO19imageRequestMissingyA2EmF")]
            private static extern IntPtr PInvoke_ImageRequestMissing();
            
            /// <summary>
            /// Gets the 'pipelineInvalidated' case of Error.
            /// </summary>
            public static Error PipelineInvalidated
            {
                get
                {
                    var result = new Error();
                    IntPtr casePtr = PInvoke_PipelineInvalidated();
                    result._payload = new SwiftSafeHandle<Error>(casePtr);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5ErrorO19pipelineInvalidatedyA2EmF")]
            private static extern IntPtr PInvoke_PipelineInvalidated();
            
            
            private unsafe Swift.SwiftString Description_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_61B8A9F1(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_61B8A9F1( SwiftSelf self);
            
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
                        
                        
                        
                        var result = PInvoke_rawValue_Get_6F8DFCD0(self);
                        
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
                private static extern System.IntPtr PInvoke_rawValue_Get_6F8DFCD0( SwiftSelf self);
                
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
                        
                        
                        
                        PInvoke_memory_Get_330B25FE(swiftIndirectResult);
                        
                        return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.Cache.Caches>(new IntPtr(swiftIndirectResult.Value));
                    }
                    
                    finally
                    {
                    }
                    
                }
                
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV6CachesV6memoryAGvgZ")]
                private static extern void PInvoke_memory_Get_330B25FE( SwiftIndirectResult swiftIndirectResult);
                
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
                        
                        
                        
                        PInvoke_disk_Get_468D52E5(swiftIndirectResult);
                        
                        return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.Cache.Caches>(new IntPtr(swiftIndirectResult.Value));
                    }
                    
                    finally
                    {
                    }
                    
                }
                
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV6CachesV4diskAGvgZ")]
                private static extern void PInvoke_disk_Get_468D52E5( SwiftIndirectResult swiftIndirectResult);
                
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
                        
                        
                        
                        PInvoke_all_Get_7E67670E(swiftIndirectResult);
                        
                        return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.Cache.Caches>(new IntPtr(swiftIndirectResult.Value));
                    }
                    
                    finally
                    {
                    }
                    
                }
                
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV6CachesV3allAGvgZ")]
                private static extern void PInvoke_all_Get_7E67670E( SwiftIndirectResult swiftIndirectResult);
                
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
                    
                    PInvoke_init_652C7E8D(swiftIndirectResult, rawValue);
                    
                }
                
                [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
                [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC5CacheV6CachesV8rawValueAGSi_tcfC")]
                private static extern void PInvoke_init_652C7E8D( SwiftIndirectResult swiftIndirectResult,  System.IntPtr rawValue);
                
                
            }
            
            
            public unsafe Swift.Nuke.ImageContainer? CachedImage( Swift.Nuke.ImageRequest _for,  Swift.Nuke.ImagePipeline.Cache.Caches caches)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_cachedImage_052ADE07(_for.Payload, caches.Payload, self);
                    
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
            private static extern IntPtr PInvoke_cachedImage_052ADE07( SafeHandle _for,  SafeHandle caches,  SwiftSelf self);
            
            
            public unsafe void StoreCachedImage( Swift.Nuke.ImageContainer arg0,  Swift.Nuke.ImageRequest _for,  Swift.Nuke.ImagePipeline.Cache.Caches caches)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_storeCachedImage_6F5D7178(arg0.Payload, _for.Payload, caches.Payload, self);
                    
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
            private static extern void PInvoke_storeCachedImage_6F5D7178( SafeHandle arg0,  SafeHandle _for,  SafeHandle caches,  SwiftSelf self);
            
            
            public unsafe void RemoveCachedImage( Swift.Nuke.ImageRequest _for,  Swift.Nuke.ImagePipeline.Cache.Caches caches)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_removeCachedImage_15B244CB(_for.Payload, caches.Payload, self);
                    
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
            private static extern void PInvoke_removeCachedImage_15B244CB( SafeHandle _for,  SafeHandle caches,  SwiftSelf self);
            
            
            public unsafe System.Boolean ContainsCachedImage( Swift.Nuke.ImageRequest _for,  Swift.Nuke.ImagePipeline.Cache.Caches caches)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_containsCachedImage_04403AD3(_for.Payload, caches.Payload, self);
                    
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
            private static extern System.Boolean PInvoke_containsCachedImage_04403AD3( SafeHandle _for,  SafeHandle caches,  SwiftSelf self);
            
            
            public unsafe Swift.Data? CachedData( Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_cachedData_316A7576(_for.Payload, self);
                    
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
            private static extern IntPtr PInvoke_cachedData_316A7576( SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe void StoreCachedData( Swift.Data arg0,  Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_storeCachedData_79D87372(arg0, _for.Payload, self);
                    
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
            private static extern void PInvoke_storeCachedData_79D87372( Swift.Data arg0,  SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe System.Boolean ContainsData( Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_containsData_33B59F35(_for.Payload, self);
                    
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
            private static extern System.Boolean PInvoke_containsData_33B59F35( SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe void RemoveCachedData( Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_removeCachedData_7D4D749C(_for.Payload, self);
                    
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
            private static extern void PInvoke_removeCachedData_7D4D749C( SafeHandle _for,  SwiftSelf self);
            
            
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
                    
                    
                    
                    PInvoke_makeImageCacheKey_4C36FDD4(swiftIndirectResult, _for.Payload, self);
                    
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
            private static extern void PInvoke_makeImageCacheKey_4C36FDD4( SwiftIndirectResult swiftIndirectResult,  SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe string MakeDataCacheKey( Swift.Nuke.ImageRequest _for)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_makeDataCacheKey_22359119(_for.Payload, self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_makeDataCacheKey_22359119( SafeHandle _for,  SwiftSelf self);
            
            
            public unsafe void RemoveAll( Swift.Nuke.ImagePipeline.Cache.Caches caches)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_removeAll_6A95D350(caches.Payload, self);
                    
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
            private static extern void PInvoke_removeAll_6A95D350( SafeHandle caches,  SwiftSelf self);
            
            
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
                    
                    
                    
                    PInvoke_dataLoader_Get_6565681E(self);
                    
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
            private static extern Swift.Runtime.ExistentialContainer1 PInvoke_dataLoader_Get_6565681E( SwiftSelf self);
            
            private unsafe void DataLoader_Set( Swift.Runtime.ExistentialContainer1 value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_dataLoader_Set_6744A353(value, self);
                    
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
            private static extern void PInvoke_dataLoader_Set_6744A353( Swift.Runtime.ExistentialContainer1 value,  SwiftSelf self);
            
            public Swift.Runtime.ExistentialContainer1 DataLoader
            {
                get => DataLoader_Get();
                set => DataLoader_Set(value);
            }
            
            private unsafe Swift.SwiftOptional<Swift.Nuke.ISwiftDataCaching> DataCache_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_dataCache_Get_6B0837C0(self);
                    
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
            private static extern IntPtr PInvoke_dataCache_Get_6B0837C0( SwiftSelf self);
            
            private unsafe void DataCache_Set( Swift.SwiftOptional<Swift.Nuke.ISwiftDataCaching> value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                    IntPtr valueBuffer = valueDisposable.Buffer;
                    
                    PInvoke_dataCache_Set_5563B1FD(valueBuffer, self);
                    
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
            private static extern void PInvoke_dataCache_Set_5563B1FD( IntPtr valueBuffer,  SwiftSelf self);
            
            public Swift.SwiftOptional<Swift.Nuke.ISwiftDataCaching> DataCache
            {
                get => DataCache_Get();
                set => DataCache_Set(value);
            }
            
            private unsafe Swift.SwiftOptional<Swift.Nuke.ISwiftImageCaching> ImageCache_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_imageCache_Get_7BCC1417(self);
                    
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
            private static extern IntPtr PInvoke_imageCache_Get_7BCC1417( SwiftSelf self);
            
            private unsafe void ImageCache_Set( Swift.SwiftOptional<Swift.Nuke.ISwiftImageCaching> value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                    IntPtr valueBuffer = valueDisposable.Buffer;
                    
                    PInvoke_imageCache_Set_1072BEF8(valueBuffer, self);
                    
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
            private static extern void PInvoke_imageCache_Set_1072BEF8( IntPtr valueBuffer,  SwiftSelf self);
            
            public Swift.SwiftOptional<Swift.Nuke.ISwiftImageCaching> ImageCache
            {
                get => ImageCache_Get();
                set => ImageCache_Set(value);
            }
            
            private unsafe System.Boolean IsDecompressionEnabled_Get()
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isDecompressionEnabled_Get_6E587F77(self);
                    
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
            private static extern System.Boolean PInvoke_isDecompressionEnabled_Get_6E587F77( SwiftSelf self);
            
            private unsafe void IsDecompressionEnabled_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isDecompressionEnabled_Set_40FA1433(value, self);
                    
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
            private static extern void PInvoke_isDecompressionEnabled_Set_40FA1433( System.Boolean value,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_isUsingPrepareForDisplay_Get_54AD0123(self);
                    
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
            private static extern System.Boolean PInvoke_isUsingPrepareForDisplay_Get_54AD0123( SwiftSelf self);
            
            private unsafe void IsUsingPrepareForDisplay_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isUsingPrepareForDisplay_Set_58E12E6E(value, self);
                    
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
            private static extern void PInvoke_isUsingPrepareForDisplay_Set_58E12E6E( System.Boolean value,  SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_dataCachePolicy_Get_23794B1D(swiftIndirectResult, self);
                    
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
            private static extern void PInvoke_dataCachePolicy_Get_23794B1D( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void DataCachePolicy_Set( Swift.Nuke.ImagePipeline.DataCachePolicy value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_dataCachePolicy_Set_6330C537(value.Payload, self);
                    
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
            private static extern void PInvoke_dataCachePolicy_Set_6330C537( SafeHandle value,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_isTaskCoalescingEnabled_Get_229D6567(self);
                    
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
            private static extern System.Boolean PInvoke_isTaskCoalescingEnabled_Get_229D6567( SwiftSelf self);
            
            private unsafe void IsTaskCoalescingEnabled_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isTaskCoalescingEnabled_Set_04DE99CC(value, self);
                    
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
            private static extern void PInvoke_isTaskCoalescingEnabled_Set_04DE99CC( System.Boolean value,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_isRateLimiterEnabled_Get_1A2EF436(self);
                    
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
            private static extern System.Boolean PInvoke_isRateLimiterEnabled_Get_1A2EF436( SwiftSelf self);
            
            private unsafe void IsRateLimiterEnabled_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isRateLimiterEnabled_Set_77438602(value, self);
                    
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
            private static extern void PInvoke_isRateLimiterEnabled_Set_77438602( System.Boolean value,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_isProgressiveDecodingEnabled_Get_6D770E8B(self);
                    
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
            private static extern System.Boolean PInvoke_isProgressiveDecodingEnabled_Get_6D770E8B( SwiftSelf self);
            
            private unsafe void IsProgressiveDecodingEnabled_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isProgressiveDecodingEnabled_Set_317F50C8(value, self);
                    
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
            private static extern void PInvoke_isProgressiveDecodingEnabled_Set_317F50C8( System.Boolean value,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_isStoringPreviewsInMemoryCache_Get_1D33293E(self);
                    
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
            private static extern System.Boolean PInvoke_isStoringPreviewsInMemoryCache_Get_1D33293E( SwiftSelf self);
            
            private unsafe void IsStoringPreviewsInMemoryCache_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isStoringPreviewsInMemoryCache_Set_39F8C80B(value, self);
                    
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
            private static extern void PInvoke_isStoringPreviewsInMemoryCache_Set_39F8C80B( System.Boolean value,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_isResumableDataEnabled_Get_0D53C686(self);
                    
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
            private static extern System.Boolean PInvoke_isResumableDataEnabled_Get_0D53C686( SwiftSelf self);
            
            private unsafe void IsResumableDataEnabled_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isResumableDataEnabled_Set_32701ACF(value, self);
                    
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
            private static extern void PInvoke_isResumableDataEnabled_Set_32701ACF( System.Boolean value,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_isLocalResourcesSupportEnabled_Get_7330A55C(self);
                    
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
            private static extern System.Boolean PInvoke_isLocalResourcesSupportEnabled_Get_7330A55C( SwiftSelf self);
            
            private unsafe void IsLocalResourcesSupportEnabled_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isLocalResourcesSupportEnabled_Set_0165A412(value, self);
                    
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
            private static extern void PInvoke_isLocalResourcesSupportEnabled_Set_0165A412( System.Boolean value,  SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_callbackQueue_Get_1DB9A09E(swiftIndirectResult, self);
                    
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
            private static extern void PInvoke_callbackQueue_Get_1DB9A09E( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void CallbackQueue_Set( Swift.DispatchQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_callbackQueue_Set_5BE1BDB1(value.Payload, self);
                    
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
            private static extern void PInvoke_callbackQueue_Set_5BE1BDB1( SafeHandle value,  SwiftSelf self);
            
            public Swift.DispatchQueue CallbackQueue
            {
                get => CallbackQueue_Get();
                set => CallbackQueue_Set(value);
            }
            
            private static System.Boolean IsSignpostLoggingEnabled_Get()
            {
                try
                {
                    
                    
                    var result = PInvoke_isSignpostLoggingEnabled_Get_352EBA11();
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV24isSignpostLoggingEnabledSbvgZ")]
            private static extern System.Boolean PInvoke_isSignpostLoggingEnabled_Get_352EBA11();
            
            private static void IsSignpostLoggingEnabled_Set( System.Boolean value)
            {
                try
                {
                    
                    
                    PInvoke_isSignpostLoggingEnabled_Set_54247E34(value);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV24isSignpostLoggingEnabledSbvsZ")]
            private static extern void PInvoke_isSignpostLoggingEnabled_Set_54247E34( System.Boolean value);
            
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
                    
                    
                    
                    PInvoke_dataLoadingQueue_Get_41AFBCD1(swiftIndirectResult, self);
                    
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
            private static extern void PInvoke_dataLoadingQueue_Get_41AFBCD1( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void DataLoadingQueue_Set( Foundation.NSOperationQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr valueHandle = value?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_dataLoadingQueue_Set_2552E98C(valueHandle, self);
                    
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
            private static extern void PInvoke_dataLoadingQueue_Set_2552E98C( IntPtr value,  SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_dataCachingQueue_Get_7D8F7DC8(swiftIndirectResult, self);
                    
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
            private static extern void PInvoke_dataCachingQueue_Get_7D8F7DC8( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void DataCachingQueue_Set( Foundation.NSOperationQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr valueHandle = value?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_dataCachingQueue_Set_340B26DF(valueHandle, self);
                    
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
            private static extern void PInvoke_dataCachingQueue_Set_340B26DF( IntPtr value,  SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_imageDecodingQueue_Get_4AEA3370(swiftIndirectResult, self);
                    
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
            private static extern void PInvoke_imageDecodingQueue_Get_4AEA3370( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void ImageDecodingQueue_Set( Foundation.NSOperationQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr valueHandle = value?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_imageDecodingQueue_Set_42CF25A6(valueHandle, self);
                    
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
            private static extern void PInvoke_imageDecodingQueue_Set_42CF25A6( IntPtr value,  SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_imageEncodingQueue_Get_11DD96A4(swiftIndirectResult, self);
                    
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
            private static extern void PInvoke_imageEncodingQueue_Get_11DD96A4( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void ImageEncodingQueue_Set( Foundation.NSOperationQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr valueHandle = value?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_imageEncodingQueue_Set_12BC49A7(valueHandle, self);
                    
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
            private static extern void PInvoke_imageEncodingQueue_Set_12BC49A7( IntPtr value,  SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_imageProcessingQueue_Get_293DD730(swiftIndirectResult, self);
                    
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
            private static extern void PInvoke_imageProcessingQueue_Get_293DD730( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void ImageProcessingQueue_Set( Foundation.NSOperationQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr valueHandle = value?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_imageProcessingQueue_Set_40D56BCF(valueHandle, self);
                    
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
            private static extern void PInvoke_imageProcessingQueue_Set_40D56BCF( IntPtr value,  SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_imageDecompressingQueue_Get_43344F2D(swiftIndirectResult, self);
                    
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
            private static extern void PInvoke_imageDecompressingQueue_Get_43344F2D( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
            private unsafe void ImageDecompressingQueue_Set( Foundation.NSOperationQueue value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr valueHandle = value?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_imageDecompressingQueue_Set_2C30CD52(valueHandle, self);
                    
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
            private static extern void PInvoke_imageDecompressingQueue_Set_2C30CD52( IntPtr value,  SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_withURLCache_Get_18FCD1E1(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.Configuration>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV12withURLCacheAEvgZ")]
            private static extern void PInvoke_withURLCache_Get_18FCD1E1( SwiftIndirectResult swiftIndirectResult);
            
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
                    
                    
                    
                    PInvoke_withDataCache_Get_79F5662A(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.Configuration>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV13withDataCacheAEvgZ")]
            private static extern void PInvoke_withDataCache_Get_79F5662A( SwiftIndirectResult swiftIndirectResult);
            
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
                
                PInvoke_init_248C8349(swiftIndirectResult, dataLoader);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV10dataLoaderAeA11DataLoading_p_tcfC")]
            private static extern void PInvoke_init_248C8349( SwiftIndirectResult swiftIndirectResult,  Swift.Runtime.ExistentialContainer1 dataLoader);
            
            
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
                    
                    PInvoke_withDataCache_618E4BDF(swiftIndirectResult, nameDisposable.Buffer, sizeLimitBuffer);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.Configuration>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13ConfigurationV13withDataCache4name9sizeLimitAESS_SiSgtFZ")]
            private static extern void PInvoke_withDataCache_618E4BDF( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer name,  IntPtr sizeLimitBuffer);
            
            
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
                    IntPtr casePtr = PInvoke_Automatic();
                    result._payload = new SwiftSafeHandle<DataCachePolicy>(casePtr);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC15DataCachePolicyO9automaticyA2EmF")]
            private static extern IntPtr PInvoke_Automatic();
            
            /// <summary>
            /// Gets the 'storeOriginalData' case of DataCachePolicy.
            /// </summary>
            public static DataCachePolicy StoreOriginalData
            {
                get
                {
                    var result = new DataCachePolicy();
                    IntPtr casePtr = PInvoke_StoreOriginalData();
                    result._payload = new SwiftSafeHandle<DataCachePolicy>(casePtr);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC15DataCachePolicyO013storeOriginalD0yA2EmF")]
            private static extern IntPtr PInvoke_StoreOriginalData();
            
            /// <summary>
            /// Gets the 'storeEncodedImages' case of DataCachePolicy.
            /// </summary>
            public static DataCachePolicy StoreEncodedImages
            {
                get
                {
                    var result = new DataCachePolicy();
                    IntPtr casePtr = PInvoke_StoreEncodedImages();
                    result._payload = new SwiftSafeHandle<DataCachePolicy>(casePtr);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC15DataCachePolicyO18storeEncodedImagesyA2EmF")]
            private static extern IntPtr PInvoke_StoreEncodedImages();
            
            /// <summary>
            /// Gets the 'storeAll' case of DataCachePolicy.
            /// </summary>
            public static DataCachePolicy StoreAll
            {
                get
                {
                    var result = new DataCachePolicy();
                    IntPtr casePtr = PInvoke_StoreAll();
                    result._payload = new SwiftSafeHandle<DataCachePolicy>(casePtr);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC15DataCachePolicyO8storeAllyA2EmF")]
            private static extern IntPtr PInvoke_StoreAll();
            
            
            private unsafe System.IntPtr HashValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_2852D835(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC15DataCachePolicyO9hashValueSivg")]
            private static extern System.IntPtr PInvoke_hashValue_Get_2852D835( SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_hash_08AE6BEC(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC15DataCachePolicyO4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_08AE6BEC( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        public unsafe Swift.Nuke.ImagePipeline Init( Swift.Nuke.ImagePipeline.Configuration configuration,  Swift.Nuke.ISwiftImagePipelineDelegate? _delegate)
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                using var _delegateSwift = _delegate is {} _delegateValue ? SwiftOptional<Swift.Nuke.ISwiftImagePipelineDelegate>.NewSome(_delegateValue) : SwiftOptional<Swift.Nuke.ISwiftImagePipelineDelegate>.NewNone();
                using PayloadBuffer<IntPtr> _delegateDisposable = _delegateSwift.PayloadBuffer;
                IntPtr _delegateBuffer = _delegateDisposable.Buffer;
                
                PInvoke_init_1B9D5D67(swiftIndirectResult, configuration.Payload, _delegateBuffer);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC13configuration8delegateA2C13ConfigurationV_AA0bC8Delegate_pSgtcfC")]
        private static extern void PInvoke_init_1B9D5D67( SwiftIndirectResult swiftIndirectResult,  SafeHandle configuration,  IntPtr _delegateBuffer);
        
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, SwiftSelf, void> s_init_arg1_77A22363_Callback = &init_arg1_77A22363_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void init_arg1_77A22363_Callback(void* arg0, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Action<Swift.Nuke.ImagePipeline.Configuration>>(new IntPtr(context.Value));
            del(SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline.Configuration>(new IntPtr(arg0)));
        }
        
        public unsafe Swift.Nuke.ImagePipeline Init( Swift.Nuke.ISwiftImagePipelineDelegate? _delegate,  Action<Swift.Nuke.ImagePipeline.Configuration> arg1)
        {
            GCHandle arg1Handle = default;
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePipeline>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                arg1Handle = GCHandle.Alloc(arg1);
                var arg1Closure = new SwiftClosureData((IntPtr)s_init_arg1_77A22363_Callback, GCHandle.ToIntPtr(arg1Handle));
                using var _delegateSwift = _delegate is {} _delegateValue ? SwiftOptional<Swift.Nuke.ISwiftImagePipelineDelegate>.NewSome(_delegateValue) : SwiftOptional<Swift.Nuke.ISwiftImagePipelineDelegate>.NewNone();
                using PayloadBuffer<IntPtr> _delegateDisposable = _delegateSwift.PayloadBuffer;
                IntPtr _delegateBuffer = _delegateDisposable.Buffer;
                
                PInvoke_init_77A22363(swiftIndirectResult, _delegateBuffer, arg1Closure);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePipeline>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (arg1Handle.IsAllocated) arg1Handle.Free();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC8delegate_AcA0bC8Delegate_pSg_yAC13ConfigurationVzXEtcfC")]
        private static extern void PInvoke_init_77A22363( SwiftIndirectResult swiftIndirectResult,  IntPtr _delegateBuffer,  SwiftClosureData arg1);
        
        
        public unsafe void Invalidate()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_invalidate_6CFF20EA(self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC10invalidateyyF")]
        private static extern void PInvoke_invalidate_6CFF20EA( SwiftSelf self);
        
        
        public unsafe Swift.Nuke.ImageTask ImageTask( Swift.URL with)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageTask>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_imageTask_48D6F8A0(swiftIndirectResult, with.Payload, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageTask>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC9imageTask4withAA0bE0C10Foundation3URLV_tF")]
        private static extern void PInvoke_imageTask_48D6F8A0( SwiftIndirectResult swiftIndirectResult,  SafeHandle with,  SwiftSelf self);
        
        
        public unsafe Swift.Nuke.ImageTask ImageTask( Swift.Nuke.ImageRequest with)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageTask>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_imageTask_5C830B65(swiftIndirectResult, with.Payload, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageTask>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC9imageTask4withAA0bE0CAA0B7RequestV_tF")]
        private static extern void PInvoke_imageTask_5C830B65( SwiftIndirectResult swiftIndirectResult,  SafeHandle with,  SwiftSelf self);
        
        
                private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> s_imageCallback_5AADF4DE = &imageOnComplete_5AADF4DE;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void imageOnComplete_5AADF4DE(IntPtr rawResult, IntPtr task)
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
                    // TODO: Call Destroy on copy buffers to properly release refs (needs type info)
                    for (int i = 1; i < holder.Length; i++)
                    {
                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                        {
                            // Release the extra retain added for async safety
                            Arc.Release(retained.Ptr);
                        }
                        else if (holder[i] is IntPtr copyBuffer && copyBuffer != IntPtr.Zero)
                        {
                            NativeMemory.Free((void*)copyBuffer);
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
        public unsafe Task<UIKit.UIImage> Image( Swift.URL _for)
        {
            var _forMetadata = SwiftObjectHelper<Swift.URL>.GetTypeMetadata();
            IntPtr _forCopyBuffer = (IntPtr)NativeMemory.Alloc(_forMetadata.Size);
            _forMetadata.ValueWitnessTable->InitializeWithCopy(
                (void*)_forCopyBuffer,
                (void*)_for.Payload.DangerousGetHandle(),
                _forMetadata);
            IntPtr _forHandle = _forCopyBuffer;
            IntPtr _selfPtr = _payload.DangerousGetHandle();
            Arc.Retain(_selfPtr);
            TaskCompletionSource<UIKit.UIImage> task = new TaskCompletionSource<UIKit.UIImage>();
            object[] _asyncCallHolder = new object[] { task, _forCopyBuffer, (object)_for, new RetainedSelfPtr(_selfPtr), (object)this };
            GCHandle handle = GCHandle.Alloc(_asyncCallHolder, GCHandleType.Normal);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_image_5AADF4DE(s_imageCallback_5AADF4DE, GCHandle.ToIntPtr(handle), _forHandle, self);
                
                return task.Task;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("SwiftBindings", EntryPoint = "$s4Nuke13ImagePipelineC5image3forSo7UIImageC10Foundation3URLV_tYaKF_async")]
        private static extern void PInvoke_image_5AADF4DE( void* s_imageCallback_5AADF4DE,  IntPtr handle,  IntPtr _for,  SwiftSelf self);
        
        
                private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> s_imageCallback_00C45306 = &imageOnComplete_00C45306;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void imageOnComplete_00C45306(IntPtr rawResult, IntPtr task)
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
                    // TODO: Call Destroy on copy buffers to properly release refs (needs type info)
                    for (int i = 1; i < holder.Length; i++)
                    {
                        if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                        {
                            // Release the extra retain added for async safety
                            Arc.Release(retained.Ptr);
                        }
                        else if (holder[i] is IntPtr copyBuffer && copyBuffer != IntPtr.Zero)
                        {
                            NativeMemory.Free((void*)copyBuffer);
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
        public unsafe Task<UIKit.UIImage> Image( Swift.Nuke.ImageRequest _for)
        {
            var _forMetadata = SwiftObjectHelper<Swift.Nuke.ImageRequest>.GetTypeMetadata();
            IntPtr _forCopyBuffer = (IntPtr)NativeMemory.Alloc(_forMetadata.Size);
            _forMetadata.ValueWitnessTable->InitializeWithCopy(
                (void*)_forCopyBuffer,
                (void*)_for.Payload.DangerousGetHandle(),
                _forMetadata);
            IntPtr _forHandle = _forCopyBuffer;
            IntPtr _selfPtr = _payload.DangerousGetHandle();
            Arc.Retain(_selfPtr);
            TaskCompletionSource<UIKit.UIImage> task = new TaskCompletionSource<UIKit.UIImage>();
            object[] _asyncCallHolder = new object[] { task, _forCopyBuffer, (object)_for, new RetainedSelfPtr(_selfPtr), (object)this };
            GCHandle handle = GCHandle.Alloc(_asyncCallHolder, GCHandleType.Normal);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_image_00C45306(s_imageCallback_00C45306, GCHandle.ToIntPtr(handle), _forHandle, self);
                
                return task.Task;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("SwiftBindings", EntryPoint = "$s4Nuke13ImagePipelineC5image3forSo7UIImageCAA0B7RequestV_tYaKF_async")]
        private static extern void PInvoke_image_00C45306( void* s_imageCallback_00C45306,  IntPtr handle,  IntPtr _for,  SwiftSelf self);
        
        
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, SwiftSelf, void> s_loadImage_completion_2261D1FB_Callback = &loadImage_completion_2261D1FB_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void loadImage_completion_2261D1FB_Callback(void* arg0, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Action<Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error>>>(new IntPtr(context.Value));
            del(SwiftMarshal.MarshalFromSwift<Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error>>(new IntPtr(arg0)));
        }
        
        public unsafe Swift.Nuke.ImageTask LoadImage( Swift.URL with,  Action<Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error>> completion)
        {
            GCHandle completionHandle = default;
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageTask>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                completionHandle = GCHandle.Alloc(completion);
                var completionClosure = new SwiftClosureData((IntPtr)s_loadImage_completion_2261D1FB_Callback, GCHandle.ToIntPtr(completionHandle));
                
                PInvoke_loadImage_2261D1FB(swiftIndirectResult, with.Payload, completionClosure, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageTask>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (completionHandle.IsAllocated) completionHandle.Free();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC04loadB04with10completionAA0B4TaskC10Foundation3URLV_ys6ResultOyAA0B8ResponseVAC5ErrorOGctF")]
        private static extern void PInvoke_loadImage_2261D1FB( SwiftIndirectResult swiftIndirectResult,  SafeHandle with,  SwiftClosureData completion,  SwiftSelf self);
        
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, SwiftSelf, void> s_loadImage_completion_189A2A50_Callback = &loadImage_completion_189A2A50_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void loadImage_completion_189A2A50_Callback(void* arg0, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Action<Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error>>>(new IntPtr(context.Value));
            del(SwiftMarshal.MarshalFromSwift<Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error>>(new IntPtr(arg0)));
        }
        
        public unsafe Swift.Nuke.ImageTask LoadImage( Swift.Nuke.ImageRequest with,  Action<Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error>> completion)
        {
            GCHandle completionHandle = default;
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageTask>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                completionHandle = GCHandle.Alloc(completion);
                var completionClosure = new SwiftClosureData((IntPtr)s_loadImage_completion_189A2A50_Callback, GCHandle.ToIntPtr(completionHandle));
                
                PInvoke_loadImage_189A2A50(swiftIndirectResult, with.Payload, completionClosure, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageTask>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (completionHandle.IsAllocated) completionHandle.Free();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImagePipelineC04loadB04with10completionAA0B4TaskCAA0B7RequestV_ys6ResultOyAA0B8ResponseVAC5ErrorOGctF")]
        private static extern void PInvoke_loadImage_189A2A50( SwiftIndirectResult swiftIndirectResult,  SafeHandle with,  SwiftClosureData completion,  SwiftSelf self);
        
        
        
        
        
        
        
        
        
    }
    
    
    public unsafe class DataLoader : ISwiftObject
    {
        private unsafe Foundation.NSUrlSession Session_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Foundation.NSUrlSession>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_session_Get_71ECE130(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Foundation.NSUrlSession>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10DataLoaderC7sessionSo12NSURLSessionCvg")]
        private static extern void PInvoke_session_Get_71ECE130( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Foundation.NSUrlSession Session
        {
            get => Session_Get();
        }
        
        private unsafe System.Boolean PrefersIncrementalDelivery_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_prefersIncrementalDelivery_Get_02C1CD76(self);
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10DataLoaderC26prefersIncrementalDeliverySbvg")]
        private static extern System.Boolean PInvoke_prefersIncrementalDelivery_Get_02C1CD76( SwiftSelf self);
        
        private unsafe void PrefersIncrementalDelivery_Set( System.Boolean value)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_prefersIncrementalDelivery_Set_592A46B3(value, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10DataLoaderC26prefersIncrementalDeliverySbvs")]
        private static extern void PInvoke_prefersIncrementalDelivery_Set_592A46B3( System.Boolean value,  SwiftSelf self);
        
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
                
                
                
                PInvoke_defaultConfiguration_Get_32E47152(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Foundation.NSUrlSessionConfiguration>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10DataLoaderC20defaultConfigurationSo012NSURLSessionE0CvgZ")]
        private static extern void PInvoke_defaultConfiguration_Get_32E47152( SwiftIndirectResult swiftIndirectResult);
        
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
                
                
                
                PInvoke_sharedUrlCache_Get_72E04AAC(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Foundation.NSUrlCache>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10DataLoaderC14sharedUrlCacheSo10NSURLCacheCvgZ")]
        private static extern void PInvoke_sharedUrlCache_Get_72E04AAC( SwiftIndirectResult swiftIndirectResult);
        
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
                IntPtr casePtr = PInvoke_StatusCodeUnacceptable(value0);
                result._payload = new SwiftSafeHandle<Error>(casePtr);
                return result;
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke10DataLoaderC5ErrorO22statusCodeUnacceptableyAESicAEmF")]
            private static extern IntPtr PInvoke_StatusCodeUnacceptable(System.IntPtr value0);
            
            
            private unsafe Swift.SwiftString Description_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_47D7DA16(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_47D7DA16( SwiftSelf self);
            
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
        
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, void*, SwiftSelf, void> s_init_validate_2793BD61_Callback = &init_validate_2793BD61_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void init_validate_2793BD61_Callback(void* indirectResult, void* arg0, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Func<Foundation.NSUrlResponse, Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1>>>(new IntPtr(context.Value));
            var result = del(SwiftMarshal.MarshalFromSwift<Foundation.NSUrlResponse>(new IntPtr(arg0)));
            // Marshal the result to the indirect result buffer
            var metadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1>>();
            var resultSpan = new Span<byte>(indirectResult, (int)metadata.Size);
            SwiftMarshal.MarshalToSwift(result, ref resultSpan);
        }
        
        public unsafe Swift.Nuke.DataLoader Init( Foundation.NSUrlSessionConfiguration configuration,  Func<Foundation.NSUrlResponse, Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1>> validate)
        {
            IntPtr configurationHandle = configuration?.Handle ?? IntPtr.Zero;
            GCHandle validateHandle = default;
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.DataLoader>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                validateHandle = GCHandle.Alloc(validate);
                var validateClosure = new SwiftClosureData((IntPtr)s_init_validate_2793BD61_Callback, GCHandle.ToIntPtr(validateHandle));
                
                PInvoke_init_2793BD61(swiftIndirectResult, configurationHandle, validateClosure);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.DataLoader>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (validateHandle.IsAllocated) validateHandle.Free();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10DataLoaderC13configuration8validateACSo25NSURLSessionConfigurationC_s5Error_pSgSo13NSURLResponseCYbctcfC")]
        private static extern void PInvoke_init_2793BD61( SwiftIndirectResult swiftIndirectResult,  IntPtr configuration,  SwiftClosureData validate);
        
        
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, void*, SwiftSelf, void> s_loadData_didReceiveData_4D4BDFFC_Callback = &loadData_didReceiveData_4D4BDFFC_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void loadData_didReceiveData_4D4BDFFC_Callback(void* arg0, void* arg1, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Action<Swift.Data, Foundation.NSUrlResponse>>(new IntPtr(context.Value));
            del(SwiftMarshal.MarshalFromSwift<Swift.Data>(new IntPtr(arg0)), SwiftMarshal.MarshalFromSwift<Foundation.NSUrlResponse>(new IntPtr(arg1)));
        }
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, SwiftSelf, void> s_loadData_completion_4D4BDFFC_Callback = &loadData_completion_4D4BDFFC_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void loadData_completion_4D4BDFFC_Callback(void* arg0, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Action<Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1>>>(new IntPtr(context.Value));
            del(SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1>>(new IntPtr(arg0)));
        }
        
        public unsafe Swift.Runtime.ExistentialContainer1 LoadData( Swift.URLRequest with,  Action<Swift.Data, Foundation.NSUrlResponse> didReceiveData,  Action<Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1>> completion)
        {
            GCHandle didReceiveDataHandle = default;
            GCHandle completionHandle = default;
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Runtime.ExistentialContainer1>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                didReceiveDataHandle = GCHandle.Alloc(didReceiveData);
                var didReceiveDataClosure = new SwiftClosureData((IntPtr)s_loadData_didReceiveData_4D4BDFFC_Callback, GCHandle.ToIntPtr(didReceiveDataHandle));
                completionHandle = GCHandle.Alloc(completion);
                var completionClosure = new SwiftClosureData((IntPtr)s_loadData_completion_4D4BDFFC_Callback, GCHandle.ToIntPtr(completionHandle));
                
                PInvoke_loadData_4D4BDFFC(with.Payload, didReceiveDataClosure, completionClosure, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Runtime.ExistentialContainer1>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
                if (didReceiveDataHandle.IsAllocated) didReceiveDataHandle.Free();
                if (completionHandle.IsAllocated) completionHandle.Free();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke10DataLoaderC04loadB04with010didReceiveB010completionAA11Cancellable_p10Foundation10URLRequestV_yAI0B0V_So13NSURLResponseCtcys5Error_pSgctF")]
        private static extern Swift.Runtime.ExistentialContainer1 PInvoke_loadData_4D4BDFFC( SafeHandle with,  SwiftClosureData didReceiveData,  SwiftClosureData completion,  SwiftSelf self);
        
        
    }
    
    
    public interface ISwiftImageEncoding
    {
        Swift.SwiftOptional<Swift.Data> encode(UIKit.UIImage arg0);
        Swift.SwiftOptional<Swift.Data> encode(Swift.Nuke.ImageContainer arg0, Swift.Nuke.ImageEncodingContext context);
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
                
                
                
                PInvoke_request_Get_61E6B340(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_request_Get_61E6B340( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
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
                
                
                
                PInvoke_image_Get_15D54F40(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_image_Get_15D54F40( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_urlResponse_Get_16055D74(self);
                
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
        private static extern IntPtr PInvoke_urlResponse_Get_16055D74( SwiftSelf self);
        
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
        Swift.Nuke.ISwiftCancellable loadData(Swift.URLRequest with, Swift.AnyType didReceiveData, Swift.AnyType completion);
    }
    
    
    public interface ISwiftCancellable
    {
        void cancel();
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
                
                
                
                PInvoke_image_Get_48DFCBA8(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_image_Get_48DFCBA8( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Image_Set( UIKit.UIImage value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            IntPtr valueHandle = value?.Handle ?? IntPtr.Zero;
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_image_Set_55E4355C(valueHandle, self);
                
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
        private static extern void PInvoke_image_Set_55E4355C( IntPtr value,  SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_type_Get_4326722D(self);
                
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
        private static extern IntPtr PInvoke_type_Get_4326722D( SwiftSelf self);
        
        private unsafe void Type_Set( Swift.SwiftOptional<Swift.Nuke.AssetType> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_type_Set_4A4AA6DD(valueBuffer, self);
                
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
        private static extern void PInvoke_type_Set_4A4AA6DD( IntPtr valueBuffer,  SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_isPreview_Get_0E02686D(self);
                
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
        private static extern System.Boolean PInvoke_isPreview_Get_0E02686D( SwiftSelf self);
        
        private unsafe void IsPreview_Set( System.Boolean value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_isPreview_Set_10E79BCA(value, self);
                
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
        private static extern void PInvoke_isPreview_Set_10E79BCA( System.Boolean value,  SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_data_Get_069D4285(self);
                
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
        private static extern IntPtr PInvoke_data_Get_069D4285( SwiftSelf self);
        
        private unsafe void Data_Set( Swift.SwiftOptional<Swift.Data> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_data_Set_4B877557(valueBuffer, self);
                
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
        private static extern void PInvoke_data_Set_4B877557( IntPtr valueBuffer,  SwiftSelf self);
        
        public Swift.SwiftOptional<Swift.Data> Data
        {
            get => Data_Get();
            set => Data_Set(value);
        }
        
        private unsafe Swift.SwiftDictionary<Swift.Nuke.ImageContainer.UserInfoKey, Swift.Runtime.ExistentialContainer0> UserInfo_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_userInfo_Get_22289C63(self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.SwiftDictionary<Swift.Nuke.ImageContainer.UserInfoKey, Swift.Runtime.ExistentialContainer0>>(new IntPtr(&result));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV8userInfoSDyAC04UserE3KeyVypGvg")]
        private static extern IntPtr PInvoke_userInfo_Get_22289C63( SwiftSelf self);
        
        private unsafe void UserInfo_Set( Swift.SwiftDictionary<Swift.Nuke.ImageContainer.UserInfoKey, Swift.Runtime.ExistentialContainer0> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_userInfo_Set_664240E5(valueBuffer, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV8userInfoSDyAC04UserE3KeyVypGvs")]
        private static extern void PInvoke_userInfo_Set_664240E5( IntPtr valueBuffer,  SwiftSelf self);
        
        public Swift.SwiftDictionary<Swift.Nuke.ImageContainer.UserInfoKey, Swift.Runtime.ExistentialContainer0> UserInfo
        {
            get => UserInfo_Get();
            set => UserInfo_Set(value);
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
                    
                    
                    
                    var result = PInvoke_rawValue_Get_6486E0C4(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_rawValue_Get_6486E0C4( SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_scanNumberKey_Get_1660F4E8(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageContainer.UserInfoKey>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV11UserInfoKeyV010scanNumberF0AEvgZ")]
            private static extern void PInvoke_scanNumberKey_Get_1660F4E8( SwiftIndirectResult swiftIndirectResult);
            
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
                    
                    
                    
                    var result = PInvoke_hashValue_Get_69169C97(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_69169C97( SwiftSelf self);
            
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
                PInvoke_init_0F90A4E9(swiftIndirectResult, arg0Disposable.Buffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV11UserInfoKeyVyAESScfC")]
            private static extern void PInvoke_init_0F90A4E9( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer arg0);
            
            
            public unsafe void Hash( Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_1B7F689A(into.Payload, self);
                    
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
            private static extern void PInvoke_hash_1B7F689A( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        public unsafe ImageContainer( UIKit.UIImage image,  Swift.Nuke.AssetType? type,  System.Boolean isPreview,  Swift.Data? data,  Swift.SwiftDictionary<Swift.Nuke.ImageContainer.UserInfoKey, Swift.Runtime.ExistentialContainer0> userInfo)
        {
            IntPtr imageHandle = image?.Handle ?? IntPtr.Zero;
            _payload = new SwiftSafeHandle<ImageContainer>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            using PayloadBuffer<IntPtr> userInfoDisposable = userInfo.PayloadBuffer;
            IntPtr userInfoBuffer = userInfoDisposable.Buffer;
            using var typeSwift = type is {} typeValue ? SwiftOptional<Swift.Nuke.AssetType>.NewSome(typeValue) : SwiftOptional<Swift.Nuke.AssetType>.NewNone();
            using PayloadBuffer<IntPtr> typeDisposable = typeSwift.PayloadBuffer;
            IntPtr typeBuffer = typeDisposable.Buffer;
            using var dataSwift = data is {} dataValue ? SwiftOptional<Swift.Data>.NewSome(dataValue) : SwiftOptional<Swift.Data>.NewNone();
            using PayloadBuffer<IntPtr> dataDisposable = dataSwift.PayloadBuffer;
            IntPtr dataBuffer = dataDisposable.Buffer;
            PInvoke_init_609C4819(swiftIndirectResult, imageHandle, typeBuffer, isPreview, dataBuffer, userInfoBuffer);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke14ImageContainerV5image4type9isPreview4data8userInfoACSo7UIImageC_AA9AssetTypeVSgSb10Foundation4DataVSgSDyAC04UserJ3KeyVypGtcfC")]
        private static extern void PInvoke_init_609C4819( SwiftIndirectResult swiftIndirectResult,  IntPtr image,  IntPtr typeBuffer,  System.Boolean isPreview,  IntPtr dataBuffer,  IntPtr userInfoBuffer);
        
        
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
                    IntPtr casePtr = PInvoke_Points();
                    result._payload = new SwiftSafeHandle<Unit>(casePtr);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO4UnitO6pointsyA2EmF")]
            private static extern IntPtr PInvoke_Points();
            
            /// <summary>
            /// Gets the 'pixels' case of Unit.
            /// </summary>
            public static Unit Pixels
            {
                get
                {
                    var result = new Unit();
                    IntPtr casePtr = PInvoke_Pixels();
                    result._payload = new SwiftSafeHandle<Unit>(casePtr);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO4UnitO6pixelsyA2EmF")]
            private static extern IntPtr PInvoke_Pixels();
            
            
            private unsafe Swift.SwiftString Description_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_536A7B63(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_536A7B63( SwiftSelf self);
            
            public Swift.SwiftString Description
            {
                get => Description_Get();
            }
            
            private unsafe System.IntPtr HashValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_6E7D9004(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO4UnitO9hashValueSivg")]
            private static extern System.IntPtr PInvoke_hashValue_Get_6E7D9004( SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_hash_644A8D36(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO4UnitO4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_644A8D36( SafeHandle into,  SwiftSelf self);
            
            
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
                    
                    
                    
                    var result = PInvoke_width_Get_5AD6BEB7(self);
                    
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
            private static extern System.Double PInvoke_width_Get_5AD6BEB7( SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_color_Get_60BDD895(swiftIndirectResult, self);
                    
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
            private static extern void PInvoke_color_Get_60BDD895( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_description_Get_4290FC60(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_4290FC60( SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_hashValue_Get_518A5577(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_518A5577( SwiftSelf self);
            
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
                
                PInvoke_init_3DC64669(swiftIndirectResult, colorHandle, width, unit.Payload);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO6BorderV5color5width4unitAESo7UIColorC_12CoreGraphics7CGFloatVAC4UnitOtcfC")]
            private static extern void PInvoke_init_3DC64669( SwiftIndirectResult swiftIndirectResult,  IntPtr color,  System.Double width,  SafeHandle unit);
            
            
            public unsafe void Hash( Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_1580EA52(into.Payload, self);
                    
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
            private static extern void PInvoke_hash_1580EA52( SafeHandle into,  SwiftSelf self);
            
            
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
                    IntPtr casePtr = PInvoke_AspectFill();
                    result._payload = new SwiftSafeHandle<ContentMode>(casePtr);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO11ContentModeO10aspectFillyA2EmF")]
            private static extern IntPtr PInvoke_AspectFill();
            
            /// <summary>
            /// Gets the 'aspectFit' case of ContentMode.
            /// </summary>
            public static ContentMode AspectFit
            {
                get
                {
                    var result = new ContentMode();
                    IntPtr casePtr = PInvoke_AspectFit();
                    result._payload = new SwiftSafeHandle<ContentMode>(casePtr);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO11ContentModeO9aspectFityA2EmF")]
            private static extern IntPtr PInvoke_AspectFit();
            
            
            private unsafe Swift.SwiftString Description_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_description_Get_36655011(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_36655011( SwiftSelf self);
            
            public Swift.SwiftString Description
            {
                get => Description_Get();
            }
            
            private unsafe System.IntPtr HashValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_638D3A17(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO11ContentModeO9hashValueSivg")]
            private static extern System.IntPtr PInvoke_hashValue_Get_638D3A17( SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_hash_27282B53(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke22ImageProcessingOptionsO11ContentModeO4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_27282B53( SafeHandle into,  SwiftSelf self);
            
            
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
        void imageTask(Swift.Nuke.ImageTask arg0, Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error> didCompleteWithResult, Swift.Nuke.ImagePipeline pipeline);
    }
    
    
    public interface ISwiftImageCaching
    {
        void removeAll();
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
                
                
                
                var result = PInvoke_hashValue_Get_6EA49F2B(self);
                
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
        private static extern System.IntPtr PInvoke_hashValue_Get_6EA49F2B( SwiftSelf self);
        
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
            PInvoke_init_6006FFB9(swiftIndirectResult, keyDisposable.Buffer);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageCacheKeyV3keyACSS_tcfC")]
        private static extern void PInvoke_init_6006FFB9( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer key);
        
        
        public unsafe ImageCacheKey( Swift.Nuke.ImageRequest request)
        {
            _payload = new SwiftSafeHandle<ImageCacheKey>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            PInvoke_init_30F3ADCB(swiftIndirectResult, request.Payload);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageCacheKeyV7requestAcA0B7RequestV_tcfC")]
        private static extern void PInvoke_init_30F3ADCB( SwiftIndirectResult swiftIndirectResult,  SafeHandle request);
        
        
        public unsafe void Hash( Swift.Hasher into)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_hash_31DDA6DE(into.Payload, self);
                
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
        private static extern void PInvoke_hash_31DDA6DE( SafeHandle into,  SwiftSelf self);
        
        
    }
    
    
    public unsafe class DataCache : ISwiftObject
    {
        private unsafe System.IntPtr SizeLimit_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_sizeLimit_Get_59CDF26C(self);
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC9sizeLimitSivg")]
        private static extern System.IntPtr PInvoke_sizeLimit_Get_59CDF26C( SwiftSelf self);
        
        private unsafe void SizeLimit_Set( System.IntPtr value)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_sizeLimit_Set_61789FC0(value, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC9sizeLimitSivs")]
        private static extern void PInvoke_sizeLimit_Set_61789FC0( System.IntPtr value,  SwiftSelf self);
        
        public System.IntPtr SizeLimit
        {
            get => SizeLimit_Get();
            set => SizeLimit_Set(value);
        }
        
        private unsafe Swift.URL Path_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.URL>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_path_Get_2932A21B(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.URL>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC4path10Foundation3URLVvg")]
        private static extern void PInvoke_path_Get_2932A21B( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Swift.URL Path
        {
            get => Path_Get();
        }
        
        private unsafe System.Double SweepInterval_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_sweepInterval_Get_3A884FB7(self);
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC13sweepIntervalSdvg")]
        private static extern System.Double PInvoke_sweepInterval_Get_3A884FB7( SwiftSelf self);
        
        private unsafe void SweepInterval_Set( System.Double value)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_sweepInterval_Set_105C3F7A(value, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC13sweepIntervalSdvs")]
        private static extern void PInvoke_sweepInterval_Set_105C3F7A( System.Double value,  SwiftSelf self);
        
        public System.Double SweepInterval
        {
            get => SweepInterval_Get();
            set => SweepInterval_Set(value);
        }
        
        private unsafe System.Boolean IsCompressionEnabled_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_isCompressionEnabled_Get_17E33D5F(self);
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC20isCompressionEnabledSbvg")]
        private static extern System.Boolean PInvoke_isCompressionEnabled_Get_17E33D5F( SwiftSelf self);
        
        private unsafe void IsCompressionEnabled_Set( System.Boolean value)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_isCompressionEnabled_Set_564F2613(value, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC20isCompressionEnabledSbvs")]
        private static extern void PInvoke_isCompressionEnabled_Set_564F2613( System.Boolean value,  SwiftSelf self);
        
        public System.Boolean IsCompressionEnabled
        {
            get => IsCompressionEnabled_Get();
            set => IsCompressionEnabled_Set(value);
        }
        
        private unsafe Swift.DispatchQueue Queue_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.DispatchQueue>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_queue_Get_00E6F200(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.DispatchQueue>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC5queueSo012OS_dispatch_D0Cvg")]
        private static extern void PInvoke_queue_Get_00E6F200( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Swift.DispatchQueue Queue
        {
            get => Queue_Get();
        }
        
        private unsafe System.IntPtr TotalCount_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_totalCount_Get_555D5AF0(self);
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC10totalCountSivg")]
        private static extern System.IntPtr PInvoke_totalCount_Get_555D5AF0( SwiftSelf self);
        
        public System.IntPtr TotalCount
        {
            get => TotalCount_Get();
        }
        
        private unsafe System.IntPtr TotalSize_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_totalSize_Get_21471494(self);
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC9totalSizeSivg")]
        private static extern System.IntPtr PInvoke_totalSize_Get_21471494( SwiftSelf self);
        
        public System.IntPtr TotalSize
        {
            get => TotalSize_Get();
        }
        
        private unsafe System.IntPtr TotalAllocatedSize_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_totalAllocatedSize_Get_7A778F89(self);
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC18totalAllocatedSizeSivg")]
        private static extern System.IntPtr PInvoke_totalAllocatedSize_Get_7A778F89( SwiftSelf self);
        
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
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, void*, SwiftSelf, void> s_init_filenameGenerator_1308364D_Callback = &init_filenameGenerator_1308364D_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void init_filenameGenerator_1308364D_Callback(void* indirectResult, void* arg0, SwiftSelf context)
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
                var filenameGeneratorClosure = new SwiftClosureData((IntPtr)s_init_filenameGenerator_1308364D_Callback, GCHandle.ToIntPtr(filenameGeneratorHandle));
                using var nameSwift = new SwiftString(name);
                using PayloadBuffer<SwiftString.Buffer> nameDisposable = nameSwift.PayloadBuffer;
                
                PInvoke_init_1308364D(swiftIndirectResult, nameDisposable.Buffer, filenameGeneratorClosure, out var error);
                
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
        private static extern void PInvoke_init_1308364D( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer name,  SwiftClosureData filenameGenerator, out SwiftError error);
        
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, void*, SwiftSelf, void> s_init_filenameGenerator_398B4356_Callback = &init_filenameGenerator_398B4356_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void init_filenameGenerator_398B4356_Callback(void* indirectResult, void* arg0, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Func<Swift.SwiftString, Swift.SwiftOptional<Swift.SwiftString>>>(new IntPtr(context.Value));
            var result = del(SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(arg0)));
            // Marshal the result to the indirect result buffer
            var metadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.SwiftOptional<Swift.SwiftString>>();
            var resultSpan = new Span<byte>(indirectResult, (int)metadata.Size);
            SwiftMarshal.MarshalToSwift(result, ref resultSpan);
        }
        
        public unsafe Swift.Nuke.DataCache Init( Swift.URL path,  Func<Swift.SwiftString, Swift.SwiftOptional<Swift.SwiftString>> filenameGenerator)
        {
            GCHandle filenameGeneratorHandle = default;
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.DataCache>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                filenameGeneratorHandle = GCHandle.Alloc(filenameGenerator);
                var filenameGeneratorClosure = new SwiftClosureData((IntPtr)s_init_filenameGenerator_398B4356_Callback, GCHandle.ToIntPtr(filenameGeneratorHandle));
                
                PInvoke_init_398B4356(swiftIndirectResult, path.Payload, filenameGeneratorClosure, out var error);
                
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
        private static extern void PInvoke_init_398B4356( SwiftIndirectResult swiftIndirectResult,  SafeHandle path,  SwiftClosureData filenameGenerator, out SwiftError error);
        
        
        public static unsafe Swift.SwiftString? Filename( string _for)
        {
            try
            {
                
                using var _forSwift = new SwiftString(_for);
                using PayloadBuffer<SwiftString.Buffer> _forDisposable = _forSwift.PayloadBuffer;
                
                var result = PInvoke_filename_4797BA27(_forDisposable.Buffer);
                
                var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.SwiftString>>(new IntPtr(&result));
                return swiftResult.ToNullable();
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC8filename3forSSSgSS_tFZ")]
        private static extern IntPtr PInvoke_filename_4797BA27( Swift.SwiftString.Buffer _for);
        
        
        public unsafe Swift.Data? CachedData( string _for)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using var _forSwift = new SwiftString(_for);
                using PayloadBuffer<SwiftString.Buffer> _forDisposable = _forSwift.PayloadBuffer;
                
                var result = PInvoke_cachedData_6F25D787(_forDisposable.Buffer, self);
                
                var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Data>>(new IntPtr(&result));
                return swiftResult.ToNullable();
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC06cachedB03for10Foundation0B0VSgSS_tF")]
        private static extern IntPtr PInvoke_cachedData_6F25D787( Swift.SwiftString.Buffer _for,  SwiftSelf self);
        
        
        public unsafe System.Boolean ContainsData( string _for)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using var _forSwift = new SwiftString(_for);
                using PayloadBuffer<SwiftString.Buffer> _forDisposable = _forSwift.PayloadBuffer;
                
                var result = PInvoke_containsData_48DEB298(_forDisposable.Buffer, self);
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC08containsB03forSbSS_tF")]
        private static extern System.Boolean PInvoke_containsData_48DEB298( Swift.SwiftString.Buffer _for,  SwiftSelf self);
        
        
        public unsafe void StoreData( Swift.Data arg0,  string _for)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using var _forSwift = new SwiftString(_for);
                using PayloadBuffer<SwiftString.Buffer> _forDisposable = _forSwift.PayloadBuffer;
                
                PInvoke_storeData_400740FC(arg0, _forDisposable.Buffer, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC05storeB0_3fory10Foundation0B0V_SStF")]
        private static extern void PInvoke_storeData_400740FC( Swift.Data arg0,  Swift.SwiftString.Buffer _for,  SwiftSelf self);
        
        
        public unsafe void RemoveData( string _for)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using var _forSwift = new SwiftString(_for);
                using PayloadBuffer<SwiftString.Buffer> _forDisposable = _forSwift.PayloadBuffer;
                
                PInvoke_removeData_38B06340(_forDisposable.Buffer, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC06removeB03forySS_tF")]
        private static extern void PInvoke_removeData_38B06340( Swift.SwiftString.Buffer _for,  SwiftSelf self);
        
        
        public unsafe void RemoveAll()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_removeAll_7670B93B(self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC9removeAllyyF")]
        private static extern void PInvoke_removeAll_7670B93B( SwiftSelf self);
        
        
        public unsafe Swift.URL? Url( string _for)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using var _forSwift = new SwiftString(_for);
                using PayloadBuffer<SwiftString.Buffer> _forDisposable = _forSwift.PayloadBuffer;
                
                var result = PInvoke_url_15DD1497(_forDisposable.Buffer, self);
                
                var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.URL>>(new IntPtr(&result));
                return swiftResult.ToNullable();
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC3url3for10Foundation3URLVSgSS_tF")]
        private static extern IntPtr PInvoke_url_15DD1497( Swift.SwiftString.Buffer _for,  SwiftSelf self);
        
        
        public unsafe void Flush()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_flush_7CE36935(self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC5flushyyF")]
        private static extern void PInvoke_flush_7CE36935( SwiftSelf self);
        
        
        public unsafe void Flush( string _for)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using var _forSwift = new SwiftString(_for);
                using PayloadBuffer<SwiftString.Buffer> _forDisposable = _forSwift.PayloadBuffer;
                
                PInvoke_flush_50E471D8(_forDisposable.Buffer, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC5flush3forySS_tF")]
        private static extern void PInvoke_flush_50E471D8( Swift.SwiftString.Buffer _for,  SwiftSelf self);
        
        
        public unsafe void Sweep()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_sweep_5FD31F73(self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9DataCacheC5sweepyyF")]
        private static extern void PInvoke_sweep_5FD31F73( SwiftSelf self);
        
        
    }
    
    
    public unsafe class ImageDecoderRegistry : ISwiftObject
    {
        private static unsafe Swift.Nuke.ImageDecoderRegistry Shared_Get()
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageDecoderRegistry>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_shared_Get_24A8F440(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageDecoderRegistry>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecoderRegistryC6sharedACvgZ")]
        private static extern void PInvoke_shared_Get_24A8F440( SwiftIndirectResult swiftIndirectResult);
        
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
                
                
                
                PInvoke_init_1E42C1FA(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageDecoderRegistry>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecoderRegistryCACycfC")]
        private static extern void PInvoke_init_1E42C1FA( SwiftIndirectResult swiftIndirectResult);
        
        
        public unsafe Swift.Nuke.ISwiftImageDecoding? Decoder( Swift.Nuke.ImageDecodingContext _for)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_decoder_11246CEC(_for.Payload, self);
                
                var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Nuke.ISwiftImageDecoding>>(new IntPtr(&result));
                return swiftResult.ToNullable();
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecoderRegistryC7decoder3forAA0B8Decoding_pSgAA0bG7ContextV_tF")]
        private static extern IntPtr PInvoke_decoder_11246CEC( SafeHandle _for,  SwiftSelf self);
        
        
        private static unsafe readonly delegate* unmanaged[Swift]<void*, void*, SwiftSelf, void> s_register_arg0_68FEA869_Callback = &register_arg0_68FEA869_Callback;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
        private static void register_arg0_68FEA869_Callback(void* indirectResult, void* arg0, SwiftSelf context)
        {
            var del = SwiftClosureMarshaller.GetDelegateFromContext<Func<Swift.Nuke.ImageDecodingContext, Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1>>>(new IntPtr(context.Value));
            var result = del(SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageDecodingContext>(new IntPtr(arg0)));
            // Marshal the result to the indirect result buffer
            var metadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1>>();
            var resultSpan = new Span<byte>(indirectResult, (int)metadata.Size);
            SwiftMarshal.MarshalToSwift(result, ref resultSpan);
        }
        
        public unsafe void Register( Func<Swift.Nuke.ImageDecodingContext, Swift.SwiftOptional<Swift.Runtime.ExistentialContainer1>> arg0)
        {
            GCHandle arg0Handle = default;
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                arg0Handle = GCHandle.Alloc(arg0);
                var arg0Closure = new SwiftClosureData((IntPtr)s_register_arg0_68FEA869_Callback, GCHandle.ToIntPtr(arg0Handle));
                
                PInvoke_register_68FEA869(arg0Closure, self);
                
                return;
            }
            
            finally
            {
                if (arg0Handle.IsAllocated) arg0Handle.Free();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecoderRegistryC8registeryyAA0B8Decoding_pSgAA0bF7ContextVcF")]
        private static extern void PInvoke_register_68FEA869( SwiftClosureData arg0,  SwiftSelf self);
        
        
        public unsafe void Clear()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_clear_3245E055(self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecoderRegistryC5clearyyF")]
        private static extern void PInvoke_clear_3245E055( SwiftSelf self);
        
        
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
                
                
                
                PInvoke_request_Get_69DDB9A6(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_request_Get_69DDB9A6( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Request_Set( Swift.Nuke.ImageRequest value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_request_Set_003278AE(value.Payload, self);
                
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
        private static extern void PInvoke_request_Set_003278AE( SafeHandle value,  SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_data_Get_2647022A(self);
                
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
        private static extern Swift.Data PInvoke_data_Get_2647022A( SwiftSelf self);
        
        private unsafe void Data_Set( Swift.Data value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_data_Set_6D74013A(value, self);
                
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
        private static extern void PInvoke_data_Set_6D74013A( Swift.Data value,  SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_isCompleted_Get_74C67AB5(self);
                
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
        private static extern System.Boolean PInvoke_isCompleted_Get_74C67AB5( SwiftSelf self);
        
        private unsafe void IsCompleted_Set( System.Boolean value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_isCompleted_Set_10445A2B(value, self);
                
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
        private static extern void PInvoke_isCompleted_Set_10445A2B( System.Boolean value,  SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_urlResponse_Get_21A6C390(self);
                
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
        private static extern IntPtr PInvoke_urlResponse_Get_21A6C390( SwiftSelf self);
        
        private unsafe void UrlResponse_Set( Swift.SwiftOptional<Foundation.NSUrlResponse> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_urlResponse_Set_13437BAC(valueBuffer, self);
                
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
        private static extern void PInvoke_urlResponse_Set_13437BAC( IntPtr valueBuffer,  SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_cacheType_Get_4F34A739(self);
                
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
        private static extern IntPtr PInvoke_cacheType_Get_4F34A739( SwiftSelf self);
        
        private unsafe void CacheType_Set( Swift.SwiftOptional<Swift.Nuke.ImageResponse.CacheType> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_cacheType_Set_6614B162(valueBuffer, self);
                
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
        private static extern void PInvoke_cacheType_Set_6614B162( IntPtr valueBuffer,  SwiftSelf self);
        
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
        
        
        public unsafe ImageDecodingContext( Swift.Nuke.ImageRequest request,  Swift.Data data,  System.Boolean isCompleted,  Foundation.NSUrlResponse? urlResponse,  Swift.Nuke.ImageResponse.CacheType? cacheType)
        {
            _payload = new SwiftSafeHandle<ImageDecodingContext>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            using var urlResponseSwift = urlResponse is {} urlResponseValue ? SwiftOptional<Foundation.NSUrlResponse>.NewSome(urlResponseValue) : SwiftOptional<Foundation.NSUrlResponse>.NewNone();
            using PayloadBuffer<IntPtr> urlResponseDisposable = urlResponseSwift.PayloadBuffer;
            IntPtr urlResponseBuffer = urlResponseDisposable.Buffer;
            using var cacheTypeSwift = cacheType is {} cacheTypeValue ? SwiftOptional<Swift.Nuke.ImageResponse.CacheType>.NewSome(cacheTypeValue) : SwiftOptional<Swift.Nuke.ImageResponse.CacheType>.NewNone();
            using PayloadBuffer<IntPtr> cacheTypeDisposable = cacheTypeSwift.PayloadBuffer;
            IntPtr cacheTypeBuffer = cacheTypeDisposable.Buffer;
            PInvoke_init_680246C6(swiftIndirectResult, request.Payload, data, isCompleted, urlResponseBuffer, cacheTypeBuffer);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke20ImageDecodingContextV7request4data11isCompleted11urlResponse9cacheTypeAcA0B7RequestV_10Foundation4DataVSbSo13NSURLResponseCSgAA0bJ0V05CacheL0OSgtcfC")]
        private static extern void PInvoke_init_680246C6( SwiftIndirectResult swiftIndirectResult,  SafeHandle request,  Swift.Data data,  System.Boolean isCompleted,  IntPtr urlResponseBuffer,  IntPtr cacheTypeBuffer);
        
        
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
                    
                    
                    
                    var result = PInvoke_identifier_Get_2E28FDB6(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_2E28FDB6( SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_description_Get_4959646A(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_4959646A( SwiftSelf self);
            
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
            
            
            private static unsafe readonly delegate* unmanaged[Swift]<void*, void*, SwiftSelf, void> s_init_arg1_6F74B14D_Callback = &init_arg1_6F74B14D_Callback;
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
            private static void init_arg1_6F74B14D_Callback(void* indirectResult, void* arg0, SwiftSelf context)
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
                    var arg1Closure = new SwiftClosureData((IntPtr)s_init_arg1_6F74B14D_Callback, GCHandle.ToIntPtr(arg1Handle));
                    using var idSwift = new SwiftString(id);
                    using PayloadBuffer<SwiftString.Buffer> idDisposable = idSwift.PayloadBuffer;
                    PInvoke_init_6F74B14D(swiftIndirectResult, idDisposable.Buffer, arg1Closure);
                    
                }
                
                finally
                {
                    if (arg1Handle.IsAllocated) arg1Handle.Free();
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO9AnonymousV2id_AESS_So7UIImageCSgAHYbctcfC")]
            private static extern void PInvoke_init_6F74B14D( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer id,  SwiftClosureData arg1);
            
            
            public unsafe UIKit.UIImage? Process( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_2EF70C95(arg0Handle, self);
                    
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
            private static extern IntPtr PInvoke_process_2EF70C95( IntPtr arg0,  SwiftSelf self);
            
            
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
                    
                    
                    
                    var result = PInvoke_identifier_Get_58EE0FBE(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_58EE0FBE( SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_description_Get_6FF49F67(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_6FF49F67( SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_hashValue_Get_02934560(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_02934560( SwiftSelf self);
            
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
                PInvoke_init_77AFDFFC(swiftIndirectResult, radius, unit.Payload, borderBuffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO14RoundedCornersV6radius4unit6borderAE12CoreGraphics7CGFloatV_AA0B17ProcessingOptionsO4UnitOAM6BorderVSgtcfC")]
            private static extern void PInvoke_init_77AFDFFC( SwiftIndirectResult swiftIndirectResult,  System.Double radius,  SafeHandle unit,  IntPtr borderBuffer);
            
            
            public unsafe UIKit.UIImage? Process( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_1158A1C0(arg0Handle, self);
                    
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
            private static extern IntPtr PInvoke_process_1158A1C0( IntPtr arg0,  SwiftSelf self);
            
            
            public unsafe void Hash( Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_397FD121(into.Payload, self);
                    
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
            private static extern void PInvoke_hash_397FD121( SafeHandle into,  SwiftSelf self);
            
            
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
                    
                    
                    
                    var result = PInvoke_identifier_Get_44FDC1F7(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_44FDC1F7( SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_description_Get_0A28FE3A(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_0A28FE3A( SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_hashValue_Get_481A7FF9(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_481A7FF9( SwiftSelf self);
            
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
            
            
            
            public unsafe Resize( System.Double width,  Swift.Nuke.ImageProcessingOptions.Unit unit,  System.Boolean upscale)
            {
                _payload = new SwiftSafeHandle<Resize>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                PInvoke_init_1B5BE5B9(swiftIndirectResult, width, unit.Payload, upscale);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6ResizeV5width4unit7upscaleAE12CoreGraphics7CGFloatV_AA0B17ProcessingOptionsO4UnitOSbtcfC")]
            private static extern void PInvoke_init_1B5BE5B9( SwiftIndirectResult swiftIndirectResult,  System.Double width,  SafeHandle unit,  System.Boolean upscale);
            
            
            public unsafe UIKit.UIImage? Process( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_7ED191FF(arg0Handle, self);
                    
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
            private static extern IntPtr PInvoke_process_7ED191FF( IntPtr arg0,  SwiftSelf self);
            
            
            public unsafe void Hash( Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_10569492(into.Payload, self);
                    
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
            private static extern void PInvoke_hash_10569492( SafeHandle into,  SwiftSelf self);
            
            
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
                    
                    
                    
                    var result = PInvoke_identifier_Get_6B0053B5(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_6B0053B5( SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_description_Get_6E773772(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_6E773772( SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_hashValue_Get_407A9C35(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_407A9C35( SwiftSelf self);
            
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
                
                PInvoke_init_25A08301(swiftIndirectResult, radius);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO12GaussianBlurV6radiusAESi_tcfC")]
            private static extern void PInvoke_init_25A08301( SwiftIndirectResult swiftIndirectResult,  System.IntPtr radius);
            
            
            public unsafe UIKit.UIImage? Process( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_4140401B(arg0Handle, self);
                    
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
            private static extern IntPtr PInvoke_process_4140401B( IntPtr arg0,  SwiftSelf self);
            
            
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
                    
                    
                    
                    PInvoke_process_4E6FD3B1(swiftIndirectResult, arg0.Payload, context.Payload, self, out var error);
                    
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
            private static extern void PInvoke_process_4E6FD3B1( SwiftIndirectResult swiftIndirectResult,  SafeHandle arg0,  SafeHandle context,  SwiftSelf self, out SwiftError error);
            
            
            public unsafe void Hash( Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_400E2F92(into.Payload, self);
                    
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
            private static extern void PInvoke_hash_400E2F92( SafeHandle into,  SwiftSelf self);
            
            
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
                    
                    
                    
                    var result = PInvoke_identifier_Get_5FA1A1DB(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_5FA1A1DB( SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_description_Get_07AE658A(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_07AE658A( SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_hashValue_Get_408182BE(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_408182BE( SwiftSelf self);
            
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
            
            
            public unsafe Composition( IEnumerable<Swift.Nuke.ISwiftImageProcessing> arg0)
            {
                _payload = new SwiftSafeHandle<Composition>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                using var arg0Swift = SwiftArray<Swift.Nuke.ISwiftImageProcessing>.FromEnumerable(arg0);
                using PayloadBuffer<IntPtr> arg0Disposable = arg0Swift.PayloadBuffer;
                IntPtr arg0Buffer = arg0Disposable.Buffer;
                PInvoke_init_4C9851CC(swiftIndirectResult, arg0Buffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO11CompositionVyAESayAA0B10Processing_pGcfC")]
            private static extern void PInvoke_init_4C9851CC( SwiftIndirectResult swiftIndirectResult,  IntPtr arg0Buffer);
            
            
            public unsafe UIKit.UIImage? Process( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_3318D2D3(arg0Handle, self);
                    
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
            private static extern IntPtr PInvoke_process_3318D2D3( IntPtr arg0,  SwiftSelf self);
            
            
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
                    
                    
                    
                    PInvoke_process_73662B13(swiftIndirectResult, arg0.Payload, context.Payload, self, out var error);
                    
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
            private static extern void PInvoke_process_73662B13( SwiftIndirectResult swiftIndirectResult,  SafeHandle arg0,  SafeHandle context,  SwiftSelf self, out SwiftError error);
            
            
            public unsafe void Hash( Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_29FD08F7(into.Payload, self);
                    
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
            private static extern void PInvoke_hash_29FD08F7( SafeHandle into,  SwiftSelf self);
            
            
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
                    
                    
                    
                    var result = PInvoke_identifier_Get_539A3165(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_539A3165( SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_description_Get_7F0F5900(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_7F0F5900( SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_hashValue_Get_5C38EB5C(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_5C38EB5C( SwiftSelf self);
            
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
                PInvoke_init_235E16DF(swiftIndirectResult, borderBuffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO6CircleV6borderAeA0B17ProcessingOptionsO6BorderVSg_tcfC")]
            private static extern void PInvoke_init_235E16DF( SwiftIndirectResult swiftIndirectResult,  IntPtr borderBuffer);
            
            
            public unsafe UIKit.UIImage? Process( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_6F4B8BDA(arg0Handle, self);
                    
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
            private static extern IntPtr PInvoke_process_6F4B8BDA( IntPtr arg0,  SwiftSelf self);
            
            
            public unsafe void Hash( Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_6F86B45A(into.Payload, self);
                    
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
            private static extern void PInvoke_hash_6F86B45A( SafeHandle into,  SwiftSelf self);
            
            
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
                    
                    
                    
                    var result = PInvoke_identifier_Get_598B3888(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_identifier_Get_598B3888( SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_context_Get_0336D39E(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.CIContext>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV7contextSo9CIContextCvgZ")]
            private static extern void PInvoke_context_Get_0336D39E( SwiftIndirectResult swiftIndirectResult);
            
            private static void Context_Set( Swift.CIContext value)
            {
                try
                {
                    
                    
                    PInvoke_context_Set_0166E80E(value.Payload);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV7contextSo9CIContextCvsZ")]
            private static extern void PInvoke_context_Set_0166E80E( SafeHandle value);
            
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
                    
                    
                    
                    var result = PInvoke_description_Get_257318FD(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_description_Get_257318FD( SwiftSelf self);
            
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
                public static Error FailedToCreateFilter((Swift.SwiftString name, Swift.SwiftDictionary<Swift.SwiftString, Swift.Runtime.ExistentialContainer0> parameters) value0)
                {
                    var result = new Error();
                    IntPtr casePtr = PInvoke_FailedToCreateFilter((value0.name.Payload.DangerousGetHandle(), value0.parameters.Payload.DangerousGetHandle()));
                    result._payload = new SwiftSafeHandle<Error>(casePtr);
                    return result;
                }
                
                [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV5ErrorO014failedToCreateE0yAGSS_SDySSypGtcAGmF")]
                private static extern IntPtr PInvoke_FailedToCreateFilter(ValueTuple<IntPtr, IntPtr> value0);
                
                /// <summary>
                /// Creates the 'inputImageIsEmpty' case of Error.
                /// </summary>
                public static Error InputImageIsEmpty(UIKit.UIImage inputImage)
                {
                    var result = new Error();
                    IntPtr casePtr = PInvoke_InputImageIsEmpty(inputImage.Handle);
                    result._payload = new SwiftSafeHandle<Error>(casePtr);
                    return result;
                }
                
                [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV5ErrorO05inputB7IsEmptyyAGSo7UIImageC_tcAGmF")]
                private static extern IntPtr PInvoke_InputImageIsEmpty(IntPtr inputImage);
                
                
                private unsafe Swift.SwiftString Description_Get()
                {
                    try
                    {
                        var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                        
                        
                        
                        var result = PInvoke_description_Get_09B9141B(self);
                        
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
                private static extern Swift.SwiftString.Buffer PInvoke_description_Get_09B9141B( SwiftSelf self);
                
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
            
            
            public unsafe CoreImageFilter( string name,  Swift.SwiftDictionary<Swift.SwiftString, Swift.Runtime.ExistentialContainer0> parameters,  string identifier)
            {
                _payload = new SwiftSafeHandle<CoreImageFilter>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                using PayloadBuffer<IntPtr> parametersDisposable = parameters.PayloadBuffer;
                IntPtr parametersBuffer = parametersDisposable.Buffer;
                using var nameSwift = new SwiftString(name);
                using PayloadBuffer<SwiftString.Buffer> nameDisposable = nameSwift.PayloadBuffer;
                using var identifierSwift = new SwiftString(identifier);
                using PayloadBuffer<SwiftString.Buffer> identifierDisposable = identifierSwift.PayloadBuffer;
                PInvoke_init_77A509C8(swiftIndirectResult, nameDisposable.Buffer, parametersBuffer, identifierDisposable.Buffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV4name10parameters10identifierAESS_SDySSypGSStcfC")]
            private static extern void PInvoke_init_77A509C8( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer name,  IntPtr parametersBuffer,  Swift.SwiftString.Buffer identifier);
            
            
            public unsafe CoreImageFilter( string name)
            {
                _payload = new SwiftSafeHandle<CoreImageFilter>((IntPtr)NativeMemory.Alloc(_payloadSize));
                var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
                
                using var nameSwift = new SwiftString(name);
                using PayloadBuffer<SwiftString.Buffer> nameDisposable = nameSwift.PayloadBuffer;
                PInvoke_init_2E351A77(swiftIndirectResult, nameDisposable.Buffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImageProcessorsO04CoreB6FilterV4nameAESS_tcfC")]
            private static extern void PInvoke_init_2E351A77( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer name);
            
            
            
            public unsafe UIKit.UIImage? Process( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_process_6B0DE49D(arg0Handle, self);
                    
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
            private static extern IntPtr PInvoke_process_6B0DE49D( IntPtr arg0,  SwiftSelf self);
            
            
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
                    
                    
                    
                    PInvoke_process_0D4D6F98(swiftIndirectResult, arg0.Payload, context.Payload, self, out var error);
                    
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
            private static extern void PInvoke_process_0D4D6F98( SwiftIndirectResult swiftIndirectResult,  SafeHandle arg0,  SafeHandle context,  SwiftSelf self, out SwiftError error);
            
            
            
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
                
                
                
                PInvoke_priority_Get_0D57933D(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_priority_Get_0D57933D( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Priority_Set( Swift.Nuke.ImageRequest.Priority value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_priority_Set_20A0E7AF(value.Payload, self);
                
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
        private static extern void PInvoke_priority_Set_20A0E7AF( SafeHandle value,  SwiftSelf self);
        
        public Swift.Nuke.ImageRequest.Priority PriorityValue
        {
            get => Priority_Get();
            set => Priority_Set(value);
        }
        
        private unsafe Swift.SwiftArray<Swift.Nuke.ISwiftImageProcessing> Processors_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_processors_Get_0221C105(self);
                
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
        private static extern IntPtr PInvoke_processors_Get_0221C105( SwiftSelf self);
        
        private unsafe void Processors_Set( Swift.SwiftArray<Swift.Nuke.ISwiftImageProcessing> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_processors_Set_61C810C8(valueBuffer, self);
                
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
        private static extern void PInvoke_processors_Set_61C810C8( IntPtr valueBuffer,  SwiftSelf self);
        
        public Swift.SwiftArray<Swift.Nuke.ISwiftImageProcessing> Processors
        {
            get => Processors_Get();
            set => Processors_Set(value);
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
                
                
                
                PInvoke_options_Get_46F4797F(swiftIndirectResult, self);
                
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
        private static extern void PInvoke_options_Get_46F4797F( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Options_Set( Swift.Nuke.ImageRequest.Options value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_options_Set_08121416(value.Payload, self);
                
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
        private static extern void PInvoke_options_Set_08121416( SafeHandle value,  SwiftSelf self);
        
        public Swift.Nuke.ImageRequest.Options OptionsValue
        {
            get => Options_Get();
            set => Options_Set(value);
        }
        
        private unsafe Swift.SwiftDictionary<Swift.Nuke.ImageRequest.UserInfoKey, Swift.Runtime.ExistentialContainer0> UserInfo_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_userInfo_Get_571CD692(self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.SwiftDictionary<Swift.Nuke.ImageRequest.UserInfoKey, Swift.Runtime.ExistentialContainer0>>(new IntPtr(&result));
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV8userInfoSDyAC04UserE3KeyVypGvg")]
        private static extern IntPtr PInvoke_userInfo_Get_571CD692( SwiftSelf self);
        
        private unsafe void UserInfo_Set( Swift.SwiftDictionary<Swift.Nuke.ImageRequest.UserInfoKey, Swift.Runtime.ExistentialContainer0> value)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using PayloadBuffer<IntPtr> valueDisposable = value.PayloadBuffer;
                IntPtr valueBuffer = valueDisposable.Buffer;
                
                PInvoke_userInfo_Set_5D0F2F4C(valueBuffer, self);
                
                return;
            }
            
            finally
            {
                if (success)
                   _payload.DangerousRelease();
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV8userInfoSDyAC04UserE3KeyVypGvs")]
        private static extern void PInvoke_userInfo_Set_5D0F2F4C( IntPtr valueBuffer,  SwiftSelf self);
        
        public Swift.SwiftDictionary<Swift.Nuke.ImageRequest.UserInfoKey, Swift.Runtime.ExistentialContainer0> UserInfo
        {
            get => UserInfo_Get();
            set => UserInfo_Set(value);
        }
        
        private unsafe Swift.SwiftOptional<Swift.URLRequest> UrlRequest_Get()
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_urlRequest_Get_1C740576(self);
                
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
        private static extern IntPtr PInvoke_urlRequest_Get_1C740576( SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_url_Get_42BFF800(self);
                
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
        private static extern IntPtr PInvoke_url_Get_42BFF800( SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_imageId_Get_7670F6CF(self);
                
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
        private static extern IntPtr PInvoke_imageId_Get_7670F6CF( SwiftSelf self);
        
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
                
                
                
                var result = PInvoke_description_Get_597FBE51(self);
                
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
        private static extern Swift.SwiftString.Buffer PInvoke_description_Get_597FBE51( SwiftSelf self);
        
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
            /// Gets the 'veryLow' case of Priority.
            /// </summary>
            public static Priority VeryLow
            {
                get
                {
                    var result = new Priority();
                    IntPtr casePtr = PInvoke_VeryLow();
                    result._payload = new SwiftSafeHandle<Priority>(casePtr);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV8PriorityO7veryLowyA2EmF")]
            private static extern IntPtr PInvoke_VeryLow();
            
            /// <summary>
            /// Gets the 'low' case of Priority.
            /// </summary>
            public static Priority Low
            {
                get
                {
                    var result = new Priority();
                    IntPtr casePtr = PInvoke_Low();
                    result._payload = new SwiftSafeHandle<Priority>(casePtr);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV8PriorityO3lowyA2EmF")]
            private static extern IntPtr PInvoke_Low();
            
            /// <summary>
            /// Gets the 'normal' case of Priority.
            /// </summary>
            public static Priority Normal
            {
                get
                {
                    var result = new Priority();
                    IntPtr casePtr = PInvoke_Normal();
                    result._payload = new SwiftSafeHandle<Priority>(casePtr);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV8PriorityO6normalyA2EmF")]
            private static extern IntPtr PInvoke_Normal();
            
            /// <summary>
            /// Gets the 'high' case of Priority.
            /// </summary>
            public static Priority High
            {
                get
                {
                    var result = new Priority();
                    IntPtr casePtr = PInvoke_High();
                    result._payload = new SwiftSafeHandle<Priority>(casePtr);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV8PriorityO4highyA2EmF")]
            private static extern IntPtr PInvoke_High();
            
            /// <summary>
            /// Gets the 'veryHigh' case of Priority.
            /// </summary>
            public static Priority VeryHigh
            {
                get
                {
                    var result = new Priority();
                    IntPtr casePtr = PInvoke_VeryHigh();
                    result._payload = new SwiftSafeHandle<Priority>(casePtr);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV8PriorityO8veryHighyA2EmF")]
            private static extern IntPtr PInvoke_VeryHigh();
            
            
            private unsafe System.IntPtr RawValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_rawValue_Get_7F70BEED(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV8PriorityO8rawValueSivg")]
            private static extern System.IntPtr PInvoke_rawValue_Get_7F70BEED( SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_rawValue_Get_4D6C2F64(self);
                    
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
            private static extern System.UInt16 PInvoke_rawValue_Get_4D6C2F64( SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_disableMemoryCacheReads_Get_3007F966(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV23disableMemoryCacheReadsAEvgZ")]
            private static extern void PInvoke_disableMemoryCacheReads_Get_3007F966( SwiftIndirectResult swiftIndirectResult);
            
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
                    
                    
                    
                    PInvoke_disableMemoryCacheWrites_Get_33BDA13B(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV24disableMemoryCacheWritesAEvgZ")]
            private static extern void PInvoke_disableMemoryCacheWrites_Get_33BDA13B( SwiftIndirectResult swiftIndirectResult);
            
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
                    
                    
                    
                    PInvoke_disableMemoryCache_Get_0E643A5C(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV18disableMemoryCacheAEvgZ")]
            private static extern void PInvoke_disableMemoryCache_Get_0E643A5C( SwiftIndirectResult swiftIndirectResult);
            
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
                    
                    
                    
                    PInvoke_disableDiskCacheReads_Get_0196B1C5(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV21disableDiskCacheReadsAEvgZ")]
            private static extern void PInvoke_disableDiskCacheReads_Get_0196B1C5( SwiftIndirectResult swiftIndirectResult);
            
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
                    
                    
                    
                    PInvoke_disableDiskCacheWrites_Get_37FC5DA3(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV22disableDiskCacheWritesAEvgZ")]
            private static extern void PInvoke_disableDiskCacheWrites_Get_37FC5DA3( SwiftIndirectResult swiftIndirectResult);
            
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
                    
                    
                    
                    PInvoke_disableDiskCache_Get_1FDAAB0C(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV16disableDiskCacheAEvgZ")]
            private static extern void PInvoke_disableDiskCache_Get_1FDAAB0C( SwiftIndirectResult swiftIndirectResult);
            
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
                    
                    
                    
                    PInvoke_reloadIgnoringCachedData_Get_53564691(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV24reloadIgnoringCachedDataAEvgZ")]
            private static extern void PInvoke_reloadIgnoringCachedData_Get_53564691( SwiftIndirectResult swiftIndirectResult);
            
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
                    
                    
                    
                    PInvoke_returnCacheDataDontLoad_Get_6709D4F8(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV23returnCacheDataDontLoadAEvgZ")]
            private static extern void PInvoke_returnCacheDataDontLoad_Get_6709D4F8( SwiftIndirectResult swiftIndirectResult);
            
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
                    
                    
                    
                    PInvoke_skipDecompression_Get_10D910C8(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV17skipDecompressionAEvgZ")]
            private static extern void PInvoke_skipDecompression_Get_10D910C8( SwiftIndirectResult swiftIndirectResult);
            
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
                    
                    
                    
                    PInvoke_skipDataLoadingQueue_Get_258BFB3C(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Options>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV20skipDataLoadingQueueAEvgZ")]
            private static extern void PInvoke_skipDataLoadingQueue_Get_258BFB3C( SwiftIndirectResult swiftIndirectResult);
            
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
                
                PInvoke_init_57A0B201(swiftIndirectResult, rawValue);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV7OptionsV8rawValueAEs6UInt16V_tcfC")]
            private static extern void PInvoke_init_57A0B201( SwiftIndirectResult swiftIndirectResult,  System.UInt16 rawValue);
            
            
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
                    
                    
                    
                    var result = PInvoke_rawValue_Get_157180BB(self);
                    
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
            private static extern Swift.SwiftString.Buffer PInvoke_rawValue_Get_157180BB( SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_imageIdKey_Get_3F71BF92(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.UserInfoKey>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV11UserInfoKeyV07imageIdF0AEvgZ")]
            private static extern void PInvoke_imageIdKey_Get_3F71BF92( SwiftIndirectResult swiftIndirectResult);
            
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
                    
                    
                    
                    PInvoke_scaleKey_Get_3EA497A8(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.UserInfoKey>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV11UserInfoKeyV05scaleF0AEvgZ")]
            private static extern void PInvoke_scaleKey_Get_3EA497A8( SwiftIndirectResult swiftIndirectResult);
            
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
                    
                    
                    
                    PInvoke_thumbnailKey_Get_440B8941(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.UserInfoKey>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV11UserInfoKeyV09thumbnailF0AEvgZ")]
            private static extern void PInvoke_thumbnailKey_Get_440B8941( SwiftIndirectResult swiftIndirectResult);
            
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
                    
                    
                    
                    var result = PInvoke_hashValue_Get_0B48A2CA(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_0B48A2CA( SwiftSelf self);
            
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
                PInvoke_init_446A61DA(swiftIndirectResult, arg0Disposable.Buffer);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV11UserInfoKeyVyAESScfC")]
            private static extern void PInvoke_init_446A61DA( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer arg0);
            
            
            public unsafe void Hash( Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_4CCEF92A(into.Payload, self);
                    
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
            private static extern void PInvoke_hash_4CCEF92A( SafeHandle into,  SwiftSelf self);
            
            
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
                    
                    
                    
                    var result = PInvoke_createThumbnailFromImageIfAbsent_Get_1518023E(self);
                    
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
            private static extern System.Boolean PInvoke_createThumbnailFromImageIfAbsent_Get_1518023E( SwiftSelf self);
            
            private unsafe void CreateThumbnailFromImageIfAbsent_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_createThumbnailFromImageIfAbsent_Set_31E22128(value, self);
                    
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
            private static extern void PInvoke_createThumbnailFromImageIfAbsent_Set_31E22128( System.Boolean value,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_createThumbnailFromImageAlways_Get_4421A131(self);
                    
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
            private static extern System.Boolean PInvoke_createThumbnailFromImageAlways_Get_4421A131( SwiftSelf self);
            
            private unsafe void CreateThumbnailFromImageAlways_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_createThumbnailFromImageAlways_Set_3E30D1C6(value, self);
                    
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
            private static extern void PInvoke_createThumbnailFromImageAlways_Set_3E30D1C6( System.Boolean value,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_createThumbnailWithTransform_Get_452B2301(self);
                    
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
            private static extern System.Boolean PInvoke_createThumbnailWithTransform_Get_452B2301( SwiftSelf self);
            
            private unsafe void CreateThumbnailWithTransform_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_createThumbnailWithTransform_Set_02DA5E20(value, self);
                    
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
            private static extern void PInvoke_createThumbnailWithTransform_Set_02DA5E20( System.Boolean value,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_shouldCacheImmediately_Get_28A22EF6(self);
                    
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
            private static extern System.Boolean PInvoke_shouldCacheImmediately_Get_28A22EF6( SwiftSelf self);
            
            private unsafe void ShouldCacheImmediately_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_shouldCacheImmediately_Set_26875B9D(value, self);
                    
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
            private static extern void PInvoke_shouldCacheImmediately_Set_26875B9D( System.Boolean value,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_hashValue_Get_7BEBF4CD(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_7BEBF4CD( SwiftSelf self);
            
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
                
                PInvoke_init_6DE59EDC(swiftIndirectResult, maxPixelSize);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV16ThumbnailOptionsV12maxPixelSizeAESf_tcfC")]
            private static extern void PInvoke_init_6DE59EDC( SwiftIndirectResult swiftIndirectResult,  System.Single maxPixelSize);
            
            
            
            public unsafe UIKit.UIImage? MakeThumbnail( Swift.Data with)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_makeThumbnail_08E28D21(with, self);
                    
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
            private static extern IntPtr PInvoke_makeThumbnail_08E28D21( Swift.Data with,  SwiftSelf self);
            
            
            public unsafe void Hash( Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_4B778536(into.Payload, self);
                    
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
            private static extern void PInvoke_hash_4B778536( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        public unsafe ImageRequest( string stringLiteral)
        {
            _payload = new SwiftSafeHandle<ImageRequest>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            using var stringLiteralSwift = new SwiftString(stringLiteral);
            using PayloadBuffer<SwiftString.Buffer> stringLiteralDisposable = stringLiteralSwift.PayloadBuffer;
            PInvoke_init_7F7AA38A(swiftIndirectResult, stringLiteralDisposable.Buffer);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV13stringLiteralACSS_tcfC")]
        private static extern void PInvoke_init_7F7AA38A( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer stringLiteral);
        
        
        public unsafe ImageRequest( Swift.URL? url,  IEnumerable<Swift.Nuke.ISwiftImageProcessing> processors,  Swift.Nuke.ImageRequest.Priority priority,  Swift.Nuke.ImageRequest.Options options,  Swift.SwiftDictionary<Swift.Nuke.ImageRequest.UserInfoKey, Swift.Runtime.ExistentialContainer0>? userInfo)
        {
            _payload = new SwiftSafeHandle<ImageRequest>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            using var urlSwift = url is {} urlValue ? SwiftOptional<Swift.URL>.NewSome(urlValue) : SwiftOptional<Swift.URL>.NewNone();
            using PayloadBuffer<IntPtr> urlDisposable = urlSwift.PayloadBuffer;
            IntPtr urlBuffer = urlDisposable.Buffer;
            using var processorsSwift = SwiftArray<Swift.Nuke.ISwiftImageProcessing>.FromEnumerable(processors);
            using PayloadBuffer<IntPtr> processorsDisposable = processorsSwift.PayloadBuffer;
            IntPtr processorsBuffer = processorsDisposable.Buffer;
            using var userInfoSwift = userInfo is {} userInfoValue ? SwiftOptional<Swift.SwiftDictionary<Swift.Nuke.ImageRequest.UserInfoKey, Swift.Runtime.ExistentialContainer0>>.NewSome(userInfoValue) : SwiftOptional<Swift.SwiftDictionary<Swift.Nuke.ImageRequest.UserInfoKey, Swift.Runtime.ExistentialContainer0>>.NewNone();
            using PayloadBuffer<IntPtr> userInfoDisposable = userInfoSwift.PayloadBuffer;
            IntPtr userInfoBuffer = userInfoDisposable.Buffer;
            PInvoke_init_2863713D(swiftIndirectResult, urlBuffer, processorsBuffer, priority.Payload, options.Payload, userInfoBuffer);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV3url10processors8priority7options8userInfoAC10Foundation3URLVSg_SayAA0B10Processing_pGAC8PriorityOAC7OptionsVSDyAC04UserI3KeyVypGSgtcfC")]
        private static extern void PInvoke_init_2863713D( SwiftIndirectResult swiftIndirectResult,  IntPtr urlBuffer,  IntPtr processorsBuffer,  SafeHandle priority,  SafeHandle options,  IntPtr userInfoBuffer);
        
        
        public unsafe ImageRequest( Swift.URLRequest urlRequest,  IEnumerable<Swift.Nuke.ISwiftImageProcessing> processors,  Swift.Nuke.ImageRequest.Priority priority,  Swift.Nuke.ImageRequest.Options options,  Swift.SwiftDictionary<Swift.Nuke.ImageRequest.UserInfoKey, Swift.Runtime.ExistentialContainer0>? userInfo)
        {
            _payload = new SwiftSafeHandle<ImageRequest>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            using var processorsSwift = SwiftArray<Swift.Nuke.ISwiftImageProcessing>.FromEnumerable(processors);
            using PayloadBuffer<IntPtr> processorsDisposable = processorsSwift.PayloadBuffer;
            IntPtr processorsBuffer = processorsDisposable.Buffer;
            using var userInfoSwift = userInfo is {} userInfoValue ? SwiftOptional<Swift.SwiftDictionary<Swift.Nuke.ImageRequest.UserInfoKey, Swift.Runtime.ExistentialContainer0>>.NewSome(userInfoValue) : SwiftOptional<Swift.SwiftDictionary<Swift.Nuke.ImageRequest.UserInfoKey, Swift.Runtime.ExistentialContainer0>>.NewNone();
            using PayloadBuffer<IntPtr> userInfoDisposable = userInfoSwift.PayloadBuffer;
            IntPtr userInfoBuffer = userInfoDisposable.Buffer;
            PInvoke_init_0678399C(swiftIndirectResult, urlRequest.Payload, processorsBuffer, priority.Payload, options.Payload, userInfoBuffer);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke12ImageRequestV03urlC010processors8priority7options8userInfoAC10Foundation10URLRequestV_SayAA0B10Processing_pGAC8PriorityOAC7OptionsVSDyAC04UserI3KeyVypGSgtcfC")]
        private static extern void PInvoke_init_0678399C( SwiftIndirectResult swiftIndirectResult,  SafeHandle urlRequest,  IntPtr processorsBuffer,  SafeHandle priority,  SafeHandle options,  IntPtr userInfoBuffer);
        
        
        
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
                    
                    
                    
                    var result = PInvoke_isProgressive_Get_5CF38191(self);
                    
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
            private static extern System.Boolean PInvoke_isProgressive_Get_5CF38191( SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_isAsynchronous_Get_1E9102C2(self);
                    
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
            private static extern System.Boolean PInvoke_isAsynchronous_Get_1E9102C2( SwiftSelf self);
            
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
                PInvoke_init_1E785915(swiftIndirectResult, assetTypeBuffer, isProgressive);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO5EmptyV9assetType13isProgressiveAeA05AssetF0VSg_SbtcfC")]
            private static extern void PInvoke_init_1E785915( SwiftIndirectResult swiftIndirectResult,  IntPtr assetTypeBuffer,  System.Boolean isProgressive);
            
            
            public unsafe Swift.Nuke.ImageContainer Decode( Swift.Data arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageContainer>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_decode_23387A3D(swiftIndirectResult, arg0, self, out var error);
                    
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
            private static extern void PInvoke_decode_23387A3D( SwiftIndirectResult swiftIndirectResult,  Swift.Data arg0,  SwiftSelf self, out SwiftError error);
            
            
            public unsafe Swift.Nuke.ImageContainer? DecodePartiallyDownloadedData( Swift.Data arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_decodePartiallyDownloadedData_612AEE17(arg0, self);
                    
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
            private static extern IntPtr PInvoke_decodePartiallyDownloadedData_612AEE17( Swift.Data arg0,  SwiftSelf self);
            
            
        }
        
        
        public unsafe class Default : ISwiftObject
        {
            private unsafe System.Boolean IsAsynchronous_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_isAsynchronous_Get_327F55D6(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO7DefaultC14isAsynchronousSbvg")]
            private static extern System.Boolean PInvoke_isAsynchronous_Get_327F55D6( SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_init_5D1CEBD5(swiftIndirectResult);
                    
                    return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageDecoders.Default>(new IntPtr(swiftIndirectResult.Value));
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO7DefaultCAEycfC")]
            private static extern void PInvoke_init_5D1CEBD5( SwiftIndirectResult swiftIndirectResult);
            
            
            public unsafe Swift.Nuke.ImageDecoders.Default? Init( Swift.Nuke.ImageDecodingContext context)
            {
                try
                {
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageDecoders.Default?>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_init_2AC61A0A(swiftIndirectResult, context.Payload);
                    
                    var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Nuke.ImageDecoders.Default>>(new IntPtr(swiftIndirectResult.Value));
                    return swiftResult.ToNullable();
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO7DefaultC7contextAESgAA0B15DecodingContextV_tcfC")]
            private static extern void PInvoke_init_2AC61A0A( SwiftIndirectResult swiftIndirectResult,  SafeHandle context);
            
            
            public unsafe Swift.Nuke.ImageContainer Decode( Swift.Data arg0)
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageContainer>();
                    var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                    var swiftIndirectResult = new SwiftIndirectResult(payload);
                    
                    
                    
                    PInvoke_decode_6E8525AD(swiftIndirectResult, arg0, self, out var error);
                    
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
            private static extern void PInvoke_decode_6E8525AD( SwiftIndirectResult swiftIndirectResult,  Swift.Data arg0,  SwiftSelf self, out SwiftError error);
            
            
            public unsafe Swift.Nuke.ImageContainer? DecodePartiallyDownloadedData( Swift.Data arg0)
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_decodePartiallyDownloadedData_61773F92(arg0, self);
                    
                    var swiftResult = SwiftMarshal.MarshalFromSwift<Swift.SwiftOptional<Swift.Nuke.ImageContainer>>(new IntPtr(&result));
                    return swiftResult.ToNullable();
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageDecodersO7DefaultC29decodePartiallyDownloadedDatayAA0B9ContainerVSg10Foundation0H0VF")]
            private static extern IntPtr PInvoke_decodePartiallyDownloadedData_61773F92( Swift.Data arg0,  SwiftSelf self);
            
            
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
                
                
                
                var result = PInvoke_rawValue_Get_2BB663A6(self);
                
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
        private static extern Swift.SwiftString.Buffer PInvoke_rawValue_Get_2BB663A6( SwiftSelf self);
        
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
                
                
                
                PInvoke_png_Get_63F0731C(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV3pngACvgZ")]
        private static extern void PInvoke_png_Get_63F0731C( SwiftIndirectResult swiftIndirectResult);
        
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
                
                
                
                PInvoke_jpeg_Get_5DA67E60(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV4jpegACvgZ")]
        private static extern void PInvoke_jpeg_Get_5DA67E60( SwiftIndirectResult swiftIndirectResult);
        
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
                
                
                
                PInvoke_gif_Get_1864DAA8(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV3gifACvgZ")]
        private static extern void PInvoke_gif_Get_1864DAA8( SwiftIndirectResult swiftIndirectResult);
        
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
                
                
                
                PInvoke_heic_Get_700EE3BC(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV4heicACvgZ")]
        private static extern void PInvoke_heic_Get_700EE3BC( SwiftIndirectResult swiftIndirectResult);
        
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
                
                
                
                PInvoke_webp_Get_1EAEBCA3(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV4webpACvgZ")]
        private static extern void PInvoke_webp_Get_1EAEBCA3( SwiftIndirectResult swiftIndirectResult);
        
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
                
                
                
                PInvoke_mp4_Get_4F3BE811(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV3mp4ACvgZ")]
        private static extern void PInvoke_mp4_Get_4F3BE811( SwiftIndirectResult swiftIndirectResult);
        
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
                
                
                
                PInvoke_m4v_Get_67DDD370(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV3m4vACvgZ")]
        private static extern void PInvoke_m4v_Get_67DDD370( SwiftIndirectResult swiftIndirectResult);
        
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
                
                
                
                PInvoke_mov_Get_3B84B507(swiftIndirectResult);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.AssetType>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV3movACvgZ")]
        private static extern void PInvoke_mov_Get_3B84B507( SwiftIndirectResult swiftIndirectResult);
        
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
                
                
                
                var result = PInvoke_hashValue_Get_27878C49(self);
                
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
        private static extern System.IntPtr PInvoke_hashValue_Get_27878C49( SwiftSelf self);
        
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
            PInvoke_init_07672229(swiftIndirectResult, rawValueDisposable.Buffer);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeV8rawValueACSS_tcfC")]
        private static extern void PInvoke_init_07672229( SwiftIndirectResult swiftIndirectResult,  Swift.SwiftString.Buffer rawValue);
        
        
        public unsafe void Hash( Swift.Hasher into)
        {
            var success = false;
            _payload.DangerousAddRef(ref success);
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_hash_4D23F31A(into.Payload, self);
                
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
        private static extern void PInvoke_hash_4D23F31A( SafeHandle into,  SwiftSelf self);
        
        
        public unsafe AssetType( Swift.Data arg0)
        {
            _payload = new SwiftSafeHandle<AssetType>((IntPtr)NativeMemory.Alloc(_payloadSize));
            var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
            
            PInvoke_init_255F0DFE(swiftIndirectResult, arg0);
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9AssetTypeVyACSg10Foundation4DataVcfC")]
        private static extern void PInvoke_init_255F0DFE( SwiftIndirectResult swiftIndirectResult,  Swift.Data arg0);
        
        
    }
    
    
    public unsafe class ImageTask : ISwiftObject, IEquatable<ImageTask>
    {
        private unsafe System.Int64 TaskId_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_taskId_Get_5EC11053(self);
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC6taskIds5Int64Vvg")]
        private static extern System.Int64 PInvoke_taskId_Get_5EC11053( SwiftSelf self);
        
        public System.Int64 TaskId
        {
            get => TaskId_Get();
        }
        
        private unsafe Swift.Nuke.ImageRequest Request_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_request_Get_0014EBC4(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC7requestAA0B7RequestVvg")]
        private static extern void PInvoke_request_Get_0014EBC4( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Swift.Nuke.ImageRequest Request
        {
            get => Request_Get();
        }
        
        private unsafe Swift.Nuke.ImageRequest.Priority Priority_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Priority>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_priority_Get_0AB46541(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Priority>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC8priorityAA0B7RequestV8PriorityOvg")]
        private static extern void PInvoke_priority_Get_0AB46541( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Priority_Set( Swift.Nuke.ImageRequest.Priority value)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_priority_Set_5B4F4B08(value.Payload, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC8priorityAA0B7RequestV8PriorityOvs")]
        private static extern void PInvoke_priority_Set_5B4F4B08( SafeHandle value,  SwiftSelf self);
        
        public Swift.Nuke.ImageRequest.Priority Priority
        {
            get => Priority_Get();
            set => Priority_Set(value);
        }
        
        private unsafe Swift.Nuke.ImageTask.Progress CurrentProgress_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageTask.Progress>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_currentProgress_Get_6ABC5A6F(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageTask.Progress>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC15currentProgressAC0E0Vvg")]
        private static extern void PInvoke_currentProgress_Get_6ABC5A6F( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Swift.Nuke.ImageTask.Progress CurrentProgress
        {
            get => CurrentProgress_Get();
        }
        
        private unsafe Swift.Nuke.ImageTask.State State_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageTask.State>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_state_Get_38C4AF01(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageTask.State>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5stateAC5StateOvg")]
        private static extern void PInvoke_state_Get_38C4AF01( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Swift.Nuke.ImageTask.State StateValue
        {
            get => State_Get();
        }
        
        private unsafe UIKit.UIImage Image_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<UIKit.UIImage>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_image_Get_13E84D6A(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<UIKit.UIImage>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5imageSo7UIImageCvg")]
        private static extern void PInvoke_image_Get_13E84D6A( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public UIKit.UIImage Image
        {
            get => Image_Get();
        }
        
        private unsafe Swift.Nuke.ImageResponse Response_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageResponse>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_response_Get_4B1E1B1B(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageResponse>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC8responseAA0B8ResponseVvg")]
        private static extern void PInvoke_response_Get_4B1E1B1B( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        public Swift.Nuke.ImageResponse Response
        {
            get => Response_Get();
        }
        
        private unsafe Swift.SwiftString Description_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_description_Get_5AA2FAD4(self);
                
                unsafe {
    return SwiftMarshal.MarshalFromSwift<Swift.SwiftString>(new IntPtr(&result));
}
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC11descriptionSSvg")]
        private static extern Swift.SwiftString.Buffer PInvoke_description_Get_5AA2FAD4( SwiftSelf self);
        
        public Swift.SwiftString Description
        {
            get => Description_Get();
        }
        
        private unsafe System.IntPtr HashValue_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_hashValue_Get_4D0223A9(self);
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC9hashValueSivg")]
        private static extern System.IntPtr PInvoke_hashValue_Get_4D0223A9( SwiftSelf self);
        
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
                    
                    
                    
                    var result = PInvoke_completed_Get_582395BD(self);
                    
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
            private static extern System.Int64 PInvoke_completed_Get_582395BD( SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_total_Get_077845FA(self);
                    
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
            private static extern System.Int64 PInvoke_total_Get_077845FA( SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_fraction_Get_54DB0572(self);
                    
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
            private static extern System.Single PInvoke_fraction_Get_54DB0572( SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_hashValue_Get_0226DF1F(self);
                    
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
            private static extern System.IntPtr PInvoke_hashValue_Get_0226DF1F( SwiftSelf self);
            
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
                
                PInvoke_init_09E97B37(swiftIndirectResult, completed, total);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC8ProgressV9completed5totalAEs5Int64V_AItcfC")]
            private static extern void PInvoke_init_09E97B37( SwiftIndirectResult swiftIndirectResult,  System.Int64 completed,  System.Int64 total);
            
            
            public unsafe void Hash( Swift.Hasher into)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_hash_16CD2149(into.Payload, self);
                    
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
            private static extern void PInvoke_hash_16CD2149( SafeHandle into,  SwiftSelf self);
            
            
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
                    IntPtr casePtr = PInvoke_Running();
                    result._payload = new SwiftSafeHandle<State>(casePtr);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5StateO7runningyA2EmF")]
            private static extern IntPtr PInvoke_Running();
            
            /// <summary>
            /// Gets the 'cancelled' case of State.
            /// </summary>
            public static State Cancelled
            {
                get
                {
                    var result = new State();
                    IntPtr casePtr = PInvoke_Cancelled();
                    result._payload = new SwiftSafeHandle<State>(casePtr);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5StateO9cancelledyA2EmF")]
            private static extern IntPtr PInvoke_Cancelled();
            
            /// <summary>
            /// Gets the 'completed' case of State.
            /// </summary>
            public static State Completed
            {
                get
                {
                    var result = new State();
                    IntPtr casePtr = PInvoke_Completed();
                    result._payload = new SwiftSafeHandle<State>(casePtr);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5StateO9completedyA2EmF")]
            private static extern IntPtr PInvoke_Completed();
            
            
            private unsafe System.IntPtr HashValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_1CCBEA32(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5StateO9hashValueSivg")]
            private static extern System.IntPtr PInvoke_hashValue_Get_1CCBEA32( SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_hash_0EA29456(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5StateO4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_0EA29456( SafeHandle into,  SwiftSelf self);
            
            
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
                IntPtr casePtr = PInvoke_Progress(value0);
                result._payload = new SwiftSafeHandle<Event>(casePtr);
                return result;
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5EventO8progressyAeC8ProgressVcAEmF")]
            private static extern IntPtr PInvoke_Progress(Swift.Nuke.ImageTask.Progress value0);
            
            /// <summary>
            /// Creates the 'preview' case of Event.
            /// </summary>
            public static Event Preview(Swift.Nuke.ImageResponse value0)
            {
                var result = new Event();
                IntPtr casePtr = PInvoke_Preview(value0);
                result._payload = new SwiftSafeHandle<Event>(casePtr);
                return result;
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5EventO7previewyAeA0B8ResponseVcAEmF")]
            private static extern IntPtr PInvoke_Preview(Swift.Nuke.ImageResponse value0);
            
            /// <summary>
            /// Gets the 'cancelled' case of Event.
            /// </summary>
            public static Event Cancelled
            {
                get
                {
                    var result = new Event();
                    IntPtr casePtr = PInvoke_Cancelled();
                    result._payload = new SwiftSafeHandle<Event>(casePtr);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5EventO9cancelledyA2EmF")]
            private static extern IntPtr PInvoke_Cancelled();
            
            /// <summary>
            /// Creates the 'finished' case of Event.
            /// </summary>
            public static Event Finished(Swift.SwiftResult<Swift.Nuke.ImageResponse, Swift.Nuke.ImagePipeline.Error> value0)
            {
                var result = new Event();
                IntPtr casePtr = PInvoke_Finished(value0.Payload.DangerousGetHandle());
                result._payload = new SwiftSafeHandle<Event>(casePtr);
                return result;
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC5EventO8finishedyAEs6ResultOyAA0B8ResponseVAA0B8PipelineC5ErrorOGcAEmF")]
            private static extern IntPtr PInvoke_Finished(IntPtr value0);
            
            
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
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_cancel_22FA2F58(self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC6cancelyyF")]
        private static extern void PInvoke_cancel_22FA2F58( SwiftSelf self);
        
        
        public unsafe void Hash( Swift.Hasher into)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_hash_17374707(into.Payload, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke9ImageTaskC4hash4intoys6HasherVz_tF")]
        private static extern void PInvoke_hash_17374707( SafeHandle into,  SwiftSelf self);
        
        
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
        public static ImageDecodingError Unknown
        {
            get
            {
                var result = new ImageDecodingError();
                IntPtr casePtr = PInvoke_Unknown();
                result._payload = new SwiftSafeHandle<ImageDecodingError>(casePtr);
                return result;
            }
        }
        
        [DllImport("Nuke", EntryPoint = "$s4Nuke18ImageDecodingErrorO7unknownyA2CmF")]
        private static extern IntPtr PInvoke_Unknown();
        
        
        private unsafe Swift.SwiftString Description_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_description_Get_3F0E0B9D(self);
                
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
        private static extern Swift.SwiftString.Buffer PInvoke_description_Get_3F0E0B9D( SwiftSelf self);
        
        public Swift.SwiftString Description
        {
            get => Description_Get();
        }
        
        private unsafe System.IntPtr HashValue_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_hashValue_Get_33A6AAE7(self);
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke18ImageDecodingErrorO9hashValueSivg")]
        private static extern System.IntPtr PInvoke_hashValue_Get_33A6AAE7( SwiftSelf self);
        
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
                
                
                
                PInvoke_hash_5367D585(into.Payload, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke18ImageDecodingErrorO4hash4intoys6HasherVz_tF")]
        private static extern void PInvoke_hash_5367D585( SafeHandle into,  SwiftSelf self);
        
        
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
                    
                    
                    
                    PInvoke_type_Get_66735035(swiftIndirectResult, self);
                    
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
            private static extern void PInvoke_type_Get_66735035( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_compressionRatio_Get_4ECC5B66(self);
                    
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
            private static extern System.Single PInvoke_compressionRatio_Get_4ECC5B66( SwiftSelf self);
            
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
                
                PInvoke_init_01671006(swiftIndirectResult, type.Payload, compressionRatio);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageEncodersO0B2IOV4type16compressionRatioAeA9AssetTypeV_SftcfC")]
            private static extern void PInvoke_init_01671006( SwiftIndirectResult swiftIndirectResult,  SafeHandle type,  System.Single compressionRatio);
            
            
            public static System.Boolean IsSupported( Swift.Nuke.AssetType type)
            {
                try
                {
                    
                    
                    var result = PInvoke_isSupported_281B9D65(type.Payload);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageEncodersO0B2IOV11isSupported4typeSbAA9AssetTypeV_tFZ")]
            private static extern System.Boolean PInvoke_isSupported_281B9D65( SafeHandle type);
            
            
            public unsafe Swift.Data? Encode( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_encode_340C3358(arg0Handle, self);
                    
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
            private static extern IntPtr PInvoke_encode_340C3358( IntPtr arg0,  SwiftSelf self);
            
            
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
                    
                    
                    
                    var result = PInvoke_compressionQuality_Get_502136DF(self);
                    
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
            private static extern System.Single PInvoke_compressionQuality_Get_502136DF( SwiftSelf self);
            
            private unsafe void CompressionQuality_Set( System.Single value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_compressionQuality_Set_7EEC025F(value, self);
                    
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
            private static extern void PInvoke_compressionQuality_Set_7EEC025F( System.Single value,  SwiftSelf self);
            
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
                    
                    
                    
                    var result = PInvoke_isHEIFPreferred_Get_2A002944(self);
                    
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
            private static extern System.Boolean PInvoke_isHEIFPreferred_Get_2A002944( SwiftSelf self);
            
            private unsafe void IsHEIFPreferred_Set( System.Boolean value)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    PInvoke_isHEIFPreferred_Set_3AA74CB2(value, self);
                    
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
            private static extern void PInvoke_isHEIFPreferred_Set_3AA74CB2( System.Boolean value,  SwiftSelf self);
            
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
                
                PInvoke_init_33B614F2(swiftIndirectResult, compressionQuality);
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke13ImageEncodersO7DefaultV18compressionQualityAESf_tcfC")]
            private static extern void PInvoke_init_33B614F2( SwiftIndirectResult swiftIndirectResult,  System.Single compressionQuality);
            
            
            public unsafe Swift.Data? Encode( UIKit.UIImage arg0)
            {
                var success = false;
                _payload.DangerousAddRef(ref success);
                IntPtr arg0Handle = arg0?.Handle ?? IntPtr.Zero;
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_encode_561C1DB4(arg0Handle, self);
                    
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
            private static extern IntPtr PInvoke_encode_561C1DB4( IntPtr arg0,  SwiftSelf self);
            
            
        }
        
        
    }
    
    
    public unsafe class ImagePrefetcher : ISwiftObject
    {
        private unsafe System.Boolean IsPaused_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                var result = PInvoke_isPaused_Get_7E5B32DF(self);
                
                return result;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC8isPausedSbvg")]
        private static extern System.Boolean PInvoke_isPaused_Get_7E5B32DF( SwiftSelf self);
        
        private unsafe void IsPaused_Set( System.Boolean value)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_isPaused_Set_3C2EB59F(value, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC8isPausedSbvs")]
        private static extern void PInvoke_isPaused_Set_3C2EB59F( System.Boolean value,  SwiftSelf self);
        
        public System.Boolean IsPaused
        {
            get => IsPaused_Get();
            set => IsPaused_Set(value);
        }
        
        private unsafe Swift.Nuke.ImageRequest.Priority Priority_Get()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImageRequest.Priority>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_priority_Get_55C2D948(swiftIndirectResult, self);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImageRequest.Priority>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC8priorityAA0B7RequestV8PriorityOvg")]
        private static extern void PInvoke_priority_Get_55C2D948( SwiftIndirectResult swiftIndirectResult,  SwiftSelf self);
        
        private unsafe void Priority_Set( Swift.Nuke.ImageRequest.Priority value)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_priority_Set_3D06EF48(value.Payload, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC8priorityAA0B7RequestV8PriorityOvs")]
        private static extern void PInvoke_priority_Set_3D06EF48( SafeHandle value,  SwiftSelf self);
        
        public Swift.Nuke.ImageRequest.Priority Priority
        {
            get => Priority_Get();
            set => Priority_Set(value);
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
                    IntPtr casePtr = PInvoke_MemoryCache();
                    result._payload = new SwiftSafeHandle<Destination>(casePtr);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC11DestinationO11memoryCacheyA2EmF")]
            private static extern IntPtr PInvoke_MemoryCache();
            
            /// <summary>
            /// Gets the 'diskCache' case of Destination.
            /// </summary>
            public static Destination DiskCache
            {
                get
                {
                    var result = new Destination();
                    IntPtr casePtr = PInvoke_DiskCache();
                    result._payload = new SwiftSafeHandle<Destination>(casePtr);
                    return result;
                }
            }
            
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC11DestinationO9diskCacheyA2EmF")]
            private static extern IntPtr PInvoke_DiskCache();
            
            
            private unsafe System.IntPtr HashValue_Get()
            {
                try
                {
                    var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                    
                    
                    
                    var result = PInvoke_hashValue_Get_4E844F1B(self);
                    
                    return result;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC11DestinationO9hashValueSivg")]
            private static extern System.IntPtr PInvoke_hashValue_Get_4E844F1B( SwiftSelf self);
            
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
                    
                    
                    
                    PInvoke_hash_3E0DCA9B(into.Payload, self);
                    
                    return;
                }
                
                finally
                {
                }
                
            }
            
            [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
            [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC11DestinationO4hash4intoys6HasherVz_tF")]
            private static extern void PInvoke_hash_3E0DCA9B( SafeHandle into,  SwiftSelf self);
            
            
        }
        
        
        public unsafe Swift.Nuke.ImagePrefetcher Init( Swift.Nuke.ImagePipeline pipeline,  Swift.Nuke.ImagePrefetcher.Destination destination,  System.IntPtr maxConcurrentRequestCount)
        {
            try
            {
                var returnMetadata = TypeMetadata.GetTypeMetadataOrThrow<Swift.Nuke.ImagePrefetcher>();
                var payload = NativeMemory.Alloc((nuint)returnMetadata.Size);
                var swiftIndirectResult = new SwiftIndirectResult(payload);
                
                
                
                PInvoke_init_44BAC175(swiftIndirectResult, pipeline.Payload, destination.Payload, maxConcurrentRequestCount);
                
                return SwiftMarshal.MarshalFromSwift<Swift.Nuke.ImagePrefetcher>(new IntPtr(swiftIndirectResult.Value));
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC8pipeline11destination25maxConcurrentRequestCountAcA0B8PipelineC_AC11DestinationOSitcfC")]
        private static extern void PInvoke_init_44BAC175( SwiftIndirectResult swiftIndirectResult,  SafeHandle pipeline,  SafeHandle destination,  System.IntPtr maxConcurrentRequestCount);
        
        
        public unsafe void StartPrefetching( IEnumerable<Swift.URL> with)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using var withSwift = SwiftArray<Swift.URL>.FromEnumerable(with);
                using PayloadBuffer<IntPtr> withDisposable = withSwift.PayloadBuffer;
                IntPtr withBuffer = withDisposable.Buffer;
                
                PInvoke_startPrefetching_51487FDE(withBuffer, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC16startPrefetching4withySay10Foundation3URLVG_tF")]
        private static extern void PInvoke_startPrefetching_51487FDE( IntPtr withBuffer,  SwiftSelf self);
        
        
        public unsafe void _startPrefetching( IEnumerable<Swift.Nuke.ImageRequest> with)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using var withSwift = SwiftArray<Swift.Nuke.ImageRequest>.FromEnumerable(with);
                using PayloadBuffer<IntPtr> withDisposable = withSwift.PayloadBuffer;
                IntPtr withBuffer = withDisposable.Buffer;
                
                PInvoke__startPrefetching_34F21259(withBuffer, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC17_startPrefetching4withySayAA0B7RequestVG_tF")]
        private static extern void PInvoke__startPrefetching_34F21259( IntPtr withBuffer,  SwiftSelf self);
        
        
        public unsafe void StopPrefetching( IEnumerable<Swift.URL> with)
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                using var withSwift = SwiftArray<Swift.URL>.FromEnumerable(with);
                using PayloadBuffer<IntPtr> withDisposable = withSwift.PayloadBuffer;
                IntPtr withBuffer = withDisposable.Buffer;
                
                PInvoke_stopPrefetching_1EFDBC44(withBuffer, self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC15stopPrefetching4withySay10Foundation3URLVG_tF")]
        private static extern void PInvoke_stopPrefetching_1EFDBC44( IntPtr withBuffer,  SwiftSelf self);
        
        
        public unsafe void StopPrefetching()
        {
            try
            {
                var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
                
                
                
                PInvoke_stopPrefetching_62C2126E(self);
                
                return;
            }
            
            finally
            {
            }
            
        }
        
        [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
        [DllImport("Nuke", EntryPoint = "$s4Nuke15ImagePrefetcherC15stopPrefetchingyyF")]
        private static extern void PInvoke_stopPrefetching_62C2126E( SwiftSelf self);
        
        
    }
    
    
}
