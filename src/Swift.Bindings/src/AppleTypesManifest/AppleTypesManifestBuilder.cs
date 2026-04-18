// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace BindingsGeneration.AppleTypesManifest;

// Walks one-or-more Apple Xcode SDK ABI JSON dumps and emits a merged manifest entry
// set per included Swift identity. Intentionally reuses `ABIRootNode`/`RootNode`/`Node`
// so the schema stays single-sourced with `SwiftABIParser`.
//
// Platform availability is picked up field-by-field from each ABI JSON's `intro_*`
// properties. Because `swift-api-digester` only emits the intro_* keys that match its
// own target, producing the full ios/maccatalyst/tvos/macos grid requires one dump per
// platform — the builder unions them per swift_identity.
//
// Size/alignment/stride and conformance descriptor symbols require live-SDK probing and
// are deliberately left unset here (null / empty). Session 6's M10 live-SDK validation
// fills them in; Session 2's contract is the static ABI-JSON view only.
public sealed class AppleTypesManifestBuilder
{
    private static readonly HashSet<string> NominalDeclKinds = new(StringComparer.Ordinal)
    {
        "Struct",
        "Enum",
        "Class",
        "Actor",
        "Protocol",
    };

    private readonly ILogger _logger;
    private readonly IncludeFilter _filter;
    private readonly Dictionary<string, TypeEntry> _typesByIdentity = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (TypeAliasEntry Entry, string Module)> _aliasesByIdentity = new(StringComparer.Ordinal);

    public AppleTypesManifestBuilder(IncludeFilter filter, ILogger logger)
    {
        _filter = filter;
        _logger = logger;
    }

    // Union of nominal-type and typealias identities. Callers use this to assert that the
    // include filter matched at least one entry; typealiases carry their own swift_identity
    // and count toward that contract even though they live in a separate dictionary.
    public IReadOnlyCollection<string> MatchedIdentities =>
        _typesByIdentity.Keys.Concat(_aliasesByIdentity.Keys).ToHashSet(StringComparer.Ordinal);

    public void IngestAbiJson(string path)
    {
        var json = File.ReadAllText(path);
        var root = JsonConvert.DeserializeObject<ABIRootNode>(json)
            ?? throw new InvalidDataException($"ABI JSON at '{path}' did not deserialize to ABIRootNode.");
        var module = root.ABIRoot.Name;
        if (string.IsNullOrEmpty(module))
            throw new InvalidDataException($"ABI JSON at '{path}' has empty module name.");

        foreach (var child in root.ABIRoot.Children)
            Walk(child, module, declarationPath: new List<string>(), inherited: new Availability());
    }

    public Manifest Build(ManifestOptions options)
    {
        var manifest = new Manifest
        {
            GeneratedBy = options.GeneratedBy,
            GeneratedAt = options.GeneratedAt,
            SdkTrain = new SdkTrain
            {
                Major = options.SdkTrainMajor,
                Label = options.SdkTrainLabel,
                Platforms = options.Platforms ?? new Availability(),
            },
        };

        foreach (var entry in _typesByIdentity.Values.OrderBy(e => e.SwiftIdentity, StringComparer.Ordinal))
        {
            var module = ExtractModuleName(entry.SwiftIdentity);
            if (!manifest.Modules.TryGetValue(module, out var bucket))
            {
                bucket = new Module();
                manifest.Modules[module] = bucket;
            }
            bucket.Types.Add(entry);
        }

        foreach (var (aliasEntry, module) in _aliasesByIdentity.Values
            .OrderBy(v => v.Entry.AliasIdentity, StringComparer.Ordinal))
        {
            if (!manifest.Modules.TryGetValue(module, out var bucket))
            {
                bucket = new Module();
                manifest.Modules[module] = bucket;
            }
            bucket.Typealiases.Add(aliasEntry);
        }

        foreach (var bucket in manifest.Modules.Values)
        {
            bucket.Types = bucket.Types.OrderBy(t => t.SwiftIdentity, StringComparer.Ordinal).ToList();
            bucket.Typealiases = bucket.Typealiases.OrderBy(a => a.AliasIdentity, StringComparer.Ordinal).ToList();
        }

        return manifest;
    }

    private void Walk(Node node, string module, List<string> declarationPath, Availability inherited)
    {
        if (node.Kind != "TypeDecl")
            return;

        // Nested name guard: for a few imported ObjC members the digester emits a compound
        // `name` like "Foo.Bar" even at a single nesting level. Normalize by taking the last
        // dotted segment — the full path is already accumulated via `declarationPath`.
        var leafName = LastDotSegment(node.Name);
        var currentPath = new List<string>(declarationPath) { leafName };
        var swiftIdentity = module + "." + string.Join(".", currentPath);

        // Effective availability = max(inherited parent minimums, this node's explicit
        // intro_* fields). Nested Swift types inherit their parent's `@available` floor —
        // e.g., `CryptoKit.P256.Signing.ECDSASignature` is only reachable where P256 is
        // (intro_iOS=13), even though the nested type has no intro_* of its own.
        var effective = Clone(inherited);
        MaxMergeNodeAvailability(node, effective);

        if (node.DeclKind == "TypeAlias")
        {
            TryRecordTypealias(node, module, swiftIdentity, effective);
            return;
        }

        if (!NominalDeclKinds.Contains(node.DeclKind ?? string.Empty))
        {
            // Still recurse — nested nominals may hang under non-nominal declarations.
            foreach (var child in node.Children ?? Enumerable.Empty<Node>())
                Walk(child, module, currentPath, effective);
            return;
        }

        if (_filter.Matches(swiftIdentity))
            RecordOrMergeType(node, module, swiftIdentity, currentPath, effective);

        foreach (var child in node.Children ?? Enumerable.Empty<Node>())
            Walk(child, module, currentPath, effective);
    }

    private void RecordOrMergeType(Node node, string module, string swiftIdentity, List<string> declarationPath, Availability effectiveAvailability)
    {
        if (!_typesByIdentity.TryGetValue(swiftIdentity, out var entry))
        {
            var managed = new ManagedRef
            {
                Namespace = "Swift." + module,
                DeclarationPath = new List<string>(declarationPath),
            };
            entry = new TypeEntry
            {
                SwiftIdentity = swiftIdentity,
                ManagedProjection = managed,
                AbiCarrier = new ManagedRef
                {
                    Namespace = managed.Namespace,
                    DeclarationPath = new List<string>(managed.DeclarationPath),
                },
                Kind = LowerKind(node.DeclKind),
                Frozen = HasAttribute(node.DeclAttributes, "Frozen"),
                StorageStrategy = "vwt_opaque",
                SequentialLayoutWhitelisted = false,
                Status = "generated",
                MetadataAccessor = BuildMetadataAccessor(node, module),
                ValueWitness = new ValueWitness
                {
                    Source = "metadata",
                    Trivial = false,
                },
            };
            _typesByIdentity[swiftIdentity] = entry;
        }
        else
        {
            entry.Frozen |= HasAttribute(node.DeclAttributes, "Frozen");
        }

        // Merge per-platform availability across ABI JSON inputs. Each input is typically
        // a single-platform digester dump; unioning the intro_* fields yields the full grid.
        // Within one input, `effectiveAvailability` already carries the ancestor-propagated
        // floor. Between inputs, take the minimum known version per platform.
        var mergedAvailability = entry.MetadataAccessor?.Availability ?? new Availability();
        MinMergeAvailability(effectiveAvailability, mergedAvailability);
        if (entry.MetadataAccessor is not null)
        {
            entry.MetadataAccessor.Availability = mergedAvailability;
            entry.MetadataAccessor.WeakLink = WeakLinkHeuristic(mergedAvailability);
        }
    }

    private void TryRecordTypealias(Node node, string module, string aliasIdentity, Availability effectiveAvailability)
    {
        var target = ExtractTypealiasTarget(node);
        if (target is null)
        {
            _logger.LogDebug("Skipping typealias '{Alias}' in '{Module}' — target type not resolvable from ABI JSON.", aliasIdentity, module);
            return;
        }

        if (!_filter.IncludeAliases(aliasIdentity, target))
            return;

        if (_aliasesByIdentity.TryGetValue(aliasIdentity, out var existing))
        {
            var merged = existing.Entry.Availability ?? new Availability();
            MinMergeAvailability(effectiveAvailability, merged);
            existing.Entry.Availability = merged.IsEmpty ? null : merged;
            return;
        }

        var availability = Clone(effectiveAvailability);
        _aliasesByIdentity[aliasIdentity] = (new TypeAliasEntry
        {
            AliasIdentity = aliasIdentity,
            TargetIdentity = target,
            Availability = availability.IsEmpty ? null : availability,
        }, module);
    }

    private static string? ExtractTypealiasTarget(Node node)
    {
        // ABI JSON TypeAlias nodes expose the underlying identity via `printedName` which
        // carries the Swift sugared form (e.g. `Token<Application>`). That survives round-trip
        // better than trying to reconstruct from the single child TypeNominal node, which
        // requires resolving `usr`s the pipeline doesn't know yet.
        var printed = node.PrintedName?.Trim();
        if (string.IsNullOrEmpty(printed))
            return null;
        return printed;
    }

    private static MetadataAccessor BuildMetadataAccessor(Node node, string module)
    {
        // Canonical Swift mangling rule: type metadata accessor symbol = `$s<type-mangled>Ma`.
        // ABI JSON already carries the type's mangled name on the TypeDecl. A missing or
        // empty mangledName means the dump is malformed (missing usr, non-nominal TypeDecl,
        // ABI JSON bug) — there is no sensible fallback. The schema's `required` gate only
        // checks presence, not emptiness, so the build must fail loud here instead of
        // silently writing an empty accessor that would [DllImport(..., EntryPoint="")]
        // and crash at runtime, or sneak past downstream validators.
        var mangled = node.MangledName;
        if (string.IsNullOrWhiteSpace(mangled))
        {
            var identity = string.IsNullOrWhiteSpace(node.PrintedName)
                ? $"module={module} usr={node.usr ?? "<null>"}"
                : node.PrintedName;
            throw new InvalidOperationException(
                $"Missing or empty mangledName on TypeDecl for {identity}. " +
                "Cannot construct metadata accessor symbol — refusing to emit a blank " +
                "[DllImport] EntryPoint. Fix the ABI dump or add the type to an exclude list.");
        }
        if (string.IsNullOrWhiteSpace(module))
        {
            throw new InvalidOperationException(
                $"Missing or empty module for TypeDecl {node.PrintedName ?? node.usr ?? "<unknown>"}. " +
                "Metadata accessor library must be a concrete framework/dylib name.");
        }
        return new MetadataAccessor
        {
            Symbol = mangled + "Ma",
            Library = module,
            Availability = new Availability(),
        };
    }

    private static Availability Clone(Availability a) => new()
    {
        Ios = a.Ios,
        Maccatalyst = a.Maccatalyst,
        Tvos = a.Tvos,
        Macos = a.Macos,
    };

    // Walk a child type. Its effective minimum for each platform is the MAXIMUM of its
    // ancestors' minimums and its own explicit intro_* (Swift's @available inheritance).
    private static void MaxMergeNodeAvailability(Node node, Availability target)
    {
        target.Ios = MaxVersion(target.Ios, node.intro_iOS);
        target.Maccatalyst = MaxVersion(target.Maccatalyst, node.intro_macCatalyst);
        target.Tvos = MaxVersion(target.Tvos, node.intro_tvOS);
        target.Macos = MaxVersion(target.Macos, node.intro_Macosx);
    }

    // Cross-input merge: several ABI JSON dumps may each see the same type. When both set
    // a platform version, take the LOWEST (most permissive) — that's the minimum version
    // the symbol is provably available on. When only one sets it, keep that value.
    private static void MinMergeAvailability(Availability source, Availability target)
    {
        target.Ios = MinVersion(target.Ios, source.Ios);
        target.Maccatalyst = MinVersion(target.Maccatalyst, source.Maccatalyst);
        target.Tvos = MinVersion(target.Tvos, source.Tvos);
        target.Macos = MinVersion(target.Macos, source.Macos);
    }

    private static string? MaxVersion(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a)) return string.IsNullOrEmpty(b) ? null : b;
        if (string.IsNullOrEmpty(b)) return a;
        return CompareVersionStrings(a, b) >= 0 ? a : b;
    }

    private static string? MinVersion(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a)) return string.IsNullOrEmpty(b) ? null : b;
        if (string.IsNullOrEmpty(b)) return a;
        return CompareVersionStrings(a, b) <= 0 ? a : b;
    }

    private static int CompareVersionStrings(string a, string b)
    {
        var pa = ParseVersionTuple(a);
        var pb = ParseVersionTuple(b);
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var va = i < pa.Length ? pa[i] : 0;
            var vb = i < pb.Length ? pb[i] : 0;
            if (va != vb) return va.CompareTo(vb);
        }
        return 0;
    }

    private static int[] ParseVersionTuple(string s)
    {
        var parts = s.Split('.');
        var result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            result[i] = int.TryParse(parts[i], out var v) ? v : 0;
        return result;
    }

    private static bool WeakLinkHeuristic(Availability a)
    {
        // Any per-platform minimum OR a platform-absence (null) implies an `@available` gate
        // exists in source; emitters must weak-link the metadata accessor. The supplement's
        // own SupportedOSPlatformVersion floor is separate — Session 4 will refine this.
        return !a.IsEmpty;
    }

    private static bool HasAttribute(string[]? attrs, string attr)
    {
        if (attrs is null) return false;
        for (int i = 0; i < attrs.Length; i++)
            if (string.Equals(attrs[i], attr, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static string LowerKind(string declKind) => declKind?.ToLowerInvariant() switch
    {
        "struct" => "struct",
        "enum" => "enum",
        "class" => "class",
        "actor" => "actor",
        "protocol" => "protocol",
        _ => declKind?.ToLowerInvariant() ?? "struct",
    };

    private static string LastDotSegment(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var idx = name.LastIndexOf('.');
        return idx < 0 ? name : name.Substring(idx + 1);
    }

    private static string ExtractModuleName(string swiftIdentity)
    {
        var idx = swiftIdentity.IndexOf('.');
        return idx < 0 ? swiftIdentity : swiftIdentity.Substring(0, idx);
    }
}

public sealed class ManifestOptions
{
    public int SdkTrainMajor { get; init; } = 26;
    public string? SdkTrainLabel { get; init; }
    public Availability? Platforms { get; init; }
    public string? GeneratedBy { get; init; }
    public string? GeneratedAt { get; init; }
}

// Filters which Swift identities land in the manifest. Kept minimal: one JSON file shape,
// hand-maintained for Session 2 and filled out exhaustively by Session 7. Excluding legacy
// Runtime-owned canonicals (Date, Data, URL, Decimal, Measurement<T>, AnyError,
// ManagedSettings.Token<T>, SwiftUI.Text) is the caller's responsibility — the filter is
// positive-list only so the supplement cannot accidentally shadow canonical identity.
public sealed class IncludeFilter
{
    private readonly HashSet<string> _identities;

    public IncludeFilter(IEnumerable<string> identities)
    {
        _identities = new HashSet<string>(identities, StringComparer.Ordinal);
    }

    /// <summary>
    /// Swift identities the caller asked for. Exposed so the regen command can diff
    /// this set against <see cref="AppleTypesManifestBuilder.MatchedIdentities"/> and
    /// fail loud on any unmatched identity — otherwise a typo in include-types.json
    /// (or a type the ABI dump silently dropped) would ship a manifest missing the
    /// expected entry and nobody would notice until a consumer crashed.
    /// </summary>
    public IReadOnlyCollection<string> RequestedIdentities => _identities;

    public bool Matches(string swiftIdentity) => _identities.Contains(swiftIdentity);

    public bool IncludeAliases(string aliasIdentity, string targetIdentity)
        => _identities.Contains(aliasIdentity);

    public static IncludeFilter FromFile(string path)
    {
        var json = File.ReadAllText(path);
        var payload = JsonConvert.DeserializeObject<IncludeFilterFile>(json)
            ?? throw new InvalidDataException($"Include-types file at '{path}' did not deserialize.");
        var ids = payload.Types ?? new List<string>();
        if (payload.Typealiases is not null)
            ids.AddRange(payload.Typealiases);
        return new IncludeFilter(ids);
    }

    private sealed class IncludeFilterFile
    {
        [JsonProperty("types")] public List<string>? Types { get; set; }
        [JsonProperty("typealiases")] public List<string>? Typealiases { get; set; }
    }
}
