// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace LoadContextHost;

/// <summary>
/// Runs one load-context ownership scenario per process (a cross-context dispatch fail-fasts the
/// process, so scenarios cannot share one). The gate launches the host once per scenario and reads
/// the <c>LC-RESULT</c> line. Evidence lines: <c>LC-STEP</c> (what ran), <c>LC-NATIVE</c> (the
/// vtables Swift holds), <c>LC-OWNERS</c> (the process-wide owner table), <c>LC-MEASURE</c>
/// (keep-alive measurements), <c>LC-RED</c> (an unrefused second copy, the pre-fix behaviour).
/// </summary>
public static class Program
{
    internal const string ConflictTypeName = "Swift.Runtime.SwiftLoadContextConflictException";
    private const string OwnerTableKey = "SwiftBindings.NativeRegistrationOwners.v1";

    // Our assemblies, staged under MonoBundle/lc/ and never on the default context's TPA list.
    internal static readonly string LcDirectory = Path.Combine(AppContext.BaseDirectory, "lc");
    internal static readonly string[] Staged = ["Swift.Runtime", "SwiftBindings.Apple", "SwiftBindingsTestLib", "LoadContextProbe"];
    private static readonly string[] BindingAndProbe = ["SwiftBindingsTestLib", "LoadContextProbe"];
    private static readonly string[] Everything = ["Swift.Runtime", "SwiftBindings.Apple", "SwiftBindingsTestLib", "LoadContextProbe"];

    private static readonly Dictionary<string, Func<bool>> Scenarios = new(StringComparer.Ordinal)
    {
        ["default-then-noncollectible"] = () => OwnerThenRefused(Lane.Default(), () => Lane.Custom("second", collectible: false, BindingAndProbe), "SwiftBindingsTestLib"),
        ["default-then-collectible"] = () => OwnerThenRefused(Lane.Default(), () => Lane.Custom("second", collectible: true, BindingAndProbe), "SwiftBindingsTestLib"),
        ["noncollectible-then-default"] = () => OwnerThenRefused(Lane.Custom("owner", collectible: false, BindingAndProbe), Lane.Default, "SwiftBindingsTestLib"),
        ["collectible-then-default"] = () => OwnerThenRefused(Lane.Custom("owner", collectible: true, BindingAndProbe), Lane.Default, "SwiftBindingsTestLib", unloadOwner: true),
        ["collectible-then-collectible"] = () => OwnerThenRefused(Lane.Custom("owner", collectible: true, BindingAndProbe), () => Lane.Custom("second", collectible: true, BindingAndProbe), "SwiftBindingsTestLib", unloadOwner: true),
        ["private-runtime-refused"] = () => OwnerThenRefused(Lane.Default(), () => Lane.Custom("private-runtime", collectible: true, Everything), "SwiftBindings.Runtime"),
        ["private-runtime-first"] = () => OwnerThenRefused(Lane.Custom("private-runtime", collectible: false, Everything), Lane.Default, "SwiftBindings.Runtime"),
        ["plugin-shared-binding"] = PluginSharedBinding,
        ["measure-keepalive-binding-init"] = () => MeasureKeepAlive(attach: false),
        ["measure-keepalive-binding-attached"] = () => MeasureKeepAlive(attach: true),
        ["measure-keepalive-private-runtime"] = MeasurePrivateRuntimeKeepAlive,
    };

    public static int Main(string[] args)
    {
        var scenario = args.Length == 2 && args[0] == "--scenario" ? args[1] : null;
        if (args.Length == 1 && args[0] == "--list")
        {
            foreach (var name in Scenarios.Keys) Console.WriteLine(name);
            return 0;
        }
        if (scenario == null || !Scenarios.TryGetValue(scenario, out var run))
        {
            Console.Error.WriteLine("usage: LoadContextHost --scenario <name> | --list");
            return 2;
        }

        // The default context finds our assemblies only through this handler, so it loads one only
        // when a scenario asks it to.
        AssemblyLoadContext.Default.Resolving += (context, name) =>
            Staged.Contains(name.Name) ? context.LoadFromAssemblyPath(Path.Combine(LcDirectory, name.Name + ".dll")) : null;

        Console.WriteLine($"LC-START {scenario} ({System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, " +
            $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");
        bool passed;
        try
        {
            passed = run();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"LC-FAIL {scenario}: unexpected {ex}");
            passed = false;
        }
        PrintOwners();
        Console.WriteLine($"LC-RESULT {scenario} {(passed ? "PASS" : "FAIL")}");
        Console.Out.Flush();
        return passed ? 0 : 1;
    }

    /// <summary>
    /// The owner registers callbacks and proves they work; a second copy then tries to initialize
    /// and must be refused before it registers anything: the vtables Swift holds are read back
    /// before and after and must still be the owner's, and the owner's conformers and closure
    /// destruction must keep working afterwards.
    /// </summary>
    private static bool OwnerThenRefused(Lane owner, Func<Lane> second, string expectedRegistration, bool unloadOwner = false)
    {
        var ok = true;
        Step(owner, "Init");
        Step(owner, "Attach");
        ok &= Expect(owner, "Fire", "ok", "10");
        var ownerHandles = ParseHandles(Step(owner, "Handles"));

        var before = NativeVtables.ReadAll();
        PrintNative("before", before);
        ok &= NativeTablesBelongTo(before, ownerHandles, "before the second copy");

        var attempt = AttemptSecondCopy(owner, second, expectedRegistration, before, ownerHandles, out var otherWeak);
        if (attempt == null)
            return false; // unrefused (pre-fix behaviour), already reported
        ok &= attempt.Value;

        if (unloadOwner)
        {
            // The owner's claim holds its assembly, which keeps a collectible owner context alive: Swift
            // still holds its callbacks.
            var ownerWeak = owner.UnloadIfCollectible()!;
            var alive = CollectAndProbe(ownerWeak);
            Console.WriteLine($"LC-MEASURE owner context alive after Unload() and full collections: {alive}");
            if (!alive)
            {
                Console.WriteLine("LC-FAIL the collectible owner context was collected while Swift holds its callbacks");
                return false;
            }
        }

        ok &= Expect(owner, "Fire", "ok", "20");
        ok &= Expect(owner, "ReleaseClosure", "ok");
        ok &= Expect(owner, "RoundTripClosure", "ok");
        ok &= NativeTablesBelongTo(NativeVtables.ReadAll(), ownerHandles, "at the end");

        // Measured here, after the attempt's frame (and the refusal exceptions it held) is gone.
        if (otherWeak != null)
            Console.WriteLine($"LC-MEASURE refused collectible context alive after Unload() and full collections: {CollectAndProbe(otherWeak)}");
        return ok;
    }

    /// <summary>
    /// Has a second copy initialize and register conformers. Returns whether it was refused cleanly,
    /// or <c>null</c> when it was not refused at all (after reporting what that did to Swift's tables
    /// and to the owner). Kept out of line so nothing from the refused context outlives it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool? AttemptSecondCopy(Lane owner, Func<Lane> second, string expectedRegistration,
        Dictionary<string, NativeVtables.Entry> before, Dictionary<string, IntPtr> ownerHandles, out WeakReference? otherWeak)
    {
        var ok = true;
        var other = second();
        otherWeak = null;
        var init = other.TryCall("Init");
        if (init.Error == null)
        {
            // Pre-fix behaviour: the copy initialized. Let it register, show what Swift now holds,
            // then show what that does to the owner's closures and conformers (the last normally
            // fail-fasts the process).
            Console.WriteLine($"LC-RED {other.Name}: second copy initialized without refusal: {init.Result}");
            Console.WriteLine($"LC-RED {other.Name}: Attach -> {other.TryCall("Attach").Describe()}");
            var overwritten = NativeVtables.ReadAll();
            PrintNative("after-unrefused-second", overwritten);
            foreach (var (name, entry) in overwritten)
            {
                if (entry != before[name])
                    Console.WriteLine($"LC-RED {name}: Swift's table changed from {before[name]} to {entry}");
            }
            Console.Out.Flush();
            Console.WriteLine($"LC-RED owner ReleaseClosure after the second copy -> {owner.TryCall("ReleaseClosure").Describe()}");
            Console.Out.Flush();
            Console.WriteLine($"LC-RED owner Fire after the second copy -> {owner.TryCall("Fire", "20").Describe()}");
            return null;
        }

        var conflict = FindConflict(init.Error);
        if (conflict == null)
        {
            Console.WriteLine($"LC-FAIL {other.Name}: Init failed, but not with {ConflictTypeName}: {init.Error}");
            ok = false;
        }
        else
        {
            Console.WriteLine($"LC-REFUSED {other.Name}: {conflict.Message}");
            if (!conflict.Message.Contains(expectedRegistration, StringComparison.Ordinal))
            {
                Console.WriteLine($"LC-FAIL refusal does not name {expectedRegistration}");
                ok = false;
            }
            ok &= MentionsBothContexts(conflict.Message, owner, other);
        }

        // The refused copy stays refused: its proxies (and their vtable installs) are unreachable.
        var attach = other.TryCall("Attach");
        Console.WriteLine($"LC-STEP {other.Name} Attach after refusal -> {attach.Describe()}");
        if (attach.Error == null)
        {
            Console.WriteLine("LC-FAIL the refused copy could still register its conformers");
            ok = false;
        }

        var after = NativeVtables.ReadAll();
        PrintNative("after", after);
        foreach (var (name, entry) in after)
        {
            if (entry != before[name])
            {
                Console.WriteLine($"LC-FAIL {name}: Swift's table changed from {before[name]} to {entry} across the refused attempt");
                ok = false;
            }
        }
        ok &= NativeTablesBelongTo(after, ownerHandles, "after the refused copy");

        otherWeak = other.UnloadIfCollectible();
        return ok;
    }

    /// <summary>
    /// A collectible plugin implements a protocol interface from the binding the default context
    /// shares. The plugin context must stay alive, and its conformer keep receiving calls, for as
    /// long as Swift holds that conformer, even after the plugin context is asked to unload.
    /// </summary>
    private static bool PluginSharedBinding()
    {
        var ok = true;
        var weak = StartPlugin();
        var aliveWhileHeld = CollectAndProbe(weak);
        Console.WriteLine($"LC-MEASURE plugin context alive after Unload() while Swift holds its conformer: {aliveWhileHeld}");
        if (!aliveWhileHeld)
        {
            Console.WriteLine("LC-FAIL the plugin context was collected while Swift holds its conformer");
            return false;
        }

        var source = AppContext.GetData("LoadContextProbe.PluginSource")!;
        source.GetType().GetMethod("FireViaStrongChild")!.Invoke(source, [42]);
        var delivered = AppContext.GetData("LoadContextProbe.PluginLastValue");
        Console.WriteLine($"LC-STEP plugin conformer after Unload() received {delivered ?? "(nothing)"}");
        if (delivered is not int value || value != 42)
        {
            Console.WriteLine("LC-FAIL the plugin conformer did not receive Swift's call");
            ok = false;
        }

        // Swift lets go of the conformer; nothing else holds the plugin.
        source.GetType().GetProperty("StrongChildDelegate")!.SetValue(source, null);
        AppContext.SetData("LoadContextProbe.PluginSource", null);
        source = null;
        Console.WriteLine($"LC-MEASURE plugin context alive after Swift released its conformer and full collections: {CollectAndProbe(weak)}");
        return ok;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference StartPlugin()
    {
        var plugin = Lane.Custom("plugin", collectible: true, ["LoadContextProbe"]);
        Step(plugin, "AttachPlugin");
        return plugin.UnloadIfCollectible()!;
    }

    /// <summary>
    /// Whether a collectible context that owns the binding is collected after Unload(), measured
    /// with the binding initialized only, or with conformers registered (vtables installed).
    /// </summary>
    /// <remarks>
    /// Report-only: the claim keeps a collectible owner alive by design, and the same scenarios run
    /// on a runtime without the claim are what show whether anything else (the pinned vtable handles,
    /// the runtime's default-context event subscription) already roots the context.
    /// </remarks>
    private static bool MeasureKeepAlive(bool attach)
    {
        var weak = StartCollectibleOwner(attach, out var callbacksWorked);
        Console.WriteLine($"LC-MEASURE collectible binding owner ({(attach ? "conformers registered" : "module initialized only")}) alive after Unload() and full collections: {CollectAndProbe(weak)}");
        return callbacksWorked;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference StartCollectibleOwner(bool attach, out bool callbacksWorked)
    {
        var owner = Lane.Custom("owner", collectible: true, BindingAndProbe);
        Step(owner, "Init");
        callbacksWorked = true;
        if (attach)
        {
            Step(owner, "Attach");
            callbacksWorked = Expect(owner, "Fire", "ok", "5");
        }
        return owner.UnloadIfCollectible()!;
    }

    /// <summary>Whether a collectible context holding only a private SwiftBindings.Runtime (initialized) is collected.</summary>
    private static bool MeasurePrivateRuntimeKeepAlive()
    {
        var weak = StartPrivateRuntime();
        Console.WriteLine($"LC-MEASURE collectible private runtime (module initialized, no binding) alive after Unload() and full collections: {CollectAndProbe(weak)}");
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference StartPrivateRuntime()
    {
        var context = new HarnessContext("private-runtime", collectible: true, ["Swift.Runtime"]);
        var runtime = context.LoadFromAssemblyName(new AssemblyName("Swift.Runtime"));
        // Reading a static field runs the runtime's module initializer.
        _ = runtime.GetType("Swift.Runtime.RuntimeContract", throwOnError: true)!.GetField("Version")!.GetValue(null);
        Console.WriteLine($"LC-STEP private runtime loaded in {context} and its module initializer run");
        var weak = new WeakReference(context, trackResurrection: true);
        context.Unload();
        return weak;
    }

    // ---- helpers

    private static string Step(Lane lane, string method, params string[] args)
    {
        var call = lane.TryCall(method, args);
        Console.WriteLine($"LC-STEP {lane.Name} {method} -> {call.Describe()}");
        if (call.Error != null)
            throw new InvalidOperationException($"{lane.Name}.{method} failed", call.Error);
        return call.Result!;
    }

    private static bool Expect(Lane lane, string method, string expected, params string[] args)
    {
        var call = lane.TryCall(method, args);
        Console.WriteLine($"LC-STEP {lane.Name} {method}({string.Join(",", args)}) -> {call.Describe()}");
        if (call.Error == null && call.Result == expected)
            return true;
        Console.WriteLine($"LC-FAIL {lane.Name}.{method} expected '{expected}'");
        return false;
    }

    private static Dictionary<string, IntPtr> ParseHandles(string text)
        => text.Split(';').Select(pair => pair.Split('='))
            .ToDictionary(p => p[0], p => (IntPtr)Convert.ToInt64(p[1], 16));

    private static bool NativeTablesBelongTo(Dictionary<string, NativeVtables.Entry> tables, Dictionary<string, IntPtr> ownerHandles, string when)
    {
        var ok = true;
        foreach (var (name, entry) in tables)
        {
            if (!entry.IsVtable)
            {
                // A hook carries no owner handle; it must be installed, and unchanged across a refused
                // attempt (checked by the caller). Which runtime it calls is proved by ReleaseClosure.
                if (entry.Word0 == IntPtr.Zero)
                {
                    Console.WriteLine($"LC-FAIL {name} {when}: no hook is installed");
                    ok = false;
                }
                continue;
            }
            if (entry.Word0 != ownerHandles[name] || entry.Word1 == IntPtr.Zero)
            {
                Console.WriteLine($"LC-FAIL {name} {when}: Swift holds {entry}, the owner's handle is 0x{ownerHandles[name]:X}");
                ok = false;
            }
        }
        if (ok)
            Console.WriteLine($"LC-NATIVE {when}: every vtable's csVTHandle is the owner's pinned handle and the destroy hook is installed");
        return ok;
    }

    private static void PrintNative(string label, Dictionary<string, NativeVtables.Entry> tables)
    {
        foreach (var (name, entry) in tables)
            Console.WriteLine($"LC-NATIVE {label} {name}: {entry}");
    }

    private static bool MentionsBothContexts(string message, Lane owner, Lane other)
    {
        if (message.Contains($"\"{owner.ContextName}\"", StringComparison.Ordinal)
            && message.Contains($"\"{other.ContextName}\"", StringComparison.Ordinal))
            return true;
        Console.WriteLine($"LC-FAIL refusal does not name both load contexts ({owner.ContextName}, {other.ContextName})");
        return false;
    }

    internal static Exception? FindConflict(Exception? ex)
    {
        for (; ex != null; ex = ex.InnerException)
        {
            if (ex.GetType().FullName == ConflictTypeName)
                return ex;
        }
        return null;
    }

    private static bool CollectAndProbe(WeakReference weak)
    {
        for (var i = 0; i < 12 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        return weak.IsAlive;
    }

    private static void PrintOwners()
    {
        if (AppContext.GetData(OwnerTableKey) is not Dictionary<string, Assembly> table)
        {
            Console.WriteLine("LC-OWNERS (no owner table: this runtime does not claim native registrations)");
            return;
        }
        lock (table)
        {
            foreach (var (key, assembly) in table.OrderBy(e => e.Key, StringComparer.Ordinal))
                Console.WriteLine($"LC-OWNERS {key} -> {assembly.GetName().Name} in {AssemblyLoadContext.GetLoadContext(assembly)}");
        }
    }
}

/// <summary>A load context that loads the named staged assemblies privately and shares everything else with Default.</summary>
internal sealed class HarnessContext(string name, bool collectible, IEnumerable<string> privateAssemblies)
    : AssemblyLoadContext(name, collectible)
{
    private readonly HashSet<string> _private = new(privateAssemblies, StringComparer.Ordinal);

    protected override Assembly? Load(AssemblyName assemblyName)
        => assemblyName.Name != null && _private.Contains(assemblyName.Name)
            ? LoadFromAssemblyPath(Path.Combine(Program.LcDirectory, assemblyName.Name + ".dll"))
            : null;
}

/// <summary>The probe as loaded in one context, called by reflection.</summary>
internal sealed class Lane
{
    private AssemblyLoadContext? _context;
    private Type? _probe;
    // After Unload() the lane holds the probe only weakly, so it can keep calling an owner that
    // something else keeps alive without itself being what keeps it alive.
    private WeakReference<Type>? _unloadedProbe;

    private Lane(string name, AssemblyLoadContext context)
    {
        Name = name;
        _context = context;
        ContextName = context.Name ?? "?";
    }

    public string Name { get; }
    public string ContextName { get; }

    public static Lane Default() => new("default", AssemblyLoadContext.Default);

    public static Lane Custom(string name, bool collectible, string[] privateAssemblies)
        => new(name, new HarnessContext(name, collectible, privateAssemblies));

    internal readonly record struct Call(string? Result, Exception? Error)
    {
        public string Describe() => Error == null
            ? Result ?? "(null)"
            : $"threw {string.Join(" <- ", Chain(Error))}";

        private static IEnumerable<string> Chain(Exception ex)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
                yield return e.GetType().FullName ?? e.GetType().Name;
        }
    }

    public Call TryCall(string method, params string[] args)
    {
        try
        {
            var probe = _probe ?? ResolveUnloadedProbe() ?? (_probe = LoadProbe());
            var info = probe.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException(probe.FullName, method);
            return new Call((string?)info.Invoke(null, args), null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            return new Call(null, ex.InnerException);
        }
        catch (Exception ex)
        {
            return new Call(null, ex);
        }
    }

    private Type? ResolveUnloadedProbe()
    {
        if (_unloadedProbe == null)
            return null;
        return _unloadedProbe.TryGetTarget(out var probe)
            ? probe
            : throw new InvalidOperationException($"{Name} was unloaded and collected");
    }

    private Type LoadProbe()
    {
        var context = _context ?? throw new InvalidOperationException($"{Name} was unloaded");
        var assembly = context.LoadFromAssemblyName(new AssemblyName("LoadContextProbe"));
        return assembly.GetType("LoadContextProbe.Probe", throwOnError: true)!;
    }

    /// <summary>Starts unloading a collectible context and drops this lane's references to it.</summary>
    public WeakReference? UnloadIfCollectible()
    {
        if (_context is not { IsCollectible: true } context)
            return null;
        var weak = new WeakReference(context, trackResurrection: true);
        if (_probe != null)
            _unloadedProbe = new WeakReference<Type>(_probe);
        context.Unload();
        _context = null;
        _probe = null;
        return weak;
    }
}
