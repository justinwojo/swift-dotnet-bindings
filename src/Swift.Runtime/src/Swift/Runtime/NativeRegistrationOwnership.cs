// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;

namespace Swift.Runtime;

/// <summary>
/// One managed owner per process for each set of process-global native callback registrations.
/// </summary>
/// <remarks>
/// <para>
/// Several registrations live in native state that every load context in the process shares: a
/// binding's reverse-dispatch vtables (<c>SBW_Set{P}_vtable</c>, one Swift global per protocol in
/// the binding's wrapper image, holding raw <c>[UnmanagedCallersOnly]</c> pointers) and the runtime's
/// closure-context destroy hook (one global in the SwiftBindingsRuntime image). A second copy of the
/// same assembly in another <see cref="AssemblyLoadContext"/> would overwrite them, and the first
/// copy's objects would then call back into code that does not know their handles.
/// </para>
/// <para>
/// So the first load context to initialize a binding module (or <c>Swift.Runtime</c> itself) owns
/// it for the rest of the process, and any other copy is refused with
/// <see cref="SwiftLoadContextConflictException"/> before it makes a single native call. The
/// generated module initializer claims as its first statement, and each generated protocol proxy
/// re-checks in its first static field initializer, ahead of any native metadata access.
/// </para>
/// <para>
/// The owner table lives in <see cref="AppContext"/> data, which belongs to <c>System.Private.CoreLib</c>
/// and is therefore shared by every load context and by every copy of this assembly. It holds the
/// owner <see cref="Assembly"/> strongly, which keeps a collectible owner context alive for the rest
/// of the process. That is intended: Swift may hold the owner's callbacks forever.
/// </para>
/// <para>
/// On Mono and NativeAOT there is only the default load context, so the first claim always succeeds
/// and every later one is the same assembly: one dictionary lookup per module and per proxy type.
/// </para>
/// </remarks>
public static class NativeRegistrationOwnership
{
    /// <summary>The <see cref="AppContext"/> data key of the process-wide owner table.</summary>
    internal const string OwnerTableKey = "SwiftBindings.NativeRegistrationOwners.v1";

    internal const string RuntimeKey = "runtime:SwiftBindings.Runtime";

    private const string ModuleKeyPrefix = "module:";

    // This copy's cached view of the process-wide table; every copy resolves the same instance.
    private static Dictionary<string, Assembly>? s_ownerTable;

    /// <summary>
    /// Claims the process-global native registrations of the Swift binding module
    /// <paramref name="moduleName"/> for <paramref name="assembly"/>. Generated bindings call this as
    /// the first statement of their module initializer and from each protocol proxy's static
    /// initialization.
    /// </summary>
    /// <param name="moduleName">The Swift module whose wrapper image holds the registrations.</param>
    /// <param name="assembly">The assembly that carries the module's generated code.</param>
    /// <returns>
    /// <c>true</c> when this call made the claim; <c>false</c> when <paramref name="assembly"/>
    /// already owned the module.
    /// </returns>
    /// <exception cref="SwiftLoadContextConflictException">
    /// Another assembly instance, normally the same binding loaded into a different load context,
    /// already owns the module.
    /// </exception>
    public static bool ClaimModule(string moduleName, Assembly assembly)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleName);
        ArgumentNullException.ThrowIfNull(assembly);
        return Claim(ModuleKeyPrefix + moduleName, $"Swift binding module '{moduleName}'", assembly);
    }

    /// <summary>Claims the runtime's own process-global registrations (the closure-context destroy hook).</summary>
    internal static bool ClaimRuntime(Assembly runtimeAssembly)
        => Claim(RuntimeKey, "SwiftBindings.Runtime (closure-context destroy callback)", runtimeAssembly);

    /// <summary>The assembly that owns <paramref name="moduleName"/>, or <c>null</c> when no context has claimed it.</summary>
    internal static Assembly? GetModuleOwner(string moduleName)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleName);
        var table = GetOwnerTable();
        lock (table)
            return table.TryGetValue(ModuleKeyPrefix + moduleName, out var owner) ? owner : null;
    }

    private static bool Claim(string key, string registration, Assembly claimant)
    {
        var table = GetOwnerTable();
        Assembly? owner;
        lock (table)
        {
            if (!table.TryGetValue(key, out owner))
            {
                table.Add(key, claimant);
                return true;
            }
        }

        if (ReferenceEquals(owner, claimant))
            return false;

        throw new SwiftLoadContextConflictException(registration, owner, claimant);
    }

    // Locking on typeof(AppContext) is deliberate: the lock must be the one object every copy of this
    // assembly in every load context sees, and a CoreLib RuntimeType is exactly that. It is held only to
    // create the table once per process.
    [SuppressMessage("Reliability", "CA2002:Do not lock on objects with weak identity",
        Justification = "The owner table must be created under a lock shared by every load context.")]
    private static Dictionary<string, Assembly> GetOwnerTable()
    {
        var table = Volatile.Read(ref s_ownerTable);
        if (table != null)
            return table;

        lock (typeof(AppContext))
        {
            table = AppContext.GetData(OwnerTableKey) as Dictionary<string, Assembly>;
            if (table == null)
            {
                table = new Dictionary<string, Assembly>(StringComparer.Ordinal);
                AppContext.SetData(OwnerTableKey, table);
            }
        }

        Volatile.Write(ref s_ownerTable, table);
        return table;
    }

    internal static string DescribeLoadContext(Assembly assembly)
    {
        try
        {
            var context = AssemblyLoadContext.GetLoadContext(assembly);
            if (context == null)
                return "(no load context)";
            var kind = context == AssemblyLoadContext.Default
                ? "default"
                : context.IsCollectible ? "collectible" : "non-collectible";
            return $"{context} ({kind})";
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException)
        {
            return "(load contexts unsupported)";
        }
    }

    internal static string DescribeAssembly(Assembly assembly) => $"'{assembly.FullName}'";
}

/// <summary>
/// Thrown when a second copy of a Swift binding, or of <c>SwiftBindings.Runtime</c>, initializes in a
/// load context other than the one that already owns its process-global native callback
/// registrations. The refusal happens before the second copy makes any native call.
/// </summary>
public sealed class SwiftLoadContextConflictException : SwiftRuntimeException
{
    /// <summary>The registration set both copies tried to own.</summary>
    public string Registration { get; }

    /// <summary>The assembly that owns the registrations.</summary>
    public string OwnerAssembly { get; }

    /// <summary>The load context of the owner assembly.</summary>
    public string OwnerLoadContext { get; }

    /// <summary>The assembly that was refused.</summary>
    public string RefusedAssembly { get; }

    /// <summary>The load context of the refused assembly.</summary>
    public string RefusedLoadContext { get; }

    internal SwiftLoadContextConflictException(string registration, Assembly owner, Assembly refused)
        : this(registration,
            NativeRegistrationOwnership.DescribeAssembly(owner),
            NativeRegistrationOwnership.DescribeLoadContext(owner),
            NativeRegistrationOwnership.DescribeAssembly(refused),
            NativeRegistrationOwnership.DescribeLoadContext(refused))
    {
    }

    private SwiftLoadContextConflictException(
        string registration, string ownerAssembly, string ownerContext, string refusedAssembly, string refusedContext)
        : base($"{registration} is already owned by {ownerAssembly} in load context {ownerContext}. " +
               $"The copy {refusedAssembly} in load context {refusedContext} is refused before it registers " +
               "anything native: its callback registrations are process-global, and only one load context " +
               "per process may own them. Load the binding and SwiftBindings.Runtime once, from the default " +
               "load context, and share them with plugin contexts instead of loading private copies.")
    {
        Registration = registration;
        OwnerAssembly = ownerAssembly;
        OwnerLoadContext = ownerContext;
        RefusedAssembly = refusedAssembly;
        RefusedLoadContext = refusedContext;
    }
}
