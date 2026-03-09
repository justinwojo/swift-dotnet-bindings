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
            var typeNameWithGenerics = GenericTypeEmitter.GetTypeNameWithGenerics(classDecl);
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
                    var baseName = GenericTypeEmitter.GetTypeNameWithGenerics(classDecl.ResolvedSuperclass!);

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

                // Emit private fields and payload.
                // ObjC-rooted classes skip all payload/Dispose/finalizer — lifecycle managed by NSObject.
                if (!isObjCRooted)
                {
                    // All classes need _payloadSize (per-type metadata size for allocation).
                    // Only root classes emit _payload, Payload property, Dispose(), and finalizer.
                    WriteClassPayloadSize(csWriter, typeNameWithGenerics, isDerived);
                    if (!isDerived)
                    {
                        WriteClassPayloadField(csWriter, typeNameWithGenerics);
                        WriteClassPayload(csWriter, typeNameWithGenerics);

                        // Emit per-type @_cdecl destroy wrapper to avoid CallConvSwift crash on NativeAOT.
                        // Only root classes register the destroy action (derived classes inherit _payload).
                        var simpleName = typeNameWithGenerics.Contains('<')
                            ? typeNameWithGenerics.Substring(0, typeNameWithGenerics.IndexOf('<'))
                            : typeNameWithGenerics;
                        DestroyWrapperEmitter.EmitIfNeeded(
                            csWriter, swiftWriter,
                            simpleName,
                            typeNameWithGenerics,
                            moduleDecl.Name,
                            classDecl.SwiftTypeName.ToString(),
                            env.TypeDatabase.AsyncLibraryName,
                            context.GetEmissionContext());
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
                var iSwiftObjectWriter = new ClassISwiftObjectMethodWriter(csWriter, env.TypeDatabase, moduleDecl, classDecl, typeNameWithGenerics, pinvokeHelperContext);
                var equatableWriter = new ClassEqualityMethodsWriter(csWriter, classDecl, typeNameWithGenerics, hasEquality, hasInequality);

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

                base.HandleBaseDecl(csWriter, swiftWriter, classDecl.Types, conductor, env.TypeDatabase, childContext);
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
        /// Writes the _payloadSize static field (all classes — each needs its own metadata size).
        /// Each class independently declares _payloadSize for its own type metadata.
        /// </summary>
        private static void WriteClassPayloadSize(CSharpWriter csWriter, string typeNameWithGenerics, bool isDerived)
        {
            csWriter.WriteLine("[EditorBrowsable(EditorBrowsableState.Never)]");
            csWriter.WriteLine($"static nuint _payloadSize = SwiftObjectHelper<{typeNameWithGenerics}>.GetTypeMetadata().Size;");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Writes the _payload instance field (root classes only — derived classes inherit).
        /// </summary>
        private static void WriteClassPayloadField(CSharpWriter csWriter, string typeNameWithGenerics)
        {
            csWriter.WriteLine("[EditorBrowsable(EditorBrowsableState.Never)]");
            csWriter.WriteLine($"protected SwiftSafeHandle<{typeNameWithGenerics}> _payload = SwiftSafeHandle<{typeNameWithGenerics}>.Zero;");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Writes the payload accessor for the class.
        /// </summary>
        private static void WriteClassPayload(CSharpWriter csWriter, string typeNameWithGenerics)
        {
            csWriter.WriteLine("[EditorBrowsable(EditorBrowsableState.Never)]");
            csWriter.WriteLine($"public SwiftSafeHandle<{typeNameWithGenerics}> Payload => _payload;");
            csWriter.WriteLine($"IntPtr ISwiftObject.SwiftHandle => _payload.DangerousGetHandle();");
            csWriter.WriteLine();
            var simpleName = typeNameWithGenerics.Contains('<')
                ? typeNameWithGenerics.Substring(0, typeNameWithGenerics.IndexOf('<'))
                : typeNameWithGenerics;
            var disposeMethods = $$"""
            /// <summary>Releases the underlying Swift object. Safe to call multiple times.</summary>
            public void Dispose()
            {
                _payload.Dispose();
                GC.SuppressFinalize(this);
            }

            ~{{simpleName}}()
            {
                Swift.Runtime.SwiftDispose.FinalizerCleanup(_payload);
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

        public ClassISwiftObjectMethodWriter(CSharpWriter csWriter, ITypeDatabase typeDatabase, ModuleDecl moduleDecl, ClassDecl classDecl, string typeNameWithGenerics, PInvokeHelperContext? pinvokeHelperContext = null)
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
                _writer.WriteLine();
            }
            WriteGetTypeMetadata();
            WriteNewFromPayload();
            WriteMarshalToSwift();
            WriteGetProtocolConformanceDescriptor();
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
                _writer.WriteLine($"static TypeMetadata ISwiftObject.GetTypeMetadata() => {_pinvokeHelperContext.HelperClassName}.PInvoke_getMetadata({metadataArgs});");
                _writer.WriteLine();

                // Add the P/Invoke declaration to the helper context
                var declaration = new PInvokeDeclaration
                {
                    LibraryPath = libPath,
                    EntryPoint = metadataAccessor,
                    MethodName = "PInvoke_getMetadata",
                    ReturnType = "TypeMetadata",
                    ParametersString = "",
                    IsAsync = false,
                    MetadataParameters = _pinvokeHelperContext.GetMetadataParameterDeclarations()
                };
                _pinvokeHelperContext.AddDeclaration(declaration);
            }
            else
            {
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
                // ObjC-rooted: payload buffer contains the object pointer. Read it, free the buffer,
                // then wrap with SwiftHandle → base(NativeHandle) → NSObject takes ownership.
                var text = $$"""
                [EditorBrowsable(EditorBrowsableState.Never)]
                static unsafe ISwiftObject ISwiftObject.NewFromPayload(IntPtr payload)
                {
                    IntPtr objectPtr = *(IntPtr*)payload;
                    NativeMemory.Free((void*)payload);
                    return new {{_typeNameWithGenerics}}(new SwiftHandle(objectPtr));
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
                    return new {{_typeNameWithGenerics}}(handle);
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
                // Derived classes assign to the inherited _payload field using the ROOT base class's
                // SwiftSafeHandle<T> type parameter. For class types, VWT->Destroy calls swift_release
                // which operates on the isa pointer inside the Swift object, ignoring the metadata's T.
                var safeHandleType = _rootBaseTypeNameWithGenerics;
                // Derived private constructors chain to the base's protected sentinel constructor
                var baseChain = _isDerived ? " : base(default(SwiftInheritanceChain))" : "";
                var text = $$"""
                {{_constructorName}}(SwiftHandle handle){{baseChain}}
                {
                    _payload = new SwiftSafeHandle<{{safeHandleType}}>(handle);
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
                        // Ensure that the instance is valid before making copy
                        bool success = false;
                        _payload.DangerousAddRef(ref success);
                        try
                        {
                            metadata.ValueWitnessTable->InitializeWithCopy(swiftDest, (void*)_payload.DangerousGetHandle(), metadata);
                            return (int)metadata.Size;
                        }
                        finally
                        {
                            if (success)
                                _payload.DangerousRelease();
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

        private void WriteSwiftEquatableImplementationWithSwiftEquals()
        {
            // Always write Equals and GetHashCode methods
            // Use typeNameWithGenerics for is-check
            var hashCodeBody = _implementsHashable
                ? "return Swift.Runtime.SwiftHashable.GetHashCode(this);"
                : "return 0;";
            var equalsMethods = $$"""
            public override bool Equals(object? obj)
            {
                return obj is {{_typeNameWithGenerics}} other && Swift.Runtime.SwiftEquatable.Equals(this, other);
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
                public static bool operator ==({{_typeNameWithGenerics}} left, {{_typeNameWithGenerics}} right)
                {
                    return Swift.Runtime.SwiftEquatable.Equals(left, right);
                }
                """;
                _writer.WriteLines(equalityOperator);
                _writer.WriteLine();
            }

            // Only write operator != if no explicit operator is defined
            if (!_hasExplicitInequalityOperator)
            {
                var inequalityOperator = $$"""
                public static bool operator !=({{_typeNameWithGenerics}} left, {{_typeNameWithGenerics}} right)
                {
                    return !Swift.Runtime.SwiftEquatable.Equals(left, right);
                }
                """;
                _writer.WriteLines(inequalityOperator);
                _writer.WriteLine();
            }

            // Write the IEquatable<T>.Equals method - use typeNameWithGenerics
            var equatableEquals = $$"""
            public bool Equals({{_typeNameWithGenerics}}? other)
            {
                return Swift.Runtime.SwiftEquatable.Equals(this, other);
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
