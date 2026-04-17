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
// the live SDK (Session 6 M10 responsibility).
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

    // Entries we skipped + why, for CLI reporting.
    public IReadOnlyList<SkippedEntry> SkippedEntries => _skipped;
    private readonly List<SkippedEntry> _skipped = new();

    public sealed record SkippedEntry(string SwiftIdentity, string Reason);

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
                    // Gate refusal: log + skip + continue. Whitelist opt-in that fails
                    // validation must not break the build for the rest of the manifest.
                    _logger.LogError(
                        "Refusing to emit sequential layout for '{Identity}': {Reason}",
                        entry.SwiftIdentity, ex.Message);
                    _skipped.Add(new SkippedEntry(entry.SwiftIdentity, ex.Message));
                }
            }
        }
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
    //   1. `storage_strategy == "sequential"` in the manifest
    //   2. `sequential_layout_whitelisted == true` in the manifest
    //   3. Swift identity is in the external whitelist file
    //   4. frozen=true
    //   5. non-generic (`<` absent from identity)
    //   6. size + alignment both non-null (validated against live SDK by Session 6)
    //
    // If the caller *requested* the sequential path (conditions 1 or 2) but any hard gate
    // fails (4–6), we throw EmitterGateException — refusing to emit rather than silently
    // corrupting memory with wrong layout.
    private bool ShouldUseSequentialLayout(TypeEntry entry)
    {
        var requestedSequential =
            string.Equals(entry.StorageStrategy, "sequential", StringComparison.Ordinal)
            || entry.SequentialLayoutWhitelisted;

        if (!requestedSequential)
            return false;

        if (!_whitelist.Contains(entry.SwiftIdentity))
            throw new EmitterGateException(
                "sequential_layout_whitelisted=true in manifest but Swift identity is " +
                "not in sequential-layout-whitelist.json. The external whitelist file is " +
                "the second gate that prevents accidental layout opt-in.");

        if (!entry.Frozen)
            throw new EmitterGateException(
                "frozen=false; sequential layout is only permitted for frozen types. " +
                "Set storage_strategy=\"vwt_opaque\" or remove from whitelist.");

        if (entry.SwiftIdentity.Contains('<'))
            throw new EmitterGateException(
                "type appears generic; generics require fully layout-known instantiation " +
                "which the whitelist path does not yet support.");

        if (entry.Size is null || entry.Alignment is null)
            throw new EmitterGateException(
                "size/alignment are null in the manifest. The sequential path requires " +
                "both to be validated against the live Apple SDK (Session 6 M10). Until " +
                "probing fills these fields, the whitelist cannot activate.");

        return true;
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
            // code that invokes the type on the missing platform. This matches the
            // Decision summary §Q10 item 1 availability guidance.
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

    // Map manifest `library` module names to the absolute dyld paths `Swift.Runtime` uses
    // (see `src/Swift.Runtime/src/Swift/Runtime/KnownLibraries.cs`). The absolute-path form
    // bypasses `SwiftFrameworkResolver` and works verbatim on iOS, macOS, Catalyst, and tvOS.
    private static string ResolveLibraryPath(string library)
    {
        return library switch
        {
            "Swift" or "SwiftCore" => "/usr/lib/swift/libswiftCore.dylib",
            "Dispatch" => "/usr/lib/swift/libswiftDispatch.dylib",
            _ => $"/System/Library/Frameworks/{library}.framework/{library}",
        };
    }

    private sealed class EmitterGateException : Exception
    {
        public EmitterGateException(string message) : base(message) { }
    }
}
