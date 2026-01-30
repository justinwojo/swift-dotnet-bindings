// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Registry that maps Swift existential containers to their corresponding C# proxy objects.
/// This enables Swift callbacks to find the C# proxy that implements protocol methods.
///
/// When a C# object implements a Swift protocol via a proxy:
/// 1. The proxy wraps the C# implementation and creates an EveryProtocol instance
/// 2. The proxy registers itself with this registry, keyed by the EveryProtocol's handle
/// 3. When Swift calls a protocol method, it passes the existential container
/// 4. The receiver method extracts the EveryProtocol handle and looks up the proxy here
/// 5. The proxy then delegates to the actual C# implementation
/// </summary>
public static class SwiftObjectRegistry
{
    // Maps EveryProtocol handle -> weak reference to proxy object
    // Using weak references so proxies can be garbage collected when no longer referenced
    private static readonly ConcurrentDictionary<IntPtr, WeakReference<object>> _registry = new();

    // Strong reference storage for proxies that shouldn't be collected while registered
    // Key is the same EveryProtocol handle, value is the proxy object
    private static readonly ConcurrentDictionary<IntPtr, object> _strongRegistry = new();

    /// <summary>
    /// Registers a proxy object with its corresponding EveryProtocol handle.
    /// Uses weak references by default so proxies can be garbage collected.
    /// </summary>
    /// <typeparam name="TProxy">The type of the proxy object.</typeparam>
    /// <param name="handle">The EveryProtocol instance handle.</param>
    /// <param name="proxy">The proxy object that implements the protocol.</param>
    public static void Register<TProxy>(IntPtr handle, TProxy proxy) where TProxy : class
    {
        if (handle == IntPtr.Zero)
            throw new ArgumentException("Handle cannot be zero", nameof(handle));
        if (proxy == null)
            throw new ArgumentNullException(nameof(proxy));

        _registry[handle] = new WeakReference<object>(proxy);
    }

    /// <summary>
    /// Registers a proxy object with strong reference, preventing garbage collection.
    /// Use this when the proxy must remain alive for the duration of an async operation
    /// or while Swift holds a reference to the existential container.
    /// </summary>
    /// <typeparam name="TProxy">The type of the proxy object.</typeparam>
    /// <param name="handle">The EveryProtocol instance handle.</param>
    /// <param name="proxy">The proxy object that implements the protocol.</param>
    public static void RegisterStrong<TProxy>(IntPtr handle, TProxy proxy) where TProxy : class
    {
        if (handle == IntPtr.Zero)
            throw new ArgumentException("Handle cannot be zero", nameof(handle));
        if (proxy == null)
            throw new ArgumentNullException(nameof(proxy));

        _strongRegistry[handle] = proxy;
        _registry[handle] = new WeakReference<object>(proxy);
    }

    /// <summary>
    /// Unregisters a proxy object, removing both weak and strong references.
    /// </summary>
    /// <param name="handle">The EveryProtocol instance handle.</param>
    public static void Unregister(IntPtr handle)
    {
        _registry.TryRemove(handle, out _);
        _strongRegistry.TryRemove(handle, out _);
    }

    /// <summary>
    /// Releases the strong reference for a handle, allowing the proxy to be garbage collected
    /// if there are no other references.
    /// </summary>
    /// <param name="handle">The EveryProtocol instance handle.</param>
    public static void ReleaseStrong(IntPtr handle)
    {
        _strongRegistry.TryRemove(handle, out _);
    }

    /// <summary>
    /// Attempts to get a proxy object for the given EveryProtocol handle.
    /// </summary>
    /// <typeparam name="TProxy">The expected type of the proxy object.</typeparam>
    /// <param name="handle">The EveryProtocol instance handle.</param>
    /// <param name="proxy">The proxy object if found.</param>
    /// <returns>True if a valid proxy was found, false otherwise.</returns>
    public static bool TryGetProxy<TProxy>(IntPtr handle, out TProxy? proxy) where TProxy : class
    {
        proxy = null;

        if (handle == IntPtr.Zero)
            return false;

        // Check strong references first (faster and always valid)
        if (_strongRegistry.TryGetValue(handle, out var strongRef))
        {
            proxy = strongRef as TProxy;
            return proxy != null;
        }

        // Fall back to weak references
        if (_registry.TryGetValue(handle, out var weakRef))
        {
            if (weakRef.TryGetTarget(out var target))
            {
                proxy = target as TProxy;
                return proxy != null;
            }
            else
            {
                // Weak reference target was collected, clean up
                _registry.TryRemove(handle, out _);
            }
        }

        return false;
    }

    /// <summary>
    /// Gets a proxy object for the given EveryProtocol handle.
    /// Throws if the proxy is not found or has been garbage collected.
    /// </summary>
    /// <typeparam name="TProxy">The expected type of the proxy object.</typeparam>
    /// <param name="handle">The EveryProtocol instance handle.</param>
    /// <returns>The proxy object.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the proxy is not found.</exception>
    public static TProxy GetProxy<TProxy>(IntPtr handle) where TProxy : class
    {
        if (!TryGetProxy<TProxy>(handle, out var proxy) || proxy == null)
        {
            throw new InvalidOperationException(
                $"No proxy of type {typeof(TProxy).Name} found for handle {handle}. " +
                "The proxy may have been garbage collected or was never registered.");
        }
        return proxy;
    }

    /// <summary>
    /// Gets a proxy object from an existential container's payload.
    /// The payload's first word contains the EveryProtocol pointer for class types,
    /// or for inline values, we need to extract the handle differently.
    /// </summary>
    /// <typeparam name="TProxy">The expected type of the proxy object.</typeparam>
    /// <param name="container">The existential container.</param>
    /// <returns>The proxy object.</returns>
    public static TProxy GetProxyFromContainer<TProxy>(IExistentialContainer container) where TProxy : class
    {
        // For class types (like EveryProtocol), Payload0 contains the object pointer
        return GetProxy<TProxy>(container.Payload0);
    }

    /// <summary>
    /// Attempts to get a proxy from an existential container.
    /// </summary>
    /// <typeparam name="TProxy">The expected type of the proxy object.</typeparam>
    /// <param name="container">The existential container.</param>
    /// <param name="proxy">The proxy object if found.</param>
    /// <returns>True if a valid proxy was found.</returns>
    public static bool TryGetProxyFromContainer<TProxy>(IExistentialContainer container, out TProxy? proxy)
        where TProxy : class
    {
        return TryGetProxy(container.Payload0, out proxy);
    }

    /// <summary>
    /// Gets the count of registered proxies (for diagnostics).
    /// </summary>
    public static int Count => _registry.Count;

    /// <summary>
    /// Gets the count of strongly-held proxies (for diagnostics).
    /// </summary>
    public static int StrongCount => _strongRegistry.Count;

    /// <summary>
    /// Cleans up any expired weak references.
    /// This is called automatically during lookups but can be called explicitly.
    /// </summary>
    public static void Cleanup()
    {
        var toRemove = new List<IntPtr>();

        foreach (var kvp in _registry)
        {
            if (!kvp.Value.TryGetTarget(out _))
            {
                toRemove.Add(kvp.Key);
            }
        }

        foreach (var handle in toRemove)
        {
            _registry.TryRemove(handle, out _);
        }
    }
}
