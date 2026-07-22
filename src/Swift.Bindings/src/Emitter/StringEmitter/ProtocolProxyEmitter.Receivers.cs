// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

public partial class ProtocolProxyEmitter
{
    /// <param name="applyVtableMembershipFilter">When true, gate every member through
    /// <see cref="ProtocolVtableMembers"/> so receivers are emitted ONLY for members
    /// that have a corresponding Swift vtable slot. Used for cross-module parent
    /// scaffolding where the per-protocol skip sets (<c>_skippedPropertyNames</c> et al.)
    /// are not populated for the parent — without this filter, the receiver loop would
    /// emit, for example, a property-setter receiver for a non-dispatchable closure
    /// property that has no Swift vtable slot to feed it AND a C# interface type that
    /// doesn't accept the receiver's raw function-pointer value (CS0029).</param>
    private void EmitReceiverMethods(CSharpWriter writer, ProtocolDecl protocolDecl, string interfaceName, bool applyVtableMembershipFilter = false)
    {
        writer.WriteLine("#region Swift Callback Receivers");
        writer.WriteLine();

        // Track emitted receivers to avoid duplicates
        var emittedReceivers = new HashSet<string>();
        // Always needed: the method loop gates layout on ProtocolVtableMembers.IncludesMethod on
        // BOTH paths (matching the struct), and IncludesMethod needs a ClosureHandler. The
        // property/subscript loops below still gate on the flag (their same-module fillability is
        // the skip sets, not the layout predicate).
        var closureHandler = new ClosureHandler(_typeDatabase);

        // Property receivers (skip static properties - they're not part of the interface)
        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic)
                continue;
            // Skip receivers for properties that the interface skipped due to AnyType generic args
            if (_skippedPropertyNames.Contains(property.Name))
                continue;
            if (applyVtableMembershipFilter && !ProtocolVtableMembers.IncludesProperty(property, protocolDecl, closureHandler!))
                continue;
            EmitPropertyReceivers(writer, property, protocolDecl, interfaceName, emittedReceivers);
        }

        // Subscript receivers (skip static subscripts - they're not part of the interface)
        int subscriptIndex = 0;
        foreach (var subscript in protocolDecl.Subscripts)
        {
            if (subscript.IsStatic)
                continue;
            // Skip receivers for subscripts that the interface skipped due to AnyType generic args
            if (_skippedSubscriptIndices.Contains(subscriptIndex))
            {
                subscriptIndex++;
                continue;
            }
            if (applyVtableMembershipFilter && !ProtocolVtableMembers.IncludesSubscript(subscript, protocolDecl, closureHandler))
            {
                subscriptIndex++;
                continue;
            }
            EmitSubscriptReceivers(writer, subscript, protocolDecl, interfaceName, subscriptIndex, emittedReceivers);
            subscriptIndex++;
        }

        // Method receivers. Index allocation MUST match the vtable struct (Vtables.cs): RAW producer
        // key (EveryProtocolEmitter.GetMethodKey), index consumed for every distinct raw method, and
        // the layout predicate (IncludesMethod) applied UNCONDITIONALLY so a receiver is emitted only
        // for a method that actually has a Swift vtable slot. Three fillability filters then layer on
        // top — they leave a slot Swift KEEPS at null rather than shrinking the layout:
        //   • _skippedMethodKeys — an AnyType-unprojectable member has no C# surface to forward to.
        //   • the raw-signature collapse — the interface emits exactly ONE method per raw Swift
        //     signature key, so two overloads that collapse to one raw key but project to DISTINCT
        //     C# methods (add(any Expression)→Add(IExpression) / add(any Swift.Sendable)→Add(object))
        //     get a single receiver for the first (surviving) overload; the second's slot is left
        //     null. Without this the orphan receiver would dispatch to a non-existent C# overload.
        //   • the projected-C# collapse — two raw-distinct overloads projecting to one C# method
        //     (consume(any A)/consume(any B) → Consume(object)) share a single receiver; the
        //     collapsed-duplicate slot is left null (same fillability model as AnyType members and
        //     the inout-ObjC deferred slot — see EveryProtocolEmitter.EmitProtocolVtableStruct).
        //     A C# conformer therefore cannot today reverse-dispatch the SECOND collapsed overload;
        //     that is a known fillability limitation, not a layout defect — the struct is correctly
        //     sized and forward dispatch + the first overload work.
        // Slot INDEX comes from the shared VtableLayout model (the SAME ordered list the vtable struct
        // renders), so a receiver's index can never drift from its struct field (Bug #21). The three
        // fillability filters below — _skippedMethodKeys, the raw-signature collapse, the projected-C#
        // collapse — are UNCHANGED: they leave a Swift-kept slot null rather than re-deriving the index.
        var methodSlotIndices = new VtableLayoutBuilder(_typeDatabase).Build(protocolDecl).MethodSlotIndexByKey;
        var methodIndices = new Dictionary<string, int>();
        var emittedRawKeys = new HashSet<string>();
        var emittedCSharpKeys = new HashSet<string>();
        foreach (var method in protocolDecl.Methods)
        {
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;
            // @objc optional methods get no reverse-dispatch slot — the Swift producer skips
            // them BEFORE the index increment, so this receiver loop must not consume the slot
            // either, or the following required method's trampoline lands in the wrong field.
            if (method.IsObjCOptional)
                continue;

            var slotKey = EveryProtocolEmitter.GetMethodKey(method);
            if (!methodIndices.TryGetValue(slotKey, out var idx))
            {
                idx = methodSlotIndices[slotKey];
                methodIndices[slotKey] = idx;
                // LAYOUT: no Swift vtable slot (non-dispatchable closure / method-generic /
                // Self-typed / mixed-generic) → no receiver. Index already consumed.
                if (!ProtocolVtableMembers.IncludesMethod(method, protocolDecl, closureHandler))
                    continue;
                // FILLABILITY: skip-set membership is keyed on the projected/collapsing key.
                // EffectiveRawKey mirrors ProtocolHandler: a label-only-overload sibling keys on its
                // label-INCLUSIVE slot key here (so both siblings get a receiver), every other method on
                // the unchanged label-erased signature key — matching what populated _skippedMethodKeys.
                var collapsingKey = ProtocolMethodDisambiguator.EffectiveRawKey(method, protocolDecl, _typeDatabase);
                if (_skippedMethodKeys.Contains(collapsingKey))
                    continue;
                // RAW-SIGNATURE DEDUP: the interface emits exactly ONE method per raw Swift
                // signature key (ProtocolHandler dedups on GetMethodSignatureKey — e.g. an
                // `add(any Expression)` / `add(any Swift.Sendable)` overload pair both collapse to
                // one raw key and only the first survives as `Add(IExpression)`). Mirror that here:
                // the surviving interface method is the FIRST by declaration order, so keep its
                // receiver and skip later same-raw-key overloads. Without this, the second overload
                // — which projects to a DISTINCT C# key (`object`) and so slips past the
                // projected-key collapse below — would emit a receiver dispatching to a non-existent
                // `Add(object)` overload (CS1503). The vtable index is already consumed above, so the
                // skipped slot is correctly left null (the documented fillability model).
                if (!emittedRawKeys.Add(collapsingKey))
                    continue;
                var projectedKey = ProtocolMethodDisambiguator.EffectiveProjectedKey(method, protocolDecl, _typeDatabase, propertyNames: null);
                if (!emittedCSharpKeys.Add(projectedKey))
                    continue;
                // Only emit receiver for new methods
                EmitMethodReceiver(writer, method, protocolDecl, interfaceName, idx, emittedReceivers);
            }
        }

        writer.WriteLine("#endregion");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits one reverse-dispatch receiver via <paramref name="emitBody"/>, falling back to a degraded
    /// fail-fast stub if the receiver's existential payload references a protocol proxy whose
    /// EveryProtocol conformance was suppressed (<see cref="SuppressedProxyReferenceException"/>) — the
    /// proxy class does not exist, so the value cannot be marshalled across the boundary. Uses the writer
    /// checkpoint primitive so a partially-written body is erased byte-for-byte before the stub replaces
    /// it (the canonical in-emission recovery — mirrors <c>WrapperEmitter</c>'s PRODUCE-path proxy gate).
    /// The stub keeps the receiver's <paramref name="receiverName"/> symbol and exact signature so the
    /// vtable static-init that address-takes <c>&amp;Receive_*</c> still resolves (a missing symbol is
    /// CS0103); only the body is replaced. Without this, a suppressed-proxy existential on ANY receiver
    /// channel propagated uncaught to <c>StringEmitter.EmitModule</c> and aborted the WHOLE module with
    /// no <c>.cs</c> produced.
    /// </summary>
    // internal (not private) so the degrade path can be exercised in isolation: a unit test injects an
    // emitBody that throws SuppressedProxyReferenceException and asserts the recorded report row names the
    // proxy carried on the exception — the regression lock for the catch → ex.ProxyClassName → RecordReceiver
    // wiring that an API-level RecordReceiver test cannot cover.
    /// <returns>
    /// <c>true</c> when the body threw (a PRODUCE arm hit a suppressed proxy) and was replaced by the
    /// fail-fast stub; <c>false</c> when the body emitted cleanly. Callers use this to decide whether to
    /// ALSO record the getter/return's CONSUME degrade: a member already fail-fast-stubbed must not be
    /// re-recorded as a consume-degrade (its return is never marshalled), so
    /// <see cref="RecordReceiverGetterConsumeDegrade"/> runs only on a <c>false</c> return.
    /// </returns>
    internal bool EmitReceiverOrDegrade(
        CSharpWriter writer, string returnType, string receiverName, string paramList,
        string memberDescriptor, Action emitBody)
    {
        var checkpoint = writer.Checkpoint();
        try
        {
            emitBody();
            return false;
        }
        catch (SuppressedProxyReferenceException ex)
        {
            writer.RollbackTo(checkpoint);
            EmitSuppressedProxyReceiverStub(writer, returnType, receiverName, paramList, memberDescriptor, ex.ProxyClassName);
            return true;
        }
    }

    /// <summary>
    /// Records a CONSUME degrade for a reverse-dispatch receiver GETTER/return whose existential value
    /// (or collection element) references a suppressed proxy. Unlike the setter/parameter receiver — which
    /// THROWS <see cref="SuppressedProxyReferenceException"/> and fail-fasts (recorded by
    /// <see cref="EmitSuppressedProxyReceiverStub"/>) — the getter marshals C#→Swift through the CONSUME
    /// arm (<c>GetOwnedParameterElementConversion</c> / <c>GetArrayElementCarrierConversion</c>), which
    /// silently DROPS the wrap fallback: a Swift-vended conformer still round-trips, but a C#-authored one
    /// cannot be handed back. The projection is rebuilt read-only here (<c>IsParameter: true</c>, matching
    /// the getter's C#→Swift direction) and the walk collects the suppressed-proxy leaves; it emits no C#.
    /// Call ONLY when the receiver did not already degrade to a fail-fast stub (see
    /// <see cref="EmitReceiverOrDegrade"/>'s return), so a member that fail-fasts on a suppressed PARAM is
    /// not also mis-recorded here.
    /// </summary>
    private void RecordReceiverGetterConsumeDegrade(string memberDescriptor, TypeSpec? valueTypeSpec)
    {
        if (valueTypeSpec == null)
            return;
        var projection = s_projectionFactory.Project(valueTypeSpec,
            new ProjectionContext { TypeDatabase = _typeDatabase, IsParameter = true, CurrentModuleName = _moduleName, EmissionContext = _emissionContext });
        foreach (var proxyName in SuppressedProxyProjectionWalk.CollectSuppressedProxyNames(projection))
            SuppressedProxyReporting.RecordReceiver(memberDescriptor, SuppressedProxyReporting.Site.ConsumeDegraded, proxyName);
    }

    /// <summary>
    /// Emits a degraded EveryProtocol reverse-dispatch receiver: the same
    /// <c>[UnmanagedCallersOnly]</c> signature the live path would emit, but a fail-fast body in place of
    /// the marshalling that could not be projected. Reached when a receiver's existential payload
    /// references a suppressed protocol proxy. Fail-fast — not a managed throw — because the receiver is
    /// the native <c>UnmanagedCallersOnly</c> entry (a managed exception cannot unwind across it) and
    /// fabricating a zero value would silently corrupt the boundary. Records the degradation so
    /// <c>EmissionReportEmitter.Emit</c> surfaces one SWIFTBIND061 warning per affected member.
    /// <para>The body routes through the same <see cref="EmitUcoGuardOpen"/>/<see cref="EmitUcoGuardCloseFailFast"/>
    /// envelope every live receiver uses, so this degraded stub carries the identical try/catch — the
    /// corpus invariant that no <c>[UnmanagedCallersOnly]</c> body lets a managed exception unwind across
    /// the Swift boundary (enforced by <c>CatchFreeUcoValidatorTests</c>). The member-named
    /// <c>FailFastSuppressedProxyReceiver</c> call is the real terminal (it <c>FailFast</c>s first); its
    /// <c>throw</c> only satisfies CS0161 for value-returning receivers and the guard's catch is likewise
    /// unreachable.</para>
    /// </summary>
    private void EmitSuppressedProxyReceiverStub(
        CSharpWriter writer, string returnType, string receiverName, string paramList, string memberDescriptor,
        string? proxyClassName)
    {
        _emissionContext.TryRecordDegradedReverseDispatchReceiver(memberDescriptor);
        // Promote the build-only SWIFTBIND061 warning to a durable, classified skip in the persisted
        // report — the receiver's reverse-dispatch surface is degraded (fail-fast), and a decline must
        // not live only as a log line. Report-layer identity dedup keeps distinct overloads distinct.
        // Pass the suppressed proxy's name (carried on the exception) so the row names the exact protocol,
        // matching every other Record* site and keeping the row greppable by protocol during triage.
        SuppressedProxyReporting.RecordReceiver(memberDescriptor, proxyClassName);
        writer.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        writer.WriteLine($"private static {returnType} {receiverName}({paramList})");
        writer.WriteLine("{");
        writer.Indent++;
        EmitUcoGuardOpen(writer);
        writer.WriteLine($"throw global::Swift.Runtime.SwiftClosureMarshaller.FailFastSuppressedProxyReceiver(\"{memberDescriptor}\");");
        EmitUcoGuardCloseFailFast(writer);
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Renders a parenthesised Swift parameter signature (<c>label: Type</c> per parameter) for a
    /// degraded-receiver descriptor over <paramref name="paramDecls"/>. Two overloaded methods — or two
    /// subscripts with different index sets — that BOTH degrade otherwise collide on a bare
    /// <c>"{Protocol}.{name}()"</c> / <c>"{Protocol} subscript getter"</c> descriptor, so the
    /// dedup set in <c>ModuleEmissionContext</c> folds them into one recorded slot and SWIFTBIND061
    /// under-counts the degraded receivers. Appending the labels AND types keeps each degraded slot a
    /// distinct entry (and the runtime fail-fast message names the exact overload). Argument labels are
    /// included — not just types — because Swift permits overloads that differ ONLY by label (e.g.
    /// <c>handle(foo: any P)</c> vs <c>handle(bar: any P)</c>), which a type-only descriptor would still
    /// fold together. Label rendering mirrors the canonical label-inclusive
    /// <c>EveryProtocolEmitter.GetMethodKey</c> via <c>GetSwiftName()</c>, except an unlabeled subscript
    /// index renders <c>_</c> off the authoritative <see cref="ArgumentDecl.IsUnlabeledSubscriptIndex"/>
    /// flag rather than its synthetic <c>index{i}</c> name (a real label could literally be <c>index0</c>).
    /// </summary>
    private static string RenderReceiverParamSignature(IEnumerable<ArgumentDecl> paramDecls)
    {
        return "(" + string.Join(", ", paramDecls.Select(p =>
        {
            var label = p.IsUnlabeledSubscriptIndex ? "_" : (p.GetSwiftName() ?? p.Name ?? "_");
            return label + ": " + (p.SwiftTypeSpec?.ToString() ?? "_");
        })) + ")";
    }

    private void EmitPropertyReceivers(CSharpWriter writer, PropertyDecl property, ProtocolDecl protocolDecl, string interfaceName, HashSet<string> emittedReceivers)
    {
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();
        var proxyClassName = GetProxyClassName(protocolDecl);

        // Dispatchable closure properties take a dedicated receiver shape — setter
        // accepts (fnPtr, ctxPtr) IntPtr pair, getter returns a 16-byte buffer
        // containing (fnPtr, ctxPtr). Skip the value-shaped emission below.
        var closureHandlerForProp = new ClosureHandler(_typeDatabase);
        if (EveryProtocolEmitter.IsDispatchableClosureProperty(property, closureHandlerForProp))
        {
            EmitDispatchableClosurePropertyReceivers(writer, property, protocolDecl, proxyClassName, interfaceName, closureHandlerForProp, emittedReceivers);
            return;
        }

        // P0: Use ABI type for MarshalFromSwift (setter reads Swift memory layout),
        // not the idiomatic type used for signatures.
        var abiTypeName = GetCSharpTypeName(property.SwiftTypeSpec, forAbiMarshalling: true);

        var pascalPropertyName = NameProvider.GetPropertyName(property.Name);

        // Sibling-property fallback: when this property is part of a sibling group (two or
        // more class-bound protocols declaring the same property name+type with differing
        // accessor sets), the Swift fan-out picks ANY populated sibling vtable, not the one
        // matching the proxy actually registered for this EveryProtocol instance. Without
        // a per-instance fallback in the receiver, a smaller-sibling proxy whose vtable was
        // skipped by the fan-out would silently return "" / no-op. We emit additional
        // lookups across the sibling interfaces (filtered by setter-presence for the setter
        // receiver) so whichever vtable Swift picks correctly locates the proxy.
        // See EveryProtocolEmitter.ComputeSiblingPropertyFallbacks and the receiver-fallback
        // helper EmitGetterFallbackLookups / EmitSetterFallbackLookups below.
        var protoQName = EveryProtocolEmitter.GetProtocolFallbackKey(protocolDecl);
        var siblingFallbacks = _emissionContext.GetSiblingPropertyFallbacks(protoQName, property.Name);

        if (hasGetter)
        {
            var receiverName = $"Receive_{property.Name}_get";
            if (emittedReceivers.Add(receiverName))
            {
                var getterDescriptor = $"{protocolDecl.Name}.{property.Name} getter";
                var degraded = EmitReceiverOrDegrade(writer, "IntPtr", receiverName, "IntPtr vtHandle, IntPtr selfContainer",
                    getterDescriptor, () =>
                {
                    // The interface property uses idiomatic C# types (e.g., string, string?, IReadOnlyList<string>)
                    // but MarshalToSwiftBuffer expects Swift ABI types (SwiftString, SwiftOptional<SwiftString>, etc.).
                    // Projection-based conversion handles existentials, strings, arrays, dicts, and optionals.
                    var getterConversion = GetReceiverGetterConversion("result", property.SwiftTypeSpec);
                    // F1: If property is narrowed (int/uint), widen back to nint/nuint for Swift ABI MarshalToSwiftBuffer.
                    // Plain nint: result is int → (nint)result ensures 8-byte write.
                    // Plain nuint: result is uint → (nuint)result ensures 8-byte write.
                    // Optional<nint/nuint>: getterConversion builds SwiftOptional<nint>.NewSome(resultVal) where
                    //   resultVal is int/uint (unwrapped from int?/uint?) — implicit widening handles it.
                    if (getterConversion == null && NativeIntOverloadEmitter.TryGetAbiWideningType(property.SwiftTypeSpec, out var abiType))
                        getterConversion = $"({abiType})result";

                    // String returns use Utf8Slice encoding to avoid ARC issues with MarshalToSwiftBuffer<SwiftString>.
                    // SwiftString contains ARC-managed references that Unsafe.Write can't retain properly,
                    // causing crashes when Swift reads the result. Utf8Slice passes raw bytes safely.
                    bool isStringReturn = IsStringTypeSpec(property.SwiftTypeSpec);

                    writer.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                    writer.WriteLine($"private static IntPtr {receiverName}(IntPtr vtHandle, IntPtr selfContainer)");
                    writer.WriteLine("{");
                    writer.Indent++;
                    EmitUcoGuardOpen(writer);
                    // Read only the first existential word: this is the proxy handle (Payload0),
                    // the sole field TryGetProxy actually uses. Avoids the 5-word over-read of
                    // *(ExistentialContainer1*)selfContainer, which over-reads stack memory when
                    // Swift passes a class-bound (2-word) existential for EveryObjCProtocol.
                    writer.WriteLine("var handle = *(IntPtr*)selfContainer;");
                    // Both branches resolve the C# impl from ProxyLifetimeTracker's strong root
                    // (Design B2): the no-sibling path via EmitResolveImplOrFailFast, the sibling
                    // fan-out path via per-interface lookups. When every proxy misses, the impl was
                    // collected while Swift still held the proxy — a lifetime-invariant violation —
                    // so the terminal FailFasts (EmitSiblingFanOutFailFast) rather than fabricating a
                    // zero/empty buffer (Defect G's silent data-corruption failure mode).
                    if (siblingFallbacks == null || siblingFallbacks.Count == 0)
                    {
                        EmitResolveImplOrFailFast(writer, interfaceName, protocolDecl, $"{pascalPropertyName} getter");
                        writer.WriteLine($"var result = impl.{pascalPropertyName};");
                        if (isStringReturn)
                        {
                            writer.WriteLine("return MarshalStringToUtf8Slice(result);");
                        }
                        else if (getterConversion != null)
                        {
                            writer.WriteLine($"var swiftResult = {getterConversion};");
                            writer.WriteLine("return MarshalToSwiftBuffer(swiftResult);");
                        }
                        else
                        {
                            writer.WriteLine("return MarshalToSwiftBuffer(result);");
                        }
                    }
                    else
                    {
                        EmitGetterLookupHit(writer, interfaceName, "primary", pascalPropertyName, getterConversion, isStringReturn);
                        int siblingIdx = 0;
                        foreach (var sibling in siblingFallbacks)
                        {
                            var siblingIface = GetQualifiedInterfaceName(sibling.Proto);
                            EmitGetterLookupHit(writer, siblingIface, $"s{siblingIdx}", pascalPropertyName, getterConversion, isStringReturn);
                            siblingIdx++;
                        }
                        EmitSiblingFanOutFailFast(writer, protocolDecl, $"{pascalPropertyName} getter");
                    }
                    EmitUcoGuardCloseFailFast(writer);
                    writer.Indent--;
                    writer.WriteLine("}");
                    writer.WriteLine();
                });
                // Reverse-dispatch getter CONSUME degrade: the getter hands `result` back to Swift via the
                // silent-drop CONSUME arm (no throw → not a fail-fast stub), so record it here when the body
                // emitted cleanly. A suppressed proxy on the getter's value type surfaces one classified row.
                if (!degraded)
                    RecordReceiverGetterConsumeDegrade(getterDescriptor, property.SwiftTypeSpec);
            }
        }

        if (hasSetter)
        {
            var receiverName = $"Receive_{property.Name}_set";
            if (emittedReceivers.Add(receiverName))
            {
                EmitReceiverOrDegrade(writer, "void", receiverName, "IntPtr vtHandle, IntPtr selfContainer, IntPtr valuePtr",
                    $"{protocolDecl.Name}.{property.Name} setter", () =>
                {
                    // Issue #40: a Swift-class (or Optional<class>) value arrives as the address of a
                    // borrowed slot holding the heap pointer (&valueCopy). The runtime copy-out helper returns
                    // the wrapper (or null) directly, so the marshalled value IS the assignment value — no
                    // idiomatic cast (which would re-wrap and, for the optional, false-trip on Unsafe.Read).
                    var classCopyOut = GetReceiverClassCopyOutExpr("valuePtr", property.SwiftTypeSpec);

                    // Optional ObjC-bridgeable VALUE setter (URL?): the Swift thunk borrows the bridged
                    // NSObject and passes one optional ObjC pointer word (nil = 0x0), so read a bare IntPtr
                    // and +0-bridge it — NOT the default two-word SwiftOptional<IntPtr> carrier, which would
                    // reinterpret the value's storage bytes as a pointer. Own the marshal read AND the
                    // assignment together (coupled) and short-circuit the default conversion below — mirror
                    // of the reverse-RETURN optional-bridgeable arm.
                    // Call unconditionally so the out-vars are definitely assigned; class copy-out still wins.
                    bool objcOptApplies = TryGetReceiverOptionalObjCBridgeableValueRead(property.SwiftTypeSpec, "valuePtr", "value", out var objcOptMarshal, out var objcOptConv);
                    bool isObjCOptRead = classCopyOut == null && objcOptApplies;

                    // Check if the property type needs conversion (e.g., SwiftOptional<SwiftString> → string?)
                    // The receiver marshals the Swift ABI type, but the interface uses the idiomatic C# type.
                    // Projection-based conversion handles existentials, strings, arrays, dicts, and optionals.
                    var returnConversion = (classCopyOut != null || isObjCOptRead) ? null : GetReceiverSetterConversion("value", property.SwiftTypeSpec);
                    var assignmentExpr = isObjCOptRead ? objcOptConv : (returnConversion ?? "value");
                    // F1: Narrow nint/nuint ABI value to int/uint for property assignment.
                    // Plain nint: value is nint (MarshalFromSwift<nint>) → (int)value.
                    // Optional<nint>: returnConversion is "((nint?)value)" → (int?)((nint?)value).
                    if (!isObjCOptRead && classCopyOut == null && NativeIntOverloadEmitter.TryGetNarrowedType(property.SwiftTypeSpec, out var narrowedType))
                        assignmentExpr = $"({narrowedType}){assignmentExpr}";

                    // String property: local MarshalFromSwift<SwiftString> uses Unsafe.Read which
                    // can't construct a managed SwiftString from raw Swift memory. Use runtime marshaller.
                    // Reference-backed collection wrappers (SwiftArray/SwiftDictionary/SwiftSet) hit the same
                    // Unsafe.Read-on-a-managed-ref hazard and route through GetReceiverRawMaterialization.
                    var marshalExpr = isObjCOptRead
                        ? objcOptMarshal
                        : classCopyOut
                        ?? (IsStringTypeSpec(property.SwiftTypeSpec)
                            ? $"global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwiftObject<Swift.SwiftString>(valuePtr)"
                            : GetReceiverRawMaterialization(abiTypeName, "valuePtr", property.SwiftTypeSpec));

                    var setterSiblings = siblingFallbacks?.Where(s => s.HasSetter).ToList();
                    if (setterSiblings == null || setterSiblings.Count == 0)
                    {
                        writer.WriteLines($$"""
                            [global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
                            private static void {{receiverName}}(IntPtr vtHandle, IntPtr selfContainer, IntPtr valuePtr)
                            {
                                try
                                {
                                    var handle = *(IntPtr*)selfContainer;
                                    // Design B2: resolve the impl from ProxyLifetimeTracker's strong root.
                                    // A null resolve means the impl was collected while Swift still held the
                                    // proxy — a lifetime-invariant violation — so trip the loud backstop
                                    // rather than silently dropping the write.
                                    var impl = Swift.Runtime.ProxyLifetimeTracker.ResolveImpl<{{interfaceName}}>(handle);
                                    if (impl is null)
                                        throw global::Swift.Runtime.SwiftClosureMarshaller.FailFastDeadProxyImpl("Swift reverse-dispatch on {{protocolDecl.Name}}.{{pascalPropertyName}} setter resolved no live C# implementation for EveryProtocol handle 0x" + handle.ToString("X") + ". The implementation was collected while Swift still held the proxy — a Design B2 lifetime-invariant violation (see ProxyLifetimeTracker).");
                                    var value = {{marshalExpr}};
                                    impl.{{pascalPropertyName}} = {{assignmentExpr}};
                                }
                                catch (global::System.Exception __uco_ex)
                                {
                                    global::Swift.Runtime.SwiftClosureMarshaller.FailFastUnhandledClosureException(__uco_ex);
                                    throw;
                                }
                            }

                            """);
                    }
                    else
                    {
                        writer.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                        writer.WriteLine($"private static void {receiverName}(IntPtr vtHandle, IntPtr selfContainer, IntPtr valuePtr)");
                        writer.WriteLine("{");
                        writer.Indent++;
                        EmitUcoGuardOpen(writer);
                        writer.WriteLine("var handle = *(IntPtr*)selfContainer;");
                        writer.WriteLine($"var value = {marshalExpr};");
                        EmitSetterLookupHit(writer, interfaceName, "primary", pascalPropertyName, assignmentExpr);
                        int siblingIdx = 0;
                        foreach (var sibling in setterSiblings)
                        {
                            var siblingIface = GetQualifiedInterfaceName(sibling.Proto);
                            EmitSetterLookupHit(writer, siblingIface, $"s{siblingIdx}", pascalPropertyName, assignmentExpr);
                            siblingIdx++;
                        }
                        EmitUcoGuardCloseFailFast(writer);
                        writer.Indent--;
                        writer.WriteLine("}");
                        writer.WriteLine();
                    }
                });
            }
        }
    }

    /// <summary>
    /// Emits the Swift-callback receivers for a dispatchable closure property.
    /// Setter receiver wraps (fnPtr, ctxPtr) into a managed
    /// <c>Action</c> via <see cref="SwiftEscapingClosure{TDelegate}"/> and assigns
    /// it onto the impl. Getter receiver decomposes the user-supplied delegate
    /// stored on the impl back into a (fnPtr, ctxPtr) pair Swift can re-wrap into
    /// a Swift closure value. Currently restricted to <c>() -&gt; Void</c> shape
    /// (enforced by <see cref="EveryProtocolEmitter.IsDispatchableClosureProperty"/>).
    /// </summary>
    private void EmitDispatchableClosurePropertyReceivers(CSharpWriter writer, PropertyDecl property,
        ProtocolDecl protocolDecl, string proxyClassName, string interfaceName, ClosureHandler closureHandler,
        HashSet<string> emittedReceivers)
    {
        if (!EveryProtocolEmitter.TryGetDispatchableClosureParam(property.SwiftTypeSpec, closureHandler, out var closure, out var isOptional) || closure is null)
            throw new InvalidOperationException($"EmitDispatchableClosurePropertyReceivers called on non-dispatchable property '{property.Name}'.");

        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();
        var pascalPropertyName = NameProvider.GetPropertyName(property.Name);
        var delegateType = closureHandler.GetCSharpDelegateType(closure);
        var nullableDelegateType = isOptional ? $"{delegateType}?" : delegateType;

        // Sibling fallback applies the same shape as value-typed properties — see
        // EmitPropertyReceivers above. Closure properties have a different value
        // shape (16-byte (fnPtr, ctxPtr) buffer / set takes the pair) but the
        // proxy-lookup-fan-out logic is identical: any populated sibling vtable
        // can route to this receiver, so we must try each sibling's
        // IProtocolProxyImpl<T> after the primary.
        var protoQName = EveryProtocolEmitter.GetProtocolFallbackKey(protocolDecl);
        var siblingFallbacks = _emissionContext.GetSiblingPropertyFallbacks(protoQName, property.Name);
        var setterSiblings = siblingFallbacks?.Where(s => s.HasSetter).ToList();

        // Per-(protocol, property) C# cdecl thunk + invoker class — fired from Swift when
        // the materialised getter closure is called. ctx is the GCHandle.ToIntPtr of the
        // user-supplied delegate stored on impl.<PascalProp>; the box wrapping it on the
        // Swift side guarantees release via the SwiftClosureContext destroy callback.
        var getterThunkName = $"_PropClosureThunk_{property.Name}";

        if (hasSetter)
        {
            var receiverName = $"Receive_{property.Name}_set";
            if (emittedReceivers.Add(receiverName))
            {
                var entryPoint = EveryProtocolEmitter.GetProtocolClosurePropertyInvokeThunkEntryPoint(protocolDecl, property);
                var helperName = EveryProtocolEmitter.GetProtocolClosureInvokeThunkHelperName(entryPoint);
                var invokerClassName = ClosureEmitter.GetInvokerClassName(helperName);

                writer.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                writer.WriteLine($"private static void {receiverName}(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawFn, IntPtr rawCtx)");
                writer.WriteLine("{");
                writer.Indent++;
                EmitUcoGuardOpen(writer);
                writer.WriteLine("var handle = *(IntPtr*)selfContainer;");
                if (setterSiblings == null || setterSiblings.Count == 0)
                {
                    EmitResolveImplOrFailFast(writer, interfaceName, protocolDecl, $"{pascalPropertyName} setter");
                    EmitClosureSetterBody(writer, isOptional, pascalPropertyName, delegateType, invokerClassName, implVar: "impl");
                }
                else
                {
                    EmitClosureSetterLookupHit(writer, interfaceName, "primary", isOptional, pascalPropertyName, delegateType, invokerClassName);
                    int idx = 0;
                    foreach (var sibling in setterSiblings)
                    {
                        var siblingIface = GetQualifiedInterfaceName(sibling.Proto);
                        EmitClosureSetterLookupHit(writer, siblingIface, $"s{idx}", isOptional, pascalPropertyName, delegateType, invokerClassName);
                        idx++;
                    }
                }
                EmitUcoGuardCloseFailFast(writer);
                writer.Indent--;
                writer.WriteLine("}");
                writer.WriteLine();
            }
        }

        if (hasGetter)
        {
            var receiverName = $"Receive_{property.Name}_get";
            if (emittedReceivers.Add(receiverName))
            {
                writer.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                writer.WriteLine($"private static IntPtr {receiverName}(IntPtr vtHandle, IntPtr selfContainer)");
                writer.WriteLine("{");
                writer.Indent++;
                EmitUcoGuardOpen(writer);
                writer.WriteLine("var handle = *(IntPtr*)selfContainer;");
                // 16-byte buffer carrying (fnPtr, ctxPtr). Allocate up-front so every exit
                // path returns the same shape.
                writer.WriteLine("var buf = (IntPtr)NativeMemory.AllocZeroed(16);");
                if (siblingFallbacks == null || siblingFallbacks.Count == 0)
                {
                    EmitResolveImplOrFailFast(writer, interfaceName, protocolDecl, $"{pascalPropertyName} getter");
                    EmitClosureGetterBody(writer, pascalPropertyName, nullableDelegateType, getterThunkName, implVar: "impl");
                    writer.WriteLine("return buf;");
                }
                else
                {
                    EmitClosureGetterLookupHit(writer, interfaceName, "primary", pascalPropertyName, nullableDelegateType, getterThunkName);
                    int idx = 0;
                    foreach (var sibling in siblingFallbacks)
                    {
                        var siblingIface = GetQualifiedInterfaceName(sibling.Proto);
                        EmitClosureGetterLookupHit(writer, siblingIface, $"s{idx}", pascalPropertyName, nullableDelegateType, getterThunkName);
                        idx++;
                    }
                    writer.WriteLine("return buf;");
                }
                EmitUcoGuardCloseFailFast(writer);
                writer.Indent--;
                writer.WriteLine("}");
                writer.WriteLine();
            }
        }

        // Stable C# cdecl thunk used by the materialised Swift closure on the getter side.
        // ctx is the GCHandle.ToIntPtr of impl.<PascalProp>. Restricted to () -> Void by
        // EveryProtocolEmitter.IsDispatchableClosureProperty.
        if (hasGetter && emittedReceivers.Add(getterThunkName))
        {
            writer.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
            writer.WriteLine($"private static void {getterThunkName}(IntPtr ctx)");
            writer.WriteLine("{");
            writer.Indent++;
            EmitUcoGuardOpen(writer);
            writer.WriteLine("if (ctx == IntPtr.Zero) return;");
            writer.WriteLine("var handle = global::System.Runtime.InteropServices.GCHandle.FromIntPtr(ctx);");
            writer.WriteLine("if (!handle.IsAllocated) return;");
            writer.WriteLine($"if (handle.Target is {delegateType} _del)");
            writer.Indent++;
            writer.WriteLine("_del();");
            writer.Indent--;
            EmitUcoGuardCloseFailFast(writer);
            writer.Indent--;
            writer.WriteLine("}");
            writer.WriteLine();
        }
    }

    /// <summary>
    /// Emits the Swift-callback receiver for a closure-returning protocol method.
    /// Receiver takes only <c>(vtHandle, selfContainer)</c>, calls the
    /// C# impl's parameterless method to obtain a managed delegate, and returns a
    /// 16-byte buffer carrying (fnPtr, GCHandle) so Swift can wrap the pair into a
    /// real <c>() -&gt; Void</c> closure. Currently restricted to <c>() -&gt; Void</c>
    /// returns (enforced by <see cref="EveryProtocolEmitter.IsDispatchableClosureReturningMethod"/>).
    /// </summary>
    private void EmitDispatchableClosureReturningMethodReceiver(CSharpWriter writer, MethodDecl method,
        ProtocolDecl protocolDecl, string proxyClassName, string interfaceName, int index, ClosureHandler closureHandler)
    {
        if (method.CSSignature.FirstOrDefault()?.SwiftTypeSpec is not ClosureTypeSpec retClosure)
            throw new InvalidOperationException(
                $"EmitDispatchableClosureReturningMethodReceiver called on method '{method.Name}' without a closure return type.");

        var receiverName = $"Receive_{method.Name}_{index}";
        var pascalMethodName = NameProvider.GetMethodName(ProtocolMethodDisambiguator.EffectiveNameInput(method, protocolDecl, _typeDatabase), propertyNames: null);
        var delegateType = closureHandler.GetCSharpDelegateType(retClosure);
        var returnedThunkName = $"_MethodClosureThunk_{method.Name}_{index}";

        writer.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        writer.WriteLine($"private static IntPtr {receiverName}(IntPtr vtHandle, IntPtr selfContainer)");
        writer.WriteLine("{");
        writer.Indent++;
        EmitUcoGuardOpen(writer);
        writer.WriteLine("var handle = *(IntPtr*)selfContainer;");
        // 16-byte buffer carrying (fnPtr, ctxPtr). Allocate up-front so every exit
        // path returns the same shape (mirrors Shape 3's getter).
        writer.WriteLine("var buf = (IntPtr)NativeMemory.AllocZeroed(16);");
        EmitResolveImplOrFailFast(writer, interfaceName, protocolDecl, $"{pascalMethodName}()");
        writer.WriteLine($"{delegateType}? _del = impl.{pascalMethodName}();");
        writer.WriteLine("if (_del is null)");
        writer.Indent++;
        writer.WriteLine("return buf;");
        writer.Indent--;
        writer.WriteLine("var _gch = global::System.Runtime.InteropServices.GCHandle.Alloc(_del);");
        writer.WriteLine($"*(IntPtr*)buf = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&{returnedThunkName};");
        writer.WriteLine("*(IntPtr*)(buf + IntPtr.Size) = global::System.Runtime.InteropServices.GCHandle.ToIntPtr(_gch);");
        writer.WriteLine("return buf;");
        EmitUcoGuardCloseFailFast(writer);
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();

        // Stable C# cdecl thunk fired by the materialised Swift closure on the Swift
        // side. ctx is the GCHandle.ToIntPtr of the Action returned from impl.<Method>().
        // Restricted to () -> Void by IsDispatchableClosureReturningMethod.
        writer.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        writer.WriteLine($"private static void {returnedThunkName}(IntPtr ctx)");
        writer.WriteLine("{");
        writer.Indent++;
        EmitUcoGuardOpen(writer);
        writer.WriteLine("if (ctx == IntPtr.Zero) return;");
        writer.WriteLine("var handle = global::System.Runtime.InteropServices.GCHandle.FromIntPtr(ctx);");
        writer.WriteLine("if (!handle.IsAllocated) return;");
        writer.WriteLine($"if (handle.Target is {delegateType} _del)");
        writer.Indent++;
        writer.WriteLine("_del();");
        writer.Indent--;
        EmitUcoGuardCloseFailFast(writer);
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits the Swift-callback receiver for a protocol method whose single parameter
    /// is an <c>@escaping () async -&gt; Int32</c> closure. Receiver takes
    /// <c>(vtHandle, selfContainer, rawArg0_fn, rawArg0_ctx)</c>, wraps the (fnPtr, ctx) pair
    /// into a managed <c>Func&lt;Task&lt;int&gt;&gt;</c> via the per-(protocol, method) async invoker
    /// class, and invokes the C# impl with that delegate. The invoker bridges
    /// <c>InvokeAsync()</c> → Swift @_cdecl thunk → completion callback → TCS.SetResult.
    /// Currently restricted to <c>() async -&gt; Int32</c> (enforced by
    /// <see cref="EveryProtocolEmitter.IsDispatchableAsyncClosureMethod"/>).
    /// </summary>
    private void EmitDispatchableAsyncClosureMethodReceiver(CSharpWriter writer, MethodDecl method,
        ProtocolDecl protocolDecl, string proxyClassName, string interfaceName, int index, ClosureHandler closureHandler)
    {
        // Locate the single async-closure param (gate guarantees exactly one).
        ArgumentDecl? asyncParam = null;
        ClosureTypeSpec? asyncClosure = null;
        int argIdx = -1;
        foreach (var (param, idx, closure) in EveryProtocolEmitter.EnumerateDispatchableAsyncClosureParams(method, closureHandler))
        {
            asyncParam = param;
            asyncClosure = closure;
            argIdx = idx;
            break;
        }
        if (asyncParam is null || asyncClosure is null || argIdx < 0)
            throw new InvalidOperationException(
                $"EmitDispatchableAsyncClosureMethodReceiver called on method '{method.Name}' without an async closure parameter.");

        var receiverName = $"Receive_{method.Name}_{index}";
        var delegateType = closureHandler.GetCSharpDelegateType(asyncClosure);
        var entryPoint = EveryProtocolEmitter.GetProtocolAsyncClosureInvokeThunkEntryPoint(protocolDecl, method, index, argIdx);
        var helperName = EveryProtocolEmitter.GetProtocolAsyncClosureInvokeThunkHelperName(entryPoint);
        var invokerClassName = GetAsyncClosureInvokerClassName(helperName);

        // Mirror the property-collision rename used by the non-async receiver so the
        // impl method name lines up with what the interface emits.
        var protoQualifiedName = protocolDecl.SwiftTypeName?.ModuleQualifiedName
                               ?? $"{protocolDecl.ModuleDecl?.Name ?? "Unknown"}.{protocolDecl.Name}";
        var canonicalPropertyNames = _emissionContext.GetInterfacePropertyNames(protoQualifiedName);
        HashSet<string> receiverPropertyNames = canonicalPropertyNames != null
            ? new HashSet<string>(canonicalPropertyNames)
            : new HashSet<string>();
        var pascalMethodName = NameProvider.GetPublicMethodName(
            ProtocolMethodDisambiguator.EffectiveNameInput(method, protocolDecl, _typeDatabase), method.IsAsync, hasReturnValue: false,
            propertyNames: receiverPropertyNames,
            isSelfReturning: false,
            parameterCount: 1,
            isMutating: method.IsMutating);

        writer.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        writer.WriteLine($"private static void {receiverName}(IntPtr vtHandle, IntPtr selfContainer, IntPtr rawArg0_fn, IntPtr rawArg0_ctx)");
        writer.WriteLine("{");
        writer.Indent++;
        EmitUcoGuardOpen(writer);
        writer.WriteLine("var handle = *(IntPtr*)selfContainer;");
        EmitResolveImplOrFailFast(writer, interfaceName, protocolDecl, $"{pascalMethodName}()");
        // Wrap the (fnPtr, ctx) pair in SwiftEscapingClosure so Arc.Retain keeps the Swift
        // context alive across the async boundary; the invoker class holds the wrapper
        // reference for the duration of the impl call. Use a method-group bound to the
        // invoker (Mono-JIT-safe — no display class in the call chain).
        writer.WriteLine($"var _wrapper = SwiftEscapingClosure<{delegateType}>.FromSwift(rawArg0_fn, rawArg0_ctx);");
        writer.WriteLine($"var _inv = new {invokerClassName}((nint)_wrapper.FunctionPointer, (nint)_wrapper.Context, _wrapper);");
        writer.WriteLine($"{delegateType} handler = _inv.InvokeAsync;");
        writer.WriteLine($"impl.{pascalMethodName}(handler);");
        EmitUcoGuardCloseFailFast(writer);
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Derives the async closure invoker class name from the per-method helper name.
    /// Mirrors <see cref="ClosureEmitter.GetInvokerClassName"/> for the async path.
    /// </summary>
    internal static string GetAsyncClosureInvokerClassName(string helperMethodName)
    {
        return $"_AsyncClosureInv_{helperMethodName.Replace("_InvokeAsyncClosureThunk_", "")}";
    }

    private void EmitSubscriptReceivers(CSharpWriter writer, SubscriptDecl subscript, ProtocolDecl protocolDecl, string interfaceName, int index, HashSet<string> emittedReceivers)
    {
        var proxyClassName = GetProxyClassName(protocolDecl);
        // P0: Use ABI type for MarshalFromSwift (reads Swift memory layout)
        var returnTypeName = GetCSharpTypeName(subscript.ReturnTypeSpec, forAbiMarshalling: true);
        var paramCount = subscript.IndexParameters.Count;
        var indexArgs = string.Join(", ", Enumerable.Range(0, paramCount).Select(i => $"index{i}"));

        // Sibling-subscript fallback: same shape as the property sibling pipeline. When
        // this subscript participates in a sibling group, the Swift fan-out can pick any
        // populated sibling vtable, so each receiver tries its own interface first and then
        // falls back to the recorded sibling interfaces. See
        // EveryProtocolEmitter.ComputeSiblingSubscriptFallbacks.
        var protoQName = EveryProtocolEmitter.GetProtocolFallbackKey(protocolDecl);
        var subscriptKey = $"subscript_{index}(" +
            string.Join(",", subscript.IndexParameters.Select(p => p.SwiftTypeSpec?.ToString() ?? "")) + ")";
        var siblingFallbacks = _emissionContext.GetSiblingSubscriptFallbacks(protoQName, subscriptKey);

        if (subscript.HasGetter)
        {
            var receiverName = $"Receive_subscript_{index}_get";
            if (emittedReceivers.Add(receiverName))
            {
                // Build parameter list
                var paramTypes = "IntPtr vtHandle, IntPtr selfContainer" + string.Concat(
                    subscript.IndexParameters.Select((p, i) => $", IntPtr arg{i}"));

                var getterDescriptor = $"{protocolDecl.Name} subscript{RenderReceiverParamSignature(subscript.IndexParameters)} getter";
                var degraded = EmitReceiverOrDegrade(writer, "IntPtr", receiverName, paramTypes,
                    getterDescriptor, () =>
                {
                    writer.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                    writer.WriteLine($"private static IntPtr {receiverName}({paramTypes})");
                    writer.WriteLine("{");
                    writer.Indent++;
                    EmitUcoGuardOpen(writer);

                    writer.WriteLine("var handle = *(IntPtr*)selfContainer;");
                    // subscriptIsString / subscriptGetterConv are shared by the no-sibling path and
                    // every sibling lookup-hit below. The all-siblings-missed terminal no longer
                    // fabricates a fallback buffer: like the no-sibling Design B2 path it FailFasts
                    // (EmitSiblingFanOutFailFast), since an unresolved impl across all proxies is a
                    // lifetime-invariant violation, not a value to fake.
                    var subscriptIsString = IsStringTypeSpec(subscript.ReturnTypeSpec);
                    var subscriptGetterConv = GetReceiverExistentialGetterConversion("result", subscript.ReturnTypeSpec)
                        ?? GetReceiverGetterConversion("result", subscript.ReturnTypeSpec);

                    // Unmarshal index parameters once — same indexes used for every sibling lookup.
                    // P0: use ABI types for MarshalFromSwift.
                    for (int i = 0; i < subscript.IndexParameters.Count; i++)
                    {
                        var param = subscript.IndexParameters[i];
                        var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, forAbiMarshalling: true);
                        if (IsStringTypeSpec(param.SwiftTypeSpec))
                            writer.WriteLine($"var index{i} = global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwiftObject<Swift.SwiftString>(arg{i}).ToString();");
                        // Issue #40: a Swift-class index arrives as the address of a borrowed slot;
                        // copy it out (deref + ObjC-aware retain) instead of Unsafe.Read-ing the heap pointer.
                        else if (GetReceiverClassCopyOutExpr($"arg{i}", param.SwiftTypeSpec) is string indexClassCopyOut)
                            writer.WriteLine($"var index{i} = {indexClassCopyOut};");
                        else
                            writer.WriteLine($"var index{i} = {GetReceiverRawMaterialization(paramTypeName, $"arg{i}", param.SwiftTypeSpec)};");
                    }

                    if (siblingFallbacks == null || siblingFallbacks.Count == 0)
                    {
                        EmitResolveImplOrFailFast(writer, interfaceName, protocolDecl, "subscript getter");
                        writer.WriteLine($"var result = impl[{indexArgs}];");
                        // String returns use Utf8Slice encoding to avoid ARC issues with
                        // MarshalToSwiftBuffer<SwiftString> — same rationale as the property
                        // getter path: SwiftString contains ARC-managed references that
                        // Unsafe.Write can't retain properly, crashing the receiver when
                        // Swift reads the result.
                        if (subscriptIsString)
                        {
                            writer.WriteLine("return MarshalStringToUtf8Slice(result);");
                        }
                        else if (subscriptGetterConv != null)
                        {
                            writer.WriteLine($"var swiftResult = {subscriptGetterConv};");
                            writer.WriteLine("return MarshalToSwiftBuffer(swiftResult);");
                        }
                        else
                        {
                            writer.WriteLine("return MarshalToSwiftBuffer(result);");
                        }
                    }
                    else
                    {
                        EmitSubscriptGetterLookupHit(writer, interfaceName, "primary", indexArgs, subscript.ReturnTypeSpec, subscriptGetterConv, subscriptIsString);
                        int siblingIdx = 0;
                        foreach (var sibling in siblingFallbacks)
                        {
                            var siblingIface = GetQualifiedInterfaceName(sibling.Proto);
                            EmitSubscriptGetterLookupHit(writer, siblingIface, $"s{siblingIdx}", indexArgs, subscript.ReturnTypeSpec, subscriptGetterConv, subscriptIsString);
                            siblingIdx++;
                        }
                        EmitSiblingFanOutFailFast(writer, protocolDecl, "subscript getter");
                    }

                    EmitUcoGuardCloseFailFast(writer);
                    writer.Indent--;
                    writer.WriteLine("}");
                    writer.WriteLine();
                });
                // Reverse-dispatch subscript-getter CONSUME degrade: the value returns to Swift via the
                // silent-drop CONSUME arm (no throw → not a fail-fast stub), so record it when the body
                // emitted cleanly — the subscript twin of the property-getter gate above.
                if (!degraded)
                    RecordReceiverGetterConsumeDegrade(getterDescriptor, subscript.ReturnTypeSpec);
            }
        }

        if (subscript.HasSetter)
        {
            var receiverName = $"Receive_subscript_{index}_set";
            if (emittedReceivers.Add(receiverName))
            {
                // Build parameter list (includes value after self)
                var paramTypes = "IntPtr vtHandle, IntPtr selfContainer, IntPtr valuePtr" + string.Concat(
                    subscript.IndexParameters.Select((p, i) => $", IntPtr arg{i}"));

                EmitReceiverOrDegrade(writer, "void", receiverName, paramTypes,
                    $"{protocolDecl.Name} subscript{RenderReceiverParamSignature(subscript.IndexParameters)} setter", () =>
                {
                    writer.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
                    writer.WriteLine($"private static void {receiverName}({paramTypes})");
                    writer.WriteLine("{");
                    writer.Indent++;
                    EmitUcoGuardOpen(writer);

                    writer.WriteLine("var handle = *(IntPtr*)selfContainer;");

                    // Unmarshal value once — same value used for every sibling lookup.
                    if (IsStringTypeSpec(subscript.ReturnTypeSpec))
                    {
                        writer.WriteLine($"var value = global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwiftObject<Swift.SwiftString>(valuePtr).ToString();");
                    }
                    // Issue #40: a Swift-class (or Optional<class>) value arrives as the address of a
                    // borrowed slot; copy it out instead of Unsafe.Read-ing the heap pointer as a managed ref.
                    else if (GetReceiverClassCopyOutExpr("valuePtr", subscript.ReturnTypeSpec) is string valueClassCopyOut)
                    {
                        writer.WriteLine($"var value = {valueClassCopyOut};");
                    }
                    // Optional ObjC-bridgeable VALUE (URL?, NS_TYPED_ENUM newtypes): the Swift thunk
                    // passes one optional ObjC pointer word, so read a bare IntPtr and +0-bridge it.
                    // GetReceiverSetterConversion's contract requires this shape to be intercepted at
                    // the call site — its default arm fails closed rather than reinterpret the value's
                    // storage bytes as the two-word SwiftOptional<IntPtr> carrier. Ordered after the
                    // class copy-out arm above, matching the property setter's precedence.
                    else if (TryGetReceiverOptionalObjCBridgeableValueRead(
                        subscript.ReturnTypeSpec, "valuePtr", "rawValue",
                        out var subscriptObjCOptMarshal, out var subscriptObjCOptConv))
                    {
                        writer.WriteLine($"var rawValue = {subscriptObjCOptMarshal};");
                        writer.WriteLine($"var value = {subscriptObjCOptConv};");
                    }
                    else
                    {
                        // Project the incoming value through the SAME general converter this subscript's
                        // GETTER and the property setter already use (existential arm first, then the
                        // projection visitor). Consulting only the existential arm here left every other
                        // projected shape — Optional<String> the loudest — sitting in its raw ABI carrier
                        // while the interface slot expects the idiomatic type: the two accessors of one
                        // member disagreed on that member's projection, and the assignment could not
                        // compile. One converter for both accessors is what keeps them agreeing.
                        var subscriptSetterConv = GetReceiverSetterConversion("rawValue", subscript.ReturnTypeSpec);
                        if (subscriptSetterConv != null)
                        {
                            writer.WriteLine($"var rawValue = {GetReceiverRawMaterialization(returnTypeName, "valuePtr", subscript.ReturnTypeSpec)};");
                            writer.WriteLine($"var value = {subscriptSetterConv};");
                        }
                        else
                        {
                            writer.WriteLine($"var value = {GetReceiverRawMaterialization(returnTypeName, "valuePtr", subscript.ReturnTypeSpec)};");
                        }
                    }

                    // Unmarshal index parameters — P0: use ABI types for MarshalFromSwift
                    for (int i = 0; i < subscript.IndexParameters.Count; i++)
                    {
                        var param = subscript.IndexParameters[i];
                        var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, forAbiMarshalling: true);
                        if (IsStringTypeSpec(param.SwiftTypeSpec))
                            writer.WriteLine($"var index{i} = global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwiftObject<Swift.SwiftString>(arg{i}).ToString();");
                        // Issue #40: a Swift-class index arrives as the address of a borrowed slot;
                        // copy it out (deref + ObjC-aware retain) instead of Unsafe.Read-ing the heap pointer.
                        else if (GetReceiverClassCopyOutExpr($"arg{i}", param.SwiftTypeSpec) is string setterIndexClassCopyOut)
                            writer.WriteLine($"var index{i} = {setterIndexClassCopyOut};");
                        else
                            writer.WriteLine($"var index{i} = {GetReceiverRawMaterialization(paramTypeName, $"arg{i}", param.SwiftTypeSpec)};");
                    }

                    var setterSiblings = siblingFallbacks?.Where(s => s.HasSetter).ToList();
                    if (setterSiblings == null || setterSiblings.Count == 0)
                    {
                        EmitResolveImplOrFailFast(writer, interfaceName, protocolDecl, "subscript setter");
                        writer.WriteLine($"impl[{indexArgs}] = value;");
                    }
                    else
                    {
                        EmitSubscriptSetterLookupHit(writer, interfaceName, "primary", indexArgs);
                        int siblingIdx = 0;
                        foreach (var sibling in setterSiblings)
                        {
                            var siblingIface = GetQualifiedInterfaceName(sibling.Proto);
                            EmitSubscriptSetterLookupHit(writer, siblingIface, $"s{siblingIdx}", indexArgs);
                            siblingIdx++;
                        }
                    }

                    EmitUcoGuardCloseFailFast(writer);
                    writer.Indent--;
                    writer.WriteLine("}");
                    writer.WriteLine();
                });
            }
        }
    }

    /// <summary>
    /// Emits a try-lookup block for one interface in a sibling-subscript getter receiver.
    /// On lookup hit, materialises the subscript value, applies any conversion, and returns
    /// via MarshalToSwiftBuffer. On miss, falls through to the next sibling. Indexes are
    /// unmarshalled once by the caller and threaded in via <paramref name="indexArgs"/>.
    /// The impl variable is suffixed per-lookup (<c>impl_{slug}</c>) because a declaration
    /// pattern variable in an <c>if</c> condition leaks into the ENCLOSING method scope, not
    /// the block — two bare <c>is {} impl</c> siblings would collide (CS0128) and the second
    /// read possibly-unassigned (CS0165). <c>result</c>/<c>swiftResult</c> stay bare because
    /// they are block-scoped <c>var</c>s. Same per-slug scheme as <see cref="EmitGetterLookupHit"/>.
    /// </summary>
    private void EmitSubscriptGetterLookupHit(CSharpWriter writer, string interfaceName, string slug,
        string indexArgs, TypeSpec returnTypeSpec, string? getterConversion, bool isStringReturn)
    {
        var implVar = $"impl_{slug}";
        writer.WriteLine($"if (Swift.Runtime.ProxyLifetimeTracker.ResolveImpl<{interfaceName}>(handle) is {{}} {implVar})");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine($"var result = {implVar}[{indexArgs}];");
        if (isStringReturn)
        {
            writer.WriteLine("return MarshalStringToUtf8Slice(result);");
        }
        else if (getterConversion != null)
        {
            writer.WriteLine($"var swiftResult = {getterConversion};");
            writer.WriteLine("return MarshalToSwiftBuffer(swiftResult);");
        }
        else
        {
            writer.WriteLine("return MarshalToSwiftBuffer(result);");
        }
        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Emits a try-lookup block for one interface in a sibling-subscript setter receiver.
    /// On lookup hit, performs the assignment and returns. On miss, falls through to the
    /// next sibling.
    /// </summary>
    private void EmitSubscriptSetterLookupHit(CSharpWriter writer, string interfaceName, string slug, string indexArgs)
    {
        var implVar = $"impl_{slug}";
        writer.WriteLine($"if (Swift.Runtime.ProxyLifetimeTracker.ResolveImpl<{interfaceName}>(handle) is {{}} {implVar})");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine($"{implVar}[{indexArgs}] = value;");
        writer.WriteLine("return;");
        writer.Indent--;
        writer.WriteLine("}");
    }

    private void EmitMethodReceiver(CSharpWriter writer, MethodDecl method, ProtocolDecl protocolDecl, string interfaceName, int index, HashSet<string> emittedReceivers)
    {
        var receiverName = $"Receive_{method.Name}_{index}";
        if (!emittedReceivers.Add(receiverName))
            return;

        var proxyClassName = GetProxyClassName(protocolDecl);

        // Closure-returning protocol methods take a dedicated receiver shape — no
        // params, returns a 16-byte buffer of (fnPtr, ctxPtr) that Swift materialises
        // into a real `() -> Void` closure.
        var closureHandlerForReturn = new ClosureHandler(_typeDatabase);
        if (EveryProtocolEmitter.IsDispatchableClosureReturningMethod(method, closureHandlerForReturn))
        {
            EmitDispatchableClosureReturningMethodReceiver(writer, method, protocolDecl, proxyClassName, interfaceName, index, closureHandlerForReturn);
            return;
        }

        // Async closure-param methods receive (rawArg0_fn, rawArg0_ctx) exactly like
        // regular escaping closure params, but wrap them into a
        // Func<Task<int>> via a TaskCompletionSource bridge so the C# impl can `await`
        // the Swift async closure. Cdecl invoke thunk lives on the Swift side; the
        // completion callback resumes the TCS from a static UnmanagedCallersOnly thunk.
        if (EveryProtocolEmitter.IsDispatchableAsyncClosureMethod(method, closureHandlerForReturn))
        {
            EmitDispatchableAsyncClosureMethodReceiver(writer, method, protocolDecl, proxyClassName, interfaceName, index, closureHandlerForReturn);
            return;
        }

        // Real-async reverse-dispatch witness (S13 Pillar C): a primitive-shaped `async throws`
        // requirement is satisfied by a genuine continuation handoff, NOT the legacy thread-blocking
        // sync slot. The Swift witness suspends on withCheckedThrowingContinuation and calls this
        // widened Start-thunk; this receiver spawns the impl's Task and resumes the Swift continuation
        // box via the trailing success/error function pointers. Excludes every closure/generic/Self
        // shape above (EmitsRealAsyncWitness rejects them), so it only sees the plain value shape.
        if (EveryProtocolEmitter.EmitsRealAsyncWitness(method))
        {
            EmitRealAsyncWitnessReceiver(writer, method, protocolDecl, interfaceName, index);
            return;
        }

        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;
        var returnTypeName = hasReturn ? GetCSharpTypeName(returnType!) : "void";

        var nonEmptyParams = method.CSSignature.Skip(1)
            .Where(p => !DefaultParameterOverloadEmitter.IsDebugParameter(p) && !p.SwiftTypeSpec.IsEmptyTuple)
            .ToList();
        // Dispatchable closure params expand into two P/Invoke slots — fnPtr + ctx — so the
        // receiver signature matches the expanded EveryProtocol cdecl vtable trampoline
        // (see EveryProtocolEmitter.CountVtableSlots). `Optional<Closure>` also uses two slots;
        // nil round-trips as `IntPtr.Zero`. Value-shaped params remain a single IntPtr each.
        var closureHandlerForParams = new ClosureHandler(_typeDatabase);
        var receiverParamFragments = new List<string>();
        for (int i = 0; i < nonEmptyParams.Count; i++)
        {
            if (EveryProtocolEmitter.TryGetDispatchableClosureParam(nonEmptyParams[i].SwiftTypeSpec, closureHandlerForParams, out _, out _))
                receiverParamFragments.Add($"IntPtr rawArg{i}_fn, IntPtr rawArg{i}_ctx");
            else
                receiverParamFragments.Add($"IntPtr rawArg{i}");
        }
        var paramTypes = "IntPtr vtHandle, IntPtr selfContainer" + string.Concat(
            receiverParamFragments.Select(f => ", " + f));

        var csharpReturnType = hasReturn ? "IntPtr" : "void";

        // Sibling-method fallback: when this method participates in a same-signature group (two or
        // more protocols declaring the same Swift method signature), the Swift owner body fans out
        // across sibling vtables and may pick ANY populated sibling's vtable — not the one matching
        // the proxy registered for this EveryProtocol instance. Without a per-instance fallback the
        // owner's receiver cannot locate a smaller-sibling proxy and returns the dead-impl null
        // value. We try this interface first, then the recorded sibling interfaces. Restricted to
        // plain sync value/string/ObjC-return methods — exactly the shape the Swift side fans out
        // (async Task returns and dispatchable-closure params take other emit paths that do not
        // fan out, so applying the fallback there would mis-marshal). See
        // EveryProtocolEmitter.ComputeSiblingMethodFallbacks.
        bool hasDispatchableClosureParamForFallback = nonEmptyParams.Any(p =>
            EveryProtocolEmitter.TryGetDispatchableClosureParam(p.SwiftTypeSpec, closureHandlerForParams, out _, out _));
        IReadOnlyList<ModuleEmissionContext.SiblingMethodFallback>? siblingFallbacks = null;
        if (!method.IsAsync && !hasDispatchableClosureParamForFallback)
        {
            var protoQNameForMethod = EveryProtocolEmitter.GetProtocolFallbackKey(protocolDecl);
            var methodMapKey = EveryProtocolEmitter.GetMethodSiblingMapKey(method);
            siblingFallbacks = _emissionContext.GetSiblingMethodFallbacks(protoQNameForMethod, methodMapKey);
        }

        // The method receiver's marshalling (per-param setter conversions, the return
        // existential-getter conversion) can throw SuppressedProxyReferenceException when an
        // existential touches a protocol proxy whose EveryProtocol conformance was suppressed at
        // generation. Guard the whole body: on throw the partial body is rolled back and the receiver
        // re-emitted as a fail-fast stub that keeps this exact signature (csharpReturnType /
        // receiverName / paramTypes), so the vtable static-init that address-takes &Receive_* still
        // resolves (a missing symbol is CS0103). The closure / async-closure / real-async receiver
        // shapes returned early above and are not routed through here — they take dedicated emit paths.
        var methodDescriptor = $"{protocolDecl.Name}.{method.Name}{RenderReceiverParamSignature(nonEmptyParams)}";
        var degraded = EmitReceiverOrDegrade(writer, csharpReturnType, receiverName, paramTypes,
            methodDescriptor,
            () => EmitMethodReceiverBody(writer, method, protocolDecl, interfaceName, index,
                receiverName, paramTypes, csharpReturnType, returnType, hasReturn,
                nonEmptyParams, closureHandlerForParams, siblingFallbacks));
        // Reverse-dispatch return-value CONSUME degrade: a method that RETURNS an existential hands it
        // back to Swift via the silent-drop CONSUME arm. Record it only when the body did NOT fail-fast
        // (a suppressed existential PARAM throws and is already recorded as receiver-failfast) — the
        // returned value is never marshalled from a fail-fast stub, so it must not be double-classified.
        if (!degraded && hasReturn)
            RecordReceiverGetterConsumeDegrade(methodDescriptor, returnType);
    }

    /// <summary>
    /// Emits the live body of a plain value-shaped method receiver — the non-closure / non-async path
    /// of <see cref="EmitMethodReceiver"/>. Factored out so <see cref="EmitReceiverOrDegrade"/> can
    /// wrap it in the suppressed-proxy checkpoint/rollback guard without reindenting the body. May throw
    /// <see cref="SuppressedProxyReferenceException"/> from its param/return marshalling; the caller
    /// catches it and degrades the receiver to a fail-fast stub.
    /// </summary>
    private void EmitMethodReceiverBody(CSharpWriter writer, MethodDecl method,
        ProtocolDecl protocolDecl, string interfaceName, int index, string receiverName,
        string paramTypes, string csharpReturnType, TypeSpec? returnType, bool hasReturn,
        List<ArgumentDecl> nonEmptyParams, ClosureHandler closureHandlerForParams,
        IReadOnlyList<ModuleEmissionContext.SiblingMethodFallback>? siblingFallbacks)
    {
        writer.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        writer.WriteLine($"private static {csharpReturnType} {receiverName}({paramTypes})");
        writer.WriteLine("{");
        writer.Indent++;

        // Optional-existential returns fall through to the normal marshalling path:
        // GetReceiverExistentialGetterConversion's Optional<existential> arm builds a valid
        // SwiftOptional<ExistentialContainerN> (NewSome/NewNone) from the C# proxy on the success
        // path below. A vanished impl no longer returns a fabricated buffer (the old "zeroed
        // buffer (None)" stub silently dropped every non-nil return); it FailFasts (Design B2).

        EmitUcoGuardOpen(writer);
        writer.WriteLine("var handle = *(IntPtr*)selfContainer;");
        // No-sibling path resolves the impl from ProxyLifetimeTracker's strong root (Design B2);
        // a null resolve trips Environment.FailFast (the impl cannot be collected while Swift holds
        // the proxy). The sibling path skips this and instead does per-interface lookups after
        // unmarshalling, since params must be unmarshalled once before trying each sibling impl.
        bool useMethodSiblingFallback = siblingFallbacks != null && siblingFallbacks.Count > 0;
        if (!useMethodSiblingFallback)
        {
            EmitResolveImplOrFailFast(writer, interfaceName, protocolDecl, $"{method.Name}()");
        }

        // Unmarshal parameters - use param{i} for local variable names to avoid conflicts with rawArg{i}
        // B10: After unmarshalling, apply type conversion from ABI to idiomatic C# types
        // (e.g., SwiftOptional<SwiftString> → string?) to match the interface method signature.
        // P0: Use ABI types for MarshalFromSwift — idiomatic types (string, bool?) can't read Swift memory.
        var argNames = new List<string>();
        // Parallel `ref `/`` modifiers for inout params. The unmarshalled `param{i}` locals are
        // writable, so `ref param{i}` binds to the interface method's `ref` slot; mutation through
        // the shared payload pointer round-trips to Swift exactly as on the forward path.
        var argModifiers = new List<string>();
        int argIndex = 0;
        foreach (var param in nonEmptyParams)
        {
            var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, forAbiMarshalling: true);
            var rawArgName = $"rawParam{argIndex}";
            var argName = $"param{argIndex}";

            // Dispatchable closure params arrive as expanded (fnPtr, ctx) IntPtrs. Wrap them
            // in SwiftEscapingClosure<TDelegate>.FromSwift (which Arc.Retain's the context for
            // ARC-correct lifetime) and bind the result to the per-shape invoker class so the
            // C# impl receives a regular managed delegate (e.g. Action / Func<...>).
            // `Optional<Closure>`: nil arrives as `IntPtr.Zero` and projects to `null`.
            // Same Mono-JIT-safe pattern as closure returns: named invoker class + method
            // group (no display class in the call chain).
            if (EveryProtocolEmitter.TryGetDispatchableClosureParam(param.SwiftTypeSpec, closureHandlerForParams, out var dispatchClosure, out var isOptional))
            {
                var entryPoint = EveryProtocolEmitter.GetProtocolClosureInvokeThunkEntryPoint(protocolDecl, method, index, argIndex);
                var helperName = EveryProtocolEmitter.GetProtocolClosureInvokeThunkHelperName(entryPoint);
                var invokerClassName = ClosureEmitter.GetInvokerClassName(helperName);
                var delegateType = closureHandlerForParams.GetCSharpDelegateType(dispatchClosure!);
                var wrapperVar = $"_closureWrapper{argIndex}";
                var invVar = $"_inv{argIndex}";
                if (isOptional)
                {
                    var nullableDelegateType = $"{delegateType}?";
                    writer.WriteLine($"// Optional Swift closure (fnPtr, ctx) → managed {nullableDelegateType}. nil arrives as IntPtr.Zero.");
                    writer.WriteLine($"{nullableDelegateType} {argName};");
                    writer.WriteLine($"if (rawArg{argIndex}_fn == IntPtr.Zero)");
                    writer.Indent++;
                    writer.WriteLine($"{argName} = null;");
                    writer.Indent--;
                    writer.WriteLine("else");
                    writer.WriteLine("{");
                    writer.Indent++;
                    writer.WriteLine($"var {wrapperVar} = SwiftEscapingClosure<{delegateType}>.FromSwift(rawArg{argIndex}_fn, rawArg{argIndex}_ctx);");
                    writer.WriteLine($"var {invVar} = new {invokerClassName}((nint){wrapperVar}.FunctionPointer, (nint){wrapperVar}.Context, {wrapperVar});");
                    writer.WriteLine($"{argName} = {invVar}.Invoke;");
                    writer.Indent--;
                    writer.WriteLine("}");
                }
                else
                {
                    writer.WriteLine($"// Wrap Swift closure (fnPtr, ctx) into a managed {delegateType} via SwiftEscapingClosure (ARC) + invoker class (Mono-JIT-safe).");
                    writer.WriteLine($"var {wrapperVar} = SwiftEscapingClosure<{delegateType}>.FromSwift(rawArg{argIndex}_fn, rawArg{argIndex}_ctx);");
                    writer.WriteLine($"var {invVar} = new {invokerClassName}((nint){wrapperVar}.FunctionPointer, (nint){wrapperVar}.Context, {wrapperVar});");
                    writer.WriteLine($"{delegateType} {argName} = {invVar}.Invoke;");
                }
            }
            // String parameter: the local MarshalFromSwift<SwiftString> helper uses Unsafe.Read<T>
            // which can't construct a managed SwiftString from raw Swift memory (16-byte value).
            // Use the runtime's SwiftMarshal.MarshalFromSwift which calls NewFromPayload.
            else if (IsStringTypeSpec(param.SwiftTypeSpec))
            {
                writer.WriteLine($"var {rawArgName} = global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwiftObject<Swift.SwiftString>(rawArg{argIndex});");
                writer.WriteLine($"var {argName} = {rawArgName}.ToString();");
            }
            // Issue #40: a Swift-class (or Optional<class>) param arrives as the address of a
            // borrowed slot holding the heap pointer (the Swift thunk passes &{param}Copy). Copy it out
            // via the runtime helper (deref + ObjC-aware retain + NewFromPayload). The local
            // Unsafe.Read<T> would reinterpret the heap pointer as a managed reference and SIGSEGV.
            else if (GetReceiverClassCopyOutExpr($"rawArg{argIndex}", param.SwiftTypeSpec) is string classCopyOut)
            {
                writer.WriteLine($"var {argName} = {classCopyOut};");
            }
            // Dictionaries need special handling in receiver context: the interface declares
            // IDictionary<K,V> (parameter form), but projection produces .AsProjected()
            // which returns IReadOnlyDictionary<K,V> (return form). IReadOnlyDictionary doesn't
            // implement IDictionary, so we must use .ToDictionary() for eager materialization.
            else if (GetReceiverDictionaryConversion(rawArgName, param.SwiftTypeSpec) is string receiverDictConversion)
            {
                writer.WriteLine($"var {rawArgName} = {GetReceiverRawMaterialization(paramTypeName, $"rawArg{argIndex}", param.SwiftTypeSpec)};");
                writer.WriteLine($"var {argName} = {receiverDictConversion};");
            }
            // Optional ObjC-bridgeable VALUE param (URL?): the Swift thunk borrows the bridged NSObject and
            // passes one optional ObjC pointer word (nil = 0x0), so read a bare IntPtr and +0-bridge it —
            // NOT the default two-word SwiftOptional<IntPtr> carrier. Mirror of the property-setter arm.
            else if (TryGetReceiverOptionalObjCBridgeableValueRead(param.SwiftTypeSpec, $"rawArg{argIndex}", rawArgName, out var objcOptMarshal, out var objcOptConv))
            {
                writer.WriteLine($"var {rawArgName} = {objcOptMarshal};");
                writer.WriteLine($"var {argName} = {objcOptConv};");
            }
            else
            {
                var setterConversion = GetReceiverSetterConversion(rawArgName, param.SwiftTypeSpec);
                if (setterConversion != null)
                {
                    writer.WriteLine($"var {rawArgName} = {GetReceiverRawMaterialization(paramTypeName, $"rawArg{argIndex}", param.SwiftTypeSpec)};");
                    writer.WriteLine($"var {argName} = {setterConversion};");
                }
                else
                {
                    writer.WriteLine($"var {argName} = {GetReceiverRawMaterialization(paramTypeName, $"rawArg{argIndex}", param.SwiftTypeSpec)};");
                }
            }
            argNames.Add(argName);
            argModifiers.Add(param.IsInOut ? "ref " : "");
            argIndex++;
        }

        var argsString = string.Join(", ", argNames.Select((n, i) => argModifiers[i] + n));

        var isSelfReturning = MethodEnvironment.IsSelfReturningMethod(method);
        // Mirror the property-collision rename applied during interface emission
        // (ProtocolProxyEmitter.InterfaceImpl.cs L62–L88). Use the canonical cached
        // set populated by ProtocolHandler / InterfacePropertyNamePrecomputer so the
        // receiver's view matches what the interface actually emits — including
        // emitted static abstract property names and excluding skipped instance
        // properties. Without this, the receiver invokes `impl.RichText(range)`
        // for a method that the interface emitted as `RichTextMethod(range)`
        // because a same-named property took the PascalCased slot (CS1955), and
        // static-property collisions are missed while skipped-property collisions
        // are over-applied.
        // The public C# method name depends on the TARGET protocol's OWN property set (a
        // same-named property steals the PascalCased slot, renaming the method). Compute it
        // per protocol via ComputeReceiverPascalMethodName so the sibling-fallback calls below
        // bind to each sibling's own emitted name, not the primary protocol's.
        var pascalMethodName = ComputeReceiverPascalMethodName(method, protocolDecl, hasReturn, isSelfReturning);

        // Return-conversion metadata is emitter-side (not generated output); compute it once so the
        // eager-impl path and every sibling-fallback lookup block share the identical conversion.
        // String returns use Utf8Slice encoding to avoid ARC issues with SwiftString. The
        // existential→getter fallback covers ObjC-bridgeable, Date, NativeRemapped, etc.; without
        // it a return of e.g. Foundation.NSUrl would write a managed reference via
        // MarshalToSwiftBuffer instead of extracting the .Handle ObjC pointer Swift expects.
        // Async receivers on this legacy fallback path satisfy the async requirement through the
        // sync-ABI witness slot: the impl call below blocks the Task (asyncResultUnwrap) so `result`
        // is the unwrapped T, and these T-shaped conversions then apply exactly as for a sync return.
        // (Earlier this path skipped the conversions for async because `result` was a Task<T>; the
        // unwrap makes that special-casing wrong — Swift reads T, so a String/ObjC async return MUST
        // be converted.) Primitive-shaped async returns never reach here — they took the real
        // reverse-async witness above.
        bool isStringMethodReturn = hasReturn && IsStringTypeSpec(returnType!);
        string? returnConv = null;
        if (hasReturn && !isStringMethodReturn)
        {
            returnConv = GetReceiverExistentialGetterConversion("result", returnType!)
                ?? GetReceiverGetterConversion("result", returnType!);
        }

        // This is the LEGACY blocking async receiver — the fallback for async requirements the real
        // reverse-async witness predicate rejects (EveryProtocolEmitter.EmitsRealAsyncWitness:
        // non-primitive return, arity > 4, generic/Self shapes). Primitive-shaped async requirements
        // returned early at the EmitsRealAsyncWitness branch above into EmitRealAsyncWitnessReceiver,
        // which hands the continuation back to Swift instead of blocking. On THIS path the async
        // requirement is satisfied via the SYNC-ABI witness slot (the sync witness ABI for the
        // remaining shapes), so the C# impl returns Task<T> (or Task) while the Swift witness reads the
        // unwrapped T (or void). Block on the Task so the sync witness body marshals T, not the Task
        // object — without this the receiver would MarshalToSwiftBuffer(Task<T>) and silently corrupt
        // the return ABI. Mirrors the forward-closure async-bridge idiom (Func<Task<T>> →
        // .GetAwaiter().GetResult()). Async is gated out of the sibling-fallback path above, so the
        // unwrap is only needed below.
        //
        // Blocking this slot is NOT deadlock-free in general — the earlier "no SynchronizationContext,
        // so blocking cannot self-deadlock" claim was too narrow. There is no SynchronizationContext
        // to re-enter, but the conformance can still self-deadlock by awaiting work that needs THIS
        // thread to make progress: a continuation pinned to the blocked thread (e.g. another
        // @MainActor hop reaching back to the main thread this witness is blocking), or cooperative
        // thread-pool starvation when many witnesses block pool threads at once. Those are inherent to
        // the sync-blocked Issue-1 workaround; the real async witness (Session 13, S13 Pillar C)
        // removes them for every shape it accepts, and this fallback retains them only for the shapes
        // it cannot yet carry. What this seam DOES guarantee: an exception escaping the awaited work
        // cannot silently corrupt the boundary — the async UCO close below converts any escape
        // (cancellation or other) into a member-named FailFast, because the sync slot has no Swift
        // error channel to carry it.
        string asyncResultUnwrap = method.IsAsync ? ".GetAwaiter().GetResult()" : string.Empty;

        // The C# interface reshapes an async requirement to a trailing `CancellationToken
        // cancellationToken = default`. When the SAME protocol also declares a sync namesake projecting
        // to the same C# name (Swift `func foo() async` + `func fooAsync()`, AF05 ruling b), BOTH
        // overloads coexist and a bare `impl.FooAsync(args)` binds the SYNC (exact-arity) overload —
        // whose return is not a Task, so the `.GetAwaiter().GetResult()` unwrap above fails to compile
        // (CS1061). Pass the trailing token explicitly so the call binds the async overload; a
        // reverse-dispatched async witness carries no token, so `default` is the correct value. Harmless
        // when no sync namesake exists — it just makes the always-present default-token argument explicit
        // (same bound overload, same behavior). Mirrors the real reverse-async witness receiver's
        // asyncImplArgs. For a SYNC method this is exactly `argsString` (no token — a sync C# overload has
        // no CancellationToken parameter), so the sibling-fallback impl calls below — which are reached
        // only for sync methods (async is gated out of the sibling path at siblingFallbacks setup) — stay
        // byte-identical. Using one expression at every impl-call site keeps them from drifting.
        string implCallArgs = method.IsAsync
            ? (argsString.Length > 0
                ? $"{argsString}, default(global::System.Threading.CancellationToken)"
                : "default(global::System.Threading.CancellationToken)")
            : argsString;

        if (useMethodSiblingFallback)
        {
            // The Swift owner body fans out across sibling vtables and may dispatch into whichever
            // sibling proxy the C# impl populated — not necessarily the one matching this interface.
            // Params are already unmarshalled once above; try this interface first, then each
            // recorded sibling interface, then fall back to the dead-impl null value.
            EmitMethodLookupHit(writer, interfaceName, "primary", pascalMethodName, implCallArgs, hasReturn, isStringMethodReturn, returnConv);
            int siblingIdx = 0;
            foreach (var sibling in siblingFallbacks!)
            {
                var siblingIface = GetQualifiedInterfaceName(sibling.Proto);
                // The sibling interface names this method from ITS OWN property set, which may
                // differ from the primary's (one declares a same-named property forcing a rename,
                // the other does not). Resolve per sibling so the call binds to the name that
                // interface actually emitted — reusing pascalMethodName would emit a call to a
                // method the sibling interface never defined (CS1061/CS1955).
                var siblingPascalMethodName = ComputeReceiverPascalMethodName(method, sibling.Proto, hasReturn, isSelfReturning);
                EmitMethodLookupHit(writer, siblingIface, $"s{siblingIdx}", siblingPascalMethodName, implCallArgs, hasReturn, isStringMethodReturn, returnConv);
                siblingIdx++;
            }
            EmitSiblingFanOutFailFast(writer, protocolDecl, $"{method.Name}()");
        }
        else if (hasReturn)
        {
            writer.WriteLine($"var result = impl.{pascalMethodName}({implCallArgs}){asyncResultUnwrap};");
            if (isStringMethodReturn)
            {
                writer.WriteLine("return MarshalStringToUtf8Slice(result);");
            }
            else if (returnConv != null)
            {
                writer.WriteLine($"var swiftResult = {returnConv};");
                writer.WriteLine("return MarshalToSwiftBuffer(swiftResult);");
            }
            else
            {
                writer.WriteLine("return MarshalToSwiftBuffer(result);");
            }
        }
        else
        {
            writer.WriteLine($"impl.{pascalMethodName}({implCallArgs}){asyncResultUnwrap};");
        }

        // Async receivers on this legacy fallback path block the Task on the sync-ABI slot (Issue 1)
        // and have no Swift error channel, so any escape — cancellation or otherwise — is
        // process-terminating. Use the member-named async close (Finding 36) so the FailFast is
        // attributable rather than anonymous; sync receivers keep the plain FailFast close.
        // (Primitive-shaped async requirements never reach here — the real reverse-async witness above
        // carries the error back through the Swift continuation box instead of FailFasting.)
        if (method.IsAsync)
            EmitUcoGuardCloseAsyncWitnessFailFast(writer, $"{protocolDecl.Name}.{method.Name}");
        else
            EmitUcoGuardCloseFailFast(writer);
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits the C# receiver for an S13 Pillar C real-async reverse-dispatch witness — the inverse of
    /// the forward async-closure Start thunk. Swift's <c>func m(...) async throws -&gt; T</c> witness
    /// suspends on <c>withCheckedThrowingContinuation</c> and calls this widened
    /// <c>[UnmanagedCallersOnly]</c> slot with <c>(vtHandle, selfContainer, &lt;value args&gt;,
    /// continuationBoxPtr, successFuncPtr, errorFuncPtr)</c>. Rather than block the slot and
    /// <c>.GetAwaiter().GetResult()</c> the impl's <c>Task</c> (the legacy Issue-1 workaround), this
    /// resolves the C# impl, marshals the (blittable-primitive) args synchronously while Swift's pointers
    /// are still live, then hands the impl's <c>Task&lt;T&gt;</c> to
    /// <see cref="Swift.Runtime.AsyncClosureHelper"/> via <c>RunAsync</c>. The helper runs the Task on
    /// the pool and, on completion, resumes the Swift continuation box exactly once — success marshals
    /// <c>T</c> into a result buffer and calls <c>successFuncPtr</c>; a fault (including
    /// <see cref="System.OperationCanceledException"/>) resumes with the error message via
    /// <c>errorFuncPtr</c>. A shared <c>AsyncResumeGuard</c> makes resume strictly once. On the THROWING
    /// path the UCO envelope's <see cref="UcoGuardEmitter.UcoFaultPolicy.ResumeBoxError"/> close routes a
    /// post-resolution synchronous escape (a marshal fault, or an impl that throws before returning its
    /// <c>Task</c>) into a box error-resume, so the Swift task is not abandoned; the NON-throwing path
    /// has no Swift error channel and FailFasts such an escape instead. A dead-proxy resolve (no live C#
    /// impl for the handle) is a Design B2 lifetime violation on BOTH paths and FailFasts the process
    /// before the box is touched — it is deliberately NOT converted into a box error-resume.
    /// <see cref="EveryProtocolEmitter.EmitsRealAsyncWitness"/> gates this to the plain value shape
    /// (non-inout blittable-primitive params + return), so the arg loop is the simple
    /// materialize-and-pass form with no closure/inout/string/ObjC arms.
    /// </summary>
    private void EmitRealAsyncWitnessReceiver(CSharpWriter writer, MethodDecl method,
        ProtocolDecl protocolDecl, string interfaceName, int index)
    {
        var receiverName = $"Receive_{method.Name}_{index}";

        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        // EmitsRealAsyncWitness guarantees a single blittable-primitive scalar return; the interface
        // method is `Task<{csReturnType}>`, so the closure state and helper generic are <csReturnType>.
        var csReturnType = GetCSharpTypeName(returnType!);

        // A throwing requirement resumes the Swift continuation box WITH the error on a C# fault (real
        // Swift error channel). A non-throwing requirement has no error channel: a fault FailFasts the
        // process — mirroring the forward non-throwing async-closure Start thunk. The slot's trailing
        // error-FP stays in the ABI either way (width is throwing-agnostic, +3); the non-throwing path
        // simply never invokes it.
        var isThrowing = method.Throws;

        var nonEmptyParams = method.CSSignature.Skip(1)
            .Where(p => !DefaultParameterOverloadEmitter.IsDebugParameter(p) && !p.SwiftTypeSpec.IsEmptyTuple)
            .ToList();

        // ABI param slots: (vtHandle, selfContainer, one raw value pointer per arg, then the trailing
        // continuation box + success/error function pointers). Matches the widened Swift vtable field
        // (EveryProtocolEmitter.EmitMethodVtableField, +3) and the C# local delegate (GetWidth +3).
        var receiverParamFragments = nonEmptyParams.Select((_, i) => $"IntPtr rawArg{i}");
        var paramTypes = "IntPtr vtHandle, IntPtr selfContainer"
            + string.Concat(receiverParamFragments.Select(f => ", " + f))
            + ", IntPtr continuationBoxPtr, IntPtr successFuncPtr, IntPtr errorFuncPtr";

        var isSelfReturning = MethodEnvironment.IsSelfReturningMethod(method);
        var pascalMethodName = ComputeReceiverPascalMethodName(method, protocolDecl, hasReturn: true, isSelfReturning);

        // Sibling-method fallback (the real-async twin of the sync receiver's path at ~L930): when this
        // real-async method participates in a same-signature group across protocols, the Swift owner
        // witness fans out across sibling vtables and may dispatch into whichever sibling proxy the C#
        // impl populated — OR, once any owner-conforming proxy has primed the owner's process-wide
        // vtable, the owner branch fires for a non-owner-only instance. Either way the owner receiver
        // must resolve THIS instance's proxy across the recorded sibling interfaces, not just its own,
        // or it FailFasts a perfectly live smaller-sibling impl. ComputeSiblingMethodFallbacks already
        // groups async methods (includeAsyncEffect:true), so the entry exists for real-async pairs; it
        // is empty for a solo group, leaving the single-resolve path byte-identical.
        var protoQNameForMethod = EveryProtocolEmitter.GetProtocolFallbackKey(protocolDecl);
        var methodSiblingMapKey = EveryProtocolEmitter.GetMethodSiblingMapKey(method);
        var siblingFallbacks = _emissionContext.GetSiblingMethodFallbacks(protoQNameForMethod, methodSiblingMapKey);
        bool useSiblingFallback = siblingFallbacks != null && siblingFallbacks.Count > 0;

        writer.WriteLine("[global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        writer.WriteLine($"private static unsafe void {receiverName}({paramTypes})");
        writer.WriteLine("{");
        writer.Indent++;

        // Shared resume-once guard + the success/error completion delegates. Each claims the guard
        // before invoking its Swift @_cdecl resume symbol, so a success and an error (or a racing
        // duplicate) can never both consume the same box. Built BEFORE the guarded body because the
        // ResumeBoxError catch resumes through errorAction. Mirrors the forward Start thunk
        // (ClosureEmitter.Async.cs) exactly.
        writer.WriteLine("var __resumeGuard = new global::Swift.Runtime.AsyncResumeGuard();");
        writer.WriteLine("var successAction = new global::System.Action<IntPtr, IntPtr>((box, resultPtr) =>");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine("if (!__resumeGuard.TryClaim()) return;");
        writer.WriteLine("var fp = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)successFuncPtr;");
        writer.WriteLine("fp(box, resultPtr);");
        writer.Indent--;
        writer.WriteLine("});");
        if (isThrowing)
        {
            // Only the throwing path resumes-with-error: the Swift box carries a CheckedContinuation<T,
            // Error> and the `_error` @_cdecl symbol. The non-throwing slot fills error-FP with a sentinel
            // (never invoked), so no errorAction is built — RunAsyncNonThrowing FailFasts on a fault.
            writer.WriteLine("var errorAction = new global::System.Action<IntPtr, IntPtr>((box, errPtr) =>");
            writer.WriteLine("{");
            writer.Indent++;
            writer.WriteLine("if (!__resumeGuard.TryClaim()) return;");
            writer.WriteLine("var fp = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)errorFuncPtr;");
            writer.WriteLine("fp(box, errPtr);");
            writer.Indent--;
            writer.WriteLine("});");
        }

        EmitUcoGuardOpen(writer);
        writer.WriteLine("var handle = *(IntPtr*)selfContainer;");
        // Solo group: resolve the single impl from this interface (a null resolve is a Design B2
        // lifetime violation → FailFast). Sibling group: defer resolution until after the args are
        // marshalled, then try the primary interface then each sibling interface (params must be read
        // once before the per-interface lookups, since the matched impl is captured by the AsyncFunc).
        if (!useSiblingFallback)
            EmitResolveImplOrFailFast(writer, interfaceName, protocolDecl, $"{method.Name}()");

        // Marshal args synchronously — Swift's argument pointers are valid only for the duration of
        // this synchronous call, so read them out to managed values BEFORE RunAsync spawns the Task.
        // Each is a blittable primitive, so the simple materialize form applies (no closure/inout/
        // string/ObjC arms). The values are then captured by the AsyncFunc closure.
        var argNames = new List<string>();
        int argIndex = 0;
        foreach (var param in nonEmptyParams)
        {
            var paramTypeName = GetCSharpTypeName(param.SwiftTypeSpec, forAbiMarshalling: true);
            var argName = $"arg{argIndex}";
            writer.WriteLine($"var {argName} = {GetReceiverRawMaterialization(paramTypeName, $"rawArg{argIndex}", param.SwiftTypeSpec)};");
            argNames.Add(argName);
            argIndex++;
        }
        var argsString = string.Join(", ", argNames);

        // The C# interface reshapes an async requirement to `Task<T> NameAsync(..., CancellationToken
        // cancellationToken = default)`. When the SAME protocol also declares a sync namesake projecting
        // to the same C# name (Swift `func foo() async` + `func fooAsync()`, AF05 ruling b), BOTH
        // `NameAsync` overloads exist and a bare `impl.NameAsync(args)` binds the SYNC (exact-arity)
        // overload — the wrong return type (CS0029: int vs Task<int>). Pass the trailing CancellationToken
        // explicitly to bind the async overload; a Swift async reverse-dispatch carries no token, so
        // `default` is the correct value. Harmless when no sync namesake exists — it just makes the
        // always-present default-token argument explicit (same bound overload, same runtime behavior).
        var asyncImplArgs = argsString.Length > 0
            ? $"{argsString}, default(global::System.Threading.CancellationToken)"
            : "default(global::System.Threading.CancellationToken)";

        // The AsyncFunc thunk handed to the helper. Solo: the inline lambda on the already-resolved
        // `impl` (byte-identical to the pre-fan-out output). Sibling: a `__asyncFunc` local bound to
        // whichever interface — primary first, then each recorded sibling — actually resolves a live
        // impl for THIS handle, so a smaller-sibling proxy reached through the owner's primed vtable is
        // still located. Each sibling interface may project this method under a different name (a
        // same-named property forces a rename on one side), so the bound call uses that interface's own
        // projected name.
        string asyncFuncExpr;
        if (useSiblingFallback)
        {
            writer.WriteLine($"global::System.Func<global::System.Threading.Tasks.Task<{csReturnType}>> __asyncFunc;");
            writer.WriteLine($"if (Swift.Runtime.ProxyLifetimeTracker.ResolveImpl<{interfaceName}>(handle) is {{}} __impl_primary)");
            writer.WriteLine("{");
            writer.Indent++;
            writer.WriteLine($"__asyncFunc = () => __impl_primary.{pascalMethodName}({asyncImplArgs});");
            writer.Indent--;
            writer.WriteLine("}");
            int siblingIdx = 0;
            foreach (var sibling in siblingFallbacks!)
            {
                var siblingIface = GetQualifiedInterfaceName(sibling.Proto);
                var siblingPascalMethodName = ComputeReceiverPascalMethodName(method, sibling.Proto, hasReturn: true, isSelfReturning);
                writer.WriteLine($"else if (Swift.Runtime.ProxyLifetimeTracker.ResolveImpl<{siblingIface}>(handle) is {{}} __impl_s{siblingIdx})");
                writer.WriteLine("{");
                writer.Indent++;
                writer.WriteLine($"__asyncFunc = () => __impl_s{siblingIdx}.{siblingPascalMethodName}({asyncImplArgs});");
                writer.Indent--;
                writer.WriteLine("}");
                siblingIdx++;
            }
            writer.WriteLine("else");
            writer.WriteLine("{");
            writer.Indent++;
            // No primary or sibling proxy resolves for this handle — Design B2 violation. The throw
            // also supplies definite-assignment for __asyncFunc (every other branch assigns it).
            EmitSiblingFanOutFailFast(writer, protocolDecl, $"{method.Name}()");
            writer.Indent--;
            writer.WriteLine("}");
            asyncFuncExpr = "__asyncFunc";
        }
        else
        {
            asyncFuncExpr = $"() => impl.{pascalMethodName}({asyncImplArgs})";
        }

        // Hand the impl's Task<T> to the shared async helper: it runs the Task on the pool and, on
        // completion, resumes the box exactly once. The GCHandle is `default` (the box owns lifetime;
        // nothing to free here — see AsyncClosureHelper remarks). Arity-0 state: the args are bound
        // inside AsyncFunc, so the no-extra-arg overload is selected. Throwing → RunAsync (success
        // marshals T → successFuncPtr; fault → errorFuncPtr). Non-throwing → RunAsyncNonThrowing
        // (success → successFuncPtr; fault FailFasts, no Swift error channel).
        if (isThrowing)
        {
            writer.WriteLine($"global::Swift.Runtime.AsyncClosureHelper.RunAsync(");
            writer.Indent++;
            writer.WriteLine("default(global::System.Runtime.InteropServices.GCHandle),");
            writer.WriteLine($"new global::Swift.Runtime.AsyncThrowingClosureState<{csReturnType}> {{ AsyncFunc = {asyncFuncExpr} }},");
            writer.WriteLine("continuationBoxPtr, successAction, errorAction);");
            writer.Indent--;
        }
        else
        {
            writer.WriteLine($"global::Swift.Runtime.AsyncClosureHelper.RunAsyncNonThrowing(");
            writer.Indent++;
            writer.WriteLine("default(global::System.Runtime.InteropServices.GCHandle),");
            writer.WriteLine($"new global::Swift.Runtime.AsyncClosureState<{csReturnType}> {{ AsyncFunc = {asyncFuncExpr} }},");
            writer.WriteLine("continuationBoxPtr, successAction);");
            writer.Indent--;
        }

        // One UCO envelope (ResumeBoxError), body varies by effect — exactly the forward Start thunk
        // shape. A synchronous escape (dead proxy already FailFasts; a faulting impl that throws before
        // returning its Task) routes through the box on the throwing path and FailFasts on the
        // non-throwing path, so the Swift task is never abandoned and the box is consumed exactly once.
        var resumeErrorBody = isThrowing
            ? "global::Swift.Runtime.AsyncClosureHelper.ReportError(__uco_ex, continuationBoxPtr, errorAction);"
            : "global::Swift.Runtime.AsyncClosureHelper.FailFastNonThrowing(__uco_ex);";
        UcoGuardEmitter.EmitClose(writer, UcoGuardEmitter.UcoFaultPolicy.ResumeBoxError,
            resumeErrorBody: new[] { resumeErrorBody });
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Computes the public C# method name the interface for <paramref name="proto"/> emits for
    /// <paramref name="method"/>, mirroring the property-collision rename applied during interface
    /// emission (ProtocolProxyEmitter.InterfaceImpl.cs L62–L88): a same-named property takes the
    /// PascalCased slot, renaming the method (e.g. <c>RichText(range)</c> → <c>RichTextMethod(range)</c>).
    /// <para>Resolved against the canonical cached property-name set (populated by ProtocolHandler /
    /// InterfacePropertyNamePrecomputer for every protocol in the module) so the receiver's view
    /// matches what the interface actually emits — including static-abstract property names and
    /// excluding skipped instance properties.</para>
    /// <para>Computed PER protocol because the same-signature sibling fan-out calls into multiple
    /// sibling interfaces, and the method name depends on each TARGET protocol's own property set.
    /// Reusing one protocol's name for a sibling whose property set differs would emit a call to a
    /// method that sibling interface never defined (CS1061/CS1955).</para>
    /// </summary>
    private string ComputeReceiverPascalMethodName(
        MethodDecl method, ProtocolDecl proto, bool hasReturn, bool isSelfReturning)
    {
        var protoQualifiedName = proto.SwiftTypeName?.ModuleQualifiedName
                               ?? $"{proto.ModuleDecl?.Name ?? "Unknown"}.{proto.Name}";
        var canonicalPropertyNames = _emissionContext.GetInterfacePropertyNames(protoQualifiedName);
        HashSet<string> receiverPropertyNames;
        if (canonicalPropertyNames != null)
        {
            receiverPropertyNames = new HashSet<string>(canonicalPropertyNames);
        }
        else
        {
            // Defensive fallback: the prepass populates the cache for every protocol in
            // the module, so this branch should not trigger in practice. Mirror the
            // canonical construction (instance + emitted static).
            receiverPropertyNames = new HashSet<string>();
            foreach (var property in proto.Properties)
            {
                if (property.IsStatic)
                {
                    if (_staticAbstractPropertyNames.Contains(property.Name))
                        receiverPropertyNames.Add(NameProvider.GetPropertyName(property.Name));
                }
                else if (!_skippedPropertyNames.Contains(property.Name) || _closureSkippedPropertyNames.Contains(property.Name))
                {
                    receiverPropertyNames.Add(NameProvider.GetPropertyName(property.Name));
                }
            }
        }
        return NameProvider.GetPublicMethodName(ProtocolMethodDisambiguator.EffectiveNameInput(method, proto, _typeDatabase), method.IsAsync, hasReturn,
            propertyNames: receiverPropertyNames,
            isSelfReturning: isSelfReturning,
            parameterCount: method.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple),
            isMutating: method.IsMutating);
    }

    /// <summary>
    /// Gets a dictionary conversion expression for receiver parameters using projections.
    /// Receivers pass unmarshalled ABI types to the C# interface implementation, which expects
    /// IDictionary&lt;K,V&gt; (parameter form). Projection-based .AsProjected() returns IReadOnlyDictionary,
    /// which doesn't implement IDictionary. This method uses .ToDictionary() for eager materialization
    /// to produce a Dictionary&lt;K,V&gt; that satisfies the IDictionary contract.
    /// Returns null if the type is not a dictionary or doesn't need conversion.
    /// </summary>
    private string? GetReceiverDictionaryConversion(string rawArgName, TypeSpec? typeSpec)
    {
        if (typeSpec == null) return null;

        var projection = s_projectionFactory.Project(typeSpec,
            new ProjectionContext { TypeDatabase = _typeDatabase, IsParameter = false, CurrentModuleName = _moduleName, EmissionContext = _emissionContext });
        if (projection is not DictionaryProjection dict) return null;

        // Owned (+1) element conversions: materializing this receiver param via .ToDictionary()
        // drives SwiftDictionary's entry enumerator, whose MarshalMovedValueFromSlot MOVES each key
        // and value out of its slot at +1 (the source dict keeps its own independent +1, so adoption
        // never double-frees). An existential leaf must therefore adopt+release that moved-out +1 on
        // Dispose/finalize or it leaks; GetOwnedReturnElementConversion appends `ownsContainer: true`
        // for it and is a no-op (== non-owning form) for every scalar/non-existential leaf. The
        // non-owning GetReturnElementConversion stays reserved for the +0 borrowed SCALAR receiver
        // path (GetReceiverExistentialSetterConversion's standalone arm).
        var keyConv = dict.KeyProjection.GetOwnedReturnElementConversion("kvp.Key");
        var valConv = dict.ValueProjection.GetOwnedReturnElementConversion("kvp.Value");

        // SwiftDictionary<K,V> implements IReadOnlyDictionary, not IDictionary.
        // Receiver params need IDictionary, so always materialize via .ToDictionary().
        var keyExpr = keyConv ?? "kvp.Key";
        var valueExpr = valConv ?? "kvp.Value";

        // Cast values to the public interface type to satisfy invariant Dictionary<K,V>.
        // e.g., Dictionary<string, RowAdapterProxy> doesn't satisfy IDictionary<string, IRowAdapter>
        // even though RowAdapterProxy : IRowAdapter, because Dictionary is invariant.
        var valPubType = dict.ValueProjection.PublicType;
        var keyPubType = dict.KeyProjection.PublicType;
        return $"{rawArgName}.ToDictionary(kvp => ({keyPubType}){keyExpr}, kvp => ({valPubType}){valueExpr})";
    }

    /// <summary>
    /// Converts C# idiomatic value to Swift ABI for MarshalToSwiftBuffer in getter receivers.
    /// Dispatches on projection type for whole-value conversion.
    /// Returns null if no conversion needed (passthrough).
    /// </summary>
    private string? GetReceiverGetterConversion(string varName, TypeSpec? typeSpec)
    {
        if (typeSpec == null) return null;

        // Check existential first — they're more specific
        var existentialConv = GetReceiverExistentialGetterConversion(varName, typeSpec);
        if (existentialConv != null) return existentialConv;

        var projection = s_projectionFactory.Project(typeSpec,
            new ProjectionContext { TypeDatabase = _typeDatabase, IsParameter = true, CurrentModuleName = _moduleName, EmissionContext = _emissionContext });
        if (projection == null) return null;

        return projection.Accept(new ReceiverGetterConversionVisitor(varName, this));
    }

    private string? GetReceiverSetGetterConversion(SetProjection set, string varName)
    {
        // ObjC-bridgeable element (e.g. Set<URL>): the whole container bridges to an NSSet handed to
        // Swift as a +1-retained NS pointer (the reverse of the forward accessor's NS-bridge), NOT a
        // layout-incompatible SwiftSet<IntPtr>. The Swift thunk balances the retain with takeRetainedValue().
        if (set.UsesObjCContainerBridge)
            return set.GetReverseReceiverObjCBridgeConversion(varName);
        var rawElem = set.ElementProjection.SwiftContainerGenericType;
        var elemConv = set.ElementProjection.GetParameterElementConversion("e");
        // Skip per-element conversion when SwiftContainerGenericType matches the C# public type
        // (e.g. NonFrozenStruct): SwiftSet<TWrapper>.FromEnumerable wants typed wrappers directly so
        // ISwiftObject.MarshalToSwift copies the struct's payload bytes by value into each slot.
        // Mirrors the SetProjection.BuildContainerSetup skip-conversion rule.
        if (elemConv != null && rawElem != set.ElementProjection.PublicType)
            return $"SwiftSet<{rawElem}>.FromEnumerable({varName}.Select(e => {elemConv}))";
        return $"SwiftSet<{rawElem}>.FromEnumerable({varName})";
    }

    private string? GetReceiverArrayGetterConversion(ArrayProjection arr, string varName)
    {
        // ObjC-bridgeable element (e.g. Array<URL>): whole container bridges to a +1-retained NSArray
        // pointer (see GetReceiverSetGetterConversion), not a layout-incompatible SwiftArray<IntPtr>.
        if (arr.UsesObjCContainerBridge)
            return arr.GetReverseReceiverObjCBridgeConversion(varName);
        var rawElem = arr.ElementProjection.SwiftContainerGenericType;
        var elemConv = arr.ElementProjection.GetParameterElementConversion("e");
        // Same skip-conversion rule as ArrayProjection.BuildContainerSetup.
        if (elemConv != null && rawElem != arr.ElementProjection.PublicType)
            return $"SwiftArray<{rawElem}>.FromEnumerable({varName}.Select(e => {elemConv}))";
        return $"SwiftArray<{rawElem}>.FromEnumerable({varName})";
    }

    private string? GetReceiverDictGetterConversion(DictionaryProjection dict, string varName)
    {
        // ObjC-bridgeable key or value (e.g. Dictionary<String,URL>): whole container bridges to a
        // +1-retained NSDictionary pointer (see GetReceiverSetGetterConversion), not a layout-
        // incompatible SwiftDictionary<IntPtr,IntPtr>.
        if (dict.UsesObjCContainerBridge)
            return dict.GetReverseReceiverObjCBridgeConversion(varName);
        var rawK = dict.KeyProjection.SwiftContainerGenericType;
        var rawV = dict.ValueProjection.SwiftContainerGenericType;
        var keyConv = dict.KeyProjection.GetParameterElementConversion("kvp.Key");
        var valConv = dict.ValueProjection.GetParameterElementConversion("kvp.Value");
        // Same skip-conversion rule as DictionaryProjection.BuildContainerSetup — when
        // SwiftContainerGenericType matches the C# public type for a key or value projection,
        // FromDictionary expects typed wrappers (not IntPtr handles) and ISwiftObject.MarshalToSwift
        // copies struct payload bytes by value via VWT.
        var skipKeyConv = keyConv != null && rawK == dict.KeyProjection.PublicType;
        var skipValConv = valConv != null && rawV == dict.ValueProjection.PublicType;
        var effectiveKeyConv = skipKeyConv ? null : keyConv;
        var effectiveValConv = skipValConv ? null : valConv;
        if (effectiveKeyConv != null || effectiveValConv != null)
        {
            var keyExpr = effectiveKeyConv ?? "kvp.Key";
            var valExpr = effectiveValConv ?? "kvp.Value";
            return $"SwiftDictionary<{rawK}, {rawV}>.FromDictionary({varName}.Select(kvp => new KeyValuePair<{rawK}, {rawV}>({keyExpr}, {valExpr})))";
        }
        return $"SwiftDictionary<{rawK}, {rawV}>.FromDictionary({varName})";
    }

    private string? GetReceiverOptionalGetterConversion(OptionalProjection opt, string varName)
    {
        var inner = opt.InnerProjection;
        var optType = inner.SwiftContainerGenericType;
        // Arms that hand the wrapper VALUE straight to NewSome (NonFrozenStruct, blittable/enum,
        // FrozenWithMemory) need the inner's metadata-bearing wrapper type as the generic. This equals
        // SwiftContainerGenericType for all of them EXCEPT FrozenWithMemoryProjection, whose
        // SwiftContainerGenericType is the by-value `.Buffer` struct (nonexistent for a handle-backed
        // wrapper such as SwiftClosedRange<T>). The handle-passing arms (Class/KeyPath/ObjC) keep
        // optType, which is the nil-pointer-optimized IntPtr.
        var passthroughOptType = inner.MarshalFromSwiftType;
        return inner switch
        {
            StringProjection => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome(new SwiftString({varName}Val)) : SwiftOptional<{optType}>.NewNone())",
            DataProjection => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome(Swift.Foundation.Data.FromByteArray({varName}Val)) : SwiftOptional<{optType}>.NewNone())",
            DateProjection => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome(({varName}Val - {DateProjection.SwiftEpoch}).TotalSeconds) : SwiftOptional<{optType}>.NewNone())",
            NativeRemappedProjection nrp => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome({(nrp.FromFactoryMethod != null ? $"{nrp.SwiftWrapperType}.{nrp.FromFactoryMethod}({varName}Val)" : $"new {nrp.SwiftWrapperType}({varName}Val)")}) : SwiftOptional<{optType}>.NewNone())",
            // Scalar ObjC-CLASS Optional return (NSURLSession & friends): Optional<class-reference> is a
            // nil-pointer-optimized single-word slot, so it coincides with SwiftOptional<IntPtr>'s marshaled
            // layout. The Swift thunk consumes it via the raw-buffer move() arm, which expects a +1-owned
            // slot; NewSome(Handle) alone deposits a borrowed +0 handle, so transfer a +1 ARC retain
            // (Arc.UnknownObjectRetain → the pointer, isa-dispatched swift_unknownObjectRetain) to balance
            // the move — symmetric with the non-optional scalar arm above and the container path.
            ObjCBridgedProjection => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome(global::Swift.Runtime.Arc.UnknownObjectRetain({varName}Val.Handle)) : SwiftOptional<{optType}>.NewNone())",
            // Scalar ObjC-BRIDGEABLE VALUE Optional return (Foundation.URL, NS_TYPED_ENUM newtypes):
            // Optional<URL> is a MULTI-WORD resilient value type, NOT a nil-pointer-optimized single-word
            // slot. Depositing a SwiftOptional<IntPtr> (one word) and reading it Swift-side as Optional<URL>
            // via move() reads past the buffer and reinterprets unrelated bytes as a URL value → corrupt URL
            // → SIGSEGV when the caller touches it. Instead deposit a bare optional ObjC POINTER: a +1
            // retained handle for .some, IntPtr.Zero for .none. MarshalToSwiftBuffer<IntPtr> writes exactly
            // one raw word and the Swift thunk reads it as an UnsafeRawPointer? (0 = nil), then bridges the
            // live NSURL into a URL at +1 — symmetric with the non-optional bridgeable arm. The paired Swift
            // pointer-optional read is emitted for the same optional-bridgeable-VALUE shape.
            ObjCBridgeableProjection => $"({varName} is {{}} {varName}Val ? global::Swift.Runtime.Arc.UnknownObjectRetain({varName}Val.Handle) : global::System.IntPtr.Zero)",
            ArrayProjection arr => BuildOptionalContainerGetterConversion(arr, varName, optType,
                GetReceiverArrayGetterConversion(arr, $"{varName}Val")),
            DictionaryProjection dict => BuildOptionalContainerGetterConversion(dict, varName, optType,
                GetReceiverDictGetterConversion(dict, $"{varName}Val")),
            SetProjection set => BuildOptionalContainerGetterConversion(set, varName, optType,
                GetReceiverSetGetterConversion(set, $"{varName}Val")),
            // Closures have their own ABI (SwiftClosureData/function pointers) — can't wrap in SwiftOptional.
            // Passthrough; accessor methods handle closure marshalling.
            ClosureProjection => null,
            // ObjC-rooted classes use .Handle (ObjC pointer), not .Payload. Same +1 transfer retain as
            // the ObjCBridged/ObjCBridgeable Optional arms above: the Swift move() arm expects a +1-owned
            // slot, so retain the borrowed handle before depositing it.
            ObjCRootedClassProjection => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome(global::Swift.Runtime.Arc.UnknownObjectRetain({varName}Val.Handle)) : SwiftOptional<{optType}>.NewNone())",
            // Class: OptionalProjection.SwiftContainerGenericType returns "IntPtr" (nil-pointer-optimized),
            // so optType=IntPtr and we pass a raw IntPtr handle.
            ClassProjection => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome({varName}Val.Payload.DangerousGetHandle()) : SwiftOptional<{optType}>.NewNone())",
            // KeyPath: same shape as ClassProjection but the wrapper IS the SafeHandle (no
            // .Payload indirection). OptionalProjection still uses IntPtr as the container
            // generic type because KeyPaths are nil-pointer-optimized reference classes.
            KeyPathProjection => $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome({varName}Val.DangerousGetHandle()) : SwiftOptional<{optType}>.NewNone())",
            // NonFrozenStruct: optType IS the typed wrapper (NonFrozenStructProjection.SwiftContainerGenericType
            // returns _typeName). SwiftOptional<TWrapper>.NewSome takes the typed wrapper directly so
            // ISwiftObject.MarshalToSwift copies the struct's payload bytes by value via VWT.
            // Lowering the Some-arg to .Payload.DangerousGetHandle() would type-mismatch (passing IntPtr
            // where the typed wrapper is expected) — same ABI-mismatch class as passing raw IntPtr slots
            // for ISwiftObject elements instead of the typed wrapper.
            NonFrozenStructProjection => $"({varName} is {{}} {varName}Val ? SwiftOptional<{passthroughOptType}>.NewSome({varName}Val) : SwiftOptional<{passthroughOptType}>.NewNone())",
            // Blittable, SimpleEnum, etc. — MarshalToSwiftBuffer writes raw bytes via Unsafe.Write<T>,
            // so C# int? (Nullable<int>) is NOT layout-compatible with SwiftOptional<int> (a class).
            // Must explicitly wrap in SwiftOptional<T>.NewSome/NewNone.
            _ => $"({varName} is {{}} {varName}Val ? SwiftOptional<{passthroughOptType}>.NewSome({varName}Val) : SwiftOptional<{passthroughOptType}>.NewNone())"
        };
    }

    private static string? BuildOptionalContainerGetterConversion(ITypeProjection inner, string varName, string optType, string? innerConv)
    {
        if (innerConv == null) return null;
        return $"({varName} is {{}} {varName}Val ? SwiftOptional<{optType}>.NewSome({innerConv}) : SwiftOptional<{optType}>.NewNone())";
    }

    /// <summary>
    /// Issue #40: route a Swift-class (or <c>Optional&lt;class&gt;</c>) reverse-callback
    /// parameter through the runtime borrowed-slot copy-out instead of the per-proxy local
    /// <c>MarshalFromSwift&lt;T&gt;</c> (which does <c>Unsafe.Read&lt;T&gt;</c> and reinterprets the Swift
    /// heap pointer as a managed reference → SIGSEGV on first use). <paramref name="slotExpr"/> is the
    /// receiver's raw <c>IntPtr</c> argument — the address of the borrowed slot the Swift thunk passed
    /// via <c>&amp;{param}Copy</c>. Applies to true Swift classes (pure-Swift <see cref="ClassProjection"/>
    /// and <c>@objc:NSObject</c> <see cref="ObjCRootedClassProjection"/>), whose ObjC-vs-native retain
    /// dispatch the runtime helper handles via <c>swift_unknownObjectRetain</c>. ObjC-<i>bridged</i> value
    /// types (<see cref="ObjCBridgedProjection"/>, e.g. NSURLSession) are NOT Swift heap classes and keep
    /// their existing <c>MarshalFromSwift&lt;IntPtr&gt;</c> + GetNSObject path. Returns the full RHS marshal
    /// expression, or <c>null</c> for any non-class param (caller keeps its own path).
    /// </summary>
    /// <summary>
    /// Raw materialization of a reverse-dispatch receiver parameter/value from its borrowed Swift slot.
    /// Reference-backed <c>ISwiftObject</c> collection wrappers (<c>SwiftArray</c>/<c>SwiftDictionary</c>/
    /// <c>SwiftSet</c>) arrive as the address of a borrowed value slot holding the storage pointer; the
    /// proxy-local <c>MarshalFromSwift&lt;T&gt;</c> (<c>Unsafe.Read&lt;T&gt;</c>) would reinterpret that
    /// storage pointer as a managed reference → garbage ref → NullReferenceException /
    /// <c>swift_abortRetainOverflow</c> SIGABRT. Materialize those via the runtime helper
    /// (<c>NewFromPayload</c> → <c>InitializeWithCopy</c>, a +1 owned copy of the borrowed slot whose
    /// finalizer rebalances the retain), exactly as the string param/value path and the forward return
    /// path already do. Blittable values and value-type existential containers keep the local
    /// <c>Unsafe.Read</c> fast path (<c>Unsafe.Read</c> is correct for value types).
    /// </summary>
    private string GetReceiverRawMaterialization(string abiTypeName, string slotExpr, TypeSpec? typeSpec)
    {
        if (ReceiverParamNeedsObjectMarshal(typeSpec))
            return $"global::Swift.Runtime.InteropServices.SwiftMarshal.MarshalFromSwiftObject<{abiTypeName}>({slotExpr})";
        return $"MarshalFromSwift<{abiTypeName}>({slotExpr})";
    }

    /// <summary>
    /// Optional ObjC-bridgeable VALUE (URL?, NS_TYPED_ENUM newtypes) arriving INTO a reverse-dispatch
    /// receiver — a settable property or a method param. This is the READ mirror of the reverse-RETURN
    /// optional-bridgeable arm (see GetReceiverOptionalGetterConversion's ObjCBridgeableProjection case):
    /// the Swift thunk borrows the bridged NSObject and passes a single optional ObjC POINTER (nil = 0x0)
    /// occupying one nil-pointer-optimized word, NOT the multi-word resilient Optional&lt;URL&gt; bytes. So
    /// the receiver must read that borrowed slot as a bare <c>IntPtr</c> (one word) and bridge the live
    /// NSObject at +0 — <c>GetNSObject</c> does NOT consume a retain, matching the Swift-side
    /// <c>passUnretained</c> borrow. Reading it as <c>SwiftOptional&lt;IntPtr&gt;</c> (the default ABI carrier
    /// for an Optional) reinterprets the value's storage as a two-word case+payload → wrong pointer →
    /// corruption/SIGSEGV on the <c>.some</c> case. The marshal type and the conversion are returned
    /// together so they can never drift. Optional&lt;class-reference&gt; (ObjCBridged / ObjCRooted / pure
    /// Swift class) is a genuine nil-pointer-optimized single-word <c>SwiftOptional&lt;IntPtr&gt;</c> and is
    /// deliberately NOT handled here — it keeps its existing path. Returns false for every other shape.
    /// </summary>
    private bool TryGetReceiverOptionalObjCBridgeableValueRead(TypeSpec? typeSpec, string slotExpr, string varName, out string marshalExpr, out string convExpr)
    {
        marshalExpr = "";
        convExpr = "";
        if (typeSpec == null)
            return false;
        var projection = s_projectionFactory.Project(typeSpec,
            new ProjectionContext { TypeDatabase = _typeDatabase, IsParameter = true, CurrentModuleName = _moduleName, EmissionContext = _emissionContext });
        if (projection is not OptionalProjection { InnerProjection: ObjCBridgeableProjection objc })
            return false;
        marshalExpr = $"MarshalFromSwift<IntPtr>({slotExpr})";
        convExpr = $"({varName} == global::System.IntPtr.Zero ? null : {MarshallingHelpers.FormatObjCBridgeCall(objc.PublicType, varName, nonNull: true)})";
        return true;
    }

    /// <summary>
    /// True when a receiver parameter's ABI carrier is a reference-backed <c>ISwiftObject</c> collection
    /// wrapper (<c>SwiftArray</c>/<c>SwiftDictionary</c>/<c>SwiftSet</c>) that must be materialized through
    /// <c>NewFromPayload</c> rather than <c>Unsafe.Read</c>. The top-level projection KIND is the reliable
    /// discriminator (strings are already special-cased upstream; classes go through the copy-out helper).
    /// </summary>
    private bool ReceiverParamNeedsObjectMarshal(TypeSpec? typeSpec)
    {
        if (typeSpec == null) return false;
        var projection = s_projectionFactory.Project(typeSpec,
            new ProjectionContext { TypeDatabase = _typeDatabase, IsParameter = true, CurrentModuleName = _moduleName, EmissionContext = _emissionContext });
        return projection?.Accept(new ReceiverParamNeedsObjectMarshalVisitor()) ?? false;
    }

    private string? GetReceiverClassCopyOutExpr(string slotExpr, TypeSpec? typeSpec)
    {
        if (typeSpec == null) return null;

        var projection = s_projectionFactory.Project(typeSpec,
            new ProjectionContext { TypeDatabase = _typeDatabase, IsParameter = true, CurrentModuleName = _moduleName, EmissionContext = _emissionContext });
        if (projection == null) return null;

        return projection.Accept(new ReceiverClassCopyOutVisitor(slotExpr));
    }

    /// <summary>
    /// Converts Swift ABI value to C# idiomatic for interface assignment in setter receivers.
    /// Dispatches on projection type for whole-value conversion.
    /// Returns null if no conversion needed (passthrough).
    /// </summary>
    private string? GetReceiverSetterConversion(string varName, TypeSpec? typeSpec)
    {
        if (typeSpec == null) return null;

        // Check existential first
        var existentialConv = GetReceiverExistentialSetterConversion(varName, typeSpec);
        if (existentialConv != null) return existentialConv;

        var projection = s_projectionFactory.Project(typeSpec,
            new ProjectionContext { TypeDatabase = _typeDatabase, IsParameter = false, CurrentModuleName = _moduleName, EmissionContext = _emissionContext });
        if (projection == null) return null;

        return projection.Accept(new ReceiverSetterConversionVisitor(varName, this));
    }

    private string? GetReceiverArraySetterConversion(ArrayProjection arr, string varName)
    {
        // Owned (+1): the .AsProjected element read drives SwiftArray's subscript getter, whose
        // InitializeWithCopy moves each element out at +1 — an existential leaf must adopt+release it.
        // GetOwnedReturnElementConversion is a no-op for non-existential leaves.
        var elemConv = arr.ElementProjection.GetOwnedReturnElementConversion("e");
        if (elemConv != null)
            return $"{varName}.AsProjected(e => {elemConv})";
        return null;  // SwiftArray<T> IS IReadOnlyList<T> — no conversion needed
    }

    private string? GetReceiverSetSetterConversion(SetProjection set, string varName)
    {
        // Owned (+1): enumerating the SwiftSet moves each element out at +1; an existential leaf must
        // adopt it. (Set<any P> is ill-formed in Swift, so this is a no-op in practice — GetOwned*
        // falls back to the non-owning form for the Hashable scalar leaves a Set can actually hold.)
        var elemConv = set.ElementProjection.GetOwnedReturnElementConversion("e");
        if (elemConv != null)
            return $"{varName}.Select(e => {elemConv}).ToHashSet()";
        return null;  // SwiftSet<T> IS IReadOnlySet<T> — no conversion needed
    }

    private string? GetReceiverDictSetterConversion(DictionaryProjection dict, string varName)
    {
        // Owned (+1): the setter materialization moves each key/value out of the SwiftDictionary at +1
        // (entry enumerator / indexer) — a buried existential leaf (e.g. [String: [String: any P]])
        // must adopt+release it. No-op for non-existential leaves; see GetReceiverDictionaryConversion.
        var keyConv = dict.KeyProjection.GetOwnedReturnElementConversion("k");
        var valConv = dict.ValueProjection.GetOwnedReturnElementConversion("v");
        if (keyConv == null && valConv == null) return null;
        // Reuse DictionaryProjection.BuildAsProjected so the invariant value-slot cast (CastValueSelectorBody)
        // is applied identically here: a settable [String: [String: any P]] property feeds the converted
        // value into an IReadOnlyDictionary<…, IReadOnlyDictionary<…>> setter param whose value slot is
        // invariant, so a bare concrete-donor inner selector body would be CS0266 (same hazard the forward
        // return path solved). For non-container values BuildAsProjected emits the exact prior shape unchanged.
        return $"{varName}{dict.BuildAsProjected(keyConv, valConv)}";
    }

    private string? GetReceiverOptionalSetterConversion(OptionalProjection opt, string varName)
    {
        var inner = opt.InnerProjection;
        return inner switch
        {
            StringProjection => $"((SwiftString?){varName})?.ToString()",
            DataProjection => $"((Swift.Foundation.Data?){varName})?.ToByteArray()",
            DateProjection => $"((double?){varName}) is {{}} {varName}DateVal ? (System.DateTimeOffset?){DateProjection.SwiftEpoch}.AddSeconds({varName}DateVal) : null",
            NativeRemappedProjection nrp => $"(({nrp.SwiftWrapperType}?){varName})?.{nrp.ToConversionMethod}()",
            ObjCBridgedProjection objc => $"({varName}.Case == Swift.SwiftOptionalCases.None ? null : {MarshallingHelpers.FormatObjCBridgeCall(objc.PublicType, $"{varName}.Some", nonNull: true)})",
            // Optional ObjC-bridgeable VALUE (URL?, NS_TYPED_ENUM newtypes) is a MULTI-word resilient value,
            // NOT a nil-pointer-optimized one-word slot like the ObjCBridged CLASS arm above. Its
            // reverse-receiver read is owned by the coupled TryGetReceiverOptionalObjCBridgeableValueRead at
            // the property-setter / method-param emission sites (read one optional ObjC pointer word, +0
            // bridge). Reaching this arm would mean a value-materialization caller skipped that interception
            // and paired a .Case/.Some read with the default two-word SwiftOptional<IntPtr> marshal — the
            // exact layout mismatch the fix removed — so fail closed at generation rather than emit it.
            ObjCBridgeableProjection => throw new InvalidOperationException(
                "Optional<ObjC-bridgeable value> reverse-receiver read must be handled by TryGetReceiverOptionalObjCBridgeableValueRead (one-word optional ObjC pointer), not GetReceiverOptionalSetterConversion."),
            ArrayProjection arr => GetReceiverOptionalContainerSetterConversion(arr, varName, arr.PublicType),
            DictionaryProjection dict => GetReceiverOptionalContainerSetterConversion(dict, varName, dict.PublicType),
            SetProjection set => GetReceiverOptionalContainerSetterConversion(set, varName, set.PublicType),
            // Closures have their own ABI — passthrough, accessor methods handle marshalling.
            ClosureProjection => null,
            // Class/NonFrozenStruct: the Optional is already deserialized as SwiftOptional<PublicType>
            // via MarshalFromSwift — .Some returns PublicType, not IntPtr. Simple nullable cast suffices.
            _ => $"(({inner.PublicType}?){varName})"
        };
    }

    private string? GetReceiverOptionalContainerSetterConversion(ITypeProjection innerContainer, string varName, string idiomaticType)
    {
        var containerConv = innerContainer.GetReturnContainerConversion($"{varName}.Some");
        var someExpr = containerConv ?? $"{varName}.Some";
        // Cast the some arm to the idiomatic type to avoid ternary covariance issues.
        // e.g., Dictionary<string, TypeProxy> vs IReadOnlyDictionary<string, IType>
        return $"({varName}.Case == Swift.SwiftOptionalCases.None ? ({idiomaticType}?)null : ({idiomaticType}){someExpr})";
    }

    /// <summary>
    /// Gets a conversion expression for existential types in getter returns (C# idiomatic → Swift ABI).
    /// Uses TypeProjectionFactory to project the type, then extracts parameter element conversions
    /// (public → ABI direction) for each existential composition pattern.
    /// Returns null if the type is not an existential or doesn't need conversion.
    /// </summary>
    private string? GetReceiverExistentialGetterConversion(string varName, TypeSpec? typeSpec)
    {
        if (typeSpec == null) return null;

        var projection = s_projectionFactory.Project(typeSpec,
            new ProjectionContext { TypeDatabase = _typeDatabase, IsParameter = true, CurrentModuleName = _moduleName, EmissionContext = _emissionContext });
        if (projection == null) return null;

        // Standalone existential. This is the getter RETURN direction (C#→Swift, +1 owned), so mint
        // an owned container rather than borrow the proxy's R0 — matching the array/dictionary arms
        // below, which already route through the owned GetArrayElementCarrierConversion. Note this is
        // the owned C#→Swift (Parameter) transform, NOT the owned Swift→C# (Return) adopt used by the
        // collection SETTER arms — see the two methods' docs in ExistentialProjection.
        if (projection is ExistentialProjection existProj)
            return existProj.GetOwnedParameterElementConversion(varName);

        // Optional<existential> — same owned C#→Swift direction; mint the inner +1.
        if (projection is OptionalProjection optProj && optProj.InnerProjection is ExistentialProjection innerExist)
        {
            var containerType = innerExist.PInvokeType;
            var extractExpr = innerExist.GetOwnedParameterElementConversion($"{varName}Val");
            return $"({varName} is {{}} {varName}Val ? SwiftOptional<{containerType}>.NewSome({extractExpr}) : SwiftOptional<{containerType}>.NewNone())";
        }

        // Array<existential>. Carrier + per-element conversion must agree on stride: a class-bound
        // single-protocol element uses the 16-byte ClassExistentialContainer1 (matching the read
        // direction's ArrayElementCarrierType), with the element narrowed from the proxy-produced
        // ExistentialContainer1; opaque/composition elements stay on the 40-byte carrier (no-op).
        if (projection is ArrayProjection arrProj && arrProj.ElementProjection is ExistentialProjection arrExist)
        {
            var containerType = arrExist.ArrayElementCarrierType;
            var elemConv = arrExist.GetArrayElementCarrierConversion("i");
            return $"SwiftArray<{containerType}>.FromEnumerable({varName}.Select(i => {elemConv}))";
        }

        // Set<existential>. Same owned-carrier + stride agreement as the array path above. This arm is
        // REQUIRED here (not in the legacy GetReceiverSetGetterConversion switch fall-through, which has
        // no existential awareness): a class-bound single-protocol element uses the 16-byte
        // ClassExistentialContainer1, opaque/composition elements stay on the 40-byte carrier (no-op).
        if (projection is SetProjection setProj && setProj.ElementProjection is ExistentialProjection setExist)
        {
            var containerType = setExist.ArrayElementCarrierType;
            var elemConv = setExist.GetArrayElementCarrierConversion("i");
            return $"SwiftSet<{containerType}>.FromEnumerable({varName}.Select(i => {elemConv}))";
        }

        // Dictionary<K, existential>. Carrier + per-value conversion must agree on stride, exactly like
        // the array path above: a class-bound single-protocol VALUE uses the 16-byte
        // ClassExistentialContainer1 (matching DictionaryProjection's carrier), with the value narrowed
        // via the owned CreateOwnedClassCarrier;
        // opaque/composition values stay on the 40-byte carrier (no-op). Keys are never class-bound
        // existentials (`any P` is not Hashable), so only the value carrier changes.
        if (projection is DictionaryProjection dictProj && dictProj.ValueProjection is ExistentialProjection dictExist)
        {
            var containerType = dictExist.ArrayElementCarrierType;
            var keyConv = dictProj.KeyProjection.GetParameterElementConversion("kvp.Key");
            var abiKeyType = dictProj.KeyProjection.SwiftContainerGenericType;
            // Skip per-element key conversion when SwiftContainerGenericType matches the C# public type
            // (e.g. NonFrozenStruct like Swift.AnyHashable): SwiftDictionary<TWrapper, V>.FromDictionary
            // wants typed wrappers directly so ISwiftObject.MarshalToSwift copies the struct's payload
            // bytes by value into each slot. Lowering to .Payload.DangerousGetHandle() would yield
            // Dictionary<nint, V> and fail to convert to IEnumerable<KeyValuePair<TWrapper, V>>.
            // Mirrors the skip-conversion rule in DictionaryProjection.BuildContainerSetup and
            // BuildDictionarySetterReceiverExpression.
            var skipKeyConv = keyConv != null && abiKeyType == dictProj.KeyProjection.PublicType;
            var keyExpr = skipKeyConv ? "kvp.Key" : (keyConv ?? "kvp.Key");
            var valConv = dictExist.GetArrayElementCarrierConversion("kvp.Value");
            return $"SwiftDictionary<{abiKeyType}, {containerType}>.FromDictionary({varName}.ToDictionary(kvp => {keyExpr}, kvp => {valConv}))";
        }

        return null;
    }

    /// <summary>
    /// Gets a conversion expression for existential types in setter params (Swift ABI → C# idiomatic).
    /// Uses TypeProjectionFactory to project the type, then extracts return element conversions
    /// (ABI → public direction) for each existential composition pattern.
    /// Returns null if the type is not an existential or doesn't need conversion.
    /// </summary>
    private string? GetReceiverExistentialSetterConversion(string varName, TypeSpec? typeSpec)
    {
        if (typeSpec == null) return null;

        var projection = s_projectionFactory.Project(typeSpec,
            new ProjectionContext { TypeDatabase = _typeDatabase, IsParameter = false, CurrentModuleName = _moduleName, EmissionContext = _emissionContext });
        if (projection == null) return null;

        // Standalone existential
        if (projection is ExistentialProjection existProj)
            return existProj.GetReturnElementConversion(varName);

        // Optional<existential>
        if (projection is OptionalProjection optProj && optProj.InnerProjection is ExistentialProjection innerExist)
        {
            var publicType = innerExist.PublicType;
            var wrapExpr = innerExist.GetReturnElementConversion($"{varName}.Some");
            return $"({varName}.Case == Swift.SwiftOptionalCases.None ? null : ({publicType}?){wrapExpr})";
        }

        // Array<existential> — COLLECTION element, NOT the scalar standalone arm above. The
        // .AsProjected read moves each element out of the SwiftArray at +1 (subscript getter
        // InitializeWithCopy), so the existential leaf must adopt+release it via the owned form;
        // the +0 borrowed standalone/Optional arms above deliberately keep the non-owning form.
        if (projection is ArrayProjection arrProj && arrProj.ElementProjection is ExistentialProjection arrExist)
        {
            var publicType = arrExist.PublicType;
            var elemConv = arrExist.GetOwnedReturnElementConversion("c");
            return $"{varName}.AsProjected<{publicType}>(c => {elemConv})";
        }

        // Dictionary<K, existential> — COLLECTION value, moved out of the SwiftDictionary at +1 by
        // .ToDictionary()'s entry enumerator (MarshalMovedValueFromSlot), so the existential value
        // leaf adopts+releases it via the owned form (no-op for the scalar key leaf).
        if (projection is DictionaryProjection dictProj && dictProj.ValueProjection is ExistentialProjection dictExist)
        {
            var publicType = dictExist.PublicType;
            var valConv = dictExist.GetOwnedReturnElementConversion("kvp.Value");
            var keyConv = dictProj.KeyProjection.GetOwnedReturnElementConversion("kvp.Key");
            var keyExpr = keyConv ?? "kvp.Key";
            return $"{varName}.ToDictionary(kvp => {keyExpr}, kvp => ({publicType}){valConv})";
        }

        return null;
    }

    /// <summary>
    /// Checks if a TypeSpec represents Swift.String.
    /// String returns from proxy receivers use Utf8Slice encoding instead of MarshalToSwiftBuffer,
    /// because SwiftString contains ARC-managed references that Unsafe.Write can't retain.
    /// </summary>
    private static bool IsStringTypeSpec(TypeSpec typeSpec)
    {
        return typeSpec is NamedTypeSpec nts && nts.Name == "Swift.String";
    }

    /// <summary>
    /// Returns true if the protocol's existential is class-bound (2-word
    /// <c>[classRef][witnessTable]</c> layout). Reads <see cref="TypeRecordFlags.ClassBound"/>
    /// off the protocol's own TypeRecord; ModuleProcessor walks the inheritance chain
    /// when setting the flag so a child protocol inheriting class-boundedness from any
    /// ancestor (issue #40) is correctly classified here without a second walk.
    /// Also used by ProtocolProxyEmitter.SwiftObject.cs for the Swift→C# wrap factory.
    /// </summary>
    private bool IsProtocolClassBound(ProtocolDecl protocolDecl)
    {
        if (protocolDecl.SwiftTypeName != null
            && _typeDatabase.TryGetTypeRecord(protocolDecl.SwiftTypeName, out var record)
            && record.Kind == TypeRecordKind.Protocol)
        {
            return (record.Flags & TypeRecordFlags.ClassBound) != 0;
        }
        // Defensive fallback for emit paths that run before the TypeRecord is
        // registered (direct-emitter test fixtures and external callers). Only
        // the directly-declared bit is available here; transitive class-boundedness
        // requires the parse-time walk's TypeRecord flag.
        return protocolDecl.IsClassBound;
    }

    /// <summary>
    /// Records the class-bound protocol's descriptor symbol + exporting library for
    /// <c>ClassExistentialContainer1</c> metadata registration in the module initializer.
    /// No-op for opaque protocols and when the descriptor symbol or library can't be resolved
    /// (cross-module protocols whose defining module isn't in the database). The carrier metadata
    /// is protocol-agnostic for the arity, so a single successful registration covers every
    /// class-bound existential in the module.
    /// </summary>
    private void RecordClassBoundExistentialMetadata(ProtocolDecl protocolDecl)
    {
        if (_emissionContext == ModuleEmissionContext.Default)
            return;
        if (!IsProtocolClassBound(protocolDecl))
            return;
        if (protocolDecl.SwiftTypeName == null
            || !_typeDatabase.TryGetTypeRecord(protocolDecl.SwiftTypeName, out var record)
            || record.Kind != TypeRecordKind.Protocol
            || string.IsNullOrEmpty(record.ProtocolDescriptorSymbol))
            return;

        // @objc protocols export no Swift `…Mp` descriptor and never marshal through the
        // ClassExistentialContainer1 carrier (IsClassBoundArity1Existential returns false for
        // them), so registering its metadata would load a nonexistent symbol and is unused.
        if ((record.Flags & TypeRecordFlags.ObjCProtocol) != 0)
            return;

        var moduleName = protocolDecl.ModuleDecl?.Name ?? protocolDecl.SwiftTypeName.Module;
        if (string.IsNullOrEmpty(moduleName))
            return;

        string libraryName;
        try
        {
            libraryName = _typeDatabase.GetLibraryPath(moduleName);
        }
        catch
        {
            // Defining module not in the database (cross-module protocol vended by a
            // dependency the binding doesn't own a database for) — skip; registration
            // is best-effort and one class-bound protocol's descriptor suffices.
            return;
        }

        _emissionContext.RecordClassBoundExistentialRegistration(libraryName, record.ProtocolDescriptorSymbol!);
    }

    private void EmitConstructors(CSharpWriter writer, ProtocolDecl protocolDecl, string interfaceName)
    {
        var proxyClassName = GetProxyClassName(protocolDecl);

        // Class-bound protocols (: AnyObject) use a 2-word existential layout:
        //   [classRef] [witnessTable]
        // Opaque protocols use a 5-word layout:
        //   [payload0] [payload1] [payload2] [metadata] [witnessTable]
        // We always allocate ExistentialContainer1 (5 words) but fill the first N words
        // according to the protocol's existential layout.
        //
        // _useObjCBase protocols (NSObjectProtocol-rooted via EveryObjCProtocol) are
        // class-bound by construction even when protocolDecl.IsClassBound is false
        // (the `IsClassBound` bool on the model only reflects an explicit AnyObject /
        // : class constraint — it does not transitively detect NSObjectProtocol
        // inheritance). Swift loads `any P` for these as a 2-word ObjC protocol
        // existential, so the witness table must land at Payload1, not _witnessTable0.
        //
        // Class-boundedness inherits transitively: `protocol Child: Parent` where Parent
        // is `: AnyObject` makes Child class-bound too. ModuleProcessor walks the chain
        // when setting TypeRecordFlags.ClassBound, so reading the flag here is sufficient.
        // Without the 2-word layout, Swift reads WT from Payload1 (which C# leaves zero)
        // → SIGSEGV on the first witness dispatch (@objc:NSObject reverse-dispatch repro, issue #40).
        var useClassBoundContainerLayout = IsProtocolClassBound(protocolDecl) || _useObjCBase;
        // Opaque (5-word) proxies read their own module's EveryProtocol metadata from the
        // per-proxy static field s_everyProtocolMetadata (Finding 33 — see the field declaration
        // and rationale in ProtocolProxyEmitter.StaticInit.cs). The field initializer fetches the
        // handle from this module's NativeMethods accessor, so there is no process-global
        // first-wins latch and no priming step in the ctor.
        var containerInitLines = useClassBoundContainerLayout
            ? "_swiftContainer.Payload1 = (IntPtr)ProtocolWitnessTableHandle;"
            : "_swiftContainer.ObjectMetadata = s_everyProtocolMetadata;\n                _swiftContainer[0] = ProtocolWitnessTableHandle;";

        // Constructor for C# implementation.
        if (_isReadOnlyProxy)
        {
            // A read-only (Swift-vended-only) proxy has NO reverse EveryProtocol conformance:
            // the @_cdecl Create{...} factory and ProtocolWitnessTableHandle it would need are
            // never emitted, so the C#→Swift synthesis direction is unsupported. Fail fast with
            // a clear NotSupportedException as the FIRST statement, before touching the dangling
            // Create P/Invoke (calling it would surface as an opaque EntryPointNotFoundException).
            // The supported direction is the forward read via the ExistentialContainer1 ctor below.
            writer.WriteLines($$"""
                /// <summary>
                /// Not supported. A C# type cannot conform back to {{interfaceName}}: this proxy is
                /// forward-only (read-only), wrapping Swift-vended <c>any {{interfaceName}}</c> values.
                /// </summary>
                /// <param name="implementation">Unused; this constructor always throws.</param>
                public {{proxyClassName}}({{interfaceName}} implementation)
                {
                    throw new global::System.NotSupportedException(
                        "Cannot create a Swift-backed proxy from a C# implementation of '{{interfaceName}}': "
                        + "this protocol is forward-only (read-only). Swift-vended 'any {{interfaceName}}' values "
                        + "can be read, but a C# type cannot conform back to it.");
                }

                """);
        }
        else
        {
        writer.WriteLines($$"""
            /// <summary>
            /// Creates a proxy wrapping a C# implementation of {{interfaceName}}.
            /// </summary>
            /// <param name="implementation">The C# implementation of the protocol.</param>
            public unsafe {{proxyClassName}}({{interfaceName}} implementation)
            {
                if (implementation == null) throw new ArgumentNullException(nameof(implementation));
                // Design B2: the impl is rooted by Swift-liveness through ProxyLifetimeTracker's
                // strong handle-keyed GCHandle (allocated in Track below), NOT by this proxy.
                // _csharpImplRef stays WEAK and exists only to satisfy the covariant
                // IProtocolProxyImpl<T>.UserImpl cross-module contract — reverse dispatch resolves
                // the impl via ProxyLifetimeTracker.ResolveImpl, never through this field.
                _csharpImplRef = new WeakReference<{{interfaceName}}>(implementation);

                // Create a real Swift {{(_useObjCBase ? "EveryObjCProtocol" : "EveryProtocol")}} instance via @_cdecl factory.
                // The pointer carries a construction +1 (R0) from Unmanaged.passRetained(). We hold
                // it as a plain IntPtr. Under Design B2, R0 is owned by THIS proxy and released on
                // the proxy's finalizer/Dispose via ProxyLifetimeTracker.ReleaseHandle (the
                // finalizer-safe Cdecl trampoline). Releasing R0 drives Swift's last retain to zero,
                // which fires EveryProtocol.deinit -> OnEveryProtocolDeinit, freeing the impl's
                // strong root and dropping the (weak) SwiftObjectRegistry entry.
                _everyProtocolHandle = NativeMethods.{{CreateHelperMethodName}}();
                _ownsEveryProtocolR0 = true;

                try
                {
                    // Create existential container manually. Opaque (5-word) layouts store this
                    // module's own EveryProtocol metadata (s_everyProtocolMetadata, Finding 33);
                    // class-bound (NSObjectProtocol-rooted) layouts don't consult ObjectMetadata.
                    _swiftContainer = new ExistentialContainer1();
                    _swiftContainer.Payload0 = _everyProtocolHandle;
                    {{containerInitLines}}

                    // Register this proxy WEAKLY so Swift callbacks can find us while the consumer
                    // holds a reference, but the proxy can still be collected once dropped — its
                    // collection is the signal that releases R0 (see the lifetime analysis in
                    // ProxyLifetimeTracker). The entry is dropped when OnEveryProtocolDeinit fires.
                    SwiftObjectRegistry.Register(_everyProtocolHandle, this);

                    // Wire Swift deinit -> C# callback. The context arg is the handle
                    // itself, so OnEveryProtocolDeinit can locate the registry entry
                    // and tracker bookkeeping for targeted teardown.
                    NativeMethods.{{SetDeinitCallbackMethodName}}(
                        _everyProtocolHandle,
                        &Swift.Runtime.ProxyLifetimeTracker.OnEveryProtocolDeinit,
                        _everyProtocolHandle);

                    // Root the impl by Swift-liveness (strong GCHandle keyed by handle) and record
                    // the R0 entry. Tracker must be called AFTER the deinit callback is wired up so
                    // that a super-fast Swift release (e.g., never-stored call) still routes through
                    // OnEveryProtocolDeinit before the finalizer path runs.
                    Swift.Runtime.ProxyLifetimeTracker.Track(implementation, _everyProtocolHandle);
                }
                catch
                {
                    // Ctor failed before tracker/registry wiring was complete — release R0
                    // directly to avoid leaking the Swift instance. This runs on a normal thread
                    // (not the finalizer), so the direct CallConvSwift Arc.Release is safe here.
                    SwiftObjectRegistry.Unregister(_everyProtocolHandle);
                    try { global::Swift.Runtime.Arc.Release(_everyProtocolHandle); } catch { /* already deallocating */ }
                    throw;
                }
                // Design B2: do NOT suppress finalization. The finalizer is what releases R0 when
                // the consumer drops the proxy without disposing it.
                Swift.Runtime.SwiftDisposeScope.TryRegister(this);
            }

            """);
        }

        // The container ctor is the forward-read path, emitted for ALL proxies INCLUDING
        // read-only ones: it wraps a Swift-vended `any P` existential and needs no reverse
        // dispatch, so it is the sole supported constructor for a read-only proxy.
        writer.WriteLines($$"""
            /// <summary>
            /// Creates a proxy from an existing Swift existential container.
            /// This constructor is used internally by generated marshalling code.
            /// </summary>
            /// <remarks>
            /// Swift-backed proxies created with this constructor dispatch blittable and String
            /// protocol members through witness table accessors. Non-dispatchable members
            /// (non-blittable non-String types, throwing, async) throw <see cref="NotSupportedException"/>.
            /// </remarks>
            /// <param name="container">The Swift existential container.</param>
            /// <param name="ownsContainer">
            /// True when this proxy ADOPTS a Swift-returned existential at +1 (set by the
            /// owned-return marshalling paths). The proxy then owns the container's
            /// value-witness retains and releases them on Dispose/finalize. Defaults to
            /// false: borrowed parameter wraps, payload-pointer reads, and externally
            /// constructed/synthetic containers do NOT own a +1 and must not be released.
            /// </param>
            [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
            public {{proxyClassName}}(ExistentialContainer1 container, bool ownsContainer = false)
            {
                _swiftContainer = container;
                _csharpImplRef = null;
                _everyProtocolHandle = IntPtr.Zero;
                // Swift-backed proxies do NOT own a construction +1 (R0) — they wrap a container
                // Swift already owns — so the finalizer/Dispose must not call ReleaseHandle.
                _ownsEveryProtocolR0 = false;
                _ownsContainer = ownsContainer;
                // Only an owning proxy has anything to release; suppress the finalizer for
                // borrowed/synthetic containers so they never queue a no-op finalize (and,
                // critically, never run a value-witness Destroy on a container they don't own).
                if (!ownsContainer)
                    GC.SuppressFinalize(this);
                Swift.Runtime.SwiftDisposeScope.TryRegister(this);
            }

            """);
    }

    /// <summary>
    /// Opens a <c>try</c> block guarding the managed body of an <c>[UnmanagedCallersOnly]</c>
    /// receiver. A managed exception that unwinds across the native (Swift) call boundary is
    /// undefined behaviour — the process aborts with a corrupted, undiagnosable stack. Pair
    /// with <see cref="EmitUcoGuardCloseFailFast"/> to convert any such escape into a
    /// controlled <see cref="System.Environment.FailFast(string, System.Exception)"/> that
    /// carries the original exception. Mirrors the closure-callback guard in ClosureEmitter.
    /// </summary>
    private static void EmitUcoGuardOpen(CSharpWriter writer)
    {
        UcoGuardEmitter.EmitOpen(writer);
    }

    /// <summary>
    /// Closes the <c>try</c> opened by <see cref="EmitUcoGuardOpen"/> with a catch that
    /// fail-fasts on any unhandled exception. The trailing <c>throw;</c> matches the proven
    /// non-throwing closure-callback shape and keeps value-returning receivers well-formed.
    /// </summary>
    private static void EmitUcoGuardCloseFailFast(CSharpWriter writer)
    {
        UcoGuardEmitter.EmitClose(writer, UcoGuardEmitter.UcoFaultPolicy.FailFast,
            exceptionVar: "__uco_ex", fullyQualified: true);
    }

    /// <summary>
    /// Closes the <c>try</c> opened by <see cref="EmitUcoGuardOpen"/> for a <b>legacy blocking
    /// async</b> protocol-requirement receiver with a member-named FailFast (Finding 36). This is the
    /// close for the fallback path only — async requirements the real reverse-async witness predicate
    /// rejects (<see cref="EveryProtocolEmitter.EmitsRealAsyncWitness"/>: non-primitive return,
    /// arity &gt; 4, generic/Self). Same fail-closed policy as <see cref="EmitUcoGuardCloseFailFast"/>
    /// — that fallback blocks on the synchronously-blocked reverse-dispatch slot (upstream Issue 1)
    /// and has no Swift error channel, so any escape is process-terminating — but it names
    /// <paramref name="member"/> and splits out <see cref="System.OperationCanceledException"/> so a
    /// cancellation token wired into the conformance produces an attributable diagnostic instead of an
    /// anonymous Swift-library crash. Primitive-shaped async requirements use the real reverse-async
    /// witness instead, which resumes the Swift continuation box with the error rather than FailFasting.
    /// </summary>
    private static void EmitUcoGuardCloseAsyncWitnessFailFast(CSharpWriter writer, string member)
    {
        UcoGuardEmitter.EmitCloseAsyncWitnessFailFast(writer, member,
            exceptionVar: "__uco_ex", fullyQualified: true);
    }

    /// <summary>
    /// Emits the "Design B2" reverse-dispatch preamble for a receiver that has NO sibling
    /// fallback: resolve the C# implementation from the handle-keyed strong root in
    /// <c>ProxyLifetimeTracker</c> and bind it to a non-null local <c>impl</c> typed as
    /// <paramref name="interfaceName"/>. The strong root keeps the impl alive for exactly as long
    /// as Swift references the proxy, so a null resolve here cannot happen in the canonical pattern;
    /// it signals that the impl was collected while Swift still held the proxy — a lifetime-invariant
    /// violation. Rather than silently fabricating a return value (Defect G's data-corruption failure
    /// mode), we trip the loud backstop <see cref="SwiftClosureMarshaller.FailFastDeadProxyImpl"/> via
    /// <c>throw</c> (the helper <see cref="System.Environment.FailFast(string)"/>s; the <c>throw</c> is
    /// unreachable but makes the compiler see <c>impl</c> as non-null downstream).
    /// <paramref name="memberDescription"/> names the protocol member for the crash diagnostic.
    /// </summary>
    private static void EmitResolveImplOrFailFast(CSharpWriter writer, string interfaceName,
        ProtocolDecl protocolDecl, string memberDescription)
    {
        writer.WriteLine($"var impl = Swift.Runtime.ProxyLifetimeTracker.ResolveImpl<{interfaceName}>(handle);");
        writer.WriteLine("if (impl is null)");
        writer.Indent++;
        writer.WriteLine($"throw global::Swift.Runtime.SwiftClosureMarshaller.FailFastDeadProxyImpl(\"Swift reverse-dispatch on {protocolDecl.Name}.{memberDescription} resolved no live C# implementation for EveryProtocol handle 0x\" + handle.ToString(\"X\") + \". The implementation was collected while Swift still held the proxy — a Design B2 lifetime-invariant violation (see ProxyLifetimeTracker).\");");
        writer.Indent--;
    }

    /// <summary>
    /// Emits the all-siblings-missed terminal of a sibling-fan-out receiver: the primary proxy and
    /// every recorded sibling proxy failed to resolve a live C# implementation from
    /// <c>ProxyLifetimeTracker</c>. This is the same Design B2 lifetime-invariant violation the
    /// no-sibling path catches in <see cref="EmitResolveImplOrFailFast"/> — the implementation was
    /// collected while Swift still held the proxy. Rather than fabricating a zero/empty return value
    /// (Defect G's silent data-corruption failure mode) it trips the loud
    /// <see cref="SwiftClosureMarshaller.FailFastDeadProxyImpl"/> backstop, emitted as
    /// <c>throw FailFastDeadProxyImpl(...)</c>. The helper <see cref="System.Environment.FailFast(string)"/>s
    /// (the process is gone before it returns) and the <c>throw</c> token supplies the receiver's
    /// terminal control-flow exit — required because C#'s definite-return analysis (CS0161) is purely
    /// syntactic and does NOT consult <c>[DoesNotReturn]</c>, so a bare helper call would leave a
    /// value-returning receiver short a terminal return even if the helper were so annotated.
    /// <paramref name="memberDescription"/> names the protocol member for the crash diagnostic.
    /// </summary>
    private static void EmitSiblingFanOutFailFast(CSharpWriter writer, ProtocolDecl protocolDecl,
        string memberDescription)
    {
        writer.WriteLine($"throw global::Swift.Runtime.SwiftClosureMarshaller.FailFastDeadProxyImpl(\"Swift reverse-dispatch on {protocolDecl.Name}.{memberDescription} resolved no live C# implementation for EveryProtocol handle 0x\" + handle.ToString(\"X\") + \" across the primary proxy and all sibling proxies. The implementation was collected while Swift still held the proxy — a Design B2 lifetime-invariant violation (see ProxyLifetimeTracker).\");");
    }

    /// <summary>
    /// Emits a try-lookup block for one interface in a sibling-property getter receiver.
    /// On lookup hit, materialises the property value, applies any conversion, and returns
    /// via the appropriate marshalling helper. On miss, falls through to the next sibling.
    /// </summary>
    private static void EmitGetterLookupHit(CSharpWriter writer, string interfaceName, string slug,
        string pascalPropertyName, string? getterConversion, bool isStringReturn)
    {
        var implVar = $"impl_{slug}";
        writer.WriteLine($"if (Swift.Runtime.ProxyLifetimeTracker.ResolveImpl<{interfaceName}>(handle) is {{}} {implVar})");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine($"var result = {implVar}.{pascalPropertyName};");
        if (isStringReturn)
            writer.WriteLine("return MarshalStringToUtf8Slice(result);");
        else if (getterConversion != null)
        {
            writer.WriteLine($"var swiftResult = {getterConversion};");
            writer.WriteLine("return MarshalToSwiftBuffer(swiftResult);");
        }
        else
        {
            writer.WriteLine("return MarshalToSwiftBuffer(result);");
        }
        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Emits a try-lookup block for one interface in a sibling-method receiver. On lookup hit,
    /// invokes the impl method (params threaded in via <paramref name="argsString"/>), then for a
    /// returning method applies any conversion and returns via the appropriate marshalling helper;
    /// for a void method calls and returns. On miss, falls through to the next sibling. Uses bare
    /// <c>result</c>/<c>swiftResult</c> (each block is its own C# scope) so the shared
    /// <paramref name="returnConv"/> expression — which references <c>result</c> — binds correctly.
    /// </summary>
    private static void EmitMethodLookupHit(CSharpWriter writer, string interfaceName, string slug,
        string pascalMethodName, string argsString, bool hasReturn, bool isStringReturn, string? returnConv)
    {
        var implVar = $"impl_{slug}";
        writer.WriteLine($"if (Swift.Runtime.ProxyLifetimeTracker.ResolveImpl<{interfaceName}>(handle) is {{}} {implVar})");
        writer.WriteLine("{");
        writer.Indent++;
        if (hasReturn)
        {
            writer.WriteLine($"var result = {implVar}.{pascalMethodName}({argsString});");
            if (isStringReturn)
            {
                writer.WriteLine("return MarshalStringToUtf8Slice(result);");
            }
            else if (returnConv != null)
            {
                writer.WriteLine($"var swiftResult = {returnConv};");
                writer.WriteLine("return MarshalToSwiftBuffer(swiftResult);");
            }
            else
            {
                writer.WriteLine("return MarshalToSwiftBuffer(result);");
            }
        }
        else
        {
            writer.WriteLine($"{implVar}.{pascalMethodName}({argsString});");
            writer.WriteLine("return;");
        }
        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Emits a try-lookup block for one interface in a sibling-property setter receiver.
    /// On lookup hit, assigns the marshalled value and returns. On miss, falls through to
    /// the next sibling. The trailing block implicitly silently drops the write when no
    /// sibling proxy is registered for this handle.
    /// </summary>
    private static void EmitSetterLookupHit(CSharpWriter writer, string interfaceName, string slug,
        string pascalPropertyName, string assignmentExpr)
    {
        var implVar = $"impl_{slug}";
        writer.WriteLine($"if (Swift.Runtime.ProxyLifetimeTracker.ResolveImpl<{interfaceName}>(handle) is {{}} {implVar})");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine($"{implVar}.{pascalPropertyName} = {assignmentExpr};");
        writer.WriteLine("return;");
        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Returns a fully-qualified C# interface name suitable for use in a same-file
    /// <c>SwiftObjectRegistry.TryGetProxy&lt;IProtocolProxyImpl&lt;...&gt;&gt;</c> call.
    /// Cross-module siblings need the <c>global::&lt;ns&gt;.{Interface}</c> prefix because
    /// the proxy class file may not <c>using</c> the sibling's namespace; <c>&lt;ns&gt;</c>
    /// is the GENERATED C# namespace (resolved through <see cref="NamespacePatternResolver"/>),
    /// not the raw Swift module name — under <c>--namespace-pattern</c> they can differ
    /// (e.g. Swift <c>StoreKit</c> → C# <c>StoreKit2</c>).
    /// </summary>
    private string GetQualifiedInterfaceName(ProtocolDecl protocolDecl)
    {
        var moduleName = protocolDecl.ModuleDecl?.Name ?? "";
        var baseName = NameProvider.GetInterfaceName(protocolDecl.Name, moduleName: moduleName);
        if (protocolDecl.ParentDecl is TypeDecl parentType)
        {
            var parentNames = new List<string>();
            BaseDecl? current = parentType;
            while (current is TypeDecl td)
            {
                parentNames.Insert(0, td.Name);
                current = td.ParentDecl;
            }
            baseName = string.Join(".", parentNames) + "." + baseName;
        }
        if (string.IsNullOrEmpty(moduleName))
            return baseName;
        var csNamespace = _emissionContext.NamespaceResolver?.ResolveNamespace(moduleName) ?? moduleName;
        return $"global::{csNamespace}.{baseName}";
    }

    /// <summary>
    /// Closure-property setter body (assignment + wrapper construction), parameterised
    /// over the impl variable so the no-fallback and per-sibling-lookup paths can share
    /// the same body shape.
    /// </summary>
    private static void EmitClosureSetterBody(CSharpWriter writer, bool isOptional, string pascalPropertyName,
        string delegateType, string invokerClassName, string implVar)
    {
        writer.WriteLine("if (rawFn == IntPtr.Zero)");
        writer.WriteLine("{");
        writer.Indent++;
        if (isOptional)
            writer.WriteLine($"{implVar}.{pascalPropertyName} = null;");
        else
            writer.WriteLine("// Non-Optional closure property: nil fnPtr is a contract violation; leave existing impl value unchanged.");
        writer.WriteLine("return;");
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine($"var _wrapper = SwiftEscapingClosure<{delegateType}>.FromSwift(rawFn, rawCtx);");
        writer.WriteLine($"var _inv = new {invokerClassName}((nint)_wrapper.FunctionPointer, (nint)_wrapper.Context, _wrapper);");
        writer.WriteLine($"{implVar}.{pascalPropertyName} = _inv.Invoke;");
        writer.WriteLine("return;");
    }

    /// <summary>
    /// Closure-property setter sibling-lookup branch: tries
    /// <c>IProtocolProxyImpl&lt;sibling&gt;</c>; on hit it executes the same setter
    /// body and returns. On miss it falls through to the next sibling. The trailing
    /// block silently drops the write when no sibling proxy matches the handle.
    /// </summary>
    private void EmitClosureSetterLookupHit(CSharpWriter writer, string interfaceName, string slug,
        bool isOptional, string pascalPropertyName, string delegateType, string invokerClassName)
    {
        var implVar = $"impl_{slug}";
        writer.WriteLine($"if (Swift.Runtime.ProxyLifetimeTracker.ResolveImpl<{interfaceName}>(handle) is {{}} {implVar})");
        writer.WriteLine("{");
        writer.Indent++;
        EmitClosureSetterBody(writer, isOptional, pascalPropertyName, delegateType, invokerClassName, implVar);
        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Closure-property getter body (reads delegate from impl, allocates GCHandle, writes
    /// (fnPtr, ctxPtr) into the 16-byte buffer). Caller is responsible for the
    /// surrounding allocation and the final <c>return buf;</c>.
    /// </summary>
    private static void EmitClosureGetterBody(CSharpWriter writer, string pascalPropertyName,
        string nullableDelegateType, string getterThunkName, string implVar)
    {
        writer.WriteLine($"{nullableDelegateType} _del = {implVar}.{pascalPropertyName};");
        writer.WriteLine("if (_del is not null)");
        writer.WriteLine("{");
        writer.Indent++;
        // GCHandle is freed by the Swift-side _SBClosureCtx box's deinit via the
        // SwiftClosureContext destroy trampoline.
        writer.WriteLine("var _gch = global::System.Runtime.InteropServices.GCHandle.Alloc(_del);");
        writer.WriteLine($"*(IntPtr*)buf = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&{getterThunkName};");
        writer.WriteLine("*(IntPtr*)(buf + IntPtr.Size) = global::System.Runtime.InteropServices.GCHandle.ToIntPtr(_gch);");
        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Closure-property getter sibling-lookup branch: tries
    /// <c>IProtocolProxyImpl&lt;sibling&gt;</c>; on hit it executes the getter body
    /// and returns the populated buffer. On miss it falls through to the next sibling
    /// (which keeps the zero-initialised buffer).
    /// </summary>
    private void EmitClosureGetterLookupHit(CSharpWriter writer, string interfaceName, string slug,
        string pascalPropertyName, string nullableDelegateType, string getterThunkName)
    {
        var implVar = $"impl_{slug}";
        writer.WriteLine($"if (Swift.Runtime.ProxyLifetimeTracker.ResolveImpl<{interfaceName}>(handle) is {{}} {implVar})");
        writer.WriteLine("{");
        writer.Indent++;
        EmitClosureGetterBody(writer, pascalPropertyName, nullableDelegateType, getterThunkName, implVar);
        writer.WriteLine("return buf;");
        writer.Indent--;
        writer.WriteLine("}");
    }
}
