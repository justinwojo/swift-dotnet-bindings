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
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
            private static unsafe byte {{callbackName}}_OnElement(void* elementPtr, long context)
            {
                var stream = SwiftAsyncStream<{{elementType}}>.FromContext(context);
                if (stream == null) return 0;

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
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
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
            [LibraryImport("{{libraryPath}}", EntryPoint = "{{swiftWrapperName}}")]
            private static unsafe partial void PInvoke_{{swiftWrapperName}}(
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
        var selfParam = isStatic ? "" : "_ self_: UnsafeMutableRawPointer, ";
        var selfAccess = isStatic ? parentTypeName : "__self";
        // Custom actor AsyncStream properties are supported: `await __self.prop` hops to the
        // actor's serial executor (whether the property is actor-isolated or nonisolated), and
        // the resulting AsyncStream iterates without further isolation because it is Sendable.
        // The gate in PropertyHandler + MemberEmissionValidator still rejects parameterized-protocol
        // element types (iOS 16+ spelling at the @_cdecl level).
        bool isOnCustomActor = (propertyDecl.ParentDecl as ClassDecl)?.IsActor == true;
        // @MainActor properties need Task { @MainActor in } and await to access the
        // actor-isolated member. Unmanaged<T> strips isolation from the reference, so
        // even inside a @MainActor context, the compiler requires explicit `await`.
        bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
            propertyDecl.ParentDecl, propertyDecl.IsMainActorIsolated);
        // Custom actor isolated properties and @MainActor properties need `await` for actor-isolated
        // access. `nonisolated` actor properties are synchronous from any context — adding `await`
        // there produces a "no async operations" Swift warning.
        bool needsActorAwait = (isOnCustomActor && !propertyDecl.IsNonisolated) || needsMainActor;
        var awaitPrefix = needsActorAwait ? "await " : "";
        var taskOpen = needsMainActor ? "Task { @MainActor in" : "Task {";

        // Self reconstruction for instance properties
        var selfReconstruction = "";
        if (!isStatic)
        {
            selfReconstruction = $"    let __self = Unmanaged<{parentTypeName}>.fromOpaque(self_).takeUnretainedValue()\n";
        }

        // Emit availability annotations from the member and ancestor chain.
        // @_cdecl wrappers are top-level functions and don't inherit enclosing type availability.
        var availability = WrapperEmitterHelpers.MergeAvailability(propertyDecl.AvailabilityAnnotations, propertyDecl.ParentDecl);
        WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);

        var mainActorAnnotation = needsMainActor ? "@MainActor\n" : "";
        swiftWriter.WriteLines($$"""
            {{mainActorAnnotation}}@_cdecl("{{swiftWrapperName}}")
            public func {{swiftWrapperName}}(
                {{selfParam}}_ elementCallback: @convention(c) (UnsafeRawPointer, Int64) -> Bool,
                _ completionCallback: @convention(c) (Int64) -> Void,
                _ context: Int64
            ) {
            {{selfReconstruction}}    {{taskOpen}}
                    for await element in {{awaitPrefix}}{{selfAccess}}.{{NameProvider.EscapeSwiftKeyword(propertyDecl.Name)}} {
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
    /// <param name="containingTypeName">Optional containing type name for collision detection (CS0542).</param>
    /// <param name="propertyRenames">Optional property renames for nested-type collision resolution.</param>
    public static void EmitPropertyGetter(
        CSharpWriter csWriter,
        PropertyDecl propertyDecl,
        AsyncStreamHandler asyncStreamHandler,
        string swiftWrapperName,
        string callbackName,
        string? containingTypeName = null,
        Dictionary<string, string>? propertyRenames = null)
    {
        var elementType = asyncStreamHandler.GetCSharpElementType(propertyDecl.SwiftTypeSpec);
        var asyncEnumerableType = $"IAsyncEnumerable<{elementType}>";
        var isStatic = propertyDecl.IsStatic;
        var staticModifier = isStatic ? "static " : "";
        var baseName = NameProvider.GetPropertyName(propertyDecl.Name, containingTypeName);
        var propertyName = NameProvider.GetFinalMemberName(baseName, propertyRenames);
        var classParent = propertyDecl.ParentDecl as ClassDecl;
        var selfExpr = classParent != null
            ? (classParent.IsObjCRooted ? "Handle" : "_handle.DangerousGetHandle()")
            : "_payload.DangerousGetHandle()";
        var selfArg = isStatic ? "" : $"(void*){selfExpr}, ";

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
