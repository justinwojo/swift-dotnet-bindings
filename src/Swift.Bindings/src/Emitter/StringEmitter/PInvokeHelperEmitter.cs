// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// One protocol-conformance entry on a generic type parameter, pre-flattened
/// at <see cref="PInvokeHelperContext.CreateIfGeneric(TypeDecl, ITypeDatabase)"/>
/// time so the emitter can render the right witness-table call site without
/// re-walking the type database for each declaration.
/// </summary>
public sealed record HelperPwtEntry(
    int GenericParamIndex,
    /// <summary>The C# generic parameter name, e.g. "T0" / "TKey".</summary>
    string GenericParamCsName,
    /// <summary>The simple protocol name (e.g. "AnyInterpolatable", "Hashable").</summary>
    string ProtocolName,
    /// <summary>The fully-qualified protocol name (e.g. "Lottie.AnyInterpolatable") — used as the lex sort key per runtime-metadata.md spec.</summary>
    string ProtocolModuleQualifiedName,
    /// <summary>True when the protocol can be projected as a static C# interface (no associated types or Self requirements and present in the TypeDatabase).</summary>
    bool IsResolvable,
    /// <summary>For resolvable conformances: the C# interface name (e.g. "ISwiftHashable", "IDescribable").</summary>
    string? ResolvableInterfaceName,
    /// <summary>For unresolvable conformances: the protocol descriptor symbol (e.g. "$s6Lottie16AnyInterpolatableMp").</summary>
    string? DescriptorSymbol,
    /// <summary>For unresolvable conformances: the dylib path that exports the descriptor.</summary>
    string? LibraryPath);

/// <summary>
/// Context for collecting P/Invoke declarations that need to be emitted in a helper class.
/// Used for generic types where DllImport cannot appear directly inside the generic class (CS7042).
/// </summary>
public class PInvokeHelperContext
{
    /// <summary>
    /// The name of the helper class that will contain the P/Invoke declarations.
    /// </summary>
    public string HelperClassName { get; }

    /// <summary>
    /// The list of generic type parameter names (T0, T1, etc.) from the containing generic type.
    /// </summary>
    public IReadOnlyList<string> GenericTypeParameters { get; }

    /// <summary>
    /// The collected P/Invoke declarations.
    /// </summary>
    public List<PInvokeDeclaration> Declarations { get; } = new();

    /// <summary>
    /// Raw code blocks to emit before P/Invoke declarations (e.g., [UnmanagedCallersOnly] callbacks
    /// for protocol extension closure bridges that can't be in a generic context).
    /// </summary>
    public List<string> RawCodeBlocks { get; } = new();

    /// <summary>
    /// Pre-flattened protocol witness-table entries for the type's generic parameters,
    /// in the order Swift's metadata accessor expects: all metadata first (declaration
    /// order), then all PWTs grouped by generic param then sorted lexicographically by
    /// protocol module-qualified name, per runtime-metadata.md.
    /// </summary>
    public IReadOnlyList<HelperPwtEntry> PwtEntries { get; }

    /// <summary>
    /// True when the type's metadata accessor would require the indirect-buffer ABI
    /// (total of metadata + PWT params exceeds 3, per runtime-metadata.md). Buffer mode
    /// is not implemented yet, so <see cref="TypeMetadataAccessorSkipGate"/> uses this
    /// flag to skip the type entirely — every C# call site we emit (cctor field
    /// initializers, expression-bodied <c>GetTypeMetadata()</c>, allocating-init
    /// metadata, enum case factories, raw-value <c>FromRawValue</c>) calls the accessor
    /// with explicit per-arg parameters, so even a "lazy" deferred call would PAC-trap
    /// the first time the type is touched on arm64e. Audited across the validation
    /// matrix; no current library exceeds 3 metadata/PWT args. Full buffer-mode
    /// emission is tracked as a 0.8.0 follow-up.
    /// </summary>
    public bool ExceedsRegisterArgumentThreshold { get; }

    /// <summary>
    /// True when at least one of the type's generic-parameter conformances cannot be
    /// represented as either a resolvable C# interface OR a runtime descriptor lookup —
    /// e.g. the protocol's record is missing from the type database, or the parser
    /// failed to capture its descriptor symbol. The handler should skip emission with a
    /// diagnostic when set.
    /// </summary>
    public bool HasUnsupportedConstraint { get; }

    /// <summary>
    /// Human-readable reason for <see cref="HasUnsupportedConstraint"/>, used by the
    /// type handler when reporting the skip.
    /// </summary>
    public string? UnsupportedConstraintReason { get; }

    // Tracks (libraryPath, descriptorSymbol) pairs that have already had their cached
    // descriptor + witness-table helpers emitted into <see cref="RawCodeBlocks"/>, so
    // multiple PWT entries that resolve to the same protocol on the same type only
    // produce one descriptor field, one cache, and one accessor method.
    private readonly HashSet<(string libPath, string symbol)> _emittedDynamicHelpers = new();

    /// <summary>
    /// Creates a new P/Invoke helper context for a generic type.
    /// </summary>
    /// <param name="typeName">The name of the containing generic type.</param>
    /// <param name="genericTypeParameters">The generic type parameter names (T0, T1, etc.).</param>
    /// <param name="pwtEntries">Pre-flattened PWT entries (or empty for unconstrained generics).</param>
    /// <param name="exceedsRegisterThreshold">Set when (metadata + PWT) > 3 — handler must skip.</param>
    /// <param name="hasUnsupportedConstraint">Set when a constraint can't be projected at all.</param>
    /// <param name="unsupportedConstraintReason">Human-readable diagnostic for the skip.</param>
    public PInvokeHelperContext(
        string typeName,
        IReadOnlyList<string> genericTypeParameters,
        IReadOnlyList<HelperPwtEntry>? pwtEntries = null,
        bool exceedsRegisterThreshold = false,
        bool hasUnsupportedConstraint = false,
        string? unsupportedConstraintReason = null)
    {
        HelperClassName = $"{typeName}_PInvoke";
        GenericTypeParameters = genericTypeParameters;
        ExceedsRegisterArgumentThreshold = exceedsRegisterThreshold;
        // When the type would require the indirect-buffer ABI, TypeMetadataAccessorSkipGate
        // skips the type entirely, so PwtEntries is unused in that branch. We still clear
        // it here defensively so any unintended downstream consumer sees an empty list
        // instead of a half-populated one.
        PwtEntries = exceedsRegisterThreshold
            ? Array.Empty<HelperPwtEntry>()
            : (pwtEntries ?? Array.Empty<HelperPwtEntry>());
        HasUnsupportedConstraint = hasUnsupportedConstraint;
        UnsupportedConstraintReason = unsupportedConstraintReason;
    }

    /// <summary>
    /// Creates a P/Invoke helper context from a type declaration if it's generic.
    /// Returns null for non-generic types. Pre-flattens any per-parameter protocol
    /// conformances so the metadata-accessor emitter can render the right PWT
    /// arguments — see <see cref="HelperPwtEntry"/>.
    /// </summary>
    /// <param name="typeDecl">The type declaration.</param>
    /// <param name="typeDatabase">
    /// The type database, used to look up each constraint protocol's projection
    /// status (resolvable C# interface vs runtime-only descriptor lookup).
    /// </param>
    /// <returns>A new context for generic types, null otherwise.</returns>
    public static PInvokeHelperContext? CreateIfGeneric(TypeDecl typeDecl, ITypeDatabase typeDatabase)
    {
        if (!typeDecl.IsGeneric)
            return null;

        var typeParams = typeDecl.GenericParameters
            .Select((p, i) => NameProvider.GetCSharpGenericParameterName(p, i))
            .ToList();

        var (entries, exceedsThreshold, unsupportedReason) =
            FlattenConformances(typeDecl, typeParams, typeDatabase);

        // Use qualified name (e.g., "Outer_Inner") to avoid helper class name collisions
        // when deferred helpers from different parent types share the same simple name.
        return new PInvokeHelperContext(
            GetQualifiedTypeName(typeDecl),
            typeParams,
            entries,
            exceedsThreshold,
            unsupportedReason != null,
            unsupportedReason);
    }

    /// <summary>
    /// Backwards-compatible overload that does not consult the type database. Used
    /// only by tests and a few callers that don't need conformance pre-flattening.
    /// New call sites must pass the type database so PWT plumbing works.
    /// </summary>
    public static PInvokeHelperContext? CreateIfGeneric(TypeDecl typeDecl)
    {
        if (!typeDecl.IsGeneric)
            return null;

        var typeParams = typeDecl.GenericParameters
            .Select((p, i) => NameProvider.GetCSharpGenericParameterName(p, i))
            .ToList();

        return new PInvokeHelperContext(GetQualifiedTypeName(typeDecl), typeParams);
    }

    /// <summary>
    /// Pre-flattens per-parameter conformances per the runtime-metadata.md ordering
    /// rule: walk generic params in declaration order, and for each param walk its
    /// conformances sorted lex by ConformanceTarget.ModuleQualifiedName.
    /// </summary>
    private static (IReadOnlyList<HelperPwtEntry> entries, bool exceedsThreshold, string? unsupportedReason)
        FlattenConformances(TypeDecl typeDecl, IReadOnlyList<string> typeParams, ITypeDatabase typeDatabase)
    {
        var entries = new List<HelperPwtEntry>();

        for (int i = 0; i < typeDecl.GenericParameters.Count; i++)
        {
            var gp = typeDecl.GenericParameters[i];
            var csName = typeParams[i];

            // Sort lexicographically by module-qualified protocol name per the doc.
            var orderedConformances = gp.GenericConformances
                .OrderBy(c => c.ConformanceTarget.ModuleQualifiedName, StringComparer.Ordinal)
                .ToList();

            foreach (var conformance in orderedConformances)
            {
                if (conformance.Kind != ConformanceKind.Protocol)
                    continue;

                var target = conformance.ConformanceTarget;

                // Marker protocols have no runtime witness tables and do NOT add an
                // argument to the metadata accessor. Mirrors the existing
                // ExistentialHandler.IsMarkerProtocol filter and the parser-side filter
                // in SwiftABIParser.cs:1247.
                if (IsMarkerProtocol(target.Name))
                    continue;

                // Conformance must be a known protocol in the type database to be
                // emitted as a witness-table arg. We MATCH the legacy filter from
                // <see cref="MetatypeHelperEmitter.GetResolvablePwtParameterCount"/>
                // (and the C# P/Invoke side's
                // <see cref="PInvokeEmitter.HandleProtocolConformance"/>) which
                // silently drops unknown / unprojectable conformances rather than
                // failing the type. Stricter behaviour would regress every
                // constrained-generic type whose constraint is on a Swift stdlib
                // protocol (Hashable, Collection, ...) that the type database
                // doesn't track — Alamofire, GRDB, DifferenceKit, RxSwift all
                // contain these. The 0.7.0 fix only PROMOTES conformances we can
                // emit safely; it does not regress ones we previously skipped.
                if (!typeDatabase.TryGetTypeRecord(target, out var record) ||
                    record.Kind != TypeRecordKind.Protocol)
                {
                    continue;
                }

                bool isResolvable =
                    !record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes) &&
                    !record.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement);

                string? interfaceName = null;
                string? descriptorSymbol = null;
                string? libraryPath = null;

                if (isResolvable)
                {
                    interfaceName = NameProvider.GetInterfaceName(target.Name, moduleName: target.Module);
                }
                else
                {
                    // Self-requirement / associated-type protocols: need a runtime
                    // descriptor lookup. If the parser failed to capture the
                    // descriptor symbol we can't construct the lookup, so fall
                    // back to the legacy silent-drop behaviour and let the call
                    // site continue to omit the PWT — the same way the legacy
                    // MetatypeHelperEmitter.GetResolvablePwtParameterCount path
                    // dropped unknown / unprojectable conformances.
                    if (string.IsNullOrEmpty(record.ProtocolDescriptorSymbol))
                    {
                        continue;
                    }
                    descriptorSymbol = record.ProtocolDescriptorSymbol;
                    libraryPath = typeDatabase.GetLibraryPath(target.Module);
                }

                entries.Add(new HelperPwtEntry(
                    GenericParamIndex: i,
                    GenericParamCsName: csName,
                    ProtocolName: target.Name,
                    ProtocolModuleQualifiedName: target.ModuleQualifiedName,
                    IsResolvable: isResolvable,
                    ResolvableInterfaceName: interfaceName,
                    DescriptorSymbol: descriptorSymbol,
                    LibraryPath: libraryPath));
            }
        }

        // runtime-metadata.md: when (num_metadata + num_pwts) > 3, the metadata
        // accessor signature switches to the indirect-buffer ABI. Buffer mode is
        // not implemented yet (Option B in the design doc) — flag the type so the
        // handler can skip emission with a diagnostic. Audited across the
        // validation matrix; no current library exceeds 3 metadata/PWT args.
        bool exceedsThreshold = (typeParams.Count + entries.Count) > 3;

        return (entries, exceedsThreshold, null);
    }

    /// <summary>
    /// Marker protocols (Sendable, Copyable, Escapable, BitwiseCopyable, etc.) have
    /// no runtime witness table — the Swift compiler does not pass them as PWT args
    /// to the type metadata accessor. Aligned with
    /// <c>ExistentialHandler.IsMarkerProtocol</c> and the parser-side filter in
    /// <c>SwiftABIParser</c>.
    /// </summary>
    private static bool IsMarkerProtocol(string simpleName) =>
        simpleName is "Sendable" or "Escapable" or "Copyable"
                   or "SendableMetatype" or "BitwiseCopyable";

    /// <summary>
    /// Builds a qualified type name by walking the parent type chain.
    /// For nested types, produces "Parent_Child" to ensure unique helper class names
    /// when multiple nested types with the same simple name exist under different parents.
    /// </summary>
    private static string GetQualifiedTypeName(TypeDecl typeDecl)
    {
        var parts = new List<string>();
        BaseDecl? current = typeDecl;
        while (current is TypeDecl td)
        {
            parts.Add(NameProvider.ToPascalCaseForTypeName(td.Name));
            current = td.ParentDecl;
        }

        if (parts.Count <= 1)
            return NameProvider.ToPascalCaseForTypeName(typeDecl.Name);

        parts.Reverse();
        return string.Join("_", parts);
    }

    /// <summary>
    /// Adds a P/Invoke declaration to the context.
    /// </summary>
    /// <param name="declaration">The P/Invoke declaration to add.</param>
    public void AddDeclaration(PInvokeDeclaration declaration)
    {
        // Deduplicate by method name to avoid duplicate P/Invoke declarations
        // (e.g., multiple failable inits in the same type share PInvokesForSwiftOptional_MetadataAccessor)
        if (Declarations.Any(d => d.MethodName == declaration.MethodName))
            return;
        Declarations.Add(declaration);
    }

    /// <summary>
    /// Gets the additional metadata parameters needed for P/Invoke declarations in a generic type.
    /// Uses IntPtr instead of TypeMetadata to avoid Mono JIT crashes when combined with
    /// SwiftSelf in CallConvSwift P/Invokes (jit-info.c:918 assertion). TypeMetadata is a
    /// single-field struct wrapping IntPtr, so IntPtr is ABI-compatible.
    /// </summary>
    /// <returns>A list of parameter strings like "IntPtr t0Metadata".</returns>
    public IReadOnlyList<string> GetMetadataParameterDeclarations()
    {
        return GenericTypeParameters
            .Select(t => $"IntPtr {t.ToLowerInvariant()}Metadata")
            .ToList();
    }

    /// <summary>
    /// Gets the argument list for passing type metadata to the helper class methods.
    /// Passes TypeMetadata.Handle (IntPtr) to match the IntPtr parameter type.
    /// </summary>
    /// <returns>A list of argument strings like "SwiftObjectHelper&lt;T0&gt;.GetTypeMetadata().Handle".</returns>
    public IReadOnlyList<string> GetMetadataArgumentList()
    {
        return GenericTypeParameters
            .Select(t => $"SwiftObjectHelper<{t}>.GetTypeMetadata().Handle")
            .ToList();
    }

    /// <summary>
    /// Returns a per-entry suffix that disambiguates same-named protocols from
    /// different modules within this type's PWT list. Empty when the entry's
    /// <see cref="HelperPwtEntry.ProtocolName"/> is unique among <see cref="PwtEntries"/>;
    /// otherwise an <c>_{8-char-hash}</c> derived from the module-qualified name. The
    /// hash makes <c>A.Syncable</c> and <c>B.Syncable</c> emit distinct identifiers
    /// (parameter names, helper field/method names, descriptor caches) so the
    /// generated C# compiles. The simple-name-only spelling is preserved for the
    /// common case (no collision) so existing baselines and snapshots are stable.
    /// </summary>
    private string GetProtocolNameDiscriminator(HelperPwtEntry entry)
    {
        // Quick scan: only one entry with this simple name → no discriminator needed.
        int sameNameCount = 0;
        foreach (var other in PwtEntries)
        {
            if (other.ProtocolName == entry.ProtocolName)
                sameNameCount++;
            if (sameNameCount > 1) break;
        }
        if (sameNameCount <= 1)
            return string.Empty;

        return "_" + EmitterUtility.DeterministicHash8(entry.ProtocolModuleQualifiedName);
    }

    /// <summary>
    /// Returns the parameter declarations for the **type metadata accessor** P/Invoke
    /// — metadata args for every generic parameter (declaration order) followed by one
    /// PWT arg per resolvable protocol conformance, sorted per
    /// runtime-metadata.md (lex by module-qualified protocol name within each generic
    /// param). All params are <c>IntPtr</c> to avoid the Mono CallConvSwift JIT crash
    /// (jit-info.c:918) and to keep one shared signature shape.
    /// </summary>
    public IReadOnlyList<string> GetTypeMetadataAccessorParameterDeclarations()
    {
        var parameters = new List<string>();

        foreach (var t in GenericTypeParameters)
            parameters.Add($"IntPtr {t.ToLowerInvariant()}Metadata");

        foreach (var entry in PwtEntries)
        {
            var discriminator = GetProtocolNameDiscriminator(entry);
            var pwtName = $"{entry.GenericParamCsName.ToLowerInvariant()}{entry.ProtocolName}{discriminator}PWT";
            parameters.Add($"IntPtr {pwtName}");
        }

        return parameters;
    }

    /// <summary>
    /// Returns the argument list for the **type metadata accessor** P/Invoke call site,
    /// in the order matching <see cref="GetTypeMetadataAccessorParameterDeclarations"/>.
    /// For each PWT entry the expression is one of:
    /// <list type="bullet">
    /// <item>Resolvable: <c>ProtocolWitnessTable.GetOrThrowAuto&lt;T0, IDescribable&gt;().Handle</c></item>
    /// <item>Unresolvable: <c>{HelperClassName}.Get{Protocol}PWT(SwiftObjectHelper&lt;T0&gt;.GetTypeMetadata()).Handle</c></item>
    /// </list>
    /// </summary>
    public IReadOnlyList<string> GetTypeMetadataAccessorArgumentList()
    {
        var args = new List<string>();

        foreach (var t in GenericTypeParameters)
            args.Add($"SwiftObjectHelper<{t}>.GetTypeMetadata().Handle");

        foreach (var entry in PwtEntries)
        {
            if (entry.IsResolvable)
            {
                args.Add(
                    $"ProtocolWitnessTable.GetOrThrowAuto<{entry.GenericParamCsName}, {entry.ResolvableInterfaceName}>().Handle");
            }
            else
            {
                EmitDynamicPwtHelperIfNeeded(entry);
                var discriminator = GetProtocolNameDiscriminator(entry);
                var helperMethodName = $"Get{entry.ProtocolName}{discriminator}PWT";
                args.Add(
                    $"{HelperClassName}.{helperMethodName}(SwiftObjectHelper<{entry.GenericParamCsName}>.GetTypeMetadata()).Handle");
            }
        }

        return args;
    }

    /// <summary>
    /// Emits the cached descriptor + witness-table accessor helpers for one
    /// unresolvable conformance into <see cref="RawCodeBlocks"/>, deduplicated by
    /// (library path, descriptor symbol). The helpers live on the helper P/Invoke
    /// class (which is non-generic to satisfy CS7042) and take the type metadata
    /// as an explicit parameter — the call site (which IS inside the generic type)
    /// supplies <c>SwiftObjectHelper&lt;T&gt;.GetTypeMetadata()</c>. Caching is keyed
    /// by metadata handle so a single helper serves all generic instantiations.
    ///
    /// Mirrors the canonical descriptor + witness-table caching pattern in
    /// <c>SwiftResult.cs:521-544</c> — per-call-site <c>LoadFromSymbol</c> would do
    /// dlopen/dlsym/dlclose on every metadata access, which is unacceptable for hot
    /// paths.
    /// </summary>
    private void EmitDynamicPwtHelperIfNeeded(HelperPwtEntry entry)
    {
        if (entry.IsResolvable || entry.DescriptorSymbol == null || entry.LibraryPath == null)
            return;

        var key = (entry.LibraryPath, entry.DescriptorSymbol);
        if (!_emittedDynamicHelpers.Add(key))
            return;

        // When two same-named protocols from different modules appear on the
        // same type (e.g. T: A.Syncable, U: B.Syncable), the simple-name spelling
        // collides. The discriminator is an empty string in the common case
        // (one protocol with that simple name) and an _<8-char-hash> suffix
        // when more than one entry shares the simple name.
        var discriminator = GetProtocolNameDiscriminator(entry);
        var camelProtocol = char.ToLowerInvariant(entry.ProtocolName[0]) + entry.ProtocolName.Substring(1);
        var descriptorFieldName = $"_{camelProtocol}{discriminator}Descriptor";
        var cacheFieldName = $"_{camelProtocol}{discriminator}WitnessTableCache";
        var helperMethodName = $"Get{entry.ProtocolName}{discriminator}PWT";

        // Helper method must be `internal` (not `private`) so the call site
        // — which lives in the outer generic type class, NOT in the helper
        // P/Invoke class — can call it. The helper class itself is already
        // internal, so widening from private to internal does not expose it
        // beyond the assembly.
        var block = $$"""
            private static readonly global::System.Lazy<global::Swift.Runtime.ProtocolDescriptor> {{descriptorFieldName}} =
                new global::System.Lazy<global::Swift.Runtime.ProtocolDescriptor>(() =>
                    global::Swift.Runtime.ProtocolDescriptor.LoadFromSymbol(
                        "{{entry.LibraryPath}}",
                        "{{entry.DescriptorSymbol}}"));

            private static readonly global::System.Collections.Concurrent.ConcurrentDictionary<global::System.IntPtr, global::Swift.Runtime.ProtocolWitnessTable> {{cacheFieldName}} =
                new global::System.Collections.Concurrent.ConcurrentDictionary<global::System.IntPtr, global::Swift.Runtime.ProtocolWitnessTable>();

            internal static global::Swift.Runtime.ProtocolWitnessTable {{helperMethodName}}(global::Swift.Runtime.TypeMetadata typeMetadata) =>
                {{cacheFieldName}}.GetOrAdd(typeMetadata.Handle, _ =>
                    global::Swift.Runtime.SwiftConformance.GetWitnessTableOrThrow(typeMetadata, {{descriptorFieldName}}.Value));
            """;

        RawCodeBlocks.Add(block);
    }

    /// <summary>
    /// Emits the helper class with all collected P/Invoke declarations.
    /// </summary>
    /// <param name="csWriter">The C# code writer.</param>
    public void EmitHelperClass(CSharpWriter csWriter)
    {
        if (Declarations.Count == 0 && RawCodeBlocks.Count == 0)
            return;

        csWriter.WriteLine($"internal static partial class {HelperClassName}");
        csWriter.WriteLine("{");
        csWriter.Indent++;

        // Emit raw code blocks (e.g., [UnmanagedCallersOnly] callbacks for closure bridges)
        foreach (var block in RawCodeBlocks)
        {
            foreach (var line in block.Split('\n'))
            {
                csWriter.WriteLine(line);
            }
            csWriter.WriteLine();
        }

        foreach (var decl in Declarations)
        {
            decl.Emit(csWriter);
            csWriter.WriteLine();
        }

        csWriter.Indent--;
        csWriter.WriteLine("}");
        csWriter.WriteLine();
    }
}

/// <summary>
/// Represents a P/Invoke declaration that will be emitted in a helper class.
/// </summary>
public class PInvokeDeclaration
{
    /// <summary>
    /// The library path for DllImport.
    /// </summary>
    public required string LibraryPath { get; init; }

    /// <summary>
    /// The entry point (mangled Swift symbol name).
    /// </summary>
    public required string EntryPoint { get; init; }

    /// <summary>
    /// The P/Invoke method name.
    /// </summary>
    public required string MethodName { get; init; }

    /// <summary>
    /// The return type of the P/Invoke method.
    /// </summary>
    public required string ReturnType { get; init; }

    /// <summary>
    /// The parameter list string for the P/Invoke method.
    /// </summary>
    public required string ParametersString { get; init; }

    /// <summary>
    /// Whether this P/Invoke is for an async method (always returns void).
    /// </summary>
    public bool IsAsync { get; init; }

    /// <summary>
    /// Additional TypeMetadata parameters for generic type support.
    /// </summary>
    public IReadOnlyList<string>? MetadataParameters { get; init; }

    /// <summary>
    /// The calling convention for this P/Invoke declaration.
    /// Defaults to Cdecl for backward compatibility (most helper P/Invokes target @_cdecl symbols).
    /// Set explicitly when the P/Invoke targets a @_silgen_name wrapper (Swift convention).
    /// </summary>
    public PInvokeCallingConvention CallingConvention { get; init; } = PInvokeCallingConvention.Cdecl;

    /// <summary>
    /// When set, uses <c>private</c> instead of <c>internal</c> visibility.
    /// </summary>
    public bool UsePrivateVisibility { get; init; }

    /// <summary>
    /// Emits the P/Invoke declaration.
    /// </summary>
    /// <param name="csWriter">The C# code writer.</param>
    public void Emit(CSharpWriter csWriter)
    {
        PInvokeEmitHelper.EmitDeclaration(csWriter, new PInvokeEmissionInfo
        {
            LibraryPath = LibraryPath,
            EntryPoint = EntryPoint,
            MethodName = MethodName,
            ReturnType = ReturnType,
            ParametersString = ParametersString,
            CallingConvention = CallingConvention,
            Visibility = UsePrivateVisibility ? PInvokeVisibility.Private : PInvokeVisibility.Internal,
            IsAsync = IsAsync,
            MetadataParameters = MetadataParameters
        });
    }
}
