// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using Microsoft.Extensions.Logging;
using Swift.Runtime;

namespace BindingsGeneration.AppleTypesManifest;

// Emits C# source files for SwiftBindings.Apple from an AppleTypesManifestModel.
//
// Default path: VWT-backed opaque storage. Modeled on the hand-rolled
// `Swift.Runtime/src/Swift/URL.cs` — `sealed class` + `SwiftSafeHandle<T>` + metadata
// accessor P/Invoke + `NativeMemory.Alloc`-backed heap copy in NewFromPayload, with
// Dispose flowing through the safe handle.
//
// Gated path: `[StructLayout(LayoutKind.Sequential)]` emission for frozen trivially-
// copyable types that pass the whitelist gate. See `SequentialLayoutWhitelist.cs`
// and the gate below; refused unless size + alignment have been validated against
// the live SDK via the live-SDK validation pass.
//
// Determinism: the emitter sorts modules + entries and produces byte-identical
// output for the same input. Filenames are `<Module>/<DeclPath joined by '.'>.cs`.
public sealed class AppleTypesCsEmitter
{
    private readonly SequentialLayoutWhitelist _whitelist;
    private readonly ILogger _logger;

    // Files we emitted during this run. Tracked so idempotent regeneration can
    // delete stale files from previous manifest revisions (but only under an
    // explicit "clean" policy owned by the caller, not the emitter).
    public IReadOnlyList<string> EmittedFiles => _emittedFiles;
    private readonly List<string> _emittedFiles = new();

    // Benign skips — entries intentionally omitted because another component owns
    // them (e.g. TypeOwnerRegistry routes `Foundation.Date` to Swift.Runtime). These
    // are NOT errors; the CLI reports their count but does not fail on them.
    public IReadOnlyList<SkippedEntry> SkippedEntries => _skipped;
    private readonly List<SkippedEntry> _skipped = new();

    // Structural skips — entries the emitter refused to emit because the manifest
    // itself is malformed (blank metadata accessor symbol/library, missing accessor,
    // …). The type is silently dropped to keep the run producing partial output for
    // diagnosis, but these MUST fail the command: shipping a manifest that drops
    // types at emit time is exactly the fail-closed case guarded here.
    public IReadOnlyList<SkippedEntry> StructuralSkips => _structuralSkips;
    private readonly List<SkippedEntry> _structuralSkips = new();

    // Whitelist opt-ins that failed the evidence/structural gate. These entries ARE
    // still emitted (via the safe VWT-opaque fallback) so consumers don't lose the
    // type entirely — but the list is non-empty iff the manifest is shipping a
    // broken opt-in, and the CLI must translate that into a non-zero exit so the
    // regression cannot slip into a release build.
    public IReadOnlyList<RefusedWhitelistEntry> RefusedWhitelistEntries => _refused;
    private readonly List<RefusedWhitelistEntry> _refused = new();

    public sealed record SkippedEntry(string SwiftIdentity, string Reason);
    public sealed record RefusedWhitelistEntry(string SwiftIdentity, string Reason);

    public AppleTypesCsEmitter(SequentialLayoutWhitelist whitelist, ILogger logger)
    {
        _whitelist = whitelist;
        _logger = logger;
    }

    public void Emit(Manifest manifest, string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        foreach (var (moduleName, module) in manifest.Modules)
        {
            foreach (var entry in module.Types.OrderBy(t => t.SwiftIdentity, StringComparer.Ordinal))
            {
                try
                {
                    EmitEntry(moduleName, entry, outputDir);
                }
                catch (EmitterGateException ex)
                {
                    // A true structural failure (blank metadata accessor, malformed
                    // manifest) — skip the type and record to StructuralSkips so the
                    // CLI fails. Distinct from benign _skipped (Runtime-owned) and
                    // distinct from the whitelist refusal flow in EmitEntry which
                    // emits VWT-opaque instead of skipping.
                    _logger.LogError(
                        "Refusing to emit '{Identity}': {Reason}",
                        entry.SwiftIdentity, ex.Message);
                    _structuralSkips.Add(new SkippedEntry(entry.SwiftIdentity, ex.Message));
                }
            }
        }

        EmitFrameworkResolverRegistration(outputDir);
    }

    // Emits a single file with a `[ModuleInitializer]` that registers
    // `SwiftFrameworkResolver` for the supplement assembly. Without this, bare-name
    // DllImports ("CryptoKit", "ManagedSettings", …) would fail to load because the
    // .NET default resolver can't find them — there's no `.framework/` in the path
    // for the macios linker to expand at build time, and no rpath covers
    // `/System/Library/Frameworks/` by default.
    private void EmitFrameworkResolverRegistration(string outputDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("//   Generated by Swift.Bindings --emit-apple-types-cs.");
        sb.AppendLine("//   Registers SwiftFrameworkResolver so bare library names in");
        sb.AppendLine("//   supplement DllImports (CryptoKit, ManagedSettings, …) resolve to");
        sb.AppendLine("//   system-framework paths at runtime. Bare names are used to avoid");
        sb.AppendLine("//   the macios linker's `.framework/` substring scan from force-adding");
        sb.AppendLine("//   `-framework X` for unused modules (BlastRadius FINDINGS #9).");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using Swift.Runtime;");
        sb.AppendLine();
        sb.AppendLine("namespace Swift;");
        sb.AppendLine();
        sb.AppendLine("internal static class AppleSupplementRegistration");
        sb.AppendLine("{");
        sb.AppendLine("#pragma warning disable CA2255 // ModuleInitializer is intentional — supplement needs self-registration");
        sb.AppendLine("    [ModuleInitializer]");
        sb.AppendLine("#pragma warning restore CA2255");
        sb.AppendLine("    internal static void Initialize()");
        sb.AppendLine("        => SwiftFrameworkResolver.RegisterForAssembly(typeof(AppleSupplementRegistration).Assembly);");
        sb.AppendLine("}");

        var outputPath = Path.Combine(outputDir, "_AppleSupplementRegistration.cs");
        File.WriteAllText(outputPath, sb.ToString());
        _emittedFiles.Add(outputPath);
    }

    private void EmitEntry(string moduleName, TypeEntry entry, string outputDir)
    {
        // TypeOwnerRegistry guard: the supplement must not shadow Runtime-owned canonicals
        // (`Foundation.Date`, `Foundation.URL`, …). The registry is authoritative; if the
        // resolver says Runtime owns a type, skip it regardless of the manifest.
        var owner = TypeOwnerRegistry.Resolve(entry.SwiftIdentity);
        if (owner.Kind == TypeOwnerKind.Runtime)
        {
            _logger.LogInformation(
                "Skipping '{Identity}': owned by SwiftBindings.Runtime per TypeOwnerRegistry.",
                entry.SwiftIdentity);
            _skipped.Add(new SkippedEntry(entry.SwiftIdentity, "owned by SwiftBindings.Runtime"));
            return;
        }

        if (entry.MetadataAccessor is null)
            throw new EmitterGateException($"no metadata_accessor in manifest entry");

        // Defense in depth against a manifest that survived schema validation (e.g. hand-
        // edited, or produced by an older builder without the fail-loud check). Emitting
        // a [DllImport(..., EntryPoint = "")] would bind to an arbitrary exported symbol
        // at load time — silent, undetectable runtime corruption.
        if (string.IsNullOrWhiteSpace(entry.MetadataAccessor.Symbol))
            throw new EmitterGateException(
                $"blank metadata_accessor.symbol for '{entry.SwiftIdentity}' — " +
                "manifest is malformed. Regenerate via apple-types-manifest and re-validate.");
        if (string.IsNullOrWhiteSpace(entry.MetadataAccessor.Library))
            throw new EmitterGateException(
                $"blank metadata_accessor.library for '{entry.SwiftIdentity}' — " +
                "manifest is malformed. Regenerate via apple-types-manifest and re-validate.");

        var useSequential = ShouldUseSequentialLayout(entry);

        var source = useSequential
            ? EmitSequentialLayout(moduleName, entry)
            : EmitVwtOpaque(moduleName, entry);

        var relativeDir = moduleName;
        var fileName = string.Join('.', entry.ManagedProjection.DeclarationPath) + ".cs";
        var moduleDir = Path.Combine(outputDir, relativeDir);
        Directory.CreateDirectory(moduleDir);
        var outputPath = Path.Combine(moduleDir, fileName);

        File.WriteAllText(outputPath, source);
        _emittedFiles.Add(outputPath);
    }

    // The whitelist gate. ALL conditions must hold:
    //   1. `storage_strategy == "sequential"` OR `sequential_layout_whitelisted == true`
    //      in the manifest (either one counts as a request for the sequential path).
    //   2. Swift identity is in the external whitelist file.
    //   3. frozen=true.
    //   4. non-generic (`<` absent from identity).
    //   5. size + alignment both non-null (validated against live SDK).
    //   6. `sequential_layout_evidence` present, with:
    //      - `stored_fields_known` true,
    //      - `copy_destroy_handling` one of "trivial" / "explicit_vwt",
    //      - `roundtrip_validated` true.
    //
    // Refusal semantics: if the manifest *requested* the sequential path but any gate
    // fails, we DO NOT throw and we DO NOT silently ship the sequential layout. Instead
    // we log an error, add the identity to `_refused`, and fall back to the VWT-opaque
    // emission so consumers never lose the type entirely. The CLI then turns a non-empty
    // `_refused` list into a non-zero exit — the whitelist opt-in has to travel with its
    // evidence or the build fails.
    private bool ShouldUseSequentialLayout(TypeEntry entry)
    {
        var requestedSequential =
            string.Equals(entry.StorageStrategy, "sequential", StringComparison.Ordinal)
            || entry.SequentialLayoutWhitelisted;

        if (!requestedSequential)
            return false;

        string? refusalReason = null;

        if (!_whitelist.Contains(entry.SwiftIdentity))
            refusalReason =
                "sequential_layout_whitelisted=true (or storage_strategy=\"sequential\") " +
                "but identity is not in the external sequential-layout-whitelist.json.";
        else if (!entry.Frozen)
            refusalReason = "frozen=false; sequential layout is only permitted for frozen types.";
        else if (entry.SwiftIdentity.Contains('<'))
            refusalReason = "type appears generic; the whitelist path does not support generics.";
        else if (entry.Size is null || entry.Alignment is null)
            refusalReason =
                "size/alignment are null in the manifest; the sequential path requires both " +
                "to be validated against the live Apple SDK.";
        else if (entry.SequentialLayoutEvidence is null)
            refusalReason =
                "sequential_layout_whitelisted=true but sequential_layout_evidence is missing. " +
                "Evidence (stored_fields_known, copy_destroy_handling, roundtrip_validated) " +
                "must travel with the whitelist opt-in.";
        else if (!entry.SequentialLayoutEvidence.StoredFieldsKnown)
            refusalReason = "sequential_layout_evidence.stored_fields_known=false.";
        else if (entry.SequentialLayoutEvidence.CopyDestroyHandling is not ("trivial" or "explicit_vwt"))
            refusalReason =
                $"sequential_layout_evidence.copy_destroy_handling=" +
                $"'{entry.SequentialLayoutEvidence.CopyDestroyHandling}' " +
                "must be one of \"trivial\" or \"explicit_vwt\".";
        else if (!entry.SequentialLayoutEvidence.RoundtripValidated)
            refusalReason = "sequential_layout_evidence.roundtrip_validated=false.";

        if (refusalReason is null)
            return true;

        _logger.LogError(
            "Refused sequential-layout opt-in for '{Identity}' — falling back to VWT-opaque: {Reason}",
            entry.SwiftIdentity, refusalReason);
        _refused.Add(new RefusedWhitelistEntry(entry.SwiftIdentity, refusalReason));
        return false;
    }

    // ---------- VWT-backed opaque path (default) ----------

    private static string EmitVwtOpaque(string moduleName, TypeEntry entry)
    {
        var ns = entry.ManagedProjection.Namespace;
        var path = entry.ManagedProjection.DeclarationPath;
        var leaf = path[^1];
        var outer = path.Take(path.Count - 1).ToList();
        var accessor = entry.MetadataAccessor!;
        var libraryPath = ResolveLibraryPath(accessor.Library);

        var sb = new StringBuilder();
        AppendFileHeader(sb, entry);
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using System.Runtime.InteropServices;");
        sb.AppendLine("using System.Runtime.Versioning;");
        sb.AppendLine("using Swift.Runtime;");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();

        var indent = 0;
        // Emit outer partial struct wrappers. The outer types in the supplement are pure
        // namespace-like containers — they never project a real Swift value themselves.
        foreach (var segment in outer)
        {
            AppendAvailabilityAttributes(sb, accessor.Availability, indent);
            sb.Append(new string(' ', indent * 4));
            sb.AppendLine($"public readonly partial struct {segment}");
            sb.Append(new string(' ', indent * 4));
            sb.AppendLine("{");
            indent++;
        }

        AppendAvailabilityAttributes(sb, accessor.Availability, indent);
        var pad = new string(' ', indent * 4);
        sb.Append(pad); sb.AppendLine($"public sealed partial class {leaf} : ISwiftObject, ISwiftStruct, IDisposable");
        sb.Append(pad); sb.AppendLine("{");
        var bodyPad = new string(' ', (indent + 1) * 4);
        sb.Append(bodyPad); sb.AppendLine($"private SwiftSafeHandle<{leaf}> _payload = SwiftSafeHandle<{leaf}>.Zero;");
        sb.Append(bodyPad); sb.AppendLine("private bool _disposed;");
        sb.Append(bodyPad); sb.AppendLine("private static TypeMetadata? _cachedMetadata;");
        sb.AppendLine();
        sb.Append(bodyPad); sb.AppendLine($"public SwiftSafeHandle<{leaf}> Payload => _payload;");
        sb.AppendLine();
        sb.Append(bodyPad); sb.AppendLine("IntPtr ISwiftObject.SwiftHandle => _payload.DangerousGetHandle();");
        sb.Append(bodyPad); sb.AppendLine("void ISwiftObject.SuppressPayloadFinalizer() => global::System.GC.SuppressFinalize(_payload);");
        sb.AppendLine();
        sb.Append(bodyPad); sb.AppendLine("static TypeMetadata ISwiftObject.GetTypeMetadata()");
        sb.Append(bodyPad); sb.AppendLine("    => _cachedMetadata ??= PInvoke_GetMetadata();");
        sb.AppendLine();
        sb.Append(bodyPad); sb.AppendLine("static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)");
        sb.Append(bodyPad); sb.AppendLine("{");
        sb.Append(bodyPad); sb.AppendLine("    var metadata = _cachedMetadata ??= PInvoke_GetMetadata();");
        sb.Append(bodyPad); sb.AppendLine("    unsafe");
        sb.Append(bodyPad); sb.AppendLine("    {");
        sb.Append(bodyPad); sb.AppendLine("        var size = (int)metadata.Size;");
        sb.Append(bodyPad); sb.AppendLine("        var heapCopy = NativeMemory.Alloc((nuint)size);");
        sb.Append(bodyPad); sb.AppendLine("        metadata.ValueWitnessTable->InitializeWithCopy(heapCopy, (void*)handle, metadata);");
        sb.Append(bodyPad); sb.AppendLine($"        return new {leaf}((IntPtr)heapCopy);");
        sb.Append(bodyPad); sb.AppendLine("    }");
        sb.Append(bodyPad); sb.AppendLine("}");
        sb.AppendLine();
        // VWT-opaque supplement types Alloc + InitializeWithCopy a fresh +1 (Copy semantics).
        sb.Append(bodyPad); sb.AppendLine("static global::Swift.Runtime.PayloadConstructionSemantics ISwiftObject.PayloadConstructionSemantics => global::Swift.Runtime.PayloadConstructionSemantics.Copy;");
        sb.AppendLine();
        sb.Append(bodyPad); sb.AppendLine("int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)");
        sb.Append(bodyPad); sb.AppendLine("{");
        sb.Append(bodyPad); sb.AppendLine("    var metadata = _cachedMetadata ??= PInvoke_GetMetadata();");
        sb.Append(bodyPad); sb.AppendLine("    if ((int)metadata.Size > swiftDestSpan.Length)");
        sb.Append(bodyPad); sb.AppendLine("        throw new ArgumentException($\"Span size mismatch: expected {(int)metadata.Size}, got {swiftDestSpan.Length}\");");
        sb.Append(bodyPad); sb.AppendLine("    unsafe");
        sb.Append(bodyPad); sb.AppendLine("    {");
        sb.Append(bodyPad); sb.AppendLine("        fixed (void* dest = swiftDestSpan)");
        sb.Append(bodyPad); sb.AppendLine("        {");
        sb.Append(bodyPad); sb.AppendLine("            bool success = false;");
        sb.Append(bodyPad); sb.AppendLine("            _payload.DangerousAddRef(ref success);");
        sb.Append(bodyPad); sb.AppendLine("            try");
        sb.Append(bodyPad); sb.AppendLine("            {");
        sb.Append(bodyPad); sb.AppendLine("                metadata.ValueWitnessTable->InitializeWithCopy(dest, (void*)_payload.DangerousGetHandle(), metadata);");
        sb.Append(bodyPad); sb.AppendLine("                return (int)metadata.Size;");
        sb.Append(bodyPad); sb.AppendLine("            }");
        sb.Append(bodyPad); sb.AppendLine("            finally");
        sb.Append(bodyPad); sb.AppendLine("            {");
        sb.Append(bodyPad); sb.AppendLine("                if (success) _payload.DangerousRelease();");
        sb.Append(bodyPad); sb.AppendLine("            }");
        sb.Append(bodyPad); sb.AppendLine("        }");
        sb.Append(bodyPad); sb.AppendLine("    }");
        sb.Append(bodyPad); sb.AppendLine("}");
        sb.AppendLine();
        sb.Append(bodyPad); sb.AppendLine("static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()");
        sb.Append(bodyPad); sb.AppendLine($"    => throw new SwiftRuntimeException($\"Protocol conformance not implemented for {leaf} and {{typeof(TProtocol).Name}}\");");
        sb.AppendLine();
        sb.Append(bodyPad); sb.AppendLine($"private {leaf}(IntPtr handle) => _payload = new SwiftSafeHandle<{leaf}>(handle);");
        sb.AppendLine();
        sb.Append(bodyPad); sb.AppendLine("[UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]");
        sb.Append(bodyPad); sb.AppendLine($"[DllImport(\"{libraryPath}\", EntryPoint = \"{accessor.Symbol}\")]");
        sb.Append(bodyPad); sb.AppendLine("private static extern TypeMetadata PInvoke_GetMetadata();");
        sb.AppendLine();
        sb.Append(bodyPad); sb.AppendLine("public void Dispose()");
        sb.Append(bodyPad); sb.AppendLine("{");
        sb.Append(bodyPad); sb.AppendLine("    if (!_disposed)");
        sb.Append(bodyPad); sb.AppendLine("    {");
        sb.Append(bodyPad); sb.AppendLine("        _payload.Dispose();");
        sb.Append(bodyPad); sb.AppendLine("        _disposed = true;");
        sb.Append(bodyPad); sb.AppendLine("    }");
        sb.Append(bodyPad); sb.AppendLine("}");
        sb.Append(pad); sb.AppendLine("}");

        for (var i = outer.Count - 1; i >= 0; i--)
        {
            var closePad = new string(' ', i * 4);
            sb.Append(closePad);
            sb.AppendLine("}");
        }

        return sb.ToString();
    }

    // ---------- Sequential-layout path (whitelist-gated) ----------

    private static string EmitSequentialLayout(string moduleName, TypeEntry entry)
    {
        var ns = entry.ManagedProjection.Namespace;
        var path = entry.ManagedProjection.DeclarationPath;
        var leaf = path[^1];
        var outer = path.Take(path.Count - 1).ToList();
        var accessor = entry.MetadataAccessor!;
        var libraryPath = ResolveLibraryPath(accessor.Library);
        var size = entry.Size!.Value;
        var alignment = entry.Alignment!.Value;

        var sb = new StringBuilder();
        AppendFileHeader(sb, entry);
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using System.Runtime.InteropServices;");
        sb.AppendLine("using System.Runtime.Versioning;");
        sb.AppendLine("using Swift.Runtime;");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();

        var indent = 0;
        foreach (var segment in outer)
        {
            AppendAvailabilityAttributes(sb, accessor.Availability, indent);
            sb.Append(new string(' ', indent * 4));
            sb.AppendLine($"public readonly partial struct {segment}");
            sb.Append(new string(' ', indent * 4));
            sb.AppendLine("{");
            indent++;
        }

        AppendAvailabilityAttributes(sb, accessor.Availability, indent);
        var pad = new string(' ', indent * 4);
        sb.Append(pad); sb.AppendLine($"[StructLayout(LayoutKind.Sequential, Size = {size}, Pack = {alignment})]");
        sb.Append(pad); sb.AppendLine($"public partial struct {leaf}");
        sb.Append(pad); sb.AppendLine("{");
        var bodyPad = new string(' ', (indent + 1) * 4);
        sb.Append(bodyPad); sb.AppendLine("// Opaque inline storage. Real field projection requires structural");
        sb.Append(bodyPad); sb.AppendLine("// ABI information that the current manifest does not carry; the");
        sb.Append(bodyPad); sb.AppendLine("// [StructLayout] Size attribute enforces the ABI footprint so the");
        sb.Append(bodyPad); sb.AppendLine("// struct round-trips through Swift calls by value correctly.");
        sb.AppendLine();
        sb.Append(bodyPad); sb.AppendLine("private static TypeMetadata? _cachedMetadata;");
        sb.AppendLine();
        sb.Append(bodyPad); sb.AppendLine("public static TypeMetadata Metadata => _cachedMetadata ??= PInvoke_GetMetadata();");
        sb.AppendLine();
        sb.Append(bodyPad); sb.AppendLine("[UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]");
        sb.Append(bodyPad); sb.AppendLine($"[DllImport(\"{libraryPath}\", EntryPoint = \"{accessor.Symbol}\")]");
        sb.Append(bodyPad); sb.AppendLine("private static extern TypeMetadata PInvoke_GetMetadata();");
        sb.Append(pad); sb.AppendLine("}");

        for (var i = outer.Count - 1; i >= 0; i--)
        {
            var closePad = new string(' ', i * 4);
            sb.Append(closePad);
            sb.AppendLine("}");
        }

        return sb.ToString();
    }

    // ---------- Shared helpers ----------

    private static void AppendFileHeader(StringBuilder sb, TypeEntry entry)
    {
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("//   Generated by Swift.Bindings --emit-apple-types-cs.");
        sb.AppendLine("//   Do NOT edit by hand — regenerate from");
        sb.AppendLine("//   src/Swift.Bindings.Sdk/tools/apple-types-manifest/manifest.json.");
        sb.AppendLine($"//   Swift identity: {entry.SwiftIdentity}");
        sb.AppendLine("// </auto-generated>");
        // The C# compiler's `<auto-generated>` heuristic suppresses analyzer warnings but
        // NOT the CS1591 doc-comment warning when GenerateDocumentationFile=true. The
        // supplement treats warnings as errors, so we must explicitly disable CS1591 for
        // generated files — adding XML docs to every emitted Payload/Dispose member would
        // bloat the output without meaningfully documenting the opaque VWT plumbing.
        sb.AppendLine("#pragma warning disable CS1591");
    }

    private static void AppendAvailabilityAttributes(StringBuilder sb, Availability availability, int indent)
    {
        if (availability is null || availability.IsEmpty)
            return;

        var pad = new string(' ', indent * 4);
        AppendOne(sb, pad, "ios", availability.Ios);
        AppendOne(sb, pad, "maccatalyst", availability.Maccatalyst);
        AppendOne(sb, pad, "tvos", availability.Tvos);
        AppendOne(sb, pad, "macos", availability.Macos);
    }

    private static void AppendOne(StringBuilder sb, string pad, string platformToken, string? version)
    {
        if (version is null)
        {
            // Null means "not available on this platform" per the manifest's availability
            // encoding. Emit [UnsupportedOSPlatform] so the C# analyzer rejects consumer
            // code that invokes the type on the missing platform. A non-null minimum is
            // emitted as [SupportedOSPlatform("{platform}{version}")] so CA1416 fires on
            // any call site below that floor.
            sb.AppendLine($"{pad}[UnsupportedOSPlatform(\"{platformToken}\")]");
        }
        else
        {
            // CA1418 rejects a bare major ("ios16"); require "<major>.<minor>". Pad a missing
            // minor with ".0" so the manifest can store the canonical form ("16") while the
            // emitted attribute stays analyzer-clean.
            var normalized = version.Contains('.') ? version : version + ".0";
            sb.AppendLine($"{pad}[SupportedOSPlatform(\"{platformToken}{normalized}\")]");
        }
    }

    // Map manifest `library` module names to the DllImport string the emitter uses.
    //
    // Apple system frameworks emit the BARE library name (e.g. `"CryptoKit"`) rather than
    // an absolute `/System/Library/Frameworks/X.framework/X` path. The reason is the
    // .NET macios linker: `tools/common/Assembly.cs::ComputeLinkerFlags` scans every
    // referenced assembly's ModuleReferences for strings containing `.framework/` and
    // force-adds `-framework X` to the native linker line — regardless of whether the
    // P/Invoke is ever called. That scan is what caused a `Locale.Language`-only consumer
    // to end up linking CryptoKit and ManagedSettings. Bare names don't match the scanner,
    // so no framework is auto-linked.
    //
    // At runtime, `SwiftFrameworkResolver` (registered via a per-assembly ModuleInitializer)
    // maps the bare name to a system-framework search path and dlopens the framework only
    // when a supplement P/Invoke is actually invoked.
    //
    // Swift stdlib paths stay absolute (no `.framework/` substring so they don't trigger
    // the scanner, and they're already canonical dyld paths).
    private static string ResolveLibraryPath(string library)
    {
        return library switch
        {
            "Swift" or "SwiftCore" => "/usr/lib/swift/libswiftCore.dylib",
            "Dispatch" => "/usr/lib/swift/libswiftDispatch.dylib",
            _ => library,
        };
    }

    private sealed class EmitterGateException : Exception
    {
        public EmitterGateException(string message) : base(message) { }
    }
}
