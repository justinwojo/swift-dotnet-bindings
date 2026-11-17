// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LoadContextHost;

/// <summary>
/// Reads the reverse-dispatch vtables, and the runtime's closure-destroy hook, that Swift actually
/// holds. Each is a private Swift global in a wrapper image (<c>_{p}_vtable</c>) or in the
/// SwiftBindingsRuntime image, so it has no export: its address comes from the image's
/// symbol table (<c>nm</c>), slid by the distance between an exported anchor's <c>dlsym</c> address
/// and that anchor's <c>nm</c> address. The first two words of the table are its <c>csVTHandle</c>
/// (the owning proxy's pinned local-vtable handle) and its first callback pointer.
/// </summary>
internal static partial class NativeVtables
{
    internal sealed record Table(string Name, string Anchor, string StorageFragment, bool IsVtable = true);

    internal static readonly Table[] Tables =
    [
        // Same-module inherited parent, in the main wrapper.
        new("inheritedParent", "SBW_SetInheritedParentDelegate_vtable", "_inheritedParentDelegate_vtable"),
        // The main wrapper's copy of the cross-module parent's table.
        new("crossModuleParentInMain", "SBW_SetSwiftBindingsTestLibDependency_CrossModuleParentDelegate_vtable",
            "_SwiftBindingsTestLibDependency_crossModuleParentDelegate_vtable"),
        // The dependency wrapper's own table for the same parent.
        new("crossModuleParentInDependency", "SBW_SetCrossModuleParentDelegate_vtable", "_crossModuleParentDelegate_vtable"),
        // SwiftBindings.Runtime's closure-context destroy hook (an optional C function pointer), in the
        // SwiftBindingsRuntime image.
        new("runtimeClosureDestroyHook", "SwiftBindings_SetClosureContextDestroyCallback", "_sbClosureCtxDestroy", IsVtable: false),
    ];

    /// <summary>The first two words of a vtable (<c>csVTHandle</c>, first callback), or of a hook (the function pointer).</summary>
    internal readonly record struct Entry(IntPtr Word0, IntPtr Word1, bool IsVtable)
    {
        public override string ToString() => IsVtable
            ? $"csVTHandle=0x{Word0:X} slot0=0x{Word1:X}"
            : $"hook=0x{Word0:X}";
    }

    private static readonly Dictionary<string, Dictionary<string, ulong>> s_symbolsByImage = new();

    internal static Dictionary<string, Entry> ReadAll()
    {
        var result = new Dictionary<string, Entry>();
        foreach (var table in Tables)
            result[table.Name] = Read(table);
        return result;
    }

    private static unsafe Entry Read(Table table)
    {
        var anchor = FindExport(table.Anchor);
        if (anchor == IntPtr.Zero)
            throw new InvalidOperationException($"No loaded image exports {table.Anchor}.");
        if (dladdr(anchor, out var info) == 0 || info.dli_fname == IntPtr.Zero)
            throw new InvalidOperationException($"dladdr failed for {table.Anchor}.");
        var image = Marshal.PtrToStringUTF8(info.dli_fname)!;
        var symbols = SymbolsOf(image);

        if (!symbols.TryGetValue("_" + table.Anchor, out var anchorUnslid))
            throw new InvalidOperationException($"{image} has no symbol _{table.Anchor}.");
        var slide = (long)anchor - (long)anchorUnslid;

        // The storage symbol is the Swift global's variable storage (mangled `...vp`). Its mangled text
        // is not searchable (Swift substitutes repeated words such as the module name), so match on
        // the demangled name, which is the global's source name.
        var storage = StorageSymbolsOf(image, symbols)
            .Where(s => s.Demangled == table.StorageFragment)
            .ToList();
        if (storage.Count != 1)
            throw new InvalidOperationException(
                $"{image}: expected one storage symbol for {table.StorageFragment}, found {storage.Count}: " +
                string.Join(", ", storage.Select(s => s.Mangled)));

        var address = (IntPtr*)((long)symbols[storage[0].Mangled] + slide);
        return new Entry(address[0], table.IsVtable ? address[1] : IntPtr.Zero, table.IsVtable);
    }

    private static IntPtr FindExport(string symbol)
    {
        var found = dlsym(RTLD_DEFAULT, symbol);
        if (found != IntPtr.Zero)
            return found;
        // Not in the global namespace: try each loaded framework image directly.
        var count = _dyld_image_count();
        for (uint i = 0; i < count; i++)
        {
            var name = Marshal.PtrToStringUTF8(_dyld_get_image_name(i));
            if (name == null || !name.Contains(".framework/", StringComparison.Ordinal))
                continue;
            var handle = dlopen(name, RTLD_LAZY | RTLD_NOLOAD);
            if (handle == IntPtr.Zero)
                continue;
            found = dlsym(handle, symbol);
            dlclose(handle);
            if (found != IntPtr.Zero)
                return found;
        }
        return IntPtr.Zero;
    }

    private static readonly Dictionary<string, List<(string Mangled, string Demangled)>> s_storageByImage = new();

    /// <summary>The image's Swift variable-storage symbols, with their simplified demangled names.</summary>
    private static List<(string Mangled, string Demangled)> StorageSymbolsOf(string image, Dictionary<string, ulong> symbols)
    {
        if (s_storageByImage.TryGetValue(image, out var cached))
            return cached;

        var mangled = symbols.Keys
            .Where(k => k.StartsWith("_$s", StringComparison.Ordinal) && k.EndsWith("vp", StringComparison.Ordinal))
            .ToList();
        var psi = new ProcessStartInfo("/usr/bin/xcrun", ["swift-demangle", "--simplified"])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var demangle = Process.Start(psi)!;
        var stdout = demangle.StandardOutput.ReadToEndAsync();
        var stderr = demangle.StandardError.ReadToEndAsync();
        foreach (var name in mangled)
            demangle.StandardInput.WriteLine(name.Substring(1)); // nm prefixes C's underscore
        demangle.StandardInput.Close();
        demangle.WaitForExit();
        if (demangle.ExitCode != 0)
            throw new InvalidOperationException($"swift-demangle exited {demangle.ExitCode}: {stderr.Result}");

        var demangled = stdout.Result.Split('\n');
        var result = new List<(string, string)>();
        for (var i = 0; i < mangled.Count; i++)
            result.Add((mangled[i], i < demangled.Length ? demangled[i].Trim() : ""));
        s_storageByImage[image] = result;
        return result;
    }

    private static Dictionary<string, ulong> SymbolsOf(string image)
    {
        if (s_symbolsByImage.TryGetValue(image, out var cached))
            return cached;

        var arch = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "x86_64" : "arm64";
        var psi = new ProcessStartInfo("/usr/bin/nm", ["-arch", arch, image])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var nm = Process.Start(psi)!;
        var stdout = nm.StandardOutput.ReadToEndAsync();
        var stderr = nm.StandardError.ReadToEndAsync();
        nm.WaitForExit();
        if (nm.ExitCode != 0)
            throw new InvalidOperationException($"nm {image} exited {nm.ExitCode}: {stderr.Result}");

        var symbols = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var line in stdout.Result.Split('\n'))
        {
            // "<16 hex digits> <type> <name>"; undefined symbols carry no address.
            var parts = line.Split(' ', 3);
            if (parts.Length != 3 || parts[0].Length == 0 || parts[1] == "U")
                continue;
            if (ulong.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out var value))
                symbols.TryAdd(parts[2], value);
        }
        s_symbolsByImage[image] = symbols;
        return symbols;
    }

    private static readonly IntPtr RTLD_DEFAULT = new(-2);
    private const int RTLD_LAZY = 0x1;
    private const int RTLD_NOLOAD = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct DlInfo
    {
        public IntPtr dli_fname;
        public IntPtr dli_fbase;
        public IntPtr dli_sname;
        public IntPtr dli_saddr;
    }

    [LibraryImport("/usr/lib/libSystem.B.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr dlsym(IntPtr handle, string symbol);

    [LibraryImport("/usr/lib/libSystem.B.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr dlopen(string path, int mode);

    [LibraryImport("/usr/lib/libSystem.B.dylib")]
    private static partial int dlclose(IntPtr handle);

    [LibraryImport("/usr/lib/libSystem.B.dylib")]
    private static partial int dladdr(IntPtr address, out DlInfo info);

    [LibraryImport("/usr/lib/libSystem.B.dylib")]
    private static partial uint _dyld_image_count();

    [LibraryImport("/usr/lib/libSystem.B.dylib")]
    private static partial IntPtr _dyld_get_image_name(uint index);
}
