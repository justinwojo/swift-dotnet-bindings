// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits @_silgen_name Swift wrapper functions for property accessors that override
/// ObjC-inherited properties. These overrides don't export Tj dispatch thunks in the
/// framework dylib, so direct P/Invoke to the thunk fails at runtime.
///
/// The wrapper is a free function in the wrapper xcframework that calls the property
/// directly, leveraging Swift's normal ObjC dispatch. Uses @_silgen_name (Swift ABI)
/// so the C# P/Invoke keeps CallConvSwift — self is passed as explicit IntPtr via
/// <see cref="MethodDecl.UsesFreeFunctionWrapper"/>.
///
/// Detection: property is override + class is ObjC-rooted + property not in any
/// resolved in-module ancestor (meaning it originates from an external ObjC class).
/// </summary>
public static class ObjCOverridePropertyWrapperEmitter
{
    /// <summary>
    /// Determines whether a property's accessor methods need @_silgen_name wrappers
    /// because the property overrides an ObjC-inherited property whose Tj dispatch
    /// thunk is unavailable in the wrapper xcframework.
    /// </summary>
    public static bool ShouldEmitWrapper(PropertyDecl propertyDecl, MethodEnvironment accessorEnv)
    {
        // Must be in xcframework mode (wrapper library available)
        if (string.IsNullOrEmpty(accessorEnv.TypeDatabase.AsyncLibraryName))
            return false;

        // Must be a non-static instance property on a class
        if (propertyDecl.ParentDecl is not ClassDecl classParent || propertyDecl.IsStatic)
            return false;

        // Must be an override
        if (!propertyDecl.IsOverride)
            return false;

        // Must be ObjC-rooted (otherwise Tj thunks exist in the Swift framework)
        if (!classParent.IsObjCRooted)
            return false;

        // If the property IS found in a resolved in-module ancestor, it was defined
        // (or overridden) in a Swift class we can see — the Tj thunk exists.
        // Only emit wrapper when the property comes from an external ObjC ancestor.
        if (WrapperEmitter.HasPropertyInResolvedAncestors(classParent, propertyDecl.Name))
            return false;

        // Skip generic parent types — @_silgen_name free functions can't express
        // type parameters from the enclosing type
        if (classParent.IsGeneric)
            return false;

        // Reject metatype-shaped property types (bare or Optional<Metatype>) — the
        // accessor wrapper would render the property type via ExistentialBypassEmitter
        // and emit a bare "Type" token; ObjC override wrappers run independently of
        // PropertyWrapperEmitter.ShouldEmitWrapper so this gate has to be here too.
        if (WrapperValidation.IsMetatypeTypeIncludingOptional(propertyDecl.SwiftTypeSpec))
            return false;

        return true;
    }

    /// <summary>
    /// Gets the @_silgen_name symbol for a property accessor wrapper.
    /// </summary>
    /// <param name="moduleName">The Swift module name (e.g., "Lottie").</param>
    /// <param name="typeName">The Swift type name (e.g., "AnimationViewBase").</param>
    /// <param name="propertyName">The Swift property name (e.g., "contentMode").</param>
    /// <param name="isGetter">True for getter, false for setter.</param>
    public static string GetAccessorSymbolName(string moduleName, string typeName, string propertyName, bool isGetter)
    {
        var safeTypeName = typeName.Replace(".", "_");
        var prefix = isGetter ? "Get" : "Set";
        // SBSW_ prefix: @_silgen_name wrapper preserves Swift CC because the property
        // type (and the class self) may not be C-representable. PInvokeEmitHelper
        // pairs the SBSW_ prefix with CallConvSwift on the C# P/Invoke side.
        return $"SBSW_{prefix}_{moduleName}_{safeTypeName}_{propertyName}";
    }

    /// <summary>
    /// Emits a @_silgen_name Swift wrapper for a property getter that overrides an ObjC-inherited property.
    /// The wrapper is a free function that takes self as an explicit parameter and calls the property.
    /// </summary>
    public static void EmitSwiftGetterWrapper(
        SwiftWriter swiftWriter,
        PropertyDecl propertyDecl,
        string symbolName,
        ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;

        if (!ctx.TryAddObjCPropertyWrapperSymbol(symbolName))
            return; // Already emitted

        var parentTypeDecl = propertyDecl.ParentDecl as TypeDecl;
        if (parentTypeDecl?.SwiftTypeName == null) return;
        var moduleQualifiedName = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;
        var propertySwiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(propertyDecl.SwiftTypeSpec);

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // ObjC override property getter wrapper for {{moduleQualifiedName}}.{{propertyDecl.Name}}.
            // Routes through wrapper lib because Tj dispatch thunk is unavailable for ObjC-inherited overrides.
            """);

        // @_silgen_name wrappers are top-level Swift functions and do NOT inherit the
        // parent type's @available; re-apply it (merged with member-level annotations and
        // any ancestor floors) so the wrapper compiles only where the wrapped API is reachable.
        var availability = WrapperEmitterHelpers.MergeAvailability(propertyDecl.AvailabilityAnnotations, parentTypeDecl);
        WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);

        if (parentTypeDecl.IsMainActorIsolated)
        {
            swiftWriter.WriteLine("@MainActor");
        }

        swiftWriter.WriteLines($$"""
            @_silgen_name("{{symbolName}}")
            public func _sbw_get_{{propertyDecl.Name}}_{{EmitterUtility.DeterministicHash8(symbolName)}}(_ self_: {{moduleQualifiedName}}) -> {{propertySwiftType}} {
                return self_.{{propertyDecl.Name}}
            }
            """);
    }

    /// <summary>
    /// Emits a @_silgen_name Swift wrapper for a property setter that overrides an ObjC-inherited property.
    /// Parameter order matches C# P/Invoke: newValue first, self last (via UsesFreeFunctionWrapper).
    /// </summary>
    public static void EmitSwiftSetterWrapper(
        SwiftWriter swiftWriter,
        PropertyDecl propertyDecl,
        string symbolName,
        ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.Default;

        if (!ctx.TryAddObjCPropertyWrapperSymbol(symbolName))
            return; // Already emitted

        var parentTypeDecl = propertyDecl.ParentDecl as TypeDecl;
        if (parentTypeDecl?.SwiftTypeName == null) return;
        var moduleQualifiedName = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;
        var propertySwiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(propertyDecl.SwiftTypeSpec);

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // ObjC override property setter wrapper for {{moduleQualifiedName}}.{{propertyDecl.Name}}.
            // Routes through wrapper lib because Tj dispatch thunk is unavailable for ObjC-inherited overrides.
            """);

        // Setter availability prefers the narrower SetterAvailabilityAnnotations when present
        // (a get-only-on-iOS-16 / set-on-iOS-18 split shape), otherwise falls back to the
        // property's getter-side annotations so both wrappers carry consistent floors.
        var setterMember = propertyDecl.SetterAvailabilityAnnotations is { Count: > 0 }
            ? propertyDecl.SetterAvailabilityAnnotations
            : propertyDecl.AvailabilityAnnotations;
        var availability = WrapperEmitterHelpers.MergeAvailability(setterMember, parentTypeDecl);
        WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);

        if (parentTypeDecl.IsMainActorIsolated)
        {
            swiftWriter.WriteLine("@MainActor");
        }

        // Parameter order: newValue first, self last — matches C# P/Invoke parameter layout
        // where HandleSwiftSelf (UsesFreeFunctionWrapper) appends self AFTER value params.
        swiftWriter.WriteLines($$"""
            @_silgen_name("{{symbolName}}")
            public func _sbw_set_{{propertyDecl.Name}}_{{EmitterUtility.DeterministicHash8(symbolName)}}(_ newValue: {{propertySwiftType}}, _ self_: {{moduleQualifiedName}}) {
                self_.{{propertyDecl.Name}} = newValue
            }
            """);
    }
}
