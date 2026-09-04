// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

public partial class ProtocolProxyEmitter
{
    private void EmitISwiftObjectImplementation(CSharpWriter writer, ProtocolDecl protocolDecl, WitnessDispatchEmitter dispatchEmitter)
    {
        var proxyClassName = GetProxyClassName(protocolDecl);
        var witnessTableSymbol = GetWitnessTableSymbol(protocolDecl);
        var wrapperLibPath = _typeDatabase.AsyncLibraryName ?? _typeDatabase.GetLibraryPath(_moduleName);

        // GetTypeMetadata source: class-bound (NSObjectProtocol-rooted EveryObjCProtocol)
        // proxies must NOT return the opaque EveryProtocol metadata — that's a different
        // Swift type's metadata — so they read directly from the EveryObjCProtocol accessor.
        // Opaque proxies return the per-module s_everyProtocolMetadata field (Finding 33),
        // sourced from this module's own metadata accessor rather than a process-global latch.
        // Read-only (Swift-vended-only) proxies have NO helper metadata to return: their module
        // may export no EveryProtocol scaffolding, and they never synthesize a conformance — so
        // they fail clean here, exactly as GetWitnessTableFromSwift does for the same proxies.
        // GetTypeMetadata is only reached on the C#-implements-protocol (synthesis) direction;
        // the Swift-vended wrap path reads the existential's own metadata word and never calls it.
        var getTypeMetadataBody = _isReadOnlyProxy
            ? "// Read-only (Swift-vended-only) proxy: no synthesizable EveryProtocol conformance,\n"
                + "                // so there is no helper type metadata to return. Only the C#-implements-protocol\n"
                + "                // (synthesis) direction reaches this method; fail clean rather than reading a\n"
                + "                // metadata accessor the wrapper never exported.\n"
                + "                throw new global::System.NotSupportedException(\n"
                + "                    \"Type metadata is not available for the read-only protocol proxy '" + protocolDecl.Name + "': its existential cannot be synthesized from a managed implementation (it carries a class-superclass or cross-module constraint the generator cannot satisfy). Use a Swift-vended instance instead.\");"
            : _useObjCBase
                ? $"return TypeMetadata.FromHandle(NativeMethods.{GetMetadataMethodName}());"
                : "// Per-module EveryProtocol metadata (Finding 33) — see s_everyProtocolMetadata.\n                return s_everyProtocolMetadata;";

        // NewFromPayload: Swift→C# wrap factory. Symmetric to the receiver-side fix —
        // for class-bound proxies (AnyObject-rooted OR NSObjectProtocol-rooted EveryObjCProtocol),
        // Swift passes a 2-word existential (`[classRef][witnessTable]`), so dereferencing
        // a full 5-word ExistentialContainer1 would over-read stack memory. Read exactly
        // the two wire words and preserve BOTH — Payload0 carries the class reference
        // (proxy handle when the existential IS our proxy, foreign class ref otherwise),
        // and Payload1 carries the witness table the sender used. Preserving the wire
        // witness table is required so that a foreign Swift/ObjC implementation of the
        // protocol round-trips through its own witness table when marshalled back to Swift
        // (synthesizing our `ProtocolWitnessTableHandle` here would dispatch foreign
        // payloads through our @_cdecl wrappers, whose TryGetProxy<T> lookup would silently
        // miss and return zero — losing the foreign implementation's behaviour). For the
        // our-proxy-on-the-wire case, word 1 IS our witness table already (set by the ctor
        // that originated the proxy), so preserving is also correct.
        var useClassBoundContainerLayout = IsProtocolClassBound(protocolDecl) || _useObjCBase;

        // A proxy that ADOPTED a Swift-returned `any P` / `(any P)?` existential at +1
        // (constructed with `ownsContainer: true` by the owned-return marshalling paths)
        // owns, for the OPAQUE (5-word) layout, the container's value-witness retains —
        // the inline class reference for a class conformer, or the heap box for a boxed
        // value conformer — and must release them on Dispose/finalize, or the payload's
        // +1 is orphaned. Destroying through the opaque existential's own value-witness
        // table (resolved by protocol count) releases either shape correctly. The release
        // is gated on _ownsContainer: borrowed parameter wraps, payload-pointer reads,
        // C#-impl-backed proxies (lifetime anchored by ProxyLifetimeTracker), and
        // externally constructed / zeroed containers do NOT own a +1 and must not be
        // destroyed. The class-bound / ObjC 2-word [classRef][witnessTable] layout is a
        // separate release shape and keeps the original no-release Dispose untouched.
        // The release body differs by container layout, but both layouts share the
        // Dispose + finalizer skeleton so that an ADOPTED (_ownsContainer) existential is
        // released exactly once. Non-owning proxies (C#-impl-backed, borrowed wraps,
        // payload reads, zeroed/synthetic containers) construct with ownsContainer:false,
        // which makes ReleaseAdoptedSwiftContainer a no-op and (via the ctor)
        // suppresses finalization — preserving the original no-release behaviour for them.
        var releaseAdoptedBody = useClassBoundContainerLayout
            ? """
                // Class-bound (2-word [classRef][witnessTable]) existential: the adopted +1
                // lives directly on the class reference in Payload0 — there is no opaque
                // value-witness table to destroy through, so an ARC release balances it.
                // The class reference may be an Objective-C object (a protocol refined by
                // NSObjectProtocol / a UIKit class), so the release routes through the
                // kind-dispatching unknown-object entry point rather than swift_release.
                // This body runs from BOTH Dispose and the GC finalizer (~Proxy), so it must
                // use the finalizer-safe Arc.UnknownObjectReleaseFinalizerSafe — which hops
                // through the SBW_SwiftUnknownObjectRelease @_cdecl trampoline — rather than a
                // direct swift_unknownObjectRelease P/Invoke, which crashes Mono with the
                // !ji->async assertion on the finalizer thread after CallConvSwift JIT
                // contamination (the same reason the opaque path routes through SBW_VWTDestroy).
                // The helper swallows every fault path, so no try/catch is needed here.
                // Gated on _ownsContainer (owned-return paths only); borrowed wraps,
                // payload reads, C#-impl-backed proxies (anchored by ProxyLifetimeTracker),
                // and zeroed containers own no +1 and must not be released.
                if (!_ownsContainer)
                    return;
                var classRef = _swiftContainer.Payload0;
                if (classRef != IntPtr.Zero)
                {
                    global::Swift.Runtime.Arc.UnknownObjectReleaseFinalizerSafe(classRef);
                }
            """
            : """
                if (!_ownsContainer)
                    return;
                try
                {
                    fixed (ExistentialContainer1* containerPtr = &_swiftContainer)
                    {
                        var existentialMetadata = Swift.Runtime.TypeMetadata.GetExistentialTypeMetadata(_swiftContainer.Count);
                        // This body runs from BOTH Dispose (user thread) and the GC finalizer
                        // (~Proxy). A direct VWT Destroy (CallConvSwift) from the finalizer thread
                        // crashes Mono with the !ji->async assertion after CallConvSwift JIT
                        // contamination — the same hazard the class-bound sibling
                        // dodges via Arc.UnknownObjectReleaseFinalizerSafe. Route through the
                        // SBW_VWTDestroy @_cdecl trampoline, which is safe from either thread.
                        Swift.Runtime.InteropServices.SwiftMarshal.DestroyWireBufferRetainsFinalizerSafe((IntPtr)containerPtr, existentialMetadata);
                    }
                }
                catch
                {
                    // Existential metadata unavailable (e.g. SwiftBindingsRuntime not
                    // loaded under unit tests) — skip the destroy rather than throw
                    // from Dispose/finalize.
                }
            """;
        var disposeAndFinalizer = $$"""
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                GC.SuppressFinalize(this);
                ReleaseAdoptedSwiftContainer();
                // Design B2: a C#-impl-backed proxy owns the EveryProtocol construction +1
                // (R0). Drop the (weak) registry entry, then release R0 through
                // ProxyLifetimeTracker (the finalizer-safe Cdecl trampoline). Releasing R0 may
                // drive Swift's retain count to zero and fire OnEveryProtocolDeinit, which frees
                // the impl's strong root and re-runs Unregister (idempotent). The HandleEntry
                // atomic makes the release exactly-once even if the finalizer also runs. The
                // null-safe receivers ensure an Unregister'd handle does not throw across the
                // [UnmanagedCallersOnly] boundary if Swift dispatches concurrently.
                if (_everyProtocolHandle != IntPtr.Zero)
                    SwiftObjectRegistry.Unregister(_everyProtocolHandle);
                if (_ownsEveryProtocolR0)
                    Swift.Runtime.ProxyLifetimeTracker.ReleaseHandle(_everyProtocolHandle);
            }

            /// <summary>
            /// Finalizer — releases an adopted (<c>_ownsContainer</c>) existential container
            /// and, for a C#-impl-backed proxy, the EveryProtocol construction +1 (R0) via
            /// <see cref="Swift.Runtime.ProxyLifetimeTracker.ReleaseHandle"/>, if the consumer
            /// never called <see cref="Dispose"/>. Proxies that own neither suppress
            /// finalization in their constructor, so this only runs for owners.
            /// </summary>
            ~{{proxyClassName}}()
            {
                if (_disposed) return;
                _disposed = true;
                ReleaseAdoptedSwiftContainer();
                if (_ownsEveryProtocolR0)
                    Swift.Runtime.ProxyLifetimeTracker.ReleaseHandle(_everyProtocolHandle);
            }

            // Releases an ADOPTED Swift-returned existential container's +1. Gated to
            // proxies that actually own a +1 (_ownsContainer == true, set only by the
            // owned-return marshalling paths). C#-impl-backed proxies, borrowed parameter
            // wraps, payload-pointer reads, and externally constructed or zeroed
            // containers do NOT own a +1 — releasing their (possibly borrowed or
            // null-metadata) container would be a use-after-free / SIGSEGV.
            private void ReleaseAdoptedSwiftContainer()
            {
            {{releaseAdoptedBody}}
            }
            """;
        var newFromPayloadBody = useClassBoundContainerLayout
            ? $"// Class-bound (AnyObject-rooted or EveryObjCProtocol): Swift passes a 2-word\n                // existential ([classRef][witnessTable]). Read exactly two wire words;\n                // preserve both so foreign implementations round-trip through their own WT.\n                var wordPtr = (IntPtr*)payload;\n                var container = new ExistentialContainer1\n                {{\n                    Payload0 = wordPtr[0],\n                    Payload1 = wordPtr[1],\n                }};\n                return new {proxyClassName}(container);"
            : $"// Opaque (5-word [payload0][payload1][payload2][metadata][WT]) existential\n                var container = *(ExistentialContainer1*)payload;\n                return new {proxyClassName}(container);";

        // GetWitnessTableFromSwift body: only reachable from the C#-implements-protocol (CALLBACK)
        // direction. When EveryProtocolEmitter did NOT export a `Get_EveryProtocol_{P}_WitnessTable`
        // symbol for this protocol (read-only / class-superclass-skipped / cross-module proxies),
        // calling NativeMethods.GetWitnessTable() would EntryPointNotFound. Fail clean with
        // NotSupportedException instead — the Swift-vended RETURN/ACCEPT directions never reach this
        // method (they dispatch through the existential's own witness table).
        var getWitnessTableBody = _witnessGetterEmitted
            ? "// Call the Swift-exported function that returns the witness table pointer\n"
                + "                // This function is generated by EveryProtocolEmitter.EmitWitnessTableGetter()\n"
                + "                return NativeMethods.GetWitnessTable();"
            : "// No Get_EveryProtocol_" + protocolDecl.Name + "_WitnessTable symbol was exported for this\n"
                + "                // protocol — its EveryProtocol conformance was not emitted (a class-superclass\n"
                + "                // or cross-module constraint the synthesized helper cannot satisfy). Only the\n"
                + "                // C#-implements-protocol (CALLBACK) direction reaches this method; fail clean\n"
                + "                // rather than surfacing an opaque EntryPointNotFoundException.\n"
                + "                throw new global::System.NotSupportedException(\n"
                + "                    \"Implementing the Swift protocol '" + protocolDecl.Name + "' in C# and passing it back to Swift is not supported: its existential cannot be synthesized from a managed implementation (it carries a class-superclass or cross-module constraint the generator cannot satisfy). Use a Swift-vended instance instead.\");";

        // Existential-layout tripwire. The container layout above (2-word class-bound vs 5-word
        // opaque) is chosen from parsed ABI facts; a mis-classified protocol puts the witness table
        // in a word Swift does not read, and Swift then dispatches through a null witness table and
        // SIGSEGVs inside the framework with no managed frame. The Swift wrapper reports
        // `MemoryLayout<any P>.size`, which decides the shape unambiguously, so the proxy asks it
        // once — lazily, when it first resolves the witness table — and compares against the size
        // its OWN layout choice implies. The expected size is derived from the same
        // `useClassBoundContainerLayout` predicate that picked the layout, so the two cannot drift.
        //
        // Emitted only when the wrapper exported the witness-table getter: the size accessor is
        // emitted beside that getter, and the proxies that lack it (read-only /
        // class-superclass-skipped / cross-module) never construct a container from
        // ProtocolWitnessTableHandle at all — GetWitnessTableFromSwift() throws for them.
        //
        // The check runs BEFORE the handle is cached, so a mismatch leaves the field zero and
        // re-throws on every subsequent access rather than caching a handle it just rejected.
        // A proxy used without the native library loaded (unit tests) fails exactly as before:
        // the P/Invoke raises DllNotFoundException, the same exception GetWitnessTableFromSwift
        // would have raised one line later.
        var expectedExistentialSize = useClassBoundContainerLayout
            ? "global::Swift.Runtime.ExistentialLayout.ClassBoundSize"
            : "global::Swift.Runtime.ExistentialLayout.OpaqueSize";
        var verifyLayoutCall = _witnessGetterEmitted
            ? "VerifyExistentialLayout();\n            "
            : "";
        var verifyLayoutMethod = _witnessGetterEmitted
            ? $$"""

            /// <summary>
            /// Asserts that the existential-container layout this proxy fills matches the layout the
            /// Swift library actually reads for <c>any {{protocolDecl.Name}}</c>. Throws
            /// <see cref="global::System.InvalidOperationException"/> when they disagree, rather than
            /// handing Swift a container whose protocol witness table sits in the wrong word.
            /// </summary>
            private static void VerifyExistentialLayout()
            {
                nint reportedSize;
                try
                {
                    reportedSize = NativeMethods.GetExistentialSize();
                }
                catch (global::System.EntryPointNotFoundException ex)
                {
                    // Fail closed: a wrapper too old to export the accessor cannot confirm the layout,
                    // and silently skipping the check would restore the crash this exists to prevent.
                    throw global::Swift.Runtime.ExistentialLayout.MissingSizeAccessor("{{protocolDecl.Name}}", ex);
                }
                global::Swift.Runtime.ExistentialLayout.Verify(
                    "{{protocolDecl.Name}}", {{expectedExistentialSize}}, (int)reportedSize);
            }

            """
            : "";

        writer.WriteLines($$"""
            #region ISwiftObject Implementation

            /// <summary>
            /// Gets the protocol witness table handle for EveryProtocol conforming to {{protocolDecl.Name}}.
            /// </summary>
            public static IntPtr ProtocolWitnessTableHandle
            {
                get
                {
                    if (_protocolWitnessTable == IntPtr.Zero)
                    {
                        {{verifyLayoutCall}}// The witness table is generated by the Swift compiler
                        // It will be available after the Swift wrapper is loaded
                        // For now, we look it up dynamically
                        _protocolWitnessTable = GetWitnessTableFromSwift();
                    }
                    return _protocolWitnessTable;
                }
            }

            private static IntPtr GetWitnessTableFromSwift()
            {
                {{getWitnessTableBody}}
            }
            {{verifyLayoutMethod}}

            ExistentialContainer1 ISwiftExistentialConvertible<ExistentialContainer1>.GetExistentialContainer()
            {
                if (_disposed) throw new ObjectDisposedException(GetType().Name);
                return _swiftContainer;
            }

            public static TypeMetadata GetTypeMetadata()
            {
                {{getTypeMetadataBody}}
            }

            public static ISwiftObject NewFromPayload(IntPtr payload)
            {
                {{newFromPayloadBody}}
            }

            // Proxy types read their existential container by value; the marshal seam frees the wire
            // temporary and never touches SwiftHandle. Declared public (not explicit) and not module-init
            // registered, so the runtime reflection backstop reliably finds it on Mono and NativeAOT.
            public static global::Swift.Runtime.PayloadConstructionSemantics PayloadConstructionSemantics => global::Swift.Runtime.PayloadConstructionSemantics.Inline;

            IntPtr ISwiftObject.SwiftHandle => _swiftContainer.Payload0;

            public int MarshalToSwift(ref Span<byte> swiftDestSpan)
            {
                if (_disposed) throw new ObjectDisposedException(GetType().Name);
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

            {{disposeAndFinalizer}}

            #endregion

            #region Marshalling Helpers

            private static IntPtr MarshalToSwiftBuffer<T>(T value)
            {
                // Reference-type wrappers (SwiftOptional<U>, SwiftArray<U>, non-frozen struct
                // wrappers, ...) are ISwiftObject classes whose C# instance is a handle to a
                // native Swift buffer, not the value itself. A blittable Unsafe.Write would copy
                // the managed reference (a pointer) rather than the value's native bytes, and the
                // native value is usually larger than a pointer (e.g. Optional<ClosedRange<Float>>),
                // so Swift would also read past the allocation. Marshal the native Swift bytes
                // through the type's value-witness table into a metadata-sized buffer instead.
                if (!typeof(T).IsValueType && value is ISwiftObject swiftObj)
                {
                    var nativeSize = (int)Swift.Runtime.TypeMetadata.GetTypeMetadataOrThrow<T>().Size;
                    // Zero-sized Swift types never reach this branch (Optional is >= 1, collections
                    // and existentials are >= one word), but keep the span length in lockstep with the
                    // allocation's size == 0 ? 1 guard so the view can never outlive a 1-byte buffer.
                    var allocSize = nativeSize == 0 ? 1 : nativeSize;
                    var nativePtr = (IntPtr)NativeMemory.AllocZeroed((nuint)allocSize);
                    var nativeSpan = new Span<byte>((void*)nativePtr, allocSize);
                    swiftObj.MarshalToSwift(ref nativeSpan);
                    return nativePtr;
                }
                // Blittable types (primitives, frozen structs, simple enums): the C# layout
                // matches the native Swift layout, so a direct byte copy is correct.
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

            /// <summary>
            /// Marshals a C# string to a heap-allocated SBW_Utf8Slice struct (ptr + len).
            /// Used for string returns from proxy receiver callbacks to avoid ARC issues
            /// with SwiftString (which contains managed references that Unsafe.Write can't retain).
            /// The Swift side reads the Utf8Slice, creates a String, and frees the buffers.
            /// </summary>
            private static unsafe IntPtr MarshalStringToUtf8Slice(string value)
            {
                var utf8 = global::System.Text.Encoding.UTF8.GetBytes(value);
                var dataPtr = (byte*)NativeMemory.Alloc((nuint)Math.Max(utf8.Length, 1));
                if (utf8.Length > 0)
                {
                    fixed (byte* src = utf8)
                        global::System.Buffer.MemoryCopy(src, dataPtr, utf8.Length, utf8.Length);
                }
                // SBW_Utf8Slice layout: { ptr: UnsafeMutablePointer<UInt8>, len: Int }
                var slicePtr = (byte*)NativeMemory.Alloc((nuint)(2 * sizeof(nint)));
                *(IntPtr*)slicePtr = (IntPtr)dataPtr;
                *(nint*)(slicePtr + sizeof(nint)) = utf8.Length;
                return (IntPtr)slicePtr;
            }

            #endregion

            """);

        // Emit NativeMethods for SetVtable + witness dispatch
        var setVtableName = GetSetVtablePInvokeName(protocolDecl);
        var mangledName = $"Set{protocolDecl.Name}_vtable";

        // Note: vtable and witness table functions are in the SwiftBindings wrapper, not the original module
        writer.WriteLine("private static partial class NativeMethods");
        writer.WriteLine("{");
        writer.Indent++;

        PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
        {
            LibraryPath = wrapperLibPath,
            EntryPoint = mangledName,
            MethodName = setVtableName,
            ReturnType = "void",
            ParametersString = "IntPtr vtable",
            CallingConvention = PInvokeCallingConvention.Cdecl,
            Visibility = PInvokeVisibility.Public
        });
        writer.WriteLine();
        // The witness-table getter P/Invoke is only reachable from GetWitnessTableFromSwift().
        // Suppress the declaration when EveryProtocolEmitter did not export the matching
        // `Get_EveryProtocol_{P}_WitnessTable` symbol (read-only / class-superclass-skipped /
        // cross-module proxies); GetWitnessTableFromSwift() throws NotSupportedException in that
        // case, so the declaration would otherwise be a dangling EntryPoint.
        if (_witnessGetterEmitted)
        {
            PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
            {
                LibraryPath = wrapperLibPath,
                EntryPoint = $"Get_EveryProtocol_{protocolDecl.Name}_WitnessTable",
                MethodName = "GetWitnessTable",
                ReturnType = "IntPtr",
                ParametersString = "",
                CallingConvention = PInvokeCallingConvention.Cdecl,
                Visibility = PInvokeVisibility.Public
            });
            writer.WriteLine();
            // Existential-size accessor for the layout tripwire, emitted beside the witness-table
            // getter and gated on the same fact, so the declared and exported sets cannot drift.
            PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
            {
                LibraryPath = wrapperLibPath,
                EntryPoint = $"Get_EveryProtocol_{protocolDecl.Name}_ExistentialSize",
                MethodName = "GetExistentialSize",
                ReturnType = "nint",
                ParametersString = "",
                CallingConvention = PInvokeCallingConvention.Cdecl,
                Visibility = PInvokeVisibility.Public
            });
            writer.WriteLine();
        }
        // The EveryProtocol carrier trio (Create / SetDeinitCallback / GetMetadata, or the
        // EveryObjCProtocol / EveryEntityProtocol twins) is DECLARED only when this proxy can
        // actually CALL it — the same `UsesEveryProtocolCarrier` fact the three call sites are
        // gated on, so the declared set and the called set derive from one decision instead of
        // two predicates that must stay accidentally equal.
        //
        // A read-only (Swift-vended-only) proxy calls none of the three: its C#-impl ctor and
        // GetTypeMetadata() throw NotSupportedException instead, and it emits no eager
        // s_everyProtocolMetadata field. Its module may also export no EveryProtocol scaffolding
        // at all (zero suitable protocols ⇒ EmitEveryProtocolClass never runs), in which case
        // these three entry points are undefined and the declarations alone are a dangling
        // wrapper-symbol reference that WrapperSymbolIntegrityGate hard-fails at generation time.
        if (UsesEveryProtocolCarrier)
        {
            PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
            {
                LibraryPath = wrapperLibPath,
                EntryPoint = CreateHelperEntryPoint,
                MethodName = CreateHelperMethodName,
                ReturnType = "IntPtr",
                ParametersString = "",
                CallingConvention = PInvokeCallingConvention.Cdecl,
                Visibility = PInvokeVisibility.Public
            });
            writer.WriteLine();
            // SBW_SetEveryProtocolDeinitCallback (or its EveryObjCProtocol twin) — wires
            // a C# unmanaged callback onto a helper instance so Swift's deinit can drive
            // the strong-registry and ProxyLifetimeTracker teardown. The function-pointer
            // parameter requires an unsafe P/Invoke; [LibraryImport] supports
            // `delegate* unmanaged[Cdecl]<...>` in .NET 10.
            PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
            {
                LibraryPath = wrapperLibPath,
                EntryPoint = SetDeinitCallbackEntryPoint,
                MethodName = SetDeinitCallbackMethodName,
                ReturnType = "void",
                ParametersString = "IntPtr instance, delegate* unmanaged[Cdecl]<IntPtr, void> callback, IntPtr context",
                CallingConvention = PInvokeCallingConvention.Cdecl,
                Visibility = PInvokeVisibility.Public,
                IsUnsafe = true,
            });
            writer.WriteLine();
            PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
            {
                LibraryPath = wrapperLibPath,
                EntryPoint = GetMetadataEntryPoint,
                MethodName = GetMetadataMethodName,
                ReturnType = "IntPtr",
                ParametersString = "",
                CallingConvention = PInvokeCallingConvention.Cdecl,
                Visibility = PInvokeVisibility.Public
            });
        }

        // Emit P/Invoke declarations for witness dispatch accessors
        EmitWitnessDispatchPInvokes(writer, protocolDecl, dispatchEmitter, wrapperLibPath);

        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Emits the merged member+protocol availability immediately before a witness-dispatch
    /// P/Invoke declaration. The Swift forwarder behind that entry point is declared at the same
    /// merged floor — a requirement introduced after its protocol is only reachable above the
    /// member's own floor — so the extern and the proxy member that calls it stay in step.
    /// Platform+version pairs the proxy class already declares are dropped by the emitter.
    /// </summary>
    private static void EmitAccessorAvailability(CSharpWriter writer, BaseDecl member, ProtocolDecl protocolDecl)
        => AvailabilityAttributeEmitter.EmitAvailabilityAttributes(writer, member, protocolDecl, emitObsolete: false);

    /// <summary>
    /// <see cref="EmitAccessorAvailability"/> for a property SETTER P/Invoke. Swift can introduce a
    /// setter after the property itself, and the <c>@_cdecl</c> setter forwarder is exported at
    /// that later floor — so the extern must carry it too. Annotating the setter extern from the
    /// property's (older) floor would advertise a symbol that is weak-linked to null between the
    /// two floors, which is a native fault at the call rather than a CA1416 diagnostic.
    /// </summary>
    private static void EmitSetterAvailability(CSharpWriter writer, PropertyDecl property, ProtocolDecl protocolDecl)
        => AvailabilityAttributeEmitter.EmitAvailabilityAttributes(
            writer, AvailabilityHelpers.SelectSetterAnnotations(property), protocolDecl, emitObsolete: false);

    /// <summary>
    /// Emits an accessor + free P/Invoke declaration pair for heap-pointer-returning property getters.
    /// Both blittable/string and collection property getters use identical P/Invoke shapes
    /// (accessor: IntPtr containerPtr → IntPtr, free: IntPtr ptr → void).
    /// </summary>
    private static void EmitHeapPointerGetterPInvokePair(CSharpWriter writer, string accessorSymbol, string freeSymbol,
        string wrapperLibPath, PropertyDecl property, ProtocolDecl protocolDecl)
    {
        writer.WriteLine();
        EmitAccessorAvailability(writer, property, protocolDecl);
        PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
        {
            LibraryPath = wrapperLibPath,
            EntryPoint = accessorSymbol,
            MethodName = accessorSymbol,
            ReturnType = "IntPtr",
            ParametersString = "IntPtr containerPtr",
            CallingConvention = PInvokeCallingConvention.Cdecl,
            Visibility = PInvokeVisibility.Public
        });
        writer.WriteLine();
        EmitAccessorAvailability(writer, property, protocolDecl);
        PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
        {
            LibraryPath = wrapperLibPath,
            EntryPoint = freeSymbol,
            MethodName = freeSymbol,
            ReturnType = "void",
            ParametersString = "IntPtr ptr",
            CallingConvention = PInvokeCallingConvention.Cdecl,
            Visibility = PInvokeVisibility.Public
        });
    }

    /// <summary>
    /// Emits P/Invoke declarations for witness dispatch accessor and free functions.
    /// </summary>
    private void EmitWitnessDispatchPInvokes(CSharpWriter writer, ProtocolDecl protocolDecl, WitnessDispatchEmitter dispatchEmitter, string wrapperLibPath)
    {
        var protocolName = protocolDecl.Name;
        var emittedPInvokes = new HashSet<string>();

        // Property getters — eligibility (static / @objc-optional / custom-actor) must match the
        // Swift wrapper walk exactly, else we declare a P/Invoke for a symbol it never exported.
        foreach (var property in protocolDecl.Properties)
        {
            if (!WitnessDispatchEmitter.IsPropertyWitnessDispatchEligible(property, protocolDecl))
                continue;

            var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
            // Only emit blittable/string getter P/Invokes here — class/struct properties
            // fall through to IsPropertyClassReturn/IsPropertyStructReturn branches below.
            // Note: Swift.String is a frozen+RefFields struct, so IsIndirectStructType matches it.
            // We must check IsStringDispatchType first to keep string on the blittable path.
            var isStringProp = WitnessDispatchEmitter.IsStringDispatchType(property.SwiftTypeSpec);
            if (hasGetter && dispatchEmitter.IsPropertyGetterDispatchable(property)
                && (isStringProp || (!dispatchEmitter.IsSwiftClassType(property.SwiftTypeSpec)
                    && !dispatchEmitter.IsIndirectStructType(property.SwiftTypeSpec))))
            {
                var csharpTypeName = GetInterfaceCompatiblePropertyTypeName(property);
                var isStringProperty = WitnessDispatchEmitter.IsStringDispatchType(property.SwiftTypeSpec);
                // Validate projected type allows dispatch (same gate as EmitPropertyImplementation)
                if (!isStringProperty && !WitnessDispatchEmitter.IsBlittablePrimitive(csharpTypeName))
                    continue;

                var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolName, "get", property.Name, 0);
                if (!emittedPInvokes.Add(accessorSymbol))
                    continue;

                var freeSymbol = WitnessDispatchEmitter.GetFreeSymbol(protocolName, "get", property.Name, 0);
                EmitHeapPointerGetterPInvokePair(writer, accessorSymbol, freeSymbol, wrapperLibPath, property, protocolDecl);
            }
            else if (hasGetter && dispatchEmitter.IsPropertyClassReturn(property))
            {
                // ClassReturn getter: returns IntPtr, no free function
                var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolName, "get", property.Name, 0);
                if (!emittedPInvokes.Add(accessorSymbol))
                    continue;

                writer.WriteLine();
                EmitAccessorAvailability(writer, property, protocolDecl);
                PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
                {
                    LibraryPath = wrapperLibPath,
                    EntryPoint = accessorSymbol,
                    MethodName = accessorSymbol,
                    ReturnType = "IntPtr",
                    ParametersString = "IntPtr containerPtr",
                    CallingConvention = PInvokeCallingConvention.Cdecl,
                    Visibility = PInvokeVisibility.Public
                });
            }
            else if (hasGetter && dispatchEmitter.IsPropertyOptionalClassReturn(property))
            {
                // Optional<class> getter: returns IntPtr (IntPtr.Zero == nil), no free function
                var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolName, "get", property.Name, 0);
                if (!emittedPInvokes.Add(accessorSymbol))
                    continue;

                writer.WriteLine();
                EmitAccessorAvailability(writer, property, protocolDecl);
                PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
                {
                    LibraryPath = wrapperLibPath,
                    EntryPoint = accessorSymbol,
                    MethodName = accessorSymbol,
                    ReturnType = "IntPtr",
                    ParametersString = "IntPtr containerPtr",
                    CallingConvention = PInvokeCallingConvention.Cdecl,
                    Visibility = PInvokeVisibility.Public
                });
            }
            else if (hasGetter && dispatchEmitter.IsPropertyStructReturn(property))
            {
                // StructReturn getter: returns void, has resultBuf param, no free function
                var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolName, "get", property.Name, 0);
                if (!emittedPInvokes.Add(accessorSymbol))
                    continue;

                writer.WriteLine();
                EmitAccessorAvailability(writer, property, protocolDecl);
                PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
                {
                    LibraryPath = wrapperLibPath,
                    EntryPoint = accessorSymbol,
                    MethodName = accessorSymbol,
                    ReturnType = "void",
                    ParametersString = "IntPtr containerPtr, IntPtr resultBuf",
                    CallingConvention = PInvokeCallingConvention.Cdecl,
                    Visibility = PInvokeVisibility.Public
                });
            }
            else if (hasGetter && dispatchEmitter.IsPropertyCollectionReturn(property))
            {
                var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolName, "get", property.Name, 0);
                if (!emittedPInvokes.Add(accessorSymbol))
                    continue;

                // ObjC-bridgeable containers return a +1 NS* collection pointer with NO free function
                // (the C# whole-container bridge adopts the +1) — same accessor-only shape as ClassReturn.
                // Native Swift container boxes use the accessor + typed free pair.
                if (CdeclParamMapper.IsObjCBridgeableContainer(property.SwiftTypeSpec, _typeDatabase))
                {
                    writer.WriteLine();
                    EmitAccessorAvailability(writer, property, protocolDecl);
                    PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
                    {
                        LibraryPath = wrapperLibPath,
                        EntryPoint = accessorSymbol,
                        MethodName = accessorSymbol,
                        ReturnType = "IntPtr",
                        ParametersString = "IntPtr containerPtr",
                        CallingConvention = PInvokeCallingConvention.Cdecl,
                        Visibility = PInvokeVisibility.Public
                    });
                }
                else
                {
                    var freeSymbol = WitnessDispatchEmitter.GetFreeSymbol(protocolName, "get", property.Name, 0);
                    EmitHeapPointerGetterPInvokePair(writer, accessorSymbol, freeSymbol, wrapperLibPath, property, protocolDecl);
                }
            }
            else if (hasGetter && dispatchEmitter.IsPropertyExistentialReturn(property))
            {
                // ExistentialReturn getter: heap-cell pointer (IntPtr) + typed free function
                var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolName, "get", property.Name, 0);
                if (!emittedPInvokes.Add(accessorSymbol))
                    continue;

                var freeSymbol = WitnessDispatchEmitter.GetFreeSymbol(protocolName, "get", property.Name, 0);
                EmitHeapPointerGetterPInvokePair(writer, accessorSymbol, freeSymbol, wrapperLibPath, property, protocolDecl);
            }
        }

        // Property setters — same shared eligibility predicate as the getter walk above.
        foreach (var property in protocolDecl.Properties)
        {
            if (!WitnessDispatchEmitter.IsPropertyWitnessDispatchEligible(property, protocolDecl))
                continue;

            var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();
            // Only emit blittable/string setter P/Invokes — no setter dispatch for class/struct types yet.
            var isSetterStringProp = WitnessDispatchEmitter.IsStringDispatchType(property.SwiftTypeSpec);
            if (hasSetter && dispatchEmitter.IsPropertySetterDispatchable(property)
                && (isSetterStringProp || (!dispatchEmitter.IsSwiftClassType(property.SwiftTypeSpec)
                    && !dispatchEmitter.IsIndirectStructType(property.SwiftTypeSpec))))
            {
                var csharpTypeName = GetInterfaceCompatiblePropertyTypeName(property);
                var isStringProperty = WitnessDispatchEmitter.IsStringDispatchType(property.SwiftTypeSpec);
                // Validate projected type allows dispatch (same gate as EmitPropertyImplementation)
                if (!isStringProperty && !WitnessDispatchEmitter.IsBlittablePrimitive(csharpTypeName))
                    continue;

                var setterSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolName, "set", property.Name, 0);
                if (!emittedPInvokes.Add(setterSymbol))
                    continue;

                writer.WriteLine();
                EmitSetterAvailability(writer, property, protocolDecl);
                PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
                {
                    LibraryPath = wrapperLibPath,
                    EntryPoint = setterSymbol,
                    MethodName = setterSymbol,
                    ReturnType = "void",
                    ParametersString = "IntPtr containerPtr, IntPtr valuePtr",
                    CallingConvention = PInvokeCallingConvention.Cdecl,
                    Visibility = PInvokeVisibility.Public
                });
            }
        }

        // Methods
        int methodIndex = 0;
        var methodIndices = new Dictionary<string, int>();
        var emittedCSharpKeys = new HashSet<string>();
        foreach (var method in protocolDecl.Methods)
        {
            // Shared eligibility predicate — @objc-optional methods consume no index here, exactly
            // as in the Swift wrapper walk, so a required method's SBW index never drifts.
            if (!WitnessDispatchEmitter.IsMethodWitnessDispatchEligible(method, protocolDecl))
                continue;
            // @objc optional methods get no witness accessor — the producer
            // (WitnessDispatchEmitter) skips them BEFORE the index increment, so this
            // consumer must too or the following required method's accessor symbol skews.
            if (method.IsObjCOptional)
                continue;

            // Allocate the SBW slot index on the SWIFT-domain requirement key — the same key the
            // producer walk (WitnessDispatchEmitter) numbers on — NOT the projected C# key. Two
            // overloads whose distinct Swift parameter types both project to Swift.AnyType are two
            // witness requirements (two producer indices); keying the index here on the collapsed
            // C# projection would give them one index and skew every later method's accessor symbol
            // (EntryPointNotFoundException). EffectiveWitnessSlotKey additionally splits a disambiguated
            // label-only pair (identical Swift types, differing labels) into two slots — matching the
            // producer — so the Swift-backed proxy can forward each sibling to its own witness.
            var slotKey = ProtocolMethodDisambiguator.EffectiveWitnessSlotKey(method, protocolDecl, _typeDatabase);
            if (methodIndices.ContainsKey(slotKey))
                continue;

            var idx = methodIndex++;
            methodIndices[slotKey] = idx;

            // Effective projected key so a disambiguated pair emits TWO distinct P/Invoke decls (one per
            // slot), keeping the consumer P/Invoke set aligned with the producer and the call-site walk.
            var projectedKey = ProtocolMethodDisambiguator.EffectiveProjectedKey(method, protocolDecl, _typeDatabase, propertyNames: null);
            if (!emittedCSharpKeys.Add(projectedKey))
                continue;

            var dispatchKind = dispatchEmitter.ClassifyMethodDispatch(method);
            if (dispatchKind == MethodDispatchKind.NotDispatchable)
                continue;

            var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
            var hasReturn = returnType != null && !returnType.IsEmptyTuple;
            var paramCount = method.CSSignature.Count - 1;

            var accessorSymbol = WitnessDispatchEmitter.GetAccessorSymbol(protocolName, "method", method.Name, idx);
            if (!emittedPInvokes.Add(accessorSymbol))
                continue;

            if (dispatchKind is MethodDispatchKind.ExistentialReturn or MethodDispatchKind.ThrowingBlittableOrString or MethodDispatchKind.ClassReturn or MethodDispatchKind.BoundGenericReturn)
            {
                // ExistentialReturn, ThrowingBlittableOrString, ClassReturn, and BoundGenericReturn share the same P/Invoke shape:
                // params: containerPtr + per-param IntPtrs + errorOut (if throwing)
                // return: IntPtr (or void for ThrowingBlittableOrString void)
                var isThrowingKind = dispatchKind == MethodDispatchKind.ThrowingBlittableOrString ||
                                     (dispatchKind == MethodDispatchKind.ExistentialReturn && method.Throws) ||
                                     (dispatchKind == MethodDispatchKind.ClassReturn && method.Throws) ||
                                     (dispatchKind == MethodDispatchKind.BoundGenericReturn && method.Throws);

                var pInvokeParams = new List<string> { "IntPtr containerPtr" };
                for (int i = 0; i < paramCount; i++)
                {
                    pInvokeParams.Add($"IntPtr arg{i}Ptr");
                }
                if (isThrowingKind)
                {
                    pInvokeParams.Add("IntPtr errorOut");
                }
                var pInvokeParamsString = string.Join(", ", pInvokeParams);

                // ThrowingBlittableOrString: value-returning uses IntPtr (nil=error), void uses void
                // ExistentialReturn / ClassReturn: always IntPtr
                var pInvokeReturnType = (dispatchKind == MethodDispatchKind.ThrowingBlittableOrString && !hasReturn)
                    ? "void"
                    : "IntPtr";

                writer.WriteLine();
                EmitAccessorAvailability(writer, method, protocolDecl);
                PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
                {
                    LibraryPath = wrapperLibPath,
                    EntryPoint = accessorSymbol,
                    MethodName = accessorSymbol,
                    ReturnType = pInvokeReturnType,
                    ParametersString = pInvokeParamsString,
                    CallingConvention = PInvokeCallingConvention.Cdecl,
                    Visibility = PInvokeVisibility.Public
                });

                // Free function: always for ExistentialReturn and BoundGenericReturn,
                // for value-returning ThrowingBlittableOrString,
                // never for ClassReturn (SafeHandle handles ARC release). An ObjC-bridgeable
                // container BoundGenericReturn also skips it — it returns a +1 NS* collection
                // pointer the C# whole-container bridge adopts (owns: true), not a native Swift box.
                var isObjCBridgedContainerReturn = dispatchKind == MethodDispatchKind.BoundGenericReturn &&
                                                   returnType != null &&
                                                   CdeclParamMapper.IsObjCBridgeableContainer(returnType, _typeDatabase);
                var needsFree = dispatchKind == MethodDispatchKind.ExistentialReturn ||
                                (dispatchKind == MethodDispatchKind.BoundGenericReturn && !isObjCBridgedContainerReturn) ||
                                (dispatchKind == MethodDispatchKind.ThrowingBlittableOrString && hasReturn);
                if (needsFree)
                {
                    var freeSymbol = WitnessDispatchEmitter.GetFreeSymbol(protocolName, "method", method.Name, idx);
                    writer.WriteLine();
                    EmitAccessorAvailability(writer, method, protocolDecl);
                    PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
                    {
                        LibraryPath = wrapperLibPath,
                        EntryPoint = freeSymbol,
                        MethodName = freeSymbol,
                        ReturnType = "void",
                        ParametersString = "IntPtr ptr",
                        CallingConvention = PInvokeCallingConvention.Cdecl,
                        Visibility = PInvokeVisibility.Public
                    });
                }

                // Emit error helper P/Invokes for throwing methods
                if (isThrowingKind)
                {
                    ErrorDescriptionEmitter.EmitCSharpErrorPInvokesViaPInvokeEmitHelper(
                        writer, GetProxyClassName(protocolDecl), _moduleName,
                        wrapperLibPath, _emissionContext);
                }
            }
            else if (dispatchKind == MethodDispatchKind.StructReturn)
            {
                // StructReturn: different P/Invoke shape — returns void, has resultBuf param
                var isThrowingStruct = method.Throws;

                var pInvokeParams = new List<string> { "IntPtr containerPtr", "IntPtr resultBuf" };
                for (int i = 0; i < paramCount; i++)
                {
                    pInvokeParams.Add($"IntPtr arg{i}Ptr");
                }
                if (isThrowingStruct)
                {
                    pInvokeParams.Add("IntPtr errorOut");
                }
                var pInvokeParamsString = string.Join(", ", pInvokeParams);

                writer.WriteLine();
                EmitAccessorAvailability(writer, method, protocolDecl);
                PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
                {
                    LibraryPath = wrapperLibPath,
                    EntryPoint = accessorSymbol,
                    MethodName = accessorSymbol,
                    ReturnType = "void",
                    ParametersString = pInvokeParamsString,
                    CallingConvention = PInvokeCallingConvention.Cdecl,
                    Visibility = PInvokeVisibility.Public
                });
                // No free function — SafeHandle owns the buffer

                // Emit error helper P/Invokes for throwing struct return methods
                if (isThrowingStruct)
                {
                    ErrorDescriptionEmitter.EmitCSharpErrorPInvokesViaPInvokeEmitHelper(
                        writer, GetProxyClassName(protocolDecl), _moduleName,
                        wrapperLibPath, _emissionContext);
                }
            }
            else
            {
                // BlittableOrString: existing pattern
                // Build parameter list: containerPtr + one IntPtr per param
                var pInvokeParams = new List<string> { "IntPtr containerPtr" };
                for (int i = 0; i < paramCount; i++)
                {
                    pInvokeParams.Add($"IntPtr arg{i}Ptr");
                }
                var pInvokeParamsString = string.Join(", ", pInvokeParams);
                var returnTypeStr = hasReturn ? "IntPtr" : "void";

                writer.WriteLine();
                EmitAccessorAvailability(writer, method, protocolDecl);
                PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
                {
                    LibraryPath = wrapperLibPath,
                    EntryPoint = accessorSymbol,
                    MethodName = accessorSymbol,
                    ReturnType = returnTypeStr,
                    ParametersString = pInvokeParamsString,
                    CallingConvention = PInvokeCallingConvention.Cdecl,
                    Visibility = PInvokeVisibility.Public
                });

                if (hasReturn)
                {
                    var freeSymbol = WitnessDispatchEmitter.GetFreeSymbol(protocolName, "method", method.Name, idx);
                    writer.WriteLine();
                    EmitAccessorAvailability(writer, method, protocolDecl);
                    PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
                    {
                        LibraryPath = wrapperLibPath,
                        EntryPoint = freeSymbol,
                        MethodName = freeSymbol,
                        ReturnType = "void",
                        ParametersString = "IntPtr ptr",
                        CallingConvention = PInvokeCallingConvention.Cdecl,
                        Visibility = PInvokeVisibility.Public
                    });
                }
            }
        }
    }
}
