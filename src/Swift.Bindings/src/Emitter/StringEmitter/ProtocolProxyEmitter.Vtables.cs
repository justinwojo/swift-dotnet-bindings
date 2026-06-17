// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

public partial class ProtocolProxyEmitter
{
    /// <summary>
    /// Emits the struct that matches the Swift vtable layout.
    /// This is passed to Swift's SetVtable function.
    ///
    /// LAYOUT is now driven uniformly by <see cref="ProtocolVtableMembers"/> on BOTH the same-module
    /// and cross-module paths (property → IncludesProperty, subscript → IncludesSubscript, method →
    /// raw-keyed IncludesMethod), so the C# struct mirrors the Swift wrapper's vtable struct exactly
    /// without a per-call-site flag. The same-module skip sets (<c>_skippedMethodKeys</c> et al.)
    /// remain FILLABILITY-only and are consulted by the receiver/assignment walks, never here.
    /// </summary>
    private void EmitSwiftVtableStruct(CSharpWriter writer, ProtocolDecl protocolDecl)
    {
        var structName = GetSwiftVtableStructName(protocolDecl);
        // Always needed now: the property/subscript LAYOUT decision routes through
        // ProtocolVtableMembers on BOTH the same-module and cross-module paths (see the loops
        // below), and IncludesProperty needs a ClosureHandler to classify closure properties.
        var closureHandler = new ClosureHandler(_typeDatabase);

        writer.WriteLine($"/// <summary>Matches Swift {protocolDecl.Name}_vtable layout</summary>");
        writer.WriteLine("[StructLayout(LayoutKind.Sequential)]");
        writer.WriteLine($"private struct {structName}");
        writer.WriteLine("{");
        writer.Indent++;

        writer.WriteLine("public IntPtr csVTHandle;");

        // Track emitted fields to avoid duplicates
        var emittedFields = new HashSet<string>();

        // Property fields. LAYOUT membership — "does the Swift _vtable carry a slot for this
        // property?" — is ProtocolVtableMembers.IncludesProperty, the single source of truth that
        // mirrors EveryProtocolEmitter.EmitProtocolVtableStruct. This is a DIFFERENT axis from
        // _skippedPropertyNames ("can C# project/fill this member?"): Swift KEEPS a slot for an
        // AnyType-unprojectable property (the assignment walk just leaves it null) and OMITS an
        // @objc optional / non-requirement / Self-typed / non-dispatchable-closure property.
        // Gating the field on _skippedPropertyNames over-skipped the AnyType slots Swift keeps,
        // making the C# [StructLayout] SMALLER than Swift's so every following field read from the
        // wrong offset (the inverse of the Finding-8 corruption). One predicate for both the
        // same-module and cross-module paths now; the assignment walk (EmitChildVtablePopulation)
        // leaves slots C# can't fill at null.
        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic)
                continue;
            if (!ProtocolVtableMembers.IncludesProperty(property, protocolDecl, closureHandler))
                continue;
            EmitPropertyVtableSwiftFields(writer, property, emittedFields);
        }

        // Subscript fields. Same layout-vs-fillability split as properties: IncludesSubscript is
        // the Swift-mirror layout predicate (drops static / Self-typed / mixed-generic, KEEPS
        // AnyType). A non-static excluded subscript still consumes its index so the next
        // dispatchable subscript lands at the slot Swift assigned it; static subscripts consume no
        // index (matching EveryProtocolEmitter).
        int subscriptIndex = 0;
        foreach (var subscript in protocolDecl.Subscripts)
        {
            if (subscript.IsStatic)
                continue;
            if (!ProtocolVtableMembers.IncludesSubscript(subscript, protocolDecl))
            {
                subscriptIndex++;
                continue;
            }
            EmitSubscriptVtableSwiftFields(writer, subscript, subscriptIndex, emittedFields);
            subscriptIndex++;
        }

        // Method fields. LAYOUT only — this struct is shared memory with Swift's _vtable, so it
        // must mirror EveryProtocolEmitter.EmitProtocolVtableStruct EXACTLY: allocate the slot index
        // from the RAW producer key (EveryProtocolEmitter.GetMethodKey — name + labels + raw Swift
        // type specs), consuming an index for every distinct raw method, and emit a field iff the
        // producer's membership predicate (ProtocolVtableMembers.IncludesMethod, == MethodEmitsVtableField
        // after the ctor/static/@objc-optional pre-skip) keeps the slot.
        //
        // Three things are DELIBERATELY absent vs. the receiver/assignment walks (which key on the
        // same raw index but layer fillability on top):
        //   • The projected-C# collapse (GetProjectedCSharpMethodKey / GetMethodSignatureKey). Two
        //     raw-distinct existential overloads that project to one C# method (e.g. consume(any A)
        //     / consume(any B) → Consume(object)) each get their OWN Swift slot, so the struct must
        //     emit BOTH func_consume_0 AND func_consume_1. Collapsing here was the WitnessIndexProto
        //     corruption: tag landed at index 1 instead of Swift's 2 and every later read shifted.
        //   • _skippedMethodKeys (AnyType-unprojectable members). That is FILLABILITY, not layout:
        //     Swift KEEPS the slot, the assignment walk just leaves it null. Gating the field on it
        //     would shrink the struct below Swift's.
        //   • _closureSkippedMethodKeys — subsumed by IncludesMethod, whose closure branch returns
        //     false for the same off-surface closure methods (and keyed on raw, the collapsing-keyed
        //     set wouldn't match anyway).
        int methodIndex = 0;
        var methodIndices = new Dictionary<string, int>();
        foreach (var method in protocolDecl.Methods)
        {
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;
            // @objc optional methods get no vtable slot — the Swift producer
            // (EveryProtocolEmitter.EmitProtocolVtableStruct) skips them BEFORE the index
            // increment, so this struct must omit the field AND not consume the slot, or the
            // C# [StructLayout] grows a field Swift never wrote and every later slot shifts.
            if (method.IsObjCOptional)
                continue;

            var slotKey = EveryProtocolEmitter.GetMethodKey(method);
            if (!methodIndices.TryGetValue(slotKey, out var idx))
            {
                idx = methodIndex++;
                methodIndices[slotKey] = idx;
                // Skip-but-consume for the categories Swift omits (non-dispatchable closure,
                // method-level generics, Self-typed, mixed-generic protocol): the producer
                // consumes the index then drops the field, so the next dispatchable method
                // lands at the slot Swift assigned it.
                if (!ProtocolVtableMembers.IncludesMethod(method, protocolDecl, closureHandler))
                    continue;
                EmitMethodVtableSwiftField(writer, method, idx, emittedFields);
            }
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits the local vtable struct that holds managed delegates. Layout mirrors the Swift-facing
    /// struct field-for-field (its values are positionally copied into _swiftVTable); see
    /// <see cref="EmitSwiftVtableStruct"/> notes for the uniform <see cref="ProtocolVtableMembers"/>
    /// layout gating.
    /// </summary>
    private void EmitLocalVtableStruct(CSharpWriter writer, ProtocolDecl protocolDecl)
    {
        var structName = GetLocalVtableStructName(protocolDecl);
        // Always needed now (see EmitSwiftVtableStruct): IncludesProperty gates the property/
        // subscript layout on every path and needs a ClosureHandler for closure-property triage.
        var closureHandler = new ClosureHandler(_typeDatabase);

        writer.WriteLine($"/// <summary>Local vtable holding managed delegates</summary>");
        writer.WriteLine($"private struct {structName}");
        writer.WriteLine("{");
        writer.Indent++;

        // Property delegates - track emitted to avoid duplicates (skip static properties)
        var emittedFields = new HashSet<string>();
        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic)
                continue;
            // The local (managed-delegate) struct must stay field-for-field aligned with the
            // Swift-facing struct, so it uses the identical LAYOUT predicate (IncludesProperty) —
            // NOT _skippedPropertyNames. See the note in EmitSwiftVtableStruct for the
            // layout-vs-fillability axis split and the positional-copy corruption this prevents.
            if (!ProtocolVtableMembers.IncludesProperty(property, protocolDecl, closureHandler))
                continue;
            EmitPropertyLocalVtableFields(writer, property, protocolDecl, emittedFields);
        }

        // Subscript delegates (skip static subscripts)
        int subscriptIndex = 0;
        foreach (var subscript in protocolDecl.Subscripts)
        {
            if (subscript.IsStatic)
                continue;
            if (!ProtocolVtableMembers.IncludesSubscript(subscript, protocolDecl))
            {
                subscriptIndex++;
                continue;
            }
            EmitSubscriptLocalVtableFields(writer, subscript, protocolDecl, subscriptIndex, emittedFields);
            subscriptIndex++;
        }

        // Method delegates. LAYOUT only — this struct must stay field-for-field aligned with the
        // Swift-facing struct (its fields are positionally copied into _swiftVTable), so it uses the
        // identical raw-key index allocation + IncludesMethod layout predicate. See the method-loop
        // note in EmitSwiftVtableStruct for why the projected-C# collapse and the fillability skip
        // sets are deliberately absent here.
        int methodIndex = 0;
        var methodIndices = new Dictionary<string, int>();
        foreach (var method in protocolDecl.Methods)
        {
            if (method.IsConstructor || method.MethodType == MethodType.Static)
                continue;
            // @objc optional methods get no vtable slot — the Swift producer
            // (EveryProtocolEmitter.EmitProtocolVtableStruct) skips them BEFORE the index
            // increment, so this struct must omit the field AND not consume the slot, or the
            // C# [StructLayout] grows a field Swift never wrote and every later slot shifts.
            if (method.IsObjCOptional)
                continue;

            var slotKey = EveryProtocolEmitter.GetMethodKey(method);
            if (!methodIndices.TryGetValue(slotKey, out var idx))
            {
                idx = methodIndex++;
                methodIndices[slotKey] = idx;
                if (!ProtocolVtableMembers.IncludesMethod(method, protocolDecl, closureHandler))
                    continue;
                EmitMethodLocalVtableField(writer, method, protocolDecl, idx, emittedFields);
            }
        }

        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    private void EmitPropertyVtableSwiftFields(CSharpWriter writer, PropertyDecl property, HashSet<string> emittedFields)
    {
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();

        if (hasGetter)
        {
            var fieldName = $"func_{property.Name}_get";
            if (emittedFields.Add(fieldName))
            {
                writer.WriteLine($"public IntPtr {fieldName};");
            }
        }
        if (hasSetter)
        {
            var fieldName = $"func_{property.Name}_set";
            if (emittedFields.Add(fieldName))
            {
                writer.WriteLine($"public IntPtr {fieldName};");
            }
        }
    }

    private void EmitPropertyLocalVtableFields(CSharpWriter writer, PropertyDecl property, ProtocolDecl protocolDecl, HashSet<string> emittedFields)
    {
        var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
        var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();
        // Dispatchable closure properties: setter slot expands into
        // (fnPtr, ctxPtr) IntPtr pair to match the Swift @convention(c) trampoline shape
        // emitted by EveryProtocolEmitter.EmitDispatchableClosurePropertyVtableFields.
        // Getter slot stays as IntPtr → IntPtr (16-byte buffer holding (fnPtr, ctxPtr)).
        var closureHandler = new ClosureHandler(_typeDatabase);
        var isDispatchableClosure = EveryProtocolEmitter.IsDispatchableClosureProperty(property, closureHandler);

        if (hasGetter)
        {
            var fieldName = $"Func_{property.Name}_get";
            if (emittedFields.Add(fieldName))
            {
                writer.WriteLine($"public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr> {fieldName};");
            }
        }
        if (hasSetter)
        {
            var fieldName = $"Func_{property.Name}_set";
            if (emittedFields.Add(fieldName))
            {
                if (isDispatchableClosure)
                    writer.WriteLine($"public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void> {fieldName};");
                else
                    writer.WriteLine($"public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void> {fieldName};");
            }
        }
    }

    private void EmitSubscriptVtableSwiftFields(CSharpWriter writer, SubscriptDecl subscript, int index, HashSet<string> emittedFields)
    {
        if (subscript.HasGetter)
        {
            var fieldName = $"func_subscript_{index}_get";
            if (emittedFields.Add(fieldName))
            {
                writer.WriteLine($"public IntPtr {fieldName};");
            }
        }
        if (subscript.HasSetter)
        {
            var fieldName = $"func_subscript_{index}_set";
            if (emittedFields.Add(fieldName))
            {
                writer.WriteLine($"public IntPtr {fieldName};");
            }
        }
    }

    private void EmitSubscriptLocalVtableFields(CSharpWriter writer, SubscriptDecl subscript, ProtocolDecl protocolDecl, int index, HashSet<string> emittedFields)
    {
        var paramCount = subscript.IndexParameters.Count;
        // Getter: IntPtr (vtable), IntPtr (self), IntPtr[] (indices) -> IntPtr (result)
        // Setter: IntPtr (vtable), IntPtr (self), IntPtr (value), IntPtr[] (indices) -> void

        if (subscript.HasGetter)
        {
            var fieldName = $"Func_subscript_{index}_get";
            if (emittedFields.Add(fieldName))
            {
                var paramTypes = "IntPtr, IntPtr" + string.Concat(Enumerable.Repeat(", IntPtr", paramCount));
                writer.WriteLine($"public delegate* unmanaged[Cdecl]<{paramTypes}, IntPtr> {fieldName};");
            }
        }
        if (subscript.HasSetter)
        {
            var fieldName = $"Func_subscript_{index}_set";
            if (emittedFields.Add(fieldName))
            {
                var paramTypes = "IntPtr, IntPtr, IntPtr" + string.Concat(Enumerable.Repeat(", IntPtr", paramCount));
                writer.WriteLine($"public delegate* unmanaged[Cdecl]<{paramTypes}, void> {fieldName};");
            }
        }
    }

    private void EmitMethodVtableSwiftField(CSharpWriter writer, MethodDecl method, int index, HashSet<string> emittedFields)
    {
        var fieldName = $"func_{method.Name}_{index}";
        if (emittedFields.Add(fieldName))
        {
            writer.WriteLine($"public IntPtr {fieldName};");
        }
    }

    private void EmitMethodLocalVtableField(CSharpWriter writer, MethodDecl method, ProtocolDecl protocolDecl, int index, HashSet<string> emittedFields)
    {
        var fieldName = $"Func_{method.Name}_{index}";
        if (!emittedFields.Add(fieldName))
            return;

        // Exclude return type, debug params, and empty tuple () params — must match receiver signature.
        // Dispatchable closure params expand into TWO IntPtr slots (fnPtr + ctx) on both Swift
        // and C# vtables — see EveryProtocolEmitter.CountVtableSlots.
        var closureHandler = new ClosureHandler(_typeDatabase);
        int slotCount = 0;
        foreach (var p in method.CSSignature.Skip(1))
        {
            if (DefaultParameterOverloadEmitter.IsDebugParameter(p) || p.SwiftTypeSpec.IsEmptyTuple)
                continue;
            slotCount += EveryProtocolEmitter.CountVtableSlots(p.SwiftTypeSpec, closureHandler);
        }
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;

        // Parameters: IntPtr (vtable), IntPtr (self), IntPtr[] (method params)
        var paramTypes = "IntPtr, IntPtr" + string.Concat(Enumerable.Repeat(", IntPtr", slotCount));
        var returnTypeStr = hasReturn ? "IntPtr" : "void";

        writer.WriteLine($"public delegate* unmanaged[Cdecl]<{paramTypes}, {returnTypeStr}> {fieldName};");
    }
}
