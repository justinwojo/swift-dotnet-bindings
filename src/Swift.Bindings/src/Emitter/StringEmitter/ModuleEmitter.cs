// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Swift.Runtime;

namespace BindingsGeneration
{
    /// <summary>
    /// Represents an string-based C# emitter.
    /// </summary>
    public partial class StringEmitter : IEmitter
    {
        // Private properties
        private readonly string _outputDirectory;
        private readonly ITypeDatabase _typeDatabase;
        private readonly ILogger _logger;
        private readonly Conductor _conductor;
        private readonly NamespacePatternResolver _namespacePatternResolver;
        private readonly string? _bridgeHintsPath;
        private readonly Dictionary<string, List<string>>? _markerProtocolConformances;

        /// <summary>
        /// Initializes a new instance of the <see cref="StringEmitter"/> class.
        /// </summary>
        public StringEmitter(
            string outputDirectory,
            ITypeDatabase typeDatabase,
            ILoggerFactory loggerFactory,
            NamespacePatternResolver? namespacePatternResolver = null,
            string? bridgeHintsPath = null,
            Dictionary<string, List<string>>? markerProtocolConformances = null)
        {
            _outputDirectory = outputDirectory;
            _typeDatabase = typeDatabase;
            _logger = loggerFactory.CreateLogger<StringEmitter>();
            _namespacePatternResolver = namespacePatternResolver ?? new NamespacePatternResolver();
            _conductor = new Conductor(loggerFactory, _namespacePatternResolver);
            _bridgeHintsPath = bridgeHintsPath;
            _markerProtocolConformances = markerProtocolConformances;
        }

        /// <summary>
        /// Emits a C# module based on the module declaration.
        /// </summary>
        /// <param name="moduleDecl">The module declaration.</param>
        /// <param name="emissionContext">Per-module emission context.</param>
        public void EmitModule(ModuleDecl moduleDecl, ModuleEmissionContext? emissionContext = null)
        {
            emissionContext ??= ModuleEmissionContext.Default;

            if (_conductor.TryGetModuleHandler(moduleDecl, out var moduleHandler))
            {
                var csStringWriter = new StringWriter();
                CSharpWriter csWriter = new(csStringWriter);
                var swiftStringWriter = new StringWriter();
                SwiftWriter swiftWriter = new(swiftStringWriter);
                var @namespace = _namespacePatternResolver.ResolveNamespace(moduleDecl.Name);

                IReadOnlyList<TypeDecl> collectedViews;
                SwiftUIBridgeCollector.Reset();
                try
                {
                    var env = moduleHandler.Marshal(moduleDecl, _typeDatabase);
                    // Pre-pass: apply CSharpTypeName renames for nested type collisions BEFORE
                    // any emission. This ensures all type references (including protocol interfaces
                    // that may be emitted before their parent types) use the correct renamed names.
                    NameProvider.PrecomputeNestedTypeRenames(moduleDecl, _typeDatabase);
                    // Pre-pass: register silent tombstones (types emitted with opaqueEmittable == 0
                    // && opaqueSkipped > 0) BEFORE any method wrappers so SB0002 diagnostics fire on
                    // call sites regardless of declaration order. See SilentTombstoneRegistrar.
                    SilentTombstoneRegistrar.Precompute(moduleDecl, _typeDatabase, emissionContext);
                    var initialContext = new TypeHandlerContext(null, new(), null, MarkerProtocolConformances: _markerProtocolConformances, EmissionContext: emissionContext);
                    moduleHandler.Emit(csWriter, swiftWriter, env, _conductor, initialContext);
                    collectedViews = SwiftUIBridgeCollector.GetCollectedViews();
                }
                finally
                {
                    SwiftUIBridgeCollector.Reset();
                }

                var csOutput = csStringWriter.ToString();

                // CX-1: When the namespace has a type with the same name (e.g., module "Valet"
                // has class "Valet"), C# resolves Valet.OtherType as a nested type lookup on the
                // class instead of a namespace member. Fix by adding global:: qualifier.
                var collisionType = moduleDecl.Types.FirstOrDefault(t => t.Name == @namespace)
                    ?? (BaseDecl?)moduleDecl.Protocols.FirstOrDefault(p => p.Name == @namespace);
                if (collisionType != null)
                {
                    // Collect nested type C# names — Namespace.NestedType refers to the collision class's
                    // nested type, not a namespace member. These must NOT get global:: qualification.
                    // Use C# names (not Swift names) because nested type renames (e.g., Connection → ConnectionType)
                    // mean the generated code uses the renamed name.
                    var nestedTypeNames = new HashSet<string>();
                    if (collisionType is TypeDecl td)
                    {
                        foreach (var nested in td.Types)
                        {
                            var csLeafName = NameProvider.ToPascalCaseForTypeName(nested.Name);
                            // Check TypeDatabase for rename (e.g., Connection → ConnectionType)
                            if (_typeDatabase.TryGetTypeRecord(nested.SwiftTypeName, out var nestedRecord))
                            {
                                var csName = nestedRecord.CSharpTypeName.Name;
                                var lastDot = csName.LastIndexOf('.');
                                if (lastDot >= 0)
                                    csLeafName = csName.Substring(lastDot + 1);
                            }
                            nestedTypeNames.Add(csLeafName);
                        }
                    }
                    csOutput = QualifyNamespaceReferences(csOutput, @namespace, nestedTypeNames);
                }

                // Post-generation ABI contract validation
                AbiContractChecker.Validate(csOutput, moduleDecl.Name, _logger);

                string csOutputPath = Path.Combine(_outputDirectory, $"{@namespace}.cs");
                using (StreamWriter outputFile = new(csOutputPath))
                {
                    outputFile.Write(csOutput);
                }
                string swiftOutputPath = Path.Combine(_outputDirectory, $"{@namespace}.Wrapper.swift");
                using (StreamWriter outputFile = new(swiftOutputPath))
                {
                    outputFile.Write(swiftStringWriter.ToString());
                }

                // Write ARM64 assembly thunk file if any thunks were emitted
                if (emissionContext.HasThunkAssembly)
                {
                    var asmContent = new System.Text.StringBuilder();
                    asmContent.Append(ThunkAssemblyEmitter.EmitFileHeader(moduleDecl.Name));
                    asmContent.Append(emissionContext.AssemblyBuilder);
                    asmContent.Append(ThunkAssemblyEmitter.EmitFileFooter());

                    string asmOutputPath = Path.Combine(_outputDirectory, $"{@namespace}.arm64.s");
                    using (StreamWriter outputFile = new(asmOutputPath))
                    {
                        outputFile.Write(asmContent.ToString());
                    }
                }

                // Detect theme-bridgeable types (classes with singleton + Color/Font properties)
                var themeInfos = ThemeBridgeEmitter.DetectThemeBridgeableTypes(moduleDecl);
                var hasViews = collectedViews.Count > 0;
                var hasThemes = themeInfos.Count > 0;

                // Always clean up stale auto-generated bridge files before writing fresh ones.
                // This prevents duplicate emission on rerun and ensures idempotency.
                if (hasViews || hasThemes)
                {
                    SwiftUIBridgeEmitter.CleanupAutoGeneratedBridgeFiles(_outputDirectory, @namespace, _logger);
                }

                if (hasViews)
                {
                    SwiftUIBridgeEmitter.EmitBridgeFiles(
                        _outputDirectory, @namespace, moduleDecl.Name, collectedViews, _logger, _typeDatabase, moduleDecl, _bridgeHintsPath);
                }
                else if (!hasThemes)
                {
                    // No views and no themes — clean up any stale bridge files
                    SwiftUIBridgeEmitter.CleanupAutoGeneratedBridgeFiles(_outputDirectory, @namespace, _logger);
                }

                // Emit theme bridge (appends to view bridge files, or creates standalone files)
                if (hasThemes)
                {
                    ThemeBridgeEmitter.EmitThemeBridge(
                        _outputDirectory, @namespace, moduleDecl.Name, themeInfos,
                        viewBridgeExists: hasViews, _logger);
                }
            }
            else
            {
                _logger.LogWarning($"No module handler found for {moduleDecl.Name}");
            }
        }

        /// <summary>
        /// Replaces bare namespace-qualified type references with global:: qualified references.
        /// Called when the module namespace collides with a type name (e.g., module "Valet" has class "Valet").
        /// </summary>
        /// <param name="csOutput">The generated C# source code.</param>
        /// <param name="namespace">The namespace that collides with a type name.</param>
        /// <param name="nestedTypeNames">Names of types nested within the collision class.
        /// References like Namespace.NestedType should NOT be qualified because they resolve
        /// to nested types of the class, not namespace members.</param>
        internal static string QualifyNamespaceReferences(string csOutput, string @namespace, HashSet<string> nestedTypeNames)
        {
            // Match Namespace.Identifier where Namespace is NOT preceded by global:: or another identifier char,
            // and NOT in a namespace declaration (namespace Valet.SwiftInterop).
            // This handles type references like Valet.SecureEnclaveValet in parameter types, return types,
            // generic arguments, typeof(), casts, etc.
            var escapedNs = Regex.Escape(@namespace);
            var pattern = $@"(?<!global::)(?<!namespace )(?<![.\w]){escapedNs}\.(?=[A-Z])";
            return Regex.Replace(csOutput, pattern, match =>
            {
                // Check if the following identifier is a nested type of the collision class.
                // If so, leave it unqualified — Reachability.Connection refers to the nested enum.
                var afterDot = csOutput.Substring(match.Index + match.Length);
                var identEnd = 0;
                while (identEnd < afterDot.Length && (char.IsLetterOrDigit(afterDot[identEnd]) || afterDot[identEnd] == '_'))
                    identEnd++;
                if (identEnd > 0)
                {
                    var nextIdent = afterDot.Substring(0, identEnd);
                    if (nestedTypeNames.Contains(nextIdent))
                        return match.Value; // Keep unqualified
                }
                return $"global::{@namespace}.";
            });
        }
    }
}
