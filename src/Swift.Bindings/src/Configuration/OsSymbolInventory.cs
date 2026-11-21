// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace BindingsGeneration
{
    /// <summary>
    /// Which Swift symbols an OS release ships (load-safety property P2). The generator ships one
    /// built from an Xcode's SDK stubs (<c>build/scripts/build-os-symbol-inventory.py</c>, embedded as
    /// <c>Data/os-symbol-inventory.bin</c>) and accepts a supplied one through
    /// <c>--os-symbol-inventory</c>.
    /// <para/>
    /// The shipped inventory holds a 40-bit hash of every Swift symbol each platform variant's SDK
    /// exports from a public OS provider, so membership has a one-in-2^40 false-positive rate per
    /// query and no false negatives. A supplied file is either a replacement in the same binary form
    /// or a JSON inventory of named providers (<see cref="Provider"/>), each listing every symbol it
    /// ships at its OS version; providers extend the inventory in use rather than replace it.
    /// </summary>
    internal sealed class OsSymbolInventory
    {
        internal const string Format = "swift-os-symbol-inventory/1";
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SBOSINV1");
        private const ulong HashMask = (1UL << 40) - 1;

        /// <summary>A platform variant the hashed inventory covers (a .tbd target suffix: <c>ios</c>, <c>ios-simulator</c>, …).</summary>
        internal sealed record Platform(string Variant, string OsVersion, string? Sdk, string? SdkBuild, int Bit);

        /// <summary>
        /// A provider whose complete symbol set a supplied inventory lists. <see cref="Floor"/>, when
        /// set, is the OS version from which a symbol it does not list may be assumed present; without
        /// it the generating SDK's version is used.
        /// </summary>
        internal sealed record Provider(
            string InstallName, string ModuleName, IReadOnlyList<string>? Variants, string OsVersion,
            string? Floor, IReadOnlySet<string> Symbols);

        private readonly ulong[] _hashes;
        private readonly byte[] _masks;

        internal IReadOnlyList<Platform> Platforms { get; }
        internal IReadOnlyList<Provider> Providers { get; }

        /// <summary>Where the inventory came from, for the binding report: Xcode version and build plus any supplied file.</summary>
        internal string Provenance { get; }

        private OsSymbolInventory(ulong[] hashes, byte[] masks, IReadOnlyList<Platform> platforms,
            IReadOnlyList<Provider> providers, string provenance)
        {
            _hashes = hashes;
            _masks = masks;
            Platforms = platforms;
            Providers = providers;
            Provenance = provenance;
        }

        private static readonly Lazy<OsSymbolInventory> s_shipped = new(LoadShipped);

        /// <summary>The inventory embedded in the generator.</summary>
        internal static OsSymbolInventory Shipped => s_shipped.Value;

        private static OsSymbolInventory LoadShipped()
        {
            var assembly = typeof(OsSymbolInventory).Assembly;
            var name = assembly.GetManifestResourceNames()
                .Single(n => n.EndsWith("os-symbol-inventory.bin", StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(name)!;
            return ReadBinary(stream, "shipped");
        }

        /// <summary>Loads a binary inventory from <paramref name="path"/>.</summary>
        internal static OsSymbolInventory LoadFile(string path)
        {
            using var stream = File.OpenRead(path);
            return Malformed(path, () => ReadBinary(stream, path));
        }

        /// <summary>
        /// Loads a supplied inventory. A binary inventory replaces <paramref name="current"/>; a JSON
        /// inventory adds its providers to it.
        /// </summary>
        internal static OsSymbolInventory Load(string path, OsSymbolInventory current)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
                return Malformed(path, () => ReadBinary(new MemoryStream(bytes), path));
            var providers = Malformed(path, () => ReadProviders(bytes, path));
            return new OsSymbolInventory(current._hashes, current._masks, current.Platforms,
                current.Providers.Concat(providers).ToList(), $"{current.Provenance}; providers from {path}");
        }

        // A JSON null where a string belongs is malformed too (GetString returns null for it).
        private static string Text(JsonElement element) =>
            element.GetString() ?? throw new InvalidOperationException("a string member is null");

        // A file that parses but lacks a required member, holds one of the wrong kind or runs short is
        // malformed, which callers report as they report any unreadable inventory.
        private static T Malformed<T>(string source, Func<T> read)
        {
            try
            {
                return read();
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException
                or ArgumentException or FormatException or OverflowException)
            {
                throw new InvalidDataException($"'{source}' is a malformed Swift OS symbol inventory: {ex.Message}", ex);
            }
        }

        /// <summary>Whether every listed platform variant ships <paramref name="symbol"/> (a <c>$s…</c> name, no leading underscore).</summary>
        internal bool ShipsOnAll(IReadOnlyList<Platform> variants, string symbol)
        {
            int required = 0;
            foreach (var v in variants)
                required |= 1 << v.Bit;
            var index = Array.BinarySearch(_hashes, Hash(symbol));
            return index >= 0 && (_masks[index] & required) == required;
        }

        /// <summary>Whether the platform variant named <paramref name="variant"/> ships <paramref name="symbol"/>; false for a variant the inventory lacks.</summary>
        internal bool ShipsOn(string variant, string symbol) =>
            Platforms.FirstOrDefault(p => p.Variant == variant) is { } platform && ShipsOnAll(new[] { platform }, symbol);

        /// <summary>The supplied provider for <paramref name="moduleName"/>, or null.</summary>
        internal Provider? FindProvider(string moduleName) =>
            Providers.LastOrDefault(p => string.Equals(p.ModuleName, moduleName, StringComparison.Ordinal));

        /// <summary>Low 40 bits of FNV-1a-64 over the UTF-8 symbol, as the inventory builder computes it.</summary>
        internal static ulong Hash(string symbol)
        {
            ulong h = 0xCBF29CE484222325UL;
            foreach (var b in Encoding.UTF8.GetBytes(symbol))
            {
                h ^= b;
                h *= 0x100000001B3UL;
            }
            return h & HashMask;
        }

        private static OsSymbolInventory ReadBinary(Stream compressed, string source)
        {
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var buffer = new MemoryStream();
            gzip.CopyTo(buffer);
            var data = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
            if (data.Length < 12 || !data[..8].SequenceEqual(Magic))
                throw new InvalidDataException($"'{source}' is not a Swift OS symbol inventory (bad magic).");
            var headerLength = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8, 4));
            if (headerLength > (uint)(data.Length - 12))
                throw new InvalidDataException($"'{source}' is truncated (its header runs past the end).");
            using var header = JsonDocument.Parse(data.Slice(12, (int)headerLength).ToArray());
            var root = header.RootElement;
            if (root.GetProperty("format").GetString() != Format)
                throw new InvalidDataException($"'{source}' has an unsupported inventory format.");

            var platforms = root.GetProperty("platforms").EnumerateArray().Select(p => new Platform(
                Text(p.GetProperty("variant")),
                Text(p.GetProperty("osVersion")),
                p.TryGetProperty("sdk", out var sdk) ? sdk.GetString() : null,
                p.TryGetProperty("sdkBuild", out var build) ? build.GetString() : null,
                p.GetProperty("bit").GetInt32())).ToList();

            var entries = new List<(ulong Hash, byte Mask)>();
            int pos = 12 + (int)headerLength;
            foreach (var group in root.GetProperty("groups").EnumerateArray())
            {
                var mask = (byte)group.GetProperty("mask").GetInt32();
                var count = group.GetProperty("count").GetInt32();
                ulong previous = 0;
                for (int i = 0; i < count; i++)
                {
                    ulong delta = 0;
                    int shift = 0;
                    while (true)
                    {
                        if (pos >= data.Length)
                            throw new InvalidDataException($"'{source}' is truncated.");
                        var b = data[pos++];
                        delta |= (ulong)(b & 0x7F) << shift;
                        if ((b & 0x80) == 0)
                            break;
                        shift += 7;
                    }
                    previous += delta;
                    entries.Add((previous, mask));
                }
            }
            entries.Sort((a, b) => a.Hash.CompareTo(b.Hash));
            var xcode = root.TryGetProperty("xcode", out var x)
                ? $"Xcode {x.GetProperty("version").GetString()} ({x.GetProperty("build").GetString()})"
                : "unknown Xcode";
            return new OsSymbolInventory(
                entries.Select(e => e.Hash).ToArray(), entries.Select(e => e.Mask).ToArray(), platforms,
                Array.Empty<Provider>(), source == "shipped" ? $"shipped inventory, {xcode}" : $"{source}, {xcode}");
        }

        private static List<Provider> ReadProviders(byte[] json, string source)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("format", out var format) || format.GetString() != Format)
                throw new InvalidDataException($"'{source}' is not a Swift OS symbol inventory (expected \"format\": \"{Format}\").");
            var providers = new List<Provider>();
            foreach (var p in root.GetProperty("providers").EnumerateArray())
            {
                var installName = Text(p.GetProperty("installName"));
                var module = p.TryGetProperty("module", out var m) && m.GetString() is { Length: > 0 } explicitModule
                    ? explicitModule
                    : OsFrameworkWeakLinking.FrameworkName(installName)
                        ?? throw new InvalidDataException($"'{source}': provider '{installName}' names no module.");
                var osVersion = Text(p.GetProperty("osVersion"));
                var floor = p.TryGetProperty("floor", out var f) ? f.GetString() : null;
                var variants = p.TryGetProperty("variants", out var v)
                    ? v.EnumerateArray().Select(Text).ToList()
                    : null;
                var symbols = new HashSet<string>(
                    p.GetProperty("symbols").EnumerateArray().Select(e => Text(e).TrimStart('_')),
                    StringComparer.Ordinal);
                providers.Add(new Provider(installName, module, variants, osVersion, floor, symbols));
            }
            return providers;
        }
    }
}
