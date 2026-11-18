// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BindingsGeneration;

/// <summary>
/// The C# surface an Objective-C-rooted binding inherits from <c>Foundation.NSObject</c>, and the
/// one decision that depends on it: whether an emitted member of such a class overrides, hides, or
/// leaves alone an inherited NSObject member of the same C# name.
///
/// <para>A Swift class rooted in <c>NSObject</c> projects to a C# class deriving from the platform
/// assembly's <c>Foundation.NSObject</c>. A Swift <c>override var description</c> projects to a
/// <c>Description</c> property, and NSObject already declares a virtual <c>Description</c>; emitted
/// as a fresh <c>virtual</c>, it hides the inherited one (CS0114, an error under
/// TreatWarningsAsErrors). Hiding is also wrong at run time: NSObject's bound members on a type
/// outside the platform assembly reach Objective-C through <c>objc_msgSendSuper</c> starting at
/// the platform class, so <c>((NSObject)x).Description</c> would return NSObject's own description
/// and never reach the Swift override. When the Swift member overrides the Objective-C member and
/// the projected C# shape is exactly the inherited one, the binding emits <c>override</c>, so every
/// C# path reaches the Swift implementation.</para>
///
/// <para>The tables hold only members present with the same shape on every Apple platform's
/// NSObject (iOS, tvOS, Mac Catalyst and macOS ref assemblies), since one decision feeds every
/// platform's compile, and only members every supported workload declares (methods that some
/// workload versions add are listed in <see cref="WorkloadSpecificMethods"/>). Names that exist
/// on some platforms only are listed in
/// <see cref="PlatformSpecificMemberNames"/>; a member projecting onto one of them can neither
/// override nor say <c>new</c> portably, so its declaration is emitted with the hiding warnings
/// suppressed instead. The reflection drift test (<c>ObjCInheritedMemberSurfaceDriftTests</c>)
/// rebuilds both sets from the installed ref assemblies, so the tables cannot go stale silently
/// and generation never depends on an installed workload.</para>
/// </summary>
public static class ObjCInheritedMemberSurface
{
    /// <summary>An inherited NSObject property: C# name, C# type, settable, virtual.</summary>
    public sealed record PropertyEntry(string Name, string Type, bool HasSetter, bool IsVirtual);

    /// <summary>An inherited NSObject method: C# name, parameter types, return type, virtual.</summary>
    public sealed record MethodEntry(string Name, string[] ParameterTypes, string ReturnType, bool IsVirtual);

    /// <summary>What an emitted member does to the inherited NSObject surface.</summary>
    public enum Disposition
    {
        /// <summary>No inherited member of this C# name/signature.</summary>
        None,
        /// <summary>The member overrides an inherited virtual member of exactly this shape.</summary>
        Override,
        /// <summary>The member hides an inherited member (different shape, non-virtual, or not a Swift override).</summary>
        Hide,
    }

    /// <summary>NSObject instance properties common to every Apple platform (public or protected).</summary>
    public static IReadOnlyList<PropertyEntry> Properties { get; } = new PropertyEntry[]
    {
        new("Class", "ObjCRuntime.Class", HasSetter: false, IsVirtual: true),
        new("ClassHandle", "ObjCRuntime.NativeHandle", HasSetter: false, IsVirtual: true),
        new("DebugDescription", "string", HasSetter: false, IsVirtual: true),
        new("Description", "string", HasSetter: false, IsVirtual: true),
        new("Handle", "ObjCRuntime.NativeHandle", HasSetter: true, IsVirtual: false),
        new("IsDirectBinding", "bool", HasSetter: true, IsVirtual: false),
        new("IsProxy", "bool", HasSetter: false, IsVirtual: true),
        new("RetainCount", "nuint", HasSetter: false, IsVirtual: true),
        new("Self", "Foundation.NSObject", HasSetter: false, IsVirtual: true),
        new("Superclass", "ObjCRuntime.Class", HasSetter: false, IsVirtual: true),
        new("SuperHandle", "ObjCRuntime.NativeHandle", HasSetter: false, IsVirtual: false),
        new("Zone", "Foundation.NSZone", HasSetter: false, IsVirtual: true),
    };

    /// <summary>NSObject instance methods common to every Apple platform (public or protected).</summary>
    public static IReadOnlyList<MethodEntry> Methods { get; } = new MethodEntry[]
    {
        new("AddObserver", new[] { "Foundation.NSObject", "Foundation.NSString", "Foundation.NSKeyValueObservingOptions", "nint" }, "void", IsVirtual: true),
        new("AddObserver", new[] { "Foundation.NSObject", "string", "Foundation.NSKeyValueObservingOptions", "nint" }, "void", IsVirtual: false),
        new("AddObserver", new[] { "Foundation.NSString", "Foundation.NSKeyValueObservingOptions", "System.Action<Foundation.NSObservedChange>" }, "System.IDisposable", IsVirtual: false),
        new("AddObserver", new[] { "string", "Foundation.NSKeyValueObservingOptions", "System.Action<Foundation.NSObservedChange>" }, "System.IDisposable", IsVirtual: false),
        new("AwakeFromNib", Array.Empty<string>(), "void", IsVirtual: true),
        new("BeginInvokeOnMainThread", new[] { "ObjCRuntime.Selector", "Foundation.NSObject" }, "void", IsVirtual: false),
        new("BeginInvokeOnMainThread", new[] { "System.Action" }, "void", IsVirtual: false),
        new("ConformsToProtocol", new[] { "ObjCRuntime.NativeHandle" }, "bool", IsVirtual: true),
        new("Copy", Array.Empty<string>(), "Foundation.NSObject", IsVirtual: true),
        new("DangerousAutorelease", Array.Empty<string>(), "Foundation.NSObject", IsVirtual: false),
        new("DangerousRelease", Array.Empty<string>(), "void", IsVirtual: false),
        new("DangerousRetain", Array.Empty<string>(), "Foundation.NSObject", IsVirtual: false),
        new("DidChange", new[] { "Foundation.NSKeyValueChange", "Foundation.NSIndexSet", "Foundation.NSString" }, "void", IsVirtual: true),
        new("DidChange", new[] { "Foundation.NSString", "Foundation.NSKeyValueSetMutationKind", "Foundation.NSSet" }, "void", IsVirtual: true),
        new("DidChangeValue", new[] { "string" }, "void", IsVirtual: true),
        new("Dispose", Array.Empty<string>(), "void", IsVirtual: false),
        new("Dispose", new[] { "bool" }, "void", IsVirtual: true),
        new("DoesNotRecognizeSelector", new[] { "ObjCRuntime.Selector" }, "void", IsVirtual: true),
        new("Equals", new[] { "Foundation.NSObject" }, "bool", IsVirtual: false),
        new("Equals", new[] { "object" }, "bool", IsVirtual: true),
        new("Finalize", Array.Empty<string>(), "void", IsVirtual: true),
        new("GetDictionaryOfValuesFromKeys", new[] { "Foundation.NSString[]" }, "Foundation.NSDictionary", IsVirtual: true),
        new("GetHashCode", Array.Empty<string>(), "int", IsVirtual: true),
        new("GetMethodForSelector", new[] { "ObjCRuntime.Selector" }, "nint", IsVirtual: true),
        new("GetNativeHash", Array.Empty<string>(), "nuint", IsVirtual: true),
        new("Init", Array.Empty<string>(), "void", IsVirtual: false),
        new("InitializeHandle", new[] { "ObjCRuntime.NativeHandle" }, "void", IsVirtual: false),
        new("InitializeHandle", new[] { "ObjCRuntime.NativeHandle", "string" }, "void", IsVirtual: false),
        new("Invoke", new[] { "System.Action", "double" }, "void", IsVirtual: true),
        new("Invoke", new[] { "System.Action", "System.TimeSpan" }, "void", IsVirtual: true),
        new("InvokeOnMainThread", new[] { "ObjCRuntime.Selector", "Foundation.NSObject" }, "void", IsVirtual: false),
        new("InvokeOnMainThread", new[] { "System.Action" }, "void", IsVirtual: false),
        new("IsEqual", new[] { "Foundation.NSObject" }, "bool", IsVirtual: true),
        new("IsKindOfClass", new[] { "ObjCRuntime.Class" }, "bool", IsVirtual: true),
        new("IsMemberOfClass", new[] { "ObjCRuntime.Class" }, "bool", IsVirtual: true),
        new("MarkDirty", Array.Empty<string>(), "void", IsVirtual: false),
        new("MutableCopy", Array.Empty<string>(), "Foundation.NSObject", IsVirtual: true),
        new("ObserveValue", new[] { "Foundation.NSString", "Foundation.NSObject", "Foundation.NSDictionary", "nint" }, "void", IsVirtual: true),
        new("PerformSelector", new[] { "ObjCRuntime.Selector" }, "Foundation.NSObject", IsVirtual: true),
        new("PerformSelector", new[] { "ObjCRuntime.Selector", "Foundation.NSObject" }, "Foundation.NSObject", IsVirtual: true),
        new("PerformSelector", new[] { "ObjCRuntime.Selector", "Foundation.NSObject", "double" }, "void", IsVirtual: true),
        new("PerformSelector", new[] { "ObjCRuntime.Selector", "Foundation.NSObject", "double", "Foundation.NSString[]" }, "void", IsVirtual: true),
        new("PerformSelector", new[] { "ObjCRuntime.Selector", "Foundation.NSObject", "Foundation.NSObject" }, "Foundation.NSObject", IsVirtual: true),
        new("PerformSelector", new[] { "ObjCRuntime.Selector", "Foundation.NSThread", "Foundation.NSObject", "bool" }, "void", IsVirtual: true),
        new("PerformSelector", new[] { "ObjCRuntime.Selector", "Foundation.NSThread", "Foundation.NSObject", "bool", "Foundation.NSString[]" }, "void", IsVirtual: true),
        new("PrepareForInterfaceBuilder", Array.Empty<string>(), "void", IsVirtual: true),
        new("RemoveObserver", new[] { "Foundation.NSObject", "Foundation.NSString" }, "void", IsVirtual: true),
        new("RemoveObserver", new[] { "Foundation.NSObject", "Foundation.NSString", "nint" }, "void", IsVirtual: true),
        new("RemoveObserver", new[] { "Foundation.NSObject", "string" }, "void", IsVirtual: false),
        new("RemoveObserver", new[] { "Foundation.NSObject", "string", "nint" }, "void", IsVirtual: false),
        new("RespondsToSelector", new[] { "ObjCRuntime.Selector" }, "bool", IsVirtual: true),
        new("SetNilValueForKey", new[] { "Foundation.NSString" }, "void", IsVirtual: true),
        new("SetValueForKey", new[] { "Foundation.NSObject", "Foundation.NSString" }, "void", IsVirtual: true),
        new("SetValueForKeyPath", new[] { "Foundation.NSObject", "Foundation.NSString" }, "void", IsVirtual: true),
        new("SetValueForKeyPath", new[] { "ObjCRuntime.NativeHandle", "Foundation.NSString" }, "void", IsVirtual: false),
        new("SetValueForUndefinedKey", new[] { "Foundation.NSObject", "Foundation.NSString" }, "void", IsVirtual: true),
        new("SetValuesForKeysWithDictionary", new[] { "Foundation.NSDictionary" }, "void", IsVirtual: true),
        new("ToString", Array.Empty<string>(), "string", IsVirtual: true),
        new("ValueForKey", new[] { "Foundation.NSString" }, "Foundation.NSObject", IsVirtual: true),
        new("ValueForKeyPath", new[] { "Foundation.NSString" }, "Foundation.NSObject", IsVirtual: true),
        new("ValueForUndefinedKey", new[] { "Foundation.NSString" }, "Foundation.NSObject", IsVirtual: true),
        new("WillChange", new[] { "Foundation.NSKeyValueChange", "Foundation.NSIndexSet", "Foundation.NSString" }, "void", IsVirtual: true),
        new("WillChange", new[] { "Foundation.NSString", "Foundation.NSKeyValueSetMutationKind", "Foundation.NSSet" }, "void", IsVirtual: true),
        new("WillChangeValue", new[] { "string" }, "void", IsVirtual: true),
    };

    /// <summary>
    /// An inherited NSObject method that only some supported platform workloads declare: present from
    /// the workload whose major version is <paramref name="FirstWorkloadMajor"/> on (on every Apple
    /// platform of that workload), absent before it.
    /// </summary>
    public sealed record WorkloadSpecificMethodEntry(MethodEntry Method, int FirstWorkloadMajor);

    /// <summary>
    /// NSObject methods whose presence depends on the platform workload the binding is compiled
    /// against. Generated code compiles against whichever supported workload the consumer has (the
    /// Xcode 26 and Xcode 27 workloads are both supported), so a member projecting onto one of
    /// these can neither override nor say <c>new</c> portably: it hides an inherited member under
    /// one workload and nothing under the other. It is classified <see cref="Disposition.Hide"/> and
    /// emitted with the hiding warnings suppressed, the one form that compiles under both.
    /// </summary>
    public static IReadOnlyList<WorkloadSpecificMethodEntry> WorkloadSpecificMethods { get; } = new WorkloadSpecificMethodEntry[]
    {
        // Protected, non-virtual; added by the Xcode 27 workloads (27.x ref packs, .NET 10 and 11).
        new(new("InitializeHandle", new[] { "ObjCRuntime.NativeHandle", "string", "bool" }, "void", IsVirtual: false), FirstWorkloadMajor: 27),
    };

    /// <summary>
    /// NSObject member names declared on some Apple platforms only (the iOS-family accessibility
    /// properties; macOS's Cocoa-bindings and editing members). Kept apart from the common tables
    /// because no single modifier compiles on every platform for them.
    /// </summary>
    public static IReadOnlyCollection<string> PlatformSpecificMemberNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        // iOS, tvOS, Mac Catalyst
        "AccessibilityAttributedUserInputLabels",
        "AccessibilityRespondsToUserInteraction",
        "AccessibilityTextualContext",
        "AccessibilityUserInputLabels",
        // macOS
        "Bind",
        "CommitEditing",
        "ExposedBindings",
        "GetBindingInfo",
        "GetBindingOptionDescriptions",
        "GetBindingValueClass",
        "ObjectDidEndEditing",
        "Unbind",
    };

    /// <summary>
    /// Classifies an instance property of an Objective-C-rooted class against the inherited
    /// NSObject properties. <see cref="Disposition.Override"/> needs a Swift override of a virtual
    /// member with the identical C# type, and no setter the inherited member lacks (CS0546).
    /// </summary>
    public static Disposition ClassifyProperty(string csharpName, string csharpType, bool hasSetter, bool isSwiftOverride)
    {
        var entry = Properties.FirstOrDefault(p => p.Name == csharpName);
        if (entry is null)
            return PlatformSpecificMemberNames.Contains(csharpName) ? Disposition.Hide : Disposition.None;
        if (isSwiftOverride && entry.IsVirtual && TypesMatch(entry.Type, csharpType) && (!hasSetter || entry.HasSetter))
            return Disposition.Override;
        return Disposition.Hide;
    }

    /// <summary>
    /// Classifies an instance method of an Objective-C-rooted class against the inherited NSObject
    /// methods. C# hides a method by name and parameter types; overriding further needs a Swift
    /// override of a virtual member returning the identical type.
    /// </summary>
    public static Disposition ClassifyMethod(string csharpName, IReadOnlyList<string> parameterTypes, string returnType, bool isSwiftOverride)
    {
        var entry = Methods.FirstOrDefault(m => SameSignature(m, csharpName, parameterTypes));
        if (entry is null)
        {
            // A platform-only name, or a method only some supported workloads declare, may or may
            // not be inherited where this compiles; the caller suppresses the hiding warnings rather
            // than guess a modifier.
            if (WorkloadSpecificMethods.Any(w => SameSignature(w.Method, csharpName, parameterTypes)))
                return Disposition.Hide;
            return PlatformSpecificMemberNames.Contains(csharpName) ? Disposition.Hide : Disposition.None;
        }
        if (isSwiftOverride && entry.IsVirtual && TypesMatch(entry.ReturnType, returnType))
            return Disposition.Override;
        return Disposition.Hide;
    }

    /// <summary>
    /// True when an Objective-C-rooted class inherits from an Objective-C class other than NSObject
    /// itself (a UIKit or AppKit class, say). Its C# base then carries members this table does not
    /// know, so the binding cannot tell whether an emitted member hides one.
    /// </summary>
    public static bool InheritsUnknownObjCSurface(ClassDecl classDecl)
    {
        if (!classDecl.IsObjCRooted)
            return false;
        var root = classDecl;
        while (root.ResolvedSuperclass != null)
            root = root.ResolvedSuperclass;
        if (!root.HasObjCSuperclass)
            return true; // rooted through a cross-module parent: its surface is not known here
        return root.SuperclassUsr != "c:objc(cs)NSObject";
    }

    /// <summary>
    /// The preprocessor line that suppresses the hiding warnings (CS0108 for a non-virtual or
    /// static base member, CS0114 for a virtual one) on a declaration whose inherited surface the
    /// generator cannot know for every platform. A member that hides nothing draws no warning, so
    /// this is the one form that compiles either way; the matching restore follows the declaration.
    /// </summary>
    public const string SuppressHidingWarnings = "#pragma warning disable CS0108, CS0114 // may hide an inherited Objective-C member";

    /// <summary>The restore line paired with <see cref="SuppressHidingWarnings"/>.</summary>
    public const string RestoreHidingWarnings = "#pragma warning restore CS0108, CS0114";

    /// <summary>
    /// Normalizes a C# type as the emitter writes it (<c>global::</c>-qualified, keyword or
    /// <c>System.*</c> spelling, nullable-annotated reference) to the table's spelling.
    /// </summary>
    internal static string NormalizeType(string csharpType)
    {
        var t = csharpType.Replace("global::", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        bool nullable = t.EndsWith('?');
        if (nullable)
            t = t[..^1];
        t = t switch
        {
            "System.String" => "string",
            "System.Boolean" => "bool",
            "System.Void" => "void",
            "System.IntPtr" => "nint",
            "System.UIntPtr" => "nuint",
            "System.Double" => "double",
            "System.Int32" => "int",
            "System.Object" => "object",
            _ => t,
        };
        // A nullable annotation on a reference type is the same runtime type; on a value type it is
        // Nullable<T>, a different one.
        if (nullable && !IsReferenceTypeSpelling(t))
            return t + "?";
        return t;
    }

    private static bool IsReferenceTypeSpelling(string t)
        => t is "string" or "object"
           || t.EndsWith("[]", StringComparison.Ordinal)
           || t.StartsWith("Foundation.NS", StringComparison.Ordinal)
           || t == "ObjCRuntime.Class"
           || t.StartsWith("System.Action", StringComparison.Ordinal)
           || t == "System.IDisposable";

    private static bool SameSignature(MethodEntry entry, string csharpName, IReadOnlyList<string> parameterTypes)
        => entry.Name == csharpName
           && entry.ParameterTypes.Length == parameterTypes.Count
           && entry.ParameterTypes.Zip(parameterTypes).All(t => TypesMatch(t.First, t.Second));

    private static bool TypesMatch(string tableType, string emittedType)
        => string.Equals(NormalizeType(tableType), NormalizeType(emittedType), StringComparison.Ordinal);
}
