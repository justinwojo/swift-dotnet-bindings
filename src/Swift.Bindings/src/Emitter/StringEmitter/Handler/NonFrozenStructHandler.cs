// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using Microsoft.Extensions.Logging;
using Swift.Runtime;

namespace BindingsGeneration
{
    /// <summary>
    /// Factory class for creating instances of NonFrozenStructHandler.
    /// </summary>
    public class NonFrozenStructHandlerFactory : HandlerFactory, IFactory<BaseDecl, ITypeHandler>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="NonFrozenStructHandlerFactory"/> class.
        /// </summary>
        /// <param name="loggerFactory">The logger factory instance.</param>
        public NonFrozenStructHandlerFactory(ILoggerFactory loggerFactory) : base(loggerFactory.CreateLogger<NonFrozenStructHandler>())
        {
        }

        /// <summary>
        /// Determines if the factory handles the specified declaration.
        /// </summary>
        /// <param name="decl">The base declaration.</param>
        public bool Handles(BaseDecl decl)
        {
            return decl is StructDecl structDecl && !structDecl.IsFrozen;
        }

        /// <summary>
        /// Constructs a new instance of StructHandler.
        /// </summary>
        public ITypeHandler Construct()
        {
            return new NonFrozenStructHandler(_handlerLogger);
        }
    }

    /// <summary>
    /// Handler class for non-frozen struct declarations.
    /// </summary>
    public class NonFrozenStructHandler : BaseHandler, ITypeHandler
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="NonFrozenStructHandler"/> class.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        public NonFrozenStructHandler(ILogger logger) : base(logger)
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

            // Create P/Invoke helper context for generic types (to avoid CS7042).
            // Pre-flatten conformances against the type database so the metadata-accessor
            // emitter can render the correct PWT plumbing.
            //
            // The ShouldSkip check MUST happen BEFORE RecordTypeEmitted: ReportCollector
            // suppresses RecordTypeSkipped if the type key is already in EmittedTypeKeys
            // (ReportCollector.cs:106), so a skipped non-frozen struct would otherwise be
            // silently miscounted as emitted.
            var ownPInvokeContext = PInvokeHelperContext.CreateIfGeneric(structDecl, env.TypeDatabase);
            if (ownPInvokeContext != null && TypeMetadataAccessorSkipGate.ShouldSkip(
                    structDecl, ownPInvokeContext, csWriter, _logger))
                return;

            ReportCollector.RecordTypeEmitted(structDecl);

            // Cross-module extension: foreign struct extended by the current module. The
            // parser surfaces these with SwiftTypeName.Module set to the canonical owner
            // (e.g. Swift.Array or Foundation.Date extended by a third-party module).
            // Cross-module extension receivers come through here — not
            // FrozenStructHandler — because the extension node itself doesn't carry
            // @frozen, so the parser builds StructDecl.IsFrozen = false even when the
            // canonical owner struct is @frozen. Without this guard, the wrapper class
            // below is emitted into the current module's namespace, colliding with the
            // canonical type's projection (e.g., a duplicate `SwiftArray` class shadowing
            // the stdlib projection). CrossModuleExtensionEmitter.EmitStruct
            // gates on the canonical TypeRecord's Frozen flag and emits a static extension
            // class or skips cleanly when the receiver isn't a frozen value struct.
            // Top-level cross-module receivers only — nested types inside a cross-module
            // extension are owned by the current module and emit through the normal path.
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

                // Non-frozen structs are projected as C# classes — mark with ISwiftStruct
                // so the SB1001 analyzer can distinguish them from Swift classes (Warning vs Info).
                interfaces.Insert(1, nameof(ISwiftStruct));

                // Decide up-front whether the Collection-with-metadata projection will fire,
                // so we can add IReadOnlyList<TElement> to the interface list before the header
                // is emitted. The actual member emission happens after property emission below.
                string? collectionProjectionInterface = CollectionProjectionEmitter.TryPlanInterface(
                    structDecl, env.TypeDatabase);
                if (collectionProjectionInterface is not null)
                    interfaces.Add(collectionProjectionInterface);

                XmlDocCommentEmitter.EmitDocComment(csWriter, structDecl);
                AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, structDecl, emitObsolete: true);
                var (opaqueEmittable, opaqueSkipped) = MemberEmissionValidator.CountEmittableMembers(structDecl, env.TypeDatabase);
                if (opaqueEmittable == 0 && opaqueSkipped > 0)
                {
                    TypeAnnotationHelper.EmitOpaqueTypeAnnotation(csWriter, opaqueSkipped);
                    context.GetEmissionContext()?.AddEmittedOpaqueType(structDecl.SwiftTypeName.ModuleQualifiedName);
                }
                else
                    TypeAnnotationHelper.EmitDisposalRemarks(csWriter, structDecl);
                TypeAnnotationHelper.EmitSwiftSendableAnnotation(csWriter, structDecl);
                TypeAnnotationHelper.EmitSwiftMainActorAnnotation(csWriter, structDecl);
                if (structDecl.Name.StartsWith("_"))
                    csWriter.WriteLine("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
                var classDeclaration = $"public partial class {typeNameWithGenerics} : {string.Join(", ", interfaces)}";
                if (!string.IsNullOrEmpty(whereClause))
                    classDeclaration += $" {whereClause}";
                csWriter.WriteLine(classDeclaration);
                csWriter.WriteLine("{");
                csWriter.Indent++;

                // `seenPropertyNames` is the duplicate-collision detector — once a name has been
                // visited (whether it ended up emitted, synthesized away, or skipped) further
                // PropertyDecl entries with the same C# name are dropped to keep the iteration
                // idempotent.
                //
                // `actuallyEmittedPropertyNames` is the set we hand to downstream emitters that
                // need to know which member names actually surfaced on the class — synthesized
                // and skipped properties are excluded so a downstream dedup (Collection
                // projection's `Count`) doesn't suppress its own member when no real property
                // was emitted to take its place. Without this split a `count: Int` that fails
                // CanEmitProperty would silently strip both the property and the projection's
                // `Count`, leaving `IReadOnlyList<TElement>.Count` unimplemented (CS0535).
                var seenPropertyNames = new HashSet<string>();
                var actuallyEmittedPropertyNames = new HashSet<string>();
                foreach (PropertyDecl propertyDecl in structDecl.Properties)
                {
                    // Use post-rename name for consistency with the propertyNames collision set below.
                    var csPropertyName = NameProvider.GetFinalMemberName(
                        NameProvider.GetPropertyName(propertyDecl.Name, structDecl.Name), propertyRenames);
                    if (!seenPropertyNames.Add(csPropertyName))
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
                        // Emit a `// Unsupported:` tombstone so consumers can grep the generated file
                        // and see *why* the property is missing. Mirrors the SkipProperty pattern in
                        // PropertyHandler.Emit — the outer gate here pre-empts that path, so without
                        // this the omission is silent.
                        UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, propertyDecl.Name, BindingItemKind.Property, skipReason.Value, skipDetails, containingDecl: propertyDecl.ParentDecl);
                        continue;
                    }

                    if (conductor.TryGetPropertyHandler(propertyDecl, out var propertyHandler))
                    {
                        var propertyEnv = propertyHandler.Marshal(propertyDecl, env.TypeDatabase);
                        propertyHandler.Emit(csWriter, swiftWriter, propertyEnv, conductor, childContext);
                        actuallyEmittedPropertyNames.Add(csPropertyName);
                    }
                    else
                        _logger.LogWarning($"No handler found for field {propertyDecl.Name}");
                }

                WritePrivateFields(csWriter, typeNameWithGenerics, ownPInvokeContext);
                WritePayload(csWriter, typeNameWithGenerics);

                // VWT Destroy via CallConvSwift is proven safe on both runtimes —
                // no @_cdecl destroy wrapper needed.

                // Emit operators (operators also have P/Invoke - need to handle for generic types)
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
                        if (operatorHandler.EmitOperator(csWriter, operatorDecl, env.TypeDatabase, pinvokeHelperContext))
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
                operatorHandler.ValidateAndEmitPairs(csWriter, structDecl.Operators, typeNameWithGenerics, emittedOperatorSymbols, isReferenceType: true);

                bool hasEquality = emittedOperatorSymbols.Contains("==");
                bool hasInequality = emittedOperatorSymbols.Contains("!=");

                // Add Equatable support if the struct conforms to Equatable.
                // Pass SwiftWriter + context for @_cdecl equality wrapper (avoids CallConvSwift crash).
                var SwiftEquatableMethodWriter = new EqualityMethodsWriter(csWriter, structDecl, true, typeNameWithGenerics, hasEquality, hasInequality, swiftWriter, context.GetEmissionContext(), env.TypeDatabase.AsyncLibraryName, env.TypeDatabase);
                SwiftEquatableMethodWriter.WriteSwiftEquatableImplementation();
                ISwiftObjectMethodWriter.WriteNonFrozenStructImplementation(pinvokeHelperContext, emitBoxable: interfaces.Contains("Swift.Runtime.IExistentialBoxable"));

                ToStringHelper.EmitToStringIfDescriptionExists(csWriter, structDecl, propertyRenames);

                csWriter.WriteLine();

                // Collect property names (post-rename) for method/property collision detection
                var propertyNames = new HashSet<string>(structDecl.Properties.Select(p =>
                    NameProvider.GetFinalMemberName(
                        NameProvider.GetPropertyName(p.Name, structDecl.Name), propertyRenames)));
                // Nested type names collide with method names in C# (CS0102)
                foreach (var nestedType in structDecl.Types)
                    propertyNames.Add(NameProvider.ToPascalCase(nestedType.Name));

                SubscriptHandler.EmitSubscripts(csWriter, swiftWriter, structDecl, env.TypeDatabase, conductor, childContext, _logger);

                if (collectionProjectionInterface is not null)
                    CollectionProjectionEmitter.EmitMembers(csWriter, structDecl, typeNameWithGenerics, env.TypeDatabase, propertyRenames, _logger,
                        swiftWriter: swiftWriter,
                        moduleCtx: context.GetEmissionContext(),
                        pinvokeHelperContext: pinvokeHelperContext,
                        alreadyEmittedMembers: actuallyEmittedPropertyNames);

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

                // Codable JSON round-trip — non-generic structs projected as classes.
                // Non-frozen structs are always class-projected; pass isProjectedAsClass: true.
                if (CodableJsonEmitter.ShouldEmit(structDecl, isProjectedAsClass: true))
                {
                    CodableJsonEmitter.Emit(
                        csWriter, swiftWriter, structDecl, moduleDecl,
                        typeNameWithGenerics, env.TypeDatabase, _logger,
                        context.GetEmissionContext());
                }

                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine();

                // Emit constrained-extension specialization classes (e.g., extension X where T == Concrete)
                ConstrainedExtensionEmitter.EmitConstrainedExtensions(
                    csWriter, swiftWriter, structDecl,
                    env.TypeDatabase, context.GetEmissionContext(), _logger);

                // Generic-parent CSM: per-parent-conformer static extension classes
                // (e.g. GenericContainer<SongItem>.Append overloads). Must live outside
                // the parent's body so the receiver can close over the generic.
                if (specEngine != null)
                {
                    ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializationsForGenericParent(
                        csWriter, swiftWriter, structDecl,
                        env.TypeDatabase, context.GetEmissionContext(), specEngine, _logger);

                    // Typed KeyPath singleton trampolines, same window as CSM emission.
                    // The non-frozen generic struct path requires this hook so the
                    // runtime tests can find their singletons.
                    KeyPathSingletonEmitter.EmitKeyPathSingletonsForGenericParent(
                        csWriter, swiftWriter, structDecl,
                        env.TypeDatabase, context.GetEmissionContext(), specEngine, _logger);

                    // Sibling per-V Sort overload emission. The emitter branches on the
                    // receiver kind: class uses unsafeBitCast, struct binds through
                    // assumingMemoryBound + (var __self + pointee write-back when the
                    // method is mutating). MusicLibraryRequest lands here.
                    KeyPathBagValueSpecializationEmitter.EmitRouteCSpecializationsForGenericParent(
                        csWriter, swiftWriter, structDecl,
                        env.TypeDatabase, context.GetEmissionContext(), specEngine, _logger);
                }

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
        /// Writes the private fields for the class.
        /// </summary>
        /// <param name="csWriter">The C# code writer.</param>
        /// <param name="typeNameWithGenerics">The type name including generic parameters.</param>
        /// <param name="pinvokeHelperContext">Optional P/Invoke helper context for generic types.
        /// When present, _payloadSize uses the helper class metadata accessor instead of
        /// SwiftObjectHelper&lt;GenericType&lt;T&gt;&gt; which crashes Mono's generic sharing.</param>
        private static void WritePrivateFields(CSharpWriter csWriter, string typeNameWithGenerics,
            PInvokeHelperContext? pinvokeHelperContext = null)
        {
            csWriter.WriteLine("[EditorBrowsable(EditorBrowsableState.Never)]");
            if (pinvokeHelperContext != null)
            {
                // Generic types: call the helper class metadata accessor with per-param
                // metadata (and per-conformance witness tables for constrained generics).
                // SwiftObjectHelper<GenericType<T>> in a static field initializer crashes
                // Mono's generic sharing (mini-generic-sharing.c:2759) because the nested
                // generic instantiation cannot be compiled without the type argument's
                // metadata.
                //
                // Route through TypeMetadata.RegisterAndGetSize so RunClassConstructor →
                // static field init populates both TypeMetadata.Cache and the
                // NewFromPayloadDispatcher factory. Passing NewFromPayloadCore as the factory
                // lets MarshalFromSwift resolve generic instantiations without falling back to
                // reflection on explicit interface implementations (which NativeAOT may not
                // enumerate via Type.GetMethods on closed generic instantiations).
                //
                // Note: when (num_metadata + num_pwts) > 3, TypeMetadataAccessorSkipGate
                // already skips the type before we reach this point, so PwtEntries is
                // always populated correctly here.
                var metadataArgs = string.Join(", ", pinvokeHelperContext.GetTypeMetadataAccessorArgumentList());
                csWriter.WriteLine($"static nuint _payloadSize = TypeMetadata.RegisterAndGetSize(typeof({typeNameWithGenerics}), {pinvokeHelperContext.HelperClassName}.PInvoke_getMetadata(TypeMetadataRequest.Complete, {metadataArgs}), NewFromPayloadCore);");
            }
            else
            {
                csWriter.WriteLine($"static nuint _payloadSize = SwiftObjectHelper<{typeNameWithGenerics}>.GetTypeMetadata().Size;");
            }
            csWriter.WriteLine("[EditorBrowsable(EditorBrowsableState.Never)]");
            csWriter.WriteLine($"SwiftSafeHandle<{typeNameWithGenerics}> _payload = SwiftSafeHandle<{typeNameWithGenerics}>.Zero;");
            csWriter.WriteLine();
        }

        /// <summary>
        /// Writes the payload accessor for the class.
        /// </summary>
        private static void WritePayload(CSharpWriter csWriter, string typeNameWithGenerics)
        {
            csWriter.WriteLine("[EditorBrowsable(EditorBrowsableState.Never)]");
            csWriter.WriteLine($"public SwiftSafeHandle<{typeNameWithGenerics}> Payload => _payload;");
            csWriter.WriteLine($"IntPtr ISwiftObject.SwiftHandle => _payload.DangerousGetHandle();");
            csWriter.WriteLine("void ISwiftObject.SuppressPayloadFinalizer() => GC.SuppressFinalize(_payload);");
            csWriter.WriteLine();
            // No wrapper finalizer: the _payload SwiftSafeHandle is itself a
            // CriticalFinalizerObject whose own finalizer releases the Swift value
            // (Cdecl VWT-destroy trampoline) — a separate ~T() would only re-do that
            // release and make every instance a second finalizable object (two-cycle
            // GC promotion). Matches the class-wrapper pattern (handle owns finalization).
            // Borrowed (+0) marshals suppress the payload finalizer independently via
            // ISwiftObject.SuppressPayloadFinalizer, so removing ~T() is ABI-neutral.
            var disposeMethods = $$"""
            /// <summary>Releases the underlying Swift object. Safe to call multiple times.</summary>
            public void Dispose()
            {
                _payload.Dispose();
                GC.SuppressFinalize(this);
            }
            """;
            csWriter.WriteLines(disposeMethods);
            csWriter.WriteLine();
        }
    }
}
