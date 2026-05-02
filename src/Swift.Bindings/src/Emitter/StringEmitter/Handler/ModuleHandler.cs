// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using Microsoft.Extensions.Logging;
using Swift.Runtime;

namespace BindingsGeneration
{
    /// <summary>
    /// Factory class for creating instances of ModuleHandler.
    /// </summary>
    public class ModuleHandlerFactory : HandlerFactory, IFactory<BaseDecl, IModuleHandler>
    {
        private readonly NamespacePatternResolver _namespacePatternResolver;

        /// <summary>
        /// Initializes a new instance of the <see cref="ModuleHandlerFactory"/> class.
        /// </summary>
        /// <param name="loggerFactory">The logger factory instance.</param>
        public ModuleHandlerFactory(ILoggerFactory loggerFactory, NamespacePatternResolver? namespacePatternResolver = null) : base(loggerFactory.CreateLogger<ModuleHandler>())
        {
            _namespacePatternResolver = namespacePatternResolver ?? new NamespacePatternResolver();
        }

        /// <summary>
        /// Determines if the factory handles the specified declaration.
        /// </summary>
        /// <param name="decl">The base declaration.</param>
        public bool Handles(BaseDecl decl)
        {
            return decl is ModuleDecl;
        }

        /// <summary>
        /// Constructs a new instance of ModuleHandler.
        /// </summary>
        public IModuleHandler Construct()
        {
            return new ModuleHandler(_handlerLogger, _namespacePatternResolver);
        }
    }

    /// <summary>
    /// Handler class for module declarations.
    /// </summary>
    public class ModuleHandler : BaseHandler, IModuleHandler
    {
        private readonly NamespacePatternResolver _namespacePatternResolver;

        public ModuleHandler(ILogger logger, NamespacePatternResolver? namespacePatternResolver = null) : base(logger)
        {
            _namespacePatternResolver = namespacePatternResolver ?? new NamespacePatternResolver();
        }

        /// <inheritdoc/>
        public IEnvironment Marshal(BaseDecl baseDecl, ITypeDatabase typeDatabase)
        {
            if (baseDecl is not ModuleDecl moduleDecl)
            {
                throw new ArgumentException("The provided decl must be a ModuleDecl.", nameof(baseDecl));
            }
            return new ModuleEnvironment(moduleDecl, typeDatabase);
        }

        /// <inheritdoc/>
        public void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor, TypeHandlerContext context)
        {
            var moduleEnv = (ModuleEnvironment)env;
            var moduleDecl = moduleEnv.ModuleDecl;

            // Per-module state is now on ModuleEmissionContext (no more static resets needed).

            // Seed ReportCollector with the set of types that type handlers will skip.
            // Member gates (ValidationRuleSet.ReferencesUnsupportedModule) consult this
            // set via ReportCollector.IsTypeSkipped so signatures referencing a skipped
            // generic (e.g., MusicKit.MusicRelationshipProperty<_,_>) get pruned in the
            // same pass they're emitted, instead of producing a dangling reference that
            // fails C# compilation with CS0234.
            TypeSkipPrePass.Run(moduleDecl, env.TypeDatabase);

            // Emit Swift imports at the top of the Swift wrapper file
            EmitSwiftImports(swiftWriter, moduleDecl, context.GetEmissionContext());

            // Emit EveryProtocol class and protocol conformances for Swift side
            EmitEveryProtocolConformances(swiftWriter, moduleDecl, env.TypeDatabase, context.GetEmissionContext());

            var generatedNamespace = _namespacePatternResolver.ResolveNamespace(moduleDecl.Name);

            csWriter.WriteLine("#nullable enable");
            csWriter.WriteLine();
            csWriter.WriteLine($"using System;");
            csWriter.WriteLine($"using System.Collections.Generic;");
            csWriter.WriteLine($"using System.Diagnostics;");
            csWriter.WriteLine($"using System.Diagnostics.CodeAnalysis;");
            csWriter.WriteLine($"using System.Linq;");
            csWriter.WriteLine($"using System.Runtime.CompilerServices;");
            csWriter.WriteLine($"using System.Runtime.InteropServices;");
            csWriter.WriteLine($"using System.Runtime.InteropServices.Swift;");
            csWriter.WriteLine($"using System.Threading.Tasks;");
            csWriter.WriteLine($"using Swift;");
            csWriter.WriteLine($"using Swift.Runtime;");
            csWriter.WriteLine($"using Swift.Runtime.InteropServices;");
            csWriter.WriteLine($"using System.ComponentModel;");
            csWriter.WriteLine($"using {generatedNamespace}.SwiftInterop;");
            // Alias the runtime Utf8Slice type so generated code can reference it unqualified
            csWriter.WriteLine("using Utf8Slice = global::Swift.Runtime.Utf8Slice;");

            // (RealityKit-bug-13: The maccatalyst-only "missing `using ARKit;`" problem will need
            // a per-project SwiftFrameworkDependency-aware emit — emitting `using` for every
            // referenced Apple framework breaks consumer projects that don't reference those
            // packages, e.g. LiveCommunicationKit which references AVFAudio types without
            // pulling AVFAudio into its csproj. Tracked in roadmap.)
            csWriter.WriteLine();
            csWriter.WriteLine($"namespace {generatedNamespace}");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // Scope composition interface collection across BOTH top-level methods and types.
            // Free functions can reference composition existentials (e.g., any Describable & TestIdentifiable),
            // so the collector must be active before emitting top-level methods.
            // Populate composition collector on the context — threaded through
            // MethodEnvironment/PropertyEnvironment → ExistentialHandler during emission.
            conductor.CompositionInterfaces.Clear();
            context = context with { CompositionCollector = conductor.CompositionInterfaces };
            {
                // Emit top-level methods
                if (moduleDecl.Methods.Any())
                {
                    var wrapperClassName = moduleDecl.Name;
                    bool stutters = generatedNamespace.EndsWith($".{moduleDecl.Name}") || generatedNamespace == moduleDecl.Name;
                    if (stutters)
                    {
                        wrapperClassName = "Functions";
                        // Check if a top-level type or the module itself already uses the chosen name
                        var typeNames = new HashSet<string>(moduleDecl.Types.Select(t => t.Name));
                        if (wrapperClassName == moduleDecl.Name || typeNames.Contains(wrapperClassName))
                            wrapperClassName = "GlobalFunctions";
                        if (wrapperClassName == moduleDecl.Name || typeNames.Contains(wrapperClassName))
                        {
                            // Ultimate fallback: append suffix until unique
                            var candidate = $"{moduleDecl.Name}Functions";
                            int suffix = 2;
                            while (typeNames.Contains(candidate))
                                candidate = $"Functions{suffix++}";
                            wrapperClassName = candidate;
                        }
                    }
                    csWriter.WriteLine($"public partial class {wrapperClassName}");
                    csWriter.WriteLine("{");
                    csWriter.Indent++;
                    csWriter.WriteLine();
                    // Track emitted signatures to avoid duplicate free function overloads
                    // (e.g., Swift count(_:) vs count(distinct:) which both project to GetCount<T0>(T0))
                    var emittedMethodSignatures = new HashSet<string>();
                    var emittedProjectedSignatures = new HashSet<string>(StringComparer.Ordinal);
                    var projectedKeyCollisionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                    var pipeline = new MemberValidationPipeline(env.TypeDatabase);
                    foreach (MethodDecl methodDecl in moduleDecl.Methods)
                    {
                        // Pipeline: unified emission validation (SPI, internal, synthesized, closures, modules)
                        var validationResult = pipeline.ValidateMethodEmission(methodDecl, null);
                        if (!validationResult.ShouldEmit)
                        {
                            ReportCollector.RecordMemberSkipped(methodDecl,
                                validationResult.Reason ?? SkipReason.ModuleInternal, validationResult.Details ?? "");
                            UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, methodDecl.Name, BindingItemKind.Method, validationResult.Reason ?? SkipReason.ModuleInternal, validationResult.Details);
                            csWriter.WriteLine();
                            continue;
                        }

                        // Primary dedup: Swift-level signature
                        var signatureKey = GetMethodSignatureKey(methodDecl, env.TypeDatabase, _logger);
                        if (emittedMethodSignatures.Contains(signatureKey))
                        {
                            _logger.LogDebug($"Skipping duplicate free function '{methodDecl.Name}' with signature: {signatureKey}");
                            ReportCollector.RecordMemberSkipped(methodDecl, SkipReason.DuplicateSignature, signatureKey);
                            UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, methodDecl.Name, BindingItemKind.Method, SkipReason.DuplicateSignature);
                            csWriter.WriteLine();
                            continue;
                        }
                        emittedMethodSignatures.Add(signatureKey);

                        // Secondary dedup: projected C# public signature.
                        // Non-constructor collisions are disambiguated with numeric suffix.
                        var projectedKey = GetProjectedCSharpMethodKey(methodDecl, env.TypeDatabase, _logger);
                        int collisionIndex = 0;
                        if (!emittedProjectedSignatures.Add(projectedKey))
                        {
                            // Free functions are never constructors — always disambiguate.
                            // Loop until a free suffix is found — a natural name like "Foo2"
                            // could already occupy the suffixed slot.
                            if (!projectedKeyCollisionCounts.TryGetValue(projectedKey, out var count))
                                count = 0;
                            string disambiguatedKey;
                            do
                            {
                                collisionIndex = ++count;
                                disambiguatedKey = ApplyCollisionSuffixToKey(projectedKey, collisionIndex);
                            } while (!emittedProjectedSignatures.Add(disambiguatedKey));
                            projectedKeyCollisionCounts[projectedKey] = collisionIndex;

                            _logger.LogDebug($"Disambiguating free function '{methodDecl.Name}' — collision #{collisionIndex + 1} for projected key: {projectedKey} → {disambiguatedKey}");
                        }

                        if (conductor.TryGetMethodHandler(methodDecl, out var methodHandler))
                        {
                            var methodEnv = new MethodEnvironment(methodDecl, env.TypeDatabase, compositionCollector: context.CompositionCollector);
                            methodEnv.CollisionIndex = collisionIndex;
                            methodEnv.EmittedProjectedSignatures = emittedProjectedSignatures;
                            methodHandler.Emit(csWriter, swiftWriter, methodEnv, conductor, context);
                        }
                        else
                        {
                            _logger.LogWarning($"No handler found for method {methodDecl.Name}");
                            ReportCollector.RecordMemberSkipped(methodDecl, SkipReason.MissingHandler, "No method handler found for top-level method.");
                            UnsupportedCommentEmitter.EmitMemberSkipped(csWriter, methodDecl.Name, BindingItemKind.Method, SkipReason.MissingHandler);
                        }
                        csWriter.WriteLine();
                    }
                    csWriter.Indent--;
                    csWriter.WriteLine("}");
                    csWriter.WriteLine();
                }

                base.HandleBaseDecl(csWriter, swiftWriter, moduleDecl.Types, conductor, env.TypeDatabase, context);

                // Emit protocol extension method Swift wrappers (accumulated during InjectExtensionMethods)
                var emissionCtx = context.GetEmissionContext();
                ProtocolExtensionEmitter.EmitSwiftWrappers(swiftWriter, emissionCtx);

                // Emit foreign type extension Swift wrappers and C# extension classes
                ForeignTypeExtensionEmitter.EmitSwiftWrappers(swiftWriter, emissionCtx);
                ForeignTypeExtensionEmitter.EmitCSharpExtensionClasses(csWriter, env.TypeDatabase, moduleDecl.Name, emissionCtx);

                // Emit deferred enum extension classes (from nested simple enums).
                // C# requires extension methods to be in top-level static classes, so nested
                // enums (e.g., ImageProcessingOptions.Unit) defer their extension classes here.
                foreach (var extensionSource in emissionCtx.DeferredEnumExtensionClasses)
                {
                    csWriter.InnerWriter.Write(extensionSource);
                }

                // Emit composition interfaces (e.g., IAgeableAndNameable : IAgeable, INameable)
                // These are collected during method/property emission when multi-protocol existentials are encountered.
                // SortedDictionary ensures deterministic emission order regardless of encounter order.
                foreach (var (compositionName, parentInterfaces) in conductor.CompositionInterfaces)
                {
                    csWriter.WriteLine();
                    csWriter.WriteLine($"public interface {compositionName} : {string.Join(", ", parentInterfaces)}");
                    csWriter.WriteLine("{");
                    csWriter.WriteLine("}");
                }

                // Emit wrap-only proxy classes for each composition interface
                foreach (var (compositionName, parentInterfaces) in conductor.CompositionInterfaces)
                {
                    EmitCompositionProxy(csWriter, compositionName, parentInterfaces, moduleDecl, env.TypeDatabase);
                }
            }

            // Emit DllImport framework resolver + NativeAOT factory registration with [ModuleInitializer]
            EmitFrameworkResolver(csWriter, moduleDecl.Name, context.GetEmissionContext());

            csWriter.Indent--;
            csWriter.WriteLine("}");

            // Emit SwiftInterop sub-namespace for protocol proxy classes.
            // Always emitted so the 'using' directive at the top resolves even when empty.
            csWriter.WriteLine();
            csWriter.WriteLine($"namespace {generatedNamespace}.SwiftInterop");
            csWriter.WriteLine("{");
            foreach (var proxySource in context.GetEmissionContext().DeferredProxyClasses)
                csWriter.InnerWriter.Write(proxySource);
            csWriter.WriteLine("}");

        }

        /// <summary>
        /// Emits a static class with [ModuleInitializer] that:
        /// 1. Registers a NativeLibrary.SetDllImportResolver for iOS framework loading
        /// 2. Pre-registers NewFromPayload factories for NativeAOT (avoids reflection trimming)
        /// </summary>
        private static void EmitFrameworkResolver(CSharpWriter csWriter, string moduleName, ModuleEmissionContext? emissionCtx)
        {
            var factoryTypes = emissionCtx?.EmittedSwiftObjectTypes ?? Array.Empty<string>();
            var conformances = emissionCtx?.EmittedConformances ?? Array.Empty<(string, string)>();
            var simpleEnumRegistrations = emissionCtx?.SimpleEnumMetadataRegistrations
                ?? Array.Empty<(string, string, string)>();

            // Emit a single [ModuleInitializer] class that:
            // 1. Registers the DllImport framework resolver (must be first — metadata lookups P/Invoke into native libs)
            // 2. Pre-registers NewFromPayload factories for NativeAOT
            // 3. Pre-registers type metadata in the cache for NativeAOT (avoids trimmed reflection path)
            // 4. Pre-registers protocol conformance factories for NativeAOT
            // 5. Registers simple enum metadata via P/Invoke (correct SwiftOptional<T> layout)
            // All in one initializer to guarantee ordering (framework resolver before any P/Invoke).
            csWriter.WriteLine();
            csWriter.WriteLine("#pragma warning disable CA2255 // ModuleInitializer is intentional in generated binding code");
            // Eager type-metadata / factory registrations touch types that may carry
            // [SupportedOSPlatform] annotations stricter than the callsite's floor. The
            // initializer runs at module load from an arbitrary OS context and every
            // call is wrapped in try/catch, so a CA1416 at this site is a false positive.
            csWriter.WriteLine("#pragma warning disable CA1416 // ModuleInitializer registrations are best-effort across OS versions");
            csWriter.WriteLines($$"""
                internal static class __SwiftFrameworkResolver_{{moduleName}}
                {
                    [ModuleInitializer]
                    internal static void Initialize()
                    {
                        global::Swift.Runtime.SwiftFrameworkResolver.RegisterForAssembly(typeof(__SwiftFrameworkResolver_{{moduleName}}).Assembly);
                """);
            foreach (var typeName in factoryTypes)
            {
                // Wrap each registration in try-catch so one failing type doesn't crash the
                // entire app during module initialization. On NativeAOT device, some types
                // (e.g., types depending on framework initialization order) may fail during
                // early startup. The factory and metadata are best-effort — types that fail
                // here will fall back to the reflection path at call time.
                //
                // Generic types (name contains '<') get factory registration only; their metadata
                // accessor can SIGSEGV in the Swift runtime during module init (not catchable in C#
                // try/catch) because the Swift class isn't fully initialized yet. On-demand lookup
                // via SwiftObjectHelper<T>.GetTypeMetadata() at actual call time works fine.
                if (typeName.Contains('<'))
                {
                    csWriter.WriteLines($"        try {{ global::Swift.Runtime.InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<{typeName}>(); }} catch {{ }}");
                }
                else
                {
                    csWriter.WriteLines($"        try {{ global::Swift.Runtime.InteropServices.SwiftMarshal.RegisterSwiftObjectFactory<{typeName}>(); global::Swift.Runtime.SwiftObjectHelper<{typeName}>.GetTypeMetadata(); }} catch {{ }}");
                }
            }
            foreach (var (typeName, protocolName) in conformances)
            {
                csWriter.WriteLines($"        try {{ global::Swift.Runtime.InteropServices.SwiftMarshal.RegisterConformanceFactory<{typeName}, {protocolName}>(); }} catch {{ }}");
            }
            // Pre-register witness tables for ALL protocol conformances.
            // This eagerly computes and caches the witness table during module initialization
            // via GetOrThrowDirect (static virtual dispatch). On NativeAOT device,
            // LoadFromSymbol → swift_getWitnessTable can crash when called later at runtime
            // (likely due to library handle lifecycle issues). Pre-registering during init
            // ensures the witness table is cached and the runtime path uses the cache.
            foreach (var (typeName, protocolName) in conformances)
            {
                csWriter.WriteLines($"        try {{ global::Swift.Runtime.InteropServices.SwiftMarshal.RegisterWitnessTable<{typeName}, {protocolName}>(); }} catch {{ }}");
            }
            // Register simple enum metadata via P/Invoke to @_cdecl Swift wrappers.
            // Simple C# enums can't implement ISwiftObject, so their Swift metadata must be
            // registered explicitly. Without this, SwiftOptional<T> gets the wrong Optional
            // layout (tag-byte encoding from the underlying integer type instead of
            // extra-inhabitant encoding from the actual Swift enum).
            foreach (var (typeName, _, _) in simpleEnumRegistrations)
            {
                var safeName = typeName.Replace(".", "_");
                csWriter.WriteLines($"        try {{ global::Swift.Runtime.TypeMetadata.RegisterMetadata(typeof({typeName}), global::Swift.Runtime.TypeMetadata.FromHandle(__GetEnumMetadata_{safeName}())); }} catch {{ }}");
            }
            csWriter.WriteLines($$"""
                    }
                """);
            // Emit DllImport P/Invoke declarations for simple enum metadata accessors.
            foreach (var (typeName, metadataSymbol, wrapperLibName) in simpleEnumRegistrations)
            {
                var safeName = typeName.Replace(".", "_");
                csWriter.WriteLines($"    [System.Runtime.InteropServices.DllImport(\"{wrapperLibName}\", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, EntryPoint = \"{metadataSymbol}\")]");
                csWriter.WriteLines($"    private static extern IntPtr __GetEnumMetadata_{safeName}();");
            }
            csWriter.WriteLines($$"""
                }
                """);

            csWriter.WriteLine("#pragma warning restore CA1416");
            csWriter.WriteLine("#pragma warning restore CA2255");
        }

        /// <summary>
        /// Known Apple frameworks that may need to be imported.
        /// </summary>
        private static readonly HashSet<string> AppleFrameworks = new()
        {
            "UIKit", "AppKit", "CoreGraphics", "CoreText", "QuartzCore",
            "CoreFoundation", "CoreImage", "CoreAnimation", "CoreMedia",
            "AVFoundation", "AVFAudio", "SceneKit", "SpriteKit", "Metal", "MetalKit",
            "GameplayKit", "MapKit", "CoreLocation", "CloudKit", "StoreKit",
            "HealthKit", "HomeKit", "WatchKit", "ARKit", "RealityKit",
            "PDFKit", "WebKit", "SafariServices", "AuthenticationServices",
            "LocalAuthentication", "Security", "CryptoKit", "Combine",
            "SwiftUI", "UniformTypeIdentifiers", "CoreData", "CoreML",
            "Vision", "NaturalLanguage", "Speech", "SoundAnalysis",
            "Accelerate", "simd", "Compression", "OSLog", "os",
            "Contacts", "ContactsUI", "EventKit", "EventKitUI",
            "PhotosUI", "Photos", "PassKit", "MessageUI",
            "UserNotifications", "NetworkExtension", "CoreBluetooth",
            "CoreNFC", "CoreMotion", "CoreTelephony", "CarPlay",
            "Intents", "IntentsUI", "LinkPresentation", "MediaPlayer",
            // Newer-SDK frameworks whose types leak into wrapper signatures for
            // libraries we bind (FamilyControls→ManagedSettings, etc.). Keep the
            // list curated so Swift stdlib identifiers like τ_0_0 and ambient
            // Apple modules (Network, Dispatch) that only appear in rejected
            // signatures don't end up as spurious `import` lines.
            "ManagedSettings", "DeviceActivity", "FamilyControls",
            "Translation", "ProximityReader", "LiveCommunicationKit",
            "WeatherKit", "TipKit", "WorkoutKit", "ActivityKit",
            "BackgroundTasks", "CallKit", "MultipeerConnectivity"
        };

        /// <summary>
        /// Collects the set of Apple framework module names referenced by a module's bound
        /// surface (declared dependencies + scanned method/property/protocol/subscript signatures).
        /// Filtering of implicit / self modules (Swift, Foundation, the module being bound) is
        /// applied here so callers receive a ready-to-emit set.
        /// </summary>
        private HashSet<string> CollectFrameworkImports(ModuleDecl moduleDecl)
        {
            var neededImports = new HashSet<string>();

            // Add platform UI frameworks if present in dependencies
            foreach (var dep in moduleDecl.Dependencies)
            {
                if (AppleFrameworks.Contains(dep))
                {
                    neededImports.Add(dep);
                }
            }

            // Scan for types used in methods that need corresponding imports
            ScanTypesForFrameworkImports(moduleDecl.Types, neededImports);

            // Scan protocols for types used in method/property signatures
            if (moduleDecl.Protocols != null)
            {
                ScanProtocolsForFrameworkImports(moduleDecl.Protocols, neededImports);
            }

            // Drop implicit / already-imported / self modules discovered during the scan.
            // Swift stdlib is implicit; Foundation is imported unconditionally on the wrapper
            // side and lives in System on the C# side; the module being bound is its own
            // namespace.
            neededImports.Remove("Swift");
            neededImports.Remove("Foundation");
            neededImports.Remove(moduleDecl.Name);

            return neededImports;
        }

        /// <summary>
        /// Emits Swift import statements to the wrapper file.
        /// </summary>
        private void EmitSwiftImports(SwiftWriter swiftWriter, ModuleDecl moduleDecl, ModuleEmissionContext? emissionCtx = null)
        {
            // Always import the module being bound. Some Apple modules (e.g. RealityFoundation)
            // are marked @_implementationOnly by their umbrella (RealityKit) and must be imported
            // through the umbrella instead. Type qualifications and the .NET namespace continue
            // to use moduleDecl.Name — only this literal import line is rewritten.
            var compileImport = AppleFrameworkRegistry.MapModuleToCompileImport(moduleDecl.Name);
            swiftWriter.WriteLine($"import {compileImport}");
            swiftWriter.WriteLine("import Foundation");

            // Build the additional-imports set with every candidate normalized through the
            // compile-import remap, then dedupe on the *normalized* name. This covers scanned
            // imports (CollectFrameworkImports) and --framework-dependency entries equally,
            // so a sibling module that pulls in @_implementationOnly RealityFoundation either
            // way still emits `import RealityKit`.
            var additionalImports = new HashSet<string>();
            foreach (var scanned in CollectFrameworkImports(moduleDecl))
            {
                additionalImports.Add(AppleFrameworkRegistry.MapModuleToCompileImport(scanned));
            }
            foreach (var depModule in moduleDecl.DependencyModuleNames)
            {
                additionalImports.Add(AppleFrameworkRegistry.MapModuleToCompileImport(depModule));
            }
            additionalImports.Remove("Swift");
            additionalImports.Remove("Foundation");
            additionalImports.Remove(compileImport);
            additionalImports.Remove(moduleDecl.Name);

            foreach (var import in additionalImports.OrderBy(s => s))
            {
                swiftWriter.WriteLine($"import {import}");
            }

            swiftWriter.WriteLine();

            // Emit SBW_Utf8Slice and SBW_Free at module level (before any functions)
            // These are needed for async String returns and may be used elsewhere.
            // Emitting unconditionally is safe - small structs/functions that do no harm if unused.
            // SBW_Free uses module-specific symbol name to avoid collisions if multiple modules
            // are linked into the same wrapper library.
            Utf8SliceEmitter.EmitIfNeeded(swiftWriter, emissionCtx);
            Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter, moduleDecl.Name, emissionCtx);

            // Emit Swift Task cancellation infrastructure (cancel function + task dictionary)
            CancellationTaskEmitter.EmitIfNeeded(swiftWriter, moduleDecl.Name, emissionCtx);

            // Emit Swift error description extraction infrastructure (for sync throwing methods)
            ErrorDescriptionEmitter.EmitIfNeeded(swiftWriter, moduleDecl.Name, emissionCtx);
        }

        /// <summary>
        /// Recursively scans types for async methods that return framework types.
        /// </summary>
        private void ScanTypesForFrameworkImports(IEnumerable<TypeDecl> types, HashSet<string> neededImports)
        {
            foreach (var type in types)
            {
                // Check methods — @_cdecl wrappers reference parameter and return types from ALL methods,
                // not just async ones. Missing imports cause "cannot find type 'X' in scope" errors.
                var methods = type switch
                {
                    StructDecl s => s.Methods,
                    ClassDecl c => c.Methods,
                    _ => Enumerable.Empty<MethodDecl>()
                };

                foreach (var method in methods)
                {
                    foreach (var sig in method.CSSignature)
                    {
                        ScanTypeSpecForImports(sig.SwiftTypeSpec, neededImports);
                    }
                }

                // Check properties — @_cdecl wrappers also reference property types
                var properties = type switch
                {
                    StructDecl s => s.Properties,
                    ClassDecl c => c.Properties,
                    _ => Enumerable.Empty<PropertyDecl>()
                };

                foreach (var property in properties)
                {
                    ScanTypeSpecForImports(property.SwiftTypeSpec, neededImports);
                }

                // Recursively check nested types
                if (type.Types.Any())
                {
                    ScanTypesForFrameworkImports(type.Types, neededImports);
                }
            }
        }

        /// <summary>
        /// Scans protocols for types used in method parameters, return types, and properties.
        /// These types appear in EveryProtocol conformance code and need corresponding imports.
        /// </summary>
        private void ScanProtocolsForFrameworkImports(IEnumerable<ProtocolDecl> protocols, HashSet<string> neededImports)
        {
            foreach (var protocol in protocols)
            {
                // Scan properties
                foreach (var property in protocol.Properties)
                {
                    ScanTypeSpecForImports(property.SwiftTypeSpec, neededImports);
                }

                // Scan methods
                foreach (var method in protocol.Methods)
                {
                    // Scan return type
                    if (method.CSSignature.Count > 0)
                    {
                        ScanTypeSpecForImports(method.CSSignature[0].SwiftTypeSpec, neededImports);
                    }

                    // Scan parameter types
                    for (int i = 1; i < method.CSSignature.Count; i++)
                    {
                        ScanTypeSpecForImports(method.CSSignature[i].SwiftTypeSpec, neededImports);
                    }
                }

                // Scan subscripts
                foreach (var subscript in protocol.Subscripts)
                {
                    ScanTypeSpecForImports(subscript.ReturnTypeSpec, neededImports);
                    foreach (var param in subscript.IndexParameters)
                    {
                        ScanTypeSpecForImports(param.SwiftTypeSpec, neededImports);
                    }
                }
            }
        }

        /// <summary>
        /// Recursively scans a TypeSpec for framework types and adds needed imports.
        /// </summary>
        private void ScanTypeSpecForImports(TypeSpec? typeSpec, HashSet<string> neededImports)
        {
            if (typeSpec == null)
                return;

            if (typeSpec is NamedTypeSpec namedType)
            {
                // Check if the type name starts with a known framework
                CheckTypeNameForFrameworkImport(namedType.Name, neededImports);

                // Recursively check generic parameters
                foreach (var genericParam in namedType.GenericParameters)
                {
                    ScanTypeSpecForImports(genericParam, neededImports);
                }
            }
            else if (typeSpec is TupleTypeSpec tupleType)
            {
                foreach (var element in tupleType.Elements)
                {
                    ScanTypeSpecForImports(element, neededImports);
                }
            }
            else if (typeSpec is ClosureTypeSpec closureType)
            {
                ScanTypeSpecForImports(closureType.Arguments, neededImports);
                ScanTypeSpecForImports(closureType.ReturnType, neededImports);
            }
            else if (typeSpec is ProtocolListTypeSpec protocolList)
            {
                foreach (var proto in protocolList.Protocols.Keys)
                {
                    CheckTypeNameForFrameworkImport(proto.Name, neededImports);
                }
            }
        }

        /// <summary>
        /// Checks if a type name requires a framework import. Adds the module portion of a
        /// qualified name to <paramref name="neededImports"/> so the generated wrapper
        /// imports every module it references (not just the curated <c>AppleFrameworks</c>
        /// set). Filtering of implicit modules (Swift, Foundation, self) is done by the
        /// caller after the full scan.
        /// </summary>
        private void CheckTypeNameForFrameworkImport(string? typeName, HashSet<string> neededImports)
        {
            if (string.IsNullOrEmpty(typeName))
                return;

            var dotIndex = typeName.IndexOf('.');
            if (dotIndex <= 0)
                return;

            var moduleName = typeName.Substring(0, dotIndex);

            // Underscore-prefixed Apple SPI modules (e.g. _LocationEssentials) are not public
            // and cannot be imported directly. Remap to the public counterpart when known;
            // drop silently when unknown (better to skip than to emit a broken import).
            if (moduleName.StartsWith("_", StringComparison.Ordinal))
            {
                if (!AppleFrameworkRegistry.TryMapSpiModuleToPublic(moduleName, out var publicModule))
                    return;
                moduleName = publicModule;
            }

            // Only add known Apple frameworks. An unconditional add broke the validation
            // corpus in two ways: (1) Swift generic placeholders like `τ_0_0` leaked in as
            // bogus modules, (2) ambient modules like `Network` got auto-imported and
            // collided with same-named types in the bound module (e.g. Starscream.Framer
            // vs Network.Framer).
            if (AppleFrameworks.Contains(moduleName))
                neededImports.Add(moduleName);
        }

        /// <summary>
        /// Emits the EveryProtocol class and protocol conformances for Swift side.
        /// This enables C# code to implement Swift protocols by providing vtable callbacks.
        /// </summary>
        private void EmitEveryProtocolConformances(SwiftWriter swiftWriter, ModuleDecl moduleDecl, ITypeDatabase typeDatabase, ModuleEmissionContext? emissionCtx = null)
        {
            // Skip if there are no protocols to conform to
            var protocols = moduleDecl.Protocols;
            if (protocols == null || !protocols.Any())
                return;

            // Check if any protocols are suitable for EveryProtocol conformance
            var suitableProtocols = protocols
                .Where(p => !p.HasSelfRequirement && p.AssociatedTypes.Count == 0)
                // Skip internal, @_spi, and @usableFromInline protocols — EveryProtocol can only
                // conform to protocols whose members are all publicly accessible.
                .Where(p => !p.IsModuleInternal)
                // Phase 2 (EveryProtocolEmitter) handles all member-level decisions:
                // constructor requirements, static method requirements, empty marker protocols, etc.
                // All protocols pass through here; the emitter records proper skip reasons.
                // Bug #14: Filter out protocols not actually defined in this module.
                // When a module extends stdlib protocols (e.g., CryptoSwift extends Collection),
                // the parser creates ProtocolDecl entries with the module's name (CryptoSwift.Collection),
                // but these are stdlib protocols re-exported by the module — not defined in it.
                // Check the mangled name: module-defined protocols encode the module name as
                // $s{length}{moduleName}... (e.g., $s11CryptoSwift...), while stdlib protocols
                // use abbreviated forms ($sSl, $sSB, $ss17...).
                .Where(p => IsMangledNameFromModule(p.MangledName, moduleDecl.Name))
                .Where(p => !HasMembersReferencingUnsupportedModule(p, typeDatabase))
                // Note: InheritsCodable filter removed — EveryProtocol now emits Codable/Error
                // stub conformances so protocols that inherit Decodable/Encodable are supported.
                // Skip protocols requiring NSObjectProtocol identity semantics —
                // EveryProtocol can't provide NSObject methods (isEqual:, hash, etc.).
                // Pure AnyObject class-bound protocols are allowed (EveryProtocol is a class).
                .Where(p => !EveryProtocolEmitter.IsClassBoundProtocol(p, protocols))
                // Skip protocols whose inheritance names a concrete class (e.g.
                // `protocol P : UIGestureRecognizer`). EveryProtocol is a plain
                // Swift class and cannot satisfy a class superclass constraint.
                .Where(p => !EveryProtocolEmitter.HasClassSuperclassRequirement(p, typeDatabase, protocols))
                // Skip CaseIterable — requires compiler-synthesized allCases. Transitive check.
                .Where(p => !EveryProtocolEmitter.InheritsCaseIterable(p, protocols))
                // Skip protocols that inherit from protocols with associated types or Self requirements.
                // EveryProtocol can't provide concrete associated types for inherited PATs.
                .Where(p => !InheritsProtocolWithAssociatedTypes(p, protocols, typeDatabase))
                // Skip protocols that inherit from stdlib protocols with requirements
                // EveryProtocol can't satisfy (CustomStringConvertible, CodingKey, etc.).
                // The vtable only includes the protocol's own members, not inherited ones.
                .Where(p => !EveryProtocolEmitter.InheritsUnsatisfiedStdlibProtocol(p, protocols))
                // Skip protocols whose member signatures reference types from this module
                // that are not in the type database (module-internal types). EveryProtocol
                // can't implement methods requiring internal types.
                .Where(p => !HasMembersReferencingInternalTypes(p, typeDatabase, moduleDecl.Name))
                .ToList();


            // Global dedup of EveryProtocol stubs is keyed by property name, so two protocols
            // that each require a property with the SAME name but DIFFERENT types produce one
            // successful conformance and one whose required member gets skipped (breaking the
            // extension with "type 'EveryProtocol' does not conform"). Pre-scan to find such
            // conflicting names and drop every protocol that participates, so the remaining
            // conformances compile cleanly. Example: MusicKit.LibraryAlbumFilter.artistName is
            // `String` while MusicKit.LibraryMusicVideoFilter.artistName is `String?`.
            // Only protocol *requirements* contribute witnesses to EveryProtocol's conformance —
            // default implementations from same-protocol extensions are extension methods on the
            // existential, not witness-table entries. Including them in the conflict scan turned
            // RealityFoundation umbrella-prefixed protocols (e.g. RealityKit.Material's
            // `name: String?` extension default) into false positives that dropped the canonical
            // BlendTreeNode/AnimationDefinition/MaterialFunction protocols whose `name: String`
            // is genuinely required.
            var propertyTypeCounts = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var p in suitableProtocols)
            {
                foreach (var prop in p.Properties)
                {
                    if (prop.IsStatic || prop.IsObjCOptional || !prop.IsProtocolRequirement)
                        continue;
                    if (!propertyTypeCounts.TryGetValue(prop.Name, out var types))
                    {
                        types = new HashSet<string>(StringComparer.Ordinal);
                        propertyTypeCounts[prop.Name] = types;
                    }
                    types.Add(prop.SwiftTypeSpec.ToString());
                }
            }
            var conflictingPropertyNames = propertyTypeCounts
                .Where(kvp => kvp.Value.Count > 1)
                .Select(kvp => kvp.Key)
                .ToHashSet(StringComparer.Ordinal);
            if (conflictingPropertyNames.Count > 0)
            {
                suitableProtocols = suitableProtocols
                    .Where(p => !p.Properties.Any(prop =>
                        !prop.IsStatic && !prop.IsObjCOptional && prop.IsProtocolRequirement &&
                        conflictingPropertyNames.Contains(prop.Name)))
                    .ToList();
            }

            if (!suitableProtocols.Any())
                return;

            var emitter = new EveryProtocolEmitter(typeDatabase, _logger, moduleDecl.Name, emissionCtx);
            var dispatchEmitter = new WitnessDispatchEmitter(typeDatabase, _logger, moduleDecl.Name, emissionCtx);

            // Emit the EveryProtocol class once
            emitter.EmitEveryProtocolClass(swiftWriter);

            // Emit Codable/Error stub conformances on EveryProtocol if any suitable protocol
            // requires them. These stubs let EveryProtocol satisfy the inherited Codable/Error
            // requirements when conforming to protocols that inherit Decodable/Encodable/Error.
            emitter.EmitCodableStubsIfNeeded(swiftWriter, suitableProtocols, protocols, typeDatabase);

            // Pre-scan: identify protocols that will be skipped by structural gates.
            // This makes genericSig constraint checks order-independent.
            emitter.PreScanProtocols(suitableProtocols);

            // Track emitted method signatures globally to detect conflicts across protocols
            // Key is the Swift method signature (e.g., "removeAll()")
            var globalEmittedSignatures = new HashSet<string>();

            // Pre-pass: determine which method signatures must be emitted non-throwing.
            // In Swift, a non-throwing method satisfies both throwing and non-throwing protocol
            // requirements, but a throwing method does NOT satisfy a non-throwing requirement.
            // If two protocols share the same method signature but differ in throws-ness,
            // we must emit the non-throwing variant to satisfy both conformances.
            var nonThrowingOverrides = ComputeNonThrowingOverrides(suitableProtocols, emitter);

            // Emit conformances and witness dispatch accessors for each suitable protocol
            foreach (var protocolDecl in suitableProtocols)
            {
                _logger.LogDebug($"Emitting EveryProtocol conformance for {protocolDecl.Name}");
                emitter.EmitProtocolConformance(swiftWriter, protocolDecl, globalEmittedSignatures, nonThrowingOverrides);
                // Skip witness dispatch for mixed-generic protocols — the type projection
                // pipeline generates incorrect types when method-level generic parameters
                // are in scope (e.g., RxTime→Double instead of Date).
                if (!EveryProtocolEmitter.IsMixedGenericProtocol(protocolDecl))
                    dispatchEmitter.EmitWitnessDispatchFunctions(swiftWriter, protocolDecl);
            }
        }

        /// <summary>
        /// Pre-computes the set of method full signatures that must be emitted non-throwing.
        /// A signature is included if it appears as both throwing (in at least one protocol)
        /// and non-throwing (in at least one other protocol). The non-throwing variant must
        /// win because it satisfies both requirements.
        /// Uses full signatures (name + param types + return type) so that overloads with
        /// different parameter types are tracked independently — e.g., a non-throwing
        /// validate(input: String) won't suppress throws on validate(input: Int32) throws.
        /// </summary>
        private static HashSet<string> ComputeNonThrowingOverrides(
            IEnumerable<ProtocolDecl> protocols, EveryProtocolEmitter emitter)
        {
            var throwingSignatures = new HashSet<string>();
            var nonThrowingSignatures = new HashSet<string>();

            foreach (var protocol in protocols)
            {
                foreach (var method in protocol.Methods)
                {
                    if (method.IsConstructor || method.MethodType == MethodType.Static)
                        continue;

                    var sig = emitter.GetSwiftMethodFullSignature(method);
                    if (method.Throws)
                        throwingSignatures.Add(sig);
                    else
                        nonThrowingSignatures.Add(sig);
                }
            }

            // Only override signatures that appear in BOTH sets (i.e., a real conflict exists)
            nonThrowingSignatures.IntersectWith(throwingSignatures);
            return nonThrowingSignatures;
        }

        /// <summary>
        /// Checks if a protocol inherits from Decodable, Encodable, or Codable,
        /// either directly or transitively through inherited protocols.
        /// EveryProtocol's handle: UnsafeRawPointer? property cannot synthesize Codable
        /// conformance, so protocols requiring it must be skipped.
        /// </summary>
        /// <param name="protocolDecl">The protocol to check.</param>
        /// <param name="allProtocols">All protocols in the module for intra-module transitive lookup.
        /// If null, only direct inheritance is checked.</param>
        /// <param name="typeDatabase">Type database for cross-module transitive lookup via
        /// TypeRecordFlags.InheritsCodable. If null, only intra-module lookup is used.</param>
        internal static bool InheritsCodable(ProtocolDecl protocolDecl, IReadOnlyList<ProtocolDecl>? allProtocols = null, ITypeDatabase? typeDatabase = null)
        {
            return InheritsCodableRecursive(protocolDecl, allProtocols, typeDatabase, new HashSet<string>(StringComparer.Ordinal));
        }

        private static bool InheritsCodableRecursive(ProtocolDecl protocolDecl, IReadOnlyList<ProtocolDecl>? allProtocols, ITypeDatabase? typeDatabase, HashSet<string> visited)
        {
            // Prevent infinite loops in circular inheritance chains
            var qualifiedName = protocolDecl.SwiftTypeName?.ToString() ?? protocolDecl.Name;
            if (!visited.Add(qualifiedName))
                return false;

            foreach (var inherited in protocolDecl.InheritedProtocols)
            {
                var name = inherited.Name;
                // Strip module prefix if present (e.g., "Swift.Decodable" → "Decodable")
                var dotIndex = name.LastIndexOf('.');
                var simpleName = dotIndex >= 0 ? name.Substring(dotIndex + 1) : name;

                if (simpleName is "Decodable" or "Encodable" or "Codable")
                    return true;

                // Intra-module transitive check: look up the inherited protocol in the module
                if (allProtocols != null)
                {
                    var inheritedDecl = allProtocols.FirstOrDefault(p =>
                        p.Name == simpleName || p.Name == name ||
                        p.SwiftTypeName?.ToString() == name);
                    if (inheritedDecl != null && InheritsCodableRecursive(inheritedDecl, allProtocols, typeDatabase, visited))
                        return true;
                }

                // Cross-module transitive check: look up the inherited protocol's TypeRecord
                // in the type database. Dependency modules are processed before the main module,
                // so their InheritsCodable flags are already set.
                if (typeDatabase != null)
                {
                    var inheritedSwiftName = SwiftTypeName.FromModuleQualifiedName(name);
                    if (typeDatabase.TryGetTypeRecord(inheritedSwiftName, out var record) &&
                        record.Kind == TypeRecordKind.Protocol &&
                        record.Flags.HasFlag(TypeRecordFlags.InheritsCodable))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Known cross-module protocols with associated types or Self requirements that
        /// may not be present in the type database (e.g., Foundation types without .NET
        /// bindings). Any protocol inheriting from one of these cannot receive an
        /// EveryProtocol conformance.
        /// </summary>
        private static readonly HashSet<string> KnownCrossModuleProtocolsWithAssociatedTypes = new(StringComparer.Ordinal)
        {
            // Foundation predicate expression DSL (iOS 17+). Has associated type Output
            // and is implemented by a closed set of compiler-known expression structs.
            "Foundation.PredicateExpression",
            "Foundation.StandardPredicateExpression",
        };

        /// <summary>
        /// Checks if a protocol transitively inherits from any protocol with associated types
        /// or Self requirements. These protocols cannot get EveryProtocol conformances because
        /// the associated type cannot be determined.
        /// </summary>
        internal static bool InheritsProtocolWithAssociatedTypes(ProtocolDecl protocolDecl, IReadOnlyList<ProtocolDecl>? allProtocols = null, ITypeDatabase? typeDatabase = null)
        {
            return InheritsProtocolWithAssociatedTypesRecursive(protocolDecl, allProtocols, typeDatabase, new HashSet<string>(StringComparer.Ordinal));
        }

        private static bool InheritsProtocolWithAssociatedTypesRecursive(ProtocolDecl protocolDecl, IReadOnlyList<ProtocolDecl>? allProtocols, ITypeDatabase? typeDatabase, HashSet<string> visited)
        {
            var qualifiedName = protocolDecl.SwiftTypeName?.ToString() ?? protocolDecl.Name;
            if (!visited.Add(qualifiedName))
                return false;

            // InheritedProtocols may be empty due to ABI JSON conformance parsing (Kind mismatch).
            // Use GenericSignature as a fallback to extract parent protocol names.
            // GenericSignature format: "<Self : Module.ParentProtocol1, Self : Module.ParentProtocol2>"
            var parentNames = new List<string>();

            // Collect from InheritedProtocols (populated when conformance Kind matches)
            foreach (var inherited in protocolDecl.InheritedProtocols)
                parentNames.Add(inherited.Name);

            // Fallback: parse GenericSignature for "Self : Module.Protocol" constraints
            if (parentNames.Count == 0 && !string.IsNullOrEmpty(protocolDecl.GenericSignature))
            {
                var sig = protocolDecl.GenericSignature;
                // Match "Self : Module.ProtocolName" or "τ_0_0 : Module.ProtocolName" patterns
                var idx = 0;
                while (idx < sig.Length)
                {
                    var colonIdx = sig.IndexOf(" : ", idx);
                    if (colonIdx < 0) break;
                    var nameStart = colonIdx + 3;
                    // Find end of the name (comma, '>', or end of string)
                    var nameEnd = sig.IndexOfAny(new[] { ',', '>' }, nameStart);
                    if (nameEnd < 0) nameEnd = sig.Length;
                    var constraintName = sig.Substring(nameStart, nameEnd - nameStart).Trim();
                    if (!string.IsNullOrEmpty(constraintName))
                        parentNames.Add(constraintName);
                    idx = nameEnd + 1;
                }
            }

            foreach (var name in parentNames)
            {
                var dotIndex = name.LastIndexOf('.');
                var simpleName = dotIndex >= 0 ? name.Substring(dotIndex + 1) : name;

                // Hardcoded cross-module list: catches Foundation.PredicateExpression and
                // similar protocols that may not be registered in the type database but are
                // known to carry associated-type requirements EveryProtocol cannot satisfy.
                if (KnownCrossModuleProtocolsWithAssociatedTypes.Contains(name))
                    return true;

                // Intra-module check: look up the inherited protocol in the module
                if (allProtocols != null)
                {
                    var inheritedDecl = allProtocols.FirstOrDefault(p =>
                        p.Name == simpleName || p.Name == name ||
                        p.SwiftTypeName?.ToString() == name);
                    if (inheritedDecl != null)
                    {
                        if (inheritedDecl.AssociatedTypes.Count > 0 || inheritedDecl.HasSelfRequirement)
                            return true;
                        if (InheritsProtocolWithAssociatedTypesRecursive(inheritedDecl, allProtocols, typeDatabase, visited))
                            return true;
                    }
                }

                // Cross-module check: look up in type database
                // Only for module-qualified names (contains a dot)
                if (typeDatabase != null && dotIndex >= 0)
                {
                    var inheritedSwiftName = SwiftTypeName.FromModuleQualifiedName(name);
                    if (typeDatabase.TryGetTypeRecord(inheritedSwiftName, out var record) &&
                        record.Kind == TypeRecordKind.Protocol &&
                        (record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes) ||
                         record.Flags.HasFlag(TypeRecordFlags.HasSelfRequirement)))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Checks if a Swift mangled name belongs to the given module.
        /// Swift encodes module names in mangled symbols as $s{length}{moduleName}...
        /// (e.g., $s11CryptoSwift...). Stdlib protocols use abbreviated forms ($sSl, $sSB, $ss...).
        /// Also accepts the umbrella prefix when the source module is exposed through Apple's
        /// `@_implementationOnly` re-export (e.g., RealityFoundation protocols carry
        /// $s10RealityKit... mangling because RealityKit is the umbrella declared in
        /// apple-frameworks.json's compileImportModule). Without the umbrella branch, those
        /// protocols would be silently filtered out of the source module's emission pass.
        /// </summary>
        internal static bool IsMangledNameFromModule(string mangledName, string moduleName)
        {
            if (string.IsNullOrEmpty(mangledName) || string.IsNullOrEmpty(moduleName))
                return false;

            // The expected mangled prefix is "$s" + length + moduleName
            var expectedPrefix = $"$s{moduleName.Length}{moduleName}";
            if (mangledName.StartsWith(expectedPrefix, StringComparison.Ordinal))
                return true;

            var umbrella = AppleFrameworkRegistry.MapModuleToCompileImport(moduleName);
            if (!string.IsNullOrEmpty(umbrella) && !string.Equals(umbrella, moduleName, StringComparison.Ordinal))
            {
                var umbrellaPrefix = $"$s{umbrella.Length}{umbrella}";
                if (mangledName.StartsWith(umbrellaPrefix, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true if the protocol has any non-static member whose type references an
        /// unsupported module (SwiftUI, Combine) that is not registered in the type database.
        /// Used to skip EveryProtocol conformance and C# proxy emission for protocols whose
        /// requirements can't be satisfied.
        /// </summary>
        internal static bool HasMembersReferencingUnsupportedModule(ProtocolDecl protocolDecl, ITypeDatabase? typeDatabase = null)
        {
            foreach (var property in protocolDecl.Properties)
            {
                if (property.IsStatic) continue;
                if (MemberEmissionValidator.ReferencesUnsupportedModule(property.SwiftTypeSpec, typeDatabase))
                    return true;
            }
            foreach (var method in protocolDecl.Methods)
            {
                if (method.IsConstructor || method.MethodType == MethodType.Static) continue;
                foreach (var arg in method.CSSignature)
                {
                    if (MemberEmissionValidator.ReferencesUnsupportedModule(arg.SwiftTypeSpec, typeDatabase))
                        return true;
                }
            }
            foreach (var subscript in protocolDecl.Subscripts)
            {
                if (subscript.IsStatic) continue;
                if (MemberEmissionValidator.ReferencesUnsupportedModule(subscript.ReturnTypeSpec, typeDatabase))
                    return true;
                foreach (var param in subscript.IndexParameters)
                {
                    if (MemberEmissionValidator.ReferencesUnsupportedModule(param.SwiftTypeSpec, typeDatabase))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Returns true if the protocol has any non-static member whose type references a type
        /// from the current module that is not registered in the type database. Such types are
        /// likely module-internal and will cause compilation errors when EveryProtocol tries to
        /// conform to the protocol (the wrapper module cannot access internal types).
        /// </summary>
        internal static bool HasMembersReferencingInternalTypes(ProtocolDecl protocolDecl, ITypeDatabase typeDatabase, string moduleName)
        {
            foreach (var property in protocolDecl.Properties)
            {
                if (property.IsStatic) continue;
                if (ReferencesInternalModuleType(property.SwiftTypeSpec, typeDatabase, moduleName))
                    return true;
            }
            foreach (var method in protocolDecl.Methods)
            {
                if (method.IsConstructor || method.MethodType == MethodType.Static) continue;
                foreach (var arg in method.CSSignature)
                {
                    if (ReferencesInternalModuleType(arg.SwiftTypeSpec, typeDatabase, moduleName))
                        return true;
                }
            }
            foreach (var subscript in protocolDecl.Subscripts)
            {
                if (subscript.IsStatic) continue;
                if (ReferencesInternalModuleType(subscript.ReturnTypeSpec, typeDatabase, moduleName))
                    return true;
                foreach (var param in subscript.IndexParameters)
                {
                    if (ReferencesInternalModuleType(param.SwiftTypeSpec, typeDatabase, moduleName))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Recursively checks if a TypeSpec references a type from the specified module that
        /// is not registered in the type database (indicating it is module-internal).
        /// </summary>
        private static bool ReferencesInternalModuleType(TypeSpec? typeSpec, ITypeDatabase typeDatabase, string moduleName)
        {
            if (typeSpec == null)
                return false;

            switch (typeSpec)
            {
                case NamedTypeSpec namedType:
                    // Generic type parameters (τ_0_0, τ_1_0, T, etc.) are not concrete types —
                    // they don't need to be in the type database and are never internal.
                    if (TypeSpecHelpers.IsGenericTypeParameter(namedType.Name))
                        return false;
                    if (namedType.HasModule())
                    {
                        var typeModule = namedType.Module;
                        // Only check types from the current module — types from other modules
                        // are either imported or from the standard library.
                        if (typeModule == moduleName)
                        {
                            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
                            if (!typeDatabase.TryGetTypeRecord(swiftTypeName, out _))
                                return true; // From this module but not in DB → likely internal
                        }
                    }
                    else
                    {
                        // Unqualified type name — ABI JSON sometimes omits the module prefix
                        // for types in the current module. Try resolving with the module name.
                        var qualifiedName = $"{moduleName}.{namedType.Name}";
                        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(qualifiedName);
                        if (!typeDatabase.TryGetTypeRecord(swiftTypeName, out _))
                        {
                            // Not found with module prefix either — could be a stdlib type
                            // (e.g., "Int", "String") or genuinely internal. Only flag as
                            // internal if it's not a well-known Swift/stdlib type.
                            var stdlibName = SwiftTypeName.FromModuleQualifiedName($"Swift.{namedType.Name}");
                            if (!typeDatabase.TryGetTypeRecord(stdlibName, out _))
                                return true; // Not in module DB or stdlib → likely internal
                        }
                    }
                    foreach (var genericParam in namedType.GenericParameters)
                    {
                        if (ReferencesInternalModuleType(genericParam, typeDatabase, moduleName))
                            return true;
                    }
                    return false;

                case TupleTypeSpec tupleType:
                    foreach (var element in tupleType.Elements)
                    {
                        if (ReferencesInternalModuleType(element, typeDatabase, moduleName))
                            return true;
                    }
                    return false;

                case ClosureTypeSpec closureType:
                    if (ReferencesInternalModuleType(closureType.Arguments, typeDatabase, moduleName))
                        return true;
                    if (ReferencesInternalModuleType(closureType.ReturnType, typeDatabase, moduleName))
                        return true;
                    return false;

                case ProtocolListTypeSpec protocolList:
                    foreach (var protocol in protocolList.Protocols.Keys)
                    {
                        if (ReferencesInternalModuleType(protocol, typeDatabase, moduleName))
                            return true;
                    }
                    return false;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Emits a wrap-only proxy class for a composition interface.
        /// The proxy wraps a Swift existential container; member access throws NotSupportedException.
        /// </summary>
        private void EmitCompositionProxy(CSharpWriter csWriter, string compositionName, List<string> parentInterfaces,
            ModuleDecl moduleDecl, ITypeDatabase typeDatabase)
        {
            var proxyClassName = compositionName.Substring(1) + "Proxy"; // Strip leading "I", add "Proxy"
            var protocolCount = parentInterfaces.Count;
            var containerType = $"Swift.Runtime.ExistentialContainer{protocolCount}";

            csWriter.WriteLine();
            csWriter.WriteLine($"/// <summary>");
            csWriter.WriteLine($"/// Wrap-only proxy for the {compositionName} composition existential.");
            csWriter.WriteLine($"/// Wraps a Swift existential container; member access is not supported.");
            csWriter.WriteLine($"/// </summary>");
            csWriter.WriteLine("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
            csWriter.WriteLine($"public unsafe class {proxyClassName} : {compositionName}, ISwiftObject, IDisposable, Swift.Runtime.ISwiftExistentialConvertible<{containerType}>");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // Field
            csWriter.WriteLine($"private readonly {containerType} _swiftContainer;");
            csWriter.WriteLine();

            // Constructor from container
            csWriter.WriteLine($"public {proxyClassName}({containerType} container)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            csWriter.WriteLine("_swiftContainer = container;");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // ISwiftExistentialConvertible (explicit interface implementation to hide from public API)
            csWriter.WriteLine($"{containerType} ISwiftExistentialConvertible<{containerType}>.GetExistentialContainer() => _swiftContainer;");
            csWriter.WriteLine();

            // ISwiftObject implementation
            csWriter.WriteLines($$"""
                IntPtr ISwiftObject.SwiftHandle => _swiftContainer.Payload0;

                public static TypeMetadata GetTypeMetadata()
                {
                    throw new NotSupportedException("Composition proxy has no single EveryProtocol metadata.");
                }

                public static ISwiftObject NewFromPayload(IntPtr payload)
                {
                    return new {{proxyClassName}}(*({{containerType}}*)payload);
                }

                public int MarshalToSwift(ref Span<byte> swiftDestSpan)
                {
                    var size = _swiftContainer.SizeOf;
                    if (swiftDestSpan.Length < size)
                        throw new ArgumentException("Destination span too small", nameof(swiftDestSpan));
                    fixed ({{containerType}}* containerPtr = &_swiftContainer)
                    {
                        new Span<byte>(containerPtr, size).CopyTo(swiftDestSpan);
                    }
                    return size;
                }

                public static ProtocolConformanceDescriptor GetProtocolConformanceDescriptor<TProtocol>() where TProtocol : class
                {
                    throw new NotSupportedException("Composition proxy does not support protocol conformance descriptors.");
                }

                public void Dispose() { }
                """);
            csWriter.WriteLine();

            // Emit stub implementations for all interface members
            EmitCompositionMemberStubs(csWriter, parentInterfaces, moduleDecl, typeDatabase);

            csWriter.Indent--;
            csWriter.WriteLine("}");
        }

        /// <summary>
        /// Emits NotSupportedException stub implementations for all inherited interface members.
        /// </summary>
        private void EmitCompositionMemberStubs(CSharpWriter csWriter, List<string> parentInterfaces,
            ModuleDecl moduleDecl, ITypeDatabase typeDatabase)
        {
            var emittedProperties = new HashSet<string>();
            var emittedMethods = new HashSet<string>();

            foreach (var interfaceName in parentInterfaces)
            {
                // Resolve interface name (e.g., "ICryptor") → protocol name (e.g., "Cryptor")
                var protocolName = interfaceName.StartsWith("I") ? interfaceName.Substring(1) : interfaceName;
                var protocolDecl = moduleDecl.Protocols?.FirstOrDefault(p => p.Name == protocolName);
                if (protocolDecl == null)
                    continue;

                // Properties
                foreach (var property in protocolDecl.Properties)
                {
                    if (property.IsStatic)
                        continue;
                    if (!emittedProperties.Add(property.Name))
                        continue;

                    var csharpType = ResolvePropertyType(property, typeDatabase);
                    var propertyName = NameProvider.GetPropertyName(property.Name);
                    var hasGetter = property.Accessors.OfType<GetAccessorDecl>().Any();
                    var hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();

                    csWriter.WriteLine($"public {csharpType} {propertyName}");
                    csWriter.WriteLine("{");
                    csWriter.Indent++;
                    if (hasGetter)
                        csWriter.WriteLine($"get => throw new NotSupportedException(\"Cannot access member on Swift-backed composition existential.\");");
                    if (hasSetter)
                        csWriter.WriteLine($"set => throw new NotSupportedException(\"Cannot access member on Swift-backed composition existential.\");");
                    csWriter.Indent--;
                    csWriter.WriteLine("}");
                    csWriter.WriteLine();
                }

                // Methods
                foreach (var method in protocolDecl.Methods)
                {
                    if (method.IsConstructor || method.MethodType == MethodType.Static)
                        continue;

                    var methodKey = ProtocolSignatureHelper.GetMethodSignatureKey(method, typeDatabase, protocolDecl);
                    if (!emittedMethods.Add(methodKey))
                        continue;

                    var returnType = ResolveMethodReturnType(method, typeDatabase);
                    var parameters = ResolveMethodParameters(method, typeDatabase);
                    bool hasReturnValue = method.CSSignature.Count > 0 && !method.CSSignature.First().SwiftTypeSpec.IsEmptyTuple;
                    var methodName = NameProvider.GetPublicMethodName(method.Name, method.IsAsync, hasReturnValue,
                        parameterCount: method.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple));

                    if (method.IsAsync)
                    {
                        returnType = returnType == "void" ? "Task" : $"Task<{returnType}>";
                    }

                    csWriter.WriteLine($"public {returnType} {methodName}({string.Join(", ", parameters)})");
                    csWriter.Indent++;
                    csWriter.WriteLine($"=> throw new NotSupportedException(\"Cannot call method on Swift-backed composition existential.\");");
                    csWriter.Indent--;
                    csWriter.WriteLine();
                }
            }
        }

        /// <summary>
        /// Resolves a property's C# type using the same chain as ProtocolHandler.EmitInterfaceProperty.
        /// </summary>
        private static string ResolvePropertyType(PropertyDecl property, ITypeDatabase typeDatabase)
        {
            return ResolveCSharpTypeName(property.SwiftTypeSpec, typeDatabase, isParameter: false);
        }

        /// <summary>
        /// Resolves a method's return type using the same chain as ProtocolHandler.EmitInterfaceMethod.
        /// </summary>
        private static string ResolveMethodReturnType(MethodDecl method, ITypeDatabase typeDatabase)
        {
            if (method.CSSignature.Count == 0) return "void";
            var returnArg = method.CSSignature[0];
            if (returnArg.SwiftTypeSpec is TupleTypeSpec tuple && tuple.IsEmptyTuple) return "void";
            return ResolveCSharpTypeName(returnArg.SwiftTypeSpec, typeDatabase, isParameter: false);
        }

        /// <summary>
        /// Resolves method parameters to C# parameter declarations.
        /// </summary>
        private static List<string> ResolveMethodParameters(MethodDecl method, ITypeDatabase typeDatabase)
        {
            var parameters = new List<string>();
            for (int i = 1; i < method.CSSignature.Count; i++)
            {
                var arg = method.CSSignature[i];
                // Skip debug params and empty tuple () params (zero-sized Void)
                if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                    continue;
                if (arg.SwiftTypeSpec.IsEmptyTuple)
                    continue;
                var paramTypeName = ResolveCSharpTypeName(arg.SwiftTypeSpec, typeDatabase);
                var paramName = NameProvider.GetCSharpParameterName(arg);
                parameters.Add($"{paramTypeName} {paramName}");
            }
            return parameters;
        }

        /// <summary>
        /// Resolves a TypeSpec to its C# type name, handling closures, tuples, existentials, bound generics,
        /// and standard types. Mirrors ProtocolHandler.GetCSharpTypeName() for composition proxy stubs.
        /// </summary>
        private static string ResolveCSharpTypeName(TypeSpec typeSpec, ITypeDatabase typeDatabase, bool isParameter = true)
        {
            // Factory-first with GenericContext: handles all types including bound generics
            var factory = new TypeProjectionFactory();
            var projection = factory.Project(typeSpec, new ProjectionContext
            {
                TypeDatabase = typeDatabase,
                IsParameter = isParameter,
                GenericContext = GenericContext.Empty
            });
            if (projection != null)
                return projection.PublicType;

            // Bound generic fallback: produce raw ABI type name with generic args
            if (typeSpec is NamedTypeSpec boundGeneric && boundGeneric.ContainsGenericParameters)
            {
                var bgh = new BoundGenericsHandler(typeDatabase);
                return bgh.TranslateBoundGenericTypeToCSharp(typeSpec, GenericContext.Empty);
            }

            // Standard type lookup
            return typeDatabase.GetTypeRecordOrAnyType(typeSpec).CSharpTypeName.FullyQualifiedName;
        }

    }
}
