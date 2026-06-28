// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

public partial class ProtocolProxyEmitter
{
    /// <summary>
    /// Returns the list of cross-module ancestor <see cref="ProtocolDecl"/>s a child
    /// protocol inherits across the module boundary — direct parents AND transitive
    /// grandparents — deduped by module-qualified name and resolved against
    /// <see cref="ModuleDecl.DependencyProtocols"/>.
    /// Walks the inheritance chain so a child inheriting <c>B.Parent</c> which itself
    /// inherits <c>C.Grandparent</c> populates BOTH ancestors' vtable storage in the
    /// local wrapper from the child proxy's static cctor.
    /// Returns an empty list when the child has no cross-module ancestors or when
    /// the bound module's parser was invoked without <c>--framework-dependency</c>
    /// for those modules (the ancestors simply aren't visible — the proxy emits
    /// without the cross-module-ancestor scaffolding, matching pre-fix behavior).
    /// </summary>
    private List<ProtocolDecl> CollectCrossModuleParents(ProtocolDecl protocolDecl)
    {
        // A read-only (forward-only) proxy has no reverse EveryProtocol conformance and never
        // reverse-dispatches an inherited requirement: the forward read of `any P` dispatches
        // through the existential's OWN witness table. The cross-module-parent reverse machinery
        // (per-parent vtable structs + receivers + the Set{Parent}_vtable P/Invoke, and its
        // execution from InitializeVtable) is therefore dead for it. Worse, a parent's
        // Set{Parent}_vtable Swift trampoline is emitted only for parents collected off
        // `suitableProtocols` — never for read-only protocols — so leaving this on would make the
        // read-only proxy's static cctor call a never-emitted Set{Parent}_vtable and throw
        // EntryPointNotFoundException at type load (first forward wrap). Returning empty here
        // suppresses BOTH callers (scaffolding emission and InitializeVtable population) in
        // lockstep — this is the single collection point. Unit-test path is unaffected:
        // _isReadOnlyProxy is false without an emission context.
        if (_isReadOnlyProxy)
            return new List<ProtocolDecl>();

        var moduleDecl = protocolDecl.ModuleDecl;
        if (moduleDecl == null || moduleDecl.DependencyProtocols.Count == 0)
            return new List<ProtocolDecl>();

        var currentModule = moduleDecl.Name;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var collected = new List<ProtocolDecl>();
        var pending = new Queue<ProtocolDecl>();

        EnqueueCrossModuleAncestors(protocolDecl.InheritedProtocols, moduleDecl, currentModule, seen, pending);
        while (pending.Count > 0)
        {
            var ancestor = pending.Dequeue();
            collected.Add(ancestor);
            EnqueueCrossModuleAncestors(ancestor.InheritedProtocols, moduleDecl, currentModule, seen, pending);
        }
        return collected;
    }

    private static void EnqueueCrossModuleAncestors(
        IEnumerable<NamedTypeSpec> inheritedProtocols,
        ModuleDecl moduleDecl,
        string currentModule,
        HashSet<string> seen,
        Queue<ProtocolDecl> pending)
    {
        foreach (var inherited in inheritedProtocols)
        {
            var inhModule = inherited.Module;
            if (string.IsNullOrEmpty(inhModule) || inhModule == currentModule)
                continue;
            if (inherited.NameWithoutModule is "Sendable" or "Escapable" or "Copyable" or "SendableMetatype" or "AnyObject")
                continue;
            if (!moduleDecl.DependencyProtocols.TryGetValue(inhModule, out var depProtos))
                continue;
            var ancestorDecl = depProtos.FirstOrDefault(dp => dp.Name == inherited.NameWithoutModule);
            if (ancestorDecl == null)
                continue;
            var key = $"{inhModule}.{ancestorDecl.Name}";
            if (seen.Add(key))
                pending.Enqueue(ancestorDecl);
        }
    }

    /// <summary>
    /// Emits vtable structs + receivers for each cross-module parent inside the child proxy
    /// class body. The Swift wrapper's local <c>extension EveryProtocol: DEP.Parent</c>
    /// dispatches the inherited requirement into module B's per-parent <c>_p_vtable</c>;
    /// these receivers fill that vtable's slots. Receiver lookup uses
    /// <c>IProtocolProxyImpl&lt;DEP.IParent&gt;</c> so any registered child proxy
    /// resolves covariantly to the parent's interface (Layer 2 of the fix).
    ///
    /// When multiple local children inherit the same cross-module parent, every child's
    /// cctor writes to the same module-global parent vtable; last-write-wins is correct
    /// because every child's receivers route through the same covariant registry lookup.
    /// </summary>
    private void EmitCrossModuleParentScaffolding(CSharpWriter writer, ProtocolDecl protocolDecl, List<ProtocolDecl> crossModuleParents)
    {
        if (crossModuleParents.Count == 0)
            return;

        // Snapshot per-protocol mutable state so we can restore it after each parent emission
        // — receivers and helpers rely on the SAME _skippedXxx fields that ProtocolProxyEmitter
        // populated for the CHILD's emission, but the PARENT's emission has its own skip set.
        var savedSkippedMethodKeys = _skippedMethodKeys;
        var savedSkippedPropertyNames = _skippedPropertyNames;
        var savedSkippedSubscriptIndices = _skippedSubscriptIndices;
        var savedClosureSkippedMethodKeys = _closureSkippedMethodKeys;
        var savedClosureSkippedPropertyNames = _closureSkippedPropertyNames;

        foreach (var parentDecl in crossModuleParents)
        {
            // Reset per-parent skip state — parent has its own member set; child's skips
            // don't apply. Vtable struct/population sites apply ProtocolVtableMembers directly
            // (the same predicates EveryProtocolEmitter uses); receivers walk the parent's
            // full surface and gracefully no-op for members without a Swift dispatch slot.
            _skippedMethodKeys = new HashSet<string>();
            _skippedPropertyNames = new HashSet<string>();
            _skippedSubscriptIndices = new HashSet<int>();
            _closureSkippedMethodKeys = new HashSet<string>();
            _closureSkippedPropertyNames = new HashSet<string>();

            var parentInterfaceName = NameProvider.GetInterfaceName(
                parentDecl.Name,
                moduleName: parentDecl.ModuleDecl?.Name ?? string.Empty,
                currentModuleName: _moduleName);

            writer.WriteLine($"#region Cross-Module Parent Scaffolding ({parentDecl.ModuleDecl?.Name}.{parentDecl.Name})");
            writer.WriteLine();

            EmitSwiftVtableStruct(writer, parentDecl);
            EmitLocalVtableStruct(writer, parentDecl);
            EmitCrossModuleParentVtableFields(writer, parentDecl);
            EmitCrossModuleParentSetVtablePInvoke(writer, parentDecl);
            EmitReceiverMethods(writer, parentDecl, parentInterfaceName, applyVtableMembershipFilter: true);

            writer.WriteLine("#endregion");
            writer.WriteLine();
        }

        _skippedMethodKeys = savedSkippedMethodKeys;
        _skippedPropertyNames = savedSkippedPropertyNames;
        _skippedSubscriptIndices = savedSkippedSubscriptIndices;
        _closureSkippedMethodKeys = savedClosureSkippedMethodKeys;
        _closureSkippedPropertyNames = savedClosureSkippedPropertyNames;
    }

    /// <summary>
    /// Emits the per-parent static fields that hold the cross-module-parent vtable +
    /// its pinned GCHandle. These are sibling to the child's own <c>_swiftVTable</c>
    /// and <c>_localVTable</c> so the cctor can populate both vtables without
    /// reusing the child's slots.
    /// </summary>
    private void EmitCrossModuleParentVtableFields(CSharpWriter writer, ProtocolDecl parentDecl)
    {
        var swiftVtableStruct = GetSwiftVtableStructName(parentDecl);
        var localVtableStruct = GetLocalVtableStructName(parentDecl);

        writer.WriteLine($"private static {swiftVtableStruct} {GetCrossModuleParentSwiftVtableFieldName(parentDecl)};");
        writer.WriteLine($"private static {localVtableStruct} {GetCrossModuleParentLocalVtableFieldName(parentDecl)};");
        writer.WriteLine($"private static GCHandle {GetCrossModuleParentLocalVtableHandleFieldName(parentDecl)};");
        writer.WriteLine();
    }

    /// <summary>
    /// Emits a <c>[DllImport]</c> P/Invoke for the LOCAL <c>Set{Module}_{Parent}_vtable</c>
    /// symbol — emitted as a <c>@_cdecl</c> in the bound module's wrapper by
    /// <see cref="EveryProtocolEmitter.EmitSetVtableFunction"/> when the cross-module
    /// parent's companion conformance is emitted (see <c>ModuleHandler.CollectCrossModuleParentDecls</c>).
    /// The module prefix disambiguates two dependency modules that export protocols with
    /// the same simple name from colliding on the wrapper's exported symbol table.
    /// </summary>
    private void EmitCrossModuleParentSetVtablePInvoke(CSharpWriter writer, ProtocolDecl parentDecl)
    {
        var wrapperLibPath = _typeDatabase.AsyncLibraryName ?? _typeDatabase.GetLibraryPath(_moduleName);
        var setVtableName = GetSetVtablePInvokeName(parentDecl);
        var entryPoint = GetCrossModuleSetVtableEntryPoint(parentDecl);
        // Disambiguate from the child's own NativeMethods.Set{Child}_vtable.
        var nativeMethodsClassName = GetCrossModuleParentNativeMethodsClassName(parentDecl);

        writer.WriteLine($"private static partial class {nativeMethodsClassName}");
        writer.WriteLine("{");
        writer.Indent++;
        PInvokeEmitHelper.EmitDeclaration(writer, new PInvokeEmissionInfo
        {
            LibraryPath = wrapperLibPath,
            EntryPoint = entryPoint,
            MethodName = setVtableName,
            ReturnType = "void",
            ParametersString = "IntPtr vtable",
            CallingConvention = PInvokeCallingConvention.Cdecl,
            Visibility = PInvokeVisibility.Public
        });
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// Mirrors <see cref="EveryProtocolEmitter.GetSetVtableMangledName"/> for the cross-module case:
    /// the wrapper's <c>@_silgen_name</c> qualifies by source module to avoid simple-name collisions
    /// between two dependency modules that export protocols with the same simple name.
    /// </summary>
    private static string GetCrossModuleSetVtableEntryPoint(ProtocolDecl parentDecl)
    {
        var sourceModule = parentDecl.ModuleDecl?.Name ?? string.Empty;
        return string.IsNullOrEmpty(sourceModule)
            ? $"Set{parentDecl.Name}_vtable"
            : $"Set{sourceModule}_{parentDecl.Name}_vtable";
    }

    /// <summary>
    /// Returns the per-parent suffix used by the child proxy's static fields and
    /// nested native-methods class. Qualified by source module so a child inheriting
    /// from two same-simple-name parents in different dependency modules emits
    /// distinct field/class names.
    /// </summary>
    private static string GetCrossModuleParentScaffoldingSuffix(ProtocolDecl parentDecl)
    {
        var sourceModule = parentDecl.ModuleDecl?.Name;
        return string.IsNullOrEmpty(sourceModule)
            ? parentDecl.Name
            : $"{sourceModule}_{parentDecl.Name}";
    }

    private static string GetCrossModuleParentSwiftVtableFieldName(ProtocolDecl parentDecl)
        => $"_swiftVTable_xm_{GetCrossModuleParentScaffoldingSuffix(parentDecl)}";

    private static string GetCrossModuleParentLocalVtableFieldName(ProtocolDecl parentDecl)
        => $"_localVTable_xm_{GetCrossModuleParentScaffoldingSuffix(parentDecl)}";

    private static string GetCrossModuleParentLocalVtableHandleFieldName(ProtocolDecl parentDecl)
        => $"_localVTableHandle_xm_{GetCrossModuleParentScaffoldingSuffix(parentDecl)}";

    private static string GetCrossModuleParentNativeMethodsClassName(ProtocolDecl parentDecl)
        => $"NativeMethods_xm_{GetCrossModuleParentScaffoldingSuffix(parentDecl)}";
}
