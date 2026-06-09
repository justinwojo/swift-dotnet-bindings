// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using Microsoft.Extensions.Logging;
using Swift.Runtime;

namespace BindingsGeneration
{
    /// <summary>
    /// Factory class for creating instances of FrozenStructHandler.
    /// </summary>
    public class FrozenStructHandlerFactory : HandlerFactory, IFactory<BaseDecl, ITypeHandler>
    {

        /// <summary>
        /// Initializes a new instance of the <see cref="FrozenStructHandlerFactory"/> class.
        /// </summary>
        /// <param name="loggerFactory">The logger factory instance.</param>
        public FrozenStructHandlerFactory(ILoggerFactory loggerFactory) : base(loggerFactory.CreateLogger<FrozenStructHandler>())
        {
        }

        /// <summary>
        /// Determines if the factory handles the specified declaration.
        /// </summary>
        /// <param name="decl">The base declaration.</param>
        public bool Handles(BaseDecl decl)
        {
            return decl is StructDecl structDecl && structDecl.IsFrozen;
        }

        /// <summary>
        /// Constructs a new instance of StructHandler.
        /// </summary>
        public ITypeHandler Construct()
        {
            return new FrozenStructHandler(_handlerLogger);
        }
    }

    /// <summary>
    /// Handler class for frozen struct declarations.
    /// </summary>
    public class FrozenStructHandler : BaseHandler, ITypeHandler
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FrozenStructHandler"/> class.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <remarks>
        public FrozenStructHandler(ILogger logger) : base(logger)
        {
        }

        /// <inheritdoc/>
        public IEnvironment Marshal(BaseDecl baseDecl, ITypeDatabase typeDatabase)
        {
            if (baseDecl is not StructDecl structDecl)
            {
                throw new ArgumentException("The provided decl must be a StructDecl.", nameof(baseDecl));
            }
            return new TypeEnvironment(structDecl, typeDatabase);
        }

        /// <inheritdoc/>
        public void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor, TypeHandlerContext context)
        {
            var structEnv = (TypeEnvironment)env;
            var structDecl = (StructDecl)structEnv.TypeDecl;
            var parentDecl = structDecl.ParentDecl ?? throw new ArgumentNullException(nameof(structDecl.ParentDecl));
            var moduleDecl = structDecl.ModuleDecl ?? throw new ArgumentNullException(nameof(structDecl.ModuleDecl));

            if (GenericTypeEmitter.TryGetUnsupportedConstraint(structDecl, out var unsupportedConstraint))
            {
                var reason = AppleFrameworkRegistry.GetUnsupportedConstraintSkipReason(unsupportedConstraint.Module);
                ReportCollector.RecordTypeSkipped(structDecl, reason, $"Unsupported generic constraint: {unsupportedConstraint.ModuleQualifiedName}");
                UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, structDecl.Name, reason, $"generic constraint: {unsupportedConstraint.ModuleQualifiedName}");
                _logger.LogWarning(
                    "Skipping type '{TypeName}' - generic constraint references unsupported protocol '{Protocol}' from module '{Module}'.",
                    structDecl.Name,
                    unsupportedConstraint.Name,
                    unsupportedConstraint.Module);
                return;
            }

            if (GenericTypeEmitter.TryGetVariadicGenericParameter(structDecl, out var variadicParam))
            {
                ReportCollector.RecordTypeSkipped(structDecl, SkipReason.UnsupportedSignature, $"Variadic generic parameter pack '{variadicParam}' has no C# equivalent.");
                UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, structDecl.Name, SkipReason.UnsupportedSignature, $"variadic generic parameter pack '{variadicParam}' (Swift `{variadicParam}` / `repeat {variadicParam}`) has no C# equivalent.");
                _logger.LogWarning(
                    "Skipping type '{TypeName}' - variadic generic parameter pack '{Variadic}' has no C# equivalent.",
                    structDecl.Name,
                    variadicParam);
                return;
            }

            // Frozen value-with-memory struct (ClassWithBufferStruct) whose blitted Buffer layout
            // cannot be sized cross-compile because a stored field is a generic value-type
            // instantiation (e.g. ClosedRange<Int>, Result<T,E>) — its size depends on the type
            // arguments, which SwiftTypeName strips, so there is no persisted InlineSize and the
            // iOS/device slice exposes no live metadata. Emitting a guessed Buffer would mis-size
            // the field and corrupt the heap, so fail closed and skip. Mirrored in TypeSkipPrePass
            // so members passing/returning the struct by value are pruned in the same pass.
            if (HasIndeterminateBufferLayout(structDecl, env.TypeDatabase))
            {
                const string detail = "stored property has a generic value-type layout whose per-instantiation size is not derivable cross-compile (e.g. ClosedRange<Bound>, Result<Success,Failure>); a blitted Buffer would mis-size the field.";
                ReportCollector.RecordTypeSkipped(structDecl, SkipReason.IndeterminateStructLayout, detail);
                UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, structDecl.Name, SkipReason.IndeterminateStructLayout, detail);
                _logger.LogWarning(
                    "Skipping frozen struct '{TypeName}' - indeterminate Buffer layout (unsizeable generic value-type stored field).",
                    structDecl.Name);
                return;
            }

            // Frozen value struct (by-value, NOT Buffer-projected) whose emitted IntPtr-word optional
            // fields place a stored field at a different byte offset than the true Swift packed layout.
            // Optional<primitive> backing is rounded up to whole 8-byte words, but Swift packs sub-word
            // optionals tighter (Bool?=1B align1, Int32?=5B align4, …); when such an optional precedes
            // another field the next field's Swift offset (packed) and C# offset (8-aligned word)
            // diverge, so a by-value cdecl pass reads that field's bytes from the wrong slot and
            // corrupts the value. Fail closed and skip rather than emit a corrupting binding (a precise
            // sub-word Buffer emitter is future work). Mirrored in TypeSkipPrePass so members passing or
            // returning the struct by value are pruned in the same pass.
            if (HasSubWordOptionalLayoutMismatch(structDecl, env.TypeDatabase))
            {
                const string detail = "frozen value struct mixes sub-word Optional<primitive> fields whose 8-byte IntPtr-word emission places a stored field at a different byte offset than the Swift packed layout; a by-value pass would read the field from the wrong slot and corrupt it.";
                ReportCollector.RecordTypeSkipped(structDecl, SkipReason.IndeterminateStructLayout, detail);
                UnsupportedCommentEmitter.EmitTypeSkipped(csWriter, structDecl.Name, SkipReason.IndeterminateStructLayout, detail);
                _logger.LogWarning(
                    "Skipping frozen struct '{TypeName}' - sub-word Optional field packing makes the by-value C# layout diverge from Swift.",
                    structDecl.Name);
                return;
            }

            // Create P/Invoke helper context for generic types (to avoid CS7042).
            // Pre-flatten conformances against the type database so the metadata-accessor
            // emitter can render the correct PWT plumbing.
            //
            // The ShouldSkip check MUST happen BEFORE RecordTypeEmitted: ReportCollector
            // suppresses RecordTypeSkipped if the type key is already in EmittedTypeKeys
            // (ReportCollector.cs:106), so a skipped frozen struct would otherwise be
            // silently miscounted as emitted.
            var ownPInvokeContext = PInvokeHelperContext.CreateIfGeneric(structDecl, env.TypeDatabase);
            if (ownPInvokeContext != null && TypeMetadataAccessorSkipGate.ShouldSkip(
                    structDecl, ownPInvokeContext, csWriter, _logger))
                return;

            ReportCollector.RecordTypeEmitted(structDecl);

            // Cross-module extension: frozen struct defined in module A, extended in module B.
            // Emit as a static extension class instead of a duplicate partial struct.
            // Mirrors ClassHandler.cs dispatch around RecordTypeEmitted — the parser only
            // surfaces foreign struct receivers when they carry extension members from the
            // current module (SwiftABIParser.HandleTypeDecl), so cross-module dispatch can
            // happen unconditionally for the module-mismatch shape.
            // Top-level cross-module receivers only. Nested types whose parent is a
            // cross-module-extension TypeDecl (e.g. structs declared inside
            // `extension ForeignModule.ForeignType { ... }`) are owned by the current
            // module and must emit normally so their members surface — re-routing them
            // through the cross-module path would skip on missing TypeDatabase entries.
            if (!string.IsNullOrEmpty(structDecl.SwiftTypeName.Module) &&
                structDecl.SwiftTypeName.Module != moduleDecl.Name &&
                structDecl.ParentDecl is ModuleDecl)
            {
                CrossModuleExtensionEmitter.Emit(
                    csWriter, swiftWriter, structDecl, moduleDecl, conductor, env, _logger,
                    context: context,
                    recurseNestedTypes: (decls, ctx) =>
                        base.HandleBaseDecl(csWriter, swiftWriter, decls, conductor, env.TypeDatabase, ctx));
                return;
            }

            // Retrieve type info from the type database
            var typeRecord = env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
            bool isProjectedAsClass = MarshallingHelpers.IsFrozenStructProjectedAsClass(typeRecord!);

            SwiftTypeInfo? swiftTypeInfo = typeRecord?.SwiftTypeInfo;

            // Get generic type parts if this is a generic type
            var typeNameWithGenerics = GenericTypeEmitter.GetTypeNameWithGenerics(structDecl, env.TypeDatabase);
            var whereClause = GenericTypeEmitter.GetWhereClause(structDecl, env.TypeDatabase);

            var ISwiftObjectMethodWriter = new ISwiftObjectMethodWriter(csWriter, env.TypeDatabase, moduleDecl, structDecl, typeNameWithGenerics, swiftWriter, context.GetEmissionContext());
            var pinvokeHelperContext = ownPInvokeContext ?? context.PInvokeHelperContext;

            // Compute property renames to resolve property/nested-type name collisions
            var propertyRenames = NameProvider.ComputePropertyRenames(structDecl, env.TypeDatabase);

            // Build child context for nested handlers
            var childContext = context with {
                PInvokeHelperContext = pinvokeHelperContext,
                PropertyRenames = propertyRenames
            };

            {
                var validatorEmissionCtx = context.GetEmissionContext();
                var extensionDefaultsIndex = validatorEmissionCtx?.ExtensionDefaultsIndex;
                var conformanceValidator = new ProtocolConformanceValidator(moduleDecl, env.TypeDatabase, extensionDefaultsIndex, validatorEmissionCtx);
                var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
                    structDecl,
                    typeNameWithGenerics,
                    moduleDecl.Name,
                    env.TypeDatabase,
                    conformanceValidator);

                // Frozen structs projected as C# classes (ref fields) — mark with ISwiftStruct
                // so the SB1001 analyzer can distinguish them from Swift classes (Warning vs Info).
                if (isProjectedAsClass)
                    interfaces.Insert(1, nameof(ISwiftStruct));

                XmlDocCommentEmitter.EmitDocComment(csWriter, structDecl);
                AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, structDecl, emitObsolete: true);
                var (opaqueEmittable, opaqueSkipped) = MemberEmissionValidator.CountEmittableMembers(structDecl, env.TypeDatabase);
                if (opaqueEmittable == 0 && opaqueSkipped > 0)
                {
                    TypeAnnotationHelper.EmitOpaqueTypeAnnotation(csWriter, opaqueSkipped);
                    context.GetEmissionContext()?.AddEmittedOpaqueType(structDecl.SwiftTypeName.ModuleQualifiedName);
                }
                else if (isProjectedAsClass)
                    TypeAnnotationHelper.EmitDisposalRemarks(csWriter, structDecl);
                TypeAnnotationHelper.EmitSwiftSendableAnnotation(csWriter, structDecl);
                if (structDecl.Name.StartsWith("_"))
                    csWriter.WriteLine("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
                if (isProjectedAsClass)
                {
                    var classDeclaration = $"public partial class {typeNameWithGenerics} : {string.Join(", ", interfaces)}";
                    if (!string.IsNullOrEmpty(whereClause))
                        classDeclaration += $" {whereClause}";
                    csWriter.WriteLine(classDeclaration);
                    csWriter.WriteLine("{");
                    csWriter.Indent++;

                    // Payload used for reference counting
                    csWriter.WriteLine("[EditorBrowsable(EditorBrowsableState.Never)]");
                    csWriter.WriteLine($"private SwiftSafeHandle<{typeNameWithGenerics}> _payload = SwiftSafeHandle<{typeNameWithGenerics}>.Zero;");
                    csWriter.WriteLine();
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

                    // VWT Destroy via CallConvSwift is proven safe on both runtimes —
                    // no @_cdecl destroy wrapper needed.
                }

                if (swiftTypeInfo.HasValue && swiftTypeInfo.Value.MetadataPtr != IntPtr.Zero)
                {
                    unsafe
                    {
                        // Apply struct layout attributes
                        csWriter.WriteLine($"[StructLayout(LayoutKind.Sequential, Size = {swiftTypeInfo.Value.ValueWitnessTable->Size})]");
                    }
                }
                if (isProjectedAsClass)
                {
                    csWriter.WriteLine($"public struct Buffer {{");
                }
                else
                {
                    var structDeclaration = $"public unsafe partial struct {typeNameWithGenerics} : {string.Join(", ", interfaces)}";
                    if (!string.IsNullOrEmpty(whereClause))
                        structDeclaration += $" {whereClause}";
                    csWriter.WriteLine(structDeclaration);
                    csWriter.WriteLine("{");
                }
                csWriter.Indent++;

                csWriter.WriteLine(@"
                // For frozen structs, we need to emit fields that match the Swift struct's memory layout exactly.
                // These backing fields are required for proper memory layout and marshalling, even though they
                // are never directly accessed from C# code. The actual value access happens through Swift's
                // accessor methods.
                //
                // Important: Direct access to these fields from C# will not provide the correct value - always
                // use the generated property accessors which call into Swift.");

                foreach (PropertyDecl propertyDecl in structDecl.Properties)
                {
                    if (!propertyDecl.HasStorage)
                        continue;

                    switch (ClassifyFrozenStructField(propertyDecl.SwiftTypeSpec, env.TypeDatabase, out int fieldByteSize))
                    {
                        case FrozenFieldLayoutKind.IntPtrFields:
                            EmitIntPtrFields(csWriter, propertyDecl.Name, fieldByteSize);
                            break;
                        case FrozenFieldLayoutKind.Indeterminate:
                            // Unreachable in normal flow: a struct carrying an indeterminate-size stored
                            // field is skipped before emission (HasIndeterminateBufferLayout, mirrored in
                            // TypeSkipPrePass). Guess-sizing the Buffer here would silently corrupt the
                            // heap, so assert the invariant loudly rather than emit a wrong-sized field.
                            throw new InvalidOperationException(
                                $"Frozen struct '{structDecl.Name}' stored field '{propertyDecl.Name}' has an " +
                                "indeterminate Buffer layout but reached field emission; it should have been skipped.");
                        default: // FrozenFieldLayoutKind.TypedField
                            var fieldRecord = env.TypeDatabase.GetTypeRecordOrThrow(propertyDecl.SwiftTypeSpec);
                            csWriter.WriteLine($"private {fieldRecord.CSharpTypeName.FullyQualifiedName} {propertyDecl.Name}_;  // Note: Do not access this field directly - use the property accessors");
                            break;
                    }
                }

                if (isProjectedAsClass)
                {
                    // Payload used for lowering at PInvoke boundary
                    csWriter.Indent -= 2;
                    csWriter.WriteLine("}");
                    csWriter.WriteLine();
                    csWriter.WriteLine($"public unsafe PayloadBuffer<{typeNameWithGenerics}.Buffer> PayloadBuffer => new PayloadBuffer<{typeNameWithGenerics}.Buffer>(_payload);");
                    csWriter.WriteLine();
                }

                var emittedPropertyNames = new HashSet<string>();
                foreach (PropertyDecl propertyDecl in structDecl.Properties)
                {
                    // Use post-rename name for consistency with the propertyNames collision set below.
                    var csPropertyName = NameProvider.GetFinalMemberName(
                        NameProvider.GetPropertyName(propertyDecl.Name, structDecl.Name), propertyRenames);
                    if (!emittedPropertyNames.Add(csPropertyName))
                    {
                        _logger.LogInformation($"Skipping duplicate property '{structDecl.Name}.{csPropertyName}'.");
                        ReportCollector.RecordMemberSkipped(propertyDecl, SkipReason.DuplicateSignature, $"Property '{csPropertyName}' already emitted.");
                        continue;
                    }

                    if (MemberEmissionValidator.IsSynthesizedProtocolProperty(propertyDecl, structDecl))
                    {
                        ReportCollector.RecordMemberSynthesized(propertyDecl);
                        continue;
                    }

                    var skipReason = MemberEmissionValidator.CanEmitProperty(propertyDecl, env.TypeDatabase, out var skipDetails, out _);
                    if (skipReason != null)
                    {
                        ReportCollector.RecordMemberSkipped(propertyDecl, skipReason.Value, skipDetails ?? "");
                        // Mirror PropertyHandler's SkipProperty: leave a `// Unsupported:` tombstone
                        // so consumers can grep the file. The outer gate skips PropertyHandler.Emit
                        // entirely, so this is the only place the omission can be made visible.
                        UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, propertyDecl.Name, BindingItemKind.Property, skipReason.Value, skipDetails);
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
                csWriter.WriteLine();

                // Emit operators
                // For Equatable types with @_cdecl wrapper support, skip == and != operators
                // here so EqualityMethodsWriter emits them with CallConvCdecl instead of
                // CallConvSwift (which crashes on NativeAOT with non-blittable SafeHandle params).
                var operatorHandler = new OperatorHandler(_logger);
                var emittedOperatorSymbols = new HashSet<string>();
                bool hasEquatableConformance = structDecl.Conformances.Any(c => c.Protocol.Name == "Equatable");
                bool hasCdeclWrapperSupport = context.GetEmissionContext() != null &&
                    env.TypeDatabase.AsyncLibraryName != null &&
                    structDecl.GenericParameters.Count == 0;
                bool deferEqualityToWrapper = hasEquatableConformance && hasCdeclWrapperSupport;
                foreach (var operatorDecl in structDecl.Operators)
                {
                    if (OperatorHandler.IsSupportedOperator(operatorDecl.OperatorSymbol))
                    {
                        // Skip == and != when @_cdecl equality wrapper will handle them
                        if (deferEqualityToWrapper &&
                            (operatorDecl.OperatorSymbol == "==" || operatorDecl.OperatorSymbol == "!="))
                            continue;
                        if (operatorHandler.EmitOperator(csWriter, operatorDecl, env.TypeDatabase, pinvokeHelperContext,
                            swiftWriter: swiftWriter, emissionContext: context.GetEmissionContext()))
                        {
                            emittedOperatorSymbols.Add(operatorDecl.OperatorSymbol);
                        }
                    }
                    else
                    {
                        ReportCollector.RecordMemberSkipped(operatorDecl, SkipReason.UnsupportedType, $"Operator '{operatorDecl.OperatorSymbol}' has no C# equivalent.");
                    }
                }
                // Handle paired operators (e.g., if == is defined but != is not)
                // Use typeNameWithGenerics to ensure generic types have proper type parameters in operator signatures
                operatorHandler.ValidateAndEmitPairs(csWriter, structDecl.Operators, typeNameWithGenerics, emittedOperatorSymbols, isProjectedAsClass);

                bool hasEquality = emittedOperatorSymbols.Contains("==");
                bool hasInequality = emittedOperatorSymbols.Contains("!=");

                // Add Equatable support if the struct conforms to Equatable.
                // Pass SwiftWriter + context for @_cdecl equality wrapper (avoids CallConvSwift crash).
                var SwiftEquatableMethodWriter = new EqualityMethodsWriter(csWriter, structDecl, isProjectedAsClass, typeNameWithGenerics, hasEquality, hasInequality, swiftWriter, context.GetEmissionContext(), env.TypeDatabase.AsyncLibraryName, env.TypeDatabase);
                SwiftEquatableMethodWriter.WriteSwiftEquatableImplementation();
                ISwiftObjectMethodWriter.WriteFrozenStructImplementation(pinvokeHelperContext, isProjectedAsClass, emitBoxable: interfaces.Contains("Swift.Runtime.IExistentialBoxable"));

                ToStringHelper.EmitToStringIfDescriptionExists(csWriter, structDecl, propertyRenames);

                // Collect property names (post-rename) for method/property collision detection
                var propertyNames = new HashSet<string>(structDecl.Properties.Select(p =>
                    NameProvider.GetFinalMemberName(
                        NameProvider.GetPropertyName(p.Name, structDecl.Name), propertyRenames)));
                // Nested type names collide with method names in C# (CS0102)
                foreach (var nestedType in structDecl.Types)
                    propertyNames.Add(NameProvider.ToPascalCase(nestedType.Name));

                SubscriptHandler.EmitSubscripts(csWriter, swiftWriter, structDecl, env.TypeDatabase, conductor, childContext, _logger);

                var emissionCtx = context.GetEmissionContext();
                emissionCtx?.PushTypeNesting(typeNameWithGenerics);
                base.HandleBaseDecl(csWriter, swiftWriter, structDecl.Types, conductor, env.TypeDatabase, childContext);
                emissionCtx?.PopTypeNesting();
                base.HandleBaseDecl(csWriter, swiftWriter, structDecl.Methods, conductor, env.TypeDatabase, childContext, propertyNames);

                // Emit concrete protocol specializations (e.g., func hash<D: DataProtocol>(data: D))
                // Must be inside the class body — these emit instance/static methods.
                var specEngine = context.GetEmissionContext().SpecializationEngine;
                if (specEngine != null)
                {
                    ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializations(
                        csWriter, swiftWriter, structDecl,
                        env.TypeDatabase, context.GetEmissionContext(), specEngine, _logger);
                }

                // AsyncSequence → IAsyncEnumerable<T>: emit GetAsyncEnumerator that
                // adapts the Swift iterator's NextAsync(ct) → Task<T?> to
                // IAsyncEnumerator<T>. Interface adoption is added by GetImplementedInterfaces.
                AsyncSequenceEmitter.TryEmitAsyncEnumerableBridge(csWriter, structDecl, env.TypeDatabase);

                // Codable JSON round-trip is intentionally NOT emitted here. FrozenStructHandler
                // covers ClassWithBufferStruct projections, which expose `_payload` + `PayloadBuffer<Buffer>`
                // but NOT the `_payloadSize`/`NewFromPayloadCore` primitives the JSON decoder factory
                // relies on. JSON is only emitted for the ClassWithOpaquePayload pattern emitted
                // by NonFrozenStructHandler; ClassWithBufferStruct support is tracked separately.

                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine();

                // Emit constrained-extension specialization classes (e.g., extension X where T == Concrete)
                ConstrainedExtensionEmitter.EmitConstrainedExtensions(
                    csWriter, swiftWriter, structDecl,
                    env.TypeDatabase, context.GetEmissionContext(), _logger);

                // Generic-parent CSM: per-parent-conformer static extension classes.
                // Must live outside the parent's body so the receiver can close over the generic.
                if (specEngine != null)
                {
                    ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializationsForGenericParent(
                        csWriter, swiftWriter, structDecl,
                        env.TypeDatabase, context.GetEmissionContext(), specEngine, _logger);

                    // Typed KeyPath singleton trampolines for closed conformers.
                    // Frozen generic structs reach this handler whenever their generic
                    // parameters lift them out of the value-type path; singletons emit
                    // at the same window as CSM.
                    KeyPathSingletonEmitter.EmitKeyPathSingletonsForGenericParent(
                        csWriter, swiftWriter, structDecl,
                        env.TypeDatabase, context.GetEmissionContext(), specEngine, _logger);

                    // Per-V Sort overloads for unconstrained-V keypath-sort methods on
                    // a frozen struct generic parent. The emitter branches receiver-kind
                    // internally; mutating methods get the `var __self` + pointee
                    // write-back pattern.
                    KeyPathBagValueSpecializationEmitter.EmitRouteCSpecializationsForGenericParent(
                        csWriter, swiftWriter, structDecl,
                        env.TypeDatabase, context.GetEmissionContext(), specEngine, _logger);
                }

                // Emit P/Invoke helper class(es) after the main struct.
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
        /// How a frozen struct's stored field is laid out in the blitted Buffer.
        /// </summary>
        internal enum FrozenFieldLayoutKind
        {
            /// <summary>Emit a typed C# backing field (blittable value type).</summary>
            TypedField,
            /// <summary>Emit one or more IntPtr backing words sized by <c>byteSize</c>.</summary>
            IntPtrFields,
            /// <summary>
            /// The field's inline byte size is not derivable cross-compile (a generic value-type
            /// instantiation with no persisted size and no live metadata). The containing struct
            /// must fail closed and be skipped rather than emit a guessed Buffer layout.
            /// </summary>
            Indeterminate,
        }

        /// <summary>
        /// Classifies how a frozen struct's stored field is emitted into the blitted Buffer, and —
        /// for reference-managed fields — resolves the exact inline byte size that the Buffer must
        /// reserve. Shared by the field-emission loop and <see cref="HasIndeterminateBufferLayout"/>
        /// so the skip decision and the emitted layout can never drift apart.
        /// </summary>
        internal static FrozenFieldLayoutKind ClassifyFrozenStructField(
            TypeSpec fieldTypeSpec, ITypeDatabase typeDatabase, out int byteSize)
        {
            byteSize = IntPtr.Size;

            // Optional<T> stored field. The generic Swift.Optional TypeRecord has no concrete
            // InlineSize (it varies by T), so resolve the inner type's size directly. Some
            // registrations mark Optional with RequiresMemoryManagement, others don't (enum kind) —
            // either way Optional<T> in a frozen struct Buffer needs IntPtr-based emission.
            if (TryComputeOptionalInlineSize(fieldTypeSpec, typeDatabase, out byteSize, out bool optionalIndeterminate))
                return FrozenFieldLayoutKind.IntPtrFields;
            if (optionalIndeterminate)
                return FrozenFieldLayoutKind.Indeterminate;

            // Direct (non-optional) stored field.
            if (!typeDatabase.TryGetTypeRecord(fieldTypeSpec, out var fieldRecord))
                return FrozenFieldLayoutKind.TypedField; // unresolved → fall back to a typed C# field (existing behavior)

            if ((fieldRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0)
            {
                // Reference-managed field (Swift.String is 16 bytes / 2 words but was once mapped to
                // a single IntPtr, causing heap overflow and SIGSEGV). Resolve the true inline size;
                // a per-instantiation generic value type that can't be sized fails closed.
                if (TryResolveReferenceFieldSize(fieldRecord, fieldTypeSpec, out byteSize))
                    return FrozenFieldLayoutKind.IntPtrFields;
                return FrozenFieldLayoutKind.Indeterminate;
            }

            return FrozenFieldLayoutKind.TypedField;
        }

        /// <summary>
        /// True when <paramref name="structDecl"/> is projected as a Buffer-backed class
        /// (<see cref="MarshallingHelpers.IsFrozenStructProjectedAsClass"/>) and at least one of its
        /// stored fields has an inline size that cannot be derived cross-compile. Such a struct must
        /// be skipped — emitting a guessed Buffer layout mis-sizes the blit and corrupts the heap.
        /// Mirrored as a skip condition in <see cref="TypeSkipPrePass"/> so members that pass or
        /// return the struct by value are pruned in the same pass.
        /// </summary>
        internal static bool HasIndeterminateBufferLayout(StructDecl structDecl, ITypeDatabase typeDatabase)
        {
            if (!typeDatabase.TryGetTypeRecord(structDecl.SwiftTypeName, out var typeRecord) ||
                !MarshallingHelpers.IsFrozenStructProjectedAsClass(typeRecord))
                return false;

            foreach (var propertyDecl in structDecl.Properties)
            {
                if (!propertyDecl.HasStorage)
                    continue;
                if (ClassifyFrozenStructField(propertyDecl.SwiftTypeSpec, typeDatabase, out _) == FrozenFieldLayoutKind.Indeterminate)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// True when <paramref name="structDecl"/> is a frozen struct projected as a BY-VALUE C# struct
        /// (NOT <see cref="MarshallingHelpers.IsFrozenStructProjectedAsClass"/>) whose emitted backing
        /// fields place at least one stored field at a different byte offset than the true Swift layout.
        ///
        /// <para><c>Optional&lt;primitive&gt;</c> fields are emitted as whole 8-byte <c>IntPtr</c> words
        /// (<see cref="EmitIntPtrFields"/>), but Swift packs many optionals tighter than their emitted
        /// word. Two distinct ways an optional over-pads: (a) sub-word optionals (Bool?=1B align1,
        /// Int8?=2B, Int16?=3B align2, Int32?/Float?=5B align4, …); and (b) tag-extended whole-word VALUE
        /// optionals (Int?/Int64?/UInt64?/Double?=9B align8 — the payload uses every bit, so Swift appends
        /// a separate tag byte and rounds to a 16-byte stride, while C# emits two IntPtr words = 16B at
        /// offset granularity 8). In BOTH cases the Swift inline size (9 or sub-word) is smaller than the
        /// emitted IntPtr backing (8 or 16). When such an over-padded optional precedes another stored
        /// field, the next field's Swift offset (packed) and C# offset (word-aligned) diverge, so a
        /// by-value cdecl pass reads that field's bytes from the wrong register/stack slot and corrupts the
        /// value. We fail closed and skip rather than emit a corrupting binding.</para>
        ///
        /// <para>Detection simulates BOTH layouts field-by-field — a count of over-padded optionals is
        /// neither necessary nor sufficient: a lone Int32? and two adjacent Int32? both lay out
        /// identically in C# and Swift, while Bool?+Int32?, Int?+Int32?, and Int64?+Int8 diverge. Only
        /// per-field START OFFSET divergence is treated as a defect: it is unconditionally corrupting (a
        /// field's bytes land in the wrong slot) independent of ABI register-padding. A pure trailing-stride
        /// difference with all offsets equal is absorbed by the ≤16-byte register classification (and by the
        /// emitted <c>[StructLayout(Size=…)]</c> when live metadata is present), so it is intentionally NOT
        /// a skip trigger — that would over-suppress 1–3 byte single-optional structs that pass correctly.
        /// The over-pad signal is <c>csSize != swiftSize</c> (NOT alignment), so a whole-word value optional
        /// such as <c>Int64?</c> that pushes a following field is caught; only reference-width optionals
        /// (<c>String?</c>/<c>class?</c> — exactly one 8-byte word, no tag byte, <c>csSize == swiftSize</c>)
        /// and typed-only structs are unaffected. Bails (no skip) on any field whose layout is not precisely
        /// derivable, preserving existing behavior. Mirrored in <see cref="TypeSkipPrePass"/>. (Method name
        /// retains the historical "SubWord" label; it now covers every over-padded optional.)</para>
        /// </summary>
        internal static bool HasSubWordOptionalLayoutMismatch(StructDecl structDecl, ITypeDatabase typeDatabase)
        {
            // By-value risk applies ONLY to a struct projected BY VALUE — a FROZEN struct (the decl
            // carries the @frozen marker, exactly the gate FrozenStructHandlerFactory.Handles keys off).
            // A NON-frozen struct is projected as ClassWithOpaquePayload (an opaque SafeHandle that is
            // pointer-passed and filled by Swift accessors) and never lowers through a by-value ABI, so
            // sub-word packing cannot corrupt it. The original guard only excluded the Buffer-class case
            // below, so a non-frozen struct with sub-word Optional<primitive> fields (e.g. two `Bool?`
            // stored properties) wrongly satisfied the gate and was added to the TypeSkipPrePass skip set
            // that ReferencesUnsupportedModule consults — silently dropping the struct's own constructor
            // and static factories even though the type itself still emits.
            if (!structDecl.IsFrozen)
                return false;

            // A frozen struct with ref-type fields is projected as a Buffer-backed class
            // (ClassWithBufferStruct): pointer-passed as an opaque Buffer Swift fills via accessors, so
            // its over-sized Buffer likewise never lowers through a by-value ABI.
            if (typeDatabase.TryGetTypeRecord(structDecl.SwiftTypeName, out var typeRecord) &&
                MarshallingHelpers.IsFrozenStructProjectedAsClass(typeRecord))
                return false;

            int swiftCursor = 0, csCursor = 0;
            bool anyOverPaddedOptional = false;
            bool offsetDiverged = false;
            foreach (PropertyDecl propertyDecl in structDecl.Properties)
            {
                if (!propertyDecl.HasStorage)
                    continue;
                if (!TryGetFrozenFieldLayout(propertyDecl.SwiftTypeSpec, typeDatabase,
                        out int swiftSize, out int swiftAlign, out int csSize, out int csAlign,
                        out bool isOverPaddedOptional))
                    return false; // layout not precisely derivable → preserve existing behavior (no skip)

                anyOverPaddedOptional |= isOverPaddedOptional;

                int swiftOffset = AlignUp(swiftCursor, swiftAlign);
                int csOffset = AlignUp(csCursor, csAlign);
                if (swiftOffset != csOffset)
                    offsetDiverged = true;

                swiftCursor = swiftOffset + swiftSize;
                csCursor = csOffset + csSize;
            }

            // Fire only when an over-padded optional actually participates AND a field offset diverges;
            // offset divergence cannot arise without an over-padded optional, so the gate is a guard rail.
            return anyOverPaddedOptional && offsetDiverged;
        }

        /// <summary>
        /// Resolves the Swift and emitted-C# inline (size, alignment) of a frozen struct stored field
        /// for <see cref="HasSubWordOptionalLayoutMismatch"/>. Returns false when the field's layout is
        /// not precisely derivable (an indeterminate-size field, or a non-primitive typed field such as a
        /// nested value struct), so the caller can preserve existing behavior rather than guess.
        /// </summary>
        private static bool TryGetFrozenFieldLayout(
            TypeSpec fieldTypeSpec, ITypeDatabase typeDatabase,
            out int swiftSize, out int swiftAlign, out int csSize, out int csAlign, out bool isOverPaddedOptional)
        {
            swiftSize = swiftAlign = csSize = csAlign = 0;
            isOverPaddedOptional = false;

            switch (ClassifyFrozenStructField(fieldTypeSpec, typeDatabase, out int byteSize))
            {
                case FrozenFieldLayoutKind.IntPtrFields:
                    // Optional<T> / reference-managed field emitted as whole 8-byte IntPtr words.
                    swiftSize = byteSize;
                    csSize = IntPtr.Size * ((byteSize + IntPtr.Size - 1) / IntPtr.Size);
                    csAlign = IntPtr.Size;
                    if (!TryGetSwiftFieldAlignment(fieldTypeSpec, typeDatabase, out swiftAlign))
                        return false;
                    // Over-pad = the emitted IntPtr backing is larger than the Swift inline size — the
                    // sole way C#/Swift field offsets can diverge. This catches BOTH sub-word optionals
                    // (Bool?/Int32?, align<8) AND tag-extended whole-word value optionals (Int64?/Int?/
                    // Double?=9B → 16B word). A reference-width optional (String?/class?=8B) has
                    // csSize==swiftSize and never shifts a following field, so it is correctly excluded.
                    isOverPaddedOptional = csSize != swiftSize;
                    return true;

                case FrozenFieldLayoutKind.TypedField:
                    // Only fixed-width primitives have a known C#/Swift-matching layout (size == align).
                    // A non-primitive typed field (nested value struct, etc.) is not analyzable → bail.
                    if (!TryGetFixedWidthPrimitiveSize(fieldTypeSpec, out int primSize))
                        return false;
                    swiftSize = swiftAlign = csSize = csAlign = primSize;
                    return true;

                default: // Indeterminate — handled by HasIndeterminateBufferLayout; do not analyze here.
                    return false;
            }
        }

        /// <summary>
        /// Resolves a frozen struct stored field's Swift inline alignment: Optional&lt;T&gt; aligns to
        /// T's alignment (a fixed-width primitive aligns to its own size; a reference/class type is
        /// pointer-aligned). Returns false for any inner type whose alignment is not known here.
        /// </summary>
        private static bool TryGetSwiftFieldAlignment(TypeSpec fieldTypeSpec, ITypeDatabase typeDatabase, out int align)
        {
            align = IntPtr.Size;
            var inner = fieldTypeSpec;
            if (fieldTypeSpec is NamedTypeSpec opt &&
                opt.Name == "Swift.Optional" &&
                opt.GenericParameters.Count == 1)
                inner = opt.GenericParameters[0];

            // Fixed-width primitive: Swift alignment == its size (Bool=1, Int16=2, Int32=4, Int64/Double=8).
            if (TryGetFixedWidthPrimitiveSize(inner, out int primSize))
            {
                align = primSize;
                return true;
            }
            // Reference-managed / class inner: pointer-aligned.
            if (typeDatabase.TryGetTypeRecord(inner, out var rec) &&
                ((rec.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0 || rec.Kind == TypeRecordKind.Class))
            {
                align = IntPtr.Size;
                return true;
            }
            return false; // unknown alignment → caller bails (no skip)
        }

        private static int AlignUp(int value, int alignment)
            => alignment <= 1 ? value : (value + alignment - 1) / alignment * alignment;

        /// <summary>
        /// Computes the inline size of Optional&lt;T&gt; for frozen struct Buffer fields.
        /// - If T has extra inhabitants (String, classes, arrays, Bool): Optional&lt;T&gt;.size == T.size
        /// - If T has no extra inhabitants (Int32, Double): Optional&lt;T&gt;.size == T.size + 1
        /// Returns false when <paramref name="fieldTypeSpec"/> is not an Optional. When it IS an
        /// Optional whose inner size cannot be derived (a generic value-type instantiation with no
        /// persisted size and no live metadata, e.g. ClosedRange&lt;Int&gt;), returns false with
        /// <paramref name="indeterminate"/> set — the caller must then fail closed.
        /// </summary>
        private static bool TryComputeOptionalInlineSize(TypeSpec fieldTypeSpec, ITypeDatabase typeDatabase, out int optionalSize, out bool indeterminate)
        {
            optionalSize = IntPtr.Size;
            indeterminate = false;

            if (fieldTypeSpec is not NamedTypeSpec optionalSpec ||
                optionalSpec.Name != "Swift.Optional" ||
                optionalSpec.GenericParameters.Count != 1)
                return false;

            var innerTypeSpec = optionalSpec.GenericParameters[0];

            // A fixed-width primitive inner (Int32/Bool/Double/...) has a language-constant Optional
            // size that the cross-compile TypeDatabase does not persist (the XML primitive records
            // carry no inlineSize) and for which no live metadata exists at generate time. Resolve it
            // from the primitive table directly; otherwise TryResolveReferenceFieldSize would clamp the
            // inner to a pointer width and Optional<Int32> would be sized as two words instead of one.
            // This also wins over the live-metadata branch below for Bool, whose extra-inhabitant
            // behaviour the metadata would report but which is absent cross-compile.
            if (TryGetOptionalPrimitiveInlineSize(innerTypeSpec, out optionalSize))
                return true;

            if (!typeDatabase.TryGetTypeRecord(innerTypeSpec, out var innerRecord))
            {
                // Optional<T> where T can't be resolved at all — the Buffer field can't be sized.
                indeterminate = true;
                return false;
            }

            if (!TryResolveReferenceFieldSize(innerRecord, innerTypeSpec, out int innerSize))
            {
                indeterminate = true;
                return false;
            }

            // Determine if the inner type has extra inhabitants the optional tag can reuse.
            bool hasExtraInhabitants;
            if (innerRecord.SwiftTypeInfo.HasValue && innerRecord.SwiftTypeInfo.Value.MetadataPtr != IntPtr.Zero)
            {
                unsafe { hasExtraInhabitants = innerRecord.SwiftTypeInfo.Value.ValueWitnessTable->HasExtraInhabitants; }
            }
            else
            {
                // Heuristic: reference-bearing types (String, classes, arrays) contain pointers whose
                // spare bit patterns the optional tag reuses → Optional<T>.size == T.size.
                hasExtraInhabitants = (innerRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0
                                      || innerRecord.Kind == TypeRecordKind.Class;
            }

            optionalSize = hasExtraInhabitants ? innerSize : innerSize + 1;
            return true;
        }

        /// <summary>
        /// Resolves the inline byte size of an <c>Optional&lt;primitive&gt;</c> Buffer field as a
        /// language constant, or returns false for any non-primitive inner (the caller then resolves it
        /// via InlineSize / live metadata / reference-field rules). Most fixed-width primitives use
        /// their full bit range, so they expose no spare pattern for the optional tag and carry a
        /// separate tag byte: <c>Optional&lt;T&gt;.size == T.size + 1</c> (verified Int8?=2, Int16?=3,
        /// Int32?=5, Int?=9, Float?=5, Double?=9). <c>Bool</c> is the lone exception in this set: only
        /// the bit patterns 0 and 1 are valid, so <c>Optional&lt;Bool&gt;</c> reuses a spare pattern for
        /// nil and <c>Optional&lt;Bool&gt;.size == Bool.size == 1</c> (no tag byte).
        ///
        /// The sole present consumer (<see cref="EmitIntPtrFields"/>) rounds every field up to whole
        /// 8-byte words, so the only size distinction it observes is the 8-byte-primitive case
        /// (<c>Int?</c>/<c>Int64?</c>/<c>Double?</c>/<c>CGFloat?</c> = 9 bytes ⇒ two words, vs the
        /// historical one-word clamp that under-sized the Buffer). The sub-word Bool/Int8/…/Float
        /// distinctions are therefore presently masked by that rounding — this helper still reports the
        /// true Swift layout size so the value is correct for any future precision-dependent consumer
        /// and so the Bool extra-inhabitant exception is not silently wrong.
        /// </summary>
        internal static bool TryGetOptionalPrimitiveInlineSize(TypeSpec innerSpec, out int optionalSize)
        {
            optionalSize = 0;
            if (!TryGetFixedWidthPrimitiveSize(innerSpec, out int innerSize))
                return false;

            bool hasExtraInhabitants = innerSpec is NamedTypeSpec named &&
                                       (named.Name == "Swift.Bool" || named.Name == "Bool");
            optionalSize = hasExtraInhabitants ? innerSize : innerSize + 1;
            return true;
        }

        /// <summary>
        /// Resolves the inline byte size of a fixed-width Swift primitive value type (Int32, Bool,
        /// Double, Int, CGFloat, …). These sizes are language constants that the cross-compile
        /// TypeDatabase does not persist (the XML primitive records carry no <c>inlineSize</c>) and
        /// for which no live metadata exists at generate time, yet they are needed to size
        /// <c>Optional&lt;primitive&gt;</c> Buffer fields: without this the reference-field fallback
        /// clamps the inner type to a pointer width and <c>Optional&lt;Int32&gt;</c> is mis-sized to
        /// two words. Returns false for any non-primitive (the caller resolves those via
        /// InlineSize/metadata/reference-field rules). Delegates to the single source of truth in
        /// <see cref="OptionalMarshalClassifier.GetSwiftTagByteOffset"/> (the tag byte offset of an
        /// Optional&lt;primitive&gt; is exactly the primitive's size).
        /// </summary>
        internal static bool TryGetFixedWidthPrimitiveSize(TypeSpec spec, out int byteSize)
        {
            byteSize = 0;
            if (spec is NamedTypeSpec named &&
                OptionalMarshalClassifier.GetSwiftTagByteOffset(named.Name) is int size)
            {
                byteSize = size;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Resolves the inline byte size a reference-managed type occupies when stored inline in a
        /// frozen struct's blitted Buffer. Returns false (indeterminate) when the size is a
        /// per-instantiation property of a generic value type that the cross-compile TypeDatabase
        /// cannot derive: <see cref="SwiftTypeName.FromTypeSpec"/> strips the generic arguments so the
        /// bare record carries no <see cref="TypeRecord.InlineSize"/>, the iOS/device slice exposes no
        /// live metadata accessor, yet the true size depends on the arguments
        /// (MemoryLayout&lt;ClosedRange&lt;Int&gt;&gt; = 16 vs &lt;ClosedRange&lt;Float&gt;&gt; = 8).
        /// Guessing a word would mis-size the Buffer and corrupt the heap, so such fields fail closed.
        /// A class reference is always one pointer regardless of generic arguments, and a non-generic
        /// reference-managed value type keeps the historical single-pointer assumption (Array/Set/
        /// Dictionary already carry InlineSize), so neither regresses.
        /// </summary>
        internal static bool TryResolveReferenceFieldSize(TypeRecord record, TypeSpec spec, out int byteSize)
        {
            byteSize = IntPtr.Size;

            if (record.InlineSize.HasValue)
            {
                byteSize = record.InlineSize.Value;
                return true;
            }
            if (record.SwiftTypeInfo.HasValue && record.SwiftTypeInfo.Value.MetadataPtr != IntPtr.Zero)
            {
                unsafe { byteSize = (int)record.SwiftTypeInfo.Value.ValueWitnessTable->Size; }
                return true;
            }
            // A class reference is exactly one pointer regardless of its generic arguments.
            if (record.Kind == TypeRecordKind.Class)
            {
                byteSize = IntPtr.Size;
                return true;
            }
            // No persisted size, no live metadata, not a class. A generic value-type instantiation's
            // size is not derivable here → fail closed. Non-generic reference-managed value types keep
            // the historical single-pointer clamp (preserves behavior; no per-instantiation ambiguity).
            if (spec.ContainsGenericParameters)
                return false;

            return true; // byteSize stays IntPtr.Size — unchanged clamp for non-generic types
        }

        /// <summary>
        /// Emits IntPtr-based backing fields for a frozen struct Buffer field.
        /// Fields larger than one pointer word get numbered suffixes (_0_, _1_, etc.).
        /// </summary>
        private static void EmitIntPtrFields(CSharpWriter csWriter, string fieldName, int fieldSize)
        {
            int wordCount = (fieldSize + IntPtr.Size - 1) / IntPtr.Size;
            if (wordCount <= 1)
            {
                csWriter.WriteLine($"private IntPtr {fieldName}_;  // Note: Do not access this field directly - use the property accessors");
            }
            else
            {
                for (int i = 0; i < wordCount; i++)
                {
                    csWriter.WriteLine($"private IntPtr {fieldName}_{i}_;  // Note: Do not access this field directly - use the property accessors");
                }
            }
        }
    }
}
