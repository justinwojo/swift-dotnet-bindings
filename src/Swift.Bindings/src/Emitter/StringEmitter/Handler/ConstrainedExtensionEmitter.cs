// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using static BindingsGeneration.ExtensionMarshallingHelper;

namespace BindingsGeneration;

/// <summary>
/// Emits C# extension method classes for constrained-extension properties on generic types.
///
/// When a generic type has constrained extensions like:
///   extension GenericType where T == ConcreteA { var prop: String { get } }
///   extension GenericType where T == ConcreteB { var prop: String { get } }
///
/// C# generics cannot dispatch among specializations at the call site. Rather than
/// skip these entirely, this emitter creates closed extension methods:
///   public static class GenericTypeConcreteAExtensions {
///       public static string GetProp(this GenericType&lt;ConcreteA&gt; self) { ... }
///   }
///
/// Each specialization gets its own @_cdecl Swift wrapper taking the concrete closed
/// generic type — no generic dispatch or metadata passing needed.
/// </summary>
public static class ConstrainedExtensionEmitter
{
    /// <summary>
    /// Scans a generic type for constrained-extension property groups and emits
    /// C# extension method classes for each concrete type specialization.
    /// </summary>
    public static void EmitConstrainedExtensions(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        TypeDecl typeDecl,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        ILogger logger)
    {
        if (!typeDecl.IsGeneric) return;

        // Value-type-projected frozen-struct parents are skipped: the call sites at
        // ~lines 315/331 emit `self.Payload.DangerousGetHandle()`, which assumes the
        // SafeHandle-backed `Payload` projection used by classes, non-frozen structs,
        // AND class-projected frozen structs (FrozenStructHandler.cs:168 emits
        // `public SwiftSafeHandle<T> Payload`). A *value-type*-projected frozen
        // struct (the `else` branch at FrozenStructHandler.cs:207) has no `Payload`
        // member at all — only direct backing fields matching Swift's memory layout.
        // Routing that shape through `self.Payload.DangerousGetHandle()` produces
        // uncompilable C#. The original bug 6 target (WeatherKit `Forecast<T>`
        // .Summary / .MinuteWeather) is a resilient (non-frozen) struct, so the
        // SafeHandle path covers it. A value-type-projected frozen-struct generic
        // with a same-type-constrained property is theoretical for current
        // validation libraries; supporting it requires teaching this emitter to
        // emit address/buffer-based dispatch, which is out of scope here.
        if (typeDecl is StructDecl sd && sd.IsFrozen)
        {
            var typeRecord = typeDatabase.GetTypeRecordOrThrow(typeDecl.SwiftTypeName);
            if (!MarshallingHelpers.IsFrozenStructProjectedAsClass(typeRecord))
            {
                logger.LogDebug(
                    $"Skipping constrained-extension emission for value-type-projected frozen struct {typeDecl.Name}: " +
                    "ConstrainedExtensionEmitter assumes SafeHandle-backed Payload, " +
                    "but value-type-projected frozen structs expose direct backing fields. " +
                    "Value-type-projected frozen structs expose direct backing fields rather than SafeHandle-backed Payload.");
                return;
            }
        }

        // Group constrained-extension properties + methods by concrete type. Both
        // surfaces share the same per-concrete extension class — properties emit as
        // `Get{Name}` static accessors, methods emit as `{Name}` static extension
        // methods on the closed-generic instance. Open-generic-return properties
        // (e.g. `var payloadValue: SignedType { get }` on the unconstrained base
        // extension) are also re-surfaced per concrete type with the open generic
        // parameter substituted at emit time.
        var propertySpecializations = FindConstrainedSpecializations(typeDecl);
        var methodSpecializations = FindConstrainedMethodSpecializations(typeDecl);

        // Open-generic-return members live on the unconstrained base extension and
        // are only re-surfaced when there is at least one constrained specialization
        // to anchor onto: each specialization picks up a per-concrete copy with the
        // open parameter substituted. Without an anchor we have no concrete type to
        // substitute, so the open-generic-return surface stays unreachable as before.
        var openGenericReturnProperties = (propertySpecializations.Count > 0 || methodSpecializations.Count > 0)
            ? FindOpenGenericReturnProperties(typeDecl)
            : new List<PropertyDecl>();

        var concreteTypes = new HashSet<SwiftTypeName>(propertySpecializations.Keys);
        foreach (var key in methodSpecializations.Keys) concreteTypes.Add(key);
        if (concreteTypes.Count == 0) return;

        var moduleName = typeDecl.SwiftTypeName.Module;
        var wrapperLibPath = typeDatabase.AsyncLibraryName ?? "libSwiftBindings";

        foreach (var concreteTypeName in concreteTypes)
        {
            propertySpecializations.TryGetValue(concreteTypeName, out var properties);
            methodSpecializations.TryGetValue(concreteTypeName, out var methods);
            EmitSpecializationClass(
                csWriter, swiftWriter, typeDecl, concreteTypeName,
                properties ?? new List<PropertyDecl>(),
                methods ?? new List<MethodDecl>(),
                openGenericReturnProperties,
                moduleName, wrapperLibPath, typeDatabase, emissionContext, logger);
        }
    }

    /// <summary>
    /// Finds constrained-extension property groups: properties whose accessor
    /// carries a same-type equality constraint (e.g.
    /// `extension Wrapper where T == Concrete`). Returns a map from concrete
    /// type to list of properties for that specialization.
    ///
    /// Both single-specialization (one sibling) and multi-specialization (many
    /// siblings) cases are emitted, because in either case the accessor mangled
    /// name is bound to the closed generic instantiation — not the open generic
    /// — and emitting at the open-generic class level would PInvoke the wrong
    /// symbol for non-matching instantiations. Multi-spec groups are accepted
    /// only when EVERY sibling carries a same-type constraint; mixed
    /// open + constrained groups are rejected to avoid namespace collision with
    /// the open-generic property emission.
    /// </summary>
    internal static Dictionary<SwiftTypeName, List<PropertyDecl>> FindConstrainedSpecializations(TypeDecl typeDecl)
    {
        var result = new Dictionary<SwiftTypeName, List<PropertyDecl>>();

        if (!typeDecl.IsGeneric)
            return result;

        // Group properties by (name, isStatic) so we can detect mixed
        // open-generic + constrained-extension siblings.
        var groups = new Dictionary<(string Name, bool IsStatic), List<PropertyDecl>>();
        foreach (var property in typeDecl.Properties)
        {
            var key = (property.Name, property.IsStatic);
            if (!groups.ContainsKey(key))
                groups[key] = new List<PropertyDecl>();
            groups[key].Add(property);
        }

        foreach (var (_, siblings) in groups)
        {
            // For both single- and multi-sibling groups: every sibling must
            // carry a parseable same-type constraint. Mixed open + constrained
            // groups are rejected so we don't emit closed extension methods
            // that conflict with an open-generic property of the same name.
            bool allConstrained = true;
            foreach (var sibling in siblings)
            {
                if (ExtractSameTypeConstraint(sibling) == null)
                {
                    allConstrained = false;
                    break;
                }
            }

            if (!allConstrained) continue;

            // Group each sibling under its concrete type.
            foreach (var sibling in siblings)
            {
                var concreteType = ExtractSameTypeConstraint(sibling)!;
                if (!result.ContainsKey(concreteType))
                    result[concreteType] = new List<PropertyDecl>();
                result[concreteType].Add(sibling);
            }
        }

        return result;
    }

    /// <summary>
    /// Extracts the concrete type from a same-type equality constraint on a property's
    /// getter accessor. Returns null if the property is not from a constrained extension.
    ///
    /// Only direct same-type constraints on the parent's generic parameter
    /// (e.g. <c>where T == Concrete</c>) are returned, because the closed-extension
    /// emission below needs a concrete <c>T</c> to instantiate the parent against.
    /// Dependent-member same-type constraints (e.g. <c>where Value.ValueType == X</c>)
    /// land in <see cref="GenericArgumentDecl.AssosiatedTypeConformances"/> and are
    /// handled by <see cref="HasParentExtensionSameTypeConstraint(PropertyDecl)"/>:
    /// they suppress the open-generic emission but cannot be re-emitted as closed
    /// extensions because no single concrete parent generic argument satisfies them.
    /// </summary>
    internal static SwiftTypeName? ExtractSameTypeConstraint(PropertyDecl property)
    {
        var getter = property.Accessors.OfType<GetAccessorDecl>().FirstOrDefault();
        if (getter == null) return null;

        foreach (var genericParam in getter.Method.GenericParameters)
        {
            foreach (var conformance in genericParam.GenericConformances)
            {
                if (conformance.Kind == ConformanceKind.ConcreteType &&
                    IsSpecializableConcretePin(conformance.ConformanceTarget))
                    return conformance.ConformanceTarget;
            }
        }

        return null;
    }

    /// <summary>
    /// A same-type pin can only close this generic parent if it names a real type. When the
    /// sugared generic signature is unavailable the parser falls back to the raw ABI spelling,
    /// where a dependent-member pin reads as a placeholder-rooted path (<c>τ_0_0.Bridge.T</c>).
    /// That is dotted, so it carries a well-formed-looking module-qualified name, but its root
    /// segment is a generic parameter rather than a module — substitution never supplied a
    /// concrete type here. Rendering it anyway spells the parent as
    /// <c>Parent&lt;τ_0_0.Bridge.T&gt;</c> in Swift source and sanitizes the same text into the
    /// @_cdecl symbol, which cannot compile ("generic parameter could not be inferred").
    /// Reporting it as un-specializable turns the whole member into one precise skip instead.
    /// The pin stays intact on the decl: other consumers read it as a structural marker for
    /// "pinned to another parameter's associated type", and this only decides whether THIS
    /// emitter can close the parent over it.
    /// </summary>
    private static bool IsSpecializableConcretePin(SwiftTypeName concreteTypeName)
    {
        var parts = concreteTypeName.ModuleQualifiedName.Split('.', StringSplitOptions.RemoveEmptyEntries);

        // Only the ROOT segment decides. It sits in module position, so a generic parameter there
        // means the path hangs off a stand-in. Later segments sit in type position, where a
        // one-letter name is a real type (`MyModule.T` pins to a type genuinely named T) and the
        // placeholder test would misread it as a parameter.
        return parts.Length > 0 && !TypeSpecHelpers.IsGenericTypeParameter(parts[0]);
    }

    /// <summary>
    /// True when a property only exists by virtue of a parent-extension same-type
    /// constraint — either a direct constraint on the parent generic parameter
    /// (<c>where T == Concrete</c>) or a dependent-member constraint on one of its
    /// associated types (<c>where Value.ValueType == Concrete</c>). The former is the
    /// closed-extension shape <see cref="ExtractSameTypeConstraint"/> already returns;
    /// the latter only surfaces via <see cref="GenericArgumentDecl.AssosiatedTypeConformances"/>.
    ///
    /// Either shape makes the property unsatisfiable in an unconstrained protocol-group
    /// conformance extension <c>extension Wrapper&lt;Value&gt;: _SBW_PG_X {}</c>, because
    /// the property is only callable for specific <c>Value</c> instantiations. The
    /// open-generic emission must skip these properties unconditionally; the direct-case
    /// is re-emitted by <see cref="ConstrainedExtensionEmitter"/>, while the
    /// dependent-member case is dropped (no concrete parent generic argument to bind to).
    /// </summary>
    internal static bool HasParentExtensionSameTypeConstraint(PropertyDecl property)
    {
        var getter = property.Accessors.OfType<GetAccessorDecl>().FirstOrDefault();
        if (getter == null) return false;

        foreach (var genericParam in getter.Method.GenericParameters)
        {
            foreach (var conformance in genericParam.GenericConformances)
            {
                if (conformance.Kind == ConformanceKind.ConcreteType)
                    return true;
            }
            foreach (var conformance in genericParam.AssosiatedTypeConformances)
            {
                if (conformance.Kind == ConformanceKind.ConcreteType)
                    return true;
            }
        }

        return false;
    }

    private static void EmitSpecializationClass(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        TypeDecl typeDecl,
        SwiftTypeName concreteTypeName,
        List<PropertyDecl> properties,
        List<MethodDecl> methods,
        List<PropertyDecl> openGenericReturnProperties,
        string moduleName,
        string wrapperLibPath,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        ILogger logger)
    {
        // Resolve C# type names
        var parentCsName = typeDecl.Name;
        var concreteCsName = ResolveCSharpName(concreteTypeName, typeDatabase);
        if (concreteCsName == null)
        {
            logger.LogWarning(
                "ConstrainedExtensionEmitter: Cannot resolve C# name for concrete type {Type}, skipping.",
                concreteTypeName);
            return;
        }

        // Primitive types (int, uint, float, etc.) cannot satisfy ISwiftObject constraints
        // on generic parent types, so skip them.
        if (IsCSharpPrimitiveType(concreteCsName))
        {
            logger.LogDebug(
                "ConstrainedExtensionEmitter: Skipping primitive concrete type {Type} — cannot satisfy ISwiftObject.",
                concreteCsName);
            return;
        }

        var closedGenericCsType = $"{parentCsName}<{concreteCsName}>";
        // Both the parent slot and the concretization slot are rendered through the
        // injective `SanitizeForSymbol(ModuleQualifiedName)` encoding, AND a literal
        // `_` separator is placed between them so that two pairs whose encoded
        // segments could otherwise concatenate to the same string (e.g.
        // parent=`A.A`, concrete=`AA.A` → `A_DAAA_DA` vs parent=`A.AA`,
        // concrete=`A.A` → same `A_DAAA_DA` without a boundary char) instead
        // render distinct class names. `parentCsName` alone is the leaf declaration
        // name (e.g. `Box` for both `Mod.Box` and `Mod.Outer.Box`); without parent
        // qualification, two nested generic parents would produce duplicate
        // `BoxFooExtensions` classes (CS0102).
        var className = $"{SanitizeParent(typeDecl)}_{SanitizeConcretization(concreteTypeName)}Extensions";

        csWriter.WriteLine();
        csWriter.WriteLine("/// <summary>");
        csWriter.WriteLine($"/// Extension methods for {closedGenericCsType} from constrained Swift extensions.");
        csWriter.WriteLine("/// </summary>");
        csWriter.WriteLine($"public static partial class {className}");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        int emittedCount = 0;
        var pinvokeDeclarations = new List<Action>();

        foreach (var property in properties)
        {
            if (TryEmitPropertyExtension(
                csWriter, swiftWriter, property, typeDecl, concreteTypeName,
                closedGenericCsType, moduleName, wrapperLibPath,
                typeDatabase, emissionContext, pinvokeDeclarations, logger,
                substitutedReturnTypeSpec: null))
            {
                emittedCount++;
            }
        }

        // Open-generic-return properties (e.g. `var payloadValue: SignedType { get }`):
        // re-emit per concrete specialization with the parent's open generic parameter
        // substituted by the concrete type. Same emit shape as the constrained-extension
        // case — only the return type spec is rewritten before classification.
        foreach (var property in openGenericReturnProperties)
        {
            var substituted = SubstituteParentGenericParameter(
                property.SwiftTypeSpec, typeDecl, concreteTypeName);
            if (substituted == null)
            {
                // Substitution failed — open-generic shape too complex (e.g. nested
                // generic with multiple parent params, or a param we can't resolve).
                // Surface a diagnostic so the routed-but-not-emitted member doesn't
                // disappear from generated output (mirrors the in-emitter shape-bail
                // diagnostics in TryEmitPropertyExtension / TryEmitMethodExtension).
                UnsupportedCommentEmitter.EmitMemberSkipped(
                    csWriter, property.Name, BindingItemKind.Property,
                    SkipReason.UnsupportedSignature,
                    $"on {typeDecl.Name}<{concreteTypeName.Name}>: open-generic return substitution unsupported",
                    containingDecl: property.ParentDecl);
                logger.LogDebug(
                    "ConstrainedExtensionEmitter: Skipping open-generic-return property {Name} on {Parent}<{Concrete}> — substitution unsupported.",
                    property.Name, typeDecl.Name, concreteTypeName.Name);
                continue;
            }
            if (TryEmitPropertyExtension(
                csWriter, swiftWriter, property, typeDecl, concreteTypeName,
                closedGenericCsType, moduleName, wrapperLibPath,
                typeDatabase, emissionContext, pinvokeDeclarations, logger,
                substitutedReturnTypeSpec: substituted))
            {
                emittedCount++;
            }
        }

        foreach (var method in methods)
        {
            if (TryEmitMethodExtension(
                csWriter, swiftWriter, method, typeDecl, concreteTypeName,
                closedGenericCsType, moduleName, wrapperLibPath,
                typeDatabase, emissionContext, pinvokeDeclarations, logger))
            {
                emittedCount++;
            }
        }

        // Emit NativeMethods class with P/Invoke declarations
        if (emittedCount > 0 && pinvokeDeclarations.Count > 0)
        {
            csWriter.WriteLine();
            csWriter.WriteLine("private static partial class NativeMethods");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            foreach (var emitPInvoke in pinvokeDeclarations)
                emitPInvoke();

            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");

        if (emittedCount > 0)
        {
            logger.LogInformation(
                "Emitted {Count} constrained-extension members for {Type}<{Concrete}>",
                emittedCount, typeDecl.Name, concreteTypeName.Name);
        }
    }

    /// <summary>
    /// Classifies the return shape of a constrained-extension property or method for emission.
    /// Internal so the shared classifier <see cref="ClassifyCEReturnShape"/> can return it
    /// to both the property and method emission paths.
    /// </summary>
    internal enum CEReturnShape
    {
        /// <summary>Swift.String — returned via Utf8Slice in a 16-byte caller buffer.</summary>
        String,
        /// <summary>Swift primitive (Int*, Bool, Float, Double, ...) — returned by value.</summary>
        Primitive,
        /// <summary>Resilient (non-frozen) Swift struct (e.g. CryptoKit.P256.Signing.ECDSASignature) — returned via indirect-result buffer sized by the type's VWT. Buffer ownership transfers to the returned SafeHandle.</summary>
        NonFrozenStruct,
        /// <summary>Foundation.Date — Swift ABI is Double (timeIntervalSinceReferenceDate); converted to System.DateTimeOffset via the Swift epoch.</summary>
        FoundationDate,
        /// <summary>Foundation.UUID — frozen 16-byte value-type; returned via indirect-result, copied to System.Guid, buffer freed in finally.</summary>
        FoundationUUID,
        /// <summary>Foundation.Data — frozen 16-byte struct (flags + object pointer); returned via indirect-result, projected as byte[] via Swift.Foundation.Data.ToByteArray(), buffer freed in finally.</summary>
        FoundationData,
    }

    private static bool TryEmitPropertyExtension(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        PropertyDecl property,
        TypeDecl parentTypeDecl,
        SwiftTypeName concreteTypeName,
        string closedGenericCsType,
        string moduleName,
        string wrapperLibPath,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        List<Action> pinvokeDeclarations,
        ILogger logger,
        TypeSpec? substitutedReturnTypeSpec)
    {
        // The property emit shape always reconstructs `obj` from a `_self`
        // pointer and accesses `obj.{property.Name}` — there is no static branch
        // mirroring the method path's `if (isStatic)` (no `Container<C>.foo`
        // call form, no dropped self_ param). A constrained `static var` would
        // therefore generate Swift that is silently stripped at wrapper-build
        // time, producing a missing-symbol C# extension. Skip with an explicit
        // diagnostic so the unsupported shape is visible rather than absorbed.
        if (property.IsStatic)
        {
            UnsupportedCommentEmitter.EmitMemberSkipped(
                csWriter, property.Name, BindingItemKind.Property,
                SkipReason.UnsupportedSignature,
                $"on {parentTypeDecl.Name}<{concreteTypeName.Name}>: constrained static property emission not yet supported",
                containingDecl: property.ParentDecl);
            logger.LogDebug(
                "ConstrainedExtensionEmitter: Skipping static property {Name} on {Parent}<{Concrete}> — static-property emission not yet supported.",
                property.Name, parentTypeDecl.Name, concreteTypeName.Name);
            return false;
        }

        // For open-generic-return properties (`payloadValue`-shape), substitute the
        // parent's open generic parameter with the concrete specialization before
        // classifying the return shape. Constrained-extension properties pass null
        // and use the property's own SwiftTypeSpec.
        var effectiveReturnTypeSpec = substitutedReturnTypeSpec ?? property.SwiftTypeSpec;

        // Bound-generic returns (e.g. open-generic `Query<T>` substituted to
        // `Query<Concrete>`) survive substitution but `ClassifyReturnType` rejects
        // any spec with remaining generic parameters. Drop with an explicit
        // diagnostic so the unsupported shape stays visible rather than being
        // absorbed by the generic "unsupported return shape" branch below.
        if (substitutedReturnTypeSpec != null
            && effectiveReturnTypeSpec is NamedTypeSpec substitutedPropNamed
            && substitutedPropNamed.ContainsGenericParameters)
        {
            UnsupportedCommentEmitter.EmitMemberSkipped(
                csWriter, property.Name, BindingItemKind.Property,
                SkipReason.UnsupportedSignature,
                $"on {parentTypeDecl.Name}<{concreteTypeName.Name}>: bound-generic return ({substitutedPropNamed.Name}) not yet supported",
                containingDecl: property.ParentDecl);
            logger.LogDebug(
                "ConstrainedExtensionEmitter: Skipping property {Name} on {Parent}<{Concrete}> — bound-generic return ({ReturnType}) is not yet supported.",
                property.Name, parentTypeDecl.Name, concreteTypeName.Name, substitutedPropNamed.Name);
            return false;
        }

        var classification = ClassifyCEReturnShape(effectiveReturnTypeSpec, typeDatabase);
        if (classification == null)
        {
            UnsupportedCommentEmitter.EmitMemberSkipped(
                csWriter, property.Name, BindingItemKind.Property,
                SkipReason.UnsupportedSignature,
                $"on {parentTypeDecl.Name}<{concreteTypeName.Name}>: unsupported return type",
                containingDecl: property.ParentDecl);
            logger.LogDebug(
                "ConstrainedExtensionEmitter: Skipping property {Name} on {Parent}<{Concrete}> — unsupported return type.",
                property.Name, parentTypeDecl.Name, concreteTypeName.Name);
            return false;
        }
        var (shape, csharpReturnType) = classification.Value;

        var propertyName = NameProvider.ToPascalCase(property.Name);

        // Build @_cdecl symbol name. Both the parent slot and the concretization
        // slot use the injective `SanitizeForSymbol(ModuleQualifiedName)` encoding
        // so two distinct Swift parents that share a leaf name (e.g. `Mod.Box` vs
        // `Mod.Outer.Box`) and two concrete types in different modules with the
        // same leaf name (e.g. `A.User` vs `B.User`) all render to distinct SBW_
        // strings. Without that, the structural-identity tuple distinguishes them
        // but `_registeredWrapperSymbols.Add(symbol)` would reject the second
        // claim on a colliding `SBW_CEGet_..._User_*` and silently drop the wrapper.
        // The static/instance marker is forward-defense for Swift's allowance
        // of `var rank` alongside `static var rank` on the same type. Static
        // constrained properties are currently rejected at the top of this
        // method (the property emit shape has no static branch), so all
        // wrappers reaching this point use `_instance`; the marker keeps the
        // dedup discipline correct if static-property emission is added later
        // without revisiting the structural-identity keys.
        var safeParentName = SanitizeParent(parentTypeDecl);
        var safeConcreteName = SanitizeConcretization(concreteTypeName);
        var staticMarker = property.IsStatic ? "_static" : "_instance";
        var symbolName = $"SBW_CEGet_{safeParentName}_{safeConcreteName}_{property.Name}{staticMarker}";

        // Dedup guard via the structural-identity claim API. S5: routing through
        // TryClaimWrapperSymbol with a sourceKey that anchors to the constrained-extension
        // identity (parent type + concretization + static/instance + getter for property)
        // locks the bug class closed for any future overlap with adjacent property-wrapper
        // paths on the same closed generic shape. The structural tuple uses the parent's
        // and concrete type's raw `SwiftTypeName.ModuleQualifiedName` (not the leaf
        // `Name`) so two parents that share a leaf (`Mod.Box` vs `Mod.Outer.Box`) and
        // two concretes that share a leaf across modules (`A.User` vs `B.User`) get
        // distinct identity buckets. There is no MethodWrapperEmitter counterpart to
        // align with — MWE walks the open generic parent, never the closed generic
        // concretizations this emitter synthesizes — so the structural identity is
        // intentionally intra-emitter (constrained-extension → constrained-extension only).
        var qualifiedParent = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;
        var qualifiedConcrete = concreteTypeName.ModuleQualifiedName;
        var dispatchMarker = property.IsStatic ? "static" : "instance";
        if (!emissionContext.TryClaimWrapperSymbol(
                $"{qualifiedParent}<{qualifiedConcrete}>",
                $"{property.Name}::{dispatchMarker}",
                $"constrained-extension::get::{qualifiedParent}::{qualifiedConcrete}::{dispatchMarker}::{property.Name}",
                symbolName,
                DeclIdFactory.ForProperty(property, AccessorKind.Getter)))
            return false;

        // ----- C# extension method -----
        csWriter.WriteLine();
        csWriter.WriteLine($"public static {csharpReturnType} Get{propertyName}(this {closedGenericCsType} self)");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        switch (shape)
        {
            case CEReturnShape.String:
                // String: stackalloc a fixed 2-word (SBW_Utf8Slice) scratch buffer to receive
                // the slice (pointer + length), call the P/Invoke, then marshal the slice to a
                // managed string. ReadUtf8Slice copies the bytes out before we return, so the
                // buffer never escapes the frame — no per-call malloc/free needed. (The former
                // NativeMemory.Alloc/Free only reclaimed this 16-byte container; stackalloc
                // reclaims it identically on frame exit, including the exceptional path.)
                csWriter.WriteLine("unsafe");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine("byte* _cdeclBuf = stackalloc byte[nint.Size * 2];");
                csWriter.WriteLine("var resultPtr = (IntPtr)_cdeclBuf;");
                csWriter.WriteLine($"NativeMethods.{symbolName}(resultPtr, self.Payload.DangerousGetHandle());");
                csWriter.WriteLine("return SwiftMarshal.ReadUtf8Slice(resultPtr);");
                csWriter.Indent--;
                csWriter.WriteLine("}");
                break;

            case CEReturnShape.Primitive:
                // Primitive: direct P/Invoke call
                csWriter.WriteLine($"return NativeMethods.{symbolName}(self.Payload.DangerousGetHandle());");
                break;

            case CEReturnShape.NonFrozenStruct:
                // Indirect-result shape: allocate VWT-sized buffer, pass SwiftIndirectResult
                // to the @_cdecl wrapper, then marshal the value out. Must match the existing
                // PropertyHandler emission so MarshalFromSwift<T> sees the same layout.
                //
                // Ownership: for non-frozen structs / classes projected as ISwiftObject,
                // SwiftMarshal.MarshalFromSwift<T>(buffer) wraps the buffer in the returned
                // SafeHandle and the wrapper takes ownership. We must NOT free on the
                // success path (use-after-free / double-free on disposal of the returned
                // object). The try / catch+Free+rethrow shape mirrors
                // ExtensionMarshallingHelper.cs:263 (ReturnKind.NonFrozenStruct): on the
                // exceptional path the wrapper call or MarshalFromSwift throws before
                // ownership transfers, so the buffer must be freed to avoid a leak; on
                // the success path control returns through MarshalFromSwift before
                // reaching the catch and the SafeHandle owns the buffer thereafter.
                csWriter.WriteLines($$"""
                    unsafe
                    {
                        var metadata = SwiftObjectHelper<{{csharpReturnType}}>.GetTypeMetadata();
                        IntPtr buffer = (IntPtr)System.Runtime.InteropServices.NativeMemory.Alloc(metadata.Size);
                        try
                        {
                            var indirectResult = new SwiftIndirectResult((void*)buffer);
                            NativeMethods.{{symbolName}}(indirectResult, self.Payload.DangerousGetHandle());
                            return SwiftMarshal.MarshalFromSwift<{{csharpReturnType}}>(buffer);
                        }
                        catch
                        {
                            System.Runtime.InteropServices.NativeMemory.Free((void*)buffer);
                            throw;
                        }
                    }
                    """);
                break;

            case CEReturnShape.FoundationDate:
                // Date is `frozen=true requiresMemoryManagement=false`; its Swift ABI is a
                // single `Double` carrying `timeIntervalSinceReferenceDate` (seconds since
                // 2001-01-01 UTC). Project to System.DateTimeOffset via the same Swift epoch
                // constant used by DateProjection.GetReturnPlan(Direct).
                csWriter.WriteLine(
                    $"var seconds = NativeMethods.{symbolName}(self.Payload.DangerousGetHandle());");
                csWriter.WriteLine(
                    $"return {DateProjection.SwiftEpoch}.AddSeconds(seconds);");
                break;

            case CEReturnShape.FoundationUUID:
                // UUID is a frozen 16-byte tuple of UInt8s. Mirror the existing
                // BlittableProjection("System.Guid") shape: stackalloc a 16-byte indirect-result
                // buffer, read it back as a System.Guid (value-copied out before the frame
                // exits), so no per-call malloc/free is needed. (The former NativeMemory.Alloc/
                // Free only reclaimed this 16-byte container; stackalloc reclaims it identically,
                // including the exceptional path — Guid is frozen, no ARC/ownership in the buffer.)
                csWriter.WriteLines($$"""
                    unsafe
                    {
                        byte* buffer = stackalloc byte[16];
                        var indirectResult = new SwiftIndirectResult((void*)buffer);
                        NativeMethods.{{symbolName}}(indirectResult, self.Payload.DangerousGetHandle());
                        return *(System.Guid*)buffer;
                    }
                    """);
                break;

            case CEReturnShape.FoundationData:
                // Foundation.Data is `frozen=true requiresMemoryManagement=false` with a
                // 16-byte Swift layout (long _flags + IntPtr _object). Mirror the
                // (*(Swift.Foundation.Data*)(void*)buffer).ToByteArray() pattern used by
                // WrapperEmitter.Return.cs:1316. The Data struct is value-copied out of the
                // buffer and ToByteArray() copies the underlying bytes via Swift's CopyBytes
                // P/Invoke before we return — so the 16-byte container can be a stackalloc
                // (reclaimed on frame exit) instead of a per-call malloc/free. The free only
                // ever reclaimed the container (the copied-out Data value carries the _object
                // pointer independently); stackalloc reclaims it identically.
                csWriter.WriteLines($$"""
                    unsafe
                    {
                        byte* buffer = stackalloc byte[16];
                        var indirectResult = new SwiftIndirectResult((void*)buffer);
                        NativeMethods.{{symbolName}}(indirectResult, self.Payload.DangerousGetHandle());
                        return (*(Swift.Foundation.Data*)(void*)buffer).ToByteArray();
                    }
                    """);
                break;
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");

        // ----- Swift @_cdecl wrapper -----
        EmitSwiftGetterWrapper(swiftWriter, property, parentTypeDecl, concreteTypeName,
            symbolName, moduleName, shape, emissionContext, effectiveReturnTypeSpec, typeDatabase);

        // ----- Queue P/Invoke declaration -----
        var capturedSymbol = symbolName;
        var capturedShape = shape;
        var capturedReturnType = csharpReturnType;
        pinvokeDeclarations.Add(() =>
        {
            var pinvokeParams = new List<string>();
            string pinvokeReturnType;

            switch (capturedShape)
            {
                case CEReturnShape.String:
                    pinvokeParams.Add("IntPtr resultPtr");
                    pinvokeParams.Add("IntPtr _self");
                    pinvokeReturnType = "void";
                    break;
                case CEReturnShape.NonFrozenStruct:
                case CEReturnShape.FoundationUUID:
                case CEReturnShape.FoundationData:
                    pinvokeParams.Add("SwiftIndirectResult indirectResult");
                    pinvokeParams.Add("IntPtr _self");
                    pinvokeReturnType = "void";
                    break;
                case CEReturnShape.FoundationDate:
                    // Date returns timeIntervalSinceReferenceDate as a single Double in xmm0/d0.
                    pinvokeParams.Add("IntPtr _self");
                    pinvokeReturnType = "double";
                    break;
                default: // Primitive
                    pinvokeParams.Add("IntPtr _self");
                    pinvokeReturnType = capturedReturnType;
                    break;
            }

            PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
            {
                LibraryPath = wrapperLibPath,
                EntryPoint = capturedSymbol,
                MethodName = capturedSymbol,
                ReturnType = pinvokeReturnType,
                ParametersString = string.Join(", ", pinvokeParams),
                CallingConvention = PInvokeCallingConvention.Cdecl,
                Visibility = PInvokeVisibility.Internal
            });
            csWriter.WriteLine();
        });

        return true;
    }

    private static void EmitSwiftGetterWrapper(
        SwiftWriter swiftWriter,
        PropertyDecl property,
        TypeDecl parentTypeDecl,
        SwiftTypeName concreteTypeName,
        string symbolName,
        string moduleName,
        CEReturnShape shape,
        ModuleEmissionContext emissionContext,
        TypeSpec effectiveReturnTypeSpec,
        ITypeDatabase typeDatabase)
    {
        var parentSwiftName = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;
        var concreteSwiftName = concreteTypeName.ModuleQualifiedName;
        var closedGenericSwiftType = $"{parentSwiftName}<{concreteSwiftName}>";

        // Ensure SBW_Utf8Slice is available for string properties
        if (shape == CEReturnShape.String)
        {
            Utf8SliceEmitter.EmitIfNeeded(swiftWriter, emissionContext);
            Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter, moduleName, emissionContext);
        }

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Constrained-extension getter for {{closedGenericSwiftType}}.{{property.Name}}.
            // Concrete specialization — no generic dispatch needed.
            """);

        // Build parameter list — indirect-result shapes use a caller-provided buffer
        // (resultPtr first); Primitive and FoundationDate return by value.
        var swiftParams = new List<string>();
        bool usesIndirectResult = shape == CEReturnShape.String
            || shape == CEReturnShape.NonFrozenStruct
            || shape == CEReturnShape.FoundationUUID
            || shape == CEReturnShape.FoundationData;
        if (usesIndirectResult)
            swiftParams.Add("_ resultPtr: UnsafeMutableRawPointer");
        swiftParams.Add("_ self_: UnsafeRawPointer");

        var returnClause = shape switch
        {
            CEReturnShape.Primitive => $" -> {ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(effectiveReturnTypeSpec)}",
            CEReturnShape.FoundationDate => " -> Double",
            _ => "",
        };
        var swiftParamString = string.Join(", ", swiftParams);

        // Swift function name uses hash to avoid collisions
        var hash = EmitterUtility.DeterministicHash8(symbolName);
        var swiftFuncName = $"_sbw_ceget_{property.Name}_{hash}";

        // Substituted open-generic-return paths (`payloadValue`-shape) bind the
        // wrapper body to the concrete type, so the wrapper's @available must
        // inherit the concrete type's annotations too — see
        // MergeWrapperAvailability for the rationale.
        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, symbolName, needsMainActor: false,
            MergeWrapperAvailability(property.AvailabilityAnnotations, parentTypeDecl, concreteTypeName, typeDatabase));
        swiftWriter.WriteLine($"public func {swiftFuncName}({swiftParamString}){returnClause} {{");
        swiftWriter.Indent++;

        // Reconstruct self from pointer — structs / enums use memory binding, classes use Unmanaged
        if (parentTypeDecl is ClassDecl)
            swiftWriter.WriteLine($"let obj = Unmanaged<{closedGenericSwiftType}>.fromOpaque(self_).takeUnretainedValue()");
        else
            swiftWriter.WriteLine($"let obj = self_.assumingMemoryBound(to: {closedGenericSwiftType}.self).pointee");

        // Emit getter body
        var propAccess = $"obj.{property.Name}";
        switch (shape)
        {
            case CEReturnShape.String:
                StringReturnEmitter.EmitGetterBody(swiftWriter, propAccess);
                break;
            case CEReturnShape.Primitive:
                swiftWriter.WriteLine($"return {propAccess}");
                break;
            case CEReturnShape.NonFrozenStruct:
                // Indirect-result write — matches PropertyHandler's emission shape so
                // `MarshalFromSwift<T>(buffer)` reads the correct VWT-sized layout.
                // Module-qualified type spec is required for .initializeMemory(as:) —
                // unqualified names (e.g. `P256.Signing.ECDSASignature`) won't resolve
                // unless the wrapper file imports the source module (e.g. CryptoKit),
                // which we can't guarantee. Fully qualified (`CryptoKit.P256.…`)
                // resolves through the framework's transitive imports.
                swiftWriter.WriteLine($"let result = {propAccess}");
                swiftWriter.WriteLine($"resultPtr.initializeMemory(as: {ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(effectiveReturnTypeSpec)}.self, repeating: result, count: 1)");
                break;
            case CEReturnShape.FoundationDate:
                // Foundation.Date.timeIntervalSinceReferenceDate is a TimeInterval (= Double)
                // counting seconds since 2001-01-01 UTC, which is exactly the Swift epoch
                // DateProjection consumes on the C# side. No buffer write needed.
                swiftWriter.WriteLine($"return {propAccess}.timeIntervalSinceReferenceDate");
                break;
            case CEReturnShape.FoundationUUID:
                // Foundation.UUID is a frozen 16-byte struct. Write its bytes into the
                // indirect-result buffer; the C# side reads them as System.Guid via
                // `*(System.Guid*)buffer`. Layout matches BlittableProjection("System.Guid").
                swiftWriter.WriteLine($"let result = {propAccess}");
                swiftWriter.WriteLine("resultPtr.initializeMemory(as: Foundation.UUID.self, repeating: result, count: 1)");
                break;
            case CEReturnShape.FoundationData:
                // Foundation.Data is a frozen 16-byte struct (flags + storage pointer); the
                // 16-byte value-type is written into the indirect-result buffer. The C# side
                // reads it as Swift.Foundation.Data and calls .ToByteArray() to materialize
                // the byte[]; the runtime ARC ownership of the underlying storage is held by
                // the value while we read it.
                swiftWriter.WriteLine($"let result = {propAccess}");
                swiftWriter.WriteLine("resultPtr.initializeMemory(as: Foundation.Data.self, repeating: result, count: 1)");
                break;
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    private static string RenderSwiftReturnType(PropertyDecl property)
    {
        return ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(property.SwiftTypeSpec);
    }

    /// <summary>
    /// Classifies a constrained-extension return type spec into one of the
    /// supported emission shapes plus its idiomatic C# return type. Returns
    /// null if the shape is unsupported (e.g. ObjC class, raw generic param,
    /// frozen value-type struct without ref fields). Mirrors the inline shape
    /// detection that previously lived in TryEmitPropertyExtension; factored
    /// out so the method emission path can share the same classifier.
    /// </summary>
    internal static (CEReturnShape Shape, string CSharpType)? ClassifyCEReturnShape(
        TypeSpec? returnTypeSpec, ITypeDatabase typeDatabase)
    {
        if (returnTypeSpec == null) return null;

        // String / Foundation value-type early branches mirror the existing
        // detection order — these shapes need type-specific marshalling at the
        // C# boundary that the generic ClassifyReturnType doesn't model.
        if (WitnessDispatchEmitter.IsStringType(returnTypeSpec))
            return (CEReturnShape.String, "string");
        if (IsFoundationType(returnTypeSpec, "Foundation.Date"))
            return (CEReturnShape.FoundationDate, "System.DateTimeOffset");
        if (IsFoundationType(returnTypeSpec, "Foundation.UUID"))
            return (CEReturnShape.FoundationUUID, "System.Guid");
        if (IsFoundationType(returnTypeSpec, "Foundation.Data"))
        {
            // The emitted body casts through Swift.Foundation.Data which lives in
            // SwiftBindings.Apple. The csproj emitter only adds the supplement
            // PackageReference when something records a reference. This emitter
            // bypasses TypeProjectionFactory (which records the Foundation.Data
            // identity itself), so record explicitly here.
            AppleSupplementReferences.Record("Foundation.Data", "ConstrainedExtensionEmitter:FoundationData");
            return (CEReturnShape.FoundationData, "byte[]");
        }

        var returnCategory = ClassifyReturnType(returnTypeSpec, typeDatabase);
        if (returnCategory == null || returnCategory.Value == ReturnKind.Void)
            return null;

        switch (returnCategory.Value)
        {
            case ReturnKind.Primitive:
                return (CEReturnShape.Primitive, ResolveCSharpTypeName(returnTypeSpec, typeDatabase));
            case ReturnKind.NonFrozenStruct:
                return (CEReturnShape.NonFrozenStruct, ResolveCSharpTypeName(returnTypeSpec, typeDatabase));
            default:
                return null;
        }
    }

    /// <summary>
    /// Substitutes the parent type's open generic parameter with the concrete
    /// specialization's TypeSpec inside <paramref name="typeSpec"/>. Used for
    /// `payloadValue`-shape members whose return type references the open
    /// parent generic param (e.g. `var payloadValue: SignedType` on
    /// `extension VerificationResult`). Returns null when the substitution is
    /// ambiguous or unsupported (multiple unresolved parent params, structurally
    /// nested params we can't cleanly walk).
    /// </summary>
    internal static TypeSpec? SubstituteParentGenericParameter(
        TypeSpec? typeSpec, TypeDecl parentTypeDecl, SwiftTypeName concreteTypeName)
    {
        if (typeSpec == null) return null;
        if (!parentTypeDecl.IsGeneric) return null;

        // Today we only support single-parameter generic parents (the common case:
        // `Wrapper<T>` with `extension Wrapper where T == Concrete`). Multi-parameter
        // parents would need a per-name substitution map and a way to discover which
        // concrete name maps to which open param, which the constrained-extension
        // grouping doesn't preserve. Skip them — caller treats null as "drop this
        // open-generic-return surface for this specialization."
        if (parentTypeDecl.GenericParameters.Count != 1) return null;
        var openParamName = parentTypeDecl.GenericParameters[0].TypeName;
        var concreteSpec = new NamedTypeSpec(concreteTypeName.ModuleQualifiedName);

        return SubstituteSingleNamed(typeSpec, openParamName, concreteSpec);
    }

    private static TypeSpec? SubstituteSingleNamed(
        TypeSpec typeSpec, string openParamName, NamedTypeSpec concreteSpec)
    {
        switch (typeSpec)
        {
            case NamedTypeSpec named:
                if (named.Name == openParamName)
                {
                    // Direct hit — replace whole node. We don't preserve generic args
                    // because the open param itself shouldn't carry generic args
                    // (it's a plain `T`-shaped parameter).
                    return named.GenericParameters.Count == 0 && named.InnerType == null
                        ? concreteSpec
                        : null;
                }
                // Recurse into generic parameters (e.g. `Optional<T>`) and inner types.
                // We only substitute when the result is structurally identical apart
                // from the swapped param; if the recursion hits an unsupported branch
                // (returns null), the whole substitution is unsupported.
                bool changed = false;
                var newGenericArgs = new List<TypeSpec>();
                foreach (var gp in named.GenericParameters)
                {
                    var substituted = SubstituteSingleNamed(gp, openParamName, concreteSpec);
                    if (substituted == null) return null;
                    if (!ReferenceEquals(substituted, gp)) changed = true;
                    newGenericArgs.Add(substituted);
                }
                if (named.InnerType != null) return null; // nested-type substitution unsupported
                if (!changed) return named;
                // Construct a new NamedTypeSpec with the same shape but substituted args.
                // Use the fully-qualified name path so the renderer still produces
                // module-qualified output downstream.
                return new NamedTypeSpec(named.Name, newGenericArgs.ToArray());
            default:
                // Tuples / closures / other shapes are intentionally unsupported for the
                // initial open-generic-return surface — they expand the testing matrix
                // significantly without adding canonical Apple-framework cases.
                return null;
        }
    }

    /// <summary>
    /// Looks up the concrete <see cref="TypeDecl"/> for a same-type constraint's
    /// target within the parent's module so the wrapper's <c>@available</c> can
    /// inherit the concrete type's own OS floor. Returns null when the concrete
    /// type lives in a different module — its annotations stay implicit there
    /// (the importing module's wrapper won't compile if it references a
    /// not-yet-available cross-module type, but that's a separate signal). Walks
    /// the module's type tree (including nested) so per-conformer specializations
    /// like <c>Product.SubscriptionInfo.RenewalInfo</c> resolve correctly.
    /// </summary>
    internal static TypeDecl? FindConcreteTypeDeclInModule(SwiftTypeName concreteTypeName, TypeDecl parentTypeDecl)
    {
        var module = parentTypeDecl.ModuleDecl;
        if (module is null) return null;
        if (!string.Equals(module.Name, concreteTypeName.Module, StringComparison.Ordinal))
            return null;
        return FindNestedTypeDecl(module.Types, concreteTypeName);
    }

    private static TypeDecl? FindNestedTypeDecl(IEnumerable<TypeDecl> types, SwiftTypeName target)
    {
        foreach (var typeDecl in types)
        {
            if (string.Equals(typeDecl.SwiftTypeName.ModuleQualifiedName, target.ModuleQualifiedName, StringComparison.Ordinal))
                return typeDecl;
            var nested = FindNestedTypeDecl(typeDecl.Types, target);
            if (nested != null) return nested;
        }
        return null;
    }

    /// <summary>
    /// Builds the merged wrapper @available annotation list for a constrained-
    /// extension wrapper, accounting for substituted-concrete-type availability.
    /// When the wrapper body references a concrete type whose own
    /// <c>@available</c> floor is stricter than the parent's, the wrapper must
    /// inherit that floor too — otherwise the wrapper's <c>@_cdecl</c> compiles
    /// at the parent's looser floor and references the concrete type before its
    /// OS introduction. Mirrors the dispatcher per-cast availability rule, scoped
    /// to the per-specialization wrapper here.
    ///
    /// Same-module concretes resolve via the in-memory TypeDecl tree
    /// (annotations live on <see cref="TypeDecl.AvailabilityAnnotations"/>);
    /// cross-module concretes fall back to <see cref="ITypeDatabase"/>, which
    /// reads <see cref="TypeRecord.AvailabilityAnnotations"/> persisted in the
    /// dependency module's XML. Without this fallback, a wrapper specialization
    /// that targets a stricter cross-module type would compile at the parent's
    /// floor and crash on older OSes when the concrete type isn't yet available.
    ///
    /// Both lookup paths return ancestor-merged annotations (e.g. nested
    /// <c>Outer.Inner</c> picks up <c>Outer</c>'s OS floor). For the in-module
    /// path that's done via <see cref="WrapperEmitterHelpers.MergeAvailabilityFromAncestors"/>
    /// over the live TypeDecl chain; for the cross-module path the merge happens
    /// at write time in <c>ModuleProcessor</c>, so the persisted TypeRecord
    /// already contains the ancestor-walked list.
    /// </summary>
    private static IReadOnlyList<AvailabilityAnnotation>? MergeWrapperAvailability(
        IReadOnlyList<AvailabilityAnnotation>? memberAnnotations,
        TypeDecl parentTypeDecl,
        SwiftTypeName concreteTypeName,
        ITypeDatabase typeDatabase)
    {
        var merged = WrapperEmitterHelpers.MergeAvailability(memberAnnotations, parentTypeDecl);
        IReadOnlyList<AvailabilityAnnotation>? concreteAnnotations = null;
        var concreteDecl = FindConcreteTypeDeclInModule(concreteTypeName, parentTypeDecl);
        if (concreteDecl != null)
        {
            concreteAnnotations = WrapperEmitterHelpers.MergeAvailabilityFromAncestors(
                memberAnnotations: null, startDecl: concreteDecl);
        }
        else if (typeDatabase.TryGetTypeRecord(concreteTypeName, out var concreteRecord)
            && concreteRecord.AvailabilityAnnotations is { Count: > 0 } recordAnnotations)
        {
            concreteAnnotations = recordAnnotations;
        }

        if (concreteAnnotations is { Count: > 0 })
        {
            var combined = new List<AvailabilityAnnotation>(concreteAnnotations);
            if (merged is { Count: > 0 })
                combined.AddRange(merged);
            return combined;
        }
        return merged;
    }

    /// <summary>
    /// Returns properties whose return type spec contains a reference to the
    /// parent's open generic parameter, AND that do not themselves carry a
    /// same-type constraint (so they live on the unconstrained base extension,
    /// e.g. `extension VerificationResult { var payloadValue: SignedType { get } }`).
    /// Each concrete specialization re-emits these with the open generic
    /// parameter substituted by the concrete type. Static accessors are excluded
    /// because the current emit shape passes `self.Payload.DangerousGetHandle()`
    /// — there is no `self` for static access.
    /// </summary>
    internal static List<PropertyDecl> FindOpenGenericReturnProperties(TypeDecl typeDecl)
    {
        var result = new List<PropertyDecl>();
        if (!typeDecl.IsGeneric) return result;

        var parentParamNames = new HashSet<string>(
            typeDecl.GenericParameters.Select(p => p.TypeName));

        foreach (var property in typeDecl.Properties)
        {
            if (property.IsStatic) continue;
            if (ExtractSameTypeConstraint(property) != null) continue;
            if (property.SwiftTypeSpec is null) continue;
            if (TypeSpecHelpers.ContainsAnyTypeName(property.SwiftTypeSpec, parentParamNames))
                result.Add(property);
        }

        return result;
    }

    /// <summary>
    /// Single source of truth for whether a same-type-constrained method on a
    /// generic parent is in the subset this emitter actually re-surfaces as a
    /// closed-generic extension method. Mirrors the filter in
    /// <see cref="FindConstrainedMethodSpecializations"/> + the parameter-count
    /// gate in <see cref="TryEmitMethodExtension"/>: zero-argument, sync,
    /// non-throwing, non-accessor, non-subscript, non-constructor, public
    /// methods only. <see cref="MemberValidationPipeline"/> consults this so it
    /// only marks the supported subset as <c>RoutedElsewhere</c>; methods that
    /// fall outside still fall through to the normal validation path and surface
    /// a proper skip reason instead of disappearing silently.
    /// </summary>
    public static bool IsEmittableConstrainedExtensionMethod(MethodDecl method)
    {
        if (method.IsConstructor) return false;
        if (method.IsAccessor) return false;
        if (method.IsSubscriptAccessor) return false;
        if (method.IsAsync || method.Throws) return false;
        if (method.IsSynthesizedAccessor) return false;
        // CSSignature[0] is the return slot; anything beyond it means the method
        // takes parameters, which the initial method-extension scope does not
        // marshal. Tracked as a follow-up under the same Fix J doc.
        if (method.CSSignature.Count > 1) return false;
        return true;
    }

    /// <summary>
    /// Extracts the concrete same-type constraint from a method's GenericParameters,
    /// parallel to <see cref="ExtractSameTypeConstraint(PropertyDecl)"/>. The
    /// constraint sits directly on <c>methodDecl.GenericParameters</c> rather
    /// than on a getter accessor's method.
    /// </summary>
    internal static SwiftTypeName? ExtractSameTypeConstraintForMethod(MethodDecl method)
    {
        foreach (var genericParam in method.GenericParameters)
        {
            foreach (var conformance in genericParam.GenericConformances)
            {
                if (conformance.Kind == ConformanceKind.ConcreteType &&
                    IsSpecializableConcretePin(conformance.ConformanceTarget))
                    return conformance.ConformanceTarget;
            }
        }
        return null;
    }

    /// <summary>
    /// Finds constrained-extension method groups: methods whose own GenericParameters
    /// carry a same-type equality constraint (e.g. `extension Wrapper where T == Concrete`).
    /// Methods are grouped by (Name, IsStatic, parameter type signature) so overloads on
    /// distinct signatures don't get conflated. As with properties, mixed
    /// constrained + unconstrained groups are rejected to avoid namespace collision
    /// with any open-generic emission of the same overload.
    /// </summary>
    internal static Dictionary<SwiftTypeName, List<MethodDecl>> FindConstrainedMethodSpecializations(TypeDecl typeDecl)
    {
        var result = new Dictionary<SwiftTypeName, List<MethodDecl>>();
        if (!typeDecl.IsGeneric) return result;

        var groups = new Dictionary<(string Name, bool IsStatic, string ParamSig), List<MethodDecl>>();
        foreach (var method in typeDecl.Methods)
        {
            // Single source of truth for the supported subset (see
            // IsEmittableConstrainedExtensionMethod). Out-of-scope variants
            // (constructors, accessors, async/throws, parametered, non-public)
            // still drop here, but MemberValidationPipeline's gate consults the
            // same predicate so they surface a proper skip reason instead of
            // being silently absorbed by RoutedElsewhere.
            if (!IsEmittableConstrainedExtensionMethod(method)) continue;

            var paramSig = BuildMethodParameterSignature(method);
            var key = (method.Name, method.MethodType == MethodType.Static, paramSig);
            if (!groups.ContainsKey(key))
                groups[key] = new List<MethodDecl>();
            groups[key].Add(method);
        }

        foreach (var (_, siblings) in groups)
        {
            bool allConstrained = true;
            foreach (var sibling in siblings)
            {
                if (ExtractSameTypeConstraintForMethod(sibling) == null)
                {
                    allConstrained = false;
                    break;
                }
            }
            if (!allConstrained) continue;

            foreach (var sibling in siblings)
            {
                var concreteType = ExtractSameTypeConstraintForMethod(sibling)!;
                if (!result.ContainsKey(concreteType))
                    result[concreteType] = new List<MethodDecl>();
                result[concreteType].Add(sibling);
            }
        }

        return result;
    }

    /// <summary>
    /// Builds a stable parameter-signature key from a method's CSSignature parameters
    /// (skipping the return slot at index 0). Used to dedup overloads in
    /// <see cref="FindConstrainedMethodSpecializations"/>.
    /// </summary>
    private static string BuildMethodParameterSignature(MethodDecl method)
    {
        if (method.CSSignature.Count <= 1) return "";
        var parts = new List<string>();
        foreach (var arg in method.CSSignature.Skip(1))
        {
            var rendered = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(arg.SwiftTypeSpec);
            parts.Add($"{arg.Name}:{rendered}");
        }
        return string.Join("|", parts);
    }

    /// <summary>
    /// Emits one constrained-extension method for a concrete specialization.
    /// Initial scope: zero-argument sync, non-throwing, non-mutating methods —
    /// covers the canonical Apple-framework cases (`WeatherKit.*Query` static
    /// factories and `MusicLibraryRequest&lt;T&gt;` no-arg accessors). Methods
    /// with parameters, async/throws variants, mutating, and closure shapes are
    /// intentionally skipped here and tracked separately; the doc calls those
    /// out as "structured result types and closure parameters" follow-ups.
    /// </summary>
    private static bool TryEmitMethodExtension(
        CSharpWriter csWriter,
        SwiftWriter swiftWriter,
        MethodDecl method,
        TypeDecl parentTypeDecl,
        SwiftTypeName concreteTypeName,
        string closedGenericCsType,
        string moduleName,
        string wrapperLibPath,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext emissionContext,
        List<Action> pinvokeDeclarations,
        ILogger logger)
    {
        if (method.CSSignature.Count > 1)
        {
            // Out of scope for the first method-extension pass; closure / complex
            // parameter marshalling needs the full param-projection pipeline.
            logger.LogDebug(
                "ConstrainedExtensionEmitter: Skipping method {Name} — non-zero-arg methods not yet supported.",
                method.Name);
            return false;
        }

        // CSSignature[0] is the return slot.
        var returnTypeSpec = method.CSSignature.Count > 0 ? method.CSSignature[0].SwiftTypeSpec : null;

        // Substitute the parent's open generic param if the return spec references
        // it. The supported shape is bare-T substitution — `func payloadValue() -> T`
        // becomes `Concrete` after substitution, and `ClassifyReturnType` accepts
        // a fully-resolved NamedTypeSpec. Bound-generic returns like
        // `func temperature() -> Query<T>` substitute to `Query<Concrete>`, which
        // still has `GenericParameters.Count > 0`; `ClassifyReturnType` rejects
        // those today and the method drops via the `classification == null`
        // branch below. The bound-generic-return shape is tracked as a follow-up
        // under the same Fix J doc rather than being silently mis-emitted here.
        TypeSpec? effectiveReturnTypeSpec = returnTypeSpec;
        if (returnTypeSpec != null && parentTypeDecl.IsGeneric)
        {
            var parentParamNames = new HashSet<string>(
                parentTypeDecl.GenericParameters.Select(p => p.TypeName));
            if (TypeSpecHelpers.ContainsAnyTypeName(returnTypeSpec, parentParamNames))
            {
                var substituted = SubstituteParentGenericParameter(returnTypeSpec, parentTypeDecl, concreteTypeName);
                if (substituted == null)
                {
                    UnsupportedCommentEmitter.EmitMemberSkipped(
                        csWriter, method.Name, BindingItemKind.Method,
                        SkipReason.UnsupportedSignature,
                        $"on {parentTypeDecl.Name}<{concreteTypeName.Name}>: open-generic return substitution unsupported",
                        containingDecl: method.ParentDecl);
                    logger.LogDebug(
                        "ConstrainedExtensionEmitter: Skipping method {Name} on {Parent}<{Concrete}> — open-generic return substitution unsupported.",
                        method.Name, parentTypeDecl.Name, concreteTypeName.Name);
                    return false;
                }
                effectiveReturnTypeSpec = substituted;
            }
        }

        // Bound-generic returns (Query<Concrete>) survive substitution but
        // `ClassifyReturnType` rejects any spec with remaining generic
        // parameters. Drop with an explicit diagnostic so the unsupported shape
        // is visible rather than swallowed by the generic "unsupported return
        // shape" branch below.
        if (effectiveReturnTypeSpec is NamedTypeSpec substitutedNamed && substitutedNamed.ContainsGenericParameters)
        {
            UnsupportedCommentEmitter.EmitMemberSkipped(
                csWriter, method.Name, BindingItemKind.Method,
                SkipReason.UnsupportedSignature,
                $"on {parentTypeDecl.Name}<{concreteTypeName.Name}>: bound-generic return ({substitutedNamed.Name}) not yet supported",
                containingDecl: method.ParentDecl);
            logger.LogDebug(
                "ConstrainedExtensionEmitter: Skipping method {Name} on {Parent}<{Concrete}> — bound-generic return ({ReturnType}) is not yet supported.",
                method.Name, parentTypeDecl.Name, concreteTypeName.Name, substitutedNamed.Name);
            return false;
        }

        var classification = ClassifyCEReturnShape(effectiveReturnTypeSpec, typeDatabase);
        // Allow Void returns for methods (rare, but legal) — represented by a null
        // spec that ClassifyCEReturnShape rejects. Promote that null to a synthetic
        // Void shape on the method side only.
        bool isVoidReturn = effectiveReturnTypeSpec == null
            || (effectiveReturnTypeSpec is TupleTypeSpec t && t.IsEmptyTuple);
        if (classification == null && !isVoidReturn)
        {
            UnsupportedCommentEmitter.EmitMemberSkipped(
                csWriter, method.Name, BindingItemKind.Method,
                SkipReason.UnsupportedSignature,
                $"on {parentTypeDecl.Name}<{concreteTypeName.Name}>: unsupported return type",
                containingDecl: method.ParentDecl);
            logger.LogDebug(
                "ConstrainedExtensionEmitter: Skipping method {Name} on {Parent}<{Concrete}> — unsupported return type {ReturnType}.",
                method.Name, parentTypeDecl.Name, concreteTypeName.Name, effectiveReturnTypeSpec);
            return false;
        }

        var (shape, csharpReturnType) = isVoidReturn
            ? (CEReturnShape.Primitive, "void")
            : classification!.Value;

        bool isStatic = method.MethodType == MethodType.Static;
        var methodPublicName = NameProvider.ToPascalCase(method.Name);

        // Symbol naming uses a SBW_CEMethod_ prefix to keep methods distinguishable
        // from properties (`SBW_CEGet_*`). The shape parallels the property one so
        // the renderer / wrapper-symbol dedup can apply uniformly. Both the parent
        // slot and the concretization slot are rendered through the injective
        // `SanitizeForSymbol(ModuleQualifiedName)` encoding — see the property-path
        // comment for the full rationale. The static/instance marker is part of the
        // SBW symbol because Swift permits `func rank()` and `static func rank()`
        // on the same type with the same identifier; without the marker, both
        // declarations would render to the same SBW string and the second
        // `_registeredWrapperSymbols.Add` would silently drop one wrapper even
        // though the structural tuple distinguishes them.
        var safeParentName = SanitizeParent(parentTypeDecl);
        var safeConcreteName = SanitizeConcretization(concreteTypeName);
        var staticMarker = isStatic ? "_static" : "_instance";
        var symbolName = $"SBW_CEMethod_{safeParentName}_{safeConcreteName}_{method.Name}{staticMarker}";

        // Dedup guard via the structural-identity claim API. S5: routing through
        // TryClaimWrapperSymbol with a sourceKey that anchors to the constrained-extension
        // method identity (parent type + concretization + static/instance marker + method)
        // locks the bug class closed for any future overlap with adjacent method-wrapper
        // paths on the same closed generic shape. The structural tuple uses the parent's
        // and concrete type's raw `SwiftTypeName.ModuleQualifiedName` (not the leaf
        // `Name`) — see the property-path comment for the full rationale. There is no
        // MethodWrapperEmitter counterpart to align with — MWE walks the open generic
        // parent, never the closed generic concretizations this emitter synthesizes — so
        // the structural identity is intentionally intra-emitter (constrained-extension →
        // constrained-extension only).
        var qualifiedParent = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;
        var qualifiedConcrete = concreteTypeName.ModuleQualifiedName;
        var dispatchMarker = isStatic ? "static" : "instance";
        if (!emissionContext.TryClaimWrapperSymbol(
                $"{qualifiedParent}<{qualifiedConcrete}>",
                $"{method.Name}::{dispatchMarker}",
                $"constrained-extension::method::{qualifiedParent}::{qualifiedConcrete}::{dispatchMarker}::{method.Name}",
                symbolName,
                DeclIdFactory.ForMethod(method)))
            return false;

        // ----- C# extension method -----
        csWriter.WriteLine();
        if (isStatic)
        {
            csWriter.WriteLine($"public static {csharpReturnType} {methodPublicName}()");
        }
        else
        {
            csWriter.WriteLine($"public static {csharpReturnType} {methodPublicName}(this {closedGenericCsType} self)");
        }
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Build the call arguments expression — instance methods pass the self
        // handle as the trailing arg; static methods pass nothing.
        var pinvokeSelfArg = isStatic ? "" : "self.Payload.DangerousGetHandle()";

        switch (shape)
        {
            case CEReturnShape.Primitive when isVoidReturn:
                if (isStatic)
                    csWriter.WriteLine($"NativeMethods.{symbolName}();");
                else
                    csWriter.WriteLine($"NativeMethods.{symbolName}({pinvokeSelfArg});");
                break;

            case CEReturnShape.String:
                // stackalloc the 2-word (SBW_Utf8Slice) scratch buffer; ReadUtf8Slice copies the
                // bytes out before return, so the buffer never escapes the frame. The former
                // NativeMemory.Alloc/Free only reclaimed this 16-byte container; stackalloc
                // reclaims it identically on frame exit (including the exceptional path).
                csWriter.WriteLine("unsafe");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine("byte* _cdeclBuf = stackalloc byte[nint.Size * 2];");
                csWriter.WriteLine("var resultPtr = (IntPtr)_cdeclBuf;");
                if (isStatic)
                    csWriter.WriteLine($"NativeMethods.{symbolName}(resultPtr);");
                else
                    csWriter.WriteLine($"NativeMethods.{symbolName}(resultPtr, {pinvokeSelfArg});");
                csWriter.WriteLine("return SwiftMarshal.ReadUtf8Slice(resultPtr);");
                csWriter.Indent--;
                csWriter.WriteLine("}");
                break;

            case CEReturnShape.Primitive:
                if (isStatic)
                    csWriter.WriteLine($"return NativeMethods.{symbolName}();");
                else
                    csWriter.WriteLine($"return NativeMethods.{symbolName}({pinvokeSelfArg});");
                break;

            case CEReturnShape.NonFrozenStruct:
                {
                    var selfArg = isStatic ? "" : ", " + pinvokeSelfArg;
                    var pinvokeArg = isStatic ? "indirectResult" : $"indirectResult{selfArg}";
                    csWriter.WriteLines($$"""
                        unsafe
                        {
                            var metadata = SwiftObjectHelper<{{csharpReturnType}}>.GetTypeMetadata();
                            IntPtr buffer = (IntPtr)System.Runtime.InteropServices.NativeMemory.Alloc(metadata.Size);
                            try
                            {
                                var indirectResult = new SwiftIndirectResult((void*)buffer);
                                NativeMethods.{{symbolName}}({{pinvokeArg}});
                                return SwiftMarshal.MarshalFromSwift<{{csharpReturnType}}>(buffer);
                            }
                            catch
                            {
                                System.Runtime.InteropServices.NativeMemory.Free((void*)buffer);
                                throw;
                            }
                        }
                        """);
                }
                break;

            case CEReturnShape.FoundationDate:
                if (isStatic)
                    csWriter.WriteLine($"var seconds = NativeMethods.{symbolName}();");
                else
                    csWriter.WriteLine($"var seconds = NativeMethods.{symbolName}({pinvokeSelfArg});");
                csWriter.WriteLine($"return {DateProjection.SwiftEpoch}.AddSeconds(seconds);");
                break;

            case CEReturnShape.FoundationUUID:
                {
                    var selfArg = isStatic ? "" : ", " + pinvokeSelfArg;
                    var pinvokeArg = isStatic ? "indirectResult" : $"indirectResult{selfArg}";
                    // stackalloc a 16-byte indirect-result buffer; the Guid is value-copied out
                    // before the frame exits (Guid is frozen, no ARC/ownership in the buffer), so
                    // the former Alloc/Free container is replaced by an equivalent stack reclaim.
                    csWriter.WriteLines($$"""
                        unsafe
                        {
                            byte* buffer = stackalloc byte[16];
                            var indirectResult = new SwiftIndirectResult((void*)buffer);
                            NativeMethods.{{symbolName}}({{pinvokeArg}});
                            return *(System.Guid*)buffer;
                        }
                        """);
                }
                break;

            case CEReturnShape.FoundationData:
                {
                    var selfArg = isStatic ? "" : ", " + pinvokeSelfArg;
                    var pinvokeArg = isStatic ? "indirectResult" : $"indirectResult{selfArg}";
                    // stackalloc the 16-byte container; the Data value is copied out and
                    // ToByteArray() copies the underlying bytes via Swift's CopyBytes P/Invoke
                    // before return (the copied-out value carries the _object pointer
                    // independently), so the free only ever reclaimed the container — stackalloc
                    // reclaims it identically on frame exit.
                    csWriter.WriteLines($$"""
                        unsafe
                        {
                            byte* buffer = stackalloc byte[16];
                            var indirectResult = new SwiftIndirectResult((void*)buffer);
                            NativeMethods.{{symbolName}}({{pinvokeArg}});
                            return (*(Swift.Foundation.Data*)(void*)buffer).ToByteArray();
                        }
                        """);
                }
                break;
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");

        // ----- Swift @_cdecl wrapper -----
        EmitSwiftMethodWrapper(swiftWriter, method, parentTypeDecl, concreteTypeName,
            symbolName, moduleName, shape, isStatic, isVoidReturn, effectiveReturnTypeSpec, emissionContext, typeDatabase);

        // ----- Queue P/Invoke declaration -----
        var capturedSymbol = symbolName;
        var capturedShape = shape;
        var capturedReturnType = csharpReturnType;
        var capturedIsStatic = isStatic;
        var capturedIsVoidReturn = isVoidReturn;
        pinvokeDeclarations.Add(() =>
        {
            var pinvokeParams = new List<string>();
            string pinvokeReturnType;

            switch (capturedShape)
            {
                case CEReturnShape.Primitive when capturedIsVoidReturn:
                    if (!capturedIsStatic) pinvokeParams.Add("IntPtr _self");
                    pinvokeReturnType = "void";
                    break;
                case CEReturnShape.String:
                    pinvokeParams.Add("IntPtr resultPtr");
                    if (!capturedIsStatic) pinvokeParams.Add("IntPtr _self");
                    pinvokeReturnType = "void";
                    break;
                case CEReturnShape.NonFrozenStruct:
                case CEReturnShape.FoundationUUID:
                case CEReturnShape.FoundationData:
                    pinvokeParams.Add("SwiftIndirectResult indirectResult");
                    if (!capturedIsStatic) pinvokeParams.Add("IntPtr _self");
                    pinvokeReturnType = "void";
                    break;
                case CEReturnShape.FoundationDate:
                    if (!capturedIsStatic) pinvokeParams.Add("IntPtr _self");
                    pinvokeReturnType = "double";
                    break;
                default: // Primitive (non-void)
                    if (!capturedIsStatic) pinvokeParams.Add("IntPtr _self");
                    pinvokeReturnType = capturedReturnType;
                    break;
            }

            PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
            {
                LibraryPath = wrapperLibPath,
                EntryPoint = capturedSymbol,
                MethodName = capturedSymbol,
                ReturnType = pinvokeReturnType,
                ParametersString = string.Join(", ", pinvokeParams),
                CallingConvention = PInvokeCallingConvention.Cdecl,
                Visibility = PInvokeVisibility.Internal
            });
            csWriter.WriteLine();
        });

        return true;
    }

    private static void EmitSwiftMethodWrapper(
        SwiftWriter swiftWriter,
        MethodDecl method,
        TypeDecl parentTypeDecl,
        SwiftTypeName concreteTypeName,
        string symbolName,
        string moduleName,
        CEReturnShape shape,
        bool isStatic,
        bool isVoidReturn,
        TypeSpec? effectiveReturnTypeSpec,
        ModuleEmissionContext emissionContext,
        ITypeDatabase typeDatabase)
    {
        var parentSwiftName = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;
        var concreteSwiftName = concreteTypeName.ModuleQualifiedName;
        var closedGenericSwiftType = $"{parentSwiftName}<{concreteSwiftName}>";

        if (shape == CEReturnShape.String)
        {
            Utf8SliceEmitter.EmitIfNeeded(swiftWriter, emissionContext);
            Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter, moduleName, emissionContext);
        }

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Constrained-extension method for {{closedGenericSwiftType}}.{{method.Name}}.
            // Concrete specialization — no generic dispatch needed.
            """);

        // Parameter list mirrors the property side: indirect-result shapes pass the
        // result buffer first, then `self_` (instance only). Static methods omit
        // `self_`. Method parameters are not yet supported here — the caller
        // already gated on CSSignature.Count <= 1.
        var swiftParams = new List<string>();
        bool usesIndirectResult = shape == CEReturnShape.String
            || shape == CEReturnShape.NonFrozenStruct
            || shape == CEReturnShape.FoundationUUID
            || shape == CEReturnShape.FoundationData;
        if (usesIndirectResult)
            swiftParams.Add("_ resultPtr: UnsafeMutableRawPointer");
        if (!isStatic)
            swiftParams.Add("_ self_: UnsafeRawPointer");

        var returnClause = (shape, isVoidReturn) switch
        {
            (_, true) => "",
            (CEReturnShape.Primitive, _) => $" -> {ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(effectiveReturnTypeSpec!)}",
            (CEReturnShape.FoundationDate, _) => " -> Double",
            _ => "",
        };
        var swiftParamString = string.Join(", ", swiftParams);

        var hash = EmitterUtility.DeterministicHash8(symbolName);
        var swiftFuncName = $"_sbw_cemethod_{method.Name}_{hash}";

        // Method body calls through the closed-generic instantiation
        // (`Wrapper<Concrete>.method()`), so the wrapper's @available inherits
        // the concrete type's annotations alongside the parent's — same shape
        // as the property side. See MergeWrapperAvailability for rationale.
        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, symbolName, needsMainActor: false,
            MergeWrapperAvailability(method.AvailabilityAnnotations, parentTypeDecl, concreteTypeName, typeDatabase));
        swiftWriter.WriteLine($"public func {swiftFuncName}({swiftParamString}){returnClause} {{");
        swiftWriter.Indent++;

        // For instance methods, materialize self from the inbound pointer the same
        // way the property emitter does. Static methods skip this step.
        if (!isStatic)
        {
            if (parentTypeDecl is ClassDecl)
                swiftWriter.WriteLine($"let obj = Unmanaged<{closedGenericSwiftType}>.fromOpaque(self_).takeUnretainedValue()");
            else
                swiftWriter.WriteLine($"let obj = self_.assumingMemoryBound(to: {closedGenericSwiftType}.self).pointee");
        }

        var callExpression = isStatic
            ? $"{closedGenericSwiftType}.{method.Name}()"
            : $"obj.{method.Name}()";

        if (isVoidReturn)
        {
            swiftWriter.WriteLine($"_ = {callExpression}");
        }
        else
        {
            switch (shape)
            {
                case CEReturnShape.String:
                    StringReturnEmitter.EmitGetterBody(swiftWriter, callExpression);
                    break;
                case CEReturnShape.Primitive:
                    swiftWriter.WriteLine($"return {callExpression}");
                    break;
                case CEReturnShape.NonFrozenStruct:
                    swiftWriter.WriteLine($"let result = {callExpression}");
                    swiftWriter.WriteLine($"resultPtr.initializeMemory(as: {ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(effectiveReturnTypeSpec!)}.self, repeating: result, count: 1)");
                    break;
                case CEReturnShape.FoundationDate:
                    swiftWriter.WriteLine($"return {callExpression}.timeIntervalSinceReferenceDate");
                    break;
                case CEReturnShape.FoundationUUID:
                    swiftWriter.WriteLine($"let result = {callExpression}");
                    swiftWriter.WriteLine("resultPtr.initializeMemory(as: Foundation.UUID.self, repeating: result, count: 1)");
                    break;
                case CEReturnShape.FoundationData:
                    swiftWriter.WriteLine($"let result = {callExpression}");
                    swiftWriter.WriteLine("resultPtr.initializeMemory(as: Foundation.Data.self, repeating: result, count: 1)");
                    break;
            }
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    private static string? ResolveCSharpName(SwiftTypeName swiftTypeName, ITypeDatabase typeDatabase)
    {
        if (typeDatabase.TryGetTypeRecord(swiftTypeName, out var record))
            return record.CSharpTypeName.FullyQualifiedName;

        // Fallback: use simple name (the type might be in the same module)
        return swiftTypeName.Name;
    }

    // Render the concretization slot for both the C# extension class name and the
    // SBW_CE{Get,Method}_ symbol. Uses the injective `SanitizeForSymbol` over
    // `ModuleQualifiedName` so two distinct Swift identities (e.g.
    // `Mod.Outer.User` vs `Mod.Outer_User`, or `Mod.User` vs `Mod.Outer.User`)
    // never render the same SBW_ string. The structural-identity tuple already
    // distinguishes them via the raw `ModuleQualifiedName`, but
    // `_registeredWrapperSymbols.Add(symbol)` rejects collisions independently
    // and would silently drop one wrapper.
    private static string SanitizeConcretization(SwiftTypeName concreteTypeName)
    {
        return SanitizeForSymbol(concreteTypeName.ModuleQualifiedName);
    }

    // Render the parent type's slot for both the C# extension class name and the
    // SBW_CE{Get,Method}_ symbol. Uses the injective encoding over the parent's
    // `SwiftTypeName.ModuleQualifiedName` so two nested generic parents that
    // share a leaf name (e.g. `Mod.Box` and `Mod.Outer.Box`) cannot collide.
    private static string SanitizeParent(TypeDecl parentTypeDecl)
    {
        return SanitizeForSymbol(parentTypeDecl.SwiftTypeName.ModuleQualifiedName);
    }

    // Injective Swift-qualified-name → C-identifier encoding. Each special char
    // becomes a 2-char `_<marker>` sequence with a unique second char per source
    // char, so distinct Swift inputs always render to distinct strings. Earlier
    // sequential-Replace schemes (e.g. `_` → `__` then `.` → `_`) were
    // non-injective at separator/underscore boundaries because a literal `_`
    // adjacent to a `.` would render the same as the swap (`Mod.A_.B` and
    // `Mod.A._B` both collapsed to `Mod_A___B`). The 2-char marker scheme is
    // injective by construction: every `_` in the output is followed by exactly
    // one of the marker letters, and the encoder produces one marker per source
    // special char, so the encoding is uniquely length-decomposable. Used only
    // on the SBW_ symbol / className paths where the rendered string itself is
    // the dedup key in `_registeredWrapperSymbols`.
    private static string SanitizeForSymbol(string swiftQualifiedName)
    {
        var sb = new System.Text.StringBuilder(swiftQualifiedName.Length);
        foreach (var c in swiftQualifiedName)
        {
            switch (c)
            {
                case '_': sb.Append("__"); break;
                case '.': sb.Append("_D"); break;
                case '<': sb.Append("_L"); break;
                case '>': sb.Append("_R"); break;
                case '[': sb.Append("_B"); break;
                case ']': sb.Append("_E"); break;
                case '(': sb.Append("_P"); break;
                case ')': sb.Append("_Q"); break;
                case ',': sb.Append("_C"); break;
                case ' ': sb.Append("_S"); break;
                case '?': sb.Append("_O"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static string SanitizeTypeName(string name)
    {
        // Strip every Swift type-syntax character that would produce an invalid C#
        // identifier. Array `[T]`, generic `T<U>`, tuple `(A, B)` and qualified
        // `Module.T` all need to map to bare-word identifiers; otherwise the emitted
        // class name (e.g. `ModuleExtensionType]Extensions`) is a
        // syntax error. Order matters: drop the closer (`>`, `]`, `)`) so the prefix
        // collapses cleanly, replace the opener / separator with underscores so a
        // composed name (`Dictionary_String_Int`) stays readable.
        return name
            .Replace(".", "_")
            .Replace("<", "_")
            .Replace(">", "")
            .Replace("[", "_")
            .Replace("]", "")
            .Replace("(", "_")
            .Replace(")", "")
            .Replace(",", "_")
            .Replace(" ", "")
            .Replace("?", "_Optional");
    }

    private static bool IsCSharpPrimitiveType(string typeName) => typeName switch
    {
        "int" or "uint" or "long" or "ulong" or "short" or "ushort" or
        "byte" or "sbyte" or "float" or "double" or "bool" or "char" or
        "nint" or "nuint" or "decimal" or "string" => true,
        _ => false
    };

    /// <summary>
    /// Tests whether a TypeSpec is the named Foundation value type (e.g. "Foundation.Data").
    /// Mirrors the bare-name predicate used elsewhere in the emitter (e.g. WrapperEmitter.Return.cs).
    /// </summary>
    private static bool IsFoundationType(TypeSpec? typeSpec, string moduleQualifiedName)
    {
        return typeSpec is NamedTypeSpec named && named.Name == moduleQualifiedName;
    }
}
