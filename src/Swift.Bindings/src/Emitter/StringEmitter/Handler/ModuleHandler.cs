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
        /// <summary>
        /// Initializes a new instance of the <see cref="ModuleHandlerFactory"/> class.
        /// </summary>
        /// <param name="loggerFactory">The logger factory instance.</param>
        public ModuleHandlerFactory(ILoggerFactory loggerFactory) : base(loggerFactory.CreateLogger<ModuleHandler>())
        {
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
            return new ModuleHandler(_handlerLogger);
        }
    }

    /// <summary>
    /// Handler class for module declarations.
    /// </summary>
    public class ModuleHandler : BaseHandler, IModuleHandler
    {
        public ModuleHandler(ILogger logger) : base(logger)
        {
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
        public void Emit(CSharpWriter csWriter, SwiftWriter swiftWriter, IEnvironment env, Conductor conductor)
        {
            var moduleEnv = (ModuleEnvironment)env;
            var moduleDecl = moduleEnv.ModuleDecl;

            // Emit Swift imports at the top of the Swift wrapper file
            EmitSwiftImports(swiftWriter, moduleDecl);

            // Emit EveryProtocol class and protocol conformances for Swift side
            EmitEveryProtocolConformances(swiftWriter, moduleDecl, env.TypeDatabase);

            var generatedNamespace = $"Swift.{moduleDecl.Name}";

            csWriter.WriteLine($"using System;");
            csWriter.WriteLine($"using System.Diagnostics;");
            csWriter.WriteLine($"using System.Diagnostics.CodeAnalysis;");
            csWriter.WriteLine($"using System.Runtime.CompilerServices;");
            csWriter.WriteLine($"using System.Runtime.InteropServices;");
            csWriter.WriteLine($"using System.Runtime.InteropServices.Swift;");
            csWriter.WriteLine($"using Swift;");
            csWriter.WriteLine($"using Swift.Runtime;");
            csWriter.WriteLine($"using Swift.Runtime.InteropServices;");
            csWriter.WriteLine();
            csWriter.WriteLine($"namespace {generatedNamespace}");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // Emit helper structs for tracking retained self pointers in async operations
            csWriter.WriteLines("""
                /// <summary>
                /// Wraps a retained Swift class pointer for async operations.
                /// Used to track self pointers that were explicitly retained via Arc.Retain()
                /// before calling async Swift methods. Must be released via Arc.Release() after callback.
                /// </summary>
                internal readonly struct RetainedSelfPtr
                {
                    public readonly IntPtr Ptr;
                    public RetainedSelfPtr(IntPtr ptr) => Ptr = ptr;
                }

                /// <summary>
                /// Wraps a SafeHandle that needs DangerousRelease() called after async completion.
                /// Used for async instance methods on structs where the SafeHandle must stay alive
                /// until the Swift async operation completes.
                /// </summary>
                internal readonly struct DeferredSafeHandleRelease
                {
                    public readonly SafeHandle Handle;
                    public DeferredSafeHandleRelease(SafeHandle handle) => Handle = handle;
                }

                /// <summary>
                /// Wraps a copy buffer pointer with its TypeMetadata for proper cleanup.
                /// Used for non-frozen struct parameters in async operations.
                /// Destroy must be called before freeing the buffer to release Swift references.
                /// </summary>
                internal readonly struct CopyBufferWithType
                {
                    public readonly IntPtr Buffer;
                    public readonly TypeMetadata Metadata;
                    public CopyBufferWithType(IntPtr buffer, TypeMetadata metadata)
                    {
                        Buffer = buffer;
                        Metadata = metadata;
                    }
                }

                """);

            // Emit top-level methods
            if (moduleDecl.Methods.Any())
            {
                // Use unsafe class since methods may use function pointers for closure parameters
                csWriter.WriteLine($"public unsafe class {moduleDecl.Name}");
                csWriter.WriteLine("{");
                csWriter.Indent++;
                csWriter.WriteLine();
                foreach (MethodDecl methodDecl in moduleDecl.Methods)
                {
                    if (conductor.TryGetMethodHandler(methodDecl, out var methodHandler))
                    {
                        var methodEnv = methodHandler.Marshal(methodDecl, env.TypeDatabase);
                        methodHandler.Emit(csWriter, swiftWriter, methodEnv, conductor);
                    }
                    else
                    {
                        _logger.LogWarning($"No handler found for method {methodDecl.Name}");
                    }
                    // EmitMethod(csWriter, swiftWriter, moduleDecl, moduleDecl, methodDecl);
                    csWriter.WriteLine();
                }
                csWriter.Indent--;
                csWriter.WriteLine("}");
                csWriter.WriteLine();
            }

            // Emit top-level types
            base.HandleBaseDecl(csWriter, swiftWriter, moduleDecl.Types, conductor, env.TypeDatabase);

            csWriter.Indent--;
            csWriter.WriteLine("}");

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

            swiftWriter.WriteLine();
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
                .ToList();

            if (!suitableProtocols.Any())
                return;

            var emitter = new EveryProtocolEmitter(typeDatabase, _logger, moduleDecl.Name);

            // Emit the EveryProtocol class once
            emitter.EmitEveryProtocolClass(swiftWriter);

            // Track emitted method signatures globally to detect conflicts across protocols
            // Key is the Swift method signature (e.g., "removeAll()")
            var globalEmittedSignatures = new HashSet<string>();

            // Emit conformances for each suitable protocol
            foreach (var protocolDecl in suitableProtocols)
            {
                _logger.LogDebug($"Emitting EveryProtocol conformance for {protocolDecl.Name}");
                emitter.EmitProtocolConformance(swiftWriter, protocolDecl, globalEmittedSignatures);
            }
        }
    }
}
