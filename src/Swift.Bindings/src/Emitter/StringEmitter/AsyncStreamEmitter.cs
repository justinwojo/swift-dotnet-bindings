// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Provides methods for emitting AsyncStream-related code, including callback functions
/// and Swift wrappers for async iteration.
/// </summary>
public static class AsyncStreamEmitter
{
    /// <summary>
    /// Emits the [UnmanagedCallersOnly] element callback for an AsyncStream property.
    /// This callback receives elements from Swift and writes them to the SwiftAsyncStream channel.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <param name="asyncStreamHandler">The AsyncStream handler.</param>
    /// <param name="callbackName">The callback function name.</param>
    public static void EmitElementCallback(
        CSharpWriter csWriter,
        PropertyDecl propertyDecl,
        AsyncStreamHandler asyncStreamHandler,
        string callbackName)
    {
        var elementType = asyncStreamHandler.GetCSharpElementType(propertyDecl.SwiftTypeSpec);

        csWriter.WriteLines($$"""
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
            private static unsafe byte {{callbackName}}_OnElement(void* elementPtr, long context)
            {
                var stream = SwiftAsyncStream<{{elementType}}>.FromContext(context);
                if (stream == null) return 0;

                var element = SwiftMarshal.MarshalFromSwift<{{elementType}}>(new IntPtr(elementPtr));
                return stream.GetElementCallback()(new IntPtr(elementPtr), context) ? (byte)1 : (byte)0;
            }
            """);
    }

    /// <summary>
    /// Emits the [UnmanagedCallersOnly] completion callback for an AsyncStream property.
    /// This callback signals when the Swift stream has completed.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="callbackName">The callback function name.</param>
    public static void EmitCompletionCallback(
        CSharpWriter csWriter,
        string callbackName)
    {
        csWriter.WriteLines($$"""
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
            private static void {{callbackName}}_OnComplete(long context)
            {
                // Stream completion is handled by the SwiftAsyncStream instance
            }
            """);
    }

    /// <summary>
    /// Emits the P/Invoke declaration for the Swift wrapper function.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="swiftWrapperName">The Swift wrapper function name.</param>
    /// <param name="libraryPath">The library path.</param>
    /// <param name="isStatic">Whether the property is static.</param>
    public static void EmitPInvokeDeclaration(
        CSharpWriter csWriter,
        string swiftWrapperName,
        string libraryPath,
        bool isStatic)
    {
        var selfParam = isStatic ? "" : "void* self, ";

        csWriter.WriteLines($$"""
            [DllImport("{{libraryPath}}", EntryPoint = "{{swiftWrapperName}}")]
            private static extern unsafe void PInvoke_{{swiftWrapperName}}(
                {{selfParam}}delegate* unmanaged[Cdecl]<void*, long, byte> elementCallback,
                delegate* unmanaged[Cdecl]<long, void> completionCallback,
                long context);
            """);
    }

    /// <summary>
    /// Emits the Swift wrapper function that iterates the AsyncStream and calls callbacks.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer.</param>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <param name="asyncStreamHandler">The AsyncStream handler.</param>
    /// <param name="swiftWrapperName">The Swift wrapper function name.</param>
    /// <param name="parentTypeName">The parent Swift type name.</param>
    public static void EmitSwiftWrapper(
        SwiftWriter swiftWriter,
        PropertyDecl propertyDecl,
        AsyncStreamHandler asyncStreamHandler,
        string swiftWrapperName,
        string parentTypeName)
    {
        var isStatic = propertyDecl.IsStatic;
        var selfParam = isStatic ? "" : "_ self: " + parentTypeName + ", ";
        var selfAccess = isStatic ? parentTypeName : "self";

        swiftWriter.WriteLines($$"""
            @_silgen_name("{{swiftWrapperName}}")
            public func {{swiftWrapperName}}(
                {{selfParam}}elementCallback: @escaping @convention(c) (UnsafeRawPointer, Int64) -> Bool,
                completionCallback: @escaping @convention(c) (Int64) -> Void,
                context: Int64
            ) {
                Task {
                    for await element in {{selfAccess}}.{{propertyDecl.Name}} {
                        let shouldContinue = withUnsafePointer(to: element) { ptr in
                            elementCallback(UnsafeRawPointer(ptr), context)
                        }
                        if !shouldContinue { break }
                    }
                    completionCallback(context)
                }
            }
            """);
    }

    /// <summary>
    /// Emits the C# property getter that returns IAsyncEnumerable.
    /// </summary>
    /// <param name="csWriter">The C# writer.</param>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <param name="asyncStreamHandler">The AsyncStream handler.</param>
    /// <param name="swiftWrapperName">The Swift wrapper function name.</param>
    /// <param name="callbackName">The callback function name prefix.</param>
    /// <param name="siblingNestedTypeNames">Optional set of nested type names for collision detection.</param>
    /// <param name="containingTypeName">Optional containing type name for collision detection (CS0542).</param>
    public static void EmitPropertyGetter(
        CSharpWriter csWriter,
        PropertyDecl propertyDecl,
        AsyncStreamHandler asyncStreamHandler,
        string swiftWrapperName,
        string callbackName,
        IReadOnlySet<string>? siblingNestedTypeNames = null,
        string? containingTypeName = null)
    {
        var elementType = asyncStreamHandler.GetCSharpElementType(propertyDecl.SwiftTypeSpec);
        var asyncEnumerableType = $"IAsyncEnumerable<{elementType}>";
        var isStatic = propertyDecl.IsStatic;
        var staticModifier = isStatic ? "static " : "";
        var propertyName = NameProvider.GetPropertyName(propertyDecl.Name, siblingNestedTypeNames, containingTypeName);
        var selfArg = isStatic ? "" : "(void*)_payload.DangerousGetHandle(), ";

        csWriter.WriteLines($$"""
            public {{staticModifier}}{{asyncEnumerableType}} {{propertyName}}
            {
                get
                {
                    unsafe
                    {
                        var stream = new SwiftAsyncStream<{{elementType}}>();
                        PInvoke_{{swiftWrapperName}}(
                            {{selfArg}}&{{callbackName}}_OnElement,
                            &{{callbackName}}_OnComplete,
                            stream.GetContext());
                        return stream;
                    }
                }
            }
            """);
    }
}
