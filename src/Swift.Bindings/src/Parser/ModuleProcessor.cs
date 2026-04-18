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
            UpdateObjCRootedTypeRecords();
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

            // Skip @_spi types — they are not part of the public API surface.
            // Not registering them ensures members referencing them fail type resolution
            // and are naturally skipped by the emitter.
            if (!structDecl.IsSpiProtected)
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

            // Non-copyable (~Copyable) types list Escapable WITHOUT Copyable.
            // In Swift 6.2+, normal types explicitly list BOTH Copyable and Escapable.
            // In pre-6.2, normal types list neither (both implicit).
            if (structDecl.Conformances.Any(c => c.Protocol.ToString() == "Swift.Escapable") &&
                !structDecl.Conformances.Any(c => c.Protocol.ToString() == "Swift.Copyable"))
                flags |= TypeRecordFlags.NonCopyable;

            if (!structDecl.IsFrozen)
                return flags;

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

                // Detect float/double fields for CallConvSwift ABI safety classification.
                // Direct float/double primitives (Swift.Float, Swift.Double, CoreFoundation.CGFloat)
                // and nested non-system structs that themselves contain float fields.
                // System structs (CGRect, etc.) are NOT flagged — they have special runtime handling.
                if (!flags.HasFlag(TypeRecordFlags.HasFloatFields))
                {
                    if (namedPropertyType.Name is "Swift.Float" or "Swift.Double" or "CoreFoundation.CGFloat" or "CoreGraphics.CGFloat")
                        flags |= TypeRecordFlags.HasFloatFields;
                    else if (propertyRecord.Kind == TypeRecordKind.Struct &&
                             propertyRecord.Flags.HasFlag(TypeRecordFlags.HasFloatFields))
                        flags |= TypeRecordFlags.HasFloatFields;
                }

                // Detect Bool fields — non-blittable in .NET CallConvSwift.
                if (!flags.HasFlag(TypeRecordFlags.HasBoolFields))
                {
                    if (namedPropertyType.Name is "Swift.Bool")
                        flags |= TypeRecordFlags.HasBoolFields;
                    else if (propertyRecord.Kind == TypeRecordKind.Struct &&
                             propertyRecord.Flags.HasFlag(TypeRecordFlags.HasBoolFields))
                        flags |= TypeRecordFlags.HasBoolFields;
                }
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
            // Compute InlineSize from SwiftTypeInfo if metadata is available.
            // This is used by FrozenStructHandler to emit correctly-sized Buffer fields.
            int? inlineSize = null;
            if (swiftTypeInfo.MetadataPtr != IntPtr.Zero)
            {
                unsafe
                {
                    inlineSize = (int)swiftTypeInfo.ValueWitnessTable->Size;
                }
            }

            // Compute ABI field layout for frozen structs (used by ARM64 thunk register decomposition).
            // Each stored instance property is classified as: i (integer), f (float), b (bool), p (pointer).
            // Nested frozen structs are recursively flattened. Layout is null if any field can't be classified.
            string? abiFieldLayout = null;
            if ((flags & TypeRecordFlags.Frozen) != 0)
            {
                abiFieldLayout = ComputeAbiFieldLayout(structDecl);
            }

            var typeRecord = new TypeRecord
            {
                SwiftTypeName = structDecl.SwiftTypeName,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(@namespace, csharpTypeIdentifier),
                SwiftTypeInfo = swiftTypeInfo,
                MetadataAccessor = structDecl.MetadataAccessor,
                Flags = flags,
                Kind = TypeRecordKind.Struct,
                InlineSize = inlineSize,
                AbiFieldLayout = abiFieldLayout,
            };

            _moduleDatabase.RegisterType(structDecl.SwiftTypeName, typeRecord);
        }

        /// <summary>
        /// Computes the ABI field layout string for a frozen struct by classifying each stored
        /// instance property as integer (i), float (f), bool (b), or pointer (p).
        /// Nested frozen structs are recursively flattened.
        /// Returns null if any field cannot be classified (e.g., generic, existential, non-frozen nested struct).
        /// </summary>
        private string? ComputeAbiFieldLayout(StructDecl structDecl)
        {
            var fields = new List<string>();

            foreach (var propertyDecl in structDecl.Properties)
            {
                // Skip computed/static properties — only stored instance properties affect ABI layout
                if (propertyDecl.IsStatic || !propertyDecl.HasStorage)
                    continue;

                if (propertyDecl.SwiftTypeSpec is not NamedTypeSpec namedType)
                {
                    _logger.LogDebug("ABI layout: field '{Field}' in '{Type}' is not a NamedTypeSpec — falling back to @_cdecl.",
                        propertyDecl.Name, structDecl.Name);
                    return null; // Can't classify (e.g., tuple, closure, protocol composition)
                }

                var fieldLayout = ClassifyFieldType(namedType);
                if (fieldLayout == null)
                {
                    _logger.LogDebug("ABI layout: field '{Field}' (type {FieldType}) in '{Type}' cannot be classified — falling back to @_cdecl.",
                        propertyDecl.Name, namedType.Name, structDecl.Name);
                    return null; // Can't classify — layout unknown
                }

                fields.Add(fieldLayout);
            }

            return fields.Count > 0 ? string.Join(",", fields) : null;
        }

        /// <summary>
        /// Classifies a single field type for ABI layout purposes.
        /// Returns a layout fragment: "i" for integer, "f" for float, "b" for bool, "p" for pointer,
        /// or a comma-separated list for nested frozen structs (e.g., "i,f" for a struct with Int and Double).
        /// Returns null if the type cannot be classified.
        ///
        /// NOTE: Sub-8-byte integer types (Int8, Int16, Int32) are classified as "i" (8-byte integer slot)
        /// because the layout string represents register FILE classification, not exact byte widths.
        /// Each leaf scalar field occupies one full ARM64 register in swiftcc. The exact byte sizes
        /// needed for thunk store instructions are resolved at thunk emission time from
        /// the original TypeSpec, not from this layout string.
        /// </summary>
        private string? ClassifyFieldType(NamedTypeSpec namedType)
        {
            // Primitive scalar types
            switch (namedType.Name)
            {
                case "Swift.Int":
                case "Swift.UInt":
                case "Swift.Int8":
                case "Swift.UInt8":
                case "Swift.Int16":
                case "Swift.UInt16":
                case "Swift.Int32":
                case "Swift.UInt32":
                case "Swift.Int64":
                case "Swift.UInt64":
                    return "i";

                case "Swift.Float":
                case "Swift.Double":
                case "CoreFoundation.CGFloat":
                case "CoreGraphics.CGFloat":
                    return "f";

                case "Swift.Bool":
                    return "b";

                case "Swift.OpaquePointer":
                case "Swift.UnsafeRawPointer":
                case "Swift.UnsafeMutableRawPointer":
                    return "p";
            }

            // Generic types can't be classified without specialization
            if (namedType.ContainsGenericParameters)
            {
                // Special case: Optional wraps a classifiable inner type
                if (namedType.Name == "Swift.Optional" && namedType.GenericParameters.Count == 1)
                {
                    // Optional<class> is a single nullable pointer — no tag
                    if (namedType.GenericParameters[0] is NamedTypeSpec innerType)
                    {
                        if (TryGetTypeRecord(innerType, out var innerRecord) && innerRecord.Kind == TypeRecordKind.Class)
                            return "p";

                        // Optional<value type> = inner layout + tag byte (integer slot)
                        var innerLayout = ClassifyFieldType(innerType);
                        if (innerLayout != null)
                            return $"{innerLayout},i";
                    }
                    return null;
                }

                // UnsafePointer<T>, UnsafeMutablePointer<T> — pointer regardless of T
                if (namedType.Name is "Swift.UnsafePointer" or "Swift.UnsafeMutablePointer")
                    return "p";

                return null;
            }

            // Existential types can't be classified
            if (namedType.IsAny)
                return null;

            // Look up in type database for struct/class/enum
            if (!TryGetTypeRecord(namedType, out var record))
                return null;

            switch (record.Kind)
            {
                case TypeRecordKind.Class:
                    return "p"; // Class reference is always a pointer

                case TypeRecordKind.Enum:
                    if (record.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
                        return "i"; // Simple enum with raw integer value
                    return null; // Complex enum — can't classify without deeper analysis

                case TypeRecordKind.Struct:
                    // Non-frozen struct — layout unknown at compile time
                    if (!record.Flags.HasFlag(TypeRecordFlags.Frozen))
                        return null;

                    // If the nested struct already has a computed ABI layout, use it
                    if (!string.IsNullOrEmpty(record.AbiFieldLayout))
                        return record.AbiFieldLayout;

                    // Frozen struct without layout — e.g., cross-module dependency emitted by an older
                    // generator that predates abiLayout persistence. This causes the entire parent struct's
                    // layout to become null, routing the function to @_cdecl instead of native thunk.
                    return null;

                default:
                    return null;
            }
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

            if (!enumDecl.IsSpiProtected)
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

            // Single-case no-payload enums have TypeMetadata.Size == 0 and cannot be
            // emitted as ISwiftObject (SafeHandle allocations break). They're also not
            // simple/raw-value enums, so EnumHandler skips them entirely. Mark the record
            // so member-level validators drop references to them.
            bool emitsAsSimple =
                (enumDecl.IsSimpleEnum && EnumHandler.CanSafelyEmitAsSimpleEnum(enumDecl)) ||
                (enumDecl.IsStringRawValueSimpleEnum && EnumHandler.CanSafelyEmitAsSimpleEnum(enumDecl));
            if (!emitsAsSimple &&
                !enumDecl.IsNamespaceEnum &&
                enumDecl.Cases.Count == 1 &&
                !enumDecl.HasAssociatedValueCases)
            {
                flags |= TypeRecordFlags.Unemittable;
            }

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

            // Compute InlineSize from SwiftTypeInfo if metadata is available.
            int? inlineSize = null;
            if (swiftTypeInfo.MetadataPtr != IntPtr.Zero)
            {
                unsafe
                {
                    inlineSize = (int)swiftTypeInfo.ValueWitnessTable->Size;
                }
            }

            var typeRecord = new TypeRecord
            {
                SwiftTypeName = enumDecl.SwiftTypeName,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(@namespace, csharpTypeIdentifier),
                SwiftTypeInfo = swiftTypeInfo,
                MetadataAccessor = enumDecl.MetadataAccessor,
                Flags = flags,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = enumDecl.RawValueTypeName,
                InlineSize = inlineSize,
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

            if (!classDecl.IsSpiProtected)
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
        /// Well-known stdlib container types that are projected to constraint-free C# types.
        /// Enums used as type arguments of these containers don't need demotion because the
        /// C# projection (IReadOnlyList, T?, IReadOnlyDictionary, IReadOnlySet) has no ISwiftObject constraint.
        /// </summary>
        private static readonly HashSet<string> ConstraintFreeContainers = new(StringComparer.Ordinal)
        {
            "Swift.Array", "Swift.Optional", "Swift.Dictionary", "Swift.Set",
        };

        /// <summary>
        /// Checks whether a generic parameter at a given position in a type declaration
        /// has protocol conformance constraints that would require ISwiftObject implementation.
        /// Returns true if the parameter has conformances (meaning enums at that position need demotion).
        /// Returns true (conservative) if the type declaration is not found.
        /// </summary>
        private bool HasProtocolConstraintAtPosition(string parentTypeName, int paramIndex)
        {
            // Look up the parent type in the module's type declarations
            foreach (var (typeSpec, typeDecl) in _typeDecls)
            {
                var swiftTypeName = SwiftTypeName.FromTypeSpec(typeSpec);
                if (swiftTypeName.ModuleQualifiedName == parentTypeName)
                {
                    if (paramIndex < typeDecl.GenericParameters.Count)
                    {
                        var genericParam = typeDecl.GenericParameters[paramIndex];
                        // If the parameter has any protocol conformances, the enum needs
                        // ISwiftObject (which maps to C# interface constraints)
                        return genericParam.GenericConformances.Count > 0;
                    }
                    // Parameter index out of range — conservative demotion
                    return true;
                }
            }
            // Type not found in module — conservative demotion for unknown external types
            return true;
        }

        /// <summary>
        /// Recursively extracts concrete type names used as generic type arguments from a TypeSpec.
        /// Only collects from NamedTypeSpec nodes that have generic parameters (bound generics).
        /// Skips well-known stdlib containers (Array, Optional, etc.) that are projected to
        /// constraint-free C# types, and checks generic parameter constraints for user-defined types.
        /// </summary>
        private void CollectGenericArgsFromTypeSpec(TypeSpec? typeSpec, HashSet<string> result)
        {
            if (typeSpec == null)
                return;

            if (typeSpec is NamedTypeSpec named)
            {
                if (named.ContainsGenericParameters)
                {
                    // Skip well-known stdlib containers — their C# projections have no ISwiftObject constraint
                    bool isConstraintFree = ConstraintFreeContainers.Contains(named.Name);

                    // This is a bound generic — collect concrete type arguments
                    for (int i = 0; i < named.GenericParameters.Count; i++)
                    {
                        var genericParam = named.GenericParameters[i];
                        if (genericParam is NamedTypeSpec argNamed &&
                            argNamed.HasModule() &&
                            !argNamed.ContainsGenericParameters)
                        {
                            // Only collect if the parent type's constraint at this position
                            // requires protocol conformance (ISwiftObject or interface)
                            if (!isConstraintFree && HasProtocolConstraintAtPosition(named.Name, i))
                            {
                                result.Add(argNamed.Name); // Name includes module prefix
                            }
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
        /// After ResolveClassHierarchy sets IsObjCRooted on ClassDecls, update the
        /// already-registered TypeRecords to include the ObjCRooted flag.
        /// RegisterClassType runs before ResolveClassHierarchy, so the flag is stale.
        /// </summary>
        private void UpdateObjCRootedTypeRecords()
        {
            foreach (var (_, typeDecl) in _typeDecls)
            {
                if (typeDecl is ClassDecl classDecl && classDecl.IsObjCRooted)
                {
                    if (_moduleDatabase.TryGetTypeRecord(classDecl.SwiftTypeName, out var record) &&
                        !record.Flags.HasFlag(TypeRecordFlags.ObjCRooted))
                    {
                        record.Flags |= TypeRecordFlags.ObjCRooted;
                    }
                }
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

            // Detect Self (τ_0_0) usage in method parameter/return types.
            // When methods use τ_0_0, the interface emits AnyType for Self positions,
            // making the constraint unsatisfiable by concrete types.
            if (!protocolDecl.HasSelfRequirement && HasMethodSelfTypeParams(protocolDecl))
                flags |= TypeRecordFlags.HasMethodSelfTypeParams;

            // Protocols with no own instance members but inherited protocol requirements
            // won't get proxy classes emitted (ProtocolProxyEmitter skips them to avoid CS0535).
            // Mark them so existential return gates can reject them.
            var hasOwnInstanceMembers = protocolDecl.Properties.Any(p => !p.IsStatic) ||
                                         protocolDecl.Methods.Any(m => !m.IsConstructor && m.MethodType != MethodType.Static) ||
                                         protocolDecl.Subscripts.Any(s => !s.IsStatic);
            // InheritedRequirementsOnly is no longer set: the proxy emitter and C# interface
            // emitter now handle inherited protocol requirements (inherited method emission
            // + C# interface inheritance). Protocols with only inherited members get valid
            // proxies that implement the inherited interface members.

            if (protocolDecl.IsClassBound)
                flags |= TypeRecordFlags.ClassBound;

            // Check if this protocol inherits from Codable (Decodable/Encodable), either
            // directly or transitively through inherited protocols already in the type database.
            // Dependencies are processed before the main module, so cross-module inherited
            // protocols will already have their InheritsCodable flag set.
            if (ProtocolInheritsCodable(protocolDecl))
                flags |= TypeRecordFlags.InheritsCodable;

            var typeRecord = new TypeRecord
            {
                SwiftTypeName = protocolDecl.SwiftTypeName,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(@namespace, csharpTypeIdentifier),
                MetadataAccessor = string.Empty, // Protocols don't have direct metadata accessors
                Flags = flags,
                Kind = TypeRecordKind.Protocol,
                ProtocolDescriptorSymbol = ConvertProtocolTypeToDescriptorSymbol(protocolDecl.MangledName),
            };

            if (!protocolDecl.IsSpiProtected)
                _moduleDatabase.RegisterType(protocolDecl.SwiftTypeName, typeRecord);
        }

        /// <summary>
        /// Converts a protocol type symbol (ABI JSON form, ending in 'P') to its
        /// protocol descriptor symbol (ending in 'Mp'). Strips exactly one terminal
        /// 'P' to avoid corrupting names that happen to contain multiple consecutive
        /// Ps (which TrimEnd('P') would mishandle).
        /// Verified via swift-demangle:
        ///   $s20SwiftBindingsTestLib8SummableP  (type)       -> $s20SwiftBindingsTestLib8SummableMp (descriptor)
        ///   $sSH                                (Hashable)   -> $sSHMp
        /// </summary>
        private static string? ConvertProtocolTypeToDescriptorSymbol(string? mangled)
        {
            if (string.IsNullOrEmpty(mangled))
                return null;
            return mangled.EndsWith('P') ? mangled[..^1] + "Mp" : mangled + "Mp";
        }

        /// <summary>
        /// Checks if a protocol inherits from Codable (Decodable/Encodable), either directly
        /// by name, transitively through intra-module ProtocolDecls, or via cross-module
        /// TypeRecord flags.
        /// </summary>
        private bool ProtocolInheritsCodable(ProtocolDecl protocolDecl)
        {
            return ProtocolInheritsCodableRecursive(protocolDecl, new HashSet<string>(StringComparer.Ordinal));
        }

        private bool ProtocolInheritsCodableRecursive(ProtocolDecl protocolDecl, HashSet<string> visited)
        {
            var qualifiedName = protocolDecl.SwiftTypeName?.ToString() ?? protocolDecl.Name;
            if (!visited.Add(qualifiedName))
                return false;

            foreach (var inherited in protocolDecl.InheritedProtocols)
            {
                var name = inherited.Name;
                var dotIndex = name.LastIndexOf('.');
                var simpleName = dotIndex >= 0 ? name.Substring(dotIndex + 1) : name;

                // Direct Codable-family name match
                if (simpleName is "Decodable" or "Encodable" or "Codable")
                    return true;

                // Intra-module transitive check: look up the inherited protocol in
                // _typeDecls (fully populated before registration starts) so declaration
                // order doesn't matter.
                var intraModuleDecl = _typeDecls.Values
                    .OfType<ProtocolDecl>()
                    .FirstOrDefault(p =>
                        p.Name == simpleName || p.Name == name ||
                        p.SwiftTypeName?.ToString() == name);
                if (intraModuleDecl != null)
                {
                    if (ProtocolInheritsCodableRecursive(intraModuleDecl, visited))
                        return true;
                    continue; // Found in-module — no need for cross-module fallback
                }

                // Cross-module transitive check: look up inherited protocol's TypeRecord
                // in the type database. Dependencies are processed first, so their flags
                // are already set by the time the main module is processed.
                var inheritedSwiftName = SwiftTypeName.FromModuleQualifiedName(name);
                if (_typeDatabase.TryGetTypeRecord(inheritedSwiftName, out var record) &&
                    record.Kind == TypeRecordKind.Protocol &&
                    record.Flags.HasFlag(TypeRecordFlags.InheritsCodable))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Checks if any of the protocol's methods use τ_0_0 (Self) in parameter or return types.
        /// </summary>
        private static bool HasMethodSelfTypeParams(ProtocolDecl protocolDecl)
        {
            foreach (var method in protocolDecl.Methods)
            {
                if (method.IsConstructor || method.MethodType == MethodType.Static)
                    continue;
                foreach (var arg in method.CSSignature)
                {
                    if (TypeSpecContainsSelfParam(arg.SwiftTypeSpec))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Recursively checks if a TypeSpec contains a reference to τ_0_0 (Self type param).
        /// </summary>
        private static bool TypeSpecContainsSelfParam(TypeSpec typeSpec)
        {
            if (typeSpec is NamedTypeSpec named)
            {
                if (named.Name == "τ_0_0")
                    return true;
                foreach (var gp in named.GenericParameters)
                {
                    if (TypeSpecContainsSelfParam(gp))
                        return true;
                }
            }
            else if (typeSpec is TupleTypeSpec tuple)
            {
                foreach (var elem in tuple.Elements)
                {
                    if (TypeSpecContainsSelfParam(elem))
                        return true;
                }
            }
            else if (typeSpec is ClosureTypeSpec closure)
            {
                if (TypeSpecContainsSelfParam(closure.ReturnType))
                    return true;
                foreach (var arg in closure.EachArgument())
                {
                    if (TypeSpecContainsSelfParam(arg))
                        return true;
                }
            }
            return false;
        }

    }
}
