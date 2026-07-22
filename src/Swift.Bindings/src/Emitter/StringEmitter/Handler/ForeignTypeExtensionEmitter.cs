// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using static BindingsGeneration.ExtensionMarshallingHelper;

namespace BindingsGeneration;

/// <summary>
/// Emits C# static extension classes for Swift extensions on foreign types
/// (types not defined in the current module, e.g., UIKit.UIView).
///
/// Unlike ProtocolExtensionEmitter (which injects methods onto existing ClassDecl),
/// foreign types have no ClassDecl in the current module. This emitter generates:
/// 1. @_silgen_name Swift wrappers (same pattern as ProtocolExtensionEmitter)
/// 2. C# static extension classes with proper marshalling for each return type
/// </summary>
public static class ForeignTypeExtensionEmitter
{
    // Note: ForeignExtensionClassInfo and ForeignExtensionMemberInfo types moved to ModuleEmissionContext.cs.

    /// <summary>
    /// Processes foreign type extensions: applies gates, generates Swift wrappers,
    /// and collects C# extension class info for later emission.
    /// </summary>
    /// <param name="availabilityAnnotations">
    /// Optional swiftinterface-derived map of fully-qualified member keys to
    /// availability annotations. The walker keys extension members as
    /// <c>{strippedExtendedType}.{printedName}</c> where the leading module
    /// dot-component of the extended type is dropped (e.g.,
    /// <c>extension CoreLocation.CLPlacemark</c> → key prefix <c>CLPlacemark</c>).
    /// We mirror that exactly so the emitted <c>@_silgen_name</c> wrapper
    /// carries the same <c>@available</c> floor the source declared (and the
    /// extension scope inherited). Without this, wrappers referencing iOS-N
    /// foreign-extension members fail to compile on device SDKs with
    /// stricter availability checking.
    /// </param>
    public static void ProcessForeignTypeExtensions(
        ModuleDecl moduleDecl,
        Dictionary<string, List<ProtocolExtensionMethodDecl>> foreignExtensions,
        ITypeDatabase typeDatabase,
        ILogger logger,
        ModuleEmissionContext? ctx = null,
        IReadOnlyDictionary<string, List<AvailabilityAnnotation>>? availabilityAnnotations = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        if (foreignExtensions.Count == 0)
            return;

        foreach (var (foreignTypeQualifiedName, members) in foreignExtensions)
        {
            // Gate: foreign type must be an ObjC class (not a primitive, struct, or protocol)
            if (!IsForeignObjCClassType(foreignTypeQualifiedName))
            {
                logger.LogDebug("Skipping foreign extension on non-ObjC type: {Type}", foreignTypeQualifiedName);
                continue;
            }

            // Track the foreign module for Swift imports
            var dotIdx = foreignTypeQualifiedName.IndexOf('.');
            if (dotIdx > 0)
            {
                ctx.AddForeignExtNeededImport(foreignTypeQualifiedName.Substring(0, dotIdx));
            }

            // Strip the leading module dot to match the AvailabilityWalker scope key.
            var availabilityTypeKey = dotIdx > 0
                ? foreignTypeQualifiedName.Substring(dotIdx + 1)
                : foreignTypeQualifiedName;

            foreach (var extMethod in members)
            {
                var memberAvailability = LookupMemberAvailability(
                    availabilityAnnotations, availabilityTypeKey, extMethod);
                TryProcessMember(moduleDecl, foreignTypeQualifiedName, extMethod,
                    typeDatabase, logger, ctx, memberAvailability);
            }
        }

        if (ctx.ForeignExtEmittedCount > 0)
        {
            logger.LogInformation("Emitted {Count} foreign type extension members", ctx.ForeignExtEmittedCount);
        }
    }

    /// <summary>
    /// Emits accumulated Swift wrapper functions to the SwiftWriter.
    /// Called from ModuleHandler.Emit() after all types have been processed.
    /// </summary>
    public static void EmitSwiftWrappers(SwiftWriter swiftWriter, ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        if (ctx.ForeignExtSwiftWrapperLines.Count == 0)
            return;

        // Emit any additional imports needed for foreign type modules
        foreach (var import in ctx.ForeignExtNeededImports.OrderBy(s => s))
        {
            swiftWriter.WriteLine($"import {import}");
        }

        swiftWriter.WriteLine();
        swiftWriter.WriteLine("// --- Foreign type extension method wrappers ---");
        foreach (var line in ctx.ForeignExtSwiftWrapperLines)
        {
            swiftWriter.WriteLine(line);
        }
    }

    /// <summary>
    /// Emits C# static extension classes for all processed foreign types.
    /// Called from ModuleHandler.Emit() after types have been emitted.
    /// </summary>
    public static void EmitCSharpExtensionClasses(CSharpWriter csWriter, ITypeDatabase typeDatabase, string moduleName, ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;
        if (ctx.ForeignExtClasses.Count == 0)
            return;

        // Ordinal for reproducible emission order independent of the host culture.
        foreach (var (foreignTypeQualifiedName, classInfo) in ctx.ForeignExtClasses.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            EmitExtensionClass(csWriter, classInfo, typeDatabase, moduleName);
        }
    }

    /// <summary>
    /// Attempts to process a single foreign extension member. Applies gates, generates
    /// Swift wrapper, and collects C# member info.
    /// </summary>
    private static void TryProcessMember(
        ModuleDecl moduleDecl,
        string foreignTypeQualifiedName,
        ProtocolExtensionMethodDecl extMethod,
        ITypeDatabase typeDatabase,
        ILogger logger,
        ModuleEmissionContext ctx,
        IReadOnlyList<AvailabilityAnnotation>? availabilityAnnotations = null)
    {
        // Gate: skip constrained extensions
        if (extMethod.WhereConstraints.Count > 0)
            return;

        // Gate: skip deprecated members
        if (extMethod.IsDeprecated)
            return;

        // Gate: skip static members (deferred)
        if (extMethod.IsStatic)
            return;

        // Gate: skip async methods
        if (!extMethod.IsProperty && ProtocolExtensionEmitter.IsAsyncSignature(extMethod.RawSignature))
            return;

        // Gate: skip throwing methods
        if (!extMethod.IsProperty && ProtocolExtensionEmitter.IsThrowingSignature(extMethod.RawSignature))
            return;

        if (extMethod.IsProperty)
        {
            TryProcessProperty(moduleDecl, foreignTypeQualifiedName, extMethod, typeDatabase, logger, ctx, availabilityAnnotations);
        }
        else
        {
            TryProcessMethod(moduleDecl, foreignTypeQualifiedName, extMethod, typeDatabase, logger, ctx, availabilityAnnotations);
        }
    }

    /// <summary>
    /// Looks up the availability annotations the swiftinterface walker stored for
    /// this extension member. The walker keys members under
    /// <c>{strippedExtendedType}.{printedName}</c> (with the leading module dot
    /// dropped on the extended type) and includes any enclosing extension-scope
    /// <c>@available</c> floor. For overloaded methods the walker also stores a
    /// disambiguated <c>{bareKey}|{paramSig}</c> entry; we try that first when a
    /// method signature is available so each overload sees its own floor.
    /// </summary>
    private static IReadOnlyList<AvailabilityAnnotation>? LookupMemberAvailability(
        IReadOnlyDictionary<string, List<AvailabilityAnnotation>>? availabilityAnnotations,
        string strippedExtendedType,
        ProtocolExtensionMethodDecl extMethod)
    {
        if (availabilityAnnotations is null || availabilityAnnotations.Count == 0)
            return null;

        var bareKey = $"{strippedExtendedType}.{extMethod.PrintedName}";
        if (!extMethod.IsProperty)
        {
            var paramSig = TryComputeMethodParamSig(extMethod);
            if (!string.IsNullOrEmpty(paramSig))
            {
                var disambKey = MemberSignatureNormalizer.ComposeKey(bareKey, paramSig);
                if (availabilityAnnotations.TryGetValue(disambKey, out var disambAnnotations))
                    return disambAnnotations;
            }
        }
        return availabilityAnnotations.TryGetValue(bareKey, out var annotations) ? annotations : null;
    }

    /// <summary>
    /// Computes the normalized parameter signature for an extension method by
    /// scraping the source-text parameter list out of the raw signature. Mirrors
    /// the swiftinterface walker's <c>buildParamSignature</c> (which feeds the
    /// same key the consumer composes). Returns the empty string for zero-param
    /// methods so the caller falls back to the bare-key lookup unchanged.
    /// </summary>
    private static string TryComputeMethodParamSig(ProtocolExtensionMethodDecl extMethod)
    {
        var sig = extMethod.RawSignature;
        var openIdx = sig.IndexOf('(');
        if (openIdx < 0) return string.Empty;
        var closeIdx = FindMatchingParen(sig, openIdx);
        if (closeIdx < 0) return string.Empty;
        var inside = sig.Substring(openIdx + 1, closeIdx - openIdx - 1).Trim();
        if (string.IsNullOrEmpty(inside)) return string.Empty;
        return MemberSignatureNormalizer.BuildSignature(
            MemberSignatureNormalizer.ExtractParamTypesFromSwiftClause(inside));
    }

    private static int FindMatchingParen(string s, int openIdx)
    {
        int depth = 0;
        for (int i = openIdx; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Appends per-platform <c>@available(Platform Version, *)</c> lines to the
    /// foreign-extension Swift wrapper buffer. Uses the same per-platform-max
    /// collapse <see cref="WrapperEmitterHelpers.CollectStrictestAvailabilityKeys"/>
    /// applies elsewhere, so stacked extension-scope + decl-local floors emit one
    /// line per platform with the tightest version. No-op when there are no
    /// annotations.
    /// </summary>
    private static void EmitForeignExtAvailabilityLines(
        ModuleEmissionContext ctx,
        IReadOnlyList<AvailabilityAnnotation>? annotations)
    {
        if (annotations is null || annotations.Count == 0)
            return;
        foreach (var key in WrapperEmitterHelpers.CollectStrictestAvailabilityKeys(annotations))
        {
            ctx.AddForeignExtWrapperLine($"@available({key}, *)");
        }
    }

    /// <summary>
    /// Processes a property getter (and optionally setter) from a foreign extension.
    /// </summary>
    private static void TryProcessProperty(
        ModuleDecl moduleDecl,
        string foreignTypeQualifiedName,
        ProtocolExtensionMethodDecl extMethod,
        ITypeDatabase typeDatabase,
        ILogger logger,
        ModuleEmissionContext ctx,
        IReadOnlyList<AvailabilityAnnotation>? availabilityAnnotations = null)
    {
        // Parse property type from raw signature: "public var name: Type { get [set] }"
        var colonIdx = extMethod.RawSignature.IndexOf($"{extMethod.MethodName}:", StringComparison.Ordinal);
        if (colonIdx < 0)
            return;

        var afterColon = extMethod.RawSignature.Substring(colonIdx + extMethod.MethodName.Length + 1).Trim();
        // Remove trailing "{ get [set] }"
        var braceIdx = afterColon.IndexOf('{');
        if (braceIdx >= 0)
            afterColon = afterColon.Substring(0, braceIdx).Trim();

        // Strip attributes
        afterColon = StripSwiftAttributes(afterColon);

        if (string.IsNullOrWhiteSpace(afterColon))
            return;

        TypeSpec? propertyTypeSpec;
        try
        {
            propertyTypeSpec = TypeSpecParser.Parse(afterColon);
        }
        catch
        {
            logger.LogDebug("Skipping foreign extension property {Type}.{Name}: TypeSpecParser error for '{TypeStr}'",
                foreignTypeQualifiedName, extMethod.MethodName, afterColon);
            return;
        }
        if (propertyTypeSpec == null)
            return;

        // Surface-verify the property type against the .NET Apple-framework surface before any other
        // classification (same rationale as the method return/parameter gates): an Apple type absent
        // from the binding surface would emit a dangling getter/setter reference. Withdraw the member
        // with a report row instead of leaking a phantom type.
        if (ReferencesAbsentAppleType(propertyTypeSpec, typeDatabase, out var absentPropType))
        {
            ReportCollector.RecordMemberSkipped(
                BindingItemKind.Property, extMethod.MethodName, moduleDecl,
                SkipReason.AbsentFrameworkType,
                $"Foreign extension on '{foreignTypeQualifiedName}': property type '{absentPropType}' is an Apple-framework type absent from the .NET binding surface; the member cannot be bound.");
            return;
        }

        // Determine return category
        var returnCategory = ClassifyReturnType(propertyTypeSpec, typeDatabase);
        // FrozenStruct has no arm in this emitter's wrapper or C# body switches —
        // accepting it produces an empty C# body and a void-return P/Invoke. Mirror
        // the explicit FrozenStruct rejection that TryProcessMethod applies.
        if (returnCategory == null || returnCategory == ReturnKind.FrozenStruct)
        {
            logger.LogDebug("Skipping foreign extension property {Type}.{Name}: unsupported return type '{TypeStr}'",
                foreignTypeQualifiedName, extMethod.MethodName, afterColon);
            return;
        }

        var flatTypeName = FlattenQualifiedName(foreignTypeQualifiedName);
        var getterSymbol = $"SBSW_{flatTypeName}_get_{extMethod.MethodName}";

        // S5: structural-identity claim. The (foreignTypeQualifiedName, methodName, sourceKey)
        // triple is the cross-emitter dedup boundary; "get " prefix in sourceKey keeps the
        // getter distinct from the setter on the same property. ForeignTypeExtensionEmitter
        // is the only path that produces wrappers for foreign-type extension members today —
        // these come from parsed Swift interface text, not from a MethodDecl that MWE walks,
        // so there is no MethodWrapperEmitter counterpart to align with. The structural claim
        // is preventive against a future emitter joining the same surface.
        var getterSourceKey = $"get {extMethod.MethodName}::{extMethod.RawSignature}";
        if (!ctx.TryClaimWrapperSymbol(foreignTypeQualifiedName, extMethod.MethodName, getterSourceKey, getterSymbol))
            return;

        // Emit Swift getter wrapper
        EmitSwiftPropertyGetter(foreignTypeQualifiedName, extMethod, propertyTypeSpec, getterSymbol, returnCategory.Value, typeDatabase, ctx, availabilityAnnotations);

        // Collect C# getter info
        var csharpMethodName = $"Get{ToPascalCase(extMethod.MethodName)}";
        var classInfo = GetOrCreateClassInfo(foreignTypeQualifiedName, moduleDecl.Name, ctx);
        classInfo.Members.Add(new ForeignExtensionMemberInfo
        {
            SymbolName = getterSymbol,
            CSharpMethodName = csharpMethodName,
            ExtMethod = extMethod,
            Parameters = new(),
            ReturnTypeSpec = propertyTypeSpec,
            ReturnTypeName = afterColon,
            ReturnCategory = returnCategory.Value,
            IsPropertyGetter = true,
        });
        ctx.ForeignExtEmittedCount++;

        // Emit setter if applicable (only for primitives)
        if (extMethod.HasSetter)
        {
            if (IsPrimitiveSetter(propertyTypeSpec, typeDatabase))
            {
                var setterSymbol = $"SBSW_{flatTypeName}_set_{extMethod.MethodName}";
                // "set " prefix in sourceKey keeps the setter structurally distinct from the
                // getter on the same property — the rendered symbol already differs (set_/get_)
                // but the structural-identity tuple must match the rendering split for cross-
                // emitter dedup to stay correct.
                var setterSourceKey = $"set {extMethod.MethodName}::{extMethod.RawSignature}";
                if (!ctx.TryClaimWrapperSymbol(foreignTypeQualifiedName, extMethod.MethodName, setterSourceKey, setterSymbol))
                    return;

                EmitSwiftPropertySetter(foreignTypeQualifiedName, extMethod, propertyTypeSpec, setterSymbol, afterColon, ctx, availabilityAnnotations);

                classInfo.Members.Add(new ForeignExtensionMemberInfo
                {
                    SymbolName = setterSymbol,
                    CSharpMethodName = $"Set{ToPascalCase(extMethod.MethodName)}",
                    ExtMethod = extMethod,
                    Parameters = new() { ("value", propertyTypeSpec, afterColon, false) },
                    ReturnTypeSpec = null,
                    ReturnTypeName = "void",
                    ReturnCategory = ReturnKind.Void,
                    IsPropertySetter = true,
                });
                ctx.ForeignExtEmittedCount++;
            }
        }
    }

    /// <summary>
    /// Processes a method from a foreign extension.
    /// </summary>
    private static void TryProcessMethod(
        ModuleDecl moduleDecl,
        string foreignTypeQualifiedName,
        ProtocolExtensionMethodDecl extMethod,
        ITypeDatabase typeDatabase,
        ILogger logger,
        ModuleEmissionContext ctx,
        IReadOnlyList<AvailabilityAnnotation>? availabilityAnnotations = null)
    {
        // Gate: skip generic methods
        if (extMethod.RawSignature.Contains($"func {extMethod.MethodName}<"))
            return;

        // Parse signature
        var parseResult = ParseMethodSignature(extMethod, typeDatabase, logger);
        if (parseResult == null)
            return;

        var (allParameters, returnTypeSpec, returnTypeName) = parseResult.Value;

        // Surface-verify the return type against the real .NET Apple-framework surface before any
        // other classification. An Apple type absent from the binding surface resolves only to a
        // synthesized ObjC-bridged class record marked AbsentAppleProjection; emitting it would
        // dangle as a CS0234/CS0721 reference. The coarse cdecl-compatibility classifier below is
        // blind to this (it treats any auto-bridge module type as a marshalable ObjC-class pointer),
        // so gate here — withdraw the member with a report row instead of leaking a phantom type or
        // silently dropping it on a null return classification.
        if (ReferencesAbsentAppleType(returnTypeSpec, typeDatabase, out var absentReturnType))
        {
            ReportCollector.RecordMemberSkipped(
                BindingItemKind.Method, extMethod.MethodName, moduleDecl,
                SkipReason.AbsentFrameworkType,
                $"Foreign extension on '{foreignTypeQualifiedName}': return type '{absentReturnType}' is an Apple-framework type absent from the .NET binding surface; the member cannot be bound.");
            return;
        }

        // Classify return type
        ReturnKind returnCategory;
        if (returnTypeSpec == null || (returnTypeSpec is TupleTypeSpec tuple && tuple.IsEmptyTuple))
        {
            returnCategory = ReturnKind.Void;
        }
        else
        {
            var classified = ClassifyReturnType(returnTypeSpec, typeDatabase);
            // FrozenStruct returns were previously rejected by ClassifyReturnType itself; that
            // shared helper now accepts them on behalf of the cross-module struct-receiver
            // emitter, so this emitter must re-impose the historical rejection so its
            // ReturnKind switches (which have no FrozenStruct arms) stay total.
            if (classified == null || classified == ReturnKind.FrozenStruct)
            {
                logger.LogDebug("Skipping foreign extension method {Type}.{Method}: unsupported return type",
                    foreignTypeQualifiedName, extMethod.MethodName);
                return;
            }
            returnCategory = classified.Value;
        }

        // Apply default parameter reduction: emit with only compatible params
        var compatibleParams = new List<(string label, TypeSpec typeSpec, string swiftType, bool hasDefault)>();
        bool hasIncompatibleNonDefault = false;
        string? absentParamType = null;

        foreach (var (label, typeSpec, swiftType, hasDefault) in allParameters)
        {
            // Surface-verify each parameter against the .NET Apple-framework surface BEFORE the
            // coarse cdecl-compatibility classification, which would accept an absent Apple type as
            // a generic ObjC-class pointer and emit a dangling CS0234 reference. A required absent
            // type withdraws the whole member (with a report row); a defaulted one is omitted, since
            // Swift fills its default and the wrapper never mentions the phantom type.
            if (ReferencesAbsentAppleType(typeSpec, typeDatabase, out var absentType))
            {
                if (!hasDefault)
                {
                    hasIncompatibleNonDefault = true;
                    absentParamType = absentType;
                    break;
                }
                // Absent Apple type with a default — omit (Swift fills the default).
                continue;
            }

            if (IsCdeclCompatibleType(typeSpec, typeDatabase))
            {
                compatibleParams.Add((label, typeSpec, swiftType, hasDefault));
            }
            else if (!hasDefault)
            {
                // Non-default incompatible param — can't emit this method at all
                hasIncompatibleNonDefault = true;
                break;
            }
            // else: incompatible with default — omit (Swift fills default)
        }

        if (hasIncompatibleNonDefault)
        {
            if (absentParamType != null)
            {
                // An absent Apple-framework parameter is a surface gap, not an unmarshalable shape:
                // report it so the withdrawal is observable rather than a silent LogDebug drop.
                ReportCollector.RecordMemberSkipped(
                    BindingItemKind.Method, extMethod.MethodName, moduleDecl,
                    SkipReason.AbsentFrameworkType,
                    $"Foreign extension on '{foreignTypeQualifiedName}': parameter type '{absentParamType}' is an Apple-framework type absent from the .NET binding surface; the member cannot be bound.");
            }
            else
            {
                logger.LogDebug("Skipping foreign extension method {Type}.{Method}: incompatible non-default parameter",
                    foreignTypeQualifiedName, extMethod.MethodName);
            }
            return;
        }

        var flatTypeName = FlattenQualifiedName(foreignTypeQualifiedName);
        var symbolName = BuildSymbolName(flatTypeName, extMethod.MethodName, compatibleParams);

        // S5: structural-identity claim. The raw signature uniquely identifies the
        // underlying Swift extension method even when the rendered SBSW_ symbol's label
        // suffix would differ across emitters (e.g. parameter-type rename in a future
        // emitter rewrite). Two genuine overloads on the same foreign type stay distinct
        // because RawSignature embeds the parameter types verbatim.
        var sourceKey = $"method {extMethod.MethodName}::{extMethod.RawSignature}";
        if (!ctx.TryClaimWrapperSymbol(foreignTypeQualifiedName, extMethod.MethodName, sourceKey, symbolName))
            return;

        // Emit Swift wrapper
        EmitSwiftMethodWrapper(foreignTypeQualifiedName, extMethod, allParameters, compatibleParams,
            returnTypeSpec, symbolName, returnCategory, typeDatabase, ctx, availabilityAnnotations);

        // Collect C# info
        var classInfo = GetOrCreateClassInfo(foreignTypeQualifiedName, moduleDecl.Name, ctx);
        classInfo.Members.Add(new ForeignExtensionMemberInfo
        {
            SymbolName = symbolName,
            CSharpMethodName = ToPascalCase(extMethod.MethodName),
            ExtMethod = extMethod,
            Parameters = compatibleParams,
            ReturnTypeSpec = returnTypeSpec,
            ReturnTypeName = returnTypeName,
            ReturnCategory = returnCategory,
        });
        ctx.ForeignExtEmittedCount++;
    }

    // ==================== Swift Wrapper Emission ====================

    /// <summary>
    /// Emits a Swift property getter wrapper.
    /// </summary>
    private static void EmitSwiftPropertyGetter(
        string foreignTypeQualifiedName,
        ProtocolExtensionMethodDecl extMethod,
        TypeSpec propertyTypeSpec,
        string symbolName,
        ReturnKind returnCategory,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext ctx,
        IReadOnlyList<AvailabilityAnnotation>? availabilityAnnotations = null)
    {
        // A SimpleEnum's physical tag layout is assigned in declaration order and is NOT
        // guaranteed to equal its raw value, so this silgen boundary can only cross it as
        // the raw scalar (matching EmitSwiftMethodWrapper's identical parameter/return
        // treatment) — never the enum type itself.
        string? returnUnderlyingSwiftType = null;
        bool returnIsSimpleEnum = returnCategory == ReturnKind.Primitive &&
            TryGetSimpleEnumLowering(propertyTypeSpec, typeDatabase, out _, out returnUnderlyingSwiftType, out _);

        string swiftReturnType;
        bool wrapAsOpaque;

        switch (returnCategory)
        {
            case ReturnKind.Void:
                swiftReturnType = "";
                wrapAsOpaque = false;
                break;
            case ReturnKind.Primitive:
                swiftReturnType = returnIsSimpleEnum ? returnUnderlyingSwiftType! : ExistentialBypassEmitter.RenderSwiftTypeSpec(propertyTypeSpec);
                wrapAsOpaque = false;
                break;
            case ReturnKind.ObjCClass:
            case ReturnKind.SwiftClass:
                swiftReturnType = "UnsafeMutableRawPointer";
                wrapAsOpaque = true;
                break;
            case ReturnKind.NonFrozenStruct:
                // Return by value — CallConvSwift handles indirect return automatically
                swiftReturnType = ExistentialBypassEmitter.RenderSwiftTypeSpec(propertyTypeSpec);
                wrapAsOpaque = false;
                break;
            default:
                return;
        }

        var returnArrow = string.IsNullOrEmpty(swiftReturnType) ? "" : $" -> {swiftReturnType}";

        ctx.AddForeignExtWrapperLine("");
        EmitForeignExtAvailabilityLines(ctx, availabilityAnnotations);
        ctx.AddForeignExtWrapperLine($"@_silgen_name(\"{symbolName}\")");
        if (extMethod.IsMainActorIsolated)
        {
            ctx.AddForeignExtWrapperLine("@MainActor");
        }
        ctx.AddForeignExtWrapperLine($"public func {symbolName}(_ self_: UnsafeMutableRawPointer){returnArrow} {{");
        ctx.AddForeignExtWrapperLine($"    let instance = Unmanaged<{foreignTypeQualifiedName}>.fromOpaque(self_).takeUnretainedValue()");

        if (wrapAsOpaque)
        {
            ctx.AddForeignExtWrapperLine($"    let result = instance.{NameProvider.EscapeSwiftKeyword(extMethod.MethodName)}");
            ctx.AddForeignExtWrapperLine($"    return Unmanaged.passUnretained(result).toOpaque()");
        }
        else if (returnCategory == ReturnKind.NonFrozenStruct)
        {
            ctx.AddForeignExtWrapperLine($"    return instance.{NameProvider.EscapeSwiftKeyword(extMethod.MethodName)}");
        }
        else if (returnCategory == ReturnKind.Primitive)
        {
            var propertyAccess = $"instance.{NameProvider.EscapeSwiftKeyword(extMethod.MethodName)}";
            ctx.AddForeignExtWrapperLine(returnIsSimpleEnum
                ? $"    return ({propertyAccess}).rawValue"
                : $"    return {propertyAccess}");
        }
        else
        {
            ctx.AddForeignExtWrapperLine($"    instance.{NameProvider.EscapeSwiftKeyword(extMethod.MethodName)}");
        }
        ctx.AddForeignExtWrapperLine("}");
    }

    /// <summary>
    /// Emits a Swift property setter wrapper (primitives only).
    /// </summary>
    private static void EmitSwiftPropertySetter(
        string foreignTypeQualifiedName,
        ProtocolExtensionMethodDecl extMethod,
        TypeSpec propertyTypeSpec,
        string symbolName,
        string swiftTypeName,
        ModuleEmissionContext ctx,
        IReadOnlyList<AvailabilityAnnotation>? availabilityAnnotations = null)
    {
        var renderedType = ExistentialBypassEmitter.RenderSwiftTypeSpec(propertyTypeSpec);

        ctx.AddForeignExtWrapperLine("");
        EmitForeignExtAvailabilityLines(ctx, availabilityAnnotations);
        ctx.AddForeignExtWrapperLine($"@_silgen_name(\"{symbolName}\")");
        if (extMethod.IsMainActorIsolated)
        {
            ctx.AddForeignExtWrapperLine("@MainActor");
        }
        ctx.AddForeignExtWrapperLine($"public func {symbolName}(_ self_: UnsafeMutableRawPointer, _ value: {renderedType}) {{");
        ctx.AddForeignExtWrapperLine($"    let instance = Unmanaged<{foreignTypeQualifiedName}>.fromOpaque(self_).takeUnretainedValue()");
        ctx.AddForeignExtWrapperLine($"    instance.{NameProvider.EscapeSwiftKeyword(extMethod.MethodName)} = value");
        ctx.AddForeignExtWrapperLine("}");
    }

    /// <summary>
    /// Emits a Swift method wrapper. Passes only compatible parameters;
    /// Swift fills defaults for omitted ones.
    /// </summary>
    private static void EmitSwiftMethodWrapper(
        string foreignTypeQualifiedName,
        ProtocolExtensionMethodDecl extMethod,
        List<(string label, TypeSpec typeSpec, string swiftType, bool hasDefault)> allParameters,
        List<(string label, TypeSpec typeSpec, string swiftType, bool hasDefault)> compatibleParams,
        TypeSpec? returnTypeSpec,
        string symbolName,
        ReturnKind returnCategory,
        ITypeDatabase typeDatabase,
        ModuleEmissionContext ctx,
        IReadOnlyList<AvailabilityAnnotation>? availabilityAnnotations = null)
    {
        // Build Swift parameter list for wrapper
        var swiftParams = new List<string>();
        swiftParams.Add("_ self_: UnsafeMutableRawPointer");

        // Compute the source-local wrapper bindings ONCE (sanitize + reserved-escape against
        // the injected `self_` and siblings) so the signature decls below and the call-arg loop later
        // index the SAME names — recomputing per loop and escaping only one would desync the wrapper.
        var paramNames = ComputeForeignExtParamNames(compatibleParams);
        for (int p = 0; p < compatibleParams.Count; p++)
        {
            var (_, typeSpec, _, _) = compatibleParams[p];
            var paramName = paramNames[p];
            if (TryGetSimpleEnumLowering(typeSpec, typeDatabase, out _, out var underlyingSwiftType, out _))
            {
                // Crosses the silgen boundary as its raw scalar; the call-arg loop below
                // reconstructs the real enum via T(rawValue:) before the Swift call.
                swiftParams.Add($"_ {paramName}: {underlyingSwiftType}");
            }
            else if (typeSpec is NamedTypeSpec namedType && !namedType.ContainsGenericParameters &&
                !MarshallingHelpers.IsSwiftPrimitive(namedType.Name))
            {
                swiftParams.Add($"_ {paramName}: UnsafeMutableRawPointer");
            }
            else
            {
                var renderedType = ExistentialBypassEmitter.RenderSwiftTypeSpec(typeSpec);
                swiftParams.Add($"_ {paramName}: {renderedType}");
            }
        }

        // Build return type. A SimpleEnum's physical tag layout is assigned in
        // declaration order and is NOT guaranteed to equal its raw value, so this
        // silgen boundary can only cross it as the raw scalar (matching the parameter
        // treatment above) — never the enum type itself, or a CallConvSwift caller that
        // isn't swiftc (i.e. the C# P/Invoke) can observe the wrong case entirely.
        string? returnUnderlyingSwiftType = null;
        bool returnIsSimpleEnum = returnCategory == ReturnKind.Primitive && returnTypeSpec != null &&
            TryGetSimpleEnumLowering(returnTypeSpec, typeDatabase, out _, out returnUnderlyingSwiftType, out _);

        string swiftReturnType;
        bool returnIsClass;
        switch (returnCategory)
        {
            case ReturnKind.ObjCClass:
            case ReturnKind.SwiftClass:
                swiftReturnType = "UnsafeMutableRawPointer";
                returnIsClass = true;
                break;
            case ReturnKind.Primitive:
                swiftReturnType = returnIsSimpleEnum ? returnUnderlyingSwiftType! : ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec!);
                returnIsClass = false;
                break;
            case ReturnKind.NonFrozenStruct:
                swiftReturnType = ExistentialBypassEmitter.RenderSwiftTypeSpec(returnTypeSpec!);
                returnIsClass = false;
                break;
            default:
                swiftReturnType = "";
                returnIsClass = false;
                break;
        }

        var returnArrow = string.IsNullOrEmpty(swiftReturnType) ? "" : $" -> {swiftReturnType}";

        ctx.AddForeignExtWrapperLine("");
        EmitForeignExtAvailabilityLines(ctx, availabilityAnnotations);
        ctx.AddForeignExtWrapperLine($"@_silgen_name(\"{symbolName}\")");
        if (extMethod.IsMainActorIsolated)
        {
            ctx.AddForeignExtWrapperLine("@MainActor");
        }
        ctx.AddForeignExtWrapperLine($"public func {symbolName}({string.Join(", ", swiftParams)}){returnArrow} {{");
        ctx.AddForeignExtWrapperLine($"    let instance = Unmanaged<{foreignTypeQualifiedName}>.fromOpaque(self_).takeUnretainedValue()");

        // Build call arguments — map compatible params into call, skip incompatible (use Swift defaults)
        var compatibleSet = new HashSet<int>();
        int compatIdx = 0;
        for (int i = 0; i < allParameters.Count; i++)
        {
            if (compatIdx < compatibleParams.Count &&
                allParameters[i].label == compatibleParams[compatIdx].label &&
                allParameters[i].swiftType == compatibleParams[compatIdx].swiftType)
            {
                compatibleSet.Add(i);
                compatIdx++;
            }
        }

        var callArgs = new List<string>();
        compatIdx = 0;
        for (int i = 0; i < allParameters.Count; i++)
        {
            if (!compatibleSet.Contains(i))
                continue; // Omitted — Swift fills default

            var (label, typeSpec, _, _) = allParameters[i];
            // Same escaped binding the signature emitted (compatible params share order) — never
            // recompute here, or a reserved-escape applied above would desync from the call body.
            var paramName = paramNames[compatIdx++];

            if (TryGetSimpleEnumLowering(typeSpec, typeDatabase, out _, out _, out var qualifiedSwiftType))
            {
                // Reconstruct the enum from its raw scalar (guard-let / preconditionFailure,
                // matching CrossModuleExtensionEmitter's identical SimpleEnum reconstruction).
                var localName = $"{paramName}Val";
                ctx.AddForeignExtWrapperLine($"    guard let {localName} = {qualifiedSwiftType}(rawValue: {paramName}) else {{ preconditionFailure(\"[SwiftBindings] Invalid raw value \\({paramName}) for {qualifiedSwiftType}\") }}");
                callArgs.Add(label == "_" ? localName : $"{label}: {localName}");
            }
            else if (typeSpec is NamedTypeSpec namedType && !namedType.ContainsGenericParameters &&
                !MarshallingHelpers.IsSwiftPrimitive(namedType.Name))
            {
                // Use Unmanaged<AnyObject> + cast to handle both true classes and ObjC-bridged structs
                var renderedType = ExistentialBypassEmitter.RenderSwiftTypeSpec(typeSpec);
                var localName = $"__{paramName}";
                ctx.AddForeignExtWrapperLine($"    let {localName} = Unmanaged<AnyObject>.fromOpaque({paramName}).takeUnretainedValue() as! {renderedType}");
                callArgs.Add(label == "_" ? localName : $"{label}: {localName}");
            }
            else
            {
                callArgs.Add(label == "_" ? paramName : $"{label}: {paramName}");
            }
        }

        var callStr = $"instance.{NameProvider.EscapeSwiftKeyword(extMethod.MethodName)}({string.Join(", ", callArgs)})";
        if (returnIsSimpleEnum)
        {
            // Cross the boundary as the raw scalar, not the enum's physical tag — see the
            // returnIsSimpleEnum comment above.
            callStr = $"({callStr}).rawValue";
        }

        if (returnIsClass)
        {
            ctx.AddForeignExtWrapperLine($"    let result = {callStr}");
            ctx.AddForeignExtWrapperLine($"    return Unmanaged.passUnretained(result).toOpaque()");
        }
        else if (string.IsNullOrEmpty(swiftReturnType))
        {
            ctx.AddForeignExtWrapperLine($"    {callStr}");
        }
        else
        {
            ctx.AddForeignExtWrapperLine($"    return {callStr}");
        }

        ctx.AddForeignExtWrapperLine("}");
    }

    // ==================== C# Extension Class Emission ====================

    /// <summary>
    /// Emits a single C# static extension class for a foreign type.
    /// </summary>
    private static void EmitExtensionClass(CSharpWriter csWriter, ForeignExtensionClassInfo classInfo,
        ITypeDatabase typeDatabase, string moduleName)
    {
        var foreignTypeName = classInfo.ForeignTypeQualifiedName;
        var dotIdx = foreignTypeName.LastIndexOf('.');
        var unqualifiedTypeName = dotIdx >= 0 ? foreignTypeName.Substring(dotIdx + 1) : foreignTypeName;
        var className = $"{unqualifiedTypeName}{moduleName}Extensions";

        // Resolve the C# namespace-qualified type name for the foreign type
        var csharpSelfType = ResolveForeignTypeCSharpName(foreignTypeName, typeDatabase);

        var wrapperLibPath = typeDatabase.AsyncLibraryName ?? "libSwiftBindings";

        csWriter.WriteLine();
        csWriter.WriteLine($"/// <summary>");
        csWriter.WriteLine($"/// Extension methods for {foreignTypeName} defined in {moduleName}.");
        csWriter.WriteLine($"/// </summary>");
        csWriter.WriteLine($"public static partial class {className}");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Resolve the emitted C# name per member up front so label-only Swift overloads
        // (which collapse to identical C# signatures — CS0111) get disambiguated before emission.
        var emittedNames = ResolveExtensionMemberNames(classInfo, typeDatabase);

        // Emit each member
        foreach (var member in classInfo.Members)
        {
            EmitExtensionMember(csWriter, member, emittedNames[member], csharpSelfType, wrapperLibPath, typeDatabase, moduleName);
        }

        // Emit NativeMethods nested class
        csWriter.WriteLine($"private static partial class NativeMethods");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        foreach (var member in classInfo.Members)
        {
            EmitNativeMethod(csWriter, member, wrapperLibPath, typeDatabase);
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");

        csWriter.Indent--;
        csWriter.WriteLine("}");
    }

    /// <summary>
    /// Resolves the emitted C# method name for every member, disambiguating label-only
    /// overloads. Swift permits overloads that differ ONLY by argument labels (e.g.
    /// <c>func f(a:)</c> vs <c>func f(b:)</c>); both PascalCase to the same C# name and, with
    /// the same projected parameter types, would emit as duplicate signatures (CS0111). Members
    /// are grouped by their full emitted signature (name + projected parameter types); only a
    /// group with more than one member is a genuine collision, and then EVERY member in that
    /// group is renamed with a label-derived suffix (not first-wins) so the emitted API stays
    /// symmetric and self-describing. A positional index is the fallback when labels don't
    /// disambiguate. Type-distinct overloads and unique names are left untouched.
    ///
    /// A renamed member must avoid EVERY other emitted signature in the class, not just the
    /// names within its own collision group. The signature keys of members that keep their
    /// natural name are reserved first, so a label suffix that would land on an unrelated
    /// natural sibling — natural <c>processBy(x:)</c> → <c>ProcessBy(int)</c> vs
    /// <c>process(by:)</c> renamed to the same <c>ProcessBy(int)</c> — is bumped with a
    /// positional index instead of silently re-introducing the CS0111 this method removes.
    /// </summary>
    private static Dictionary<ForeignExtensionMemberInfo, string> ResolveExtensionMemberNames(
        ForeignExtensionClassInfo classInfo, ITypeDatabase typeDatabase)
    {
        var resolved = new Dictionary<ForeignExtensionMemberInfo, string>();

        var groups = classInfo.Members
            .GroupBy(m => BuildEmittedSignatureKey(m, m.CSharpMethodName, typeDatabase))
            .Select(g => g.ToList())
            .ToList();

        // Reserve the signature key of every member that keeps its natural name (singleton
        // groups) before assigning any suffix, so cross-group collisions are caught too.
        var reservedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var members in groups)
        {
            if (members.Count != 1)
                continue;
            resolved[members[0]] = members[0].CSharpMethodName;
            reservedKeys.Add(BuildEmittedSignatureKey(members[0], members[0].CSharpMethodName, typeDatabase));
        }

        foreach (var members in groups)
        {
            if (members.Count == 1)
                continue;

            foreach (var member in members)
            {
                var baseName = member.CSharpMethodName;
                var suffix = BuildLabelSuffix(member);
                var candidate = baseName + suffix;
                var key = BuildEmittedSignatureKey(member, candidate, typeDatabase);
                if (suffix.Length == 0 || !reservedKeys.Add(key))
                {
                    var i = 1;
                    do
                    {
                        candidate = $"{baseName}{suffix}{i++}";
                        key = BuildEmittedSignatureKey(member, candidate, typeDatabase);
                    } while (!reservedKeys.Add(key));
                }
                resolved[member] = candidate;
            }
        }

        return resolved;
    }

    /// <summary>
    /// Builds the emitted-signature collision key: the C# method name plus the projected C#
    /// parameter types, in order. The <c>this self</c> receiver is identical for every member
    /// in one extension class, so it's omitted — only the relative signatures within the class
    /// determine a CS0111 collision.
    /// </summary>
    private static string BuildEmittedSignatureKey(ForeignExtensionMemberInfo member, string name,
        ITypeDatabase typeDatabase)
    {
        var sb = new System.Text.StringBuilder(name);
        sb.Append('(');
        var first = true;
        foreach (var (_, typeSpec, _, _) in member.Parameters)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(ResolveCSharpParameterType(typeSpec, typeDatabase));
        }
        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>
    /// Concatenates the PascalCased Swift argument labels into a name suffix, skipping wildcard
    /// (<c>_</c>) and empty labels. Empty when the member carries no explicit labels.
    /// </summary>
    private static string BuildLabelSuffix(ForeignExtensionMemberInfo member)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (label, _, _, _) in member.Parameters)
        {
            if (string.IsNullOrEmpty(label) || label == "_")
                continue;
            sb.Append(ToPascalCase(label));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Emits a single public extension method in the extension class.
    /// </summary>
    private static void EmitExtensionMember(CSharpWriter csWriter, ForeignExtensionMemberInfo member,
        string emittedMethodName, string csharpSelfType, string wrapperLibPath, ITypeDatabase typeDatabase, string moduleName)
    {
        var csharpReturnType = ResolveCSharpReturnType(member, typeDatabase, moduleName);

        // Build parameter list
        var paramList = new List<string>();
        paramList.Add($"this {csharpSelfType} self");
        foreach (var (label, typeSpec, swiftType, _) in member.Parameters)
        {
            var paramTypeName = ResolveCSharpParameterType(typeSpec, typeDatabase);
            var paramName = ToCamelCase(label == "_" ? GetParamNameFromType(swiftType) : label);
            if (member.IsPropertySetter && label == "value")
                paramName = "value";
            paramList.Add($"{paramTypeName} {paramName}");
        }

        // For setter, return type is void
        var methodReturnType = member.IsPropertySetter ? "void" : csharpReturnType;

        csWriter.WriteLine();
        csWriter.WriteLine($"public static {methodReturnType} {emittedMethodName}({string.Join(", ", paramList)})");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        EmitMethodBody(csWriter, member, typeDatabase, moduleName);

        csWriter.Indent--;
        csWriter.WriteLine("}");
    }

    /// <summary>
    /// Emits the method body with proper marshalling based on return type category.
    /// </summary>
    private static void EmitMethodBody(CSharpWriter csWriter, ForeignExtensionMemberInfo member,
        ITypeDatabase typeDatabase, string moduleName)
    {
        // Build native call arguments
        var nativeArgs = new List<string>();

        // For non-frozen struct returns, SwiftIndirectResult is the first parameter
        if (member.ReturnCategory == ReturnKind.NonFrozenStruct)
        {
            nativeArgs.Add("indirectResult");
        }

        nativeArgs.Add("self.Handle");

        // Add method parameters
        foreach (var (label, typeSpec, swiftType, _) in member.Parameters)
        {
            var paramName = ToCamelCase(label == "_" ? GetParamNameFromType(swiftType) : label);
            if (member.IsPropertySetter && label == "value")
                paramName = "value";

            // A SimpleEnum is a NamedTypeSpec too, but it crosses the silgen boundary as
            // its raw integer scalar (see EmitSwiftMethodWrapper's reconstruction via
            // T(rawValue:)), never a pointer — check this before the generic
            // NamedTypeSpec/.Handle branch below, or a C# enum value falls into that
            // branch and emits `status.Handle`, which doesn't exist on a plain enum.
            if (ExtensionMarshallingHelper.TryGetSimpleEnumLowering(typeSpec, typeDatabase,
                out var simpleEnumUnderlyingCS, out _, out _))
            {
                nativeArgs.Add($"({simpleEnumUnderlyingCS}){paramName}");
            }
            else if (typeSpec is NamedTypeSpec namedType && !namedType.ContainsGenericParameters &&
                !MarshallingHelpers.IsSwiftPrimitive(namedType.Name) &&
                !MarshallingHelpers.TypeAliasToCSPrimitive.ContainsKey(namedType.Name))
            {
                // Distinguish ObjC classes (.Handle) from same-module Swift classes (.Payload.DangerousGetHandle())
                if (IsSameModuleSwiftClass(namedType, typeDatabase))
                    nativeArgs.Add($"{paramName}.Payload.DangerousGetHandle()");
                else
                    nativeArgs.Add($"{paramName}.Handle");
            }
            else
            {
                nativeArgs.Add(paramName);
            }
        }

        var nativeCall = $"NativeMethods.{member.SymbolName}({string.Join(", ", nativeArgs)})";

        var csharpType = ResolveCSharpReturnType(member, typeDatabase, moduleName);
        bool returnNeedsEnumCast = member.ReturnCategory == ReturnKind.Primitive && member.ReturnTypeSpec != null &&
            ExtensionMarshallingHelper.TryGetSimpleEnumLowering(member.ReturnTypeSpec, typeDatabase, out _, out _, out _);
        EmitReturnValueMarshalling(csWriter, member.ReturnCategory, nativeCall, csharpType, returnNeedsEnumCast);
    }

    /// <summary>
    /// Emits a P/Invoke declaration in the NativeMethods nested class.
    /// </summary>
    private static void EmitNativeMethod(CSharpWriter csWriter, ForeignExtensionMemberInfo member,
        string wrapperLibPath, ITypeDatabase typeDatabase)
    {
        var pinvokeParams = new List<string>();

        // Non-frozen struct returns use SwiftIndirectResult as first param
        bool usesIndirectResult = member.ReturnCategory == ReturnKind.NonFrozenStruct;
        string pinvokeReturnType;

        if (usesIndirectResult)
        {
            pinvokeParams.Add("SwiftIndirectResult result");
            pinvokeReturnType = "void";
        }
        else
        {
            pinvokeReturnType = ExtensionMarshallingHelper.ResolvePInvokeReturnType(
                member.ReturnTypeSpec, member.ReturnCategory, typeDatabase, usesIndirectResult: false);
        }

        // Self parameter
        pinvokeParams.Add("IntPtr self_");

        // Method parameters
        foreach (var (label, typeSpec, swiftType, _) in member.Parameters)
        {
            var paramName = ToCamelCase(label == "_" ? GetParamNameFromType(swiftType) : label);
            if (member.IsPropertySetter && label == "value")
                paramName = "value";

            // Mirror EmitMethodBody: a SimpleEnum crosses this silgen boundary as its raw
            // integer scalar, not a pointer, so the P/Invoke declaration must take the
            // underlying numeric type — not fall into the generic NamedTypeSpec IntPtr arm.
            if (ExtensionMarshallingHelper.TryGetSimpleEnumLowering(typeSpec, typeDatabase,
                out var simpleEnumUnderlyingCS, out _, out _))
            {
                pinvokeParams.Add($"{simpleEnumUnderlyingCS} {paramName}");
            }
            else if (typeSpec is NamedTypeSpec namedType && !namedType.ContainsGenericParameters &&
                !MarshallingHelpers.IsSwiftPrimitive(namedType.Name) &&
                !MarshallingHelpers.TypeAliasToCSPrimitive.ContainsKey(namedType.Name))
            {
                pinvokeParams.Add($"IntPtr {paramName}");
            }
            else
            {
                var pinvokeType = ExtensionMarshallingHelper.ResolveCSharpTypeName(typeSpec, typeDatabase);
                // Bool parameters need [MarshalAs(UnmanagedType.U1)]
                if (typeSpec is NamedTypeSpec paramNamed && paramNamed.Name == "Swift.Bool")
                    pinvokeParams.Add($"{MarshallingHelpers.BoolPInvokeParamAttribute} bool {paramName}");
                else
                    pinvokeParams.Add($"{pinvokeType} {paramName}");
            }
        }

        // Foreign type extension wrappers use @_silgen_name (swiftcc), not @_cdecl.
        // CallConvSwift ensures SwiftIndirectResult maps to x8, not x0.
        PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
        {
            LibraryPath = wrapperLibPath,
            EntryPoint = member.SymbolName,
            MethodName = member.SymbolName,
            ReturnType = pinvokeReturnType,
            ParametersString = string.Join(", ", pinvokeParams),
            CallingConvention = PInvokeCallingConvention.Swift,
            Visibility = PInvokeVisibility.Internal
        });
        csWriter.WriteLine();
    }

    // ==================== Type Resolution Helpers ====================

    /// <summary>
    /// Checks if a foreign type qualified name represents an ObjC class type.
    /// Only ObjC classes (UIView, UILabel, etc.) are supported — not primitives,
    /// structs, or other Swift value types.
    ///
    /// Unqualified names (no module prefix) are rejected because IsObjCModuleType
    /// requires a module to check. SPM-built .swiftinterface files always use
    /// fully-qualified names for foreign types (e.g., "UIKit.UIView"), so this
    /// limitation only affects the safety-net unqualified parser path.
    /// </summary>
    private static bool IsForeignObjCClassType(string foreignTypeQualifiedName)
    {
        // Use TypeDatabaseExtensions.IsObjCModuleType via a temporary NamedTypeSpec.
        // This handles both qualified ("UIKit.UIView") and rejects unqualified names
        // (NamedTypeSpec.HasModule() returns false → IsObjCModuleType returns false).
        try
        {
            var namedType = new NamedTypeSpec(foreignTypeQualifiedName);
            return TypeDatabaseExtensions.IsObjCModuleType(namedType);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Classifies a property type for setter emission. Only primitives are supported.
    /// </summary>
    private static bool IsPrimitiveSetter(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        return ClassifyParameterType(typeSpec, typeDatabase) == ParamKind.Primitive;
    }

    /// <summary>
    /// Checks if a TypeSpec is cdecl-compatible for foreign extension methods.
    /// Uses ClassifyParameterType from ExtensionMarshallingHelper — a type is compatible
    /// if it classifies to any ParamKind (primitives, ObjC classes, Swift classes, simple enums).
    /// Also accepts empty tuples (Void).
    /// </summary>
    private static bool IsCdeclCompatibleType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec is NamedTypeSpec namedType)
        {
            if (namedType.ContainsGenericParameters)
            {
                // Optional parameters have no marshalling path in ForeignTypeExtensionEmitter —
                // reject them here so they are either omitted (if they have defaults) or cause
                // the whole method to be skipped.
                return false;
            }

            // FrozenStruct params route through pinned-pointer + cdecl wrappers in the
            // cross-module struct-receiver path; this emitter has no equivalent plumbing,
            // so reject them here (historically rejected at the helper level).
            var kind = ClassifyParameterType(typeSpec, typeDatabase);
            if (kind == null || kind == ParamKind.FrozenStruct)
                return false;

            // A SimpleEnum crosses this silgen boundary as its raw integer scalar, never a
            // pointer (see the declaration/call-arg loops in EmitSwiftMethodWrapper) — that
            // lowering only exists for a non-String raw value. Reject one this emitter cannot
            // lower so the containing method is cleanly skipped/defaulted via the existing
            // hasIncompatibleNonDefault path, instead of falling through to the object-pointer
            // arm below and emitting an illegal `Unmanaged<AnyObject> ... as! T` for a
            // non-class type (e.g. CoreData.NSAttributeType).
            if (kind == ParamKind.SimpleEnum &&
                !TryGetSimpleEnumLowering(typeSpec, typeDatabase, out _, out _, out _))
            {
                return false;
            }

            return true;
        }

        if (typeSpec is ClosureTypeSpec) return false;
        if (typeSpec is TupleTypeSpec t) return t.IsEmptyTuple;
        if (typeSpec is ProtocolListTypeSpec) return false;
        return false;
    }

    /// <summary>
    /// Checks if a NamedTypeSpec represents a same-module Swift class (not an ObjC class).
    /// Same-module Swift classes expose .Payload (SafeHandle) instead of .Handle (IntPtr).
    /// ObjC-bridged classes (UIColor, UIImage, etc.) use .Handle even if they appear in TypeDatabase.
    /// </summary>
    private static bool IsSameModuleSwiftClass(NamedTypeSpec namedType, ITypeDatabase typeDatabase)
    {
        // ObjC types always use .Handle — check this first to avoid false positives
        // from TypeDatabase entries for ObjC-bridged types
        if (TypeDatabaseExtensions.IsObjCModuleType(namedType))
            return false;

        try
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
            if (typeDatabase.TryGetTypeRecord(swiftTypeName, out var typeRecord))
                return typeRecord.Kind == TypeRecordKind.Class
                    && !typeRecord.Flags.HasFlag(TypeRecordFlags.ObjCBridged);
        }
        catch (ArgumentException)
        {
            // Not a module-qualified name
        }
        return false;
    }

    /// <summary>
    /// Resolves a foreign type's C# name using TypeDatabase or ObjC module conventions.
    /// Uses the NamedTypeSpec overload of TryGetTypeRecord which auto-creates ObjC bridged
    /// type records (handling class remappings like Foundation.HTTPURLResponse → NSHttpUrlResponse).
    /// </summary>
    private static string ResolveForeignTypeCSharpName(string foreignTypeQualifiedName, ITypeDatabase typeDatabase)
    {
        // Use NamedTypeSpec-based lookup which goes through CreateObjCBridgedTypeRecord
        // for ObjC types, handling Apple framework class remappings correctly
        try
        {
            var namedType = new NamedTypeSpec(foreignTypeQualifiedName);
            if (typeDatabase.TryGetTypeRecord(namedType, out var typeRecord))
            {
                return typeRecord.CSharpTypeName.FullyQualifiedName;
            }
        }
        catch (ArgumentException)
        {
            // Fall through
        }

        // Fallback: manual module.typeName construction
        return MarshallingHelpers.MapQualifiedTypeToNet(foreignTypeQualifiedName);
    }

    /// <summary>
    /// Resolves the C# return type for an extension member.
    /// </summary>
    private static string ResolveCSharpReturnType(ForeignExtensionMemberInfo member, ITypeDatabase typeDatabase, string moduleName)
    {
        if (member.ReturnCategory == ReturnKind.Void)
            return "void";

        if (member.ReturnTypeSpec == null)
            return "void";

        return ExtensionMarshallingHelper.ResolveCSharpTypeName(member.ReturnTypeSpec, typeDatabase);
    }

    /// <summary>
    /// Resolves a C# type name for a parameter TypeSpec.
    /// </summary>
    private static string ResolveCSharpParameterType(TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        return ExtensionMarshallingHelper.ResolveCSharpTypeName(typeSpec, typeDatabase);
    }

    // ==================== Parsing Helpers ====================

    /// <summary>
    /// Parses a method signature into structured parameter and return type info.
    /// Includes default value detection for each parameter.
    /// </summary>
    private static (List<(string label, TypeSpec typeSpec, string swiftType, bool hasDefault)> parameters,
                     TypeSpec? returnTypeSpec, string returnTypeName)?
        ParseMethodSignature(
            ProtocolExtensionMethodDecl extMethod,
            ITypeDatabase typeDatabase,
            ILogger logger)
    {
        var line = extMethod.RawSignature;

        var funcIdx = line.IndexOf($"func {extMethod.MethodName}", StringComparison.Ordinal);
        if (funcIdx < 0)
            return null;

        var parenStart = line.IndexOf('(', funcIdx);
        if (parenStart < 0)
            return null;

        int depth = 0;
        int parenEnd = parenStart;
        for (int i = parenStart; i < line.Length; i++)
        {
            if (line[i] == '(') depth++;
            if (line[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    parenEnd = i;
                    break;
                }
            }
        }

        var paramStr = line.Substring(parenStart + 1, parenEnd - parenStart - 1);

        var parameters = new List<(string label, TypeSpec typeSpec, string swiftType, bool hasDefault)>();
        if (!string.IsNullOrWhiteSpace(paramStr))
        {
            var parts = SplitParameters(paramStr);
            foreach (var part in parts)
            {
                var parsed = ParseParameter(part.Trim(), out var isVariadic);
                if (parsed == null)
                {
                    if (isVariadic)
                    {
                        logger.LogDebug("Skipping foreign extension method {Method}: variadic parameter '{Param}' is not supported on this raw-text extension path",
                            extMethod.MethodName, part.Trim());
                    }
                    else
                    {
                        logger.LogDebug("Skipping foreign extension method {Method}: could not parse parameter '{Param}'",
                            extMethod.MethodName, part.Trim());
                    }
                    return null;
                }
                parameters.Add(parsed.Value);
            }
        }

        // Parse return type
        TypeSpec? returnTypeSpec = null;
        string returnTypeName = "void";

        var afterParen = line.Substring(parenEnd + 1).Trim();
        var braceIdx = afterParen.IndexOf('{');
        if (braceIdx >= 0)
            afterParen = afterParen.Substring(0, braceIdx).Trim();

        var arrowIdx = SwiftTypeListText.IndexOfTopLevelArrow(afterParen);
        if (arrowIdx >= 0)
        {
            var returnTypeStr = afterParen.Substring(arrowIdx + 2).Trim();
            try
            {
                // ParsePrefix, not the EOF-strict Parse: this slice is everything after the
                // top-level "->" and can legitimately carry a trailing method-level "where" clause
                // (e.g. "T where T: Equatable"). The return type IS the leading prefix; the where-tail
                // is not trailing garbage. Unlike the protocol-extension path there is no upstream
                // where-clause gate here, so using the strict Parse would skip such a method that the
                // old lenient parse emitted — a behavior change the prefix variant deliberately avoids.
                returnTypeSpec = TypeSpecParser.ParsePrefix(returnTypeStr);
            }
            catch
            {
                logger.LogDebug("Skipping foreign extension method {Method}: TypeSpecParser error for return type '{Type}'",
                    extMethod.MethodName, returnTypeStr);
                return null;
            }
            if (returnTypeSpec == null)
            {
                logger.LogDebug("Skipping foreign extension method {Method}: could not parse return type '{Type}'",
                    extMethod.MethodName, returnTypeStr);
                return null;
            }
            returnTypeName = returnTypeStr;
        }

        return (parameters, returnTypeSpec, returnTypeName);
    }

    /// <summary>
    /// Parses a single parameter, including default value detection.
    /// </summary>
    private static (string label, TypeSpec typeSpec, string swiftType, bool hasDefault)?
        ParseParameter(string paramDecl, out bool isVariadic)
    {
        isVariadic = false;

        var colonIdx = paramDecl.IndexOf(':');
        if (colonIdx < 0)
            return null;

        var beforeColon = paramDecl.Substring(0, colonIdx).Trim();
        var afterColon = paramDecl.Substring(colonIdx + 1).Trim();

        afterColon = StripSwiftAttributes(afterColon);

        // A variadic parameter (`UIView...`) has no ABI-JSON-derived MethodDecl/TypeSpec fact to
        // check on this raw-text-parsing emitter (unlike the ABI-JSON path's
        // TypeSpec.IsVariadic) — and TypeSpecParser, which this method hands the type text to
        // below, does not recognize "..." as syntax: it silently folds the ellipsis into the
        // type NAME token, corrupting Module/NameWithoutModule downstream and eventually
        // producing an illegal cast in the emitted wrapper. Detect the trailing marker before it
        // ever reaches the parser and decline the member outright — this text-based path has no
        // facility to reconstruct the array-splat call shape the ABI-JSON path uses, so a
        // precise skip is correct here, not a partial or corrupted emit.
        if (afterColon.TrimEnd().EndsWith("...", StringComparison.Ordinal))
        {
            isVariadic = true;
            return null;
        }

        // Detect and remove default value
        bool hasDefault = false;
        var defaultIdx = FindDefaultValueStart(afterColon);
        if (defaultIdx >= 0)
        {
            hasDefault = true;
            afterColon = afterColon.Substring(0, defaultIdx).Trim();
        }

        if (string.IsNullOrWhiteSpace(afterColon))
            return null;

        var words = beforeColon.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var label = words.Length > 0 ? words[0] : "_";

        TypeSpec? typeSpec;
        try
        {
            typeSpec = TypeSpecParser.Parse(afterColon);
        }
        catch
        {
            return null;
        }
        if (typeSpec == null)
            return null;

        return (label, typeSpec, afterColon, hasDefault);
    }

    // ==================== String Helpers ====================

    /// <summary>
    /// Strips Swift parameter attributes.
    /// </summary>
    private static string StripSwiftAttributes(string typeStr)
    {
        while (typeStr.StartsWith("@"))
        {
            var spaceIdx = typeStr.IndexOf(' ');
            if (spaceIdx < 0) break;
            typeStr = typeStr.Substring(spaceIdx + 1).TrimStart();
        }
        if (typeStr.StartsWith("inout "))
            typeStr = typeStr.Substring(6).TrimStart();
        return typeStr;
    }

    /// <summary>
    /// Finds the start of a default value in a parameter type string.
    /// </summary>
    private static int FindDefaultValueStart(string typeStr)
    {
        int depth = 0;
        for (int i = 0; i < typeStr.Length; i++)
        {
            char c = typeStr[i];
            if (c == '<' || c == '(' || c == '[') depth++;
            if (c == '>' || c == ')' || c == ']') depth--;
            if (c == '=' && depth == 0 && i > 0 && typeStr[i - 1] == ' ')
                return i - 1;
        }
        return -1;
    }

    /// <summary>
    /// Splits a parameter list string by commas at top-level nesting depth. Delegates to the
    /// shared <see cref="SwiftTypeListText.SplitTopLevelParameters"/> implementation (Finding 49
    /// grammar consolidation), which adds the closure-arrow guard and string-literal tracking
    /// this local clone previously lacked.
    /// </summary>
    private static List<string> SplitParameters(string paramStr)
        => SwiftTypeListText.SplitTopLevelParameters(paramStr);

    private static string FlattenQualifiedName(string qualifiedName)
    {
        return qualifiedName.Replace(".", "_");
    }

    private static string BuildSymbolName(string flatTypeName, string methodName,
        List<(string label, TypeSpec typeSpec, string swiftType, bool hasDefault)> parameters)
    {
        // SBSW_ prefix because foreign-type-extension wrappers are emitted as @_silgen_name
        // (Swift CC P/Invoke). PInvokeEmitHelper enforces SBW_ ↔ Cdecl exclusively, so the
        // SBSW_ prefix is what signals "Swift CC is the legal pairing" for this wrapper kind.
        var baseName = $"SBSW_{flatTypeName}_{methodName}";
        if (parameters.Count > 0)
        {
            var labels = string.Join("_", parameters.Select(p =>
            {
                var label = p.label == "_" ? "" : p.label;
                var typeSpec = p.typeSpec;
                var typeSuffix = typeSpec is NamedTypeSpec named
                    ? named.Name.Substring(named.Name.LastIndexOf('.') + 1)
                    : "";
                return string.IsNullOrEmpty(label) ? typeSuffix : $"{label}{typeSuffix}";
            }));
            baseName += $"_{labels}";
        }
        return baseName;
    }

    private static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToUpperInvariant(name[0]) + name.Substring(1);
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    /// <summary>
    /// Computes the source-local Swift wrapper binding name for each compatible param: keyword-rename
    /// + sanitize the label (or a type-derived name when the label is <c>_</c>) via the canonical
    /// <see cref="CdeclParamMapper.BuildSwiftBindingName"/> core, then reserved-escape it
    /// against the injected synthetics the wrapper adds to the same signature (<c>self_</c>, …) and
    /// its siblings. The method-wrapper signature loop and the call-arg loop MUST index into this one
    /// list so the <c>_ {name}:</c> decls and the body's <c>{name}</c> references stay in lockstep;
    /// recomputing per loop and escaping only one would desync the wrapper, and swiftc would silently
    /// strip it from the dylib (runtime EntryPointNotFoundException).
    /// </summary>
    private static List<string> ComputeForeignExtParamNames(
        List<(string label, TypeSpec typeSpec, string swiftType, bool hasDefault)> parameters)
    {
        // Dedup user-vs-user FIRST: two compatible unlabeled params of the same Swift type both derive
        // the same type-based binding (`func combine(_ a: Int, _ b: Int)` → two `value`), which swiftc
        // rejects as a duplicate binding. Suffix repeats (`value`, `value2`) exactly as the protocol-
        // extension path does — then reserved-escape against the injected synthetics + siblings.
        //
        // The per-param base name is built via CdeclParamMapper.BuildSwiftBindingName with
        // escapeReservedCollision:false (the reserved-collision step runs once below, after
        // dedup, against the full sibling set) rather than this file's own former hand-rolled
        // keyword table — that table covered only a curated subset of Swift keywords (missing
        // e.g. "extension", "associatedtype", "subscript", …), so a param literally named one of
        // the missing keywords (e.g. `func foo(extension: Bool)`) reached the reserved-collision
        // escape below unrenamed — which only escapes a name colliding with an injected synthetic,
        // not a bare keyword — and emitted an invalid `_ extension: Bool` wrapper parameter.
        var names = new List<string>(parameters.Count);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (label, _, swiftType, _) in parameters)
        {
            var rawBaseName = label == "_" ? GetParamNameFromType(swiftType) : label;
            var baseName = CdeclParamMapper.BuildSwiftBindingName(rawBaseName, escapeReservedCollision: false);
            if (seen.TryGetValue(baseName, out var count))
            {
                seen[baseName] = count + 1;
                names.Add($"{baseName}{count + 1}");
            }
            else
            {
                seen[baseName] = 1;
                names.Add(baseName);
            }
        }
        var siblings = new HashSet<string>(names, StringComparer.Ordinal);
        for (int i = 0; i < names.Count; i++)
            names[i] = NameProvider.EscapeReservedSwiftWrapperLabel(
                names[i], CdeclParamMapper.ExcludeSelf(siblings, names[i]));
        return names;
    }

    private static string GetParamNameFromType(string swiftType)
    {
        var dotIdx = swiftType.LastIndexOf('.');
        var typeName = dotIdx >= 0 ? swiftType.Substring(dotIdx + 1) : swiftType;

        if (typeName == "Bool") return "enabled";
        if (typeName is "Int" or "Int32" or "Int64") return "value";
        if (typeName is "Float" or "Double" or "CGFloat") return "value";
        if (typeName == "String") return "str";

        if (typeName.Length > 0)
            return char.ToLowerInvariant(typeName[0]) + typeName.Substring(1);

        return "arg";
    }

    private static ForeignExtensionClassInfo GetOrCreateClassInfo(string foreignTypeQualifiedName, string moduleName, ModuleEmissionContext ctx)
    {
        return ctx.GetOrAddForeignExtClass(foreignTypeQualifiedName, () => new ForeignExtensionClassInfo
        {
            ForeignTypeQualifiedName = foreignTypeQualifiedName,
            ModuleName = moduleName,
        });
    }
}
