// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    public static class MarshallingHelpers // TODO: Find better place for those
    {
        public static bool MethodRequiresIndirectResult(MethodEnvironment env)
        {
            if (env.MethodDecl.IsAsync) return false;

            if (env.MethodDecl.IsConstructor && !(env.ParentDecl is StructDecl structDecl && structDecl.IsFrozen)) return true;

            var returnType = env.MethodDecl.CSSignature.First();

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
            return (typeRecord.Flags & TypeRecordFlags.Frozen) != 0 && (typeRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0;
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
        /// Determines if a method is a property setter based on its name.
        /// Property setters are generated with names ending in "_Set".
        /// </summary>
        /// <param name="methodDecl">The method declaration to check.</param>
        /// <returns>True if the method is a property setter, false otherwise.</returns>
        public static bool MethodIsSetter(MethodDecl methodDecl)
        {
            return methodDecl.Name.EndsWith("_Set");
        }
    }
}
