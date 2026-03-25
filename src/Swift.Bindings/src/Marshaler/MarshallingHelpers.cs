// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    public static class MarshallingHelpers
    {
        private static readonly SwiftTypeName SwiftStringTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String");
        private static readonly SwiftTypeName SwiftArrayTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array");
        private static readonly SwiftTypeName SwiftDictionaryTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Dictionary");
        private static readonly SwiftTypeName SwiftSetTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Set");
        private static readonly SwiftTypeName SwiftOptionalTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional");

        /// <summary>
        /// Determines whether the specified type spec represents a type that can be
        /// automatically converted to/from an idiomatic .NET type (String, Array, Dictionary, or Optional).
        /// </summary>
        public static bool IsConvertibleType(TypeSpec? typeSpec)
        {
            return IsSwiftString(typeSpec) ||
                   IsSwiftArray(typeSpec) ||
                   IsSwiftDictionary(typeSpec) ||
                   IsSwiftSet(typeSpec) ||
                   IsSwiftOptional(typeSpec) ||
                   IsFoundationDate(typeSpec);
        }

        /// <summary>
        /// Checks whether the type spec represents Foundation.Date.
        /// Date needs conversion: Swift Date = Double (8 bytes), C# DateTimeOffset = 12 bytes.
        /// The type database maps Date → double (matching ABI). DateProjection handles
        /// the double ↔ DateTimeOffset conversion in method bodies and property accessors.
        /// </summary>
        public static bool IsFoundationDate(TypeSpec? typeSpec)
        {
            return typeSpec is NamedTypeSpec named && named.Name == "Foundation.Date";
        }

        /// <summary>
        /// Checks whether a type spec is a <see cref="NamedTypeSpec"/> with a module
        /// and matches the given <paramref name="expectedName"/>.
        /// </summary>
        private static bool MatchesSwiftTypeName(TypeSpec? typeSpec, SwiftTypeName expectedName)
        {
            if (typeSpec is not NamedTypeSpec namedTypeSpec)
                return false;
            if (!namedTypeSpec.HasModule())
                return false;
            return SwiftTypeName.FromTypeSpec(namedTypeSpec).Equals(expectedName);
        }

        /// <summary>
        /// Determines whether the specified type spec represents Swift.String.
        /// </summary>
        public static bool IsSwiftString(TypeSpec? typeSpec) => MatchesSwiftTypeName(typeSpec, SwiftStringTypeName);

        /// <summary>
        /// Determines whether the specified type spec represents Swift.Array.
        /// </summary>
        public static bool IsSwiftArray(TypeSpec? typeSpec) => MatchesSwiftTypeName(typeSpec, SwiftArrayTypeName);

        /// <summary>
        /// Determines whether the specified type spec represents Swift.Dictionary.
        /// </summary>
        public static bool IsSwiftDictionary(TypeSpec? typeSpec) => MatchesSwiftTypeName(typeSpec, SwiftDictionaryTypeName);

        /// <summary>
        /// Determines whether the specified type spec represents Swift.Set.
        /// </summary>
        public static bool IsSwiftSet(TypeSpec? typeSpec) => MatchesSwiftTypeName(typeSpec, SwiftSetTypeName);

        /// <summary>
        /// Determines whether the specified type spec represents Swift.Optional.
        /// </summary>
        public static bool IsSwiftOptional(TypeSpec? typeSpec) => MatchesSwiftTypeName(typeSpec, SwiftOptionalTypeName);

        /// <summary>
        /// Determines whether the specified type spec represents Swift.Optional wrapping
        /// an ObjC bridged type (e.g., Optional&lt;UIImage&gt;, Optional&lt;NSUrlResponse&gt;).
        /// ObjC optionals use nullable pointer ABI (nil = IntPtr.Zero), not SwiftOptional layout.
        /// </summary>
        public static bool IsOptionalObjCBridged(TypeSpec? typeSpec, ITypeDatabase typeDatabase)
        {
            if (!IsSwiftOptional(typeSpec))
                return false;
            var namedType = (NamedTypeSpec)typeSpec!;
            if (namedType.GenericParameters.Count != 1)
                return false;
            var inner = namedType.GenericParameters[0];
            if (inner is not NamedTypeSpec innerNamed || !innerNamed.HasModule())
                return false;
            var innerTypeName = SwiftTypeName.FromModuleQualifiedName(innerNamed.Name);
            if (typeDatabase.TryGetTypeRecord(innerTypeName, out var typeRecord))
                return IsObjCBridged(typeRecord) || IsObjCBridgeable(typeRecord);
            // Fallback: Apple framework ObjC classes (e.g., QuartzCore.CALayer) not in the module
            // database. Must match TypeProjectionFactory's Optional<T> fallback exactly:
            // IsOptionalFallbackModule + HasObjCClassPrefix → ObjCBridgedProjection.
            if (AppleFrameworkRegistry.IsOptionalFallbackModule(innerNamed.Module) &&
                !AppleFrameworkRegistry.IsNestedType(innerNamed.Name) &&
                !TypeDatabaseExtensions.IsKnownAppleValueType(innerNamed) &&
                AppleFrameworkRegistry.HasObjCClassPrefix(innerNamed.Name))
                return true;
            return false;
        }

        public static bool MethodRequiresIndirectResult(MethodEnvironment env)
        {
            if (env.MethodDecl.IsAsync) return false;

            if (IsCdeclNonSetterWrapper(env))
            {
                var cdeclResult = IsCdeclIndirectResultRequired(env);
                if (cdeclResult.HasValue) return cdeclResult.Value;
            }

            var ctorResult = IsConstructorIndirectResultRequired(env);
            if (ctorResult.HasValue) return ctorResult.Value;

            return IsTypeInherentlyIndirect(env);
        }

        /// <summary>
        /// Returns true if the method uses a @_cdecl wrapper and is NOT a setter.
        /// This guard condition appears repeatedly in indirect-result logic because
        /// setters return void and never need indirect result handling.
        /// </summary>
        internal static bool IsCdeclNonSetterWrapper(MethodEnvironment env)
        {
            return (env.MethodDecl.UsesCdeclPropertyWrapper || env.MethodDecl.UsesCdeclMethodWrapper)
                && !MethodIsSetter(env.MethodDecl);
        }

        /// <summary>
        /// Determines indirect result requirements specific to @_cdecl wrappers.
        /// Checks String, existential, Optional&lt;value&gt;, closure, DynamicSelf, and tuple returns.
        /// Returns null if no @_cdecl-specific decision applies (fall through to general logic).
        /// </summary>
        internal static bool? IsCdeclIndirectResultRequired(MethodEnvironment env)
        {
            var returnTypeForCdecl = env.MethodDecl.CSSignature.First();

            // Void returns never need indirect result
            if (returnTypeForCdecl.SwiftTypeSpec.IsEmptyTuple)
                return false;

            if (returnTypeForCdecl.SwiftTypeSpec is NamedTypeSpec nts && nts.Name == "Swift.String")
                return true;

            // Existential returns: @_cdecl can't return existential containers directly.
            if (env.ExistentialHandler.IsExistential(returnTypeForCdecl.SwiftTypeSpec))
                return true;

            // Optional<value-type>: @_cdecl can't return generics directly.
            // Exception: Optional<ObjC-bridgeable container> (e.g., [URL]?) returns as nullable ObjC pointer.
            if (MethodWrapperEmitter.IsOptionalType(returnTypeForCdecl.SwiftTypeSpec) &&
                !CdeclParamMapper.IsOptionalWithReferenceInner(returnTypeForCdecl.SwiftTypeSpec, env.TypeDatabase) &&
                !CdeclParamMapper.IsOptionalObjCBridgeableContainer(returnTypeForCdecl.SwiftTypeSpec, env.TypeDatabase))
                return true;

            // Closure returns: @_cdecl can't return closures directly — write to resultPtr buffer.
            if (returnTypeForCdecl.SwiftTypeSpec is ClosureTypeSpec)
                return true;

            // Bound generic collection returns (Array, Dictionary, Set): @_cdecl can't return
            // generics directly. Swift wrapper writes to resultPtr via initializeMemory(as:).
            // Exception: ObjC-bridgeable containers (e.g., [URL]) return as retained ObjC pointer directly.
            if (env.BoundGenericsHandler.IsBoundGeneric(returnTypeForCdecl) &&
                env.BoundGenericsHandler.RequiresBoundGenericMarshalling(returnTypeForCdecl) &&
                MethodWrapperEmitter.IsSupportedCollectionType(returnTypeForCdecl.SwiftTypeSpec) &&
                !CdeclParamMapper.IsObjCBridgeableContainer(returnTypeForCdecl.SwiftTypeSpec, env.TypeDatabase))
                return true;

            // DynamicSelf (Self): @_cdecl wrapper returns retained class pointer directly.
            if (returnTypeForCdecl.SwiftTypeSpec.IsDynamicSelf)
                return false;

            // Tuple returns: @_cdecl wrapper writes result to resultPtr buffer.
            if (returnTypeForCdecl.SwiftTypeSpec is TupleTypeSpec ts && !ts.IsEmptyTuple)
                return true;

            return null; // No @_cdecl-specific decision — fall through
        }

        /// <summary>
        /// Determines indirect result requirements for constructors.
        /// Failable constructors and non-frozen struct constructors need indirect result.
        /// Returns null if the method is not a constructor or no constructor-specific rule applies.
        /// </summary>
        internal static bool? IsConstructorIndirectResultRequired(MethodEnvironment env)
        {
            if (!env.MethodDecl.IsConstructor) return null;

            // Failable constructors (init?) always need indirect result because they return
            // Optional<Self> which must be checked for None before extracting the value.
            if (env.MethodDecl.IsFailable) return true;

            // Non-frozen struct constructors use indirect result (struct too large for registers).
            // Class constructors return a pointer directly — NOT via indirect result.
            // Enum constructors fall through to type-based checks.
            if (env.ParentDecl is StructDecl structDecl && !structDecl.IsFrozen) return true;

            // @_cdecl constructor wrappers for structs/enums always write to resultPtr.
            // The Swift @_cdecl function signature takes UnsafeMutableRawPointer as the first
            // parameter and returns void (see CdeclSignatureContract: "Struct constructors
            // always write to result buffer"). The C# P/Invoke must match by adding an IntPtr
            // resultPtr parameter and returning void, not returning the struct by value.
            if (env.MethodDecl.UsesCdeclConstructorWrapper && env.ParentDecl is not ClassDecl)
                return true;

            return null; // Enum constructors or frozen struct constructors — fall through
        }

        /// <summary>
        /// Type-based indirect result determination for non-@_cdecl and non-constructor cases.
        /// Checks DynamicSelf, closures, existentials, tuples, bound generics, and TypeRecord-based dispatch.
        /// </summary>
        internal static bool IsTypeInherentlyIndirect(MethodEnvironment env)
        {
            var returnType = env.MethodDecl.CSSignature.First();
            bool isCdeclNonSetter = IsCdeclNonSetterWrapper(env);

            // DynamicSelf (Self return type) always requires indirect result.
            if (returnType.SwiftTypeSpec.IsDynamicSelf)
                return true;

            // Closure return types: non-cdecl passes as function pointers directly.
            if (returnType.SwiftTypeSpec is ClosureTypeSpec)
                return false;

            // Existential return types (protocol types) are passed via existential containers (IntPtr)
            if (env.ExistentialHandler.IsExistential(returnType.SwiftTypeSpec))
                return false;

            // Non-generic tuple return types are handled by TupleHandler, not via indirect result.
            // Tuples with generic type parameter elements require indirect result (sret).
            if (returnType.SwiftTypeSpec is TupleTypeSpec tupleSpec && !tupleSpec.IsEmptyTuple)
            {
                var tupleHandler = new TupleHandler(env.TypeDatabase);
                return tupleHandler.HasGenericTypeParameterElements(tupleSpec);
            }

            // Bound generics that require marshalling return IntPtr directly from PInvoke.
            if (!env.MethodDecl.IsConstructor &&
                env.BoundGenericsHandler.IsBoundGeneric(returnType) &&
                env.BoundGenericsHandler.RequiresBoundGenericMarshalling(returnType))
            {
                // @_cdecl Optional<value-type>: force IndirectResult instead of IntPtr marshalling.
                if (isCdeclNonSetter &&
                    MethodWrapperEmitter.IsOptionalType(returnType.SwiftTypeSpec) &&
                    !CdeclParamMapper.IsOptionalWithReferenceInner(returnType.SwiftTypeSpec, env.TypeDatabase))
                {
                    return true;
                }
                return false;
            }

            if (returnType.IsGeneric) return true;

            TypeRecord typeRecord = env.TypeDatabase.GetTypeRecordOrThrow(returnType.SwiftTypeSpec);

            // @_cdecl: NSString typedef structs need indirect result.
            if (isCdeclNonSetter &&
                IsObjCBridged(typeRecord) &&
                returnType.SwiftTypeSpec is NamedTypeSpec nsTypedefSpec &&
                AppleFrameworkRegistry.TryGetNetTypeName(nsTypedefSpec.Name, out var remappedName) &&
                remappedName == "Foundation.NSString")
                return true;

            // Swift classes return pointers directly in registers
            if (typeRecord.Kind == TypeRecordKind.Class)
                return false;

            // Simple enums are C# value types returned directly in registers
            if (typeRecord.Kind == TypeRecordKind.Enum &&
                (typeRecord.Flags & TypeRecordFlags.SimpleEnum) != 0)
                return false;

            // ObjC-bridgeable value types (e.g., URL) return as ObjC class pointers, not indirect result.
            if (IsObjCBridgeable(typeRecord))
                return false;

            if (!IsTypeFrozen(typeRecord)) return true;

            // @_cdecl: frozen structs also need indirect result (except primitives).
            if (isCdeclNonSetter &&
                !CdeclParamMapper.IsCdeclPrimitive(returnType.SwiftTypeSpec))
                return true;

            return false;
        }

        public static bool MethodRequiresSwiftSelf(MethodEnvironment env)
        {
            if (env.ParentDecl is ModuleDecl) return false; // global funcs
            if (env.MethodDecl.MethodType == MethodType.Static) return false;
            if (env.MethodDecl.IsConstructor) return false;

            return true;
        }

        public static bool IsTypeFrozen(TypeRecord typeRecord)
        {
            return (typeRecord.Flags & TypeRecordFlags.Frozen) != 0;
        }

        public static bool RequiresMemoryManagement(TypeRecord typeRecord)
        {
            return (typeRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0;
        }

        public static bool IsFrozenStructProjectedAsClass(TypeRecord typeRecord)
        {
            return typeRecord.Kind == TypeRecordKind.Struct &&
                   (typeRecord.Flags & TypeRecordFlags.Frozen) != 0 &&
                   (typeRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0;
        }

        /// <summary>
        /// Determines if a type is an Objective-C bridged class that should be remapped
        /// to its .NET iOS binding equivalent (e.g., UIKit.UIImage instead of Swift.UIImage).
        /// </summary>
        /// <param name="typeRecord">The type record to check.</param>
        /// <returns>True if the type is ObjC bridged, false otherwise.</returns>
        public static bool IsObjCBridged(TypeRecord typeRecord)
        {
            return (typeRecord.Flags & TypeRecordFlags.ObjCBridged) != 0;
        }

        /// <summary>
        /// Checks whether a type record represents an ObjC-bridgeable value type (e.g., Foundation.URL).
        /// These Swift value types freely bridge to ObjC classes via _ObjectiveCBridgeable and cross
        /// the @_cdecl boundary as ObjC object pointers instead of Swift struct bytes.
        /// </summary>
        public static bool IsObjCBridgeable(TypeRecord typeRecord)
        {
            return (typeRecord.Flags & TypeRecordFlags.ObjCBridgeable) != 0;
        }

        /// <summary>
        /// Unwraps Optional&lt;T&gt; to get the inner type spec, recursively handling nested optionals.
        /// Returns null if the input is not a NamedTypeSpec.
        /// </summary>
        public static TypeSpec? UnwrapOptionalTypeSpec(TypeSpec typeSpec)
        {
            if (typeSpec is not NamedTypeSpec namedType)
                return null;

            var nameWithoutModule = namedType.Name.Contains('.')
                ? namedType.Name.Substring(namedType.Name.LastIndexOf('.') + 1)
                : namedType.Name;
            if (nameWithoutModule == "Optional" && namedType.GenericParameters.Count == 1)
                return UnwrapOptionalTypeSpec(namedType.GenericParameters[0]);

            return namedType;
        }

        /// <summary>
        /// Determines if a class type is rooted in an ObjC hierarchy (e.g., inherits from NSObject/CALayer).
        /// </summary>
        public static bool IsObjCRooted(TypeRecord typeRecord)
        {
            return (typeRecord.Flags & TypeRecordFlags.ObjCRooted) != 0;
        }

        /// <summary>
        /// Determines if a method is a property setter based on its name.
        /// Property setters are generated with names ending in "_Set".
        /// </summary>
        /// <param name="methodDecl">The method declaration to check.</param>
        /// <returns>True if the method is a property setter, false otherwise.</returns>
        public static bool MethodIsSetter(MethodDecl methodDecl)
        {
            return methodDecl.Name.EndsWith("_Set");
        }

        /// <summary>
        /// Maps a Swift module name to its corresponding .NET namespace.
        /// Returns the original module name if no mapping exists.
        /// All emission paths MUST use this method instead of maintaining local copies of the mapping.
        /// </summary>
        public static string MapSwiftModuleToNetNamespace(string swiftModule)
            => AppleFrameworkRegistry.MapModuleToNetNamespace(swiftModule);

        /// <summary>
        /// Maps a module-qualified Swift type name (e.g., "QuartzCore.CALayer") to its
        /// .NET equivalent (e.g., "CoreAnimation.CALayer"). If the module has no mapping,
        /// the original name is returned unchanged.
        /// </summary>
        public static string MapQualifiedTypeToNet(string qualifiedSwiftTypeName)
        {
            if (string.IsNullOrEmpty(qualifiedSwiftTypeName))
                return qualifiedSwiftTypeName;

            // Check explicit type remapping first (handles both module remapping AND type name changes)
            if (AppleFrameworkRegistry.TryGetNetTypeName(qualifiedSwiftTypeName, out var remapped))
                return remapped;

            var dotIndex = qualifiedSwiftTypeName.IndexOf('.');
            if (dotIndex <= 0)
                return qualifiedSwiftTypeName;

            var swiftModule = qualifiedSwiftTypeName.Substring(0, dotIndex);
            var typeName = qualifiedSwiftTypeName.Substring(dotIndex + 1);
            var netNamespace = MapSwiftModuleToNetNamespace(swiftModule);
            return $"{netNamespace}.{typeName}";
        }

        /// <summary>
        /// Replaces all known Swift module names with their .NET namespace equivalents
        /// within a free-form string (e.g., diagnostic messages, attribute content).
        /// Matches module names followed by a dot to avoid false positives.
        /// </summary>
        public static string MapModulesInString(string text)
            => AppleFrameworkRegistry.MapModulesInString(text);

        /// <summary>
        /// Gets the fully-qualified .NET base type name for an ObjC-rooted class.
        /// Maps the Swift module to the corresponding .NET namespace and uses the
        /// ObjC class name from the superclass chain.
        /// </summary>
        /// <param name="classDecl">The class declaration with an ObjC superclass.</param>
        /// <returns>The .NET type name (e.g., "CoreAnimation.CALayer", "UIKit.UIControl").</returns>
        public static string? GetObjCBaseTypeName(ClassDecl classDecl)
        {
            if (!classDecl.HasObjCSuperclass || classDecl.DirectSuperclassName == null)
                return null;

            // If the superclass is from an unsupported Apple module (XCTest, SwiftUI, etc.),
            // fall back to Foundation.NSObject — all ObjC-rooted types ultimately derive from it.
            var dotIdx = classDecl.DirectSuperclassName.IndexOf('.');
            if (dotIdx > 0)
            {
                var module = classDecl.DirectSuperclassName.Substring(0, dotIdx);
                if (module is "SwiftUI" or "XCTest" or "Combine" or "_Concurrency"
                    or "Observation" or "WidgetKit" or "AppIntents" or "Charts" or "TipKit")
                    return "Foundation.NSObject";
            }

            return MapQualifiedTypeToNet(classDecl.DirectSuperclassName);
        }

        /// <summary>
        /// Checks if a P/Invoke type string represents bool, which requires
        /// [MarshalAs(UnmanagedType.U1)] for LibraryImport compatibility.
        /// Used for both parameter and return type marshalling.
        /// </summary>
        public static bool IsBoolType(string type) => type == "bool";

        /// <summary>
        /// Checks if a Swift type spec represents Bool, which requires conversion in callbacks.
        /// </summary>
        public static bool IsBoolType(TypeSpec typeSpec)
        {
            return typeSpec is NamedTypeSpec namedType && namedType.Name == "Swift.Bool";
        }

        /// <summary>
        /// Checks if a type name represents a Swift primitive type.
        /// </summary>
        public static bool IsSwiftPrimitive(string typeName)
        {
            return typeName switch
            {
                "Swift.Int" or "Swift.Int8" or "Swift.Int16" or "Swift.Int32" or "Swift.Int64" => true,
                "Swift.UInt" or "Swift.UInt8" or "Swift.UInt16" or "Swift.UInt32" or "Swift.UInt64" => true,
                "Swift.Float" or "Swift.Double" => true,
                "Swift.Bool" => true,
                "CoreFoundation.CGFloat" => true,
                "CoreFoundation.CGSize" or "CoreFoundation.CGPoint" or "CoreFoundation.CGRect" => true,
                _ => false,
            };
        }

        /// <summary>
        /// Swift type aliases that resolve to primitives.
        /// </summary>
        public static readonly Dictionary<string, string> TypeAliasToCSPrimitive = new(StringComparer.Ordinal)
        {
            { "Foundation.TimeInterval", "double" },
        };

        /// <summary>
        /// Checks if a C# type name belongs to a CoreFoundation framework namespace.
        /// CoreFoundation types inherit from INativeObject (not NSObject), requiring
        /// GetINativeObject instead of GetNSObject for bridging.
        /// </summary>
        public static bool IsCoreFoundationType(string typeName)
        {
            return typeName.StartsWith("CoreText.") ||
                   typeName.StartsWith("CoreGraphics.") ||
                   typeName.StartsWith("CoreImage.") ||
                   typeName.StartsWith("CoreAnimation.") ||
                   typeName.StartsWith("CoreMedia.") ||
                   typeName.StartsWith("CoreVideo.") ||
                   typeName.StartsWith("Security.") ||
                   typeName.StartsWith("CoreFoundation.");
        }

        /// <summary>
        /// Formats the correct ObjC bridge call for a given type, dispatching between
        /// GetNSObject (for NSObject subclasses) and GetINativeObject (for CoreFoundation types).
        /// </summary>
        /// <param name="publicType">The C# public type name (e.g., "UIKit.UIImage", "CoreText.CTFont").</param>
        /// <param name="resultExpr">The expression holding the IntPtr result.</param>
        /// <param name="nonNull">If true, appends ! for non-null assertion.</param>
        public static string FormatObjCBridgeCall(string publicType, string resultExpr, bool nonNull = false)
        {
            var suffix = nonNull ? "!" : "";
            if (IsCoreFoundationType(publicType))
                return $"ObjCRuntime.Runtime.GetINativeObject<{publicType}>({resultExpr}, false){suffix}";
            return $"ObjCRuntime.Runtime.GetNSObject<{publicType}>({resultExpr}){suffix}";
        }
    }
}
