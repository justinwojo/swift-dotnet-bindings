// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

public partial class ProtocolProxyEmitter
{
    /// <summary>
    /// Emits the struct that matches the Swift vtable layout.
    /// This is passed to Swift's SetVtable function.
    ///
    /// LAYOUT is rendered from the single <see cref="VtableLayout"/> model (<see cref="VtableLayoutBuilder"/>):
    /// this struct walks <see cref="VtableLayout.IncludedSlots"/> in declaration order, so the C# struct
    /// mirrors the Swift wrapper's vtable struct field-for-field without a per-call-site flag. Membership
    /// still flows through <see cref="ProtocolVtableMembers"/>, which now delegates to the model's
    /// <c>Classify*</c> oracle. The same-module skip sets (<c>_skippedMethodKeys</c> et al.) remain
    /// FILLABILITY-only and are consulted by the receiver/assignment walks, never here.
    /// </summary>
    private void EmitSwiftVtableStruct(CSharpWriter writer, ProtocolDecl protocolDecl)
    {
        var structName = GetSwiftVtableStructName(protocolDecl);

        writer.WriteLine($"/// <summary>Matches Swift {protocolDecl.Name}_vtable layout</summary>");
        writer.WriteLine("[StructLayout(LayoutKind.Sequential)]");
        writer.WriteLine($"private struct {structName}");
        writer.WriteLine("{");
        writer.Indent++;

        writer.WriteLine("public IntPtr csVTHandle;");

        // Track emitted fields to avoid duplicates
        var emittedFields = new HashSet<string>();

        // This struct is shared memory with Swift's _vtable, so it must mirror
        // EveryProtocolEmitter.EmitProtocolVtableStruct field-for-field. Both render the SAME ordered
        // VtableLayout: membership, raw-key index allocation (skip-but-consume), and the static-
        // subscript-consumes-no-index rule all come from VtableLayoutBuilder, so the C# [StructLayout]
        // and the Swift struct cannot disagree on slot count or position.
        //
        // The model is LAYOUT, not fillability: it KEEPS the slot for an AnyType-unprojectable member
        // (the assignment walk leaves it null) and gives each raw-distinct existential overload its
        // OWN slot (no projected-C# collapse) — gating on _skippedMethodKeys / the projected key here
        // would shrink the struct below Swift's and shift every later field (the Finding-8 / WitnessIndexProto
        // corruption). Those fillability filters stay on the receiver/assignment walks only.
        var layout = new VtableLayoutBuilder(_typeDatabase).Build(protocolDecl);
        foreach (var slot in layout.IncludedSlots)
        {
            switch (slot.Kind)
            {
                case VtableMemberKind.Property:
                    EmitPropertyVtableSwiftFields(writer, slot.AsProperty!, emittedFields);
                    break;
                case VtableMemberKind.Subscript:
                    EmitSubscriptVtableSwiftFields(writer, slot.AsSubscript!, slot.SlotIndex, emittedFields);
                    break;
                case VtableMemberKind.Method:
                    EmitMethodVtableSwiftField(writer, slot.AsMethod!, slot.SlotIndex, emittedFields);
                    break;
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

        writer.WriteLine($"/// <summary>Local vtable holding managed delegates</summary>");
        writer.WriteLine($"private struct {structName}");
        writer.WriteLine("{");
        writer.Indent++;

        // The local (managed-delegate) struct is positionally copied into _swiftVTable, so it must
        // stay field-for-field aligned with the Swift-facing struct. It renders the SAME VtableLayout
        // model (identical membership + raw-key skip-but-consume index) — see EmitSwiftVtableStruct
        // for why the projected-C# collapse and the fillability skip sets are deliberately absent.
        var emittedFields = new HashSet<string>();
        var layout = new VtableLayoutBuilder(_typeDatabase).Build(protocolDecl);
        foreach (var slot in layout.IncludedSlots)
        {
            switch (slot.Kind)
            {
                case VtableMemberKind.Property:
                    EmitPropertyLocalVtableFields(writer, slot.AsProperty!, protocolDecl, emittedFields);
                    break;
                case VtableMemberKind.Subscript:
                    EmitSubscriptLocalVtableFields(writer, slot.AsSubscript!, protocolDecl, slot.SlotIndex, emittedFields);
                    break;
                case VtableMemberKind.Method:
                    EmitMethodLocalVtableField(writer, slot.AsMethod!, protocolDecl, slot.SlotIndex, slot.Width, emittedFields);
                    break;
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

    private void EmitMethodLocalVtableField(CSharpWriter writer, MethodDecl method, ProtocolDecl protocolDecl, int index, int slotCount, HashSet<string> emittedFields)
    {
        var fieldName = $"Func_{method.Name}_{index}";
        if (!emittedFields.Add(fieldName))
            return;

        // slotCount is the slot's VtableLayout width: dispatchable closure / Optional<Closure> params
        // expand into TWO IntPtr slots (fnPtr + ctx), every other param into one (return type, debug
        // params, and empty-tuple () params contribute none) — see VtableLayoutBuilder.GetWidth and
        // EveryProtocolEmitter.CountVtableSlots. Taking it from the model ties this delegate's arity to
        // the same width the Swift struct field renders, so they cannot disagree on parameter count.
        var returnType = method.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        var hasReturn = returnType != null && !returnType.IsEmptyTuple;

        // Parameters: IntPtr (vtable), IntPtr (self), IntPtr[] (method params)
        var paramTypes = "IntPtr, IntPtr" + string.Concat(Enumerable.Repeat(", IntPtr", slotCount));
        var returnTypeStr = hasReturn ? "IntPtr" : "void";

        writer.WriteLine($"public delegate* unmanaged[Cdecl]<{paramTypes}, {returnTypeStr}> {fieldName};");
    }
}
