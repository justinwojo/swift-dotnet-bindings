// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using Microsoft.Extensions.Logging;
using Swift.Runtime;

namespace BindingsGeneration
{
    /// <summary>
    /// Factory class for creating instances of ClassHandler.
    /// </summary>
    public class ClassHandlerFactory : HandlerFactory, IFactory<BaseDecl, ITypeHandler>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ClassHandlerFactory"/> class.
        /// </summary>
        /// <param name="loggerFactory">The logger factory instance.</param>
        public ClassHandlerFactory(ILoggerFactory loggerFactory) : base(loggerFactory.CreateLogger<ClassHandler>())
        {
        }

        /// <summary>
        /// Determines if the factory handles the specified declaration.
        /// </summary>
        /// <param name="decl">The base declaration.</param>
        public bool Handles(BaseDecl decl)
        {
            return decl is ClassDecl;
        }

        /// <summary>
        /// Constructs a new instance of ClassHandler.
        /// </summary>
        public ITypeHandler Construct()
        {
            return new ClassHandler(_handlerLogger);
        }
    }

    /// <summary>
    /// Handler class for class declarations.
    /// </summary>
    public class ClassHandler : BaseHandler, ITypeHandler
    {
        public ClassHandler(ILogger logger) : base(logger)
        {
        }

        /// <inheritdoc/>
        public IEnvironment Marshal(BaseDecl baseDecl, ITypeDatabase typeDatabase)
        {
            if (baseDecl is not ClassDecl classDecl)
            {
                throw new ArgumentException("The provided decl must be a ClassDecl.", nameof(baseDecl));
            }
            return new TypeEnvironment(classDecl, typeDatabase);
        }

        /// <inheritdoc/>
        public void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor, TypeHandlerContext context)
        {
            var classEnv = (TypeEnvironment)env;
            var classDecl = (ClassDecl)classEnv.TypeDecl;
            var moduleDecl = classDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(classDecl.ModuleDecl));

            if (GenericTypeEmitter.TryGetUnsupportedConstraint(classDecl, out var unsupportedConstraint))
            {
                var reason = unsupportedConstraint.Module == "SwiftUI"
                    ? SkipReason.SwiftUIConstraint
                    : unsupportedConstraint.Module == "Combine"
                        ? SkipReason.CombineFramework
                        : SkipReason.UnsupportedType;
                ReportCollector.RecordTypeSkipped(classDecl, reason, $"Unsupported generic constraint: {unsupportedConstraint.ModuleQualifiedName}");
                _logger.LogWarning(
                    "Skipping type '{TypeName}' - generic constraint references unsupported protocol '{Protocol}' from module '{Module}'.",
                    classDecl.Name,
                    unsupportedConstraint.Name,
                    unsupportedConstraint.Module);
                return;
            }

            ReportCollector.RecordTypeEmitted(classDecl);

            // Cross-module extension: type defined in module A, extended in module B.
            // Emit as a static extension class instead of a duplicate partial class.
            if (!string.IsNullOrEmpty(classDecl.SwiftTypeName.Module) &&
                classDecl.SwiftTypeName.Module != moduleDecl.Name)
            {
                CrossModuleExtensionEmitter.Emit(csWriter, swiftWriter, classDecl, moduleDecl, conductor, env, _logger);
                return;
            }

            // Get generic type parts if this is a generic type
            var typeNameWithGenerics = GenericTypeEmitter.GetTypeNameWithGenerics(classDecl, env.TypeDatabase);
            var whereClause = GenericTypeEmitter.GetWhereClause(classDecl, env.TypeDatabase);

            // Create P/Invoke helper context for generic types (to avoid CS7042)
            var ownPInvokeContext = PInvokeHelperContext.CreateIfGeneric(classDecl);
            var pinvokeHelperContext = ownPInvokeContext ?? context.PInvokeHelperContext;

            // Compute property renames to resolve property/nested-type name collisions
            var propertyRenames = NameProvider.ComputePropertyRenames(classDecl, env.TypeDatabase);

            // Build child context for nested handlers
            var childContext = context with {
                PInvokeHelperContext = pinvokeHelperContext,
                PropertyRenames = propertyRenames
            };

            {
                var extensionDefaultsIndex = context.GetEmissionContext()?.ExtensionDefaultsIndex;
                var conformanceValidator = new ProtocolConformanceValidator(moduleDecl, env.TypeDatabase, extensionDefaultsIndex);
                var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
                    classDecl,
                    typeNameWithGenerics,
                    moduleDecl.Name,
                    env.TypeDatabase,
                    conformanceValidator);

                // A class is derived only if its resolved base class will actually be emitted.
                // If the base class would be skipped (e.g., unsupported generic constraints),
                // fall back to flat emission to avoid referencing a non-emitted base type.
                bool isDerived = IsEffectivelyDerived(classDecl);
                bool isObjCRooted = classDecl.IsObjCRooted;
                // An ObjC-rooted boundary class directly inherits an ObjC type (e.g., CALayer)
                // and is NOT derived from a same-module Swift parent.
                bool isObjCBoundary = isObjCRooted && !isDerived;

                if (isDerived)
                {
                    var baseName = GenericTypeEmitter.GetTypeNameWithGenerics(classDecl.ResolvedSuperclass!, env.TypeDatabase);

                    // Collect base class interfaces to filter duplicates
                    var baseInterfaces = new HashSet<string>(
                        ProtocolConformanceHelper.GetImplementedInterfaces(
                            classDecl.ResolvedSuperclass!,
                            baseName,
                            moduleDecl.Name, env.TypeDatabase, conformanceValidator));

                    // Start with base class name. Keep ISwiftObject (needed for explicit interface
                    // re-implementation with derived type metadata). Skip IDisposable (inherited).
                    // Skip base-class protocols that aren't parameterized differently on derived.
                    var derivedInterfaces = new List<string> { baseName };
                    foreach (var iface in interfaces)
                    {
                        if (iface == "IDisposable")
                            continue;
                        if (iface == "ISwiftObject")
                        {
                            derivedInterfaces.Add(iface);
                            continue;
                        }
                        if (!baseInterfaces.Contains(iface))
                            derivedInterfaces.Add(iface);
                    }
                    interfaces = derivedInterfaces;
                }
                else if (isObjCBoundary)
                {
                    // ObjC-rooted boundary class: inherit from MAUI ObjC binding type.
                    // Replace IDisposable (inherited from NSObject) with ObjC base type.
                    // Fallback: cross-module ObjC-rooted with unresolved Swift parent → Foundation.NSObject
                    // (all ObjC-rooted Swift classes ultimately derive from NSObject).
                    var objcBaseName = MarshallingHelpers.GetObjCBaseTypeName(classDecl) ?? "Foundation.NSObject";
                    var boundaryInterfaces = new List<string> { objcBaseName };
                    foreach (var iface in interfaces)
                    {
                        if (iface == "IDisposable")
                            continue; // Inherited from NSObject
                        boundaryInterfaces.Add(iface);
                    }
                    interfaces = boundaryInterfaces;
                }

                if (classDecl.IsActor)
                    csWriter.WriteLine("// Swift actor type - methods are actor-isolated unless marked nonisolated");

                XmlDocCommentEmitter.EmitDocComment(csWriter, classDecl);
                AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, classDecl, emitObsolete: true);
                var (opaqueEmittable, opaqueSkipped) = MemberEmissionValidator.CountEmittableMembers(classDecl, env.TypeDatabase);
                if (opaqueEmittable == 0 && opaqueSkipped > 0)
                    TypeAnnotationHelper.EmitOpaqueTypeAnnotation(csWriter, opaqueSkipped);
                else
                    TypeAnnotationHelper.EmitDisposalRemarks(csWriter, classDecl);
                if (classDecl.Name.StartsWith("_"))
                    csWriter.WriteLine("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
                var classDeclaration = $"public partial class {typeNameWithGenerics} : {string.Join(", ", interfaces)}";
                if (!string.IsNullOrEmpty(whereClause))
                    classDeclaration += $" {whereClause}";
                csWriter.WriteLine(classDeclaration);
                csWriter.WriteLine("{");
                csWriter.Indent++;

                // Emit properties (skip unownedExecutor for actors - it's an internal actor runtime property)
                // Bug #9: Track emitted property C# names to detect duplicates.
                // Swift allows a type to have both static (from protocol conformance) and instance
                // properties with the same name, but C# does not (CS0102).
                var emittedPropertyNames = new HashSet<string>();
                foreach (PropertyDecl propertyDecl in classDecl.Properties)
                {
                    if (classDecl.IsActor && propertyDecl.Name == "unownedExecutor")
                    {
                        _logger.LogInformation($"Skipping actor runtime property 'unownedExecutor' on {classDecl.Name}.");
                        ReportCollector.RecordMemberSkipped(BindingItemKind.Property, propertyDecl.Name, classDecl, SkipReason.UnsupportedType, "Actor runtime property 'unownedExecutor' is not user-facing.");
                        continue;
                    }

                    // Bug #9: Skip duplicate property names (static + instance with same C# name)
                    // Use post-rename name for consistency with the propertyNames collision set below.
                    var csPropertyName = NameProvider.GetFinalMemberName(
                        NameProvider.GetPropertyName(propertyDecl.Name, classDecl.Name), propertyRenames);
                    if (!emittedPropertyNames.Add(csPropertyName))
                    {
                        _logger.LogInformation($"Skipping duplicate property '{classDecl.Name}.{csPropertyName}' (static/instance collision).");
                        ReportCollector.RecordMemberSkipped(BindingItemKind.Property, propertyDecl.Name, classDecl, SkipReason.DuplicateSignature, $"Property '{csPropertyName}' already emitted with different staticness.");
                        continue;
                    }

                    if (MemberEmissionValidator.IsSynthesizedProtocolProperty(propertyDecl, classDecl))
                    {
                        ReportCollector.RecordMemberSynthesized(BindingItemKind.Property, propertyDecl.Name, classDecl);
                        continue;
                    }

                    var skipReason = MemberEmissionValidator.CanEmitProperty(propertyDecl, env.TypeDatabase, out var skipDetails, out _);
                    if (skipReason != null)
                    {
                        ReportCollector.RecordMemberSkipped(BindingItemKind.Property, propertyDecl.Name, classDecl, skipReason.Value, skipDetails ?? "");
                        continue;
                    }

                    if (conductor.TryGetPropertyHandler(propertyDecl, out var propertyHandler))
                    {
                        var propertyEnv = propertyHandler.Marshal(propertyDecl, env.TypeDatabase);
                        propertyHandler.Emit(csWriter, swiftWriter, propertyEnv, conductor, childContext);
                    }
                    else
                    {
                        _logger.LogWarning($"No handler found for property {propertyDecl.Name}");
                    }
                }

                // Emit private fields and handle.
                // ObjC-rooted classes skip all handle/Dispose — lifecycle managed by NSObject.
                if (!isObjCRooted)
                {
                    // Class constructors use Unmanaged.passRetained().toOpaque() — no _payloadSize needed.
                    // Only structs/enums allocate via _payloadSize (SwiftSafeHandle path).
                    // Emitting _payloadSize for classes triggers SwiftObjectHelper<T>.GetTypeMetadata().Size
                    // at class load time, which can return garbage or cause crashes (e.g., CryptoSwift SIGABRT).

                    // Only root classes emit _handle, Payload property, Dispose().
                    // SwiftClassHandle calls Arc.Release directly (no VWT Destroy wrapper needed).
                    // No generated finalizer needed — SafeHandle's built-in finalizer calls ReleaseHandle.
                    if (!isDerived)
                    {
                        // If the class has a declared superclass but IsEffectivelyDerived returned false
                        // (e.g., superclass has unsupported generic constraints), we still emit _handle/Payload
                        // but with `new` modifiers to avoid CS0108 (hides inherited member).
                        bool needsNewModifier = classDecl.DirectSuperclassName != null;
                        WriteClassHandleField(csWriter, typeNameWithGenerics, needsNewModifier);
                        WriteClassHandleAccessors(csWriter, typeNameWithGenerics, needsNewModifier);
                    }
                }

                // Emit operators
                var operatorHandler = new OperatorHandler(_logger);
                var emittedOperatorSymbols = new HashSet<string>();
                foreach (var operatorDecl in classDecl.Operators)
                {
                    if (OperatorHandler.IsSupportedOperator(operatorDecl.OperatorSymbol))
                    {
                        if (operatorHandler.EmitOperator(csWriter, operatorDecl, env.TypeDatabase, pinvokeHelperContext))
                        {
                            emittedOperatorSymbols.Add(operatorDecl.OperatorSymbol);
                        }
                    }
                    else
                    {
                        ReportCollector.RecordMemberSkipped(BindingItemKind.Operator, operatorDecl.OperatorSymbol, classDecl, SkipReason.UnsupportedType, $"Operator '{operatorDecl.OperatorSymbol}' has no C# equivalent.");
                    }
                }
                // Handle paired operators (e.g., if == is defined but != is not)
                // Use typeNameWithGenerics to ensure generic types have proper type parameters in operator signatures
                operatorHandler.ValidateAndEmitPairs(csWriter, classDecl.Operators, typeNameWithGenerics, emittedOperatorSymbols, isReferenceType: true);

                bool hasEquality = emittedOperatorSymbols.Contains("==");
                bool hasInequality = emittedOperatorSymbols.Contains("!=");

                // Emit ISwiftObject implementation
                var iSwiftObjectWriter = new ClassISwiftObjectMethodWriter(csWriter, env.TypeDatabase, moduleDecl, classDecl, typeNameWithGenerics, pinvokeHelperContext, swiftWriter, context.GetEmissionContext(), hasBoxable: interfaces.Contains("Swift.Runtime.IExistentialBoxable"));
                var equatableWriter = new ClassEqualityMethodsWriter(csWriter, classDecl, typeNameWithGenerics, hasEquality, hasInequality, swiftWriter, context.GetEmissionContext(), env.TypeDatabase.AsyncLibraryName);

                // Derived classes emit equality if they have their own IEquatable<DerivedType>
                // (IEquatable<Derived> is a different interface from IEquatable<Base>).
                // Root classes always emit equality if Equatable.
                if (!isDerived || interfaces.Any(i => i.StartsWith("IEquatable<")))
                    equatableWriter.WriteSwiftEquatableImplementation();
                iSwiftObjectWriter.WriteClassImplementation();

                ToStringHelper.EmitToStringIfDescriptionExists(csWriter, classDecl, propertyRenames);

                // Collect property and nested type names for method/member collision detection
                var propertyNames = new HashSet<string>(classDecl.Properties.Select(p =>
                    NameProvider.GetFinalMemberName(
                        NameProvider.GetPropertyName(p.Name, classDecl.Name), propertyRenames)));
                // Nested type names collide with method names in C# (CS0102)
                foreach (var nestedType in classDecl.Types)
                    propertyNames.Add(NameProvider.ToPascalCase(nestedType.Name));

                SubscriptHandler.EmitSubscripts(csWriter, swiftWriter, classDecl, env.TypeDatabase, conductor, childContext, _logger);

                // Push type name (with generics) for nested type factory registration (NativeAOT).
                // C# requires Outer<T>.Nested, not Outer.Nested, for nested types under generic outers.
                var emissionCtx = context.GetEmissionContext();
                emissionCtx?.PushTypeNesting(typeNameWithGenerics);
                base.HandleBaseDecl(csWriter, swiftWriter, classDecl.Types, conductor, env.TypeDatabase, childContext);
                emissionCtx?.PopTypeNesting();
                base.HandleBaseDecl(csWriter, swiftWriter, classDecl.Methods, conductor, env.TypeDatabase, childContext, propertyNames);

                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine();

                // Emit P/Invoke helper class(es) after the main class.
                // If this is a nested generic inside a generic parent, defer emission
                // (emitting here would still be inside the outer generic type → CS7042).
                if (ownPInvokeContext != null)
                {
                    if (context.PInvokeHelperContext != null)
                        context.DeferredPInvokeHelperContexts.Add(ownPInvokeContext);
                    else
                    {
                        ownPInvokeContext.EmitHelperClass(csWriter);
                        foreach (var deferred in context.DeferredPInvokeHelperContexts)
                            deferred.EmitHelperClass(csWriter);
                        context.DeferredPInvokeHelperContexts.Clear();
                    }
                }
            }
        }

        // ComputePropertyRenames is now centralized in NameProvider.

        /// <summary>
        /// Returns true if the class has a resolved superclass that will actually be emitted
        /// in C# (i.e., not skipped due to unsupported generic constraints).
        /// This is the canonical "effectively derived" predicate — use it everywhere
        /// instead of raw HasResolvedSuperclass to ensure consistent behavior.
        /// </summary>
        internal static bool IsEffectivelyDerived(ClassDecl classDecl)
            => classDecl.HasResolvedSuperclass
               && !GenericTypeEmitter.TryGetUnsupportedConstraint(classDecl.ResolvedSuperclass!, out _);

        /// <summary>
        /// Writes the _handle instance field (root classes only — derived classes inherit).
        /// Uses SwiftClassHandle&lt;T&gt; which directly holds the Swift object pointer (no buffer).
        /// </summary>
        private static void WriteClassHandleField(CSharpWriter csWriter, string typeNameWithGenerics, bool needsNewModifier = false)
        {
            var newKeyword = needsNewModifier ? "new " : "";
            csWriter.WriteLine("[EditorBrowsable(EditorBrowsableState.Never)]");
            csWriter.WriteLine($"{newKeyword}protected SwiftClassHandle<{typeNameWithGenerics}> _handle = SwiftClassHandle<{typeNameWithGenerics}>.Zero;");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Writes the handle accessor, Payload compatibility property, and Dispose for classes.
        /// No generated finalizer needed — SwiftClassHandle's built-in SafeHandle finalizer
        /// calls ReleaseHandle → Arc.Release, which is safe on both Mono and NativeAOT.
        /// </summary>
        private static void WriteClassHandleAccessors(CSharpWriter csWriter, string typeNameWithGenerics, bool needsNewModifier = false)
        {
            var newKeyword = needsNewModifier ? "new " : "";
            csWriter.WriteLine("[EditorBrowsable(EditorBrowsableState.Never)]");
            csWriter.WriteLine($"{newKeyword}public SwiftClassHandle<{typeNameWithGenerics}> Payload => _handle;");
            csWriter.WriteLine($"IntPtr ISwiftObject.SwiftHandle => _handle.DangerousGetHandle();");
            csWriter.WriteLine($"internal {newKeyword}IntPtr GetSwiftHandle() => _handle.DangerousGetHandle();");
            csWriter.WriteLine();
            var newDispose = needsNewModifier ? "new " : "";
            var disposeMethods = $$"""
            /// <summary>
            /// Releases the underlying Swift ARC reference. Safe to call multiple times.
            /// Not required for correctness — the finalizer handles ARC cleanup automatically.
            /// Use for deterministic cleanup of scarce resources.
            /// </summary>
            public {{newDispose}}void Dispose()
            {
                _handle.Dispose();
                GC.SuppressFinalize(this);
            }
            """;
            csWriter.WriteLines(disposeMethods);
            csWriter.WriteLine();
        }
    }

    /// <summary>
    /// Class responsible for emitting the necessary code for ISwiftObject methods for classes.
    /// </summary>
    class ClassISwiftObjectMethodWriter
    {
        private readonly IndentedTextWriter _writer;
        private readonly ITypeDatabase _typeDatabase;
        private readonly ModuleDecl _moduleDecl;
        private readonly ClassDecl _classDecl;
        private readonly string _typeNameWithGenerics;
        private readonly string _constructorName;
        private readonly PInvokeHelperContext? _pinvokeHelperContext;
        private readonly bool _isDerived;
        private readonly bool _isObjCRooted;
        private readonly bool _isObjCBoundary;
        private readonly string _rootBaseTypeNameWithGenerics;
        private readonly SwiftWriter? _swiftWriter;
        private readonly ModuleEmissionContext? _emissionCtx;
        private readonly bool _hasBoxable;

        public ClassISwiftObjectMethodWriter(CSharpWriter csWriter, ITypeDatabase typeDatabase, ModuleDecl moduleDecl, ClassDecl classDecl, string typeNameWithGenerics, PInvokeHelperContext? pinvokeHelperContext = null, SwiftWriter? swiftWriter = null, ModuleEmissionContext? emissionCtx = null, bool hasBoxable = false)
        {
            _writer = csWriter;
            _typeDatabase = typeDatabase;
            _moduleDecl = moduleDecl;
            _classDecl = classDecl;
            _typeNameWithGenerics = typeNameWithGenerics;
            var angleBracket = typeNameWithGenerics.IndexOf('<');
            _constructorName = angleBracket >= 0 ? typeNameWithGenerics.Substring(0, angleBracket) : typeNameWithGenerics;
            _pinvokeHelperContext = pinvokeHelperContext;
            _isDerived = ClassHandler.IsEffectivelyDerived(classDecl);
            _isObjCRooted = classDecl.IsObjCRooted;
            _isObjCBoundary = _isObjCRooted && !_isDerived;
            _rootBaseTypeNameWithGenerics = GetRootBaseTypeNameWithGenerics(classDecl);
            _swiftWriter = swiftWriter;
            _emissionCtx = emissionCtx;
            _hasBoxable = hasBoxable;
        }

        /// <summary>
        /// Walks the ResolvedSuperclass chain to find the root base class type name.
        /// Stops at non-emittable ancestors (unsupported generic constraints) to stay
        /// consistent with IsEffectivelyDerived — a flat-emitted class must use its own
        /// type name so _payload and the private constructor agree on SwiftSafeHandle&lt;T&gt;.
        /// </summary>
        internal static string GetRootBaseTypeNameWithGenerics(ClassDecl classDecl)
        {
            var current = classDecl;
            while (current.HasResolvedSuperclass
                   && !GenericTypeEmitter.TryGetUnsupportedConstraint(current.ResolvedSuperclass!, out _))
                current = current.ResolvedSuperclass!;
            return GenericTypeEmitter.GetTypeNameWithGenerics(current);
        }

        /// <summary>
        /// Checks whether the class has a truly parameterless constructor (init() in Swift).
        /// Only checks for zero-parameter constructors in the ABI, NOT constructors with all-default
        /// parameters. The default-parameter overload emitter may skip methods with unsupported types,
        /// so we can't rely on it to produce a parameterless overload.
        /// </summary>
        private static bool HasParameterlessConstructor(ClassDecl classDecl)
        {
            return classDecl.Methods.Any(m =>
                m.IsConstructor && m.Visibility == Visibility.Public &&
                m.CSSignature.Count <= 1); // CSSignature[0] is return type, no parameters = Count <= 1
        }

        /// <summary>
        /// Writes the implementation for ISwiftObject methods for classes.
        /// </summary>
        public void WriteClassImplementation()
        {
            if (_isObjCBoundary)
            {
                // ObjC-rooted boundary class: NSObject.Handle IS the Swift object pointer.
                _writer.WriteLine("IntPtr ISwiftObject.SwiftHandle => Handle;");
                _writer.WriteLine("internal IntPtr GetSwiftHandle() => Handle;");
                _writer.WriteLine();
            }
            WriteGetTypeMetadata();
            WriteNewFromPayload();
            WriteMarshalToSwift();
            WriteGetProtocolConformanceDescriptor();
            WriteBoxAsExistential1(_hasBoxable);
            RecordTypeIfNonGeneric();
        }

        /// <summary>
        /// Writes the GetTypeMetadata method for the class along with the PInvoke method.
        /// </summary>
        private void WriteGetTypeMetadata()
        {
            string libPath = _typeDatabase.GetLibraryPath(_moduleDecl.Name);
            // For classes, the metadata accessor is the mangled name + "Ma"
            string metadataAccessor = $"{_classDecl.MangledName}Ma";

            if (_pinvokeHelperContext != null)
            {
                // For generic types, call the helper class with type metadata arguments
                var metadataArgs = string.Join(", ", _pinvokeHelperContext.GetMetadataArgumentList());
                _writer.WriteLine($"static TypeMetadata ISwiftObject.GetTypeMetadata() => {_pinvokeHelperContext.HelperClassName}.PInvoke_getMetadata(TypeMetadataRequest.Complete, {metadataArgs});");
                _writer.WriteLine();

                // Add the P/Invoke declaration to the helper context
                var declaration = new PInvokeDeclaration
                {
                    LibraryPath = libPath,
                    EntryPoint = metadataAccessor,
                    MethodName = "PInvoke_getMetadata",
                    ReturnType = "TypeMetadata",
                    ParametersString = "TypeMetadataRequest request",
                    IsAsync = false,
                    MetadataParameters = _pinvokeHelperContext.GetMetadataParameterDeclarations()
                };
                _pinvokeHelperContext.AddDeclaration(declaration);
            }
            else if (_swiftWriter != null && _emissionCtx != null &&
                     !string.IsNullOrEmpty(_typeDatabase.AsyncLibraryName))
            {
                // Xcframework mode: emit @_cdecl metadata wrapper.
                // Internal types (both @usableFromInline and truly internal) are inaccessible
                // by name from external Swift code, so the wrapper's `Module.Type.self` reference
                // won't compile. Fall back to CallConvSwift targeting the dylib directly.
                var moduleQualified = _classDecl.SwiftTypeName.ModuleQualifiedName;
                var moduleName = _classDecl.SwiftTypeName.Module;

                if (_classDecl.IsModuleInternal)
                {
                    // Fallback: use CallConvSwift P/Invoke targeting the dylib's metadata accessor
                    _writer.WriteLine("static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();");
                    _writer.WriteLine();

                    foreach (var line in PInvokeEmitHelper.FormatDeclarationLines(new PInvokeEmissionInfo
                    {
                        LibraryPath = libPath,
                        EntryPoint = metadataAccessor,
                        MethodName = "PInvoke_getMetadata",
                        ReturnType = "TypeMetadata",
                        ParametersString = "",
                        Visibility = PInvokeVisibility.Internal,
                        HasNewModifier = _isDerived
                    }))
                        _writer.WriteLine(line);
                    _writer.WriteLine();
                }
                else
                {
                    var symbol = MetadataWrapperEmitter.GetMetadataSymbolName(moduleName, moduleQualified);
                    MetadataWrapperEmitter.EmitIfNeeded(_swiftWriter, moduleName, moduleQualified, symbol, _emissionCtx);

                    // Try wrapper DLL first (Cdecl), fall back to dylib (CallConvSwift)
                    // when the wrapper wasn't compiled for this module.
                    _writer.WriteLines("""
                        static TypeMetadata ISwiftObject.GetTypeMetadata()
                        {
                            try
                            {
                                return PInvoke_getMetadata();
                            }
                            catch (System.DllNotFoundException)
                            {
                                return PInvoke_getMetadata_fallback();
                            }
                            catch (System.EntryPointNotFoundException)
                            {
                                return PInvoke_getMetadata_fallback();
                            }
                        }
                        """);
                    _writer.WriteLine();

                    foreach (var line in PInvokeEmitHelper.FormatDeclarationLines(new PInvokeEmissionInfo
                    {
                        LibraryPath = _typeDatabase.AsyncLibraryName!,
                        EntryPoint = symbol,
                        MethodName = "PInvoke_getMetadata",
                        ReturnType = "TypeMetadata",
                        ParametersString = "",
                        Visibility = PInvokeVisibility.Internal,
                        CallingConvention = PInvokeCallingConvention.Cdecl,
                        HasNewModifier = _isDerived
                    }))
                        _writer.WriteLine(line);
                    _writer.WriteLine();

                    // Fallback P/Invoke targeting the dylib's metadata accessor directly
                    foreach (var line in PInvokeEmitHelper.FormatDeclarationLines(new PInvokeEmissionInfo
                    {
                        LibraryPath = libPath,
                        EntryPoint = metadataAccessor,
                        MethodName = "PInvoke_getMetadata_fallback",
                        ReturnType = "TypeMetadata",
                        ParametersString = "",
                        Visibility = PInvokeVisibility.Internal,
                        HasNewModifier = _isDerived
                    }))
                        _writer.WriteLine(line);
                    _writer.WriteLine();
                }
            }
            else
            {
                // Manual mode: existing CallConvSwift path
                _writer.WriteLine("static TypeMetadata ISwiftObject.GetTypeMetadata() => PInvoke_getMetadata();");
                _writer.WriteLine();

                foreach (var line in PInvokeEmitHelper.FormatDeclarationLines(new PInvokeEmissionInfo
                {
                    LibraryPath = libPath,
                    EntryPoint = metadataAccessor,
                    MethodName = "PInvoke_getMetadata",
                    ReturnType = "TypeMetadata",
                    ParametersString = "",
                    Visibility = PInvokeVisibility.Internal,
                    HasNewModifier = _isDerived
                }))
                    _writer.WriteLine(line);
                _writer.WriteLine();
            }
        }

        /// <summary>
        /// Writes the NewFromPayload method for the class.
        /// The handle must carry a +1 ARC retain that this wrapper takes ownership of.
        /// </summary>
        private void WriteNewFromPayload()
        {
            if (_isObjCRooted)
            {
                // ObjC-rooted: handle is the raw Swift object pointer (same as pure Swift classes).
                // Wrap with SwiftHandle → base(NativeHandle) → NSObject takes ownership.
                var text = $$"""
                [EditorBrowsable(EditorBrowsableState.Never)]
                static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                {
                    return new {{_typeNameWithGenerics}}(new SwiftHandle(handle));
                }
                """;
                _writer.WriteLines(text);
                _writer.WriteLine();
            }
            else
            {
                var text = $$"""
                [EditorBrowsable(EditorBrowsableState.Never)]
                static ISwiftObject ISwiftObject.NewFromPayload(IntPtr handle)
                {
                    var obj = new {{_typeNameWithGenerics}}(handle);
                    Swift.Runtime.SwiftDisposeScope.TryRegister(obj);
                    return obj;
                }
                """;
                _writer.WriteLines(text);
                _writer.WriteLine();
            }

            EmitPrivateConstructor();
        }

        /// <summary>
        /// Writes the private constructor accepting a SwiftHandle.
        /// The handle must carry a +1 ARC retain that this wrapper takes ownership of.
        /// Dispose releases exactly one ARC reference.
        /// </summary>
        private void EmitPrivateConstructor()
        {
            if (_isObjCRooted)
            {
                // ObjC-rooted: SwiftHandle entry-point constructor chains to base(NativeHandle).
                // DangerousRelease() balances: Swift returns +1, MAUI NSObject(NativeHandle,false) retains +2, release back to +1.
                var text = $$"""
                internal {{_constructorName}}(SwiftHandle handle) : base((ObjCRuntime.NativeHandle)handle.Handle)
                {
                    DangerousRelease();
                }
                """;
                _writer.WriteLines(text);
                _writer.WriteLine();

                // Protected NativeHandle constructor for same-module Swift subclass chaining.
                // NO DangerousRelease — only the entry-point SwiftHandle ctor releases.
                var protectedCtor = $$"""
                [EditorBrowsable(EditorBrowsableState.Never)]
                protected {{_constructorName}}(ObjCRuntime.NativeHandle handle) : base(handle) { }
                """;
                _writer.WriteLines(protectedCtor);
                _writer.WriteLine();
            }
            else
            {
                // SwiftClassHandle directly holds the Swift object pointer (no buffer).
                // Derived classes assign to the inherited _handle field using the ROOT base class's
                // SwiftClassHandle<T> type parameter. Arc.Release operates on the isa pointer
                // inside the Swift object, ignoring the metadata's T.
                var handleType = _rootBaseTypeNameWithGenerics;
                // Derived private constructors chain to the base's protected sentinel constructor
                var baseChain = _isDerived ? " : base(default(SwiftInheritanceChain))" : "";
                var text = $$"""
                {{_constructorName}}(SwiftHandle handle){{baseChain}}
                {
                    _handle = new SwiftClassHandle<{{handleType}}>(handle);
                }
                """;

                _writer.WriteLines(text);
                _writer.WriteLine();

                // All classes emit a protected constructor with a SwiftInheritanceChain sentinel parameter
                // for derived class constructor chaining. Derived constructors chain to
                // base(default(SwiftInheritanceChain)) to invoke this constructor. SwiftInheritanceChain
                // is a marker struct from Swift.Runtime that cannot conflict with any Swift-generated
                // constructor parameters (unlike bool, int, etc. which Swift types commonly use).
                {
                    var sentinelBaseChain = _isDerived ? " : base(default(SwiftInheritanceChain))" : "";
                    var protectedCtor = $$"""
                    [EditorBrowsable(EditorBrowsableState.Never)]
                    protected {{_constructorName}}(SwiftInheritanceChain _swiftObject){{sentinelBaseChain}} { }
                    """;
                    _writer.WriteLines(protectedCtor);
                    _writer.WriteLine();
                }
            }
        }

        /// <summary>
        /// Records this type for NativeAOT factory registration if it's non-generic.
        /// Generic types rely on constrained code paths for registration.
        /// Also records protocol conformance pairs for NativeAOT pre-registration.
        /// </summary>
        private void RecordTypeIfNonGeneric()
        {
            if (_emissionCtx != null && !_typeNameWithGenerics.Contains('<'))
            {
                _emissionCtx.RecordSwiftObjectType(_typeNameWithGenerics);
                foreach (var protocolName in ProtocolConformanceHelper.GetConformanceProtocolNames(
                    _classDecl.Conformances, _moduleDecl.Name, _typeNameWithGenerics, _typeDatabase))
                {
                    _emissionCtx.RecordConformance(_typeNameWithGenerics, protocolName);
                }
            }
        }

        /// <summary>
        /// Writes the MarshalToSwift method for the class.
        /// </summary>
        private void WriteMarshalToSwift()
        {
            if (_isObjCRooted)
            {
                // ObjC-rooted: use Handle directly (the NSObject pointer IS the Swift object pointer).
                // No DangerousAddRef/Release — NSObject manages lifecycle via ARC.
                var text = $$"""
                [EditorBrowsable(EditorBrowsableState.Never)]
                unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                {
                    var metadata = SwiftObjectHelper<{{_typeNameWithGenerics}}>.GetTypeMetadata();
                    fixed (void* swiftDest = swiftDestSpan)
                    {
                        IntPtr selfPtr = Handle;
                        metadata.ValueWitnessTable->InitializeWithCopy(swiftDest, &selfPtr, metadata);
                        return (int)metadata.Size;
                    }
                }
                """;
                _writer.WriteLines(text);
                _writer.WriteLine();
            }
            else
            {
                // SwiftClassHandle: DangerousGetHandle() IS the Swift object pointer (no buffer).
                // VWT->InitializeWithCopy for classes expects a pointer TO the class pointer,
                // so we take the address of a local copy.
                // DangerousAddRef/Release prevents concurrent finalizer from releasing the handle.
                var text = $$"""
                [EditorBrowsable(EditorBrowsableState.Never)]
                unsafe int ISwiftObject.MarshalToSwift(ref Span<byte> swiftDestSpan)
                {
                    var metadata = SwiftObjectHelper<{{_typeNameWithGenerics}}>.GetTypeMetadata();
                    if ((int)metadata.Size > swiftDestSpan.Length)
                    {
                        throw new ArgumentException($"Span size does not match type size, Expected: {(int)metadata.Size}, Actual: {swiftDestSpan.Length}");
                    }
                    fixed (void* swiftDest = swiftDestSpan)
                    {
                        bool success = false;
                        _handle.DangerousAddRef(ref success);
                        try
                        {
                            IntPtr selfPtr = _handle.DangerousGetHandle();
                            metadata.ValueWitnessTable->InitializeWithCopy(swiftDest, &selfPtr, metadata);
                            return (int)metadata.Size;
                        }
                        finally
                        {
                            if (success)
                                _handle.DangerousRelease();
                        }
                    }
                }
                """;
                _writer.WriteLines(text);
                _writer.WriteLine();
            }
        }

        /// <summary>
        /// Writes the GetProtocolConformanceDescriptor method for the class.
        /// </summary>
        private void WriteGetProtocolConformanceDescriptor()
        {
            WriteStaticConstructor();
            var libPath = _typeDatabase.GetLibraryPath(_moduleDecl.Name);
            var text = $$"""
            [EditorBrowsable(EditorBrowsableState.Never)]
            static ProtocolConformanceDescriptor ISwiftObject.GetProtocolConformanceDescriptor<TProtocol>()
                where TProtocol : class
            {
                if (!_protocolConformanceSymbols.TryGetValue(typeof(TProtocol), out var symbolName))
                {
                    throw new SwiftRuntimeException($"Attempted to retrieve protocol conformance descriptor for type {{_classDecl.Name}} and protocol {typeof(TProtocol).Name}, but no conformance was found.");
                }

                return ProtocolConformanceDescriptor.LoadFromSymbol("{{libPath}}", symbolName);
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        /// <summary>
        /// Writes the static constructor for the class.
        /// </summary>
        private void WriteStaticConstructor()
        {
            var text = $$"""
            [EditorBrowsable(EditorBrowsableState.Never)]
            private static Dictionary<Type, string> _protocolConformanceSymbols;

            static {{_constructorName}}()
            {
                _protocolConformanceSymbols = new Dictionary<Type, string>
                {
                    {{GenerateGetProtocolConformanceDictionaryEntries()}}
                };
            }
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        private void WriteBoxAsExistential1(bool emit)
        {
            if (!emit)
                return;

            var text = $$"""
            [EditorBrowsable(EditorBrowsableState.Never)]
            ExistentialContainer1 Swift.Runtime.IExistentialBoxable.BoxAsExistential1<TProtocol>()
                => ExistentialContainerFactory.Create<{{_typeNameWithGenerics}}, TProtocol>(this);
            """;

            _writer.WriteLines(text);
            _writer.WriteLine();
        }

        private string GenerateGetProtocolConformanceDictionaryEntries()
        {
            return ProtocolConformanceHelper.GenerateProtocolConformanceDictionaryEntries(
                CollectAllConformancesWithResolvedSymbols(),
                _moduleDecl.Name,
                _typeNameWithGenerics,
                _typeDatabase);
        }

        /// <summary>
        /// Collects all conformances for this class, including inherited conformances from ancestors.
        /// Only walks ancestors if _isDerived is true (which uses the strict IsEffectivelyDerived predicate).
        /// Own conformances are yielded first; empty symbols are resolved from ancestors via 'with' expression.
        /// Deduplicates by Protocol.ModuleQualifiedName to avoid duplicate dictionary entries.
        /// </summary>
        private IEnumerable<TypeConformance> CollectAllConformancesWithResolvedSymbols()
        {
            var seen = new HashSet<string>();

            // Yield own conformances first, resolving empty symbols from ancestors
            foreach (var conformance in _classDecl.Conformances)
            {
                seen.Add(conformance.Protocol.ModuleQualifiedName);

                if (string.IsNullOrEmpty(conformance.ProtocolConformanceDescriptor) && _isDerived)
                {
                    // Try to find a non-empty symbol from an ancestor
                    var ancestorSymbol = FindConformanceSymbolInAncestors(conformance.Protocol);
                    if (ancestorSymbol != null)
                    {
                        yield return conformance with { ProtocolConformanceDescriptor = ancestorSymbol };
                        continue;
                    }
                }

                yield return conformance;
            }

            // Yield ancestor conformances not already present on this class
            if (!_isDerived)
                yield break;

            var current = _classDecl;
            while (current.HasResolvedSuperclass)
            {
                var ancestor = current.ResolvedSuperclass!;
                if (GenericTypeEmitter.TryGetUnsupportedConstraint(ancestor, out _))
                    break; // Stop at non-emittable ancestor

                foreach (var conformance in ancestor.Conformances)
                {
                    if (seen.Add(conformance.Protocol.ModuleQualifiedName))
                        yield return conformance;
                }

                current = ancestor;
            }
        }

        /// <summary>
        /// Walks the ResolvedSuperclass chain looking for a non-empty conformance symbol
        /// for the given protocol. Returns null if no ancestor has it.
        /// </summary>
        private string? FindConformanceSymbolInAncestors(SwiftTypeName protocol)
        {
            var current = _classDecl;
            while (current.HasResolvedSuperclass)
            {
                var ancestor = current.ResolvedSuperclass!;
                if (GenericTypeEmitter.TryGetUnsupportedConstraint(ancestor, out _))
                    break;

                var match = ancestor.Conformances.FirstOrDefault(c =>
                    c.Protocol.ModuleQualifiedName == protocol.ModuleQualifiedName
                    && !string.IsNullOrEmpty(c.ProtocolConformanceDescriptor));
                if (match != null)
                    return match.ProtocolConformanceDescriptor;

                current = ancestor;
            }
            return null;
        }
    }

    /// <summary>
    /// Class responsible for emitting equality methods for class types.
    /// </summary>
    public class ClassEqualityMethodsWriter
    {
        private readonly IndentedTextWriter _writer;
        private readonly ClassDecl _classDecl;
        private readonly string _typeNameWithGenerics;
        private readonly bool _implementsEquatable;
        private readonly bool _implementsHashable;
        private readonly bool _hasExplicitEqualityOperator;
        private readonly bool _hasExplicitInequalityOperator;
        private readonly SwiftWriter? _swiftWriter;
        private readonly ModuleEmissionContext? _emissionContext;
        private readonly string? _wrapperLibraryName;

        public ClassEqualityMethodsWriter(CSharpWriter csWriter, ClassDecl classDecl, string typeNameWithGenerics, bool hasExplicitEqualityOperator, bool hasExplicitInequalityOperator)
        {
            _writer = csWriter;
            _classDecl = classDecl;
            _typeNameWithGenerics = typeNameWithGenerics;
            _implementsEquatable = _classDecl.Conformances.Any(c => c.Protocol.Name == "Equatable");
            // OptionSet, RawRepresentable, and SetAlgebra imply Hashable in Swift.
            // The ABI JSON may not list Hashable explicitly for types that get it transitively.
            _implementsHashable = _classDecl.Conformances.Any(c =>
                c.Protocol.ModuleQualifiedName == "Swift.Hashable" ||
                (c.Protocol.Name == "Hashable" && string.IsNullOrEmpty(c.Protocol.Module)) ||
                c.Protocol.Name == "OptionSet" ||
                c.Protocol.Name == "RawRepresentable");
            _hasExplicitEqualityOperator = hasExplicitEqualityOperator;
            _hasExplicitInequalityOperator = hasExplicitInequalityOperator;
        }

        /// <summary>
        /// Constructor with Swift wrapper support. When swiftWriter and emissionContext are provided,
        /// emits @_cdecl equality wrappers instead of using SwiftEquatable.Equals (which uses
        /// CallConvSwift and crashes on NativeAOT).
        /// </summary>
        public ClassEqualityMethodsWriter(CSharpWriter csWriter, ClassDecl classDecl, string typeNameWithGenerics, bool hasExplicitEqualityOperator, bool hasExplicitInequalityOperator, SwiftWriter? swiftWriter, ModuleEmissionContext? emissionContext, string? wrapperLibraryName)
            : this(csWriter, classDecl, typeNameWithGenerics, hasExplicitEqualityOperator, hasExplicitInequalityOperator)
        {
            _swiftWriter = swiftWriter;
            _emissionContext = emissionContext;
            _wrapperLibraryName = wrapperLibraryName;
        }

        public void WriteSwiftEquatableImplementation()
        {
            if (_implementsEquatable)
            {
                WriteSwiftEquatableImplementationWithSwiftEquals();
            }
            else
            {
                WriteDefaultEquatableImplementation();
            }
        }

        /// <summary>
        /// Gets the @_cdecl symbol name for a class equality wrapper.
        /// </summary>
        private static string GetEqualitySymbolName(ClassDecl classDecl)
        {
            var moduleName = classDecl.ModuleDecl?.Name ?? "Unknown";
            var safeTypeName = classDecl.Name.Replace(".", "_");
            var hash = EmitterUtility.DeterministicHash8(classDecl.MangledName ?? classDecl.Name);
            return $"SBW_{moduleName}_{safeTypeName}_eq_{hash}";
        }

        /// <summary>
        /// Emits a @_cdecl Swift wrapper for class equality comparison and returns the symbol name.
        /// Uses Unmanaged&lt;AnyObject&gt;.fromOpaque to safely cast opaque pointers back to class instances.
        /// Returns null if wrapper emission is not available or not needed.
        /// </summary>
        private string? TryEmitSwiftEqualityWrapper()
        {
            if (_swiftWriter == null || _emissionContext == null || _wrapperLibraryName == null)
                return null;

            // Skip for generic types (wrapper can't be instantiated)
            if (_classDecl.GenericParameters.Count > 0)
                return null;

            // Skip for module-internal classes — the wrapper library can't name them.
            // Same guard as metadata wrapper emission (WriteGetTypeMetadata).
            if (_classDecl.IsModuleInternal)
                return null;

            var symbolName = GetEqualitySymbolName(_classDecl);

            // Check dedup — don't emit twice for the same symbol
            if (!_emissionContext.TryAddEqualityWrapperSymbol(symbolName))
                return symbolName; // Already emitted, return for C# P/Invoke

            var swiftTypeName = _classDecl.SwiftTypeName.ToString();

            // Classes use Unmanaged<AnyObject>.fromOpaque to safely convert opaque pointers
            // back to class instances (NOT assumingMemoryBound which is for structs).
            _swiftWriter.WriteLines($$"""

            @_cdecl("{{symbolName}}")
            public func {{symbolName}}(_ lhs: UnsafeRawPointer, _ rhs: UnsafeRawPointer) -> UInt8 {
                let l = Unmanaged<AnyObject>.fromOpaque(lhs).takeUnretainedValue() as! {{swiftTypeName}}
                let r = Unmanaged<AnyObject>.fromOpaque(rhs).takeUnretainedValue() as! {{swiftTypeName}}
                return (l == r) ? 1 : 0
            }
            """);

            return symbolName;
        }

        /// <summary>
        /// Emits the C# P/Invoke declaration for the class equality wrapper.
        /// </summary>
        private void EmitEqualityPInvoke(string symbolName)
        {
            _writer.WriteLines($$"""
            [global::System.Runtime.InteropServices.LibraryImport("{{_wrapperLibraryName}}", EntryPoint = "{{symbolName}}")]
            [return: global::System.Runtime.InteropServices.MarshalAs(global::System.Runtime.InteropServices.UnmanagedType.U1)]
            private static partial bool PInvoke_eq(IntPtr lhs, IntPtr rhs);
            """);
            _writer.WriteLine();
        }

        private void WriteSwiftEquatableImplementationWithSwiftEquals()
        {
            // Try to emit @_cdecl equality wrapper (avoids CallConvSwift which crashes on NativeAOT).
            // Only works for non-generic types with wrapper library support.
            var eqSymbol = TryEmitSwiftEqualityWrapper();
            if (eqSymbol != null)
            {
                EmitEqualityPInvoke(eqSymbol);
            }

            // Equality comparison expression — use @_cdecl P/Invoke if available.
            // Classes: extract pointer via GetSwiftHandle() (emitted on root and ObjC boundary classes).
            string equalsExpr(string lhs, string rhs)
            {
                if (eqSymbol == null) return $"Swift.Runtime.SwiftEquatable.Equals({lhs}, {rhs})";
                return $"PInvoke_eq({lhs}.GetSwiftHandle(), {rhs}.GetSwiftHandle())";
            }

            // Always write Equals and GetHashCode methods
            // Use typeNameWithGenerics for is-check
            var hashCodeBody = _implementsHashable
                ? "return Swift.Runtime.SwiftHashable.GetHashCode(this);"
                : "return 0;";
            var equalsMethods = $$"""
            public override bool Equals(object? obj)
            {
                return obj is {{_typeNameWithGenerics}} other && {{equalsExpr("this", "other")}};
            }

            public override int GetHashCode()
            {
                {{hashCodeBody}}
            }
            """;

            _writer.WriteLines(equalsMethods);
            _writer.WriteLine();

            // Only write operator == if no explicit operator is defined
            // Use typeNameWithGenerics for operator parameters to fix CS0563/CS0305
            if (!_hasExplicitEqualityOperator)
            {
                var equalityOperator = $$"""
                public static bool operator ==({{_typeNameWithGenerics}}? left, {{_typeNameWithGenerics}}? right)
                {
                    if (left is null) return right is null;
                    if (right is null) return false;
                    return {{equalsExpr("left", "right")}};
                }
                """;
                _writer.WriteLines(equalityOperator);
                _writer.WriteLine();
            }

            // Only write operator != if no explicit operator is defined
            if (!_hasExplicitInequalityOperator)
            {
                var inequalityOperator = $$"""
                public static bool operator !=({{_typeNameWithGenerics}}? left, {{_typeNameWithGenerics}}? right)
                {
                    if (left is null) return right is not null;
                    if (right is null) return true;
                    return !{{equalsExpr("left", "right")}};
                }
                """;
                _writer.WriteLines(inequalityOperator);
                _writer.WriteLine();
            }

            // Write the IEquatable<T>.Equals method - use typeNameWithGenerics
            var equatableEquals = $$"""
            public bool Equals({{_typeNameWithGenerics}}? other)
            {
                if (other is null) return false;
                return {{equalsExpr("this", "other")}};
            }
            """;

            _writer.WriteLines(equatableEquals);
            _writer.WriteLine();
        }

        private void WriteDefaultEquatableImplementation()
        {
            // Non-Equatable types: no Equals/GetHashCode/operator overrides.
            // Classes inherit reference equality from object.
        }
    }
}
