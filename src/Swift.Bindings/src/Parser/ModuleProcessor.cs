// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using Swift.Runtime;

namespace BindingsGeneration
{
    /// <summary>
    /// Represents the result of processing a Swift module.
    /// </summary>
    /// <param name="ModuleDatabase">The module database containing type records.</param>
    sealed record ModuleProcessingResult(ModuleTypeDatabase ModuleDatabase);

    /// <summary>
    /// Performs post-processing of types collected from the Swift ABI before generating bindings.
    /// Calculates properties on types which are not directly available from the ABI.
    /// Generates type database entries for structs, enums, and classes.
    /// </summary>
    internal class ModuleProcessor
    {
        private readonly string _module;
        private readonly string _dylibPath;
        private readonly ITypeDatabase _typeDatabase;
        private readonly ModuleTypeDatabase _moduleDatabase;
        private readonly Dictionary<NamedTypeSpec, TypeDecl> _typeDecls;
        private readonly NamespacePatternResolver _namespacePatternResolver;
        private readonly ILogger _logger;
        private readonly HashSet<string> _processingInProgress = new(StringComparer.Ordinal);

        /// <summary>
        /// Initializes a new instance of the <see cref="ModuleProcessor"/> class.
        /// </summary>
        /// <param name="module">The name of the Swift module being processed.</param>
        /// <param name="dylibPath">The file path to the Swift dynamic library (used for metadata extraction).</param>
        /// <param name="runtimeLibraryName">The library name for DllImport in generated code.</param>
        /// <param name="typeDecls">A dictionary mapping Swift type specs to their declarations.</param>
        /// <param name="typeDatabase">The global type database tracking processed types.</param>
        /// <param name="logger">Logger instance.</param>
        public ModuleProcessor(
            string module,
            string dylibPath,
            string runtimeLibraryName,
            Dictionary<NamedTypeSpec, TypeDecl> typeDecls,
            ITypeDatabase typeDatabase,
            ILogger logger,
            NamespacePatternResolver? namespacePatternResolver = null)
        {
            _module = module;
            _dylibPath = dylibPath;
            _typeDatabase = typeDatabase;
            // Use runtimeLibraryName for DllImport in generated code
            _moduleDatabase = new ModuleTypeDatabase(module, runtimeLibraryName);
            _typeDecls = typeDecls;
            _namespacePatternResolver = namespacePatternResolver ?? new NamespacePatternResolver();
            _logger = logger;
        }

        /// <summary>
        /// Tries to retrieve a <see cref="TypeRecord"/> for the specified Swift type.
        /// </summary>
        /// <param name="swiftTypeSpec">The Swift type specification.</param>
        /// <param name="record">
        /// When this method returns, contains the <see cref="TypeRecord"/> if found; otherwise, <c>null</c>.
        /// </param>
        /// <returns><c>true</c> if the type was found; otherwise, <c>false</c>.</returns>
        private bool TryGetTypeRecord(NamedTypeSpec swiftTypeSpec, [NotNullWhen(true)] out TypeRecord? record)
        {
            var swiftTypeName = SwiftTypeName.FromTypeSpec(swiftTypeSpec);

            // First, check if this module is the one being processed.
            if (swiftTypeName.Module == _module)
            {
                return _moduleDatabase.TryGetTypeRecord(swiftTypeName, out record);
            }

            // Otherwise, fall back to checking the global type database.
            return _typeDatabase.TryGetTypeRecord(swiftTypeName, out record);
        }

        /// <summary>
        /// Executes the post-processing workflow for all unprocessed types in the current module.
        /// Produces type database entries for structs, enums, and classes.
        /// </summary>
        /// <returns>A <see cref="ModuleProcessingResult"/>The module database and out-of-module type records.</returns>
        public ModuleProcessingResult FinalizeTypeProcessingAndCreateModuleDatabase()
        {
            foreach (var (typeSpec, typeDecl) in _typeDecls)
            {
                ProcessTypeRecursively(typeSpec, typeDecl);
            }

            ResolveClassHierarchy();
            DemoteSimpleEnumsUsedAsGenericArgs();

            return new ModuleProcessingResult(_moduleDatabase);
        }

        /// <summary>
        /// Recursively processes a type. If the type is a struct, enum, or class,
        /// calls into specialized handlers.
        /// </summary>
        /// <param name="namedTypeSpec">The Swift type specification.</param>
        /// <param name="typeDecl">The associated type declaration.</param>
        private void ProcessTypeRecursively(NamedTypeSpec namedTypeSpec, TypeDecl typeDecl)
        {
            var swiftTypeName = SwiftTypeName.FromTypeSpec(namedTypeSpec);
            if (_moduleDatabase.IsTypeProcessed(swiftTypeName))
                return;

            if (!_processingInProgress.Add(swiftTypeName.ModuleQualifiedName))
                return; // Cycle detected — already processing this type

            try
            {
                switch (typeDecl)
                {
                    case StructDecl structDecl:
                        ProcessStruct(namedTypeSpec, structDecl);
                        break;

                    case EnumDecl enumDecl:
                        ProcessEnum(namedTypeSpec, enumDecl);
                        break;

                    case ClassDecl classDecl:
                        ProcessClass(namedTypeSpec, classDecl);
                        break;

                    case ProtocolDecl protocolDecl:
                        ProcessProtocol(namedTypeSpec, protocolDecl);
                        break;

                    default:
                        _logger.LogWarning($"Skipping unknown type declaration '{typeDecl.GetType().Name}'.");
                        break;
                }
            }
            finally
            {
                _processingInProgress.Remove(swiftTypeName.ModuleQualifiedName);
            }
        }

        /// <summary>
        /// Processes a Swift struct declaration to determine final properties such as blittability
        /// and frozenness, then registers it into the type database.
        /// </summary>
        /// <param name="namedTypeSpec">The Swift type specification for the struct.</param>
        /// <param name="structDecl">The struct declaration.</param>
        private void ProcessStruct(NamedTypeSpec namedTypeSpec, StructDecl structDecl)
        {
            // Ensure that all properties are processed or known in the database.
            ProcessStructProperties(structDecl);

            // Get metadata pointer if possible (may fail for cross-platform builds, e.g., iOS on macOS)
            IntPtr metadataPtr = IntPtr.Zero;
            if (!string.IsNullOrEmpty(structDecl.MetadataAccessor))
            {
                try
                {
                    metadataPtr = DynamicLibraryLoader.invoke(_dylibPath, structDecl.MetadataAccessor);
                }
                catch
                {
                    // If metadata accessor fails (e.g., iOS dylib on macOS), continue without metadata
                    _logger.LogWarning($"Failed to get metadata for struct '{structDecl.Name}'. Continuing without metadata.");
                }
            }
            var swiftTypeInfo = new SwiftTypeInfo { MetadataPtr = metadataPtr };

            TypeRecordFlags flags = CacluateFlags(structDecl);

            RegisterStructType(namedTypeSpec, structDecl, swiftTypeInfo, flags);

            // Update the struct declaration in memory so future passes see these properties.
            structDecl.IsFrozen = (flags & TypeRecordFlags.Frozen) != 0;
        }

        /// <summary>
        /// Ensures that each property in the struct is either from an already processed type
        /// or is recursively processed in this module.
        /// </summary>
        /// <param name="structDecl">The struct declaration.</param>
        /// <exception cref="Exception">
        /// Thrown if a property type cannot be found or processed.
        /// </exception>
        private void ProcessStructProperties(StructDecl structDecl)
        {
            foreach (var propertyDecl in structDecl.Properties)
            {
                // Skip static properties
                if (propertyDecl.IsStatic)
                    continue;

                // Skip existential types - they don't have TypeDecl entries
                // This includes protocol compositions (ProtocolListTypeSpec) and
                // single-protocol existentials (NamedTypeSpec with IsAny=true)
                if (propertyDecl.SwiftTypeSpec is ProtocolListTypeSpec)
                    continue;

                if (propertyDecl.SwiftTypeSpec is not NamedTypeSpec namedPropertyType)
                    continue;

                // Skip existential NamedTypeSpec (any Protocol syntax)
                if (namedPropertyType.IsAny)
                    continue;

                // If the property is from a different module, ensure that type is already processed.
                if (namedPropertyType.Module != _module)
                {
                    if (!_typeDatabase.IsTypeProcessed(namedPropertyType))
                    {
                        _logger.LogWarning(
                                $"Skipping property '{propertyDecl.Name}' of type '{namedPropertyType.NameWithoutModule}' " +
                                $"from module '{namedPropertyType.Module}'. Type should have been processed " +
                                "in a previous module but was not found.");
                        continue;
                    }
                }
                // If the property is in the same module, process it recursively.
                else
                {
                    if (!_typeDecls.TryGetValue(namedPropertyType, out var nestedDecl))
                    {
                        _logger.LogWarning(
                                $"Skipping property '{propertyDecl.Name}' of type '{namedPropertyType.NameWithoutModule}' " +
                                $"from module '{namedPropertyType.Module}'. Not found in type declarations.");
                        continue;
                    }
                    ProcessTypeRecursively(namedPropertyType, nestedDecl);
                }
            }
        }

        /// <summary>
        /// Determines whether a struct is truly frozen. The struct itself must be marked frozen
        /// and all of its properties must also be from frozen types.
        /// </summary>
        /// <param name="structDecl">The struct declaration.</param>
        /// <returns><c>true</c> if the struct is frozen; otherwise, <c>false</c>.</returns>
        private TypeRecordFlags CacluateFlags(StructDecl structDecl)
        {
            TypeRecordFlags flags = TypeRecordFlags.None;

            if (!structDecl.IsFrozen)
                return TypeRecordFlags.None;

            if (structDecl.IsFrozen)
                flags |= TypeRecordFlags.Frozen;

            foreach (var propertyDecl in structDecl.Properties)
            {
                if (propertyDecl.SwiftTypeSpec is not NamedTypeSpec namedPropertyType)
                    continue;

                if (propertyDecl.IsStatic)
                    continue;

                if (!TryGetTypeRecord(namedPropertyType, out var propertyRecord))
                {
                    // Generic types (e.g., Swift.KeyPath<T, V>) may not be registered in the type database.
                    // Treat as non-frozen and requiring memory management to be safe.
                    _logger.LogWarning($"Type not found in the database: {namedPropertyType}. Assuming non-frozen.");
                    flags &= ~TypeRecordFlags.Frozen;
                    flags |= TypeRecordFlags.RequiresMemoryManagement;
                    continue;
                }

                // If any property is not frozen struct, remove the frozen flag
                if (propertyRecord.Kind == TypeRecordKind.Struct && (propertyRecord.Flags & TypeRecordFlags.Frozen) == 0)
                    flags &= ~TypeRecordFlags.Frozen;

                // If any property is heap-allocated, set the RequiresMemoryManagement flag
                if ((propertyRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0 || propertyRecord.Kind == TypeRecordKind.Class)
                    flags |= TypeRecordFlags.RequiresMemoryManagement;
            }

            return flags;
        }

        /// <summary>
        /// Inserts a struct's details (e.g. name, metadata accessor, frozenness, blittability) into the type database.
        /// </summary>
        /// <param name="namedTypeSpec">The Swift type specification, including module name.</param>
        /// <param name="structDecl">The struct declaration node.</param>
        /// <param name="swiftTypeInfo">Pointer to the Swift metadata plus ValueWitnessTable.</param>
        /// <param name="isFrozen">Indicates whether the struct is effectively frozen.</param>
        private void RegisterStructType(
            NamedTypeSpec namedTypeSpec,
            StructDecl structDecl,
            SwiftTypeInfo swiftTypeInfo,
            TypeRecordFlags flags)
        {
            var @namespace = _namespacePatternResolver.ResolveNamespace(namedTypeSpec.Module);
            var rawIdentifier = structDecl.SwiftTypeName.Module == ""
                ? structDecl.SwiftTypeName.Name
                : structDecl.SwiftTypeName.ModuleQualifiedName.Substring(
                    structDecl.SwiftTypeName.ModuleQualifiedName.IndexOf(".") + 1);
            var csharpTypeIdentifier = string.Join(".",
                rawIdentifier.Split('.').Select(NameProvider.ToPascalCaseForTypeName));
            var typeRecord = new TypeRecord
            {
                SwiftTypeName = structDecl.SwiftTypeName,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(@namespace, csharpTypeIdentifier),
                SwiftTypeInfo = swiftTypeInfo,
                MetadataAccessor = structDecl.MetadataAccessor,
                Flags = flags,
                Kind = TypeRecordKind.Struct,
            };

            _moduleDatabase.RegisterType(structDecl.SwiftTypeName, typeRecord);
        }

        /// <summary>
        /// Processes an enum declaration and registers it in the type database.
        /// </summary>
        /// <param name="namedTypeSpec">Spec for the enum's name, module, etc.</param>
        /// <param name="enumDecl">The enum declaration node.</param>
        private void ProcessEnum(NamedTypeSpec namedTypeSpec, EnumDecl enumDecl)
        {
            // Get metadata pointer if the enum has a metadata accessor
            IntPtr metadataPtr = IntPtr.Zero;
            if (!string.IsNullOrEmpty(enumDecl.MetadataAccessor))
            {
                try
                {
                    metadataPtr = DynamicLibraryLoader.invoke(_dylibPath, enumDecl.MetadataAccessor);
                }
                catch
                {
                    // If metadata accessor fails, continue with zero pointer
                    _logger.LogWarning($"Failed to get metadata for enum '{enumDecl.Name}'. Continuing without metadata.");
                }
            }

            var swiftTypeInfo = new SwiftTypeInfo { MetadataPtr = metadataPtr };
            TypeRecordFlags flags = CalculateEnumFlags(enumDecl);

            RegisterEnumType(namedTypeSpec, enumDecl, swiftTypeInfo, flags);
        }

        /// <summary>
        /// Determines the flags for an enum type.
        /// </summary>
        /// <param name="enumDecl">The enum declaration.</param>
        /// <returns>The type record flags.</returns>
        private TypeRecordFlags CalculateEnumFlags(EnumDecl enumDecl)
        {
            TypeRecordFlags flags = TypeRecordFlags.None;

            if (enumDecl.IsFrozen)
                flags |= TypeRecordFlags.Frozen;

            // Enums with associated values may require memory management
            // depending on the types of the associated values
            if (enumDecl.HasAssociatedValueCases)
            {
                // For now, mark enums with associated values as requiring memory management
                // A more sophisticated check would examine the associated value types
                flags |= TypeRecordFlags.RequiresMemoryManagement;
            }

            if ((enumDecl.IsSimpleEnum || enumDecl.IsStringRawValueSimpleEnum) &&
                EnumHandler.CanSafelyEmitAsSimpleEnum(enumDecl))
                flags |= TypeRecordFlags.SimpleEnum;

            return flags;
        }

        /// <summary>
        /// Inserts an enum's details into the type database.
        /// </summary>
        /// <param name="namedTypeSpec">The Swift type specification, including module name.</param>
        /// <param name="enumDecl">The enum declaration node.</param>
        /// <param name="swiftTypeInfo">Pointer to the Swift metadata plus ValueWitnessTable.</param>
        /// <param name="flags">The type record flags.</param>
        private void RegisterEnumType(
            NamedTypeSpec namedTypeSpec,
            EnumDecl enumDecl,
            SwiftTypeInfo swiftTypeInfo,
            TypeRecordFlags flags)
        {
            var @namespace = _namespacePatternResolver.ResolveNamespace(namedTypeSpec.Module);
            var rawIdentifier = enumDecl.SwiftTypeName.Module == ""
                ? enumDecl.SwiftTypeName.Name
                : enumDecl.SwiftTypeName.ModuleQualifiedName.Substring(
                    enumDecl.SwiftTypeName.ModuleQualifiedName.IndexOf(".") + 1);
            var csharpTypeIdentifier = string.Join(".",
                rawIdentifier.Split('.').Select(NameProvider.ToPascalCaseForTypeName));

            var typeRecord = new TypeRecord
            {
                SwiftTypeName = enumDecl.SwiftTypeName,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(@namespace, csharpTypeIdentifier),
                SwiftTypeInfo = swiftTypeInfo,
                MetadataAccessor = enumDecl.MetadataAccessor,
                Flags = flags,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = enumDecl.RawValueTypeName,
            };

            _moduleDatabase.RegisterType(enumDecl.SwiftTypeName, typeRecord);
        }

        /// <summary>
        /// Inserts a class's details into the type database.
        /// </summary>
        /// <param name="namedTypeSpec">The Swift type specification, including module name.</param>
        /// <param name="classDecl">The class declaration node.</param>
        /// <param name="swiftTypeInfo">Pointer to the Swift metadata plus ValueWitnessTable.</param>
        private void RegisterClassType(
            NamedTypeSpec namedTypeSpec,
            ClassDecl classDecl,
            SwiftTypeInfo swiftTypeInfo)
        {
            TypeRecordFlags flags = TypeRecordFlags.RequiresMemoryManagement;
            if (classDecl.IsObjCRooted)
                flags |= TypeRecordFlags.ObjCRooted;
            var @namespace = _namespacePatternResolver.ResolveNamespace(namedTypeSpec.Module);
            var rawIdentifier = classDecl.SwiftTypeName.Module == ""
                ? classDecl.SwiftTypeName.Name
                : classDecl.SwiftTypeName.ModuleQualifiedName.Substring(
                    classDecl.SwiftTypeName.ModuleQualifiedName.IndexOf(".") + 1);
            var csharpTypeIdentifier = string.Join(".",
                rawIdentifier.Split('.').Select(NameProvider.ToPascalCaseForTypeName));
            var typeRecord = new TypeRecord
            {
                SwiftTypeName = classDecl.SwiftTypeName,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(@namespace, csharpTypeIdentifier),
                SwiftTypeInfo = swiftTypeInfo,
                MetadataAccessor = $"{classDecl.MangledName}Ma",
                Flags = flags,
                Kind = TypeRecordKind.Class,
                SuperclassTypeName = classDecl.DirectSuperclassName != null
                    && !classDecl.DirectSuperclassName.Contains('<')
                    ? SwiftTypeName.FromModuleQualifiedName(classDecl.DirectSuperclassName)
                    : null,
            };

            _moduleDatabase.RegisterType(classDecl.SwiftTypeName, typeRecord);
        }

        /// <summary>
        /// Processes a class declaration. Currently unimplemented.
        /// </summary>
        /// <param name="namedTypeSpec">Spec for the class's name, module, etc.</param>
        /// <param name="classDecl">The class declaration node.</param>
        private void ProcessClass(NamedTypeSpec namedTypeSpec, ClassDecl classDecl)
        {
            // Get metadata pointer if possible (may fail for cross-platform builds, e.g., iOS on macOS)
            IntPtr metadataPtr = IntPtr.Zero;
            try
            {
                metadataPtr = DynamicLibraryLoader.invoke(_dylibPath, $"{classDecl.MangledName}Ma");
            }
            catch
            {
                // If metadata accessor fails (e.g., iOS dylib on macOS), continue without metadata
                _logger.LogWarning($"Failed to get metadata for class '{classDecl.Name}'. Continuing without metadata.");
            }
            var swiftTypeInfo = new SwiftTypeInfo { MetadataPtr = metadataPtr };

            RegisterClassType(namedTypeSpec, classDecl, swiftTypeInfo);
        }

        /// <summary>
        /// Resolves superclass references for all classes within the current module.
        /// For each ClassDecl with a DirectSuperclassName, looks up the matching ClassDecl
        /// in the module's type declarations. Same-module matches are resolved;
        /// cross-module and ObjC base classes are left unresolved (HasExternalSuperclass = true).
        /// </summary>
        /// <summary>
        /// Post-scan pass: demotes simple enums that are used as bound generic type arguments.
        /// C# enums cannot implement interfaces (ISwiftObject), so they fail generic constraints
        /// like <c>where T : ISwiftObject</c> that are automatically added to all generic type parameters.
        /// Such enums must fall back to the class-based representation.
        /// </summary>
        private void DemoteSimpleEnumsUsedAsGenericArgs()
        {
            // Collect all type specs used as concrete generic type arguments across the module
            var enumsUsedAsGenericArgs = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (_, typeDecl) in _typeDecls)
            {
                CollectBoundGenericEnumArgs(typeDecl, enumsUsedAsGenericArgs);
            }

            if (enumsUsedAsGenericArgs.Count == 0)
                return;

            // Demote any simple enum whose module-qualified name appears as a generic type argument
            foreach (var (typeSpec, typeDecl) in _typeDecls)
            {
                if (typeDecl is not EnumDecl)
                    continue;

                var swiftTypeName = SwiftTypeName.FromTypeSpec(typeSpec);
                if (!enumsUsedAsGenericArgs.Contains(swiftTypeName.ModuleQualifiedName))
                    continue;

                if (!_moduleDatabase.TryGetTypeRecord(swiftTypeName, out var record))
                    continue;

                if (!record.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
                    continue;

                var demotedRecord = record with { Flags = record.Flags & ~TypeRecordFlags.SimpleEnum };
                _moduleDatabase.RegisterType(swiftTypeName, demotedRecord);
                _logger.LogInformation($"Demoted simple enum '{swiftTypeName}' to class-based: used as generic type argument with ISwiftObject constraint.");
            }
        }

        /// <summary>
        /// Recursively collects module-qualified names of types used as concrete generic type arguments
        /// in the given type declaration's properties, methods, and subscripts.
        /// </summary>
        private void CollectBoundGenericEnumArgs(TypeDecl typeDecl, HashSet<string> result)
        {
            // Properties
            foreach (var prop in typeDecl.Properties)
            {
                CollectGenericArgsFromTypeSpec(prop.SwiftTypeSpec, result);
            }

            // Methods (return types + parameters)
            foreach (var method in typeDecl.Methods)
            {
                foreach (var arg in method.CSSignature)
                {
                    CollectGenericArgsFromTypeSpec(arg.SwiftTypeSpec, result);
                }
            }

            // Subscripts
            foreach (var subscript in typeDecl.Subscripts)
            {
                CollectGenericArgsFromTypeSpec(subscript.ReturnTypeSpec, result);
                foreach (var idx in subscript.IndexParameters)
                {
                    CollectGenericArgsFromTypeSpec(idx.SwiftTypeSpec, result);
                }
            }

            // Nested types
            foreach (var nested in typeDecl.Types)
            {
                CollectBoundGenericEnumArgs(nested, result);
            }
        }

        /// <summary>
        /// Recursively extracts concrete type names used as generic type arguments from a TypeSpec.
        /// Only collects from NamedTypeSpec nodes that have generic parameters (bound generics).
        /// </summary>
        private static void CollectGenericArgsFromTypeSpec(TypeSpec? typeSpec, HashSet<string> result)
        {
            if (typeSpec == null)
                return;

            if (typeSpec is NamedTypeSpec named)
            {
                if (named.ContainsGenericParameters)
                {
                    // This is a bound generic — collect concrete type arguments
                    foreach (var genericParam in named.GenericParameters)
                    {
                        if (genericParam is NamedTypeSpec argNamed &&
                            argNamed.HasModule() &&
                            !argNamed.ContainsGenericParameters)
                        {
                            // Concrete type argument (not itself generic)
                            result.Add(argNamed.Name); // Name includes module prefix
                        }

                        // Recurse into nested generics (e.g., Array<ScanningResult<T, MyEnum>>)
                        CollectGenericArgsFromTypeSpec(genericParam, result);
                    }
                }
                else
                {
                    // Not a bound generic at this level, but recurse into generic params if any
                    foreach (var gp in named.GenericParameters)
                        CollectGenericArgsFromTypeSpec(gp, result);
                }
            }
            else if (typeSpec is TupleTypeSpec tuple)
            {
                foreach (var elem in tuple.Elements)
                    CollectGenericArgsFromTypeSpec(elem, result);
            }
            else if (typeSpec is ClosureTypeSpec closure)
            {
                CollectGenericArgsFromTypeSpec(closure.Arguments, result);
                CollectGenericArgsFromTypeSpec(closure.ReturnType, result);
            }
        }

        private void ResolveClassHierarchy()
        {
            // Build a lookup from module-qualified name to ClassDecl for efficient resolution.
            var classesByName = new Dictionary<string, ClassDecl>(StringComparer.Ordinal);
            foreach (var (_, typeDecl) in _typeDecls)
            {
                if (typeDecl is ClassDecl classDecl)
                {
                    classesByName[classDecl.SwiftTypeName.ModuleQualifiedName] = classDecl;
                }
            }

            // Resolve each class's direct superclass
            foreach (var (_, classDecl) in classesByName)
            {
                var superName = classDecl.DirectSuperclassName;
                if (superName == null)
                    continue; // Root class

                if (classesByName.TryGetValue(superName, out var superclassDecl))
                {
                    classDecl.ResolvedSuperclass = superclassDecl;
                }
                // else: cross-module or ObjC base — leave null (HasExternalSuperclass will be true)
            }

            // Compute IsObjCRooted via fixed-point loop.
            // A class is ObjC-rooted if it directly inherits an ObjC class (HasObjCSuperclass),
            // or if its resolved superclass is ObjC-rooted, or if the TypeDatabase has its
            // parent TypeRecord marked ObjCRooted (cross-module case).
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var (_, classDecl) in classesByName)
                {
                    if (classDecl.IsObjCRooted)
                        continue;

                    if (classDecl.HasObjCSuperclass)
                    {
                        classDecl.IsObjCRooted = true;
                        changed = true;
                        continue;
                    }

                    if (classDecl.ResolvedSuperclass?.IsObjCRooted == true)
                    {
                        classDecl.IsObjCRooted = true;
                        changed = true;
                        continue;
                    }

                    // Cross-module: check parent TypeRecord in the global database.
                    // Skip generic superclass names (contain '<') — SwiftTypeName rejects them.
                    if (classDecl.DirectSuperclassName != null &&
                        !classDecl.DirectSuperclassName.Contains('<'))
                    {
                        var parentSwiftName = SwiftTypeName.FromModuleQualifiedName(classDecl.DirectSuperclassName);
                        if (_typeDatabase.TryGetTypeRecord(parentSwiftName, out var parentRecord) &&
                            MarshallingHelpers.IsObjCRooted(parentRecord))
                        {
                            classDecl.IsObjCRooted = true;
                            changed = true;
                        }
                    }
                }
            }

            // Validate: detect cycles (should be impossible in valid ABI, but guard).
            // Collect all cycle participants and clear their ResolvedSuperclass to avoid
            // leaving partially-resolved inconsistent state (e.g., A→B→A where only A is cleared).
            var cycleParticipants = new HashSet<ClassDecl>(ReferenceEqualityComparer.Instance);
            foreach (var (_, classDecl) in classesByName)
            {
                CollectCycleParticipants(classDecl, cycleParticipants);
            }
            foreach (var participant in cycleParticipants)
            {
                _logger.LogWarning(
                    "Cyclic class hierarchy detected for '{ClassName}'. Clearing resolved superclass.",
                    participant.SwiftTypeName.ModuleQualifiedName);
                participant.ResolvedSuperclass = null;
            }
        }

        /// <summary>
        /// If the class participates in a cycle, adds all cycle members to the set.
        /// Uses Floyd's tortoise-and-hare algorithm, then walks the cycle to collect all participants.
        /// </summary>
        private static void CollectCycleParticipants(ClassDecl classDecl, HashSet<ClassDecl> participants)
        {
            var slow = classDecl.ResolvedSuperclass;
            var fast = classDecl.ResolvedSuperclass?.ResolvedSuperclass;
            while (fast != null)
            {
                if (ReferenceEquals(slow, fast))
                {
                    // Found a cycle — collect all members in the cycle
                    var current = slow;
                    do
                    {
                        participants.Add(current!);
                        current = current!.ResolvedSuperclass;
                    } while (!ReferenceEquals(current, slow));
                    return;
                }
                slow = slow?.ResolvedSuperclass;
                fast = fast.ResolvedSuperclass?.ResolvedSuperclass;
            }
        }

        /// <summary>
        /// Processes a protocol declaration and registers it in the type database.
        /// </summary>
        /// <param name="namedTypeSpec">Spec for the protocol's name, module, etc.</param>
        /// <param name="protocolDecl">The protocol declaration node.</param>
        /// <returns><c>true</c> if the protocol was processed successfully; otherwise, <c>false</c>.</returns>
        private bool ProcessProtocol(NamedTypeSpec namedTypeSpec, ProtocolDecl protocolDecl)
        {
            RegisterProtocolType(namedTypeSpec, protocolDecl);
            return true;
        }

        /// <summary>
        /// Inserts a protocol's details into the type database.
        /// </summary>
        /// <param name="namedTypeSpec">The Swift type specification, including module name.</param>
        /// <param name="protocolDecl">The protocol declaration node.</param>
        private void RegisterProtocolType(NamedTypeSpec namedTypeSpec, ProtocolDecl protocolDecl)
        {
            var @namespace = _namespacePatternResolver.ResolveNamespace(namedTypeSpec.Module);

            // Protocol types are projected as interfaces in C#
            // Use "I" prefix for interface naming convention
            var csharpTypeIdentifier = NameProvider.GetInterfaceName(protocolDecl.Name, moduleName: namedTypeSpec.Module);

            // Protocols with associated types or Self requirements generate generic C# interfaces
            // Mark them so we can skip them in generic constraints (can't use generic interfaces without type arguments)
            var flags = protocolDecl.AssociatedTypes.Count > 0
                ? TypeRecordFlags.HasAssociatedTypes
                : TypeRecordFlags.None;

            if (protocolDecl.HasSelfRequirement)
                flags |= TypeRecordFlags.HasSelfRequirement;

            // Protocols with no own instance members but inherited protocol requirements
            // won't get proxy classes emitted (ProtocolProxyEmitter skips them to avoid CS0535).
            // Mark them so existential return gates can reject them.
            var hasOwnInstanceMembers = protocolDecl.Properties.Any(p => !p.IsStatic) ||
                                         protocolDecl.Methods.Any(m => !m.IsConstructor && m.MethodType != MethodType.Static) ||
                                         protocolDecl.Subscripts.Any(s => !s.IsStatic);
            if (!hasOwnInstanceMembers)
            {
                var hasInheritedRequirements = protocolDecl.InheritedProtocols.Any(inherited =>
                    inherited.NameWithoutModule != "AnyObject");
                if (hasInheritedRequirements)
                    flags |= TypeRecordFlags.InheritedRequirementsOnly;
            }

            if (protocolDecl.IsClassBound)
                flags |= TypeRecordFlags.ClassBound;

            var typeRecord = new TypeRecord
            {
                SwiftTypeName = protocolDecl.SwiftTypeName,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(@namespace, csharpTypeIdentifier),
                MetadataAccessor = string.Empty, // Protocols don't have direct metadata accessors
                Flags = flags,
                Kind = TypeRecordKind.Protocol,
            };

            _moduleDatabase.RegisterType(protocolDecl.SwiftTypeName, typeRecord);
        }

    }
}
