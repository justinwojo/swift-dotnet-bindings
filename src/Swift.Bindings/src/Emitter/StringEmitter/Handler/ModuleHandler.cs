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

            // Reset per-module state for shared emitters
            EnumHandler.ResetUtf8SliceTracking();
            CancellationTaskEmitter.ResetForModule();

            // Emit Swift imports at the top of the Swift wrapper file
            EmitSwiftImports(swiftWriter, moduleDecl);

            // Emit EveryProtocol class and protocol conformances for Swift side
            EmitEveryProtocolConformances(swiftWriter, moduleDecl, env.TypeDatabase);

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
            csWriter.WriteLine();
            csWriter.WriteLine($"namespace {generatedNamespace}");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // Pre-compute all nested type renames before emitting any types.
            // This ensures cross-type references to renamed nested types resolve correctly
            // regardless of emission order (e.g., type B referencing A.Cache which was
            // renamed to A.CacheInfo won't fail if B is emitted before A).
            NameProvider.PrecomputeAllNestedTypeRenames(moduleDecl.Types, env.TypeDatabase);

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
                    foreach (MethodDecl methodDecl in moduleDecl.Methods)
                    {
                        if (conductor.TryGetMethodHandler(methodDecl, out var methodHandler))
                        {
                            var methodEnv = new MethodEnvironment(methodDecl, env.TypeDatabase, compositionCollector: context.CompositionCollector);
                            methodHandler.Emit(csWriter, swiftWriter, methodEnv, conductor, context);
                        }
                        else
                        {
                            _logger.LogWarning($"No handler found for method {methodDecl.Name}");
                            ReportCollector.RecordMemberSkipped(BindingItemKind.Method, methodDecl.Name, moduleDecl, SkipReason.MissingHandler, "No method handler found for top-level method.");
                        }
                        csWriter.WriteLine();
                    }
                    csWriter.Indent--;
                    csWriter.WriteLine("}");
                    csWriter.WriteLine();
                }

                base.HandleBaseDecl(csWriter, swiftWriter, moduleDecl.Types, conductor, env.TypeDatabase, context);

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

            // Emit DllImport framework resolver with [ModuleInitializer]
            EmitFrameworkResolver(csWriter, moduleDecl.Name);

            csWriter.Indent--;
            csWriter.WriteLine("}");

        }

        /// <summary>
        /// Emits a static class with a [ModuleInitializer] that registers a NativeLibrary.SetDllImportResolver
        /// to resolve DllImport names as @rpath/{name}.framework/{name} for iOS framework loading.
        /// </summary>
        private static void EmitFrameworkResolver(CSharpWriter csWriter, string moduleName)
        {
            csWriter.WriteLine();
            csWriter.WriteLine("#pragma warning disable CA2255 // ModuleInitializer is intentional in generated binding code");
            csWriter.WriteLines($$"""
                internal static class __SwiftFrameworkResolver_{{moduleName}}
                {
                    [ModuleInitializer]
                    internal static void Initialize()
                    {
                        try
                        {
                            NativeLibrary.SetDllImportResolver(typeof(__SwiftFrameworkResolver_{{moduleName}}).Assembly, (libraryName, assembly, searchPath) =>
                            {
                                var frameworkPath = $"@rpath/{libraryName}.framework/{libraryName}";
                                if (NativeLibrary.TryLoad(frameworkPath, out var handle))
                                    return handle;
                                return IntPtr.Zero;
                            });
                        }
                        catch (InvalidOperationException)
                        {
                            // A resolver is already registered for this assembly.
                        }
                    }
                }
                """);
            csWriter.WriteLine("#pragma warning restore CA2255");
        }

        /// <summary>
        /// Known Apple frameworks that may need to be imported.
        /// </summary>
        private static readonly HashSet<string> AppleFrameworks = new()
        {
            "UIKit", "AppKit", "CoreGraphics", "CoreText", "QuartzCore",
            "CoreFoundation", "CoreImage", "CoreAnimation", "CoreMedia",
            "AVFoundation", "SceneKit", "SpriteKit", "Metal", "MetalKit",
            "GameplayKit", "MapKit", "CoreLocation", "CloudKit", "StoreKit",
            "HealthKit", "HomeKit", "WatchKit", "ARKit", "RealityKit",
            "PDFKit", "WebKit", "SafariServices", "AuthenticationServices",
            "LocalAuthentication", "Security", "CryptoKit", "Combine",
            "SwiftUI", "UniformTypeIdentifiers", "CoreData", "CoreML",
            "Vision", "NaturalLanguage", "Speech", "SoundAnalysis",
            "Accelerate", "simd", "Compression", "OSLog", "os"
        };

        /// <summary>
        /// Emits Swift import statements to the wrapper file.
        /// </summary>
        private void EmitSwiftImports(SwiftWriter swiftWriter, ModuleDecl moduleDecl)
        {
            // Always import the module being bound
            swiftWriter.WriteLine($"import {moduleDecl.Name}");
            swiftWriter.WriteLine("import Foundation");

            // Track which framework imports are needed
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

            foreach (var import in neededImports.OrderBy(s => s))
            {
                swiftWriter.WriteLine($"import {import}");
            }

            // Import dependency modules (from --framework-dependency)
            foreach (var depModule in moduleDecl.DependencyModuleNames.OrderBy(s => s))
            {
                if (depModule != moduleDecl.Name && !neededImports.Contains(depModule))
                {
                    swiftWriter.WriteLine($"import {depModule}");
                }
            }

            swiftWriter.WriteLine();

            // Emit SBW_Utf8Slice and SBW_Free at module level (before any functions)
            // These are needed for async String returns and may be used elsewhere.
            // Emitting unconditionally is safe - small structs/functions that do no harm if unused.
            // SBW_Free uses module-specific symbol name to avoid collisions if multiple modules
            // are linked into the same wrapper library.
            Utf8SliceEmitter.EmitIfNeeded(swiftWriter);
            Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter, moduleDecl.Name);

            // Emit Swift Task cancellation infrastructure (cancel function + task dictionary)
            CancellationTaskEmitter.EmitIfNeeded(swiftWriter, moduleDecl.Name);
        }

        /// <summary>
        /// Recursively scans types for async methods that return framework types.
        /// </summary>
        private void ScanTypesForFrameworkImports(IEnumerable<TypeDecl> types, HashSet<string> neededImports)
        {
            foreach (var type in types)
            {
                // Check methods
                var methods = type switch
                {
                    StructDecl s => s.Methods,
                    ClassDecl c => c.Methods,
                    _ => Enumerable.Empty<MethodDecl>()
                };

                foreach (var method in methods)
                {
                    // Check async methods - their return types appear in Swift callbacks
                    if (method.IsAsync && method.CSSignature.Count > 0)
                    {
                        var returnType = method.CSSignature.First();
                        ScanTypeSpecForImports(returnType.SwiftTypeSpec, neededImports);
                    }
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
        /// Checks if a type name requires a framework import.
        /// </summary>
        private void CheckTypeNameForFrameworkImport(string? typeName, HashSet<string> neededImports)
        {
            if (string.IsNullOrEmpty(typeName))
                return;

            // Extract the module/framework name from the type name
            var dotIndex = typeName.IndexOf('.');
            if (dotIndex > 0)
            {
                var moduleName = typeName.Substring(0, dotIndex);
                if (AppleFrameworks.Contains(moduleName))
                {
                    neededImports.Add(moduleName);
                }
            }
        }

        /// <summary>
        /// Emits the EveryProtocol class and protocol conformances for Swift side.
        /// This enables C# code to implement Swift protocols by providing vtable callbacks.
        /// </summary>
        private void EmitEveryProtocolConformances(SwiftWriter swiftWriter, ModuleDecl moduleDecl, ITypeDatabase typeDatabase)
        {
            // Skip if there are no protocols to conform to
            var protocols = moduleDecl.Protocols;
            if (protocols == null || !protocols.Any())
                return;

            // Check if any protocols are suitable for EveryProtocol conformance
            var suitableProtocols = protocols
                .Where(p => !p.HasSelfRequirement && p.AssociatedTypes.Count == 0)
                .Where(p => p.Properties.Any() ||
                           p.Methods.Any(m => !m.IsConstructor && m.MethodType != MethodType.Static) ||
                           p.Subscripts.Any())
                // Bug #14: Filter out protocols not actually defined in this module.
                // When a module extends stdlib protocols (e.g., CryptoSwift extends Collection),
                // the parser creates ProtocolDecl entries with the module's name (CryptoSwift.Collection),
                // but these are stdlib protocols re-exported by the module — not defined in it.
                // Check the mangled name: module-defined protocols encode the module name as
                // $s{length}{moduleName}... (e.g., $s11CryptoSwift...), while stdlib protocols
                // use abbreviated forms ($sSl, $sSB, $ss17...).
                .Where(p => IsMangledNameFromModule(p.MangledName, moduleDecl.Name))
                .Where(p => !HasMembersReferencingUnsupportedModule(p))
                .ToList();

            if (!suitableProtocols.Any())
                return;

            var emitter = new EveryProtocolEmitter(typeDatabase, _logger, moduleDecl.Name);
            var dispatchEmitter = new WitnessDispatchEmitter(typeDatabase, _logger, moduleDecl.Name);

            // Emit the EveryProtocol class once
            emitter.EmitEveryProtocolClass(swiftWriter);

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
                dispatchEmitter.EmitWitnessDispatchFunctions(swiftWriter, protocolDecl);
            }
        }

        /// <summary>
        /// Pre-computes the set of method signatures that must be emitted non-throwing.
        /// A signature is included if it appears as both throwing (in at least one protocol)
        /// and non-throwing (in at least one other protocol). The non-throwing variant must
        /// win because it satisfies both requirements.
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

                    var sig = emitter.GetSwiftMethodSignature(method);
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
        /// Checks if a Swift mangled name belongs to the given module.
        /// Swift encodes module names in mangled symbols as $s{length}{moduleName}...
        /// (e.g., $s11CryptoSwift...). Stdlib protocols use abbreviated forms ($sSl, $sSB, $ss...).
        /// </summary>
        internal static bool IsMangledNameFromModule(string mangledName, string moduleName)
        {
            if (string.IsNullOrEmpty(mangledName) || string.IsNullOrEmpty(moduleName))
                return false;

            // The expected mangled prefix is "$s" + length + moduleName
            var expectedPrefix = $"$s{moduleName.Length}{moduleName}";
            return mangledName.StartsWith(expectedPrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Returns true if the protocol has any non-static member whose type references an
        /// unsupported module (SwiftUI, Combine). Used to skip EveryProtocol conformance and
        /// C# proxy emission for protocols whose requirements can't be satisfied.
        /// </summary>
        internal static bool HasMembersReferencingUnsupportedModule(ProtocolDecl protocolDecl)
        {
            foreach (var property in protocolDecl.Properties)
            {
                if (property.IsStatic) continue;
                if (MemberEmissionValidator.ReferencesUnsupportedModule(property.SwiftTypeSpec))
                    return true;
            }
            foreach (var method in protocolDecl.Methods)
            {
                if (method.IsConstructor || method.MethodType == MethodType.Static) continue;
                foreach (var arg in method.CSSignature)
                {
                    if (MemberEmissionValidator.ReferencesUnsupportedModule(arg.SwiftTypeSpec))
                        return true;
                }
            }
            foreach (var subscript in protocolDecl.Subscripts)
            {
                if (subscript.IsStatic) continue;
                if (MemberEmissionValidator.ReferencesUnsupportedModule(subscript.ReturnTypeSpec))
                    return true;
                foreach (var param in subscript.IndexParameters)
                {
                    if (MemberEmissionValidator.ReferencesUnsupportedModule(param.SwiftTypeSpec))
                        return true;
                }
            }
            return false;
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

            // ISwiftExistentialConvertible
            csWriter.WriteLine($"public {containerType} GetExistentialContainer() => _swiftContainer;");
            csWriter.WriteLine();

            // ISwiftObject implementation
            csWriter.WriteLines($$"""
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
                    var methodName = NameProvider.GetPublicMethodName(method.Name, method.IsAsync, hasReturnValue);

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
