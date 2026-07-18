// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Emits concrete trampolines that surface a bound-generic base class's instance methods on a
/// concrete (non-generic) subclass that closes ALL of the base's type parameters.
///
/// A concrete Swift subclass of a bound-generic base — e.g.
/// <c>final class ConcreteLifecycle: LifecycleKernel&lt;ScanReadout, ScanBanner&gt;</c> — cannot be
/// modeled as C# inheritance today: the TypeName model can't represent a closed generic
/// instantiation, so the superclass is dropped during hierarchy resolution and the subclass emits
/// flat (<c>: ISwiftObject</c>), losing every inherited base method. This also covers the case where
/// the base has a protocol-with-associated-type / Self constraint that prevents its methods from
/// emitting at all on the open generic.
///
/// Rather than thread a closed instantiation through the inheritance pipeline, this emitter writes a
/// per-method concrete <c>@_cdecl</c> shim that <c>unsafeBitCast</c>s the opaque self to the concrete
/// leaf type and calls the inherited method directly. Swift resolves all of the closed generic's
/// metadata and protocol witness tables internally, so no metadata or PWT crosses the C boundary —
/// the same mechanism works identically for unconstrained and PAT-constrained bases. A matching
/// C# extension method on the leaf forwards the call. The leaf stays flat (no parser-guard removal,
/// no TypeName-model surgery, no open-generic-base rooting hazard); this is purely additive.
///
/// First cut covers the highest-value shape: instance, non-async, non-throwing, non-generic base
/// methods that take zero parameters and return either void or a blittable scalar. This is exactly
/// the control/lifecycle-method surface (pause/resume/restart/…) the gap was reported against.
/// </summary>
public static class SubclassClosedParentTrampolineEmitter
{
    /// <summary>
    /// Swift primitive return types that are directly <c>@_cdecl</c>-returnable and round-trip as a
    /// blittable C# scalar (identity marshalling). Bool is intentionally excluded from the first cut
    /// (it needs an Int8 projection on the cdecl boundary).
    /// </summary>
    private static readonly Dictionary<string, (string Swift, string CSharp)> BlittableScalarReturns =
        new(System.StringComparer.Ordinal)
        {
            ["Int8"] = ("Int8", "sbyte"),
            ["Int16"] = ("Int16", "short"),
            ["Int32"] = ("Int32", "int"),
            ["Int64"] = ("Int64", "long"),
            ["Int"] = ("Int", "nint"),
            ["UInt8"] = ("UInt8", "byte"),
            ["UInt16"] = ("UInt16", "ushort"),
            ["UInt32"] = ("UInt32", "uint"),
            ["UInt64"] = ("UInt64", "ulong"),
            ["UInt"] = ("UInt", "nuint"),
            ["Float"] = ("Float", "float"),
            ["Double"] = ("Double", "double"),
        };

    /// <summary>
    /// Entry point. If <paramref name="leafDecl"/> is a concrete class whose direct superclass is a
    /// same-module bound-generic base, emits a <c>{Leaf}BaseTrampolines</c> static extension class
    /// (namespace scope, after the leaf's body is closed) exposing the base's eligible instance
    /// methods. No-op for any other shape.
    /// </summary>
    public static void EmitSubclassClosedParentTrampolines(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        ClassDecl leafDecl,
        ModuleDecl moduleDecl,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        ILogger logger)
    {
        // The leaf must be a concrete (non-generic), non-internal class. Internal types never emit a
        // public surface; generic leaves are the open-generic case, handled elsewhere.
        if (leafDecl.IsGeneric || leafDecl.IsModuleInternal) return;

        // A nested leaf is emitted INLINE inside its enclosing type's body, but the trampoline
        // extension lives in a namespace-scope `static partial class`. Emitting that class while the
        // enclosing type body is still open yields CS1109 ("extension methods must be defined in a
        // top-level static class"). Decline nested leaves, mirroring the CSM nested-parent guard.
        if (leafDecl.ParentDecl is TypeDecl) return;

        var directSuper = leafDecl.DirectSuperclassName;
        if (directSuper == null || !directSuper.Contains('<')) return;

        // The @_cdecl trampolines only exist in xcframework (wrapper) mode; otherwise there is no
        // wrapper library to host them.
        if (!WrapperValidation.IsXCFrameworkMode(typeDatabase)) return;

        // Resolve the open generic base by simple name. The guards that drop a bound-generic
        // superclass also null out ResolvedSuperclass, so resolve from the module's own type list.
        var baseSimpleName = ExtractSimpleBaseName(directSuper);
        var baseDecl = moduleDecl.Types
            .OfType<ClassDecl>()
            .FirstOrDefault(t => t.Name == baseSimpleName && t.IsGeneric);
        if (baseDecl == null)
        {
            logger.LogDebug(
                "SubclassClosedParentTrampoline: {Leaf} closes a bound-generic base '{Base}' but no generic ClassDecl matched in module '{Module}'.",
                leafDecl.Name, baseSimpleName, moduleDecl.Name);
            return;
        }

        var moduleName = baseDecl.SwiftTypeName.Module;
        var wrapperLibPath = typeDatabase.AsyncLibraryName ?? "libSwiftBindings";
        var leafSwiftName = leafDecl.SwiftTypeName.ModuleQualifiedName;

        // Methods the leaf declares itself (e.g. overrides) already emit on the flat leaf class —
        // an extension method of the same name would be redundant and shadowed. Skip those.
        var leafOwnMethodNames = new HashSet<string>(
            leafDecl.Methods
                .Where(m => m.MethodType == MethodType.Instance && !m.IsAccessor && !m.IsConstructor)
                .Select(m => m.Name),
            System.StringComparer.Ordinal);

        var eligible = baseDecl.Methods
            .Where(m => IsEligibleBaseMethod(m, leafOwnMethodNames))
            .ToList();
        if (eligible.Count == 0) return;

        // Open the extension class (namespace scope — the leaf's body is already closed). Use a
        // distinct suffix from the conformer-keyed CSM (`{Type}{Conformers}CsmExtensions`) so the
        // two never collide.
        var extClassName = $"{leafDecl.Name}BaseTrampolines";

        csWriter.WriteLine();
        csWriter.WriteLine($"/// <summary>Concrete trampolines surfacing {baseSimpleName}&lt;…&gt; base methods on {leafDecl.Name}.</summary>");
        csWriter.WriteLine($"public static partial class {extClassName}");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        var emittedNames = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var method in eligible)
        {
            TryEmitTrampoline(
                csWriter, swiftWriter, method, leafDecl, baseDecl, leafSwiftName,
                moduleName, wrapperLibPath, typeDatabase, emissionContext, emittedNames, logger);
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();
    }

    /// <summary>
    /// First-cut eligibility: instance, public, non-async, non-throwing, non-generic, non-accessor,
    /// non-constructor base method taking zero parameters and returning void or a blittable scalar.
    /// Methods the leaf itself declares are excluded (they already emit on the flat leaf).
    /// </summary>
    private static bool IsEligibleBaseMethod(MethodDecl method, HashSet<string> leafOwnMethodNames)
    {
        if (method.MethodType != MethodType.Instance) return false;
        if (method.IsConstructor || method.IsAccessor) return false;
        if (method.IsAsync || method.Throws) return false;
        // An instance method of a generic class carries the CLASS's generic parameters
        // (τ_0_*) in its GenericParameters list, so MethodDecl.IsGeneric is true even for a
        // method that introduces none of its own. The trampoline closes those class params
        // via the concrete leaf, so only METHOD-OWN generics (depth ≥ 1, τ_1_*) are
        // disqualifying — they can't be specialized from a single concrete subclass.
        if (HasMethodOwnGenerics(method)) return false;
        if (method.IsModuleInternal || method.IsSpiProtected) return false;
        if (leafOwnMethodNames.Contains(method.Name)) return false;

        // Zero parameters (an empty-tuple placeholder, if present, is not a real parameter).
        var paramArgs = method.CSSignature.Skip(1).Where(a => !a.SwiftTypeSpec.IsEmptyTuple).ToList();
        if (paramArgs.Count != 0) return false;

        // Return: void or a recognized blittable scalar.
        var returnSpec = method.CSSignature.First().SwiftTypeSpec;
        if (returnSpec.IsEmptyTuple) return true;
        return TryResolveScalarReturn(returnSpec, out _, out _);
    }

    /// <summary>
    /// True if the method introduces its own generic parameters (canonical depth ≥ 1, e.g.
    /// <c>τ_1_0</c>), as opposed to merely inheriting the enclosing generic class's parameters
    /// (<c>τ_0_*</c>). A method-own generic can't be specialized from a single concrete subclass.
    /// </summary>
    private static bool HasMethodOwnGenerics(MethodDecl method)
        => method.GenericParameters.Any(gp => TryGetGenericDepth(gp.TypeName, out var d) && d >= 1);

    /// <summary>
    /// Parses the canonical depth out of a generic-parameter type name of the form
    /// <c>τ_&lt;depth&gt;_&lt;index&gt;</c> (or <c>t_&lt;depth&gt;_&lt;index&gt;</c>). Returns false for a
    /// sugared/plain name that carries no canonical depth.
    /// </summary>
    private static bool TryGetGenericDepth(string typeName, out int depth)
    {
        depth = 0;
        var parts = typeName.Split('_');
        return parts.Length >= 3 && int.TryParse(parts[1], out depth);
    }

    private static void TryEmitTrampoline(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodDecl method,
        ClassDecl leafDecl,
        ClassDecl baseDecl,
        string leafSwiftName,
        string moduleName,
        string wrapperLibPath,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        HashSet<string> emittedNames,
        ILogger logger)
    {
        var csMethodName = NameProvider.ToPascalCase(method.Name);
        if (!emittedNames.Add(csMethodName))
            return; // Duplicate visible signature (zero-arg ⇒ name is the whole key).

        var returnSpec = method.CSSignature.First().SwiftTypeSpec;
        bool isVoid = returnSpec.IsEmptyTuple;
        string swiftReturn = "";
        string csReturn = "void";
        if (!isVoid)
        {
            if (!TryResolveScalarReturn(returnSpec, out var swiftScalar, out var csScalar))
                return; // Preflight admitted only void/scalar; defensive.
            swiftReturn = $" -> {swiftScalar}";
            csReturn = csScalar;
        }

        // Disambiguating hash over the (leaf, base, method) identity so two leaves closing the same
        // base — or a base method name reused across leaves — never collide on the wrapper symbol.
        var hashInput = $"{moduleName}.{leafDecl.Name}|{baseDecl.SwiftTypeName.ModuleQualifiedName}|{method.Name}";
        var hash = EmitterUtility.DeterministicHash8(hashInput);
        // SBW_SCP_ ("subclass-closed parent") is a dedicated namespace for these trampolines, kept
        // distinct from the CSM emitter's SBW_CSM_ prefix so the two symbol families can never
        // collide and CSM's "no other emitter produces an SBW_CSM_ symbol" invariant stays honest.
        var cdeclSymbol = $"SBW_SCP_{moduleName}_{leafDecl.Name}_{method.Name}_{hash}";

        // Registry guard — every SBW_ wrapper symbol must be unique across the module.
        if (!emissionContext.TryAddMethodWrapperSymbol(cdeclSymbol, DeclIdFactory.ForMethod(method)))
            return;

        var swiftMethodName = NameProvider.ParserNameToSwift(method);
        var mergedAvailability = WrapperEmitterHelpers.MergeAvailability(method.AvailabilityAnnotations, baseDecl);
        bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
            baseDecl, method.IsMainActorIsolated, method.IsNonisolated);

        // --- Swift @_cdecl shim ---
        swiftWriter.WriteLine();
        swiftWriter.WriteLine($"// Subclass-closed base trampoline: {leafSwiftName}.{method.Name} (inherited from {baseDecl.Name})");
        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, cdeclSymbol, needsMainActor, mergedAvailability);
        swiftWriter.WriteLine($"public func {cdeclSymbol}(_ self_: UnsafeMutableRawPointer){swiftReturn} {{");
        swiftWriter.WriteLine($"    let __self = unsafeBitCast(OpaquePointer(self_), to: {leafSwiftName}.self)");
        swiftWriter.WriteLine($"    {(isVoid ? "" : "return ")}__self.{swiftMethodName}()");
        swiftWriter.WriteLine("}");

        // --- C# P/Invoke ---
        csWriter.WriteLine();
        AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(
            csWriter, mergedAvailability, baseDecl.AvailabilityAnnotations);
        PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
        {
            LibraryPath = wrapperLibPath,
            EntryPoint = cdeclSymbol,
            MethodName = cdeclSymbol,
            ReturnType = csReturn,
            ParametersString = "IntPtr self_",
            CallingConvention = PInvokeCallingConvention.Cdecl,
            Visibility = PInvokeVisibility.Internal,
            // Fail-closed: this SBW_ entry point was just registered above via
            // TryAddMethodWrapperSymbol, so the contract passes. If a future refactor ever
            // emitted the P/Invoke without registering the symbol, the contract throws rather
            // than shipping a LibraryImport that references a wrapper symbol nothing produced.
            EmissionContext = emissionContext,
            EnforceWrapperContract = true
        });

        // --- C# public extension method ---
        // No AddRef/Release: the live `self` reference keeps the handle valid across the
        // synchronous P/Invoke (matches the sync CSM extension convention).
        csWriter.WriteLine();
        AvailabilityAttributeEmitter.EmitSupportedOSPlatformsFromAnnotations(
            csWriter, mergedAvailability, baseDecl.AvailabilityAnnotations);
        csWriter.WriteLine($"/// <summary>Inherited from {baseDecl.Name}&lt;…&gt;.</summary>");
        csWriter.WriteLine($"public static {csReturn} {csMethodName}(this {leafDecl.Name} self)");
        csWriter.WriteLine("{");
        csWriter.Indent++;
        AvailabilityAttributeEmitter.EmitRuntimeAvailabilityGuard(
            csWriter, mergedAvailability, $"{leafDecl.Name}.{csMethodName}");
        var call = $"{cdeclSymbol}(((global::Swift.Runtime.ISwiftObject)self).SwiftHandle)";
        csWriter.WriteLine(isVoid ? $"{call};" : $"return {call};");
        csWriter.Indent--;
        csWriter.WriteLine("}");
    }

    /// <summary>
    /// Resolves a return <see cref="TypeSpec"/> to its (Swift, C#) blittable-scalar pair, taking the
    /// last dot-segment of a possibly module-qualified name (e.g. <c>Swift.Int32</c> → <c>Int32</c>).
    /// </summary>
    private static bool TryResolveScalarReturn(TypeSpec returnSpec, out string swiftType, out string csType)
    {
        swiftType = "";
        csType = "";
        if (returnSpec is not NamedTypeSpec named) return false;
        var name = named.Name;
        var dot = name.LastIndexOf('.');
        if (dot >= 0) name = name.Substring(dot + 1);
        if (!BlittableScalarReturns.TryGetValue(name, out var pair)) return false;
        swiftType = pair.Swift;
        csType = pair.CSharp;
        return true;
    }

    /// <summary>
    /// Strips generic arguments and namespace qualification from a superclass spelling, e.g.
    /// <c>SwiftBindingsTestLib.LifecycleKernel&lt;…&gt;</c> → <c>LifecycleKernel</c>.
    /// </summary>
    private static string ExtractSimpleBaseName(string superclassName)
    {
        var angle = superclassName.IndexOf('<');
        var head = angle >= 0 ? superclassName.Substring(0, angle) : superclassName;
        var dot = head.LastIndexOf('.');
        return dot >= 0 ? head.Substring(dot + 1) : head;
    }
}
