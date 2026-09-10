// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Every hand-written P/Invoke in the runtime names a native entry point by hand, and nothing about
/// declaring one proves the symbol exists: a name that no library exports compiles cleanly, links
/// cleanly, and then throws <see cref="EntryPointNotFoundException"/> the first time the member is
/// called. Swift-mangled names are the sharp case — they are long, hand-transcribed, and easy to
/// derive from a source declaration that the compiler in fact inlined into its callers and never
/// emitted a callable symbol for.
///
/// This asks dyld directly, which is the only oracle that answers the question a caller asks. It
/// deliberately resolves through a loaded library handle rather than reading a static symbol table,
/// because a system framework may re-export another one (its own binary then lists a symbol it does
/// not define) and a caller that dlopens the umbrella still resolves it.
///
/// A library that will not load is a failure, not a skip. Tolerating one would let a mistyped or
/// stale path silently retire every declaration behind it — the same silence this test exists to
/// end. If an import is ever added against a framework that does not exist on the build host, the
/// right response is a deliberate one (move the check to a platform that can answer it), not a
/// quiet pass. The floors are the second control: they red if the sweep stops reaching the
/// runtime's imports at all, rather than reporting zero problems over zero declarations.
/// </summary>
public class NativeEntryPointResolutionTests
{
    /// <summary>
    /// A P/Invoke declaration reduced to what a resolution check needs.
    /// </summary>
    private readonly record struct NativeImport(string DeclaringType, string MethodName, string Library, string EntryPoint);

    [Fact]
    public void EveryPathQualifiedImport_ResolvesInTheLibraryItNames()
    {
        var imports = CollectPathQualifiedImports();

        // Cache load results so a library with dozens of imports is opened once.
        var handles = new Dictionary<string, IntPtr>();
        var unresolved = new List<string>();
        var unloadable = new SortedSet<string>();
        var checkedImports = 0;
        var checkedMangled = 0;
        var checkedLibraries = new SortedSet<string>();

        foreach (var import in imports)
        {
            if (!handles.TryGetValue(import.Library, out var handle))
            {
                handle = NativeLibrary.TryLoad(import.Library, out var loaded) ? loaded : IntPtr.Zero;
                handles[import.Library] = handle;
            }

            if (handle == IntPtr.Zero)
            {
                unloadable.Add(import.Library);
                continue;
            }

            checkedImports++;
            checkedLibraries.Add(import.Library);
            if (IsSwiftMangled(import.EntryPoint))
                checkedMangled++;

            if (!NativeLibrary.TryGetExport(handle, import.EntryPoint, out var address) || address == IntPtr.Zero)
            {
                unresolved.Add(
                    $"{import.DeclaringType}.{import.MethodName} -> {import.EntryPoint} (not exported by {import.Library})");
            }
        }

        Assert.True(
            unloadable.Count == 0,
            $"Librar(ies) the runtime names by absolute path that this host could not load, leaving every " +
            $"import behind them unchecked:{Environment.NewLine}{string.Join(Environment.NewLine, unloadable)}");

        Assert.True(
            unresolved.Count == 0,
            $"Native entry points that no loaded library exports (these throw EntryPointNotFoundException " +
            $"at the first call):{Environment.NewLine}{string.Join(Environment.NewLine, unresolved)}");

        // Fail-closed controls. These are floors on the SWEEP, not on the runtime's design: they only
        // assert that the check still reached a broad, mangled-name-bearing population, and they sit
        // close under the population the runtime declares today so that losing a whole declaring type
        // reds rather than passing. Lowering one to accommodate imports that stopped being discovered
        // is exactly the regression they exist to catch; removing declarations on purpose is the one
        // reason to move them, and then they move together with the removal.
        //
        // They last moved when the collection and Hashable P/Invokes that pass `self` as an untyped
        // SwiftSelf were rerouted through the C cdecl shims: fifteen path-qualified declarations were
        // deleted in that move, and the replacements name `SwiftBindingsRuntime` by bare library name,
        // which this sweep deliberately does not resolve. The floors are set one declaring type under
        // what remains — 57 imports across 4 libraries, 32 of them mangled, with the largest single
        // declaring type contributing 9 imports and 6 mangled names.
        Assert.True(checkedImports >= 50,
            $"Only {checkedImports} import(s) were resolvable-checked; the sweep is no longer reaching the " +
            $"runtime's P/Invoke surface.");
        Assert.True(checkedLibraries.Count >= 3,
            $"Imports were checked against only {checkedLibraries.Count} distinct librar(ies); the runtime " +
            $"names more than that by absolute path.");
        Assert.True(checkedMangled >= 28,
            $"Only {checkedMangled} Swift-mangled entry point(s) were checked. Mangled names are the class of " +
            $"name this test exists for, so a sweep that no longer sees them proves nothing.");
    }

    /// <summary>
    /// A library named by an absolute path is loaded by dyld exactly as written, so this test can ask
    /// the same question the runtime's own call will ask. Imports named by a bare library or framework
    /// name go through the runtime's <c>DllImportResolver</c> and a platform-dependent probe order, so
    /// resolving them from a console test host would answer a different question than the one a device
    /// or simulator process asks.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Host-only test assembly; it is never trimmed or AOT-published. Enumerating the " +
                        "runtime's P/Invoke declarations is the subject of the test.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Host-only test assembly; it is never trimmed or AOT-published. Enumerating the " +
                        "runtime's P/Invoke declarations is the subject of the test.")]
    private static List<NativeImport> CollectPathQualifiedImports()
    {
        var runtimeAssembly = typeof(Swift.DispatchQueue).Assembly;
        var results = new List<NativeImport>();

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                 BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        foreach (var type in runtimeAssembly.GetTypes())
        {
            foreach (var method in type.GetMethods(all))
            {
                if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0)
                    continue;

                var dllImport = method.GetCustomAttribute<DllImportAttribute>();
                if (dllImport is null)
                    continue;

                var library = dllImport.Value;
                if (string.IsNullOrEmpty(library) || !Path.IsPathRooted(library))
                    continue;

                var entryPoint = string.IsNullOrEmpty(dllImport.EntryPoint) ? method.Name : dllImport.EntryPoint;
                results.Add(new NativeImport(type.FullName ?? type.Name, method.Name, library, entryPoint));
            }
        }

        return results;
    }

    /// <summary>
    /// Swift's mangled names all carry the reserved <c>$s</c> prefix (older toolchains emitted
    /// <c>_$S</c>/<c>$S</c>); a C symbol never does.
    /// </summary>
    private static bool IsSwiftMangled(string entryPoint)
        => entryPoint.StartsWith("$s", StringComparison.Ordinal) ||
           entryPoint.StartsWith("$S", StringComparison.Ordinal);

    /// <summary>
    /// The sweep above only proves something if a name nobody exports actually fails it. This runs the
    /// same resolution step against a well-formed Swift mangling for a member the Dispatch overlay
    /// declares but inlines into its callers — <c>DispatchQueue.global()</c> — which is the exact shape
    /// that reads as a perfectly good entry point and is not one.
    ///
    /// The lookup is aimed at the overlay that owns the name, not merely at some library that was never
    /// going to have it: a Dispatch mangling is trivially absent from libswiftCore whether or not the
    /// overlay exports it, so asking there would be a control whose replacement trigger could never
    /// fire. Both halves — the absent mangled name and a real exported symbol from the same library —
    /// are asked of the same handle, so a failure separates "that symbol is missing" from "the lookup
    /// is not working".
    /// </summary>
    [Fact]
    public void ResolutionCheck_RejectsAMangledNameTheOwningOverlayDoesNotExport()
    {
        const string dispatchOverlay = "/usr/lib/swift/libswiftDispatch.dylib";
        Assert.True(NativeLibrary.TryLoad(dispatchOverlay, out var overlay),
            $"Could not load {dispatchOverlay}; the control cannot ask the library that owns the name.");

        Assert.False(
            NativeLibrary.TryGetExport(overlay, "$s8Dispatch0A5QueueC6globalACyFZ", out _),
            "The Dispatch overlay must not export a callable symbol for its inlined queue accessor; if it " +
            "now does, this control needs replacing with a name that is still absent.");

        // Positive half: a symbol the same overlay really does export resolves through the same handle.
        // This one is an extension on the imported ObjC class, which is the form the overlay emits.
        Assert.True(
            NativeLibrary.TryGetExport(overlay, "$sSo17OS_dispatch_queueC8DispatchE4mainABvgZ", out var mainGetter) &&
            mainGetter != IntPtr.Zero,
            "The Dispatch overlay must export its main-queue accessor; the export lookup itself is not working.");
    }
}
