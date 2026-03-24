// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Provides access to ObjC runtime functions for resolving class metadata.
/// Used instead of fragile Swift overlay metadata accessor symbols ($sSo...CMa)
/// which can break when Apple merges framework dylibs (e.g., iOS 26).
/// </summary>
internal static class ObjCInterop
{
    private const string ObjCRuntime = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjCRuntime, EntryPoint = "objc_getClass")]
    private static extern IntPtr ObjCGetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string className);

    [DllImport(KnownLibraries.SwiftCore, EntryPoint = "swift_getObjCClassMetadata")]
    private static extern IntPtr SwiftGetObjCClassMetadata(IntPtr objcClass);

    /// <summary>
    /// Resolves the Swift type metadata for an ObjC class by name.
    /// Uses objc_getClass (stable ObjC runtime API) to find the class, then
    /// swift_getObjCClassMetadata (stable Swift runtime API in libswiftCore)
    /// to create proper Swift metadata with a valid value witness table.
    /// This is a stable alternative to $sSo...CMa Swift overlay metadata accessors.
    /// </summary>
    /// <param name="className">The ObjC class name (e.g., "NSURLResponse", "UIImage").</param>
    /// <returns>A TypeMetadata with valid VWT for the ObjC class.</returns>
    /// <exception cref="SwiftRuntimeException">Thrown if the class cannot be found.</exception>
    public static TypeMetadata GetTypeMetadata(string className)
    {
        ArgumentException.ThrowIfNullOrEmpty(className);

        var classPtr = ObjCGetClass(className);
        if (classPtr == IntPtr.Zero)
        {
            throw new SwiftRuntimeException($"objc_getClass failed to find class '{className}'. " +
                "The class may not be available on this platform.");
        }
        var metadataPtr = SwiftGetObjCClassMetadata(classPtr);
        if (metadataPtr == IntPtr.Zero)
        {
            throw new SwiftRuntimeException($"swift_getObjCClassMetadata returned null for class '{className}'.");
        }
        return TypeMetadata.FromHandle(metadataPtr);
    }
}
