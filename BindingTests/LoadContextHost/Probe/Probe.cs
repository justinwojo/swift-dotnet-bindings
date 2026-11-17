// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using SwiftBindingsTestLib;

namespace LoadContextProbe;

/// <summary>
/// Runs inside one load context and drives the binding that context resolves. Every entry point
/// takes and returns strings so the host can call it by reflection without loading the binding;
/// a returned string starting with <c>FAIL</c> is a failed check. Exceptions (a refusal) propagate.
/// </summary>
public static class Probe
{
    // Sources and implementations an owner context keeps registered with Swift for the whole run.
    private static InheritedDelegateSource? s_inherited;
    private static InheritedNonEmptyChildSource? s_nonEmpty;
    private static CrossModuleInheritedDelegateSource? s_crossModule;
    private static ChildImpl? s_childImpl;
    private static NonEmptyChildImpl? s_nonEmptyImpl;
    private static CrossModuleChildImpl? s_crossModuleImpl;
    private static CallbackRelay? s_relay;
    private static Func<long, long>? s_stored;
    private static int s_storedDestroys;
    private static int s_observerInstalled;

    /// <summary>Touches the binding, which runs its module initializer in this context.</summary>
    public static string Init()
    {
        using var relay = new CallbackRelay();
        return $"binding {Describe(typeof(CallbackRelay).Assembly)}; runtime {Describe(typeof(Swift.Runtime.SwiftFrameworkResolver).Assembly)}";
    }

    /// <summary>
    /// Registers C# conformers with Swift for a same-module inherited protocol, a non-empty child
    /// and a cross-module parent, plus an escaping closure Swift stores. This runs the proxies'
    /// static constructors, which install the reverse-dispatch vtables.
    /// </summary>
    public static string Attach()
    {
        InstallDestroyObserver();
        s_childImpl = new ChildImpl();
        s_inherited = new InheritedDelegateSource();
        s_inherited.StrongChildDelegate = s_childImpl;

        s_nonEmptyImpl = new NonEmptyChildImpl();
        s_nonEmpty = new InheritedNonEmptyChildSource();
        s_nonEmpty.ChildDelegate = s_nonEmptyImpl;

        s_crossModuleImpl = new CrossModuleChildImpl();
        s_crossModule = new CrossModuleInheritedDelegateSource();
        s_crossModule.ChildDelegate = s_crossModuleImpl;

        s_relay = new CallbackRelay();
        s_stored = new Offset(1000).Apply;
        s_storedDestroys = 0;
        s_relay.Store(0, s_stored);
        return "attached";
    }

    /// <summary>Has Swift call every registered conformer and the stored closure with <paramref name="value"/>.</summary>
    public static string Fire(string value)
    {
        var v = int.Parse(value);
        var failures = new List<string>();

        s_inherited!.FireViaStrongChild(value: v);
        Check(failures, "inherited parent via strong child", s_inherited.LastSlotFired == 3 && s_childImpl!.Last == v,
            $"slot {s_inherited.LastSlotFired}, last {s_childImpl!.Last}");

        s_nonEmpty!.FireParentViaChild(value: v + 1);
        Check(failures, "inherited parent via non-empty child", s_nonEmptyImpl!.LastParent == v + 1,
            $"last {s_nonEmptyImpl.LastParent}");
        s_nonEmpty.FireChildOwnMethod(value: v + 2);
        Check(failures, "non-empty child's own requirement", s_nonEmptyImpl.LastChild == v + 2,
            $"last {s_nonEmptyImpl.LastChild}");

        s_crossModule!.FireViaCrossModuleChild(value: v + 3);
        Check(failures, "cross-module parent via child", s_crossModule.LastSlotFired == 1 && s_crossModuleImpl!.Last == v + 3,
            $"slot {s_crossModule.LastSlotFired}, last {s_crossModuleImpl!.Last}");

        var invoked = s_relay!.Invoke(0, v);
        Check(failures, "stored closure", invoked == v + 1000, $"returned {invoked}");

        return failures.Count == 0 ? "ok" : "FAIL " + string.Join("; ", failures);
    }

    /// <summary>
    /// Has Swift release the stored closure, and checks the destroy reached THIS context's runtime
    /// (its <c>ContextDestroyed</c> observer), which is the runtime whose hook Swift must call.
    /// </summary>
    public static string ReleaseClosure()
    {
        s_relay!.ClearAll();
        var destroys = Volatile.Read(ref s_storedDestroys);
        return destroys == 1 ? "ok" : $"FAIL the stored closure's context reached this runtime's destroy hook {destroys} times (expected 1)";
    }

    /// <summary>
    /// Creates and releases a fresh closure context; checks this runtime's destroy hook frees it.
    /// </summary>
    public static string RoundTripClosure()
    {
        InstallDestroyObserver();
        using var relay = new CallbackRelay();
        s_stored = new Offset(7).Apply;
        s_storedDestroys = 0;
        relay.Store(1, s_stored);
        var result = relay.Invoke(1, 1);
        relay.ClearAll();
        var destroys = Volatile.Read(ref s_storedDestroys);
        return result == 8 && destroys == 1 ? "ok" : $"FAIL invoke {result}, destroys {destroys}";
    }

    /// <summary>
    /// The pinned local-vtable handles this context's proxies put in the <c>csVTHandle</c> slot of
    /// each Swift vtable, as <c>name=0x…</c> pairs. The host compares them with Swift's tables.
    /// </summary>
    public static string Handles()
    {
        var bindingAssembly = typeof(CallbackRelay).Assembly;
        return string.Join(";",
            HandleOf(bindingAssembly, "SwiftBindingsTestLib.SwiftInterop.InheritedParentDelegateProxy", "_localVTableHandle", "inheritedParent"),
            HandleOf(bindingAssembly, "SwiftBindingsTestLib.SwiftInterop.CrossModuleInheritedChildDelegateProxy",
                "_localVTableHandle_xm_SwiftBindingsTestLibDependency_CrossModuleParentDelegate", "crossModuleParentInMain"),
            HandleOf(bindingAssembly, "SwiftBindingsTestLibDependency.SwiftInterop.CrossModuleParentDelegateProxy", "_localVTableHandle", "crossModuleParentInDependency"));
    }

    private static string HandleOf(Assembly assembly, string typeName, string field, string name)
    {
        var type = assembly.GetType(typeName, throwOnError: true)!;
        RuntimeHelpers.RunClassConstructor(type.TypeHandle);
        var handle = (GCHandle)type.GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        return $"{name}=0x{GCHandle.ToIntPtr(handle):X}";
    }

    // ---- Plugin topology: this assembly in a collectible context, the binding shared from Default.

    internal const string PluginSourceKey = "LoadContextProbe.PluginSource";
    internal const string PluginLastValueKey = "LoadContextProbe.PluginLastValue";

    /// <summary>
    /// Registers a conformer implemented in this (plugin) assembly with Swift through the shared
    /// binding's strong delegate slot, and parks the Swift source object in AppContext data. The
    /// source's type is the default context's, so parking it does not root this context; only
    /// Swift's hold on the conformer does.
    /// </summary>
    public static string AttachPlugin()
    {
        var source = new InheritedDelegateSource();
        source.StrongChildDelegate = new PluginChildImpl();
        AppContext.SetData(PluginSourceKey, source);
        return $"plugin {Describe(typeof(Probe).Assembly)} uses binding {Describe(typeof(CallbackRelay).Assembly)}";
    }

    private static void Check(List<string> failures, string what, bool ok, string detail)
    {
        if (!ok) failures.Add($"{what}: {detail}");
    }

    private static string Describe(Assembly assembly)
        => $"in {AssemblyLoadContext.GetLoadContext(assembly)}";

    private static void InstallDestroyObserver()
    {
        if (Interlocked.Exchange(ref s_observerInstalled, 1) != 0)
            return;
        // The runtime's destroy notification is internal; it fires on the runtime whose hook Swift called.
        var contextType = typeof(Swift.Runtime.SwiftFrameworkResolver).Assembly.GetType("Swift.Runtime.SwiftClosureContext", throwOnError: true)!;
        var destroyed = contextType.GetEvent("ContextDestroyed", BindingFlags.NonPublic | BindingFlags.Static)!;
        destroyed.GetAddMethod(nonPublic: true)!.Invoke(null, [new Action<IntPtr>(OnDestroyed)]);
    }

    private static void OnDestroyed(IntPtr context)
    {
        var stored = s_stored;
        if (stored != null && ReferenceEquals(GCHandle.FromIntPtr(context).Target, stored))
            Interlocked.Increment(ref s_storedDestroys);
    }

    private sealed class Offset(long by)
    {
        public long Apply(long value) => value + by;
    }

    private sealed class ChildImpl : IInheritedChildDelegate
    {
        public int Last { get; private set; } = int.MinValue;
        public void ParentDidNotify(int value) => Last = value;
    }

    private sealed class NonEmptyChildImpl : IInheritedNonEmptyChildDelegate
    {
        public int LastParent { get; private set; } = int.MinValue;
        public int LastChild { get; private set; } = int.MinValue;
        public void ParentDidNotify(int value) => LastParent = value;
        public void ChildDidNotify(int value) => LastChild = value;
    }

    private sealed class CrossModuleChildImpl : ICrossModuleInheritedChildDelegate
    {
        public int Last { get; private set; } = int.MinValue;
        public void CrossModuleDidNotify(int value) => Last = value;
    }

    private sealed class PluginChildImpl : IInheritedChildDelegate
    {
        public void ParentDidNotify(int value) => AppContext.SetData(PluginLastValueKey, value);
    }
}
