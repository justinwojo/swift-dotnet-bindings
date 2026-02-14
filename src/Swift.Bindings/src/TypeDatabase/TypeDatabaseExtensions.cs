// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

// TODO: TypeDatabase should hold only nominal types (represented by NamedTypeSpec). Specifically tuples, closures etc. should not reside inside TypeDatabase.
// Functions taking TypeSpec should be moved into another class which will handle construction of complex types using nominal types.

public static class TypeDatabaseExtensions
{
    public readonly record struct AnyTypeFallbackInfo(string Reason, string SwiftType);
    private static readonly HashSet<string> BareGenericCSharpTypeNames = new(StringComparer.Ordinal)
    {
        "SwiftDictionary", "Swift.SwiftDictionary", "Swift.Runtime.SwiftDictionary",
        "SwiftArray", "Swift.SwiftArray", "Swift.Runtime.SwiftArray",
        "SwiftOptional", "Swift.SwiftOptional", "Swift.Runtime.SwiftOptional",
        "SwiftResult", "Swift.SwiftResult", "Swift.Runtime.SwiftResult",
        "SwiftSet", "Swift.SwiftSet", "Swift.Runtime.SwiftSet",
    };

    /// <summary>
    /// Determines whether the specified Swift type has been processed.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <returns>True if the type has been processed; otherwise, false.</returns>
    public static bool IsTypeProcessed(this ITypeDatabase typeDatabase, TypeSpec typeSpec)
    {
        return typeSpec switch
        {
            NamedTypeSpec namedTypeSpec => typeDatabase.IsTypeProcessed(namedTypeSpec),
            TupleTypeSpec { IsEmptyTuple: true } => true,
            ProtocolListTypeSpec => true, // Existential types are handled via ExistentialContainer
            _ => false
        };
    }

    /// <summary>
    /// Determines whether the specified Swift type has been processed.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <returns>True if the type has been processed; otherwise, false.</returns>
    public static bool IsTypeProcessed(this ITypeDatabase typeDatabase, NamedTypeSpec typeSpec)
    {
        // Generic type parameters are handled as AnyType (considered "processed")
        if (TypeSpecHelpers.IsGenericTypeParameter(typeSpec.Name))
        {
            return true;
        }

        // Existential types (any X) are handled separately, not processed as regular types
        if (IsExistentialTypeName(typeSpec))
        {
            return true;
        }

        // Pointer types are always mapped to IntPtr
        if (IsPointerType(typeSpec))
        {
            return true;
        }

        var typeName = SwiftTypeName.FromTypeSpec(typeSpec);
        if (typeDatabase.IsTypeProcessed(typeName))
            return true;

        // ObjC class types get synthetic ObjCBridged records (DB-first to allow explicit overrides)
        return IsObjCModuleType(typeSpec);
    }

    /// <summary>
    /// Gets the type record for the specified Swift type or the Any type if the type is not found.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <returns>The type record.</returns>
    public static TypeRecord GetTypeRecordOrAnyType(this ITypeDatabase typeDatabase, TypeSpec typeSpec)
    {
        return typeSpec switch
        {
            NamedTypeSpec namedTypeSpec => typeDatabase.GetTypeRecordOrAnyType(namedTypeSpec),
            TupleTypeSpec { IsEmptyTuple: true } => VoidType,
            ProtocolListTypeSpec protocolList => GetExistentialTypeRecord(protocolList),
            _ => AnyType
        };
    }

    /// <summary>
    /// Gets the type record for the specified Swift type or the Any type if the type is not found.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <returns>The type record.</returns>
    public static TypeRecord GetTypeRecordOrAnyType(this ITypeDatabase typeDatabase, NamedTypeSpec typeSpec)
    {
        // Generic type parameters (τ_0_0, T, Element, etc.) should return AnyType
        // since their concrete types aren't known at binding generation time
        if (TypeSpecHelpers.IsGenericTypeParameter(typeSpec.Name))
        {
            return AnyType;
        }

        // Existential types (any X) return AnyType
        if (IsExistentialTypeName(typeSpec))
        {
            return AnyType;
        }

        // Pointer types are always mapped to IntPtr
        if (IsPointerType(typeSpec))
        {
            return IntPtrType;
        }

        // Guard: known-generic types used without type arguments produce bare
        // C# types like "SwiftDictionary" (CS0305). Return AnyType to trigger skip.
        if (!typeSpec.ContainsGenericParameters && IsKnownGenericType(typeSpec.Name))
            return AnyType;

        // ObjC types are handled in the SwiftTypeName overload (DB-first, synthetic second)
        var typeName = SwiftTypeName.FromTypeSpec(typeSpec);
        return typeDatabase.GetTypeRecordOrAnyType(typeName);
    }

    /// <summary>
    /// Gets the type record for the specified Swift type or throws an exception if the type is not found.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <returns>The type record.</returns>
    public static TypeRecord GetTypeRecordOrThrow(this ITypeDatabase typeDatabase, TypeSpec typeSpec)
    {
        return typeSpec switch
        {
            NamedTypeSpec namedTypeSpec => typeDatabase.GetTypeRecordOrThrow(namedTypeSpec),
            TupleTypeSpec { IsEmptyTuple: true } => VoidType,
            _ => throw new ArgumentException($"Attempted to read TypeRecord of unsupported type spec: {typeSpec}")
        };
    }

    /// <summary>
    /// Tries to get the type record for the specified Swift type.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <param name="record">The type record.</param>
    /// <returns>True if the type record was found; otherwise, false.</returns>
    public static bool TryGetTypeRecord(this ITypeDatabase typeDatabase, TypeSpec typeSpec, [NotNullWhen(returnValue: true)] out TypeRecord? record)
    {
        record = null;
        return typeSpec switch
        {
            NamedTypeSpec namedTypeSpec => typeDatabase.TryGetTypeRecord(namedTypeSpec, out record),
            TupleTypeSpec { IsEmptyTuple: true } => false,
            _ => false
        };
    }

    /// <summary>
    /// Tries to get the type record for the specified Swift type.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <param name="record">The type record.</param>
    /// <returns>True if the type record was found; otherwise, false.</returns>
    public static bool TryGetTypeRecord(this ITypeDatabase typeDatabase, NamedTypeSpec typeSpec, [NotNullWhen(returnValue: true)] out TypeRecord? record)
    {
        // Generic type parameters return AnyType
        if (TypeSpecHelpers.IsGenericTypeParameter(typeSpec.Name))
        {
            record = AnyType;
            return true;
        }

        // Existential types (any X) return AnyType
        if (IsExistentialTypeName(typeSpec))
        {
            record = AnyType;
            return true;
        }

        // Pointer types are always mapped to IntPtr
        if (IsPointerType(typeSpec))
        {
            record = IntPtrType;
            return true;
        }

        var typeName = SwiftTypeName.FromTypeSpec(typeSpec);
        if (typeDatabase.TryGetTypeRecord(typeName, out record))
            return true;

        // ObjC class types get synthetic ObjCBridged records (DB-first to allow explicit overrides)
        if (IsObjCModuleType(typeSpec))
        {
            record = CreateObjCBridgedTypeRecord(typeName);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets the type record for the specified Swift type or throws an exception if the type is not found.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <returns>The type record.</returns>
    public static TypeRecord GetTypeRecordOrThrow(this ITypeDatabase typeDatabase, NamedTypeSpec typeSpec)
    {
        // Generic type parameters return AnyType (they can't be resolved to concrete types)
        if (TypeSpecHelpers.IsGenericTypeParameter(typeSpec.Name))
        {
            return AnyType;
        }

        // Existential types (any X) return AnyType
        if (IsExistentialTypeName(typeSpec))
        {
            return AnyType;
        }

        // Pointer types are always mapped to IntPtr
        if (IsPointerType(typeSpec))
        {
            return IntPtrType;
        }

        // ObjC types are handled in the SwiftTypeName overload (DB-first, synthetic second)
        var typeName = SwiftTypeName.FromTypeSpec(typeSpec);
        return typeDatabase.GetTypeRecordOrThrow(typeName);
    }

    /// <summary>
    /// Gets the type record for the specified Swift type or throws an exception if the type is not found.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="swiftTypeName">The Swift type name.</param>
    /// <returns>The type record.</returns>
    public static TypeRecord GetTypeRecordOrThrow(this ITypeDatabase typeDatabase, SwiftTypeName swiftTypeName)
    {
        if (typeDatabase.TryGetTypeRecord(swiftTypeName, out var record))
            return record;

        // ObjC class types not in the database get synthetic ObjCBridged records.
        // Covers ObjectiveC/Foundation root classes and Apple framework module types.
        if (IsObjCClassSwiftType(swiftTypeName))
            return CreateObjCBridgedTypeRecord(swiftTypeName);

        throw new Exception($"Type {swiftTypeName.ModuleQualifiedName} not found in database.");
    }

    /// <summary>
    /// Gets the type record for the specified Swift type or the Any type if the type is not found.
    /// </summary>
    /// <param name="typeDatabase">The type database.</param>
    /// <param name="swiftTypeName">The Swift type name.</param>
    /// <returns>The type record.</returns>
    public static TypeRecord GetTypeRecordOrAnyType(this ITypeDatabase typeDatabase, SwiftTypeName swiftTypeName)
    {
        if (typeDatabase.TryGetTypeRecord(swiftTypeName, out var record))
            return record;

        // ObjC class types not in the database get synthetic ObjCBridged records.
        // Covers ObjectiveC/Foundation root classes and Apple framework module types.
        if (IsObjCClassSwiftType(swiftTypeName))
            return CreateObjCBridgedTypeRecord(swiftTypeName);

        return AnyType;
    }

    /// <summary>
    /// Tries to describe why a type would degrade to AnyType when resolving type records.
    /// Generic type parameters are excluded because they are expected to resolve through generic constraints.
    /// </summary>
    public static bool TryGetAnyTypeFallbackInfo(this ITypeDatabase typeDatabase, TypeSpec typeSpec, [NotNullWhen(true)] out AnyTypeFallbackInfo? fallbackInfo)
    {
        switch (typeSpec)
        {
            case NamedTypeSpec namedTypeSpec:
                return typeDatabase.TryGetAnyTypeFallbackInfo(namedTypeSpec, out fallbackInfo);
            default:
                fallbackInfo = null;
                return false;
        }
    }

    /// <summary>
    /// Tries to describe why a named type would degrade to AnyType when resolving type records.
    /// </summary>
    public static bool TryGetAnyTypeFallbackInfo(this ITypeDatabase typeDatabase, NamedTypeSpec typeSpec, [NotNullWhen(true)] out AnyTypeFallbackInfo? fallbackInfo)
    {
        // Generic type parameters (T, τ_0_0, Element, etc.) are expected and should not be marked as unsupported.
        if (TypeSpecHelpers.IsGenericTypeParameter(typeSpec.Name))
        {
            fallbackInfo = null;
            return false;
        }

        if (IsExistentialTypeName(typeSpec))
        {
            fallbackInfo = new AnyTypeFallbackInfo(
                "Existential type fallback",
                typeSpec.ToString());
            return true;
        }

        // Pointer types are fully handled (mapped to IntPtr), not a fallback
        if (IsPointerType(typeSpec))
        {
            fallbackInfo = null;
            return false;
        }

        // ObjC framework types are handled via synthetic ObjCBridged records, not a fallback
        if (IsObjCModuleType(typeSpec))
        {
            fallbackInfo = null;
            return false;
        }

        var typeName = SwiftTypeName.FromTypeSpec(typeSpec);
        if (typeDatabase.TryGetTypeRecord(typeName, out _))
        {
            fallbackInfo = null;
            return false;
        }

        fallbackInfo = new AnyTypeFallbackInfo(
            "Type is missing from the type database",
            typeName.ModuleQualifiedName);
        return true;
    }

    /// <summary>
    /// Detects bare generic C# type names (no &lt;...&gt; arguments), including nullable reference suffixes.
    /// </summary>
    public static bool IsBareGenericTypeName(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return false;

        var normalized = typeName.Trim();
        if (normalized.EndsWith("?", StringComparison.Ordinal))
            normalized = normalized.Substring(0, normalized.Length - 1);

        return !normalized.Contains('<') && BareGenericCSharpTypeNames.Contains(normalized);
    }

    /// <summary>
    /// Gets the type record for Swift pointer types, mapped to System.IntPtr.
    /// Covers OpaquePointer, UnsafePointer, UnsafeMutablePointer, UnsafeRawPointer,
    /// UnsafeMutableRawPointer, and Builtin.RawPointer.
    /// </summary>
    public static TypeRecord IntPtrType { get; } = new TypeRecord
    {
        CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "IntPtr"),
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.OpaquePointer"),
        MetadataAccessor = string.Empty,
        Flags = TypeRecordFlags.Frozen,
        Kind = TypeRecordKind.Struct,
    };

    /// <summary>
    /// Gets the type record for the Any type.
    /// </summary>
    /// <returns>The type record for the Any type.</returns>
    public static TypeRecord AnyType { get; } = new TypeRecord
    {
        CSharpTypeName = CSharpTypeName.AnyType,
        SwiftTypeName = SwiftTypeName.AnyType,
        MetadataAccessor = string.Empty,
        Flags = TypeRecordFlags.None,
        Kind = TypeRecordKind.Protocol,
    };

    /// <summary>
    /// Gets the type record for the Void type.
    /// </summary>
    /// <returns>The type record for the Void type.</returns>
    public static TypeRecord VoidType { get; } = new TypeRecord
    {
        CSharpTypeName = CSharpTypeName.VoidType,
        SwiftTypeName = SwiftTypeName.VoidType,
        MetadataAccessor = string.Empty,
        Flags = TypeRecordFlags.Frozen,
        Kind = TypeRecordKind.Struct,
    };

    /// <summary>
    /// Gets the type record for an existential type (protocol or protocol composition).
    /// </summary>
    /// <param name="protocolList">The protocol list type specification.</param>
    /// <returns>The type record for the existential type.</returns>
    private static TypeRecord GetExistentialTypeRecord(ProtocolListTypeSpec protocolList)
    {
        var protocolCount = protocolList.Protocols.Count;
        var protocolNames = protocolList.Protocols.Count == 0
            ? "Any"
            : string.Join(" & ", protocolList.Protocols.Keys.Select(p => p.NameWithoutModule));

        return new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", $"ExistentialContainer{protocolCount}"),
            // Use AnyType for existential types since they don't have a standard module-qualified name
            SwiftTypeName = SwiftTypeName.AnyType,
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.Frozen, // Existential containers have fixed layout
            Kind = TypeRecordKind.Existential,
        };
    }

    /// <summary>
    /// The ObjectiveC module name in Swift ABI.
    /// TypeSpecParser.cs remaps "ObjectiveC.X" → "Foundation.X", so both must be checked.
    /// </summary>
    private const string ObjCModuleName = "ObjectiveC";

    /// <summary>
    /// Apple framework modules whose types are predominantly Objective-C classes.
    /// Types from these modules get synthetic ObjCBridged records when not explicitly
    /// registered in the type database XML, unless excluded by
    /// <see cref="AppleFrameworkValueTypes"/>. The C# namespace matches the Swift module
    /// name (e.g., UIKit.UIImage → UIKit.UIImage in C#).
    /// Modules with many value types (Metal, CoreMotion, CoreGraphics, GameKit, simd, etc.)
    /// are intentionally excluded — their types must be registered via XML.
    /// KNOWN GAP: If a newly encountered struct/enum from an included module is not in
    /// <see cref="AppleFrameworkValueTypes"/>, it will be misclassified as a class. This
    /// produces a compile-time error in generated code (not a silent runtime bug). The
    /// long-term fix is ABI-driven classification using the "usr" field from ABI JSON.
    /// </summary>
    private static readonly HashSet<string> AppleObjCFrameworkModules = new(StringComparer.Ordinal)
    {
        "UIKit", "AppKit", "CoreImage", "CoreData",
        "WebKit", "SceneKit", "SpriteKit", "ARKit", "RealityKit",
        "AVFoundation", "Photos", "PhotosUI", "Contacts", "ContactsUI",
        "EventKit", "EventKitUI", "HealthKit", "HomeKit", "CloudKit",
        "StoreKit", "PDFKit", "SafariServices",
        "AuthenticationServices", "CoreBluetooth", "CoreSpotlight",
        "CoreML", "Vision", "NaturalLanguage", "SoundAnalysis", "Speech",
        "MultipeerConnectivity", "UserNotifications", "NetworkExtension",
        "Intents", "IntentsUI",
    };

    /// <summary>
    /// Known value types (structs/enums) from Apple ObjC framework modules.
    /// These must NOT be auto-bridged as ObjC classes — they are value types
    /// that need different marshalling (direct copy, not pointer + ARC).
    /// Key is "Module.TypeName" (fully qualified).
    /// </summary>
    private static readonly HashSet<string> AppleFrameworkValueTypes = new(StringComparer.Ordinal)
    {
        // UIKit structs
        "UIKit.UIEdgeInsets", "UIKit.UIOffset", "UIKit.UIFloatRange",
        "UIKit.NSDirectionalEdgeInsets",
        // UIKit nested enums/structs (flattened in .NET: UIView.ContentMode → UIViewContentMode)
        "UIKit.UIView.ContentMode", "UIKit.UIControl.State", "UIKit.UIControl.Event",
        "UIKit.UIAccessibilityTraits",
        // UIKit enums (ObjC enums bridged to Swift — value types, not NSObject subclasses)
        // Names use the Swift nested form from ABI JSON printedName (e.g., UIActivityIndicatorView.Style)
        "UIKit.UIBarStyle", "UIKit.UIKeyboardAppearance", "UIKit.UITextField.ViewMode",
        "UIKit.UIControl.ContentVerticalAlignment", "UIKit.UIActivityIndicatorView.Style",
        "UIKit.UIBlurEffect.Style", "UIKit.UILayoutPriority",
        "UIKit.NSTextAlignment",
        // AVFoundation structs
        "AVFoundation.AVAudioFramePosition", "AVFoundation.AVAudioFrameCount",
        "AVFoundation.AVAudioPacketCount", "AVFoundation.AVAudioChannelCount",
        "AVFoundation.AVCaptureVideoOrientation",
        // CoreData structs
        "CoreData.NSFetchRequestResultType",
        // SceneKit structs
        "SceneKit.SCNVector3", "SceneKit.SCNVector4", "SceneKit.SCNMatrix4",
        // MapKit structs (module removed from auto-bridge, but kept here for safety)
        "MapKit.MKCoordinateRegion", "MapKit.MKCoordinateSpan",
        "MapKit.MKMapRect", "MapKit.MKMapPoint", "MapKit.MKMapSize",
        // ARKit structs
        "ARKit.ARRaycastQuery",
    };

    /// <summary>
    /// Creates a synthetic ObjCBridged TypeRecord for an ObjC class type.
    /// The resulting record triggers the existing ObjCBridged marshalling pipeline
    /// (IntPtr in P/Invoke, Handle extraction in wrappers).
    /// For ObjectiveC/Foundation root classes, the C# namespace is "Foundation".
    /// For Apple framework types (UIKit, AppKit, etc.), the C# namespace matches the Swift module.
    /// </summary>
    private static TypeRecord CreateObjCBridgedTypeRecord(SwiftTypeName swiftTypeName)
    {
        // ObjectiveC module types (NSObject, NSProxy) map to Foundation.* in C#
        // Apple framework types (UIKit.UIImage, AppKit.NSImage) keep their module as namespace
        var csharpNamespace = (swiftTypeName.Module == ObjCModuleName || swiftTypeName.Module == "Foundation")
            ? "Foundation"
            : swiftTypeName.Module;

        // For nested ObjC types (e.g., UIKit.UIView.ContentMode), .NET iOS bindings flatten
        // the parent type into the name: UIView + ContentMode = UIViewContentMode.
        // SwiftTypeName.Name only has the leaf ("ContentMode"), so extract parent components
        // from the ModuleQualifiedName.
        var csharpName = swiftTypeName.Name;
        var parts = swiftTypeName.ModuleQualifiedName.Split('.');
        if (parts.Length > 2)
        {
            // parts[0] = module, parts[1..n-1] = parent types, parts[n] = leaf
            csharpName = string.Concat(parts.Skip(1));
        }

        return new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName(csharpNamespace, csharpName),
            SwiftTypeName = swiftTypeName,
            MetadataAccessor = string.Empty,
            Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Class,
        };
    }

    /// <summary>
    /// Determines whether the specified NamedTypeSpec represents an ObjC class type
    /// that should get a synthetic ObjCBridged record.
    /// Covers two categories:
    /// 1. ObjectiveC/Foundation root classes (NSObject, NSProxy) — known safe subset
    /// 2. Apple framework module types (UIKit.UIImage, AppKit.NSImage) — assumed to be classes
    ///    unless listed in <see cref="AppleFrameworkValueTypes"/>
    /// TypeSpecParser.cs remaps "ObjectiveC.X" → "Foundation.X", so we check both modules.
    /// </summary>
    internal static bool IsObjCModuleType(NamedTypeSpec typeSpec)
    {
        if (!typeSpec.HasModule())
            return false;

        // ObjectiveC/Foundation root classes (conservative: only NSObject, NSProxy)
        if ((typeSpec.Module == ObjCModuleName || typeSpec.Module == "Foundation")
            && IsKnownObjCRootClass(typeSpec.NameWithoutModule))
            return true;

        // Apple framework module types (UIKit, AppKit, etc.) are ObjC classes by default,
        // but exclude known value types (structs/enums) from those modules
        return AppleObjCFrameworkModules.Contains(typeSpec.Module)
            && !AppleFrameworkValueTypes.Contains(typeSpec.Name);
    }

    /// <summary>
    /// Determines whether the specified SwiftTypeName represents an ObjC class type.
    /// Mirrors <see cref="IsObjCModuleType"/> but for the SwiftTypeName path.
    /// </summary>
    private static bool IsObjCClassSwiftType(SwiftTypeName swiftTypeName)
    {
        // ObjectiveC/Foundation root classes
        if ((swiftTypeName.Module == ObjCModuleName || swiftTypeName.Module == "Foundation")
            && IsKnownObjCRootClass(swiftTypeName.Name))
            return true;

        // Apple framework module types, excluding known value types
        return AppleObjCFrameworkModules.Contains(swiftTypeName.Module)
            && !AppleFrameworkValueTypes.Contains(swiftTypeName.ModuleQualifiedName);
    }

    /// <summary>
    /// Returns true if the given unqualified type name is a known Objective-C root class.
    /// The ObjectiveC Swift module only defines NSObject and NSProxy as root classes;
    /// these get remapped to Foundation.NSObject and Foundation.NSProxy by TypeSpecParser.
    /// Other ObjectiveC module types (Selector, ObjCBool, NSZone) are value types.
    /// </summary>
    private static bool IsKnownObjCRootClass(string name)
    {
        return name is "NSObject" or "NSProxy";
    }

    /// <summary>
    /// Determines whether the specified NamedTypeSpec represents a Swift pointer type
    /// that should be mapped to System.IntPtr.
    /// </summary>
    private static readonly HashSet<string> KnownGenericTypes = new(StringComparer.Ordinal)
    {
        "Dictionary", "Array", "Set", "Optional", "Result",
        "Swift.Dictionary", "Swift.Array", "Swift.Set", "Swift.Optional", "Swift.Result"
    };

    private static bool IsKnownGenericType(string name) => KnownGenericTypes.Contains(name);

    private static bool IsPointerType(NamedTypeSpec typeSpec)
    {
        return typeSpec.Name is "Swift.OpaquePointer" or "Swift.UnsafePointer"
            or "Swift.UnsafeMutablePointer" or "Swift.UnsafeRawPointer"
            or "Swift.UnsafeMutableRawPointer" or "Builtin.RawPointer";
    }

    /// <summary>
    /// Determines whether the specified NamedTypeSpec represents an existential type.
    /// Existential types come through as NamedTypeSpec with names like "any" or "any SomeProtocol"
    /// when parsing tuple elements or enum associated values containing existential types.
    /// </summary>
    /// <param name="typeSpec">The type specification.</param>
    /// <returns><c>true</c> if this is an existential type name; otherwise, <c>false</c>.</returns>
    private static bool IsExistentialTypeName(NamedTypeSpec typeSpec)
    {
        // Check if the TypeSpec has the IsAny flag set (set by TypeSpecParser when "any" prefix is parsed)
        // This is the primary way existential types are detected (e.g., "any Swift.Encoder" -> IsAny=true, Name="Swift.Encoder")
        if (typeSpec.IsAny)
        {
            return true;
        }

        // Check for existential type patterns:
        // - "any" alone
        // - "any SomeProtocol" or "any Module.Protocol"
        if (typeSpec.Name == "any" || typeSpec.Name.StartsWith("any "))
        {
            return true;
        }

        // Don't classify generic type parameters as existential types.
        // Generic parameters (τ_0_0, T, Element, etc.) are unbound type parameters
        // that should be handled by the generic type system, not as existentials.
        if (TypeSpecHelpers.IsGenericTypeParameter(typeSpec.Name))
        {
            return false;
        }

        // Check if this is a type name without a module qualifier (no dot)
        // These are typically special types or parsing artifacts that should be treated as existential
        // Exclude known single-word types that are valid (Swift.Any, Swift.AnyObject are already prefixed)
        if (!typeSpec.HasModule() && typeSpec.Name != "Swift.Any" && typeSpec.Name != "Swift.AnyObject")
        {
            return true;
        }

        return false;
    }
}
