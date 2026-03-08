// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    public static class MarshallingHelpers // TODO: Find better place for those
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
                   IsSwiftOptional(typeSpec);
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
                return IsObjCBridged(typeRecord);
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

            // Failable constructors (init?) always need indirect result because they return
            // Optional<Self> which must be checked for None before extracting the value.
            if (env.MethodDecl.IsConstructor && env.MethodDecl.IsFailable) return true;

            if (env.MethodDecl.IsConstructor && !(env.ParentDecl is StructDecl structDecl && structDecl.IsFrozen)) return true;

            var returnType = env.MethodDecl.CSSignature.First();

            // DynamicSelf (Self return type) always requires indirect result.
            // Currently this works via the accidental path: IsExistentialTypeName("Self") → AnyType → not frozen → true.
            // This explicit guard prevents breakage if IsExistentialTypeName heuristic changes.
            if (returnType.SwiftTypeSpec.IsDynamicSelf)
                return true;

            // Closure return types don't require indirect result - they are passed as function pointers
            if (returnType.SwiftTypeSpec is ClosureTypeSpec)
                return false;

            // Existential return types (protocol types and compositions) are passed via existential containers (IntPtr)
            if (env.ExistentialHandler.IsExistential(returnType.SwiftTypeSpec))
                return false;

            // Non-generic tuple return types are handled by TupleHandler, not via indirect result.
            // Tuples with generic type parameter elements require indirect result because
            // element sizes are unknown at compile time — the Swift ABI mandates sret for these.
            if (returnType.SwiftTypeSpec is TupleTypeSpec tupleSpec && !tupleSpec.IsEmptyTuple)
            {
                var tupleHandler = new TupleHandler(env.TypeDatabase);
                if (tupleHandler.HasGenericTypeParameterElements(tupleSpec))
                    return true;
                return false;
            }

            // Bound generics that require marshalling (SwiftArray, SwiftOptional, etc.) return IntPtr directly
            // from PInvoke and don't need indirect result handling. They're marshalled via SwiftMarshal.MarshalFromSwift.
            // Note: This doesn't apply to constructors (handled above) since failable initializers need special handling.
            if (!env.MethodDecl.IsConstructor &&
                env.BoundGenericsHandler.IsBoundGeneric(returnType) &&
                env.BoundGenericsHandler.RequiresBoundGenericMarshalling(returnType))
            {
                return false;
            }

            if (returnType.IsGeneric) return true;

            TypeRecord typeRecord = env.TypeDatabase.GetTypeRecordOrThrow(returnType.SwiftTypeSpec);

            // Swift classes return pointers directly in registers, not via indirect result
            if (typeRecord.Kind == TypeRecordKind.Class)
                return false;

            // Simple enums are C# value types returned directly in registers
            // regardless of frozen status — no-payload enums are always register-sized
            if (typeRecord.Kind == TypeRecordKind.Enum &&
                (typeRecord.Flags & TypeRecordFlags.SimpleEnum) != 0)
                return false;

            if (!IsTypeFrozen(typeRecord)) return true;
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
